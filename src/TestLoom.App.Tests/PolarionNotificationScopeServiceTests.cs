using TestLoom.App.Models;
using TestLoom.App.Services;
using static TestLoom.App.Services.PolarionNotificationScopeService;

namespace TestLoom.App.Tests;

public sealed class PolarionNotificationScopeServiceTests
{
    [Fact]
    public void OnlySelectedVehiclesMayGenerateNotifications()
    {
        var prefs = new PolarionNotificationPreferences { Veicoli = ["Daily MY24"] };
        var n1 = new NonConformita
        {
            CollegamentiPolarion = [new CollegamentoPolarion { Codice = "TICKET-123" }],
            VeicoliSegnalati = ["Daily MY24"]
        };
        var n2 = new NonConformita
        {
            CollegamentiPolarion = [new CollegamentoPolarion { Codice = "TICKET-456" }],
            VeicoliSegnalati = ["Alpha MY26"]
        };
        var scopes = Build([n1, n2], Array.Empty<ChangeRequest>(), _ => Array.Empty<string>(), prefs,
            new Dictionary<string, string>());
        Assert.Equal(Mode.Full, scopes["TICKET-123"]);
        Assert.Equal(Mode.Silent, scopes["TICKET-456"]);
    }

    [Fact]
    public void OneActiveAssociationWinsOverAnotherClosedNcForSameTicket()
    {
        var prefs = new PolarionNotificationPreferences { Gamme = ["LCV Diesel"] };
        var vehicles = new Dictionary<string, string> { ["Daily MY24"] = "LCV Diesel" };
        var closed = new NonConformita
        {
            StatoWorkflow = StatoAnomalia.Conclusa,
            VeicoliSegnalati = ["Daily MY24"],
            CollegamentiPolarion = [new CollegamentoPolarion { Codice = "TICKET-123" }]
        };
        var open = new ChangeRequest
        {
            VeicoliImpattati = ["Daily MY24"],
            TicketCollegati = [new TicketRif { Riferimento = "ticket-123" }]
        };
        var scopes = Build([closed], [open], _ => Array.Empty<string>(), prefs, vehicles);
        Assert.Equal(Mode.Full, scopes["TICKET-123"]);
        Assert.Single(scopes);
    }

    [Fact]
    public void ClosedAssociationOnlyNotifiesReopenAndArchivedCrNeverSubscribes()
    {
        var prefs = new PolarionNotificationPreferences { IncludiNonAssociati = true };
        var closed = new ChangeRequest
        {
            Stato = StatoChangeRequest.Chiusa,
            TicketCollegati = [new TicketRif { Riferimento = "TICKET-55" }]
        };
        var archived = new ChangeRequest
        {
            Archiviata = true,
            TicketCollegati = [new TicketRif { Riferimento = "TICKET-99" }]
        };
        var scopes = Build([], [closed, archived], _ => Array.Empty<string>(), prefs,
            new Dictionary<string, string>());
        Assert.Equal(Mode.ReopenOnly, scopes["TICKET-55"]);
        Assert.False(scopes.ContainsKey("TICKET-99"));
    }

    [Fact]
    public void UnlinkedNcDoesNotTriggerUnnecessaryTestCaseLookups()
    {
        var calls = 0;
        var nc = new NonConformita { TestCaseId = "TEST-123" };
        var result = Build([nc], Array.Empty<ChangeRequest>(),
            _ => { calls++; return new[] { "Daily MY24" }; },
            new PolarionNotificationPreferences { Veicoli = ["Daily MY24"] },
            new Dictionary<string, string>());
        Assert.Empty(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void UnselectedItemsDoNotBecomeSubscriptions()
    {
        var prefs = new PolarionNotificationPreferences();
        var nc = new NonConformita
        {
            CollegamentiPolarion = [new CollegamentoPolarion { Codice = "TICKET-123" }]
        };
        Assert.Equal(Mode.Silent, Build([nc], [], _ => Array.Empty<string>(), prefs,
            new Dictionary<string, string>())["TICKET-123"]);
    }

    [Fact]
    public void NcCanInheritVehicleFromLinkedTestWithoutChangingItsStoredData()
    {
        var prefs = new PolarionNotificationPreferences { Veicoli = ["Daily MY24"] };
        var nc = new NonConformita
        {
            TestCaseId = "TEST-1",
            CollegamentiPolarion = [new CollegamentoPolarion { Codice = "TICKET-123" }]
        };
        Assert.Equal(Mode.Full, Build([nc], [],
            id => id == "TEST-1" ? new[] { "Daily MY24" } : Array.Empty<string>(), prefs,
            new Dictionary<string, string>())["TICKET-123"]);
    }
}
