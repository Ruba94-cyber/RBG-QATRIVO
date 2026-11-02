using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>
/// Benchmark dell'apertura e della ricerca degli editor master ad alta crescita (componente e Requisiti) su un workspace
/// Phase D sintetico, a scala 5k, 10k, 20k, 50k righe per catalogo (di default 10k e 50k).
/// Gating: TestLoom_PERF_MASTER_EDITOR=1. Senza variabile il test esce subito senza misurare nulla.
/// TestLoom_PERF_MASTER_SCALE=N limita il giro a una sola scala; TestLoom_PERF_MASTER_LABEL dà l'etichetta ai file (default "corrente").
///
/// Misure (p50, p95, max, MB allocati per operazione, contatori PerfContatori Load/LoadCatalog per operazione):
///  - Load a freddo: PhaseDMasterRepositoryEngine.Load() con indice SQLite locale vuoto, una cartella nuova per campione.
///  - Load a caldo, engine: stessa istanza e stessa cartella locale, dopo un warm-up.
///  - Load a caldo, repository: state.Master.X.Load() sull'AppState, dopo un warm-up. La differenza con l'engine e'
///    il costo del repository (tracciamento delle righe in ConditionalWeakTable compreso).
///  - Apertura a freddo: primo editor su un AppState appena creato (3 campioni).
///  - Apertura a caldo: new XViewModel(state) sullo stesso AppState, dopo un warm-up (5 campioni).
///  - Ricerca: FiltroTesto impostato a ogni tasto (prefissi crescenti di un nome bersaglio), piu' il reset del filtro.
/// Per ogni vista mostrata si registra un checksum (FNV-1a sulle chiavi di ItemsAttivi, in ordine): confrontando
/// il TSV prima e dopo una modifica si verifica che siano mostrate le stesse righe nello stesso ordine.
///
/// Output: .ai/artifacts/perf/master-editor-{etichetta}-{N}k.tsv (campioni grezzi) e .md (tabella p50/p95/max),
/// nella radice del repository (.ai non versionata). Il workspace sintetico vive sotto .ai/tmp e viene rimosso a fine test.
/// %LOCALAPPDATA% e' gia' reindirizzato da TestLocalAppDataFixture.
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfMasterEditorBenchmarkTests
{
    private const int CampioniLoad = 5;
    private const int CampioniAperturaFredda = 3;
    private const int CampioniApertura = 5;
    private const int VeicoliBanco = 200;
    private const string GammaBanco = "Gamma-BENCH";

    private readonly ITestOutputHelper _output;
    public PerfMasterEditorBenchmarkTests(ITestOutputHelper output) => _output = output;

    private sealed record Misura(string Catalogo, string Operazione, int Indice, double Ms, double MbAlloc,
        long Load, long LoadCatalog, string Checksum, int Righe, long Refresh = 0);

    [Theory]
    [InlineData(5_000)]
    [InlineData(10_000)]
    [InlineData(20_000)]
    [InlineData(50_000)]
    public void MisuraEditorMasterScala(int scala)
    {
        if (Environment.GetEnvironmentVariable("TestLoom_PERF_MASTER_EDITOR") != "1")
        {
            _output.WriteLine("TestLoom_PERF_MASTER_EDITOR non impostata a 1: benchmark escluso (non eseguito).");
            return;
        }
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MASTER_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo))
        {
            if (scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;
        }
        else if (scala is 5_000 or 20_000)
        {
            return; // Di default girano solo 10k e 50k; 5k e 20k solo con TestLoom_PERF_MASTER_SCALE.
        }

        var radice = TrovaRadiceRepo()
            ?? throw new InvalidOperationException("Radice del repository non trovata (manca TestLoom.slnx).");
        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MASTER_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var temporanea = Path.Combine(radice, ".ai", "tmp", "perf-master-" + Guid.NewGuid().ToString("N")[..8]);
        var misure = new List<Misura>();
        try
        {
            var dati = Path.Combine(temporanea, "dati");
            var locale = Path.Combine(temporanea, "locale");
            Directory.CreateDirectory(dati);
            Directory.CreateDirectory(locale);
            var paths = new DataPaths(dati);
            paths.EnsureStructure();
            PhaseDMasterBootstrapService.EnsureReady(paths);

            // Seme diretto nel journal (come PhaseDMasterLoadBenchmarkTests): isola il costo di lettura da quello di scrittura.
            var seme = new SeedJournal(paths);
            seme.Scrivi("componente", scala, n => new Componente { Nome = $"Componente-BENCH-{n:D6}" });
            seme.Scrivi("requisiti", scala, n => new Requisito
            {
                Id = $"REQ-BENCH-{n:D6}",
                Testo = $"Requisito di prova {n} per il banco editor",
                ApplicabileBanco = true,
                ApplicabileVeicolo = true,
            });
            seme.Scrivi("veicoli", VeicoliBanco, n => new Veicolo { Nome = $"VEI-BENCH-{n:D4}", GammaNome = GammaBanco });

            var configService = new AppConfigService(Path.Combine(locale, "config.json"));
            configService.Save(new AppConfig { CartellaCondivisa = dati, Lingua = "it", OperatoreAttivo = "" });

            misure.AddRange(MisuraLoadFreddo<Componente>("componente", scala, dati, temporanea));
            misure.AddRange(MisuraLoadFreddo<Requisito>("requisiti", scala, dati, temporanea));
            misure.AddRange(MisuraLoadEngineACaldo<Componente>("componente", scala, dati, temporanea));
            misure.AddRange(MisuraLoadEngineACaldo<Requisito>("requisiti", scala, dati, temporanea));

            misure.AddRange(MisuraAperturaFredda<Componente, EcuViewModel>("componente", scala, configService, s => new EcuViewModel(s)));
            misure.AddRange(MisuraAperturaFredda<Requisito, RequisitiViewModel>("requisiti", scala, configService, s => new RequisitiViewModel(s)));

            // Scomposizione dell'apertura e della ricerca: stesse chiamate che fa l'editor, una alla volta.
            misure.AddRange(MisuraParti<Componente>("componente", scala, dati, temporanea, MasterDataPagedReadSources.Componente(paths),
                n => $"Componente-BENCH-{n:D6}"));
            misure.AddRange(MisuraParti<Requisito>("requisiti", scala, dati, temporanea,
                MasterDataPagedReadSources.Requisiti(paths), n => $"REQ-BENCH-{n:D6}"));

            using (var stato = new AppState(configService, runBackgroundMaintenance: false))
            {
                misure.AddRange(MisuraLoadACaldo("componente", scala, () => stato.Master.Componente.Load().Count));
                misure.AddRange(MisuraLoadACaldo("requisiti", scala, () => stato.Master.Requisiti.Load().Count));

                misure.AddRange(MisuraAperturaACaldo<Componente, EcuViewModel>("componente", scala, stato, s => new EcuViewModel(s)));
                misure.AddRange(MisuraAperturaACaldo<Requisito, RequisitiViewModel>("requisiti", scala, stato, s => new RequisitiViewModel(s)));

                misure.AddRange(MisuraRicerca<Componente, EcuViewModel>("componente", scala, stato, s => new EcuViewModel(s), n => $"Componente-BENCH-{n:D6}"));
                misure.AddRange(MisuraRicerca<Requisito, RequisitiViewModel>("requisiti", scala, stato, s => new RequisitiViewModel(s), n => $"REQ-BENCH-{n:D6}"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(temporanea, recursive: true); } catch { }
        }

        ScriviRisultati(radice, etichetta, scala, misure);
        foreach (var gruppo in misure.GroupBy(m => (m.Catalogo, m.Operazione)))
            _output.WriteLine($"{gruppo.Key.Catalogo} | {gruppo.Key.Operazione}: n={gruppo.Count()} " +
                              $"p50={Percentile(gruppo.Select(m => m.Ms), 50):F1} ms p95={Percentile(gruppo.Select(m => m.Ms), 95):F1} ms");
    }

    private static List<Misura> MisuraLoadFreddo<T>(string catalogo, int righe, string dati, string temporanea)
        where T : MasterEntity
    {
        var res = new List<Misura>();
        for (var k = 0; k < CampioniAperturaFredda; k++)
        {
            var locale = Path.Combine(temporanea, $"freddo-{catalogo}-{k}");
            Directory.CreateDirectory(locale);
            var engine = new PhaseDMasterRepositoryEngine<T>(dati, catalogo, localBaseDirectory: locale);
            GcStabile();
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var caricato = engine.Load();
            sw.Stop();
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(righe, caricato.Items.Count);
            res.Add(new Misura(catalogo, "Load a freddo (engine, indice locale vuoto)", k, sw.Elapsed.TotalMilliseconds,
                MB(a1 - a0), 0, 0, "", caricato.Items.Count));
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(locale, recursive: true); } catch { }
        }
        return res;
    }

    private static List<Misura> MisuraLoadEngineACaldo<T>(string catalogo, int righe, string dati, string temporanea)
        where T : MasterEntity
    {
        var locale = Path.Combine(temporanea, $"caldo-engine-{catalogo}");
        Directory.CreateDirectory(locale);
        var engine = new PhaseDMasterRepositoryEngine<T>(dati, catalogo, localBaseDirectory: locale);
        engine.Load(); // Warm-up: materializza l'indice locale.
        var res = new List<Misura>();
        for (var k = 0; k < CampioniLoad; k++)
        {
            GcStabile();
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var caricato = engine.Load();
            sw.Stop();
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(righe, caricato.Items.Count);
            res.Add(new Misura(catalogo, "Load a caldo (engine, stessa istanza)", k, sw.Elapsed.TotalMilliseconds,
                MB(a1 - a0), 0, 0, "", caricato.Items.Count));
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(locale, recursive: true); } catch { }
        return res;
    }

    /// <summary>Parti dell'apertura e della ricerca, misurate separatamente: refresh dell'indice, prima pagina SQL,
    /// chiavi attive (picker Unisci), materializzazione del catalogo Phase D (senza post-elaborazione dell'engine),
    /// e per tasto la sola query SQL della pagina (senza la vista).</summary>
    private static List<Misura> MisuraParti<T>(string catalogo, int righe, string dati, string temporanea,
        MasterDataPagedReadSource<T> sorgente, Func<int, string> nomeDi) where T : MasterEntity
    {
        var locale = Path.Combine(temporanea, $"parti-{catalogo}");
        Directory.CreateDirectory(locale);
        var materializzatore = new PhaseDMasterMaterializer(dati, locale);
        var res = new List<Misura>();
        res.AddRange(Campiona(catalogo, "Parte: enumerazione sorgenti journal (senza leggerle)", -1,
            () => PhaseDMasterJournalSourceReader.Enumerate(dati).Count));
        res.AddRange(Campiona(catalogo, "Parte: Refresh materializzatore (no-op, globale sul workspace)", -1,
            () => { materializzatore.Refresh(dati); return 0; }));
        res.AddRange(Campiona(catalogo, "Parte: refresh indice (Refresh, catalogo gia' allineato)", -1,
            () => { sorgente.Refresh(); return 0; }));
        res.AddRange(Campiona(catalogo, "Parte: prima pagina SQL (200 righe, nessun filtro)", Math.Min(righe, 200),
            () => sorgente.ReadPage(null, 1, 200).Items.Count));
        res.AddRange(Campiona(catalogo, "Parte: chiavi attive (picker Unisci)", righe,
            () => sorgente.ReadAllActiveKeys().Count));
        res.AddRange(Campiona(catalogo, "Parte: materializzazione Phase D (LoadCatalog, stessa cartella locale)", righe,
            () => materializzatore.LoadCatalog<T>(dati, catalogo).Items.Count));

        var bersagli = new[] { 1, righe / 4, righe / 2, 3 * righe / 4, righe };
        foreach (var n in bersagli)
        {
            var nome = nomeDi(n);
            for (var lunghezza = 1; lunghezza <= nome.Length; lunghezza++)
            {
                var prefisso = nome[..lunghezza];
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                var pagina = sorgente.ReadPage(prefisso, 1, 200);
                sw.Stop();
                var a1 = GC.GetAllocatedBytesForCurrentThread();
                res.Add(new Misura(catalogo, "Ricerca: sola query SQL per tasto (pagina 200)", n, sw.Elapsed.TotalMilliseconds,
                    MB(a1 - a0), 0, 0, "", pagina.Items.Count));
            }
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(locale, recursive: true); } catch { }
        return res;
    }

    /// <summary>Cinque campioni dopo un warm-up e un GC stabilizzato. Se righeAttese e' negativo non verifica il risultato.</summary>
    private static List<Misura> Campiona(string catalogo, string operazione, int righeAttese, Func<int> op)
    {
        op(); // Warm-up.
        var res = new List<Misura>();
        for (var k = 0; k < CampioniLoad; k++)
        {
            GcStabile();
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var n = op();
            sw.Stop();
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            if (righeAttese >= 0) Assert.Equal(righeAttese, n);
            res.Add(new Misura(catalogo, operazione, k, sw.Elapsed.TotalMilliseconds, MB(a1 - a0), 0, 0, "", n));
        }
        return res;
    }

    private static List<Misura> MisuraLoadACaldo(string catalogo, int righe, Func<int> carica)
    {
        carica(); // Warm-up.
        var res = new List<Misura>();
        for (var k = 0; k < CampioniLoad; k++)
        {
            GcStabile();
            var c0 = PerfContatori.LeggiSnapshot();
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var n = carica();
            sw.Stop();
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            var c1 = PerfContatori.LeggiSnapshot();
            Assert.Equal(righe, n);
            res.Add(new Misura(catalogo, "Load a caldo (repository dell'AppState)", k, sw.Elapsed.TotalMilliseconds,
                MB(a1 - a0), c1.MasterLoad - c0.MasterLoad, c1.LoadCatalog - c0.LoadCatalog, "", n));
        }
        return res;
    }

    private static List<Misura> MisuraAperturaFredda<T, TVm>(string catalogo, int righe, AppConfigService cfg,
        Func<AppState, TVm> crea)
        where T : MasterEntity, new()
        where TVm : MasterDataEditorViewModelBase<T>
    {
        var res = new List<Misura>();
        for (var k = 0; k < CampioniAperturaFredda; k++)
        {
            using var stato = new AppState(cfg, runBackgroundMaintenance: false);
            GcStabile();
            var c0 = PerfContatori.LeggiSnapshot();
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            using var vm = crea(stato);
            sw.Stop();
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            var c1 = PerfContatori.LeggiSnapshot();
            // Controllo di sanita' fuori dal tempo: la vista mostra la prima pagina (o tutto il catalogo se piu' piccolo).
            Assert.Equal(Math.Min(righe, vm.DimensionePagina), vm.ItemsAttivi.Count);
            res.Add(new Misura(catalogo, "Apertura editor a freddo (primo editor su AppState nuovo)", k,
                sw.Elapsed.TotalMilliseconds, MB(a1 - a0), c1.MasterLoad - c0.MasterLoad,
                c1.LoadCatalog - c0.LoadCatalog, Checksum<T>(vm), vm.ItemsAttivi.Count, c1.PhaseDRefreshEseguiti - c0.PhaseDRefreshEseguiti));
        }
        return res;
    }

    private static List<Misura> MisuraAperturaACaldo<T, TVm>(string catalogo, int righe, AppState stato,
        Func<AppState, TVm> crea)
        where T : MasterEntity, new()
        where TVm : MasterDataEditorViewModelBase<T>
    {
        crea(stato).Dispose(); // Warm-up.
        var res = new List<Misura>();
        for (var k = 0; k < CampioniApertura; k++)
        {
            GcStabile();
            var c0 = PerfContatori.LeggiSnapshot();
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var vm = crea(stato);
            sw.Stop();
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            var c1 = PerfContatori.LeggiSnapshot();
            Assert.Equal(Math.Min(righe, vm.DimensionePagina), vm.ItemsAttivi.Count);
            res.Add(new Misura(catalogo, "Apertura editor a caldo (AppState gia' aperto)", k, sw.Elapsed.TotalMilliseconds,
                MB(a1 - a0), c1.MasterLoad - c0.MasterLoad, c1.LoadCatalog - c0.LoadCatalog, Checksum<T>(vm), vm.ItemsAttivi.Count,
                c1.PhaseDRefreshEseguiti - c0.PhaseDRefreshEseguiti));
            vm.Dispose();
        }
        return res;
    }

    private static List<Misura> MisuraRicerca<T, TVm>(string catalogo, int scala, AppState stato, Func<AppState, TVm> crea,
        Func<int, string> nomeDi)
        where T : MasterEntity, new()
        where TVm : MasterDataEditorViewModelBase<T>
    {
        using var vm = crea(stato);
        var res = new List<Misura>();
        // Bersagli distribuiti lungo il catalogo: ogni giro digita il nome completo carattere per carattere.
        foreach (var n in new[] { 1, scala / 4, scala / 2, 3 * scala / 4, scala })
        {
            var nome = nomeDi(n);
            vm.FiltroTesto = "";
            GcStabile();
            for (var lunghezza = 1; lunghezza <= nome.Length; lunghezza++)
            {
                var prefisso = nome[..lunghezza];
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                vm.FiltroTesto = prefisso;
                vm.ConfermaRicercaImmediata(); // query per tasto come prima del debounce: Invio applica subito
                sw.Stop();
                var a1 = GC.GetAllocatedBytesForCurrentThread();
                res.Add(new Misura(catalogo, "Ricerca: un tasto (vista paginata)", n, sw.Elapsed.TotalMilliseconds,
                    MB(a1 - a0), 0, 0, Checksum<T>(vm), vm.ItemsAttivi.Count));
            }
            // Digitazione con debounce (ritardo di 60 s nel banco, cosi' nessuna query scade durante la misura): il tempo
            // sul thread UI per tasto e' solo l'impostazione del campo. La query in attesa si applica a fine misura.
            vm.DebounceRicerca = TimeSpan.FromSeconds(60);
            for (var lunghezza = 1; lunghezza <= nome.Length; lunghezza++)
            {
                var prefisso = nome[..lunghezza];
                var a4 = GC.GetAllocatedBytesForCurrentThread();
                var sw4 = Stopwatch.StartNew();
                vm.FiltroTesto = prefisso;
                sw4.Stop();
                var a5 = GC.GetAllocatedBytesForCurrentThread();
                res.Add(new Misura(catalogo, "Ricerca: digitazione con debounce (solo il campo)", n, sw4.Elapsed.TotalMilliseconds,
                    MB(a5 - a4), 0, 0, "", 0));
            }
            vm.ConfermaRicercaImmediata();
            vm.DebounceRicerca = TimeSpan.FromMilliseconds(250);
            var a2 = GC.GetAllocatedBytesForCurrentThread();
            var sw2 = Stopwatch.StartNew();
            vm.FiltroTesto = "";
            sw2.Stop();
            var a3 = GC.GetAllocatedBytesForCurrentThread();
            res.Add(new Misura(catalogo, "Ricerca: reset del filtro", n, sw2.Elapsed.TotalMilliseconds,
                MB(a3 - a2), 0, 0, Checksum<T>(vm), vm.ItemsAttivi.Count));
        }
        return res;
    }

    /// <summary>FNV-1a sulle chiavi delle righe mostrate, nell'ordine di vista: stesse righe e stesso ordine danno lo stesso valore.</summary>
    private static string Checksum<T>(MasterDataEditorViewModelBase<T> vm) where T : MasterEntity, new()
    {
        ulong h = 14695981039346656037UL;
        foreach (var item in vm.ItemsAttivi)
        {
            foreach (var c in vm.ChiaveArchivio(item))
                h = (h ^ c) * 1099511628211UL;
            h = (h ^ 0x0A) * 1099511628211UL;
        }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static void ScriviRisultati(string radice, string etichetta, int scala, List<Misura> misure)
    {
        var cartella = Path.Combine(radice, ".ai", "artifacts", "perf");
        Directory.CreateDirectory(cartella);
        var radiceFile = Path.Combine(cartella, $"master-editor-{etichetta}-{scala / 1000}k");

        var tsv = new System.Text.StringBuilder();
        tsv.AppendLine("catalogo\toperazione\tindice\tms\tmb_alloc\tload\tload_catalog\tchecksum\trighe\trefresh");
        foreach (var m in misure)
            tsv.AppendLine(string.Join('\t', m.Catalogo, m.Operazione, m.Indice,
                m.Ms.ToString("F3", CultureInfo.InvariantCulture), m.MbAlloc.ToString("F2", CultureInfo.InvariantCulture),
                m.Load, m.LoadCatalog, m.Checksum, m.Righe, m.Refresh));
        File.WriteAllText(radiceFile + ".tsv", tsv.ToString());

        var configurazione =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var md = new System.Text.StringBuilder();
        md.AppendLine($"# Editor master: {etichetta}, {scala} righe per catalogo");
        md.AppendLine();
        md.AppendLine($"Configurazione {configurazione}, .NET {Environment.Version}, UTC {DateTime.UtcNow:O}. " +
                      $"Catalogo veicoli sintetico: {VeicoliBanco} righe. Journal Phase D seminato direttamente.");
        md.AppendLine();
        md.AppendLine("| Catalogo | Operazione | Campioni | p50 ms | p95 ms | max ms | MB alloc p50 | Load/op | LoadCatalog/op | Refresh/op |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var gruppo in misure.GroupBy(m => (m.Catalogo, m.Operazione)))
        {
            var ms = gruppo.Select(m => m.Ms).ToList();
            var p50 = Percentile(ms, 50).ToString("F1", CultureInfo.InvariantCulture);
            var p95 = Percentile(ms, 95).ToString("F1", CultureInfo.InvariantCulture);
            var max = ms.Max().ToString("F1", CultureInfo.InvariantCulture);
            var mbP50 = Percentile(gruppo.Select(m => m.MbAlloc), 50).ToString("F2", CultureInfo.InvariantCulture);
            md.AppendLine($"| {gruppo.Key.Catalogo} | {gruppo.Key.Operazione} | {ms.Count} | {p50} | {p95} | {max} | " +
                          $"{mbP50} | {gruppo.Max(m => m.Load)} | {gruppo.Max(m => m.LoadCatalog)} | {gruppo.Max(m => m.Refresh)} |");
        }
        File.WriteAllText(radiceFile + ".md", md.ToString());
    }

    /// <summary>Percentile nearest-rank (stesso metodo di PickerRicercaBenchmarkTests).</summary>
    private static double Percentile(IEnumerable<double> valori, double p)
    {
        var ordinati = valori.OrderBy(x => x).ToArray();
        var rango = (int)Math.Ceiling(p / 100.0 * ordinati.Length);
        return ordinati[Math.Clamp(rango - 1, 0, ordinati.Length - 1)];
    }

    private static double MB(long bytes) => bytes / 1048576.0;

    private static void GcStabile()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string? TrovaRadiceRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
        return null;
    }

    /// <summary>Scrive righe Phase D con il percorso di pubblicazione reale (PhaseDMasterJournalService.Append), a blocchi
    /// da 2000 mutazioni: il journal ruota in segmenti da 512 KB e si compatta in checkpoint come in produzione. Un file
    /// current.ndjson unico di decine di MB non e' un layout che l'app produce, e falserebbe il costo di Refresh.
    /// Il device e' quello locale (Append lo richiede); la sequenza di batch viene dal contatore locale.</summary>
    private sealed class SeedJournal
    {
        private const int RighePerBatch = 2000;
        private readonly DataPaths _paths;
        private readonly string _dispositivo;

        public SeedJournal(DataPaths paths)
        {
            _paths = paths;
            _dispositivo = DeviceIdentityService.GetOrCreate();
        }

        public void Scrivi<T>(string catalogo, int righe, Func<int, T> crea) where T : MasterEntity
        {
            var scritte = 0;
            while (scritte < righe)
            {
                var quante = Math.Min(RighePerBatch, righe - scritte);
                var sequenza = PhaseDMasterJournalService.NextDeviceSequence(_paths.Root);
                var mutazioni = new List<PhaseDMasterMutation>(quante);
                for (var i = 0; i < quante; i++)
                {
                    var entita = crea(scritte + i + 1);
                    var uid = MasterEntityIdentityService.GetOrCreate(entita);
                    var payload = JsonSerializer.Serialize(entita, JsonIo.CompactOptions);
                    mutazioni.Add(new PhaseDMasterMutation
                    {
                        MutationId = Guid.NewGuid(),
                        Catalog = catalogo,
                        EntityUid = uid,
                        Operation = PhaseDMasterMutationOperation.Upsert,
                        VersionVector = new Dictionary<string, long> { [_dispositivo] = sequenza },
                        PayloadJson = payload,
                        PayloadSha256 = PhaseDMasterMutation.HashPayload(payload),
                        BusinessKey = PhaseDMasterBusinessKeyService.Get(entita),
                        TimestampUtc = DateTime.UtcNow,
                    });
                }
                var batch = new PhaseDMasterMutationBatch
                {
                    BatchId = Guid.NewGuid(),
                    DeviceId = _dispositivo,
                    DeviceSequence = sequenza,
                    CreatedUtc = DateTime.UtcNow,
                    Mutations = mutazioni,
                };
                PhaseDMasterJournalService.Append(_paths, batch);
                scritte += quante;
            }
        }
    }
}
