using System.Security.AccessControl;
using System.Security.Principal;
using Monkey.Core;

namespace Monkey.Service;

/// <summary>
/// Laden und Speichern des Zustands, plus Absicherung des Datenordners.
/// Geschrieben wird immer erst in eine Temporaerdatei und dann ersetzt, damit ein
/// Stromausfall mitten im Schreiben keine unbrauchbare Datei hinterlaesst.
/// </summary>
internal sealed class StateStore
{
    private readonly object _gate = new();

    public GuardState Load()
    {
        lock (_gate)
        {
            EnsureDirectory();

            foreach (var candidate in new[] { Paths.StateFile, Paths.StateBackup })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    var state = GuardState.FromJson(File.ReadAllText(candidate));
                    if (state is not null)
                    {
                        if (candidate == Paths.StateBackup)
                            Log.Write("Main file unreadable, state restored from the backup.");
                        return state;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"State in '{candidate}' not readable: {ex.Message}");
                }
            }

            Log.Write("No state found, creating defaults.");
            return new GuardState { TrustedNow = DateTimeOffset.Now };
        }
    }

    public void Save(GuardState state)
    {
        lock (_gate)
        {
            state.LastSaved = DateTimeOffset.Now;
            var temp = Paths.StateFile + ".tmp";

            try
            {
                EnsureDirectory();
                File.WriteAllText(temp, state.ToJson());

                if (File.Exists(Paths.StateFile))
                    File.Replace(temp, Paths.StateFile, Paths.StateBackup, ignoreMetadataErrors: true);
                else
                    File.Move(temp, Paths.StateFile);
            }
            catch (Exception ex)
            {
                Log.Write($"State could not be saved: {ex.Message}");
            }
        }
    }

    public static void EnsureDirectory() => Directory.CreateDirectory(Paths.DataDir);

    /// <summary>
    /// Dichtet den Datenordner ab: SYSTEM darf alles, Administratoren duerfen nur
    /// lesen, alle anderen gar nichts. Der Dienst stellt das bei jedem Start wieder
    /// her, damit eine aufgeweichte ACL sich selbst repariert.
    /// </summary>
    public static void Harden()
    {
        try
        {
            EnsureDirectory();

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            var security = new DirectorySecurity();
            security.SetOwner(system);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            security.AddAccessRule(new FileSystemAccessRule(
                system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.ReadAndExecute | FileSystemRights.ReadPermissions,
                inherit, PropagationFlags.None, AccessControlType.Allow));

            // Administratoren duerfen den Bestand einsehen, aber nicht veraendern.
            // Wer die ACL selbst umschreibt, kommt weiter - das ist der bewusst
            // offen gelassene Hebel, siehe README.
            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                inherit, PropagationFlags.None, AccessControlType.Deny));

            security.AddAccessRule(new FileSystemAccessRule(
                users, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Deny));

            new DirectoryInfo(Paths.DataDir).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not set permissions on the data folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Gibt den Ordner fuer Administratoren wieder frei. Wird von 'init' vor dem
    /// Schreiben und von der Deinstallation gebraucht.
    /// </summary>
    public static void Unharden()
    {
        try
        {
            EnsureDirectory();

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            new DirectoryInfo(Paths.DataDir).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not reset permissions on the data folder: {ex.Message}");
        }
    }
}
