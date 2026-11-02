using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionBackgroundSyncTests
{
    [Fact]
    public void DeduplicatesTicketIdsAcrossNcAndCrAndExcludesArchivedCr()
    {
        var nc = new NonConformita
        {
            CollegamentiPolarion =
            [
                new CollegamentoPolarion { Codice = "TICKET-123" },
                new CollegamentoPolarion { Codice = "invalid", Url = "https://example.invalid/ticketing/#/TICKET-456" }
            ]
        };
        var cr = new ChangeRequest
        {
            TicketCollegati = [new TicketRif { Riferimento = "ticket-123" }]
        };
        var archived = new ChangeRequest
        {
            Archiviata = true,
            TicketCollegati = [new TicketRif { Riferimento = "TICKET-999" }]
        };
        Assert.Equal(new[] { "TICKET-123", "TICKET-456" },
            PolarionBackgroundSyncService.CollectTicketIds(new[] { nc }, new[] { cr, archived }));
    }

    [Fact]
    public void InvalidExternalReferencesCannotInjectCachePaths()
    {
        var nc = new NonConformita
        {
            CollegamentiPolarion = [new CollegamentoPolarion { Codice = "../foo", Url = "https://example.invalid/foo" }]
        };
        Assert.Empty(PolarionBackgroundSyncService.CollectTicketIds(new[] { nc }, Array.Empty<ChangeRequest>()));
    }
}
