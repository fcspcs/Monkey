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

    /// <summary>
    /// Letzter Speicherfehler, null solange gespeichert werden kann. Ein stiller
    /// Fehler hiess frueher: alles lebt nur noch im Arbeitsspeicher, und der
    /// naechste Dienststart wirft Guthaben, Passwort und Einstellungen weg.
    /// Deshalb wandert der Fehler in den Status und damit sichtbar ins Steuerpult.
    /// </summary>
    public string? LastSaveError { get; private set; }

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

                LastSaveError = null;
            }
            catch (Exception ex)
            {
                // Nur beim ersten Auftreten protokollieren: gespeichert wird im
                // Minutentakt, ein Dauerfehler wuerde das Log sonst fluten.
                if (LastSaveError != ex.Message)
                    Log.Write($"State could not be saved: {ex.Message}");
                LastSaveError = ex.Message;
            }
        }
    }

    public static void EnsureDirectory() => Directory.CreateDirectory(Paths.DataDir);

    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Dichtet den Datenordner ab: SYSTEM darf alles, Administratoren duerfen nur
    /// lesen, alle anderen stehen gar nicht erst in der Liste. Der Dienst stellt
    /// das bei jedem Start wieder her, damit eine aufgeweichte ACL sich selbst
    /// repariert.
    /// </summary>
    public static void Harden()
    {
        try
        {
            EnsureDirectory();

            var security = new DirectorySecurity();
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var rule in HardenedRules()) security.AddAccessRule(rule);

            new DirectoryInfo(Paths.DataDir).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not set permissions on the data folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Bewusst eine reine Allow-Liste. Das Token von LocalSystem enthaelt neben
    /// SYSTEM auch den SID der Administratorengruppe, und eine Deny-Regel schlaegt
    /// jede Allow-Regel - ein Schreibverbot fuer Administratoren sperrte deshalb
    /// den Dienst selbst aus, und jeder Neustart warf den Zustand weg (der
    /// Reset-Fehler bis v1.3.3). Wer hier nicht aufgefuehrt ist, hat ohnehin
    /// keinen Zugriff: die Liste ist gegen Vererbung abgeschottet. Dass ein
    /// Administrator die ACL selbst umschreiben kann, bleibt der bewusst offen
    /// gelassene Hebel, siehe README.
    /// </summary>
    internal static FileSystemAccessRule[] HardenedRules() =>
    [
        new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow),
        new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.ReadAndExecute | FileSystemRights.ReadPermissions,
            Inherit, PropagationFlags.None, AccessControlType.Allow),
    ];

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

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                system, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));

            new DirectoryInfo(Paths.DataDir).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not reset permissions on the data folder: {ex.Message}");
        }
    }
}
