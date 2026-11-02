using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Feedback utente 2026-09-22 ("estendi modello CR ma non renderli campi obbligatori"): una Change
/// Request importata da una segnalazione esterna porta matricola, licenze, funzionalita' toccate e l'aggancio alle
/// richieste nate dallo stesso ticket. La NonConformita' aveva gia' questi campi, la ChangeRequest no, e senza
/// di essi l'import perdeva 255 matricola, 255 licenze, 178 funzionalita' e 4 gruppi interi di CR sorelle.</summary>
public sealed class ChangeRequestCampiSegnalazioneTests
{
    [Fact]
    public void CampiSegnalazione_SonoVuotiPerDefault_ENonObbligatori()
    {
        var cr = new ChangeRequest();

        Assert.Empty(cr.VinCliente);
        Assert.Empty(cr.Licenze);
        Assert.Empty(cr.FunzionalitaImpattate);
        Assert.Null(cr.GruppoSegnalazioneUid);
    }

    /// <summary>Compatibilita' all'indietro: un file scritto prima di questa modifica non contiene i campi
    /// nuovi e deve continuare a caricarsi, con le liste vuote invece che nulle.</summary>
    [Fact]
    public void JsonSenzaICampiNuovi_SiDeserializzaSenzaErrori()
    {
        const string jsonPrecedente = """
        {"SchemaVersion":1,"Id":"CR-2026-0001","Titolo":"Vecchia CR","Stato":"Aperta"}
        """;

        var cr = JsonSerializer.Deserialize<ChangeRequest>(jsonPrecedente, JsonIo.Options)!;

        Assert.Equal("CR-2026-0001", cr.Id);
        Assert.NotNull(cr.VinCliente);
        Assert.Empty(cr.VinCliente);
        Assert.NotNull(cr.Licenze);
        Assert.NotNull(cr.FunzionalitaImpattate);
        Assert.Null(cr.GruppoSegnalazioneUid);
    }

    [Fact]
    public void CampiSegnalazione_SopravvivonoAlGiroCompletoDiSerializzazione()
    {
        var gruppo = Guid.NewGuid();
        var originale = new ChangeRequest
        {
            Id = "CR-2026-0002",
            VinCliente = new List<string> { "ZCFC735B305312068", "N/D" },
            Licenze = new List<string> { "0011859" },
            FunzionalitaImpattate = new List<string> { "Sostituzione componente", "Lettura Guasti" },
            GruppoSegnalazioneUid = gruppo,
        };

        var ricaricata = JsonSerializer.Deserialize<ChangeRequest>(JsonSerializer.Serialize(originale, JsonIo.Options), JsonIo.Options)!;

        Assert.Equal(new[] { "ZCFC735B305312068", "N/D" }, ricaricata.VinCliente);
        Assert.Equal(new[] { "0011859" }, ricaricata.Licenze);
        Assert.Equal(new[] { "Sostituzione componente", "Lettura Guasti" }, ricaricata.FunzionalitaImpattate);
        Assert.Equal(gruppo, ricaricata.GruppoSegnalazioneUid);
    }
}
