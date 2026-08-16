using System.Security.AccessControl;
using System.Security.Principal;
using Monkey.Core;
using Monkey.Service;
using Xunit;

namespace Monkey.Tests;

public sealed class StateStoreTests
{
    [Fact]
    public void Harden_UsesOnlyAllowRules()
    {
        // Der Reset-Fehler bis v1.3.3: Ein Schreibverbot fuer die
        // Administratorengruppe traf auch das LocalSystem-Token des Dienstes,
        // denn das traegt denselben Gruppen-SID - und Deny schlaegt Allow.
        // Der Dienst konnte nie speichern, jeder Neustart warf alles weg.
        Assert.All(StateStore.HardenedRules(),
            rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));
    }

    [Fact]
    public void Harden_GrantsSystemFullControl()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var rule = Assert.Single(StateStore.HardenedRules(),
            r => system.Equals(r.IdentityReference));

        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }

    [Fact]
    public void Save_RemembersFailure_And_ClearsItOnSuccess()
    {
        TestEnv.FreshDataDir();
        var store = new StateStore();

        // Ein Ordner blockiert den Platz der Temporaerdatei - das Schreiben muss
        // scheitern, und der Fehler darf nicht stumm untergehen.
        Directory.CreateDirectory(Paths.StateFile + ".tmp");
        store.Save(TestEnv.NewState(s => s.BalanceSeconds = 4321));

        Assert.NotNull(store.LastSaveError);
        Assert.False(File.Exists(Paths.StateFile));

        Directory.Delete(Paths.StateFile + ".tmp");
        store.Save(TestEnv.NewState(s => s.BalanceSeconds = 4321));

        Assert.Null(store.LastSaveError);
        Assert.Equal(4321, store.Load().BalanceSeconds);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        TestEnv.FreshDataDir();
        var store = new StateStore();
        var state = TestEnv.NewState(s => s.BalanceSeconds = 4321);

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal(4321, loaded.BalanceSeconds);
        Assert.True(loaded.HasPassword);
        Assert.Equal(state.PasswordHash, loaded.PasswordHash);
    }

    [Fact]
    public void Load_NoFiles_ReturnsDefaults()
    {
        TestEnv.FreshDataDir();
        var loaded = new StateStore().Load();

        Assert.False(loaded.HasPassword);
        Assert.Equal(0, loaded.BalanceSeconds);
        Assert.Equal(30, loaded.Config.DailyGrantMinutes);
    }

    [Fact]
    public void Load_CorruptMainFile_FallsBackToBackup()
    {
        TestEnv.FreshDataDir();
        var store = new StateStore();

        store.Save(TestEnv.NewState(s => s.BalanceSeconds = 111));
        store.Save(TestEnv.NewState(s => s.BalanceSeconds = 222));
        File.WriteAllText(Paths.StateFile, "das ist kein json {{{");

        var loaded = store.Load();

        // Die Sicherung ist der Stand vor dem letzten Speichern.
        Assert.Equal(111, loaded.BalanceSeconds);
        Assert.True(loaded.HasPassword);
    }

    [Fact]
    public void Load_EverythingCorrupt_FailsClosedToDefaults()
    {
        TestEnv.FreshDataDir();
        var store = new StateStore();

        store.Save(TestEnv.NewState(s => s.BalanceSeconds = 111));
        store.Save(TestEnv.NewState(s => s.BalanceSeconds = 222));
        File.WriteAllText(Paths.StateFile, "kaputt");
        File.WriteAllText(Paths.StateBackup, "auch kaputt");

        var loaded = store.Load();

        // Kein Passwort heisst: nichts wird freigegeben, siehe GuardEngine.
        Assert.False(loaded.HasPassword);
        Assert.Equal(0, loaded.BalanceSeconds);
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        TestEnv.FreshDataDir();
        var store = new StateStore();

        store.Save(TestEnv.NewState());
        store.Save(TestEnv.NewState());

        Assert.True(File.Exists(Paths.StateFile));
        Assert.False(File.Exists(Paths.StateFile + ".tmp"));
    }
}
