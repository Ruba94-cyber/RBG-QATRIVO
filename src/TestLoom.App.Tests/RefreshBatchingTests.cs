using System.ComponentModel;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Home e Report ricalcolavano il proprio contenuto una volta per OGNI mutazione di filtro, non una volta per
/// gesto dell'utente (feedback utente 2026-09-18: "anche mettendo i filtri la cosa e' decisamente lenta...
/// sembra bloccato"). Due catene lo provocavano:
///  - FiltroMultiSelect.Ripopola notifica Cambiato quando esisteva gia' una selezione, e Cambiato e'
///    agganciato ad AggiornaDashboard/GeneraAnteprima: un solo Aggiorna() ne ripopola una dozzina;
///  - i comandi "Reimposta filtri" azzerano 6-13 filtri in sequenza, uno alla volta.
/// Su 100k Test ogni ricalcolo costa centinaia di ms fra filtraggio dell'archivio, aggregati e grafici, quindi
/// la differenza fra 1 e 13 ricalcoli e' la differenza fra una pagina reattiva e una che sembra bloccata.
///
/// Questi test contano le notifiche che ogni ricalcolo REALE emette una volta sola
/// (HaFiltriDashboardAttivi per la dashboard, TestoConteggioAnteprima/MatriceVuota per il Report): sono un
/// guardrail sul NUMERO di elaborazioni, non sul tempo, quindi non dipendono dalla macchina.
/// </summary>
[Trait("Category", "Lento")]
public class RefreshBatchingTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public RefreshBatchingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomBatching_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        _state.InizializzaCartella(_dir);
    }

    public void Dispose()
    {
        _state.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static int ContaNotifiche(INotifyPropertyChanged vm, string proprieta, Action azione)
    {
        var conteggio = 0;
        void Handler(object? _, PropertyChangedEventArgs e) { if (e.PropertyName == proprieta) conteggio++; }
        vm.PropertyChanged += Handler;
        try { azione(); }
        finally { vm.PropertyChanged -= Handler; }
        return conteggio;
    }

    private static void SelezionaPrimaOpzione(FiltroMultiSelect filtro)
    {
        var opzione = filtro.Opzioni.FirstOrDefault();
        if (opzione != null) opzione.Selezionato = true;
    }

    [Fact]
    public void HomeAggiorna_ConFiltriSelezionati_RicalcolaLaDashboardUnaVoltaSola()
    {
        var home = new HomeViewModel(_state, _ => { });
        SelezionaPrimaOpzione(home.FiltroGammaDashboard);
        SelezionaPrimaOpzione(home.FiltroVeicoloDashboard);
        SelezionaPrimaOpzione(home.FiltroEsitoDashboard);

        var ricalcoli = ContaNotifiche(home, nameof(HomeViewModel.HaFiltriDashboardAttivi), home.Aggiorna);

        Assert.Equal(1, ricalcoli);
    }

    [Fact]
    public void HomeReimpostaFiltri_AzzeraTuttoConUnSoloRicalcolo()
    {
        var home = new HomeViewModel(_state, _ => { });
        SelezionaPrimaOpzione(home.FiltroGammaDashboard);
        SelezionaPrimaOpzione(home.FiltroVeicoloDashboard);
        SelezionaPrimaOpzione(home.FiltroEsitoDashboard);
        SelezionaPrimaOpzione(home.FiltroOperatoreDashboard);
        Assert.True(home.HaFiltriDashboardAttivi);

        var ricalcoli = ContaNotifiche(home, nameof(HomeViewModel.HaFiltriDashboardAttivi),
            () => home.ReimpostaFiltriDashboardCommand.Execute(null));

        Assert.Equal(1, ricalcoli);
        // Il batching non deve "perdere" l'azzeramento: e' la regressione da temere quando si accorpano refresh.
        Assert.False(home.HaFiltriDashboardAttivi);
        Assert.False(home.FiltroGammaDashboard.HaSelezione);
        Assert.False(home.FiltroEsitoDashboard.HaSelezione);
    }

    [Fact]
    public void ReportAggiorna_ConFiltriSelezionati_RigeneraAnteprimaEMatriceUnaVoltaSola()
    {
        var report = new ReportViewModel(_state);
        SelezionaPrimaOpzione(report.FiltroTemplate);

        var anteprime = 0;
        var matrici = 0;
        void Handler(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReportViewModel.TestoConteggioAnteprima)) anteprime++;
            if (e.PropertyName == nameof(ReportViewModel.MatriceVuota)) matrici++;
        }
        report.PropertyChanged += Handler;
        try { report.Aggiorna(); }
        finally { report.PropertyChanged -= Handler; }

        Assert.Equal(1, anteprime);
        Assert.Equal(1, matrici);
    }

    [Fact]
    public void ReportReimpostaFiltri_AzzeraTuttoConUnaSolaRigenerazione()
    {
        var report = new ReportViewModel(_state);
        SelezionaPrimaOpzione(report.FiltroTemplate);
        report.FiltroEcu = report.EcuDisponibili.FirstOrDefault();
        Assert.True(report.HaFiltriAttivi);

        var anteprime = ContaNotifiche(report, nameof(ReportViewModel.TestoConteggioAnteprima),
            () => report.ReimpostaFiltriCommand.Execute(null));

        Assert.Equal(1, anteprime);
        Assert.False(report.HaFiltriAttivi);
        Assert.Null(report.FiltroEcu);
        Assert.False(report.FiltroTemplate.HaSelezione);
    }

    /// <summary>Parita' dei risultati: accorpare i ricalcoli non deve cambiare COSA si vede. L'anteprima senza
    /// filtri deve contenere tutti i Test dell'archivio, esattamente come prima del batching.</summary>
    [Fact]
    public void ReportSenzaFiltri_AnteprimaContieneTuttiITest()
    {
        var report = new ReportViewModel(_state);
        report.ReimpostaFiltriCommand.Execute(null);

        Assert.Equal(_state.TestCases.GetAll().Count, report.Anteprima.Count);
    }

    [Fact]
    public void ReportFiltro_CorruzioneIndiceNotificaCoordinatoreRecuperaERestaEquivalenteAiJson()
    {
        // Il seed generale include intenzionalmente un JSON corrotto per altri test di sicurezza. Questa prova
        // deve invece offrire al recovery una sorgente autorevole completa, altrimenti il fail-closed corretto
        // impedirebbe per contratto di marcare la ricostruzione come sana.
        foreach (var path in Directory.GetFiles(_dir, "testcase_CORROTTO-TEST-99.json", SearchOption.AllDirectories))
            File.Delete(path);
        _state.TestCases.RefreshIndex();

        var report = new ReportViewModel(_state);
        var componente = Assert.Single(report.EcuDisponibili.Take(1));
        var coordinatore = Assert.IsType<LocalIndexRecoveryCoordinator>(_state.TestCases.IndexRecovery);
        var ricostruzioniPrima = coordinatore.RicostruzioniEseguite;
        var database = Assert.IsType<string>(_state.TestCases.LocalIndexDatabasePath);

        SqliteConnection.ClearAllPools();
        using (var file = new FileStream(database, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            file.Position = 0;
            file.Write(new byte[100]);
            file.Flush(flushToDisk: true);
        }

        report.FiltroEcu = componente;

        Assert.True(coordinatore.AttendiRecupero(TimeSpan.FromSeconds(15)), coordinatore.Motivo);
        Assert.Equal(LocalIndexHealth.Healthy, coordinatore.Salute);
        Assert.Equal(ricostruzioniPrima + 1, coordinatore.RicostruzioniEseguite);
        var attesi = _state.TestCases.GetAll().Count(tc => tc.Componente.Any(x => x.Equals(componente, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(attesi, report.TotaleRisultatiAnteprima);
    }

    [Fact]
    public void NonConformitaBootstrap_RipopolaERipristinaFiltriConUnaSolaScansioneCompleta()
    {
        var refresh = 0;
        var vm = new NonConformitaViewModel(
            _state, _ => { }, null, null, null, () => refresh++);

        Assert.Equal(1, refresh);

        vm.Aggiorna();
        Assert.Equal(2, refresh);
    }
}
