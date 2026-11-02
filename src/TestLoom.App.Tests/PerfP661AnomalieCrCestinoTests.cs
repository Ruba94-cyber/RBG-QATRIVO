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
/// Banco del ciclo prestazioni 6.6.1 (wt-p661-anomalie): pagina Anomalie/CR (segnalazioni da classificare, elenco NC
/// e Change Request, filtro CR), editor di una Change Request, cestino di recovery e ricerca globale.
/// Volumi: Test, NC e CR = scala; segnalazioni da classificare = 2.000; record nel cestino = 1.000.
///
/// Attivo solo con TestLoom_PERF_P661=1 (categoria Benchmark, fuori dal giro standard). Per ogni percorso: 1 warm-up non
/// cronometrato e 5 misure, GC stabilizzato prima di ogni misura. Dove il ViewModel esegue il lavoro in background
/// (Anomalie, con <c>ForzaBackground</c> come in UI reale), i tempi sono due: "blocco" = quanto resta occupato il thread
/// chiamante (UI) prima che il metodo ritorni; "fino a dati" = blocco + attesa dei dati applicati. Dove il lavoro e' ancora
/// sincrono i due tempi coincidono e viene riportato un solo tempo.
///
/// Output: .ai/artifacts/perf/p661-anomalie-{etichetta}-{N}k.md (tabella) e .tsv (dati). Etichetta da TestLoom_PERF_MEDIA_LABEL
/// (default "corrente"); TestLoom_PERF_MEDIA_SCALE limita il giro a una scala.
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfP661AnomalieCrCestinoTests
{
    private const int CampioniMisurati = 5;
    private const int SegnalazioniDaClassificare = 2_000;
    private const int RecordCestino = 1_000;

    // Termini diversi a ogni giro (warm-up incluso): ogni misura cambia davvero la query.
    private static readonly string[] TerminiRicercaGlobale =
    {
        "Operatore-Bench-07", "scenario banco 17", "REQ-SCALE-0",
        "Componente-Scale-00", "Veicolo-Scale-0", "banco 42",
    };

    // Valori di filtro CR consecutivi sempre distinti: ogni passo ricarica l'elenco.
    private static readonly StatoChangeRequest?[] StatiFiltroCr = { null, StatoChangeRequest.Chiusa, StatoChangeRequest.DaValidare, StatoChangeRequest.Validata };

    private readonly ITestOutputHelper _output;
    public PerfP661AnomalieCrCestinoTests(ITestOutputHelper output) => _output = output;

    private sealed record RigaPerf(string Percorso, double P50, double Max, double DeltaWsMb, double[] Campioni,
        PerfContatori.Snapshot Contatori, double GcPausaP50Ms);

    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public void MisuraAnomalieCrCestinoScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_P661"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;

        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radiceRepo = TrovaRadiceRepo();
        var baseTemporanea = radiceRepo != null
            ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-p661")
            : Path.Combine(Path.GetTempPath(), "TestLoom_PerfP661");
        var root = Path.Combine(baseTemporanea, $"{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var configPath = Path.Combine(root, "local", "config.json");
        var righe = new List<RigaPerf>();
        var contesto = new List<string>();
        try
        {
            var configService = CostruisciWorkspace(scala, dataRoot, configPath);
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            AttivaOperatoreMaster(state);

            // Pagina Anomalie/CR: costruzione come alla prima visita (il costruttore esegue il ricalcolo in linea).
            righe.Add(Misura("Anomalie/CR (costruzione)", () => new NonConformitaViewModel(state, _ => { }), vm => Scarta(vm)));

            // Pagina gia' aperta: Aggiorna() e filtro CR come in UI reale (ForzaBackground). L'elenco delle NC e' in background
            // dove il codice lo prevede; il resto del ricalcolo (segnalazioni, CR) e' quello che blocca il thread UI.
            var vmNc = new NonConformitaViewModel(state, _ => { });
            vmNc.ForzaBackground = true;
            vmNc.Aggiorna();
            vmNc.AttendiAggiornamentoAsync().GetAwaiter().GetResult();
            contesto.Add($"Anomalie: segnalazioni da classificare nell'elenco={vmNc.Segnalazioni.Count} (dataset {SegnalazioniDaClassificare}); " +
                $"NC totali={vmNc.TotaleRighe}; CR totali={vmNc.TotaleCr}.");

            righe.AddRange(MisuraDueTempi("Anomalie/CR (Aggiorna)",
                () => { vmNc.Aggiorna(); return 0; },
                _ => vmNc.AttendiAggiornamentoAsync().GetAwaiter().GetResult(),
                _ => { }));

            var indiceStatoCr = 0;
            righe.AddRange(MisuraDueTempi("Anomalie/CR (filtro stato CR)",
                () => { vmNc.FiltroStatoCr = StatiFiltroCr[indiceStatoCr++ % StatiFiltroCr.Length]; return 0; },
                _ => vmNc.AttendiAggiornamentoAsync().GetAwaiter().GetResult(),
                _ => { }));

            // Filtro Gamma: attiva/disattiva una gamma a turno. Tocca anche la lettura dei Veicoli per CR (filtro Gamma sulle CR).
            var gammeDisponibili = vmNc.FiltroGamma.Opzioni.Select(o => o.Valore).Take(3).ToList();
            if (gammeDisponibili.Count > 0)
            {
                var indiceGamma = 0;
                righe.AddRange(MisuraDueTempi("Anomalie/CR (filtro gamma)",
                    () =>
                    {
                        var opzione = vmNc.FiltroGamma.Opzioni.Single(o => o.Valore == gammeDisponibili[indiceGamma % gammeDisponibili.Count]);
                        indiceGamma++;
                        opzione.Selezionato = !opzione.Selezionato;
                        return 0;
                    },
                    _ => vmNc.AttendiAggiornamentoAsync().GetAwaiter().GetResult(),
                    _ => { }));
            }
            Scarta(vmNc);

            // Editor di una Change Request esistente, come in UI reale: il costruttore torna subito e i Test candidati arrivano in
            // background ("blocco" = ritorno del costruttore; "fino a dati" = candidati pubblicati).
            var crId = state.ChangeRequests.GetAll().First().Id;
            // "Fino a dati" aspetta sia i Test candidati sia il giro di cataloghi e copertura (AttendiGiroAsync).
            righe.AddRange(MisuraDueTempi("Editor CR (apertura)",
                () => new ChangeRequestEditorViewModel(state, crId, () => { }, _ => { }, forzaBackground: true, gateCaricamentoTest: null),
                vm =>
                {
                    vm.AttendiCaricamentoTestDisponibiliAsync().GetAwaiter().GetResult();
                    vm.AttendiGiroAsync().GetAwaiter().GetResult();
                },
                vm => Scarta(vm)));
            var editorContesto = new ChangeRequestEditorViewModel(state, crId, () => { }, _ => { }, forzaBackground: true, gateCaricamentoTest: null);
            editorContesto.AttendiCaricamentoTestDisponibiliAsync().GetAwaiter().GetResult();
            editorContesto.AttendiGiroAsync().GetAwaiter().GetResult();
            contesto.Add($"Editor CR: Test candidati={editorContesto.TestDisponibili.Count}; TemplateCr={editorContesto.TemplateCr.Count}.");
            Scarta(editorContesto);

            // Cestino di recovery come in UI reale: il costruttore torna subito e l'elenco arriva dopo (lettura e verifica
            // dei payload fuori dal thread UI).
            righe.AddRange(MisuraDueTempi("Cestino (apertura)",
                () => new RecoveryBinViewModel(state, forzaBackground: true),
                vm => vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult(),
                vm => Scarta(vm)));
            var cestino = new RecoveryBinViewModel(state, forzaBackground: true);
            cestino.AttendiAggiornamentoAsync().GetAwaiter().GetResult();
            contesto.Add($"Cestino: record letti={cestino.Totale}.");
            Scarta(cestino);

            // Ricerca globale: una query, e apertura dell'overlay con tre query (come un utente che digita).
            var ricerca = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);
            var indiceRicerca = 0;
            righe.Add(MisuraAzione("Ricerca globale (query)",
                () => ricerca.Query = TerminiRicercaGlobale[indiceRicerca++ % TerminiRicercaGlobale.Length]));
            var indiceApertura = 0;
            righe.Add(MisuraAzione("Ricerca globale (apertura overlay + 3 query)", () =>
            {
                ricerca.Reset();
                ricerca.PreparaApertura(); // segnale di apertura usato da MainViewModel.ApriRicercaGlobale
                for (var k = 0; k < 3; k++) ricerca.Query = TerminiRicercaGlobale[(indiceApertura++) % TerminiRicercaGlobale.Length];
            }));
            contesto.Add("Ricerca globale, risultati per termine: " + string.Join(", ", TerminiRicercaGlobale.Select(t =>
            {
                ricerca.Query = t;
                return ricerca.Risultati.Count.ToString(CultureInfo.InvariantCulture);
            })));
            Scarta(ricerca);
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
        File.WriteAllText(Path.Combine(cartellaPerf, $"p661-anomalie-{etichetta}-{suffisso}.md"), md, Encoding.UTF8);
        File.WriteAllText(Path.Combine(cartellaPerf, $"p661-anomalie-{etichetta}-{suffisso}.tsv"), ComponiTsv(righe), Encoding.UTF8);
        _output.WriteLine($"ARTEFATTI|cartella={cartellaPerf}|etichetta={etichetta}|scala={suffisso}");
    }

    /// <summary>Workspace sintetico: come PerfMediaScalaTests (Test, NC e CR = scala, cataloghi di base), piu' le segnalazioni
    /// da classificare e i record del cestino. Le segnalazioni puntano a condizioni reali dei Test generati.</summary>
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

        // Segnalazioni da classificare e record del cestino: scritti tramite i servizi reali, cosi' il bootstrap finale li legge
        // come farebbe l'app. Il cestino archivia copie di NC esistenti (ArchiveOnly non cancella la sorgente).
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

            var ncDaArchiviare = Directory.EnumerateFiles(paths.NonConformitaDir, "nc_*.json").Take(RecordCestino).ToList();
            foreach (var file in ncDaArchiviare)
                DomainRecoveryService.ArchiveOnly(statoDati.Paths, file, DomainRecoveryCategory.NonConformita, "Master");
            if (ncDaArchiviare.Count < RecordCestino) throw new InvalidOperationException("NC insufficienti per il cestino del banco.");
        }

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

    /// <summary>Operatore Master sigillato per la sessione di misura: il cestino e' visibile solo a un Master (come RecoveryBinPurgaTests).</summary>
    private static void AttivaOperatoreMaster(AppState state)
    {
        const string nome = "Operatore Banco Master";
        state.Master.Operatori.Save(new List<Operatore>
        {
            TestOperatori.Sigilla(new Operatore { Nome = nome, Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") })
        });
        if (!state.ImpostaOperatore(nome, "1234")) throw new InvalidOperationException("Impossibile attivare l'operatore Master del banco.");
    }

    /// <summary>Misura un percorso con un solo tempo (lavoro sincrono). I contatori sono azzerati prima di ogni misura.</summary>
    private RigaPerf Misura<T>(string percorso, Func<T> operazione, Action<T> dopo)
    {
        dopo(operazione());
        var wsBase = MemoriaWorkingSetMb();

        var campioni = new double[CampioniMisurati];
        var pauseGc = new double[CampioniMisurati];
        PerfContatori.Snapshot contatori = default;
        for (var i = 0; i < CampioniMisurati; i++)
        {
            StabilizzaGc();
            PerfContatori.Reset();
            var pausaPrima = GC.GetTotalPauseDuration();
            var sw = Stopwatch.StartNew();
            var risultato = operazione();
            campioni[i] = sw.Elapsed.TotalMilliseconds;
            pauseGc[i] = (GC.GetTotalPauseDuration() - pausaPrima).TotalMilliseconds;
            contatori = PerfContatori.LeggiSnapshot();
            dopo(risultato);
        }
        var deltaWs = MemoriaWorkingSetMb() - wsBase;
        return RigaDaCampioni(percorso, campioni, deltaWs, contatori, pauseGc);
    }

    /// <summary>Misura un percorso in due tempi: "blocco" = fino al ritorno di <paramref name="operazione"/>, "fino a dati" =
    /// dopo <paramref name="attesa"/>. Stesso warm-up e stesse misure di <see cref="Misura{T}"/>.</summary>
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

    private RigaPerf MisuraAzione(string percorso, Action operazione) =>
        Misura<int>(percorso, () => { operazione(); return 0; }, _ => { });

    private RigaPerf RigaDaCampioni(string percorso, double[] campioni, double deltaWs, PerfContatori.Snapshot contatori, double[] pauseGc)
    {
        var ordinati = campioni.OrderBy(v => v).ToArray();
        var riga = new RigaPerf(percorso, Mediana(ordinati), ordinati[^1], deltaWs, campioni, contatori,
            Mediana(pauseGc.OrderBy(v => v).ToArray()));
        _output.WriteLine($"BENCHMARK|percorso={percorso}|p50={F(riga.P50)}|max={F(riga.Max)}" +
            $"|pausaGcP50Ms={F(riga.GcPausaP50Ms)}" +
            $"|deltaWsMb={deltaWs.ToString("F1", CultureInfo.InvariantCulture)}" +
            $"|fingerprintCalcolati={contatori.FingerprintCalcolati}|fingerprintRiusati={contatori.FingerprintRiusati}" +
            $"|masterLoad={contatori.MasterLoad}|phaseDRefresh={contatori.PhaseDRefreshEseguiti}" +
            $"|phaseDRefreshAccorpati={contatori.PhaseDRefreshAccorpati}" +
            $"|phaseDRefreshMs={F(contatori.PhaseDRefreshMs)}|testGetAll={contatori.TestCaseGetAll}" +
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
        sb.AppendLine($"# Misure prestazioni Anomalie/CR, editor CR, cestino e ricerca globale — {etichetta} — {scala} Test — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine($"Dataset: Test={scala}, NC={scala}, CR={scala}, Segnalazioni da classificare={SegnalazioniDaClassificare}, " +
            $"record nel cestino={RecordCestino}, cataloghi={BancoScaleSeedCore.TargetCatalogoDefault} voci. Ambiente: {RuntimeInformation.OSDescription}, " +
            $"{Environment.ProcessorCount} thread logici, {RuntimeInformation.FrameworkDescription}, memoria disponibile al GC " +
            $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0:F1} GB.");
        sb.AppendLine();
        sb.AppendLine("1 warm-up non cronometrato + 5 misure per percorso; p50 = mediana delle 5 misure. " +
            "\"blocco thread UI\" = fino al ritorno del metodo; \"fino a dati\" = dopo l'attesa dei dati applicati. " +
            "ΔWS = working set dopo le 5 misure meno quello dopo il warm-up. Contatori = ultimo giro misurato.");
        foreach (var riga in contesto)
        {
            sb.AppendLine();
            sb.AppendLine(riga);
        }
        sb.AppendLine();
        sb.AppendLine("| Percorso | p50 (ms) | max (ms) | Pausa GC p50 (ms) | ΔWS (MB) | Load master | Refresh Phase D (n / ms) | Refresh accorpati | Test GetAll | campioni (ms) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var riga in righe)
        {
            var c = riga.Contatori;
            sb.AppendLine($"| {riga.Percorso} | {F(riga.P50)} | {F(riga.Max)} | {F(riga.GcPausaP50Ms)} | " +
                $"{riga.DeltaWsMb.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)} | " +
                $"{c.MasterLoad} | {c.PhaseDRefreshEseguiti} / {F(c.PhaseDRefreshMs)} | {c.PhaseDRefreshAccorpati} | {c.TestCaseGetAll} | " +
                $"{string.Join("; ", riga.Campioni.Select(F))} |");
        }
        return sb.ToString();
    }

    private static string ComponiTsv(IReadOnlyList<RigaPerf> righe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("percorso\tp50_ms\tmax_ms\tgc_pausa_p50_ms\tdelta_ws_mb\tmaster_load\tphased_refresh_n\tphased_refresh_ms\tphased_refresh_accorpati\ttest_getall");
        foreach (var riga in righe)
        {
            var c = riga.Contatori;
            sb.AppendLine(string.Join("\t", riga.Percorso, F(riga.P50), F(riga.Max), F(riga.GcPausaP50Ms),
                riga.DeltaWsMb.ToString("F1", CultureInfo.InvariantCulture),
                c.MasterLoad, c.PhaseDRefreshEseguiti, F(c.PhaseDRefreshMs), c.PhaseDRefreshAccorpati, c.TestCaseGetAll));
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
