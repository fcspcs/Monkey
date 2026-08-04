using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Monkey.Setup;

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

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>
    /// Liest eine Datei ueber das Sicherungs-Privileg, ohne ihre Rechte zu
    /// veraendern. So laesst sich der Passwort-Hash einer gehaerteten Installation
    /// pruefen, ohne den Schutz aufzuweichen - ein Fehlversuch hinterlaesst nichts.
    /// Gibt null zurueck, wenn die Datei fehlt oder nicht lesbar ist.
    /// </summary>
    public static byte[]? ReadPrivileged(string path)
    {
        try
        {
            EnablePrivilege("SeBackupPrivilege");
            var handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }

            using (handle)
            using (var stream = new FileStream(handle, FileAccess.Read))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
        catch
        {
            return null;
        }
    }

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

        // Wichtig: erst den Ordner selbst entsperren, dann hineinschauen. Ein
        // Ordner, der schon das Auflisten verweigert, laesst sich sonst nicht
        // einmal aufzaehlen - die Besitzuebernahme muss also vor dem Absteigen
        // passieren.
        UnlockTree(path);
        Directory.Delete(path, recursive: true);
    }

    private static void UnlockTree(string dir)
    {
        Unlock(dir, isDirectory: true);

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(dir);
        }
        catch
        {
            return; // liess sich trotz Freigabe nicht auflisten - dann eben nicht.
        }

        foreach (var child in children)
        {
            if (Directory.Exists(child)) UnlockTree(child);
            else Unlock(child, isDirectory: false);
        }
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
