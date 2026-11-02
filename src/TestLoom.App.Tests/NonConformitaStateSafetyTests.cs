using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class NonConformitaStateSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NcSafety_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _originalConfirm = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _originalConfirm;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        return state;
    }

    [Fact]
    public void ClassificaSegnalazioneSicuro_ConflittoSulSecondoWriter_RollbackaLaNcOrfana()
    {
        using var state = CreaStato();
        var seg = new SegnalazioneDaClassificare
        {
            TestId = "TC-CONFLICT",
            CondizioneUid = Guid.NewGuid(),
            EsecuzioneIterazione = 1,
            Stato = StatoSegnalazione.DaClassificare,
            CreataIl = DateTime.Now
        };
        state.Segnalazioni.Save(seg);

        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = Assert.Single(vm.Segnalazioni);
        riga.Severita = SeveritaAnomalia.High;

        // Simula una seconda replica che salva la Segnalazione dopo il caricamento della pagina.
        var remoto = EntityStateFingerprint.Clone(state.Segnalazioni.Get(seg.Uid)!);
        remoto.MotivazioneEsclusione = "aggiornamento remoto";
        state.Segnalazioni.Save(remoto, remoto.RevisioneDati);

        vm.ClassificaSegnalazioneSicuroCommand.Execute(riga);

        state.NonConformita.RefreshIndex();
        state.Segnalazioni.RefreshIndex();
        Assert.Empty(state.NonConformita.GetAll());
        var autorevole = state.Segnalazioni.Get(seg.Uid)!;
        Assert.Equal(StatoSegnalazione.DaClassificare, autorevole.Stato);
        Assert.Equal("aggiornamento remoto", autorevole.MotivazioneEsclusione);
        Assert.NotNull(vm.MessaggioErrore);
    }

    private (NonConformita Nc, SegnalazioneDaClassificare Seg) CreaNcConSegnalazioneClassificata(AppState state)
    {
        var nc = new NonConformita { Id = "NC-0001", TestCaseId = "TC-1", DataApertura = DateTime.Now, Severita = SeveritaAnomalia.High };
        state.NonConformita.Save(nc);
        var seg = new SegnalazioneDaClassificare
        {
            TestId = "TC-1", CondizioneUid = Guid.NewGuid(), EsecuzioneIterazione = 1, CreataIl = DateTime.Now,
            Stato = StatoSegnalazione.Classificata, AnomaliaUid = nc.Uid
        };
        state.Segnalazioni.Save(seg);
        return (nc, seg);
    }

    private static void ImpostaMaster(AppState state)
    {
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Crea("Mario Rossi", RuoloOperatore.Master));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
    }

    /// <summary>Review 2026-09-30: eliminare una NC lasciava la segnalazione 'Classificata' con AnomaliaUid pendente.</summary>
    [Fact]
    public async Task EliminaAnomaliaSicuro_RiportaLeSegnalazioniCollegateADaClassificare()
    {
        using var state = CreaStato();
        ImpostaMaster(state);
        var (nc, seg) = CreaNcConSegnalazioneClassificata(state);
        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = Assert.Single(vm.Righe);

        await vm.EliminaAnomaliaSicuroCommand.ExecuteAsync(riga);

        Assert.Null(vm.MessaggioErrore);
        Assert.Empty(state.NonConformita.GetAll());
        var autorevole = state.Segnalazioni.Get(seg.Uid)!;
        Assert.Equal(StatoSegnalazione.DaClassificare, autorevole.Stato);
        Assert.Null(autorevole.AnomaliaUid);
    }

    /// <summary>Review 6.4.3: solo le segnalazioni Classificata vengono scollegate; Esclusa/Superata restano intatte.</summary>
    [Fact]
    public async Task EliminaAnomaliaSicuro_NonToccaSegnalazioniEscluseOSuperate()
    {
        using var state = CreaStato();
        ImpostaMaster(state);
        var (nc, _) = CreaNcConSegnalazioneClassificata(state);
        var esclusa = new SegnalazioneDaClassificare
        {
            TestId = "TC-1", CondizioneUid = Guid.NewGuid(), EsecuzioneIterazione = 1, CreataIl = DateTime.Now,
            Stato = StatoSegnalazione.Esclusa, AnomaliaUid = nc.Uid
        };
        var superata = new SegnalazioneDaClassificare
        {
            TestId = "TC-1", CondizioneUid = Guid.NewGuid(), EsecuzioneIterazione = 2, CreataIl = DateTime.Now,
            Stato = StatoSegnalazione.RisoltaDaRipetizione, AnomaliaUid = nc.Uid
        };
        state.Segnalazioni.Save(esclusa);
        state.Segnalazioni.Save(superata);
        var vm = new NonConformitaViewModel(state, _ => { });

        await vm.EliminaAnomaliaSicuroCommand.ExecuteAsync(Assert.Single(vm.Righe));

        Assert.Null(vm.MessaggioErrore);
        Assert.Equal(StatoSegnalazione.Esclusa, state.Segnalazioni.Get(esclusa.Uid)!.Stato);
        Assert.Equal(StatoSegnalazione.RisoltaDaRipetizione, state.Segnalazioni.Get(superata.Uid)!.Stato);
    }

    [Fact]
    public async Task EliminaAnomaliaSicuro_FallimentoDopoLoScollegamento_RollbackSuTuttiIRecord()
    {
        using var state = CreaStato();
        ImpostaMaster(state);
        var (nc, seg) = CreaNcConSegnalazioneClassificata(state);
        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = Assert.Single(vm.Righe);

        // Un'altra postazione modifica la NC dopo il caricamento: l'Elimina (secondo writer) confligge
        // DOPO che la segnalazione e' gia' stata scollegata nella transazione.
        var remoto = EntityStateFingerprint.Clone(state.NonConformita.GetById(nc.Id)!);
        remoto.Descrizione = "modifica concorrente";
        state.NonConformita.Save(remoto, remoto.RevisioneDati);

        await vm.EliminaAnomaliaSicuroCommand.ExecuteAsync(riga);

        Assert.NotNull(vm.MessaggioErrore);
        state.NonConformita.RefreshIndex();
        state.Segnalazioni.RefreshIndex();
        Assert.Equal("modifica concorrente", Assert.Single(state.NonConformita.GetAll()).Descrizione);
        var autorevole = state.Segnalazioni.Get(seg.Uid)!;
        Assert.Equal(StatoSegnalazione.Classificata, autorevole.Stato);
        Assert.Equal(nc.Uid, autorevole.AnomaliaUid);
    }

    [Fact]
    public async Task ChiudiManualmenteSicuro_ConflittoNonMutaLoStatoLive()
    {
        using var state = CreaStato();
        var nc = new NonConformita
        {
            Id = "NC-0001",
            TestCaseId = "TC-1",
            DataApertura = DateTime.Now,
            Severita = SeveritaAnomalia.High
        };
        state.NonConformita.Save(nc);

        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = Assert.Single(vm.Righe);
        riga.Nc.MotivoChiusura = "fix verificato";

        var remoto = EntityStateFingerprint.Clone(state.NonConformita.GetById(nc.Id)!);
        remoto.Descrizione = "modifica concorrente";
        state.NonConformita.Save(remoto, remoto.RevisioneDati);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

        await vm.ChiudiManualmenteSicuroCommand.ExecuteAsync(riga.Nc);

        Assert.Equal(StatoNonConformita.Aperta, riga.Nc.Stato);
        Assert.NotNull(vm.MessaggioErrore);
        var autorevole = state.NonConformita.GetById(nc.Id)!;
        Assert.Equal(StatoNonConformita.Aperta, autorevole.Stato);
        Assert.Equal("modifica concorrente", autorevole.Descrizione);
    }

    [Fact]
    public async Task CreaDettaglio_RileggeLaNcDalJsonSenzaRefreshCompletoPrimaDiChiudere()
    {
        using var locale = CreaStato();
        ImpostaMaster(locale);
        var nc = new NonConformita
        {
            Id = "NC-REFRESH-1", TestCaseId = "TC-REFRESH", DataApertura = DateTime.Now,
            Severita = SeveritaAnomalia.High, Descrizione = "fotografia iniziale"
        };
        locale.NonConformita.Save(nc);
        var vm = new NonConformitaViewModel(locale, _ => { });
        var riga = Assert.Single(vm.Righe);

        using var remoto = TestStateFactory.Create();
        remoto.InizializzaCartella(_dir);
        var aggiornamento = EntityStateFingerprint.Clone(remoto.NonConformita.GetById(nc.Id)!);
        aggiornamento.Descrizione = "aggiornata dal secondo PC";
        remoto.NonConformita.Save(aggiornamento, aggiornamento.RevisioneDati);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

        var dettaglio = vm.CreaDettaglio(riga);
        Assert.Equal("aggiornata dal secondo PC", dettaglio.Nc.Descrizione);
        dettaglio.MotivoChiusura = "ripristino verificato";
        await dettaglio.ChiudiCommand.ExecuteAsync(null);

        var canonica = locale.NonConformita.GetById(nc.Id)!;
        Assert.Null(dettaglio.Errore);
        Assert.Equal(StatoAnomalia.Conclusa, canonica.StatoWorkflow);
        Assert.Equal("aggiornata dal secondo PC", canonica.Descrizione);
        Assert.Equal("ripristino verificato", canonica.MotivoChiusura);
    }

    [Fact]
    public async Task CreaDettaglio_ConModificaCheArrivaDopoAperturaMantieneIlConflittoFailClosed()
    {
        using var locale = CreaStato();
        ImpostaMaster(locale);
        var nc = new NonConformita
        {
            Id = "NC-REFRESH-2", TestCaseId = "TC-REFRESH", DataApertura = DateTime.Now,
            Severita = SeveritaAnomalia.High, Descrizione = "fotografia iniziale"
        };
        locale.NonConformita.Save(nc);
        var vm = new NonConformitaViewModel(locale, _ => { });
        var dettaglio = vm.CreaDettaglio(Assert.Single(vm.Righe));

        using var remoto = TestStateFactory.Create();
        remoto.InizializzaCartella(_dir);
        var aggiornamento = EntityStateFingerprint.Clone(remoto.NonConformita.GetById(nc.Id)!);
        aggiornamento.Descrizione = "aggiornata dopo l'apertura del dettaglio";
        remoto.NonConformita.Save(aggiornamento, aggiornamento.RevisioneDati);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        dettaglio.MotivoChiusura = "motivo da conservare";

        await dettaglio.ChiudiCommand.ExecuteAsync(null);

        Assert.True(dettaglio.ConflittoInAttesa);
        Assert.Contains("motivo da conservare", dettaglio.MotivoChiusura);
        var canonica = remoto.NonConformita.GetById(nc.Id)!;
        Assert.Equal(StatoAnomalia.InVerifica, canonica.StatoWorkflow);
        Assert.Equal("aggiornata dopo l'apertura del dettaglio", canonica.Descrizione);
    }

    [Fact]
    public void EscludiSegnalazioneSicuro_ConflittoNonSporcaLaRigaLive()
    {
        using var state = CreaStato();
        var seg = new SegnalazioneDaClassificare
        {
            TestId = "TC-2",
            CondizioneUid = Guid.NewGuid(),
            EsecuzioneIterazione = 1,
            Stato = StatoSegnalazione.DaClassificare,
            CreataIl = DateTime.Now
        };
        state.Segnalazioni.Save(seg);

        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = Assert.Single(vm.Segnalazioni);
        riga.MotivazioneEsclusione = "non riproducibile";

        var remoto = EntityStateFingerprint.Clone(state.Segnalazioni.Get(seg.Uid)!);
        remoto.MotivazioneEsclusione = "nota remota";
        state.Segnalazioni.Save(remoto, remoto.RevisioneDati);

        vm.EscludiSegnalazioneSicuroCommand.Execute(riga);

        Assert.Equal(StatoSegnalazione.DaClassificare, riga.Segnalazione.Stato);
        Assert.NotNull(vm.MessaggioErrore);
        var autorevole = state.Segnalazioni.Get(seg.Uid)!;
        Assert.Equal(StatoSegnalazione.DaClassificare, autorevole.Stato);
        Assert.Equal("nota remota", autorevole.MotivazioneEsclusione);
    }
}
