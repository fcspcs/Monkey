using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TimeGuard.Setup;

/// <summary>
/// Raeumt gesperrte Reste einer frueheren Installation weg. Der Dienst haertet
/// seinen Programmordner gegen Schreiben, Loeschen und sogar Besitzuebernahme
/// durch Administratoren ab. Um solche Reste dennoch zu entfernen, aktiviert der
/// (erhoehte) Installer die dafuer noetigen Privilegien und uebernimmt den Besitz -
/// das ist genau der Fall, fuer den es SeTakeOwnership/SeRestore gibt.
///
/// Bewusst sprachunabhaengig ueber die Windows-API statt ueber takeown/icacls,
/// deren Eingabeaufforderungen je nach Systemsprache verschieden sind.
/// </summary>
internal static class NativeSecurity
{
    private const int TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const int TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool _privilegesEnabled;

    private static void EnsurePrivileges()
    {
        if (_privilegesEnabled) return;
        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeRestorePrivilege");
        _privilegesEnabled = true;
    }

    /// <summary>
    /// Loescht einen Ordner (oder eine Datei) auch dann, wenn er per ACL gegen
    /// Administratoren gesperrt ist. Fehlt der Pfad, passiert nichts.
    /// </summary>
    public static void ForceDelete(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        EnsurePrivileges();

        if (File.Exists(path))
        {
            Unlock(path, isDirectory: false);
            File.Delete(path);
            return;
        }

        // Erst alle Sperren im Baum loesen, dann rekursiv loeschen.
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            Unlock(entry, Directory.Exists(entry));
        Unlock(path, isDirectory: true);

        Directory.Delete(path, recursive: true);
    }

    private static readonly SecurityIdentifier Admins =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static void Unlock(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                var info = new DirectoryInfo(path);

                var owner = new DirectorySecurity();
                owner.SetOwner(Admins);
                info.SetAccessControl(owner);

                var acl = info.GetAccessControl();
                acl.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                RemoveDenies(acl);
                acl.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(acl);
            }
            else
            {
                var info = new FileInfo(path);
                if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
                    info.Attributes = FileAttributes.Normal;

                var owner = new FileSecurity();
                owner.SetOwner(Admins);
                info.SetAccessControl(owner);

                var acl = info.GetAccessControl();
                acl.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                RemoveDenies(acl);
                acl.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl,
                    AccessControlType.Allow));
                info.SetAccessControl(acl);
            }
        }
        catch
        {
            // Best effort - ein einzelner Eintrag darf das Aufraeumen nicht stoppen.
        }
    }

    private static void RemoveDenies(FileSystemSecurity acl)
    {
        foreach (FileSystemAccessRule rule in
                 acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Deny)
                acl.RemoveAccessRuleSpecific(rule);
    }
}
