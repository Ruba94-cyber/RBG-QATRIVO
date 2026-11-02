using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Tests.Perf;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>
/// Banco del ciclo prestazioni 6.6.1 (wt-p661-opt-ricerca): ricerca globale per dominio (Test, NC, CR, segnalazioni,
/// master) e primo ingresso della pagina Report (costruttore, blocco del thread UI).
/// Volumi come il banco Anomalie: Test, NC e CR = scala; segnalazioni da classificare = 2.000.
///
/// Attivo solo con TestLoom_PERF_OPT_RICERCA=1 (categoria Benchmark, fuori dal giro standard). TestLoom_PERF_MEDIA_SCALE limita la
/// scala, TestLoom_PERF_MEDIA_LABEL etichetta l'output. Per ogni percorso: 1 warm-up non cronometrato e 5 misure, GC stabilizzato
/// prima di ogni misura; p50 = mediana delle 5 misure.
///
/// La scomposizione per dominio replica le letture e i filtri di GlobalSearchViewModel e misura ciascuna sorgente da sola;
/// la riga "VM" misura la ricerca reale con debounce 0. Parita': per ogni termine e per ogni operatore viene riportata una
/// firma dei risultati (elementi e ordine): firme uguali = stessi risultati.
///
/// Output: .ai/artifacts/perf/opt-ricerca-{etichetta}-{N}k.md e .tsv nella radice del repository (non versionata).
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfP661OptRicercaTests
{
    private const int CampioniMisurati = 5;
    private const int SegnalazioniDaClassificare = 2_000;
    private const string NomeMaster = "Operatore Banco Master";
    private const string NomeStandard = "Operatore Banco Standard";
    private const string PinBanco = "1234";

    // Stessi termini del banco Anomalie: ogni misura (warm-up incluso) cambia davvero la query.
    private static readonly string[] TerminiRicercaGlobale =
    {
        "Operatore-Bench-07", "scenario banco 17", "REQ-SCALE-0",
        "Componente-Scale-00", "Veicolo-Scale-0", "banco 42",
    };

    private readonly ITestOutputHelper _output;
    public PerfP661OptRicercaTests(ITestOutputHelper output) => _output = output;

    private sealed record RigaPerf(string Percorso, double P50, double Max, double DeltaWsMb, double[] Campioni,
        PerfContatori.Snapshot Contatori, double GcPausaP50Ms);

    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public void MisuraRicercaGlobaleEPrimoIngressoReportScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_OPT_RICERCA"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;

        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radiceRepo = TrovaRadiceRepo();
        var baseTemporanea = radiceRepo != null
            ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-opt-ricerca")
            : Path.Combine(Path.GetTempPath(), "TestLoom_PerfOptRicerca");
        var root = Path.Combine(baseTemporanea, $"{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var configPath = Path.Combine(root, "local", "config.json");
        var righe = new List<RigaPerf>();
        var contesto = new List<string>();
        try
        {
            // Seme riusabile: il workspace si genera una volta (TestLoom_PERF_CREA_SEME=1, solo generazione, nessuna misura) e
            // ogni ripetizione ne riusa una copia. Senza seme il workspace si genera come prima.
            var cartellaSeme = radiceRepo != null
                ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-opt-ricerca-seme", scala.ToString(CultureInfo.InvariantCulture))
                : null;
            var creaSeme = string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_CREA_SEME"), "1", StringComparison.Ordinal);
            var usaSeme = !creaSeme && cartellaSeme != null && File.Exists(Path.Combine(cartellaSeme, "completo.txt"));
            var configService = usaSeme
                ? PreparaDaSeme(scala, cartellaSeme!, dataRoot, configPath)
                : CostruisciWorkspace(scala, dataRoot, configPath);
            if (creaSeme)
            {
                if (cartellaSeme == null) throw new InvalidOperationException("Radice del repository non trovata: il seme non si puo' salvare.");
                SalvaSeme(dataRoot, cartellaSeme);
                _output.WriteLine($"SEME|scala={scala}|cartella={cartellaSeme}");
                return;
            }
            _output.WriteLine($"WORKSPACE|scala={scala}|sorgente={(usaSeme ? "seme" : "generato")}");
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            var gamma = AttivaOperatoriBanco(state);

            // Ricerca reale (GlobalSearchViewModel, debounce 0). A/B nello stesso processo e sullo stesso workspace: "PRIMA" usa la
            // lettura storica (copia di NC e Segnalazioni, verifica per Test per ogni Segnalazione), "DOPO" il codice attuale.
            // Le firme dei risultati di PRIMA e DOPO devono coincidere (stessi elementi, stesso ordine).
            var ricerca = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);
            ricerca.PercorsoStoricoPerConfronto = true;
            contesto.Add(FirmeRicerca("Master, prima", ricerca));
            ricerca.PercorsoStoricoPerConfronto = false;
            contesto.Add(FirmeRicerca("Master, dopo", ricerca));
            var indiceQuery = 0;
            righe.Add(MisuraAzione("Ricerca globale (VM) - query, Master, PRIMA", () =>
            {
                ricerca.PercorsoStoricoPerConfronto = true;
                ricerca.Query = TerminiRicercaGlobale[indiceQuery++ % TerminiRicercaGlobale.Length];
            }));
            righe.Add(MisuraAzione("Ricerca globale (VM) - query, Master, DOPO", () =>
            {
                ricerca.PercorsoStoricoPerConfronto = false;
                ricerca.Query = TerminiRicercaGlobale[indiceQuery++ % TerminiRicercaGlobale.Length];
            }));
            var indiceApertura = 0;
            // Apertura dell'overlay come MainViewModel.ApriRicercaGlobale: reset, segnale di apertura e tre query.
            void Apertura(bool storico)
            {
                ricerca.PercorsoStoricoPerConfronto = storico;
                ricerca.Reset();
                ricerca.PreparaApertura();
                for (var k = 0; k < 3; k++) ricerca.Query = TerminiRicercaGlobale[indiceApertura++ % TerminiRicercaGlobale.Length];
            }
            righe.Add(MisuraAzione("Ricerca globale (VM) - apertura overlay + 3 query, Master, PRIMA", () => Apertura(storico: true)));
            righe.Add(MisuraAzione("Ricerca globale (VM) - apertura overlay + 3 query, Master, DOPO", () => Apertura(storico: false)));

            // Scomposizione per dominio, operatore Master: una lettura per sorgente, come nel codice di produzione.
            var indiceTermine = 0;
            string ProssimoTermine() => TerminiRicercaGlobale[indiceTermine++ % TerminiRicercaGlobale.Length];
            var indiceMaster = new MasterDataLocalIndexCoordinator(state.Paths);
            // Come CalcolaRisultati, ogni scomposizione gira dentro una sola lettura logica dei master (MasterLetturaScope).
            righe.Add(MisuraAzione("Dominio Test (FTS, Master)", () => InLetturaMaster(() => DominioTest(state, ProssimoTermine(), standard: false))));
            righe.Add(MisuraAzione("Dominio NC (GetAll, clona ogni NC)", () => InLetturaMaster(() => DominioNc(state.NonConformita.GetAll(), ProssimoTermine()))));
            righe.Add(MisuraAzione("Dominio NC (PeekAllReadOnly, senza clone)", () => InLetturaMaster(() => DominioNc(state.NonConformita.PeekAllReadOnly(), ProssimoTermine()))));
            righe.Add(MisuraAzione("Dominio CR (GetAll, clona ogni CR)", () => InLetturaMaster(() => DominioCr(state.ChangeRequests.GetAll(), ProssimoTermine()))));
            righe.Add(MisuraAzione("Dominio CR (PeekAllReadOnly + confronto, senza proiezione)", () => InLetturaMaster(() => DominioCr(state.ChangeRequests.PeekAllReadOnly(), ProssimoTermine()))));
            righe.Add(MisuraAzione("Dominio CR (proiezione CercaTesto, primi 8)", () => InLetturaMaster(() => state.ChangeRequests.CercaTesto(ProssimoTermine(), 8).Count)));
            righe.Add(MisuraAzione("Dominio segnalazioni (GetAll + lookup, Master)", () => InLetturaMaster(() => DominioSegnalazioni(state, ProssimoTermine()))));
            righe.Add(MisuraAzione("Master indice FTS (componente, Requisiti, Funzionalita)", () => InLetturaMaster(() => DominioMasterIndice(indiceMaster, ProssimoTermine()))));
            righe.Add(MisuraAzione("Master Load (Veicoli + Strumenti, sempre)", () => InLetturaMaster(() => DominioMasterFallback(state, ProssimoTermine()))));

            // Operatore Standard con una gamma assegnata: il perimetro si applica a Test e segnalazioni.
            state.ImpostaOperatore(NomeStandard, PinBanco);
            contesto.Add($"Perimetro Standard (gamma '{gamma}'): Test visibili = " +
                $"{state.FiltraTestCaseVisibiliPerOperatore(state.TestCases.GetAll()).Count()} su {state.TestCases.Count()}.");
            // Le righe PRIMA dell'operatore Standard costano circa 40 s per query (una lettura del catalogo Veicoli per ogni
            // segnalazione aperta, come nel codice precedente). Con TestLoom_PERF_OPT_SALTA_PRIMA_STANDARD=1 non si misurano: la
            // ripetizione le salta e il dato resta quello della ripetizione con tutte le righe.
            var saltaPrimaStandard = string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_OPT_SALTA_PRIMA_STANDARD"), "1", StringComparison.Ordinal);
            if (saltaPrimaStandard)
                contesto.Add("Righe PRIMA dell'operatore Standard non misurate in questa ripetizione (vedi la ripetizione con tutte le righe).");
            ricerca.PercorsoStoricoPerConfronto = false;
            contesto.Add(FirmeRicerca("Standard, dopo", ricerca));
            if (!saltaPrimaStandard)
            {
                ricerca.PercorsoStoricoPerConfronto = true;
                contesto.Add(FirmeRicerca("Standard, prima", ricerca));
                ricerca.PercorsoStoricoPerConfronto = false;
                righe.Add(MisuraAzione("Ricerca globale (VM) - query, Standard, PRIMA", () =>
                {
                    ricerca.PercorsoStoricoPerConfronto = true;
                    ricerca.Query = TerminiRicercaGlobale[indiceQuery++ % TerminiRicercaGlobale.Length];
                }));
            }
            righe.Add(MisuraAzione("Ricerca globale (VM) - query, Standard, DOPO", () =>
            {
                ricerca.PercorsoStoricoPerConfronto = false;
                ricerca.Query = TerminiRicercaGlobale[indiceQuery++ % TerminiRicercaGlobale.Length];
            }));
            if (!saltaPrimaStandard)
                righe.Add(MisuraAzione("Ricerca globale (VM) - apertura overlay + 3 query, Standard, PRIMA", () => Apertura(storico: true)));
            righe.Add(MisuraAzione("Ricerca globale (VM) - apertura overlay + 3 query, Standard, DOPO", () => Apertura(storico: false)));
            righe.Add(MisuraAzione("Dominio Test (FTS, Standard)", () => InLetturaMaster(() => DominioTest(state, ProssimoTermine(), standard: true))));
            if (!saltaPrimaStandard)
                righe.Add(MisuraAzione("Dominio segnalazioni (GetAll + lookup, Standard)", () => InLetturaMaster(() => DominioSegnalazioni(state, ProssimoTermine()))));
            Scarta(ricerca);

            // Primo ingresso della pagina Report (operatore Master), PRIMA = costruttore in linea (aggiornaInBackground: false, come
            // prima della modifica), DOPO = costruttore con il primo giro in background (come MainViewModel). Il blocco e' il
            // ritorno del costruttore; "fino a dati" dopo l'attesa dei dati applicati.
            state.ImpostaOperatore(NomeMaster, PinBanco);
            righe.AddRange(MisuraDueTempi("Report, primo ingresso PRIMA (costruttore in linea)",
                () => new ReportViewModel(state, aggiornaInBackground: false),
                vm => vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult(),
                vm => Scarta(vm)));
            righe.AddRange(MisuraDueTempi("Report, primo ingresso DOPO (costruttore in background)",
                () => new ReportViewModel(state, aggiornaInBackground: true),
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
        File.WriteAllText(Path.Combine(cartellaPerf, $"opt-ricerca-{etichetta}-{suffisso}.md"), md, Encoding.UTF8);
        File.WriteAllText(Path.Combine(cartellaPerf, $"opt-ricerca-{etichetta}-{suffisso}.tsv"), ComponiTsv(righe), Encoding.UTF8);
        _output.WriteLine($"ARTEFATTI|cartella={cartellaPerf}|etichetta={etichetta}|scala={suffisso}");
    }

    // --- Letture per dominio: stessa forma del codice di GlobalSearchViewModel (Match, Take, filtri di ruolo). ---

    private static int DominioTest(AppState state, string q, bool standard)
    {
        var hit = LocalTestSearchService.Search(state.TestCases.LocalIndexDatabasePath, q, 32) ?? Array.Empty<LocalTestSearchHit>();
        IEnumerable<LocalTestSearchHit> filtrati = hit;
        if (standard)
        {
            var gamme = state.OperatoreCorrente!.GammeAssegnate.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ammessi = state.Master.Veicoli.Load().Where(v => gamme.Contains(v.GammaNome)).Select(v => v.Nome)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtrati = filtrati.Where(h => h.Veicoli.Any(ammessi.Contains));
        }
        return filtrati.Take(12).Count();
    }

    private static int DominioNc(IEnumerable<NonConformita> nc, string q) =>
        nc.Where(n => Match(q, n.Id, n.TestCaseId, n.Descrizione, n.Owner)).Take(8).Count();

    private static int DominioCr(IEnumerable<ChangeRequest> cr, string q) =>
        cr.Where(c => Match(q, c.Id, c.Titolo, c.Descrizione, c.Responsabile)
            || c.VeicoliImpattati.Any(v => Match(q, v)) || c.EcuImpattate.Any(e => Match(q, e))).Take(8).Count();

    private static int DominioSegnalazioni(AppState state, string q)
    {
        var aggiunti = 0;
        var aperti = state.Segnalazioni.GetAll().Where(s => s.Stato is StatoSegnalazione.DaClassificare or StatoSegnalazione.Esclusa);
        foreach (var segnalazione in aperti)
        {
            var test = segnalazione.TestUid is { } testUid
                ? state.TestCases.GetByUid(testUid)
                : state.TestCases.GetById(segnalazione.TestId);
            if (test == null || test.Eliminato || !state.TestCaseVisibilePerOperatore(test)) continue;

            var esecuzione = test.Esecuzioni.FirstOrDefault(e => e.Iterazione == segnalazione.EsecuzioneIterazione);
            var condizione = esecuzione?.CondizioniEffettive.FirstOrDefault(c => c.Uid == segnalazione.CondizioneUid);
            var componente = condizione?.Componente.ToString() ?? "";
            var matches = Match(q, test.Id, segnalazione.TestId, segnalazione.Uid.ToString("D"),
                segnalazione.Uid.ToString("N"), segnalazione.Stato.ToString(),
                segnalazione.MotivazioneEsclusione, condizione?.Funzionalita,
                condizione?.DescrizioneProblema, condizione?.Nota, componente);
            if (!matches) continue;
            if (++aggiunti >= 8) break;
        }
        return aggiunti;
    }

    private static int DominioMasterIndice(MasterDataLocalIndexCoordinator indice, string q) =>
        indice.Store.Query<Componente>(MasterDataLocalIndexCoordinator.EcuCatalog, q, false, 0, 5).Count
        + indice.Store.Query<Requisito>(MasterDataLocalIndexCoordinator.RequisitiCatalog, q, false, 0, 5).Count
        + indice.Store.Query<Funzionalita>(MasterDataLocalIndexCoordinator.FunzionalitaCatalog, q, false, 0, 5).Count;

    private static int DominioMasterFallback(AppState state, string q) =>
        state.Master.Veicoli.Load().Where(x => !x.Archiviata && Match(q, x.Nome, x.GammaNome)).Take(5).Count()
        + state.Master.Strumenti.Load().Where(x => !x.Archiviata && Match(q, x.Nome)).Take(5).Count();

    private static bool Match(string q, params string?[] valori) =>
        valori.Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Stessa lettura logica dei master che CalcolaRisultati apre attorno a ogni ricerca (sincrona, stesso thread).</summary>
    private static int InLetturaMaster(Func<int> domino)
    {
        using var letturaMaster = MasterLetturaScope.Apri();
        return domino();
    }

    // --- Workspace e operatori ---

    /// <summary>Workspace sintetico come il banco Anomalie (Test, NC e CR = scala; segnalazioni da classificare = 2.000).
    /// Senza cestino: la ricerca globale non lo interroga.</summary>
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

    /// <summary>Salva il seme: copia dei dati condivisi appena generati e il marcatore di completamento.</summary>
    private static void SalvaSeme(string dataRoot, string cartellaSeme)
    {
        if (Directory.Exists(cartellaSeme)) Directory.Delete(cartellaSeme, recursive: true);
        CopiaCartella(dataRoot, Path.Combine(cartellaSeme, "data"));
        File.WriteAllText(Path.Combine(cartellaSeme, "completo.txt"), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
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

    /// <summary>Operatore Master e operatore Standard con una gamma (la prima dei Veicoli). Ritorna la gamma assegnata.
    /// La sessione parte dal Master.</summary>
    private static string AttivaOperatoriBanco(AppState state)
    {
        var gamma = state.Master.Veicoli.Load()
            .Where(v => !v.Archiviata && !string.IsNullOrWhiteSpace(v.GammaNome))
            .Select(v => v.GammaNome).First();
        state.Master.Operatori.Save(new List<Operatore>
        {
            TestOperatori.Sigilla(new Operatore { Nome = NomeMaster, Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash(PinBanco) }),
            TestOperatori.Sigilla(new Operatore
            {
                Nome = NomeStandard, Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash(PinBanco),
                GammeAssegnate = new List<string> { gamma },
            }),
        });
        if (!state.ImpostaOperatore(NomeMaster, PinBanco)) throw new InvalidOperationException("Impossibile attivare l'operatore Master del banco.");
        return gamma;
    }

    /// <summary>Firma dei risultati per ciascun termine: elementi e ordine. Stesse firme prima e dopo = stessi risultati.</summary>
    private static string FirmeRicerca(string ambito, GlobalSearchViewModel ricerca)
    {
        var parti = new List<string>();
        foreach (var termine in TerminiRicercaGlobale)
        {
            ricerca.Query = termine;
            var testo = string.Join("\n", ricerca.Risultati.Select(r =>
                string.Join("|", r.Icona, r.Tipo, r.Titolo, r.Sottotitolo, r.Destinazione, r.ElementoId, r.ElementoUid)));
            var firma = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(testo)))[..16];
            parti.Add($"'{termine}' {ricerca.Risultati.Count} risultati, firma {firma}");
        }
        return $"Ricerca globale, operatore {ambito} - " + string.Join("; ", parti);
    }

    // --- Misura (stesso schema del banco Anomalie) ---

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
        sb.AppendLine($"# Ricerca globale per dominio e primo ingresso Report — {etichetta} — {scala} Test — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine($"Dataset: Test={scala}, NC={scala}, CR={scala}, Segnalazioni da classificare={SegnalazioniDaClassificare}, " +
            $"cataloghi={BancoScaleSeedCore.TargetCatalogoDefault} voci. Ambiente: {RuntimeInformation.OSDescription}, " +
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
