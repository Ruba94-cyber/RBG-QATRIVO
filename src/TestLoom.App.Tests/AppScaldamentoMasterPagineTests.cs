using System.Reflection;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Copertura dell'audit prestazioni 6.4.0 sui dati reali (2026-09-26): "Master" (Impostazioni, prima apertura)
/// e "Master.TipiDiTest" erano le uniche due pagine sopra i 700ms alla prima visita (1364ms e 739ms, vedi
/// .ai/artifacts/perf_datireali-71224a2.csv.mediane.csv), perché né ImpostazioniViewModel né le sue sotto-pagine
/// passano mai da MainViewModel._pagineCache (OttieniPagina esclude "impostazioni" esplicitamente, e
/// ImpostazioniViewModel.SostituisciPaginaCorrente ricostruisce ogni sotto-sezione con item.Crea() a ogni
/// visita): a differenza di WU Status/Report/Test, nessuna pagina di Impostazioni veniva scaldata durante il
/// bootstrap. Questi test riproducono, per riflessione, esattamente la chiamata privata
/// App.ScaldaPagineAsync(state, mainViewModel, progress) usata da App.axaml.cs prima di mostrare la MainWindow,
/// e verificano le due correzioni mirate: (1) il warm-up "solo tipo" di ImpostazioniView/TestTemplateView (stessa
/// tecnica già usata per ArchivioView/TestCaseEditorView, mai un ViewModel/DataContext reale) non lancia e non
/// tocca OttieniPagina/la cache delle pagine; (2) TestCaseRepository.GetAllOrdinatiPerId() — usata dall'editor
/// per il selettore "Test collegati" e memoizzata pigramente (~200ms la prima volta su un archivio grande) — è
/// ormai precalcolata dallo stesso scaldamento, non più pagata dalla prima apertura reale dell'editor.
/// </summary>
public sealed class AppScaldamentoMasterPagineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ScaldamentoMaster_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreateMasterState()
    {
        var dataRoot = Path.Combine(_root, "data");
        var configPath = Path.Combine(_root, "local", "config.json");
        Directory.CreateDirectory(dataRoot);
        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Master Test" });

        var state = new AppState(configService, runBackgroundMaintenance: false);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    /// <summary>Invoca App.ScaldaPagineAsync esattamente come App.axaml.cs, senza dover costruire una vera
    /// finestra/Application Avalonia (non disponibile in un test headless): stesso principio già usato da
    /// TestCaseListViewPreCostruzioneScaldamentoTests per il resto dello scaldamento.</summary>
    private static async Task InvocaScaldaPagineAsync(AppState state, MainViewModel main)
    {
        var metodo = typeof(App).GetMethod("ScaldaPagineAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(metodo);
        var progress = new Progress<BootstrapProgress>();
        var task = (Task)metodo!.Invoke(null, new object?[] { state, main, progress, null, true, null })!;
        await task;
    }

    [Fact]
    public async Task ScaldaPagineAsync_PreMemoizzaGetAllOrdinatiPerId_PrimaAperturaEditorNonLaCalcolaPiu()
    {
        var state = CreateMasterState();
        using var _dispose = state;
        state.TestCases.Save(new TestCase { Id = "TC-00001", Componente = "Componente-000", Veicolo = "Veicolo-000" }, isNew: true);
        var main = new MainViewModel(state);

        var campo = typeof(TestCaseRepository).GetField("_attiviOrdinatiPerId", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(campo);

        // Prima dello scaldamento la cache memoizzata e' ancora vuota: nessun punto del bootstrap l'ha ancora
        // toccata (senza la correzione, resterebbe cosi' fino alla prima apertura reale di un editor Test).
        Assert.Null(campo!.GetValue(state.TestCases));

        await InvocaScaldaPagineAsync(state, main);

        // Con la correzione, ScaldaPagineAsync la precalcola: la prima apertura reale dell'editor (che la
        // legge per il selettore "Test collegati", vedi TestCaseEditorViewModel.RicaricaListeMaster) trova
        // gia' il risultato in cache invece di pagare l'ordinamento dell'intero archivio a finestra visibile.
        Assert.NotNull(campo.GetValue(state.TestCases));
    }

    [Fact]
    public async Task ScaldaPagineAsync_ScaldaAncheLeViewMasterETipiDiTest_SenzaLanciareNeToccareLaCache()
    {
        var state = CreateMasterState();
        using var _dispose = state;
        var main = new MainViewModel(state);

        // Non deve lanciare (la View "Master" e' 323 righe di XAML compilato mai toccate finora dallo
        // scaldamento) e non deve introdurre alcuna cache per "impostazioni": OttieniPagina deve continuare a
        // costruirla sempre da zero, esattamente come oggi (vedi commento MainViewModel.OttieniPagina).
        await InvocaScaldaPagineAsync(state, main);

        var impostazioni = main.NavItems.First(n => n.Id == "impostazioni");
        var prima = impostazioni.Crea();
        var seconda = impostazioni.Crea();
        Assert.NotSame(prima, seconda);
    }
}
