using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Chiusura e riapertura NC: motivo obbligatorio, severita se richiesta, permessi ricontrollati nel servizio
/// (non solo dai pulsanti) e controllo di revisione al salvataggio.</summary>
[Collection("Localization")]
public sealed class AnomaliaAzioniServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AnomaliaAzioni_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato(RuoloOperatore ruoloAttivo = RuoloOperatore.Master)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        var altro = TestOperatori.Sigilla(new Operatore { Nome = "Altro Prova", Ruolo = ruoloAttivo, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master, altro });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-0001", Owner = "Mario", Descrizione = "KO", Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now,
        });
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-0002", Owner = "Mario", Descrizione = "KO senza severita", Severita = null,
            StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now,
        });
        Assert.True(state.ImpostaOperatore(altro.Nome, "1234"));
        return state;
    }

    [Fact]
    public void Chiudi_ConMotivo_ChiudeELoScriveNelloStorico()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        var nc = state.NonConformita.GetById("NC-0001")!;

        AnomaliaAzioniService.Chiudi(state, nc, "  risolta con aggiornamento  ", severitaRichiesta: true);

        var chiusa = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal(StatoAnomalia.Conclusa, chiusa.StatoWorkflow);
        Assert.Equal("risolta con aggiornamento", chiusa.MotivoChiusura);
        var voce = chiusa.Changelog.Last(c => c.Campo == "Chiusura");
        Assert.Contains("risolta con aggiornamento", voce.ValoreNuovo);
        Assert.Equal(nameof(StatoAnomalia.Confermata), voce.ValorePrecedente);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Chiudi_SenzaMotivo_RifiutaENonCambiaNulla(string? motivo)
    {
        var state = CreaStato();
        var nc = state.NonConformita.GetById("NC-0001")!;

        Assert.Throws<ArgumentException>(() => AnomaliaAzioniService.Chiudi(state, nc, motivo, severitaRichiesta: true));

        Assert.Equal(StatoAnomalia.Confermata, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public void Chiudi_SeveritaRichiestaMancante_Rifiuta()
    {
        var state = CreaStato();
        var nc = state.NonConformita.GetById("NC-0002")!;

        Assert.Throws<InvalidOperationException>(() => AnomaliaAzioniService.Chiudi(state, nc, "motivo", severitaRichiesta: true));
        AnomaliaAzioniService.Chiudi(state, nc, "motivo", severitaRichiesta: false);

        Assert.Equal(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0002")!.StatoWorkflow);
    }

    [Fact]
    public void ChiudiERiapri_ComeViewer_IlServizioRifiutaAnchePassandoDallaUi()
    {
        var state = CreaStato(RuoloOperatore.Viewer);
        var nc = state.NonConformita.GetById("NC-0001")!;

        Assert.Throws<UnauthorizedAccessException>(() => AnomaliaAzioniService.Chiudi(state, nc, "motivo", severitaRichiesta: false));
        Assert.Throws<UnauthorizedAccessException>(() => AnomaliaAzioniService.Riapri(state, nc, "motivo"));

        Assert.Equal(StatoAnomalia.Confermata, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public void Riapri_RichiedeIlMotivoELoScriveNelloStorico()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        AnomaliaAzioniService.Chiudi(state, state.NonConformita.GetById("NC-0001")!, "chiusa", severitaRichiesta: false);
        var chiusa = state.NonConformita.GetById("NC-0001")!;

        Assert.Throws<ArgumentException>(() => AnomaliaAzioniService.Riapri(state, chiusa, " "));
        Assert.Equal(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);

        AnomaliaAzioniService.Riapri(state, chiusa, "il difetto si ripresenta");

        var riaperta = state.NonConformita.GetById("NC-0001")!;
        Assert.NotEqual(StatoAnomalia.Conclusa, riaperta.StatoWorkflow);
        Assert.Equal("", riaperta.MotivoChiusura);
        Assert.Contains("il difetto si ripresenta", riaperta.Changelog.Last(c => c.Campo == "Riapertura").ValoreNuovo);
    }

    [Fact]
    public void Chiudi_ConConflittoDiRevisione_NonSovrascriveICambiDiUnAltroPc()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        var vecchia = state.NonConformita.GetById("NC-0001")!;
        var altroPc = EntityStateFingerprint.Clone(vecchia);
        altroPc.Descrizione = "Aggiornata da un altro PC";
        state.NonConformita.Save(altroPc, altroPc.RevisioneDati);

        Assert.Throws<ConflittoRevisioneException>(() =>
            AnomaliaAzioniService.Chiudi(state, vecchia, "chiusura su dati vecchi", severitaRichiesta: false));

        var suDisco = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal(StatoAnomalia.Confermata, suDisco.StatoWorkflow);
        Assert.Equal("Aggiornata da un altro PC", suDisco.Descrizione);
    }
}
