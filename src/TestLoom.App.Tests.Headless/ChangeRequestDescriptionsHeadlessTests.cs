using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class ChangeRequestDescriptionsHeadlessTests
{
    [Fact]
    public void DescrizioneBreveCr_SiPersistEPassaAllaRevisioneSenzaModificareIRecordEsistenti()
    {
        var precedente = JsonSerializer.Deserialize<ChangeRequest>("""
            { "Id": "CR-OLD", "Titolo": "Titolo storico", "Descrizione": "Descrizione completa" }
            """);
        Assert.NotNull(precedente);
        Assert.Equal("", precedente.DescrizioneBreve);

        var cr = new ChangeRequest
        {
            Id = "CR-NEW",
            Titolo = "Titolo",
            DescrizioneBreve = "Sintesi breve",
            Descrizione = "Dettaglio completo",
        };

        var json = JsonSerializer.Serialize(cr);
        var ricaricata = JsonSerializer.Deserialize<ChangeRequest>(json);
        Assert.NotNull(ricaricata);
        Assert.Equal("Sintesi breve", ricaricata.DescrizioneBreve);
        Assert.Equal("Dettaglio completo", ricaricata.Descrizione);

        var revisione = ChangeRequestWorkflowService.CreaRevisione(cr, "operatore");
        Assert.Equal("Sintesi breve", revisione.DescrizioneBreve);
        Assert.Equal("Dettaglio completo", revisione.Descrizione);
    }
}
