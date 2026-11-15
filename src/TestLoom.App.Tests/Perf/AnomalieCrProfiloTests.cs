using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Xunit.Abstractions;

namespace TestLoom.App.Tests.Perf;

/// <summary>Profilo per fasi della pagina Anomalie/CR su un banco esistente (env TestLoom_PERF_ANOMALIE_DATASET). Auto-escluso senza variabile.</summary>
public class AnomalieCrProfiloTests
{
    private readonly ITestOutputHelper _o;
    public AnomalieCrProfiloTests(ITestOutputHelper o) => _o = o;

    private static double Ms(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }

    [Fact]
    public void ProfiloFasi()
    {
        var ds = Environment.GetEnvironmentVariable("TestLoom_PERF_ANOMALIE_DATASET");
        if (string.IsNullOrWhiteSpace(ds) || !Directory.Exists(ds)) return;
        var cfg = Environment.GetEnvironmentVariable("TestLoom_PERF_INPROCESS_CONFIG") ?? Path.Combine(Path.GetTempPath(), "ditm_prof_cfg", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        var cs = new AppConfigService(cfg);
        cs.Save(new AppConfig { CartellaCondivisa = ds, Lingua = "it", OperatoreAttivo = "Operatore Prova A" });
        var sw = Stopwatch.StartNew();
        using var state = new AppState(cs, runBackgroundMaintenance: false);
        _o.WriteLine($"bootstrap {sw.ElapsedMilliseconds} ms");
        AnomalieCrPrecarico.PrecaricaAsync(state, null, CancellationToken.None).GetAwaiter().GetResult();
        if (Environment.GetEnvironmentVariable("TestLoom_PERF_PROFILO_NAV") == "1")
        {
            var main = new MainViewModel(state);
            main.SelectedNavItem = main.NavItems.First(n => n.Id == "anomalie");
            for (var i = 0; i < 3; i++)
            {
                main.SelectedNavItem = main.NavItems.First(n => n.Id == "home");
                PerfContatori.Reset();
                var t = Ms(() => main.SelectedNavItem = main.NavItems.First(n => n.Id == "anomalie"));
                var c = PerfContatori.LeggiSnapshot();
                _o.WriteLine($"NAV anomalie (pagina in cache): {t:F1} ms loadCatalog={c.LoadCatalog} masterLoad={c.MasterLoad} phaseD={c.PhaseDRefreshEseguiti}/{c.PhaseDRefreshAccorpati}");
                var pag = (NonConformitaViewModel)main.CurrentPage!;
                PerfContatori.Reset();
                var t2 = Ms(() => pag.Aggiorna());
                var c2 = PerfContatori.LeggiSnapshot();
                _o.WriteLine($"Aggiorna diretto: {t2:F1} ms loadCatalog={c2.LoadCatalog} masterLoad={c2.MasterLoad} phaseD={c2.PhaseDRefreshEseguiti}/{c2.PhaseDRefreshAccorpati}");
            }
            return;
        }
        var vm = new NonConformitaViewModel(state, _ => { });
        for (var giro = 0; giro < 4; giro++)
        {
            _o.WriteLine($"--- giro {giro}");
            foreach (var chiave in new[] { ChangeRequestOrdinamentoChiave.Apertura, ChangeRequestOrdinamentoChiave.Titolo, ChangeRequestOrdinamentoChiave.Stato })
            {
                var t = Ms(() => state.ChangeRequests.TryQueryElenco(new ChangeRequestElencoFiltro(), new ChangeRequestOrdinamento(chiave, giro % 2 == 0), 1, 250, default, out _));
                var f = state.ChangeRequests.UltimeFasiElencoMs;
                _o.WriteLine($"CR TryQueryElenco {chiave}: {t:F1} ms | verifica={f.Verifica:F1} interroga={f.Interroga:F1} pagina={f.Pagina:F1}");
            }
            var tc = Ms(() => state.ChangeRequests.TryQueryElenco(new ChangeRequestElencoFiltro(Stato: StatoChangeRequest.Chiusa), ChangeRequestOrdinamento.Predefinito, 1, 250, default, out _));
            var f2 = state.ChangeRequests.UltimeFasiElencoMs;
            _o.WriteLine($"CR stato=Chiusa: {tc:F1} ms | verifica={f2.Verifica:F1} interroga={f2.Interroga:F1} pagina={f2.Pagina:F1}");
            var req = new AnomalieQueryRichiesta(new AnomalieFiltroNc(), new AnomalieFiltroAmbito(), AnomalieOrdinamento.Predefinito, 1, 250);
            _o.WriteLine($"NC.Query default             {Ms(() => AnomalieQueryService.Esegui(state.NonConformita, id => state.TestCases.GetById(id), req, trovaUid: state.TestCases.GetByUid)):F1} ms");
            var reqS = new AnomalieQueryRichiesta(new AnomalieFiltroNc { StatiWorkflow = new[] { StatoAnomalia.Confermata } }, new AnomalieFiltroAmbito(), AnomalieOrdinamento.Predefinito, 1, 250);
            _o.WriteLine($"NC.Query stato               {Ms(() => AnomalieQueryService.Esegui(state.NonConformita, id => state.TestCases.GetById(id), reqS, trovaUid: state.TestCases.GetByUid)):F1} ms");
            _o.WriteLine($"Segnalazioni.GetAll          {Ms(() => state.Segnalazioni.GetAll()):F1} ms");
            _o.WriteLine($"NC.TryAssegnatariDistinti    {Ms(() => state.NonConformita.TryAssegnatariDistinti(out _)):F1} ms");
            _o.WriteLine($"CR.TryValoriElenco           {Ms(() => state.ChangeRequests.TryValoriElenco()):F1} ms");
            _o.WriteLine($"Master 4 Load (scope)        {Ms(() => { using var s2 = MasterLetturaScope.Apri(); state.Master.Veicoli.Load(); state.Master.Gamma.Load(); state.Master.Componente.Load(); state.Master.Funzionalita.Load(); }):F1} ms");
            PerfContatori.Reset();
            var tAgg = Ms(() => vm.Aggiorna());
            var cnt = PerfContatori.LeggiSnapshot();
            _o.WriteLine($"VM Aggiorna                  {tAgg:F1} ms  (righe={vm.Righe.Count} cr={vm.ChangeRequests.Count} totCr={vm.TotaleCr} loadCatalog={cnt.LoadCatalog} masterLoad={cnt.MasterLoad})");
            _o.WriteLine($"VM ordina CR titolo          {Ms(() => vm.ImpostaOrdinamentoCr(ChangeRequestOrdinamentoChiave.Titolo)):F1} ms");
            _o.WriteLine($"VM ordina NC id              {Ms(() => vm.ImpostaOrdinamento(AnomalieOrdinamentoChiave.Id)):F1} ms");
            _o.WriteLine($"VM FiltroStatoCr             {Ms(() => vm.FiltroStatoCr = (giro % 2 == 0) ? StatoChangeRequest.Chiusa : StatoChangeRequest.Aperta):F1} ms");
            var gen = state.ChangeRequests.PeekAllReadOnly().FirstOrDefault();
            if (gen != null) _o.WriteLine($"CR editor ctor               {Ms(() => { var ed = new ChangeRequestEditorViewModel(state, gen.Id, () => { }, _ => { }, forzaBackground: true, gateCaricamentoTest: null); }):F1} ms");
        }
    }
}
