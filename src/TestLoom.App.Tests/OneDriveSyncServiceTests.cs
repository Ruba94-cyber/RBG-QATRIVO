using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Finto OneDrive: stato dei file, client, orologio virtuale. Nessuna attesa reale, nessun OneDrive reale.</summary>
internal sealed class FinoOneDriveAccesso : IOneDriveAccesso
{
    public bool DentroOneDrive = true;
    public bool Client = true;
    public bool ClientAvviabile = true;
    public int AvvioClientChiamate;
    public bool ScaricaFallisce;
    public bool ScansioneLancia;
    public List<FileCloud> File = new();
    public List<string> Scaricati = new();
    public IReadOnlyCollection<string> UltimiSospetti = Array.Empty<string>();
    /// <summary>Numero di scansioni dopo il quale i file in attesa di caricamento risultano caricati (null = mai).</summary>
    public int? CaricatiDopoScansioni;
    public int Scansioni;
    public Action? SuScansione;

    public bool EDentroOneDrive(string percorso) => DentroOneDrive;
    public bool ClientInEsecuzione() => Client;
    public bool AvviaClient() { AvvioClientChiamate++; if (ClientAvviabile) Client = true; return ClientAvviabile; }

    public ScansioneCloud Scansiona(string radice, IReadOnlyCollection<string> percorsiSospetti, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ScansioneLancia) throw new IOException("scansione non riuscita");
        Scansioni++;
        UltimiSospetti = percorsiSospetti;
        SuScansione?.Invoke();
        if (CaricatiDopoScansioni is { } n && Scansioni > n)
            File = File.Where(f => f.Stato != StatoFileCloud.InAttesaCaricamento).ToList();
        return new ScansioneCloud(File.Count + 5, File.Where(f => f.Stato != StatoFileCloud.Locale).ToList());
    }

    public void Scarica(string percorso, CancellationToken ct)
    {
        if (ScaricaFallisce) throw new IOException("richiamo non riuscito");
        lock (Scaricati) Scaricati.Add(percorso);
        lock (File)
            File = File.Select(f => f.Percorso == percorso ? f with { Stato = StatoFileCloud.Locale } : f).ToList();
    }
}

internal sealed class PendenteStoreInMemoria : IOneDrivePendenteStore
{
    public OneDrivePendente? Valore;
    public OneDrivePendente? Leggi(string radice) => Valore;
    public void Salva(string radice, OneDrivePendente pendente) => Valore = pendente;
    public void Cancella(string radice) => Valore = null;
}

[Collection("OneDriveStatico")]
public sealed class OneDriveSyncServiceTests : IDisposable
{
    private readonly string _radice = Path.Combine(Path.GetTempPath(), "TestLoom_OneDrive_" + Guid.NewGuid().ToString("N"));
    private readonly FinoOneDriveAccesso _od = new();
    private readonly PendenteStoreInMemoria _store = new();
    private DateTime _ora = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    public OneDriveSyncServiceTests() => Directory.CreateDirectory(_radice);

    public void Dispose()
    {
        OneDriveSyncGate.AzzeraTutto();
        try { Directory.Delete(_radice, true); } catch (IOException) { }
    }

    private string P(string relativo) => Path.Combine(_radice, relativo);

    private OneDriveSyncService Servizio(OneDriveSyncOptions? o = null) =>
        new(_od, _store, o,
            attesa: (t, ct) => { ct.ThrowIfCancellationRequested(); _ora += t; return Task.CompletedTask; },
            oraUtc: () => _ora);

    [Fact]
    public async Task CartellaNonOneDrive_NonApplicabile_NessunAccesso()
    {
        _od.DentroOneDrive = false;
        var r = await Servizio().VerificaEForzaAvvioAsync(_radice);
        Assert.Equal(EsitoSyncOneDrive.NonApplicabile, r.Esito);
        Assert.True(r.Sicuro);
        Assert.Equal(0, _od.Scansioni);
    }

    [Fact]
    public async Task Avvio_ScaricaISoloOnline_EDichiaraCompletata()
    {
        _od.File.Add(new FileCloud(P("requisiti.json"), StatoFileCloud.SoloOnline, 10));
        _od.File.Add(new FileCloud(P(@"testcases\BCM\testcase_1.json"), StatoFileCloud.SoloOnline, 10));
        var avanzamenti = new List<AvanzamentoSyncOneDrive>();

        var r = await Servizio().VerificaEForzaAvvioAsync(_radice, new SincronoProgress(avanzamenti.Add));

        Assert.Equal(EsitoSyncOneDrive.Completata, r.Esito);
        Assert.Equal(2, _od.Scaricati.Count);
        Assert.Contains(avanzamenti, a => a.Fase == FaseSyncOneDrive.Scaricamento && a.Totale == 2 && a.DaScaricare == 2);
        Assert.Contains(avanzamenti, a => a.Elaborati == 2 && a.Totale == 2);
    }

    [Fact]
    public async Task Avvio_AllegatiSoloOnline_SonoVolutiENonVengonoScaricati()
    {
        _od.File.Add(new FileCloud(P(@"allegati\test\abc\foto.png"), StatoFileCloud.SoloOnline, 5_000_000));
        var r = await Servizio().VerificaEForzaAvvioAsync(_radice);
        Assert.Equal(EsitoSyncOneDrive.Completata, r.Esito);
        Assert.Empty(_od.Scaricati);
    }

    [Fact]
    public async Task Avvio_FileTemporaneiEDiProva_NonContano()
    {
        _od.File.Add(new FileCloud(P(".testloom-write-probe-1"), StatoFileCloud.InAttesaCaricamento, 1));
        _od.File.Add(new FileCloud(P("x.json.tmp"), StatoFileCloud.InAttesaCaricamento, 1));
        _od.File.Add(new FileCloud(P("~$lock.json"), StatoFileCloud.InAttesaCaricamento, 1));
        var r = await Servizio().VerificaEForzaAvvioAsync(_radice);
        Assert.Equal(EsitoSyncOneDrive.Completata, r.Esito);
    }

    [Fact]
    public async Task Avvio_ClientSpento_LoAvviaEProcede()
    {
        _od.Client = false;
        var r = await Servizio().VerificaEForzaAvvioAsync(_radice);
        Assert.Equal(EsitoSyncOneDrive.Completata, r.Esito);
        Assert.Equal(1, _od.AvvioClientChiamate);
    }

    [Fact]
    public async Task Avvio_ClientNonAvviabile_ClientNonAttivo_ENonSicuro()
    {
        _od.Client = false;
        _od.ClientAvviabile = false;
        var r = await Servizio().VerificaEForzaAvvioAsync(_radice);
        Assert.Equal(EsitoSyncOneDrive.ClientNonAttivo, r.Esito);
        Assert.False(r.Sicuro);
    }

    [Fact]
    public async Task Avvio_RichiamoCheFallisce_ScadeAlTimeoutVirtuale_ConConteggi()
    {
        _od.ScaricaFallisce = true;
        _od.File.Add(new FileCloud(P("requisiti.json"), StatoFileCloud.SoloOnline, 10));
        _od.File.Add(new FileCloud(P("componente.json"), StatoFileCloud.SoloOnline, 10));
        _od.File.Add(new FileCloud(P("a.json"), StatoFileCloud.InAttesaCaricamento, 10));
        var inizio = _ora;

        var r = await Servizio().VerificaEForzaAvvioAsync(_radice);

        Assert.Equal(EsitoSyncOneDrive.Timeout, r.Esito);
        Assert.Equal(2, r.DaScaricare);
        Assert.Equal(1, r.DaCaricare);
        Assert.False(r.Sicuro);
        Assert.True(_ora - inizio >= TimeSpan.FromSeconds(45));
        Assert.True(_ora - inizio < TimeSpan.FromSeconds(50));
    }

    [Fact]
    public async Task Avvio_CaricamentoLocaleInSospeso_AttendeChePartaPoiCompleta()
    {
        _od.File.Add(new FileCloud(P("a.json"), StatoFileCloud.InAttesaCaricamento, 10));
        _od.CaricatiDopoScansioni = 3;
        var r = await Servizio().VerificaEForzaAvvioAsync(_radice);
        Assert.Equal(EsitoSyncOneDrive.Completata, r.Esito);
        Assert.Equal(4, _od.Scansioni);
    }

    [Fact]
    public async Task Avvio_PendenteDellaChiusuraPrecedente_VieneVerificatoEPoiCancellato()
    {
        _store.Valore = new OneDrivePendente { NumeroFile = 1, Percorsi = { "a.json" } };
        var r = await Servizio().VerificaEForzaAvvioAsync(_radice);
        Assert.Equal(EsitoSyncOneDrive.Completata, r.Esito);
        Assert.Contains("a.json", _od.UltimiSospetti);
        Assert.Null(_store.Valore);
    }

    [Fact]
    public async Task Chiusura_AttendeIlCaricamento_Completata_ECancellaIlPendente()
    {
        _store.Valore = new OneDrivePendente { NumeroFile = 9 };
        _od.File.Add(new FileCloud(P("a.json"), StatoFileCloud.InAttesaCaricamento, 10));
        _od.File.Add(new FileCloud(P("b.json"), StatoFileCloud.SoloOnline, 10)); // alla chiusura non interessa
        _od.CaricatiDopoScansioni = 2;

        var r = await Servizio().AttendiCaricamentoChiusuraAsync(_radice);

        Assert.Equal(EsitoSyncOneDrive.Completata, r.Esito);
        Assert.Empty(_od.Scaricati);
        Assert.Null(_store.Valore);
    }

    [Fact]
    public async Task Chiusura_TimeoutSessanta_PersistePendente_ChiudeConAvviso()
    {
        _od.File.Add(new FileCloud(P(@"testcases\BCM\testcase_9.json"), StatoFileCloud.InAttesaCaricamento, 10));
        _od.File.Add(new FileCloud(P("requisiti.json"), StatoFileCloud.InAttesaCaricamento, 10));
        var inizio = _ora;

        var r = await Servizio().AttendiCaricamentoChiusuraAsync(_radice);

        Assert.Equal(EsitoSyncOneDrive.Timeout, r.Esito);
        Assert.Equal(2, r.DaCaricare);
        Assert.InRange((_ora - inizio).TotalSeconds, 60, 65);
        Assert.NotNull(_store.Valore);
        Assert.Equal(2, _store.Valore!.NumeroFile);
        Assert.Contains(@"testcases\BCM\testcase_9.json", _store.Valore.Percorsi);
        Assert.Contains("requisiti.json", _store.Valore.Percorsi);
    }

    [Fact]
    public async Task Chiusura_PendentePersistitoEPoiRipreso_AlProssimoAvvio()
    {
        _od.File.Add(new FileCloud(P("a.json"), StatoFileCloud.InAttesaCaricamento, 10));
        var chiusura = await Servizio().AttendiCaricamentoChiusuraAsync(_radice);
        Assert.Equal(EsitoSyncOneDrive.Timeout, chiusura.Esito);
        Assert.NotNull(_store.Valore);

        // Prossimo avvio: OneDrive nel frattempo ha caricato.
        _od.CaricatiDopoScansioni = 0;
        var avvio = await Servizio().VerificaEForzaAvvioAsync(_radice);
        Assert.Equal(EsitoSyncOneDrive.Completata, avvio.Esito);
        Assert.Contains("a.json", _od.UltimiSospetti);
        Assert.Null(_store.Valore);
    }

    [Fact]
    public async Task Chiusura_AnnullataDall_Utente_PersistePendente()
    {
        _od.File.Add(new FileCloud(P("a.json"), StatoFileCloud.InAttesaCaricamento, 10));
        using var cts = new CancellationTokenSource();
        _od.SuScansione = () => { if (_od.Scansioni == 2) cts.Cancel(); };

        var r = await Servizio().AttendiCaricamentoChiusuraAsync(_radice, null, cts.Token);

        Assert.Equal(EsitoSyncOneDrive.Annullata, r.Esito);
        Assert.NotNull(_store.Valore);
        Assert.Equal(1, _store.Valore!.NumeroFile);
    }

    [Fact]
    public async Task Chiusura_NienteDaCaricare_RapidaSenzaAttese()
    {
        var inizio = _ora;
        var r = await Servizio().AttendiCaricamentoChiusuraAsync(_radice);
        Assert.Equal(EsitoSyncOneDrive.Completata, r.Esito);
        Assert.Equal(inizio, _ora);
    }

    [Fact]
    public async Task ErroreImprevisto_DiventaErrore_NonLanciaENonBloccaLApp()
    {
        _od.ScansioneLancia = true;
        var r = await OneDriveSyncDiTest(Servizio()).VerificaEApplicaAsync();
        Assert.Equal(EsitoSyncOneDrive.Errore, r.Esito);
        Assert.False(OneDriveSyncGate.Attivo(_radice));
    }

    [Fact]
    public async Task EsitoNonRiuscito_AttivaSolaLettura_PoliticaDiScritturaFailClosed_RiprovaRiuscitaLoSblocca()
    {
        _od.ScaricaFallisce = true;
        _od.File.Add(new FileCloud(P("requisiti.json"), StatoFileCloud.SoloOnline, 10));
        var ap = OneDriveSyncDiTest(Servizio());

        var r = await ap.VerificaEApplicaAsync();

        Assert.Equal(EsitoSyncOneDrive.Timeout, r.Esito);
        Assert.True(OneDriveSyncGate.Attivo(_radice));
        Assert.Contains("1", OneDriveSyncGate.Motivo(_radice));
        var paths = new DataPaths(_radice);
        Assert.False(WorkspaceWritePolicy.CanWrite(paths));
        var ex = Assert.Throws<IOException>(() => WorkspaceWritePolicy.EnsureCanWrite(paths, "Salvataggio test"));
        Assert.Contains("OneDrive", ex.Message);
        Assert.Equal(StatoCondivisione.SolaLettura, OneDriveSyncGate.Esito(_radice)!.Stato);

        // Riprova: il richiamo ora riesce.
        _od.ScaricaFallisce = false;
        var riprova = await ap.VerificaEApplicaAsync();
        Assert.Equal(EsitoSyncOneDrive.Completata, riprova.Esito);
        Assert.False(OneDriveSyncGate.Attivo(_radice));
        Assert.Null(OneDriveSyncGate.Esito(_radice));
    }

    [Fact]
    public async Task ClientNonAttivo_AttivaSolaLetturaConMessaggioDedicato()
    {
        _od.Client = false;
        _od.ClientAvviabile = false;
        await OneDriveSyncDiTest(Servizio()).VerificaEApplicaAsync();
        Assert.True(OneDriveSyncGate.Attivo(_radice));
        Assert.Contains("OneDrive", OneDriveSyncGate.Motivo(_radice));
        Assert.Contains("Riprova", OneDriveSyncGate.Motivo(_radice), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppState_ConBloccoOneDrive_ApreInSolaLettura_ENonScriveSulCondiviso()
    {
        var paths = new DataPaths(_radice);
        paths.EnsureStructure();
        OneDriveSyncGate.Blocca(_radice, "OneDrive non allineato (test).");

        using var state = TestStateFactory.Create();
        state.ImpostaCartella(_radice);

        Assert.True(state.InizializzazioneWorkspaceCompletata);
        Assert.Equal(StatoCondivisione.SolaLettura, state.StatoCondivisione.Stato);
        Assert.Contains("OneDrive", state.StatoCondivisione.Messaggio);
        Assert.False(state.PuoScrivere);
        Assert.False(File.Exists(Path.Combine(_radice, ".testloom-write-probe")));

        // Dopo lo sblocco ("Riprova" riuscito) la rilettura riporta il workspace operativo.
        OneDriveSyncGate.Sblocca(_radice);
        state.RileggiDaDisco();
        Assert.Equal(StatoCondivisione.Operativa, state.StatoCondivisione.Stato);
    }

    [Fact]
    public void Localizzazione_TuttiITestiOneDrive_HannoItalianoInglese_E_Francese()
    {
        var chiavi = new[]
        {
            "OneDriveVerificaTesto", "OneDriveAvvioClientTesto", "OneDriveScaricamentoFmt", "OneDriveAttesaCaricamentoFmt",
            "OneDriveSolaLetturaTimeoutFmt", "OneDriveSolaLetturaClientTesto", "OneDriveRiprovaBtn", "OneDriveUscitaTitolo",
            "OneDriveUscitaTesto", "OneDriveUscitaFmt", "OneDriveUscitaChiudiSubito", "OneDriveUscitaChiudi", "OneDriveUscitaAvvisoFmt",
        };
        foreach (var k in chiavi)
            Assert.True(LocalizationService.TraduzioniFrancesi.ContainsKey(k), "manca FR: " + k);
        var loc = LocalizationService.Instance;
        var lingua = loc.Lingua;
        try
        {
            foreach (var l in new[] { "it", "en", "fr" })
            {
                loc.Lingua = l;
                foreach (var k in chiavi) Assert.NotEqual(k, loc.Traduci(k));
            }
            loc.Lingua = "it";
            Assert.Contains("3", OneDriveSync.TestoAvanzamento(new AvanzamentoSyncOneDrive(FaseSyncOneDrive.Scaricamento, 0, 4, 3, 1, TimeSpan.Zero)));
        }
        finally { loc.Lingua = lingua; }
    }

    // ---- helper ---------------------------------------------------------------------------------------------

    private OneDriveSyncDiTestHelper OneDriveSyncDiTest(OneDriveSyncService s) => new(s, _radice);

    /// <summary>Applica l'esito del servizio come fa <see cref="OneDriveSync.VerificaEApplicaAsync"/>, con il servizio di test.</summary>
    private sealed class OneDriveSyncDiTestHelper
    {
        private readonly OneDriveSyncService _s;
        private readonly string _radice;
        public OneDriveSyncDiTestHelper(OneDriveSyncService s, string radice) { _s = s; _radice = radice; }

        public async Task<RisultatoSyncOneDrive> VerificaEApplicaAsync()
        {
            var precedente = OneDriveSync.Servizio;
            OneDriveSync.Servizio = _s;
            try { return await OneDriveSync.VerificaEApplicaAsync(_radice, null); }
            finally { OneDriveSync.Servizio = precedente; }
        }
    }

    private sealed class SincronoProgress : IProgress<AvanzamentoSyncOneDrive>
    {
        private readonly Action<AvanzamentoSyncOneDrive> _a;
        public SincronoProgress(Action<AvanzamentoSyncOneDrive> a) => _a = a;
        public void Report(AvanzamentoSyncOneDrive value) => _a(value);
    }
}
