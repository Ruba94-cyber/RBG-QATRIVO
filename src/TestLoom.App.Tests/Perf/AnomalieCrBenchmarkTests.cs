using System.Diagnostics;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Xunit.Abstractions;

namespace TestLoom.App.Tests.Perf;

/// <summary>
/// Benchmark mirato e riproducibile della pagina Anomalie/CR (requisito di release: P95 &lt;= 700 ms con 100.000 NC e 100.000 CR dopo il
/// precaricamento). SKIPPATO di default: attivo solo con <c>TestLoom_PERF_ANOMALIE_DATASET</c> puntata su un banco generato da
/// <c>tools/perf-dataset-gen</c> (es. <c>--nc 100000 --cr 100000 --tests 5000 --audit 200 --storico 200 --wu 200 --seed 42</c>).
/// Variabili opzionali: <c>TestLoom_PERF_ANOMALIE_CAMPIONI</c> (default 20), <c>TestLoom_PERF_ANOMALIE_OUTPUT</c> (file markdown),
/// <c>TestLoom_PERF_ANOMALIE_STORICO=1</c> (misura anche 2 aperture col percorso storico, lentissimo a 100k),
/// <c>TestLoom_PERF_INPROCESS_CONFIG</c> (config stabile: riusa il read-model locale gia' costruito).
/// Misura, con <c>Stopwatch</c> e almeno 20 campioni: costruzione della proiezione (precarico), apertura pagina a cache calda
/// (<c>Aggiorna</c>), 5 filtri comuni, 3 ordinamenti NC e 3 CR, 3 ricerche testuali, apertura dettaglio NC e CR; RAM della proiezione.
/// Esecuzione sincrona (nessun dispatcher): il tempo include lavoro che nell'app gira fuori dal thread UI.
/// </summary>
public class AnomalieCrBenchmarkTests
{
    private readonly ITestOutputHelper _o;
    public AnomalieCrBenchmarkTests(ITestOutputHelper o) => _o = o;

    private sealed record Riga(string Operazione, List<double> Ms);

    private static double Percentile(List<double> v, double p)
    {
        var s = v.OrderBy(x => x).ToList();
        var rank = (int)Math.Ceiling(p / 100.0 * s.Count);
        return s[Math.Clamp(rank - 1, 0, s.Count - 1)];
    }

    [Fact]
    public void MisuraPaginaAnomalieCr()
    {
        var ds = Environment.GetEnvironmentVariable("TestLoom_PERF_ANOMALIE_DATASET");
        if (string.IsNullOrWhiteSpace(ds) || !Directory.Exists(ds)) { _o.WriteLine("TestLoom_PERF_ANOMALIE_DATASET non impostata: benchmark auto-escluso."); return; }
        var campioni = int.TryParse(Environment.GetEnvironmentVariable("TestLoom_PERF_ANOMALIE_CAMPIONI"), out var c) && c >= 5 ? c : 20;
        var cfgPath = Environment.GetEnvironmentVariable("TestLoom_PERF_INPROCESS_CONFIG") ?? Path.Combine(Path.GetTempPath(), "ditm_anomalie_bench_cfg", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);
        var cs = new AppConfigService(cfgPath);
        cs.Save(new AppConfig { CartellaCondivisa = ds, Lingua = "it", OperatoreAttivo = "Operatore Prova A" });

        var righe = new List<Riga>();
        List<double> Nuova(string nome) { var l = new List<double>(); righe.Add(new Riga(nome, l)); return l; }
        static double Ms(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }

        var swBoot = Stopwatch.StartNew();
        using var state = new AppState(cs, runBackgroundMaintenance: false);
        var bootMs = swBoot.Elapsed.TotalMilliseconds;
        _o.WriteLine($"bootstrap AppState: {bootMs:F0} ms | NC={state.NonConformita.CountAll} CR(indice)={state.ChangeRequests.Count()} | processori={Environment.ProcessorCount}");

        // ---- precarico (costruzione proiezione) + RAM
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var ramPrima = GC.GetTotalMemory(true);
        var swPre = Stopwatch.StartNew();
        var ultimo = (0, 0);
        AnomalieCrPrecarico.PrecaricaAsync(state, new Progress<(int fatti, int totale)>(p => ultimo = p), CancellationToken.None).GetAwaiter().GetResult();
        var precaricoMs = swPre.Elapsed.TotalMilliseconds;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var ramDopo = GC.GetTotalMemory(true);
        Assert.True(state.ChangeRequests.ElencoPronto, "proiezione non costruita");
        var nCr = state.ChangeRequests.ElencoConteggio;
        _o.WriteLine($"PRECARICO: {precaricoMs:F0} ms | righe CR in proiezione={nCr} | progresso finale={ultimo}");
        _o.WriteLine($"RAM: heap prima={ramPrima / 1048576.0:F1} MB, dopo={ramDopo / 1048576.0:F1} MB, delta={(ramDopo - ramPrima) / 1048576.0:F1} MB; stima proiezione CR={state.ChangeRequests.ElencoStimaByte / 1048576.0:F1} MB ({state.ChangeRequests.ElencoStimaByte / Math.Max(1, nCr)} B/riga)");

        // ---- apertura pagina a cache calda
        var vm = new NonConformitaViewModel(state, _ => { });
        var apertura = Nuova("Apertura pagina (Aggiorna, cache calda)");
        var cataloghi = Nuova("  di cui 4 Load() dei cataloghi master (Veicoli/Gamma/Componente/Funzionalita)");
        for (var i = 0; i < campioni + 2; i++)
        {
            var t = Ms(() => vm.Aggiorna());
            var cat = Ms(() => { using var s = MasterLetturaScope.Apri(); state.Master.Veicoli.Load(); state.Master.Gamma.Load(); state.Master.Componente.Load(); state.Master.Funzionalita.Load(); });
            if (i >= 2) { apertura.Add(t); cataloghi.Add(cat); } // 2 giri di assestamento scartati
        }

        // ---- 5 filtri comuni (ognuno ricalcola NC + CR + segnalazioni, come l'app)
        void Reimposta() => vm.ReimpostaFiltriCommand.Execute(null);
        List<double> Filtro(string nome, Action<int> applica)
        {
            var l = Nuova(nome);
            for (var i = 0; i < campioni; i++) { Reimposta(); l.Add(Ms(() => applica(i))); }
            Reimposta();
            return l;
        }
        var stati = Enum.GetValues<StatoChangeRequest>();
        Filtro("Filtro 1: stato CR", i => vm.FiltroStatoCr = stati[i % stati.Length]);
        var statiNc = Enum.GetValues<StatoAnomalia>();
        Filtro("Filtro 2: stato anomalia (NC, multi)", i => vm.FiltroStatoAnomalia.Opzioni[i % vm.FiltroStatoAnomalia.Opzioni.Count].Selezionato = true);
        Filtro("Filtro 3: priorita NC (multi)", i => vm.FiltroPriorita.Opzioni[i % vm.FiltroPriorita.Opzioni.Count].Selezionato = true);
        Filtro("Filtro 4: veicolo (ambito NC + CR)", i => vm.FiltroVeicolo.Opzioni[i % Math.Min(vm.FiltroVeicolo.Opzioni.Count, 10)].Selezionato = true);
        Filtro("Filtro 5: intervallo data apertura", i => { vm.FiltroDataAperturaDa = new DateTimeOffset(new DateTime(2026, 1, 1).AddDays(i * 3)); });

        // ---- 3 ordinamenti NC e 3 CR (colonne diverse a ogni campione)
        var ncChiavi = new[] { AnomalieOrdinamentoChiave.Id, AnomalieOrdinamentoChiave.Descrizione, AnomalieOrdinamentoChiave.Gravita };
        foreach (var k in ncChiavi)
        {
            var l = Nuova($"Ordinamento NC per {k}");
            for (var i = 0; i < campioni; i++) l.Add(Ms(() => vm.ImpostaOrdinamento(k))); // un secondo clic inverte la direzione
        }
        vm.ImpostaOrdinamento(AnomalieOrdinamentoChiave.Apertura);
        var crChiavi = new[] { ChangeRequestOrdinamentoChiave.Titolo, ChangeRequestOrdinamentoChiave.Stato, ChangeRequestOrdinamentoChiave.Veicolo };
        foreach (var k in crChiavi)
        {
            var l = Nuova($"Ordinamento CR per {k}");
            for (var i = 0; i < campioni; i++) l.Add(Ms(() => vm.ImpostaOrdinamentoCr(k)));
        }
        vm.ImpostaOrdinamentoCr(ChangeRequestOrdinamentoChiave.Apertura);
        Reimposta();

        // ---- ricerca testuale (termini diversi, nessun risultato incluso)
        var termini = new[] { "calibrazione", "Guasto", "scenario 12", "zzz-inesistente", "NC-0099", "verifica" };
        var ricerca = Nuova("Ricerca testuale (NC + CR)");
        for (var i = 0; i < campioni; i++) { Reimposta(); var q = termini[i % termini.Length]; ricerca.Add(Ms(() => vm.FiltroTesto = q)); }
        Reimposta();

        // ---- dettaglio NC e CR
        var rnd = new Random(1);
        var ncId = Nuova("Apertura dettaglio NC (GetById + ViewModel)");
        var tuttiNc = state.NonConformita.PeekAllReadOnly();
        for (var i = 0; i < campioni; i++)
        {
            var id = tuttiNc[rnd.Next(tuttiNc.Count)].Id;
            ncId.Add(Ms(() =>
            {
                var nc = state.NonConformita.GetById(id) ?? throw new InvalidOperationException(id);
                _ = new AnomaliaDettaglioViewModel(state, nc.Clona());
            }));
        }
        var crDet = Nuova("Apertura dettaglio CR: costruttore editor (thread UI; giro cataloghi/Test in background)");
        var crPronto = Nuova("Apertura dettaglio CR: pronto (giro completato: 7 cataloghi + Test candidati)");
        var pagina = vm.ChangeRequests.ToList();
        if (pagina.Count == 0) { vm.Aggiorna(); pagina = vm.ChangeRequests.ToList(); }
        for (var i = 0; i < campioni; i++)
        {
            var id = pagina[rnd.Next(pagina.Count)].Id;
            ChangeRequestEditorViewModel? ed = null;
            var t = Ms(() => { ed = new ChangeRequestEditorViewModel(state, id, () => { }, _ => { }, forzaBackground: true, gateCaricamentoTest: null); });
            crDet.Add(t);
            crPronto.Add(t + Ms(() => { ed!.AttendiGiroAsync().GetAwaiter().GetResult(); ed.AttendiCaricamentoTestDisponibiliAsync().GetAwaiter().GetResult(); }));
        }

        // ---- confronto opzionale col percorso storico (2 campioni)
        if (Environment.GetEnvironmentVariable("TestLoom_PERF_ANOMALIE_STORICO") == "1")
        {
            var st = Nuova("[STORICO] Pagina CR: PeekAllReadOnly + filtro/ordinamento (percorso precedente)");
            for (var i = 0; i < 2; i++)
                st.Add(Ms(() => NonConformitaViewModel.CalcolaPaginaChangeRequest(state.ChangeRequests.PeekAllReadOnly(),
                    new NonConformitaViewModel.FiltroChangeRequest(null, null, null, null, null, null, false, null, null, null, null, null, null, 1), null)));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Banco: {ds} | NC={state.NonConformita.CountAll} CR={nCr} | campioni={campioni} | CPU logiche={Environment.ProcessorCount}");
        sb.AppendLine($"Bootstrap AppState {bootMs:F0} ms | Precarico proiezione CR+NC {precaricoMs:F0} ms | RAM proiezione CR: delta heap {(ramDopo - ramPrima) / 1048576.0:F1} MB, stima {state.ChangeRequests.ElencoStimaByte / 1048576.0:F1} MB (di cui testo descrizioni {state.ChangeRequests.ElencoStimaDescrizioniByte / 1048576.0:F1} MB)");
        sb.AppendLine();
        sb.AppendLine("| Operazione | n | P50 (ms) | P95 (ms) | max (ms) | <=700 ms |");
        sb.AppendLine("|---|---:|---:|---:|---:|:---:|");
        foreach (var r in righe)
            sb.AppendLine($"| {r.Operazione} | {r.Ms.Count} | {Percentile(r.Ms, 50):F1} | {Percentile(r.Ms, 95):F1} | {r.Ms.Max():F1} | {(Percentile(r.Ms, 95) <= 700 ? "si" : "NO")} |");
        _o.WriteLine(sb.ToString());
        if (Environment.GetEnvironmentVariable("TestLoom_PERF_ANOMALIE_OUTPUT") is { Length: > 0 } outPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
