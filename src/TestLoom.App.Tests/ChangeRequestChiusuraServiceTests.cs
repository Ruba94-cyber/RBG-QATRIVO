using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Regole uniche di chiusura/riapertura CR: sempre possibile con motivo scritto, solo Master,
/// marcatura "senza validazione" se la CR non era Validata.</summary>
public sealed class ChangeRequestChiusuraServiceTests
{
    private static readonly DateTime Adesso = new(2026, 10, 21, 10, 30, 0);

    private static ChangeRequest Cr(StatoChangeRequest stato) => new() { Id = "CR-1", Titolo = "t", Stato = stato };

    [Theory]
    [InlineData(StatoChangeRequest.Aperta, true)]
    [InlineData(StatoChangeRequest.Implementata, true)]
    [InlineData(StatoChangeRequest.DaValidare, true)]
    [InlineData(StatoChangeRequest.Validata, false)]
    public void Chiudi_DaOgniStatoAttivo_MarcaSenzaValidazioneSoloSeNonValidata(StatoChangeRequest stato, bool attesaMarcatura)
    {
        var cr = Cr(stato);

        var esito = ChangeRequestChiusuraService.Chiudi(cr, "Master1", "  chiusa dal responsabile  ", true, true, Adesso);

        Assert.Equal(EsitoTransizioneCr.Ok, esito);
        Assert.Equal(StatoChangeRequest.Chiusa, cr.Stato);
        Assert.Equal(attesaMarcatura, cr.ChiusaSenzaValidazione);
        Assert.Equal("chiusa dal responsabile", cr.MotivoChiusura);
        Assert.Equal("Master1", cr.ChiusaDa);
        Assert.Equal(Adesso, cr.ChiusaIl);
        var voce = Assert.Single(cr.Changelog);
        Assert.Equal("stato", voce.Campo);
        Assert.Equal(stato.ToString(), voce.ValorePrecedente);
        Assert.Contains("chiusa dal responsabile", voce.ValoreNuovo);
        Assert.Equal(attesaMarcatura, voce.ValoreNuovo!.Contains("senza validazione"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Chiudi_SenzaMotivo_NonChiudeENonToccaIlRecord(string? motivo)
    {
        var cr = Cr(StatoChangeRequest.Aperta);

        var esito = ChangeRequestChiusuraService.Chiudi(cr, "Master1", motivo, true, true, Adesso);

        Assert.Equal(EsitoTransizioneCr.MotivoMancante, esito);
        Assert.Equal(StatoChangeRequest.Aperta, cr.Stato);
        Assert.False(cr.ChiusaSenzaValidazione);
        Assert.Null(cr.ChiusaIl);
        Assert.Empty(cr.Changelog);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Chiudi_SenzaPermessi_ILServizioRifiutaAncheSeLaUiVieneAggirata(bool puoScrivere, bool eMaster)
    {
        var cr = Cr(StatoChangeRequest.Validata);

        var esito = ChangeRequestChiusuraService.Chiudi(cr, "Op", "motivo", puoScrivere, eMaster, Adesso);

        Assert.Equal(EsitoTransizioneCr.NonAutorizzato, esito);
        Assert.Equal(StatoChangeRequest.Validata, cr.Stato);
        Assert.Empty(cr.Changelog);
    }

    [Theory]
    [InlineData(StatoChangeRequest.Chiusa, false)]
    [InlineData(StatoChangeRequest.Annullata, false)]
    [InlineData(StatoChangeRequest.Aperta, true)]
    public void Chiudi_SuStatoTerminaleOArchiviata_StatoNonValido(StatoChangeRequest stato, bool archiviata)
    {
        var cr = Cr(stato);
        cr.Archiviata = archiviata;

        var esito = ChangeRequestChiusuraService.Chiudi(cr, "Master1", "motivo", true, true, Adesso);

        Assert.Equal(EsitoTransizioneCr.StatoNonValido, esito);
        Assert.Equal(stato, cr.Stato);
        Assert.Empty(cr.Changelog);
    }

    [Fact]
    public void Riapri_DaChiusaSenzaValidazione_AzzeraChiusuraEConservaLoStoricoConMotivo()
    {
        var cr = Cr(StatoChangeRequest.Aperta);
        ChangeRequestChiusuraService.Chiudi(cr, "Master1", "obsoleta", true, true, Adesso);

        var esito = ChangeRequestChiusuraService.Riapri(cr, "Master2", " serve ancora ", true, true, Adesso.AddDays(1));

        Assert.Equal(EsitoTransizioneCr.Ok, esito);
        Assert.Equal(StatoChangeRequest.Aperta, cr.Stato);
        Assert.Null(cr.ChiusaIl);
        Assert.Equal("", cr.ChiusaDa);
        Assert.Equal("", cr.MotivoChiusura);
        Assert.False(cr.ChiusaSenzaValidazione);
        Assert.Equal(2, cr.Changelog.Count);
        var riapertura = cr.Changelog[^1];
        Assert.Equal("Chiusa (senza validazione)", riapertura.ValorePrecedente);
        Assert.Equal("Aperta: serve ancora", riapertura.ValoreNuovo);
        Assert.Equal("Master2", riapertura.Operatore);
    }

    [Fact]
    public void Riapri_Rifiuti_PermessiMotivoEStato()
    {
        var chiusa = Cr(StatoChangeRequest.Chiusa);
        chiusa.MotivoChiusura = "m";

        Assert.Equal(EsitoTransizioneCr.NonAutorizzato,
            ChangeRequestChiusuraService.Riapri(chiusa, "Op", "motivo", true, false, Adesso));
        Assert.Equal(EsitoTransizioneCr.MotivoMancante,
            ChangeRequestChiusuraService.Riapri(chiusa, "Master1", " ", true, true, Adesso));
        Assert.Equal(StatoChangeRequest.Chiusa, chiusa.Stato);
        Assert.Equal("m", chiusa.MotivoChiusura);
        Assert.Empty(chiusa.Changelog);

        var aperta = Cr(StatoChangeRequest.Aperta);
        Assert.Equal(EsitoTransizioneCr.StatoNonValido,
            ChangeRequestChiusuraService.Riapri(aperta, "Master1", "motivo", true, true, Adesso));
    }

    [Fact]
    public void ChiusuraDaValidata_NonMarcaESiRiapreSenzaResiduiDiChiusura()
    {
        var cr = Cr(StatoChangeRequest.Validata);
        ChangeRequestChiusuraService.Chiudi(cr, "Master1", "validata e rilasciata", true, true, Adesso);
        Assert.False(cr.ChiusaSenzaValidazione);

        ChangeRequestChiusuraService.Riapri(cr, "Master1", "regressione", true, true, Adesso.AddHours(1));

        Assert.Equal("Chiusa", cr.Changelog[^1].ValorePrecedente);
        Assert.Equal("", cr.MotivoChiusura);
    }

    [Fact]
    public void ModelloCr_JsonVecchioSenzaICampiNuovi_SiDeserializzaConDefaultSicuri()
    {
        var json = "{\"Id\":\"CR-9\",\"ChiusaDa\":\"Master1\"}";

        var cr = System.Text.Json.JsonSerializer.Deserialize<ChangeRequest>(json)!;

        Assert.Equal("", cr.MotivoChiusura);
        Assert.False(cr.ChiusaSenzaValidazione);
    }
}
