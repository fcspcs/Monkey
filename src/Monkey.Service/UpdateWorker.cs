using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Monkey.Core;

namespace Monkey.Service;

/// <summary>
/// Selbstaktualisierung aus den GitHub-Releases - ohne Master-Passwort, denn sie
/// kann nur in eine Richtung: eine NEUERE, vom Projektschluessel SIGNIERTE
/// Version installieren. Ohne gueltige Signatur passiert nichts; damit kann auch
/// niemand, der den Netzwerkverkehr beherrscht (etwa per selbst installiertem
/// Wurzelzertifikat), dem Dienst eine praeparierte "neue Version" unterschieben.
/// Der Update-Kanal waere sonst der schwaechste Riegel des ganzen Werkzeugs.
///
/// Ablauf: Manifest (update.json) des neuesten Releases laden, Signatur gegen
/// den eingebetteten oeffentlichen Schluessel pruefen, Version vergleichen, den
/// Installer in den abgedichteten Datenordner laden, Hash gegen das signierte
/// Manifest pruefen - und erst dann als SYSTEM im stillen Update-Modus starten.
/// Der tauscht die Programmdateien und startet den Dienst neu; Guthaben und
/// Passwort bleiben unberuehrt, die Zustandsdatei wird nicht angefasst.
/// </summary>
internal sealed class UpdateWorker(GuardEngine engine) : BackgroundService
{
    /// <summary>Woher Updates kommen. Forks tragen hier ihr eigenes Repo ein.</summary>
    private const string Repo = "fcspcs/Monkey";

    private const string SetupAssetName = "MonkeySetup.exe";
    private const string ManifestAssetName = "update.json";
    private const long MaxSetupBytes = 300L * 1024 * 1024;

    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = CreateClient();

    private Version? _attempted;

    public static string CurrentVersionText { get; } = CurrentVersion().ToString();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CleanupLeftovers();

        var publicKeyPem = LoadPublicKeyPem();
        if (publicKeyPem is null)
        {
            Log.Write("Auto-update: no update key embedded in this build - updates stay manual.");
            return;
        }

        try
        {
            await Task.Delay(FirstDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (engine.AutoUpdateEnabled)
                {
                    try
                    {
                        await CheckOnceAsync(publicKeyPem, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Kein Netz oder GitHub nicht erreichbar ist Alltag - eine
                        // Zeile genuegt, der naechste Versuch kommt von selbst.
                        Log.Write($"Auto-update check failed: {Shorten(ex.Message)}");
                    }
                }

                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Regulaeres Herunterfahren.
        }
    }

    private async Task CheckOnceAsync(string publicKeyPem, CancellationToken token)
    {
        using var release = await GetJsonAsync($"https://api.github.com/repos/{Repo}/releases/latest", 1024 * 1024, token);
        if (release is null || release.RootElement.ValueKind != JsonValueKind.Object) return;

        string? manifestUrl = null, setupUrl = null;
        if (release.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                if (name == ManifestAssetName) manifestUrl = url;
                else if (name == SetupAssetName) setupUrl = url;
            }
        }

        // Ein Release ohne signiertes Manifest ist kein Update-Kandidat.
        if (manifestUrl is null || setupUrl is null) return;
        if (!IsGitHubHttps(manifestUrl) || !IsGitHubHttps(setupUrl)) return;

        using var manifest = await GetJsonAsync(manifestUrl, 64 * 1024, token);
        if (manifest is null || manifest.RootElement.ValueKind != JsonValueKind.Object) return;

        var versionText = ReadString(manifest.RootElement, "version");
        var sha256 = ReadString(manifest.RootElement, "sha256")?.ToLowerInvariant();
        var signatureText = ReadString(manifest.RootElement, "signature");

        if (versionText is null || signatureText is null ||
            sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            return;

        if (!Version.TryParse(versionText, out var remote)) return;
        remote = Normalize(remote);

        // Nur strikt neuer. Das blockt auch das Wiedereinspielen einer alten,
        // einst gueltig signierten Version (Downgrade).
        if (remote <= CurrentVersion()) return;
        if (_attempted is not null && remote <= _attempted) return;

        // Signatur zuerst - was nicht vom Projektschluessel unterschrieben ist,
        // wird gar nicht erst heruntergeladen.
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText); }
        catch (FormatException) { return; }

        var payload = Encoding.ASCII.GetBytes($"MonkeyUpdate.v1\n{versionText}\n{sha256}\n");
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportFromPem(publicKeyPem);
            if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                Log.Write($"Auto-update: manifest for v{versionText} has a BAD signature - ignored.");
                return;
            }
        }

        // In den abgedichteten Datenordner laden: dort schreibt nur SYSTEM,
        // zwischen Pruefung und Start kann also niemand die Datei austauschen.
        var stagingDir = Path.Combine(Paths.DataDir, "update");
        Directory.CreateDirectory(stagingDir);
        var stagedSetup = Path.Combine(stagingDir, SetupAssetName);

        await DownloadAsync(setupUrl, stagedSetup, token);

        var actual = Convert.ToHexString(await HashFileAsync(stagedSetup, token)).ToLowerInvariant();
        if (actual != sha256)
        {
            Log.Write("Auto-update: downloaded installer does not match the signed hash - discarded.");
            TryDelete(stagedSetup);
            return;
        }

        _attempted = remote;
        Log.Write($"Auto-update: v{versionText} verified (signature and hash) - installing over v{CurrentVersionText}.");

        // Der neue Installer uebernimmt: Dienst stoppen, Dateien tauschen, Dienst
        // starten. Er laeuft als eigener Prozess weiter, wenn dieser hier endet.
        Process.Start(new ProcessStartInfo
        {
            FileName = stagedSetup,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "update" },
        });
    }

    // -------------------------------------------------------------- Werkzeug

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Monkey-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version CurrentVersion() =>
        Normalize(typeof(UpdateWorker).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>
    /// Nur https und nur GitHub-Hosts. Die eigentliche Sicherheit liegt in der
    /// Signatur - das hier ist Hygiene obendrauf.
    /// </summary>
    private static bool IsGitHubHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "github.com" || uri.Host == "api.github.com"
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<JsonDocument?> GetJsonAsync(string url, int maxBytes, CancellationToken token)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        await CopyBoundedAsync(stream, memory, maxBytes, token);
        memory.Position = 0;
        return await JsonDocument.ParseAsync(memory, cancellationToken: token);
    }

    private static async Task DownloadAsync(string url, string targetFile, CancellationToken token)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = File.Create(targetFile);
        await CopyBoundedAsync(source, target, MaxSetupBytes, token);
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, long maxBytes, CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new InvalidOperationException("Download larger than expected - aborted.");
            await target.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, token);
    }

    private static string? LoadPublicKeyPem()
    {
        try
        {
            using var stream = typeof(UpdateWorker).Assembly.GetManifestResourceStream("update-key.pem");
            if (stream is null) return null;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var pem = reader.ReadToEnd();

            // Einmal probeweise importieren: ein kaputter Schluessel soll sich
            // beim Start zeigen, nicht erst beim ersten Release.
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            return pem;
        }
        catch (Exception ex)
        {
            Log.Write($"Auto-update: embedded update key unusable ({ex.Message}) - updates stay manual.");
            return null;
        }
    }

    /// <summary>
    /// Reste frueherer Updates wegraeumen: beiseite gelegte alte Programmdateien
    /// und der Staging-Ordner. Was noch laeuft (etwa der alte Agent oder der
    /// Updater selbst), bleibt eben bis zum naechsten Start liegen.
    /// </summary>
    private static void CleanupLeftovers()
    {
        try
        {
            if (Path.GetDirectoryName(Environment.ProcessPath) is { } programDir)
                foreach (var old in Directory.GetFiles(programDir, "*.old"))
                    TryDelete(old);

            var stagingDir = Path.Combine(Paths.DataDir, "update");
            if (Directory.Exists(stagingDir))
                foreach (var file in Directory.GetFiles(stagingDir))
                    TryDelete(file);
        }
        catch (Exception)
        {
            // Aufraeumen ist Kuer.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* gesperrt - naechstes Mal */ }
    }

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}
