using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Tests.Perf;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>
/// Banco Home del ciclo prestazioni 6.6.1 (wt-p661-opt-ricerca): apertura della Home con tutte le sezioni visibili, due tempi per
/// percorso: "blocco" = ritorno del costruttore con aggiornaInBackground, "fino a dati" = dopo l'attesa dei dati applicati.
/// Stesso workspace del banco ricerca (Test, NC e CR = scala; segnalazioni da classificare = 2.000), letto dal seme riusabile
/// (<c>.ai/tmp/perf-opt-ricerca-seme/{scala}</c>, creato dal banco ricerca con TestLoom_PERF_CREA_SEME=1). Senza seme il workspace si genera.
///
/// Attivo solo con TestLoom_PERF_OPT_HOME=1. TestLoom_PERF_MEDIA_SCALE limita la scala, TestLoom_PERF_MEDIA_LABEL etichetta l'output.
/// Per ogni percorso: 1 warm-up non cronometrato e 5 misure, GC stabilizzato prima di ogni misura; p50 = mediana.
/// Il file usa solo API presenti anche nella base: lo stesso file compila nella build della base e in quella del worktree.
/// Output: .ai/artifacts/perf/home-opt-{etichetta}-{N}k.md e .tsv nella radice del repository (non versionata).
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfP661HomeOptTests
{
    private const int CampioniMisurati = 5;
    private const int SegnalazioniDaClassificare = 2_000;
    private const string NomeMaster = "Operatore Banco Master";
    private const string PinBanco = "1234";

    private readonly ITestOutputHelper _output;
    public PerfP661HomeOptTests(ITestOutputHelper output) => _output = output;

    private sealed record RigaPerf(string Percorso, double P50, double Max, double DeltaWsMb, double[] Campioni,
        PerfContatori.Snapshot Contatori, double GcPausaP50Ms);

    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public void MisuraHomeTutteLeSezioniOptScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_OPT_HOME"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;

        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radiceRepo = TrovaRadiceRepo();
        var baseTemporanea = radiceRepo != null
            ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-opt-home")
            : Path.Combine(Path.GetTempPath(), "TestLoom_PerfOptHome");
        var root = Path.Combine(baseTemporanea, $"{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var configPath = Path.Combine(root, "local", "config.json");
        var righe = new List<RigaPerf>();
        var contesto = new List<string>();
        try
        {
            var cartellaSeme = radiceRepo != null
                ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-opt-ricerca-seme", scala.ToString(CultureInfo.InvariantCulture))
                : null;
            var usaSeme = cartellaSeme != null && File.Exists(Path.Combine(cartellaSeme, "completo.txt"));
            var configService = usaSeme
                ? PreparaDaSeme(scala, cartellaSeme!, dataRoot, configPath)
                : CostruisciWorkspace(scala, dataRoot, configPath);
            _output.WriteLine($"WORKSPACE|scala={scala}|sorgente={(usaSeme ? "seme" : "generato")}");

            using var state = new AppState(configService, runBackgroundMaintenance: false);
            AttivaOperatoreMaster(state);
            contesto.Add($"Dataset: Test={scala}, NC={scala}, CR={scala}, segnalazioni da classificare={SegnalazioniDaClassificare}; " +
                $"sorgente del workspace: {(usaSeme ? "seme riusato" : "generato in questa run")}.");

            // Apertura come la vive la UI (MainViewModel passa aggiornaInBackground: true): blocco e fino a dati applicati.
            righe.AddRange(MisuraDueTempi("Home (apertura) - tutte le sezioni",
                () => new HomeViewModel(state, _ => { }, aggiornaInBackground: true),
                vm => vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult(),
                vm => Scarta(vm)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }

        var md = ComponiMarkdown(scala, etichetta, righe, contesto);
        _output.WriteLine(md);
        if (radiceRepo == null)
        {
            _output.WriteLine("ARTEFATTI|radice_repo_non_trovata=true|scritti=false");
            return;
        }
        var suffisso = $"{scala / 1000}k";
        var cartellaPerf = Path.Combine(radiceRepo, ".ai", "artifacts", "perf");
        Directory.CreateDirectory(cartellaPerf);
        File.WriteAllText(Path.Combine(cartellaPerf, $"home-opt-{etichetta}-{suffisso}.md"), md, Encoding.UTF8);
        File.WriteAllText(Path.Combine(cartellaPerf, $"home-opt-{etichetta}-{suffisso}.tsv"), ComponiTsv(righe), Encoding.UTF8);
        _output.WriteLine($"ARTEFATTI|cartella={cartellaPerf}|etichetta={etichetta}|scala={suffisso}");
    }

    // --- Workspace (generazione o seme, come il banco ricerca) ---

    /// <summary>Workspace sintetico (Test, NC e CR = scala; segnalazioni da classificare = 2.000), come il banco ricerca.</summary>
    private AppConfigService CostruisciWorkspace(int scala, string dataRoot, string configPath)
    {
        SeedDemoDataService.GeneraDemoPulita(dataRoot);
        var paths = new DataPaths(dataRoot);
        paths.EnsureStructure();

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "" });

        List<string> ecuNomi, veicoliNomi, funzionalitaNomi, requisitoId, revisioniUdtChiavi;
        int testAttivi, ncAttive;
        using (var state = new AppState(configService, runBackgroundMaintenance: false))
        {
            BancoScaleSeedCore.SeedCataloghiEWorkingUnit(state, dataRoot, _output,
                BancoScaleSeedCore.TargetCatalogoDefault, scala);
            ecuNomi = state.Master.Componente.Load().Select(e => e.Nome).ToList();
            veicoliNomi = state.Master.Veicoli.Load().Select(v => v.Nome).ToList();
            funzionalitaNomi = state.Master.Funzionalita.Load().Select(f => f.Nome).ToList();
            requisitoId = state.Master.Requisiti.Load().Select(r => r.Id).ToList();
            revisioniUdtChiavi = state.Master.RevisioniUdt.Load().Select(r => r.WorkingUnitRev).ToList();
            testAttivi = state.TestCases.Count();
            ncAttive = state.NonConformita.CountAll;
        }

        var testEsistenti = Directory.Exists(paths.TestCasesDir)
            ? Directory.EnumerateFiles(paths.TestCasesDir, "testcase_*.json", SearchOption.AllDirectories).Count()
            : 0;
        var ncEsistenti = Directory.Exists(paths.NonConformitaDir)
            ? Directory.EnumerateFiles(paths.NonConformitaDir, "nc_*.json").Count()
            : 0;
        var crEsistenti = Directory.Exists(paths.ChangeRequestDir)
            ? Directory.EnumerateFiles(paths.ChangeRequestDir, "cr_*.json").Count()
            : 0;

        var testIds = BancoPerf100kGeneratoreTests.CreaTestRealistici(paths, Math.Max(0, scala - testAttivi), testEsistenti,
            ecuNomi, veicoliNomi, funzionalitaNomi, requisitoId, revisioniUdtChiavi);
        BancoPerf100kGeneratoreTests.CreaNonConformitaRealistiche(paths, Math.Max(0, scala - ncAttive), ncEsistenti, testIds);
        BancoPerf100kGeneratoreTests.CreaChangeRequestRealistiche(paths, Math.Max(0, scala - crEsistenti), crEsistenti, testIds);

        using (var statoDati = new AppState(configService, runBackgroundMaintenance: false))
        {
            var testConCondizione = statoDati.TestCases.GetAll()
                .Where(t => t.Esecuzioni.Any(e => e.Condizioni.Count > 0))
                .Take(SegnalazioniDaClassificare).ToList();
            if (testConCondizione.Count == 0) throw new InvalidOperationException("Nessun Test con condizioni per le segnalazioni del banco.");
            for (var i = 0; i < SegnalazioniDaClassificare; i++)
            {
                var test = testConCondizione[i % testConCondizione.Count];
                var condizione = test.Esecuzioni.First(e => e.Condizioni.Count > 0).Condizioni[0];
                statoDati.Segnalazioni.Save(new SegnalazioneDaClassificare
                {
                    Uid = Guid.NewGuid(),
                    TestId = test.Id,
                    CondizioneUid = condizione.Uid,
                    Stato = StatoSegnalazione.DaClassificare,
                    CreataIl = DateTime.Now.AddSeconds(-i),
                });
            }
        }

        return ConcludiWorkspace(scala, dataRoot, configService);
    }

    /// <summary>Parte finale comune a generazione e seme: purga della cache locale, bootstrap di verifica del dataset e configurazione.</summary>
    private static AppConfigService ConcludiWorkspace(int scala, string dataRoot, AppConfigService configService)
    {
        // Stessa purga del generatore prima del bootstrap finale: senza, AppState riprenderebbe la fotografia locale precedente.
        BancoPerf100kGeneratoreTests.PurgaCacheWorkspaceLocale(dataRoot);

        using (var statoFinale = new AppState(configService, runBackgroundMaintenance: false))
        {
            var testCount = statoFinale.TestCases.Count();
            var ncCount = statoFinale.NonConformita.CountAll;
            if (testCount < scala || ncCount < scala) throw new InvalidOperationException($"Dataset incompleto: test={testCount}, nc={ncCount}.");
        }

        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "" });
        return configService;
    }

    /// <summary>Workspace dal seme: copia dei dati condivisi nella radice di questa ripetizione, poi la stessa parte finale della generazione.</summary>
    private static AppConfigService PreparaDaSeme(int scala, string cartellaSeme, string dataRoot, string configPath)
    {
        CopiaCartella(Path.Combine(cartellaSeme, "data"), dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "" });
        return ConcludiWorkspace(scala, dataRoot, configService);
    }

    /// <summary>Copia ricorsiva con robocopy (molti file piccoli): i codici di uscita 0-7 sono successo.</summary>
    private static void CopiaCartella(string origine, string destinazione)
    {
        Directory.CreateDirectory(destinazione);
        var avvio = new ProcessStartInfo("robocopy", $"\"{origine}\" \"{destinazione}\" /E /MT:8 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var processo = Process.Start(avvio) ?? throw new InvalidOperationException("robocopy non avviabile.");
        processo.StandardOutput.ReadToEnd();
        processo.WaitForExit();
        if (processo.ExitCode >= 8) throw new IOException($"robocopy fallito (codice {processo.ExitCode}) copiando '{origine}'.");
    }

    /// <summary>Operatore Master della sessione di misura (come il banco ricerca): la Home legge l'operatore corrente.</summary>
    private static void AttivaOperatoreMaster(AppState state)
    {
        state.Master.Operatori.Save(new List<Operatore>
        {
            TestOperatori.Sigilla(new Operatore { Nome = NomeMaster, Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash(PinBanco) }),
        });
        if (!state.ImpostaOperatore(NomeMaster, PinBanco)) throw new InvalidOperationException("Impossibile attivare l'operatore Master del banco.");
    }

    // --- Misura (stesso schema del banco ricerca) ---

    /// <summary>Misura un percorso in due tempi: "blocco" = fino al ritorno di <paramref name="operazione"/>, "fino a dati" =
    /// dopo <paramref name="attesa"/>. Stesso warm-up e stesse misure del banco ricerca.</summary>
    private IEnumerable<RigaPerf> MisuraDueTempi<T>(string percorso, Func<T> operazione, Action<T> attesa, Action<T> dopo)
    {
        var warmUp = operazione();
        attesa(warmUp);
        dopo(warmUp);
        var wsBase = MemoriaWorkingSetMb();

        var blocco = new double[CampioniMisurati];
        var fino = new double[CampioniMisurati];
        var pauseGc = new double[CampioniMisurati];
        PerfContatori.Snapshot contatori = default;
        for (var i = 0; i < CampioniMisurati; i++)
        {
            StabilizzaGc();
            PerfContatori.Reset();
            var pausaPrima = GC.GetTotalPauseDuration();
            var sw = Stopwatch.StartNew();
            var risultato = operazione();
            blocco[i] = sw.Elapsed.TotalMilliseconds;
            attesa(risultato);
            fino[i] = sw.Elapsed.TotalMilliseconds;
            pauseGc[i] = (GC.GetTotalPauseDuration() - pausaPrima).TotalMilliseconds;
            contatori = PerfContatori.LeggiSnapshot();
            dopo(risultato);
        }
        var deltaWs = MemoriaWorkingSetMb() - wsBase;
        return new[]
        {
            RigaDaCampioni(percorso + " - blocco thread UI", blocco, deltaWs, contatori, pauseGc),
            RigaDaCampioni(percorso + " - fino a dati", fino, deltaWs, contatori, pauseGc),
        };
    }

    private RigaPerf RigaDaCampioni(string percorso, double[] campioni, double deltaWs, PerfContatori.Snapshot contatori, double[] pauseGc)
    {
        var ordinati = campioni.OrderBy(v => v).ToArray();
        var riga = new RigaPerf(percorso, Mediana(ordinati), ordinati[^1], deltaWs, campioni, contatori,
            Mediana(pauseGc.OrderBy(v => v).ToArray()));
        _output.WriteLine($"BENCHMARK|percorso={percorso}|p50={F(riga.P50)}|max={F(riga.Max)}" +
            $"|pausaGcP50Ms={F(riga.GcPausaP50Ms)}" +
            $"|deltaWsMb={deltaWs.ToString("F1", CultureInfo.InvariantCulture)}" +
            $"|masterLoad={contatori.MasterLoad}|phaseDRefresh={contatori.PhaseDRefreshEseguiti}" +
            $"|phaseDRefreshAccorpati={contatori.PhaseDRefreshAccorpati}" +
            $"|campioni={string.Join(';', campioni.Select(F))}");
        return riga;
    }

    private static double Mediana(double[] ordinati)
    {
        var n = ordinati.Length;
        return n % 2 == 1 ? ordinati[n / 2] : (ordinati[n / 2 - 1] + ordinati[n / 2]) / 2.0;
    }

    private static void StabilizzaGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double MemoriaWorkingSetMb()
    {
        using var processo = Process.GetCurrentProcess();
        return processo.WorkingSet64 / 1024.0 / 1024.0;
    }

    private static void Scarta(object? oggetto) => (oggetto as IDisposable)?.Dispose();

    private static string F(double valore) => valore.ToString("F1", CultureInfo.InvariantCulture);

    private static string ComponiMarkdown(int scala, string etichetta, IReadOnlyList<RigaPerf> righe, IReadOnlyList<string> contesto)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Home con tutte le sezioni, apertura con aggiornaInBackground — {etichetta} — {scala} Test — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine($"Ambiente: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} thread logici, " +
            $"{RuntimeInformation.FrameworkDescription}, memoria disponibile al GC " +
            $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0:F1} GB.");
        sb.AppendLine();
        sb.AppendLine("1 warm-up non cronometrato + 5 misure; p50 = mediana. \"blocco thread UI\" = ritorno del costruttore; " +
            "\"fino a dati\" = dopo l'attesa dei dati applicati. Contatori = ultimo giro misurato.");
        foreach (var riga in contesto)
        {
            sb.AppendLine();
            sb.AppendLine(riga);
        }
        sb.AppendLine();
        sb.AppendLine("| Percorso | p50 (ms) | max (ms) | Pausa GC p50 (ms) | ΔWS (MB) | Load master | Refresh Phase D (n) | Refresh accorpati | campioni (ms) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var riga in righe)
        {
            var c = riga.Contatori;
            sb.AppendLine($"| {riga.Percorso} | {F(riga.P50)} | {F(riga.Max)} | {F(riga.GcPausaP50Ms)} | " +
                $"{riga.DeltaWsMb.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)} | " +
                $"{c.MasterLoad} | {c.PhaseDRefreshEseguiti} | {c.PhaseDRefreshAccorpati} | " +
                $"{string.Join("; ", riga.Campioni.Select(F))} |");
        }
        return sb.ToString();
    }

    private static string ComponiTsv(IReadOnlyList<RigaPerf> righe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("percorso\tp50_ms\tmax_ms\tgc_pausa_p50_ms\tdelta_ws_mb\tmaster_load\tphased_refresh_n\tphased_refresh_accorpati");
        foreach (var riga in righe)
        {
            var c = riga.Contatori;
            sb.AppendLine(string.Join("\t", riga.Percorso, F(riga.P50), F(riga.Max), F(riga.GcPausaP50Ms),
                riga.DeltaWsMb.ToString("F1", CultureInfo.InvariantCulture),
                c.MasterLoad, c.PhaseDRefreshEseguiti, c.PhaseDRefreshAccorpati));
        }
        return sb.ToString();
    }

    /// <summary>Radice del repository (cartella con TestLoom.slnx) risalendo dalla cartella di esecuzione dei test.</summary>
    private static string? TrovaRadiceRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
        return null;
    }
}
