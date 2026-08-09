using System.Security.Cryptography;
using System.Text;

namespace Monkey.Service;

/// <summary>
/// Duenner Mantel um die Windows-DPAPI. CurrentUser-Scope: im Dienstbetrieb ist
/// das SYSTEM - dann kann nur SYSTEM entschluesseln, nicht der angemeldete
/// Benutzer und auch kein Administrator, der die Zustandsdatei liest.
/// </summary>
internal static class Dpapi
{
    // Zusaetzliche Entropie bindet das Chiffrat an diesen Zweck; ein Geheimnis ist
    // sie nicht.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Monkey.Telegram.v1");

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedBase64) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser));
}
