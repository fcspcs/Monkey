using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Monkey.Core;

namespace Monkey.Service;

/// <summary>
/// Der Selbstschutz. Er laeuft nur mit den Rechten von LocalSystem und stellt bei
/// jedem Dienststart sowie bei jedem Watchdog-Tick alle Sperren wieder her. Wer
/// einen Riegel entfernt, findet ihn nach spaetestens einer Minute wieder vor.
///
/// Keine dieser Massnahmen macht das Tool fuer einen Administrator unentfernbar.
/// Ein lokaler Administrator kann ueber die Windows-Aufgabenplanung beliebigen
/// Code als LocalSystem starten und handelt dann mit derselben Identitaet wie
/// dieser Dienst. Die Riegel verhindern beiläufiges Abschalten; eine echte Grenze
/// entsteht erst, wenn der Alltagsbenutzer keine lokalen Admin-Zugangsdaten hat.
/// </summary>
internal static class SelfProtect
{
    public const string ServiceName = Paths.ServiceName;
    public const string WatchdogTask = "Monkey Watchdog";
    public const string AgentAutostartName = "MonkeyAgent";

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ServiceRegPath = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;

    private static readonly string[] SafeBootKeys =
    [
        @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\" + ServiceName,
        @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\" + ServiceName,
    ];

    // Gesperrt: Administratoren duerfen abfragen und starten, aber nicht stoppen,
    // umkonfigurieren, loeschen - und vor allem nicht die Rechte selbst aendern
    // (kein WD/WO). Eigentuemer ist SYSTEM; nur als Eigentuemer laesst sich diese
    // Sperre spaeter wieder aendern.
    private const string LockedSddl =
        "O:SYG:SYD:(D;;DCWPDTSD;;;IU)(D;;DCWPDTSD;;;SU)(D;;DCWPDTSD;;;BA)" +
        "(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)" +
        "(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRC;;;BA)";

    // Offen: der Normalzustand einer Dienst-Sicherheitsbeschreibung, damit sich
    // der Dienst nach autorisiertem Teardown regulaer stoppen und loeschen laesst.
    private const string OpenSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)" +
        "(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    private static bool TeardownRequested => File.Exists(Paths.TeardownMarker);

    /// <summary>
    /// Wird bei jedem Dienststart aufgerufen. Richtet alle Riegel neu auf.
    /// </summary>
    public static void ApplyAll()
    {
        if (TeardownRequested)
        {
            Log.Write("Teardown marker present - self-protection not applied.");
            return;
        }

        StateStore.Harden();
        HardenServiceRegistry();
        HardenProgramDir();
        EnsureSafeBoot();
        EnsureAgentAutostart();
        EnsureWatchdogTask();
        LockService();
    }

    /// <summary>
    /// Ziel der geplanten Aufgabe. Laeuft als SYSTEM und braucht kein Passwort -
    /// die Aktion kann nur schuetzen, nie eine Grenze aufheben.
    /// </summary>
    public static int RunWatchdog()
    {
        if (TeardownRequested)
        {
            RemoveWatchdogTask();
            return 0;
        }

        if (!ServiceExists())
        {
            Log.Write("Watchdog: service missing - recreating it.");
            CreateService();
        }

        StartService();
        ApplyAll();
        return 0;
    }

    /// <summary>
    /// Autorisierter Abbau. Nur der Dienst ruft das nach geprueftem Passwort auf.
    /// Danach laesst sich alles regulaer entfernen.
    /// </summary>
    public static void Teardown()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Paths.TeardownMarker, DateTimeOffset.Now.ToString("o"));
        }
        catch (Exception ex)
        {
            Log.Write($"Teardown marker not writable: {ex.Message}");
        }

        RemoveWatchdogTask();
        RemoveSafeBoot();
        RemoveAgentAutostart();
        UnhardenServiceRegistry();
        UnhardenProgramDir();
        SetServiceSddl(OpenSddl);
        StateStore.Unharden();
        Log.Write("Teardown prepared: locks released, the service can be stopped.");
    }

    // ------------------------------------------------------------- Dienst

    private static string ServiceExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Could not determine own path.");

    private static bool ServiceExists()
    {
        using var scm = Registry.LocalMachine.OpenSubKey(ServiceRegPath);
        return scm is not null;
    }

    private static void CreateService()
    {
        Sc($"create {ServiceName} binPath= \"{ServiceExePath}\" start= auto obj= LocalSystem " +
           $"DisplayName= \"Monkey screen time\"");
        Sc($"description {ServiceName} \"Limits daily computer time, unused time rolls over.\"");
        Sc($"failure {ServiceName} reset= 0 actions= restart/2000/restart/2000/restart/2000");
    }

    private static void StartService() => Sc($"start {ServiceName}");

    private static void LockService() => SetServiceSddl(LockedSddl);

    private static void SetServiceSddl(string sddl) => Sc($"sdset {ServiceName} \"{sddl}\"");

    // --------------------------------------------------- Registry des Dienstes

    private static void HardenServiceRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceRegPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.TakeOwnership | RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
            if (key is null) return;

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            var security = new RegistrySecurity();
            security.SetOwner(system);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit;

            security.AddAccessRule(new RegistryAccessRule(
                system, RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            // Administratoren duerfen lesen, aber nicht aendern oder loeschen. Das
            // verhindert das einfache Abschalten ueber Start=4.
            //
            // Wichtig: hier NICHT RegistryRights.WriteKey verweigern. Dieser Wert
            // schliesst ReadPermissions (READ_CONTROL) mit ein, und genau das wird
            // zum Oeffnen des Schluessels gebraucht - ein solcher Deny sperrt also
            // versehentlich auch das Lesen aus, sodass der Dienst fuer die eigenen
            // Werkzeuge unsichtbar wird. Deshalb nur die einzelnen Schreibrechte.
            security.AddAccessRule(new RegistryAccessRule(
                admins, RegistryRights.ReadKey, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new RegistryAccessRule(
                admins, RegistryRights.SetValue | RegistryRights.CreateSubKey
                        | RegistryRights.CreateLink | RegistryRights.Delete
                        | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership,
                inherit, PropagationFlags.None, AccessControlType.Deny));

            security.AddAccessRule(new RegistryAccessRule(
                users, RegistryRights.ReadKey, inherit, PropagationFlags.None, AccessControlType.Allow));

            key.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not protect the service registry key: {ex.Message}");
        }
    }

    private static void UnhardenServiceRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceRegPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
            if (key is null) return;

            // Vererbung wieder einschalten und die zuvor gesetzte Sperre fallen
            // lassen. Beim Deinstallieren entfernt der Dienst-Loeschbefehl den
            // Schluessel ohnehin - das hier ist nur die saubere Zwischenstufe.
            var security = key.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            key.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not reset the service registry key: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ Programmordner

    private static string ProgramDir =>
        Path.GetDirectoryName(ServiceExePath) ?? throw new InvalidOperationException("Programmordner unbekannt.");

    private static void HardenProgramDir()
    {
        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            const FileSystemRights forbidden = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                | FileSystemRights.Write | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

            var security = new DirectorySecurity();
            security.SetOwner(system);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            security.AddAccessRule(new FileSystemAccessRule(
                system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            // Jeder darf lesen und ausfuehren (der Agent liegt hier), aber niemand
            // ausser SYSTEM darf die Programmdateien aendern oder loeschen.
            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                admins, forbidden, inherit, PropagationFlags.None, AccessControlType.Deny));
            security.AddAccessRule(new FileSystemAccessRule(
                users, forbidden, inherit, PropagationFlags.None, AccessControlType.Deny));

            new DirectoryInfo(ProgramDir).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not protect the program folder: {ex.Message}");
        }
    }

    private static void UnhardenProgramDir()
    {
        try
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            new DirectoryInfo(ProgramDir).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not release the program folder: {ex.Message}");
        }
    }

    // -------------------------------------------------------- Abgesicherter Modus

    private static void EnsureSafeBoot()
    {
        foreach (var path in SafeBootKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(path);
                key?.SetValue(string.Empty, "Service");
            }
            catch (Exception ex)
            {
                Log.Write($"Could not set safe-mode entry '{path}': {ex.Message}");
            }
        }
    }

    private static void RemoveSafeBoot()
    {
        foreach (var path in SafeBootKeys)
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
            catch (Exception ex) { Log.Write($"Could not remove safe-mode entry '{path}': {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------- Agent-Autostart

    private static void EnsureAgentAutostart()
    {
        var agent = Path.Combine(Path.GetDirectoryName(ServiceExePath) ?? string.Empty, "MonkeyAgent.exe");
        if (!File.Exists(agent)) return;

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RunKey);
            key?.SetValue(AgentAutostartName, $"\"{agent}\"");
        }
        catch (Exception ex)
        {
            Log.Write($"Could not set the display autostart: {ex.Message}");
        }
    }

    private static void RemoveAgentAutostart()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(AgentAutostartName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not remove the display autostart: {ex.Message}");
        }
    }

    // --------------------------------------------------------------- Watchdog

    private static void EnsureWatchdogTask()
    {
        // Jede Minute als SYSTEM. Ein geloeschter Dienst ist damit binnen einer
        // Minute wieder da und gesperrt.
        var command = $"\"{ServiceExePath}\" watchdog";
        var args = $"/Create /F /RU SYSTEM /RL HIGHEST /SC MINUTE /MO 1 " +
                   $"/TN \"{WatchdogTask}\" /TR \"{command.Replace("\"", "\\\"")}\"";
        SchTasks(args);
    }

    private static void RemoveWatchdogTask() => SchTasks($"/Delete /F /TN \"{WatchdogTask}\"");

    // --------------------------------------------------------------- Prozesse

    private static void Sc(string arguments) => Run("sc.exe", arguments);
    private static void SchTasks(string arguments) => Run("schtasks.exe", arguments);

    private static void Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return;
            process.WaitForExit(15000);
        }
        catch (Exception ex)
        {
            Log.Write($"'{file} {arguments}' failed: {ex.Message}");
        }
    }
}
