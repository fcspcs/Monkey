using System.Security.AccessControl;
using System.Security.Principal;
using Monkey.Service;
using Xunit;

namespace Monkey.Tests;

public sealed class SelfProtectTests
{
    // Zugriffsbits fuer Dienste, siehe SERVICE_* in winsvc.h.
    private const int ServiceChangeConfig = 0x0002;
    private const int ServiceStart = 0x0010;
    private const int ServiceStop = 0x0020;
    private const int Delete = 0x10000;
    private const int WriteDac = 0x40000;

    /// <summary>
    /// Der Reset-Fehler bis v1.3.3 in seiner zweiten Form: Deny-Eintraege fuer
    /// die Administratorengruppe treffen auch das LocalSystem-Token, denn das
    /// traegt denselben Gruppen-SID - und Deny schlaegt Allow. Der Dienst konnte
    /// sich dadurch beim signierten Selbst-Update nicht einmal selbst stoppen.
    /// </summary>
    [Fact]
    public void LockedSddl_UsesOnlyAllowAces()
    {
        var descriptor = new RawSecurityDescriptor(SelfProtect.LockedSddl);

        Assert.All(descriptor.DiscretionaryAcl!.Cast<GenericAce>(),
            ace => Assert.Equal(AceType.AccessAllowed, ace.AceType));
    }

    [Fact]
    public void LockedSddl_SystemMayStop_AdminsMayOnlyLookAndStart()
    {
        var descriptor = new RawSecurityDescriptor(SelfProtect.LockedSddl);
        var aces = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToList();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var systemMask = Assert.Single(aces, a => a.SecurityIdentifier.Equals(system)).AccessMask;
        var adminMask = Assert.Single(aces, a => a.SecurityIdentifier.Equals(admins)).AccessMask;

        Assert.Equal(ServiceStop, systemMask & ServiceStop);

        Assert.Equal(ServiceStart, adminMask & ServiceStart);
        Assert.Equal(0, adminMask & (ServiceStop | ServiceChangeConfig | Delete | WriteDac));
    }
}
