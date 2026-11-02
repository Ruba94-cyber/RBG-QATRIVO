using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests.Perf;

/// <summary>
/// GENERATORE riproducibile di un banco di prova fittizio a piena scala (Piano 6.4.0, misura prestazioni
/// 2026-09-25): 100.000 Test, 100.000 Non Conformità, ~2.000 Change Request, 100.000 RevisioniUdt (Working
/// Unit) e ~1000 voci per ogni catalogo di sistema (Componente, Veicoli, Gamma, Categorie, Funzionalità, Strumenti,
/// Operatori, Requisiti, CampiSistemaPianificazione, CampiSistemaDettagli, TestTemplate) — sempre FUORI dal
/// repository. Dati SOLO fittizi, generati SOLO tramite API/repository già presenti dell'app:
///   - SeedDemoDataService.GeneraDemoPulita per la base realistica (cataloghi/template/Test iniziali con lo
///     stesso motore usato dalla UI, mai un editing manuale dei file JSON);
///   - BancoScaleSeedCore (nucleo estratto da DemoDataScaleSeederTests) per portare i cataloghi di sistema a
///     ~1000 voci ciascuno e RevisioniUdt a 100k, tramite AppState.Master.* (repository reali);
///   - lo stesso stile di CreaTestRealistici/CreaNonConformitaRealistiche/CreaChangeRequestRealistiche di
///     FullScaleNavigationTests/ScalePerformanceTests per i restanti Test/NC/CR: oggetti del modello
///     serializzati con JsonSerializer + JsonIo.Options sui percorsi di DataPaths (mai stringhe JSON grezze).
///
/// SKIPPATO di default: attivo solo con la variabile d'ambiente <c>TestLoom_PERF_BENCH_GENERATE</c> puntata su
/// una cartella vuota o inesistente (mai su una cartella con dati reali). Idempotente per rilancio a freddo:
/// una cartella non vuota e non generata da questo strumento (nessun marker disposable di SeedDemoDataService)
/// fa fallire subito il test invece di mescolarsi con dati preesistenti.
///
/// ORDINE (motivato dal requisito di coerenza referenziale Test/NC ↔ cataloghi ↔ RevisioniUdt, che i cataloghi
/// esistano con nomi noti PRIMA di scrivere i Test che li referenziano):
///   1. GeneraDemoPulita(cartella)                        — base realistica (piccola).
///   2. AppState #1 + BancoScaleSeedCore                  — cataloghi a 1000 voci, RevisioniUdt a 100k.
///   3. Lettura nomi/chiavi finali dei cataloghi dall'AppState ancora aperto (nessuna doppia formula).
///   4. Scrittura diretta su disco (DataPaths) di Test/NC/CR fino ai volumi target, referenziando quei nomi.
///   5. AppState #2 — bootstrap/indicizzazione finale (come farebbe l'app all'avvio) + stampa volumi letti
///      dai repository reali, a conferma che il banco è consistente e apribile.
///
/// Comando:
///   TestLoom_PERF_BENCH_GENERATE=/home/user/testloom-bench/base \
///   dotnet test src/TestLoom.App.Tests -c Release --filter FullyQualifiedName~BancoPerf100kGeneratoreTests
/// </summary>
public class BancoPerf100kGeneratoreTests
{
    private const int TargetTest = 100_000;
    private const int TargetNc = 100_000;
    private const int TargetCr = 2_000;
    private const int TargetCatalogo = BancoScaleSeedCore.TargetCatalogoDefault; // 1000
    private const int TargetRevisioniUdt = BancoScaleSeedCore.TargetRevisioniUdtDefault; // 100_000

    private readonly ITestOutputHelper _output;
    public BancoPerf100kGeneratoreTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void GeneraBancoDiProvaAPienaScala()
    {
        var cartella = Environment.GetEnvironmentVariable("TestLoom_PERF_BENCH_GENERATE");
        if (string.IsNullOrWhiteSpace(cartella))
        {
            _output.WriteLine("TestLoom_PERF_BENCH_GENERATE non impostata: generatore auto-escluso (non fallito).");
            return;
        }

        cartella = Path.GetFullPath(cartella);
        if (Directory.Exists(cartella) && Directory.EnumerateFileSystemEntries(cartella).Any())
            throw new InvalidOperationException(
                $"'{cartella}' non è vuota: il generatore non sovrascrive una cartella con dati esistenti. " +
                "Cancellarla manualmente prima di rigenerare il banco.");

        BancoScaleSeedCore.VerificaSpazioLibero(cartella);

        // Precauzione di riproducibilità (bug scoperto durante il primo run reale di questo generatore, non
        // della codebase applicativa): PhaseDMasterMaterializer tiene il proprio read-model SQLite locale in
        // %LOCALAPPDATA%/TestLoom/workspaces/<hash della root normalizzata> — un secondo lancio del generatore
        // sulla STESSA cartella target (es. dopo un run interrotto o rilanciato per correggere un bug del
        // generatore stesso) riusa quella cache locale stantia, che punta a UID/contenuti della demo scritta
        // dal run precedente: la conversione Phase D del nuovo contenuto legacy la vede come teste concorrenti
        // non risolte (PhaseDMasterConflictException). Il generatore deve essere riproducibile da zero anche
        // rilanciandolo sullo stesso percorso: purga quella cache prima di scrivere qualunque cosa.
        PurgaCacheWorkspaceLocale(cartella);

        var swTotale = Stopwatch.StartNew();

        // FASE 1 — base realistica: stesso motore usato dall'app per popolare la demo (Genera*), niente JSON
        // scritto a mano. Piccola (decine di Test, cataloghi a poche unità), la scala vera arriva alle fasi 2/4.
        var swDemo = Stopwatch.StartNew();
        SeedDemoDataService.GeneraDemoPulita(cartella);
        swDemo.Stop();
        _output.WriteLine($"FASE|nome=demo_pulita|durata_ms={swDemo.ElapsedMilliseconds}");

        var configPath = Path.Combine(Path.GetTempPath(), "TestLoom_PerfBench", "Generate_" + Guid.NewGuid().ToString("N"), "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        List<string> ecuNomi, veicoliNomi, funzionalitaNomi;
        List<string> requisitoId;
        List<string> revisioniUdtChiavi;
        try
        {
            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = cartella, Lingua = "it", OperatoreAttivo = "" });

            // FASE 2 — cataloghi di sistema a ~1000 voci + RevisioniUdt a 100k, tramite i repository reali
            // (stesso percorso di codice della UI). runBackgroundMaintenance:false: questa AppState serve solo
            // a scrivere i cataloghi, non è quella usata per misurare l'avvio (la misura è nell'harness
            // PerfInProcessPagineTests, su un'AppState costruita come fa davvero l'app).
            var swCataloghi = Stopwatch.StartNew();
            int testAttiviEsistenti, ncAttiveEsistenti;
            using (var state = new AppState(configService, runBackgroundMaintenance: false))
            {
                BancoScaleSeedCore.SeedCataloghiEWorkingUnit(state, cartella, _output, TargetCatalogo, TargetRevisioniUdt);

                // FASE 3 — cattura i nomi/chiavi finali per referenziarli nei Test/NC scritti in FASE 4, senza
                // ricalcolare la formula altrove: letti dagli stessi repository appena scritti sopra.
                ecuNomi = state.Master.Componente.Load().Select(e => e.Nome).ToList();
                veicoliNomi = state.Master.Veicoli.Load().Select(v => v.Nome).ToList();
                funzionalitaNomi = state.Master.Funzionalita.Load().Select(f => f.Nome).ToList();
                requisitoId = state.Master.Requisiti.Load().Select(r => r.Id).ToList();
                revisioniUdtChiavi = state.Master.RevisioniUdt.Load().Select(r => r.WorkingUnitRev).ToList();

                // Conteggio ATTIVO (esclude Eliminato) letto dai repository reali, non dal numero grezzo di
                // file su disco: la demo pulita include realisticamente qualche Test/NC già eliminato (edge
                // case genuino del motore demo, non un difetto di questo generatore), che altrimenti farebbe
                // sottostimare quanti Test/NC generare per raggiungere il volume ATTIVO richiesto dal piano.
                testAttiviEsistenti = state.TestCases.Count();
                ncAttiveEsistenti = state.NonConformita.CountAll;
            }
            swCataloghi.Stop();
            _output.WriteLine($"FASE|nome=cataloghi_e_working_unit|durata_ms={swCataloghi.ElapsedMilliseconds}" +
                $"|componente={ecuNomi.Count}|veicoli={veicoliNomi.Count}|funzionalita={funzionalitaNomi.Count}" +
                $"|requisiti={requisitoId.Count}|revisioniUdt={revisioniUdtChiavi.Count}");

            // FASE 4 — Test/NC/CR fino ai volumi target: scrittura diretta su disco con DataPaths (stesso
            // livello usato da FullScaleNavigationTests/ScalePerformanceTests), oggetti del modello serializzati
            // con JsonSerializer + JsonIo.Options — mai stringhe JSON grezze.
            var paths = new DataPaths(cartella);
            paths.EnsureStructure();
            var testEsistenti = Directory.Exists(paths.TestCasesDir)
                ? Directory.EnumerateFiles(paths.TestCasesDir, "testcase_*.json", SearchOption.AllDirectories).Count()
                : 0;
            var ncEsistenti = Directory.Exists(paths.NonConformitaDir)
                ? Directory.EnumerateFiles(paths.NonConformitaDir, "nc_*.json").Count()
                : 0;
            var crEsistenti = Directory.Exists(paths.ChangeRequestDir)
                ? Directory.EnumerateFiles(paths.ChangeRequestDir, "cr_*.json").Count()
                : 0;

            // Il gap da colmare e' sul conteggio ATTIVO (FASE 3 sopra), non sul numero grezzo di file: la demo
            // pulita può includere Test/NC già eliminati. L'offset di numerazione ID resta invece il conteggio
            // grezzo di file, per non generare mai un Id che coincide testualmente con uno già presente su disco.
            var testDaCreare = Math.Max(0, TargetTest - testAttiviEsistenti);
            var ncDaCreare = Math.Max(0, TargetNc - ncAttiveEsistenti);
            var crDaCreare = Math.Max(0, TargetCr - crEsistenti);

            var swTest = Stopwatch.StartNew();
            var testIds = CreaTestRealistici(paths, testDaCreare, testEsistenti, ecuNomi, veicoliNomi, funzionalitaNomi,
                requisitoId, revisioniUdtChiavi);
            swTest.Stop();
            _output.WriteLine($"FASE|nome=test_case|durata_ms={swTest.ElapsedMilliseconds}|creati={testDaCreare}|totale_target={TargetTest}");

            var swNc = Stopwatch.StartNew();
            CreaNonConformitaRealistiche(paths, ncDaCreare, ncEsistenti, testIds);
            swNc.Stop();
            _output.WriteLine($"FASE|nome=non_conformita|durata_ms={swNc.ElapsedMilliseconds}|create={ncDaCreare}|totale_target={TargetNc}");

            var swCr = Stopwatch.StartNew();
            CreaChangeRequestRealistiche(paths, crDaCreare, crEsistenti, testIds);
            swCr.Stop();
            _output.WriteLine($"FASE|nome=change_request|durata_ms={swCr.ElapsedMilliseconds}|create={crDaCreare}|totale_target={TargetCr}");

            // FASE 5 — bootstrap/indicizzazione finale: stesso passo dell'avvio reale dell'app, a riprova che
            // il banco è consistente/apribile. Volumi finali letti dai repository reali, non ricontati a mano.
            //
            // Purga la stessa cache locale di FASE 1 prima di aprire: l'AppState di FASE 2 (necessaria per
            // seminare i cataloghi) aveva già costruito/persistito una fotografia locale di TestCaseRepository/
            // NonConformitaRepository conoscendo solo i pochi Test della base demo. AppState, quando trova una
            // fotografia locale già valida, salta di proposito la riconciliazione piena all'apertura per restare
            // veloce (RicaricaTutto(saltaRefreshTestCases: true), vedi AppState.cs) e si affida al watcher/
            // riconciliazione incrementale in background — mai partita qui (runBackgroundMaintenance:false).
            // Un RefreshIndex() esplicito e' stato provato ma e' risultato non praticabile a 100k file appena
            // scritti (reconciliation "incrementale" pensata per poche modifiche alla volta, non per una
            // riconciliazione da un indice quasi vuoto a 100k file in un colpo solo: minuti, non secondi). Una
            // AppState davvero "a freddo" (nessuna fotografia locale) segue invece il percorso cold pieno
            // (RicaricaTutto(saltaRefreshTestCases:false) -> RefreshIndex cold su tutti i file), lo stesso già
            // misurato e garantito rapido da FullScaleNavigationTests/ScalePerformanceTests su 100k Test.
            PurgaCacheWorkspaceLocale(cartella);
            var swBootstrap = Stopwatch.StartNew();
            using (var stateFinale = new AppState(configService, runBackgroundMaintenance: false))
            {
                var testCount = stateFinale.TestCases.Count();
                var ncCount = stateFinale.NonConformita.CountAll;
                var crCount = stateFinale.ChangeRequests.GetAll().Count;
                swBootstrap.Stop();
                _output.WriteLine($"FASE|nome=bootstrap_indicizzazione_finale|durata_ms={swBootstrap.ElapsedMilliseconds}");
                _output.WriteLine("VOLUMI_FINALI|" + string.Join('|', new[]
                {
                    $"test={testCount}",
                    $"nc={ncCount}",
                    $"cr={crCount}",
                    $"componente={stateFinale.Master.Componente.Load().Count}",
                    $"veicoli={stateFinale.Master.Veicoli.Load().Count}",
                    $"gamma={stateFinale.Master.Gamma.Load().Count}",
                    $"categorie={stateFinale.Master.Categorie.Load().Count}",
                    $"funzionalita={stateFinale.Master.Funzionalita.Load().Count}",
                    $"strumenti={stateFinale.Master.Strumenti.Load().Count}",
                    $"operatori={stateFinale.Master.Operatori.Load().Count}",
                    $"requisiti={stateFinale.Master.Requisiti.Load().Count}",
                    $"campiPianificazione={stateFinale.Master.CampiSistemaPianificazione.Load().Count}",
                    $"campiDettagli={stateFinale.Master.CampiSistemaDettagli.Load().Count}",
                    $"testTemplate={stateFinale.Master.TestTemplate.Load().Count}",
                    $"revisioniUdt={stateFinale.Master.RevisioniUdt.Load().Count}",
                }));

                Assert.True(testCount >= TargetTest, $"Test attesi >= {TargetTest}, letti {testCount}.");
                Assert.True(ncCount >= TargetNc, $"NC attese >= {TargetNc}, lette {ncCount}.");
                Assert.True(crCount >= TargetCr, $"CR attese >= {TargetCr}, lette {crCount}.");
            }
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true); } catch { /* best-effort */ }
        }

        swTotale.Stop();
        _output.WriteLine($"TOTALE|durata_ms={swTotale.ElapsedMilliseconds}|cartella={cartella}");
    }

    /// <summary>Test realistici: componente/Funzionalità/Veicoli presi dai cataloghi a 1000 voci (indice deterministico
    /// dal seed), operazioni (Condizioni) con esiti vari inclusi "Da eseguire", una parte con valori Working
    /// Unit coerenti con le chiavi già presenti in RevisioniUdt, e qualche Test con Origine ImportStatoUdt.
    /// Restituisce gli Id creati (serve a CreaNonConformitaRealistiche/CreaChangeRequestRealistiche per
    /// collegamenti coerenti).</summary>
    internal static List<string> CreaTestRealistici(DataPaths paths, int count, int offset,
        IReadOnlyList<string> ecuNomi, IReadOnlyList<string> veicoliNomi, IReadOnlyList<string> funzionalitaNomi,
        IReadOnlyList<string> requisitoId, IReadOnlyList<string> revisioniUdtChiavi)
    {
        var ids = new List<string>(count);
        var esiti = new[] { Esito.Conforme, Esito.NonConforme, Esito.ConformeConDeviazioni, Esito.DaEseguire, Esito.NonApplicabile, Esito.DaRivedere };
        var stati = new[] { StatoLavoroTest.Pianificato, StatoLavoroTest.InCorso, StatoLavoroTest.Concluso, StatoLavoroTest.DaRivedere, StatoLavoroTest.InAttesa };

        for (var k = 0; k < count; k++)
        {
            var indice = offset + k;
            var id = $"TC-{indice:D6}";
            ids.Add(id);
            var componente = ecuNomi.Count > 0 ? ecuNomi[indice % ecuNomi.Count] : $"Componente-{indice % 1000:D4}";
            var veicolo = veicoliNomi.Count > 0 ? veicoliNomi[indice % veicoliNomi.Count] : "";
            var funzione = funzionalitaNomi.Count > 0 ? funzionalitaNomi[indice % funzionalitaNomi.Count] : "";
            var requisito = requisitoId.Count > 0 ? requisitoId[indice % requisitoId.Count] : "";
            var stato = stati[indice % stati.Length];

            var tc = new TestCase
            {
                Id = id,
                Uid = Guid.NewGuid(),
                RevisioneDati = 1,
                DataCreazione = new DateTime(2026, 1, 1).AddDays(indice % 300),
                UltimaModifica = new DateTime(2026, 6, 1).AddDays(indice % 200),
                ModificatoDa = $"Operatore-Bench-{indice % 20:D2}",
                CreatoDa = $"Operatore-Bench-{indice % 20:D2}",
                Componente = new EcuSelection(new[] { componente }),
                Veicolo = veicolo,
                Veicoli = string.IsNullOrEmpty(veicolo) ? new List<string>() : new List<string> { veicolo },
                StatoLavoro = stato,
                DescrizioneBreve = $"Diagnostica banco scenario {indice % 500}",
                Descrizione = $"Verifica realistica di scala su {funzione} ({componente}).",
                Funzionalita = string.IsNullOrEmpty(funzione) ? new List<string>() : new List<string> { funzione },
                RequisitoId = string.IsNullOrEmpty(requisito) ? new List<string>() : new List<string> { requisito },
            };

            // Origine ImportStatoUdt su una piccola quota (feedback utente 6.4.0: "Test importati, solo stato
            // finale") — marcatore di Changelog riconosciuto da TestCaseOrigine.EImportStatoUdt.
            var eImportato = indice % 97 == 0;
            if (eImportato)
            {
                tc.Origine = OrigineTest.ImportStatoUdt;
                tc.Changelog.Add(new ChangelogEntry
                {
                    Data = tc.UltimaModifica,
                    Operatore = "Import",
                    Campo = TestCaseOrigine.MarkerChangelogImportStatoUdt,
                    ValoreNuovo = "Stato finale importato",
                });
            }

            // Una revisione con 1-3 operazioni (Condizioni), esiti variati inclusi "Da eseguire".
            var numeroOperazioni = 1 + indice % 3;
            var esecuzione = new Esecuzione
            {
                Iterazione = 1,
                Data = tc.UltimaModifica,
                Operatore = tc.ModificatoDa,
                Matricola = $"Matricola-{indice % 5000:D5}",
            };
            for (var op = 0; op < numeroOperazioni; op++)
            {
                var condizione = new Condizione
                {
                    Componente = new EcuSelection(new[] { componente }),
                    Funzionalita = funzione,
                    Esito = esiti[(indice + op) % esiti.Length],
                };

                // Una quota di operazioni con valori Pacchetto coerenti con una chiave già presente nel
                // catalogo RevisioniUdt (stessa formula di BancoScaleSeedCore.ChiaveRevisioneUdtScale, così i
                // due combaciano sempre senza bisogno di leggere/parsare la chiave a ritroso).
                if (revisioniUdtChiavi.Count > 0 && (indice + op) % 10 == 0)
                {
                    var wuIndice = 1 + (indice + op) % revisioniUdtChiavi.Count;
                    condizione.ValoriStrumenti[RevisioneUdtService.ChiaveCampoNome] = BancoScaleSeedCore.NomeWorkingUnitScale(wuIndice);
                    condizione.ValoriStrumenti[RevisioneUdtService.ChiaveCampoVersione] = BancoScaleSeedCore.VersioneWorkingUnitScale(wuIndice);
                }
                esecuzione.Condizioni.Add(condizione);
            }
            esecuzione.SincronizzaEsito();
            tc.Esecuzioni.Add(esecuzione);

            var dir = paths.TestCaseDir(componente);
            Directory.CreateDirectory(dir);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options);
            File.WriteAllBytes(Path.Combine(dir, $"testcase_{id}.json"), bytes);
        }
        return ids;
    }

    internal static void CreaNonConformitaRealistiche(DataPaths paths, int count, int offset, IReadOnlyList<string> testIds)
    {
        if (count == 0) return;
        Directory.CreateDirectory(paths.NonConformitaDir);
        for (var k = 0; k < count; k++)
        {
            var indice = offset + k;
            var id = $"NC-{indice:D6}";
            var testCaseId = testIds.Count > 0 && indice % 5 == 0 ? testIds[indice % testIds.Count] : "";
            var chiusa = indice % 3 == 0;
            var nc = new NonConformita
            {
                Uid = Guid.NewGuid(),
                RevisioneDati = 1,
                Id = id,
                TestCaseId = testCaseId,
                DataApertura = new DateTime(2026, 1, 1).AddDays(indice % 300),
                DataChiusura = chiusa ? new DateTime(2026, 6, 1).AddDays(indice % 150) : null,
                Owner = $"Operatore-Bench-{indice % 20:D2}",
                StatoWorkflow = chiusa ? StatoAnomalia.Conclusa : StatoAnomalia.Confermata,
                Severita = (SeveritaAnomalia)(1 + indice % 5),
                Descrizione = $"Anomalia riscontrata su scenario banco {indice % 500}",
                Azione = "In lavorazione",
            };
            if (!string.IsNullOrEmpty(testCaseId))
                nc.ProveCollegate.Add(new RiferimentoProva { TestId = testCaseId });
            var bytes = JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options);
            File.WriteAllBytes(paths.NonConformitaFile(id), bytes);
        }
    }

    internal static void CreaChangeRequestRealistiche(DataPaths paths, int count, int offset, IReadOnlyList<string> testIds)
    {
        if (count == 0) return;
        Directory.CreateDirectory(paths.ChangeRequestDir);
        for (var k = 0; k < count; k++)
        {
            var indice = offset + k;
            var id = $"CR-BENCH-{indice:D5}";
            var testCaseId = testIds.Count > 0 ? testIds[indice % testIds.Count] : "";
            var cr = new ChangeRequest
            {
                Uid = Guid.NewGuid(),
                RevisioneDati = 1,
                Id = id,
                Titolo = $"Change Request banco {indice}",
                Descrizione = "Richiesta di modifica generata per il banco di prova a piena scala.",
                Stato = (StatoChangeRequest)(indice % 4),
                DataCreazione = new DateTime(2026, 1, 1).AddDays(indice % 300),
                UltimaModifica = new DateTime(2026, 6, 1).AddDays(indice % 150),
            };
            if (!string.IsNullOrEmpty(testCaseId))
                cr.TestValidazione.Add(new CollegamentoValidazioneCr { TestId = testCaseId });
            var bytes = JsonSerializer.SerializeToUtf8Bytes(cr, JsonIo.Options);
            File.WriteAllBytes(paths.ChangeRequestFile(id), bytes);
        }
    }

    /// <summary>Stessa formula di PhaseDMasterMaterializer.NormalizeRoot/hash della cartella (privata, quindi
    /// replicata qui sola andata — sola lettura del percorso, nessuna dipendenza dal materializer stesso):
    /// cancella SOLO la cache locale di questa specifica cartella target, mai l'intero
    /// %LOCALAPPDATA%/TestLoom/workspaces (che può contenere workspace di altre cartelle non correlate).</summary>
    internal static void PurgaCacheWorkspaceLocale(string cartella)
    {
        // ClearAllPools PRIMA di cancellare la cartella: Microsoft.Data.Sqlite tiene le connessioni in un pool
        // di processo tenuto vivo per riuso (Pooling=true nelle connection string di PhaseDMasterMaterializer/
        // LocalTestIndexStore), indicizzato dalla connection string — quindi dallo stesso percorso file, che
        // qui non cambia da un run all'altro. Su Linux un file cancellato mentre una connessione pooled lo tiene
        // ancora aperto resta leggibile tramite quella connessione (inode "orfano"): senza questa chiamata la
        // AppState successiva può riprendere in silenzio una connessione pooled sul DB VECCHIO già cancellato
        // invece di aprire quello nuovo allo stesso percorso, restituendo conteggi della generazione precedente
        // (bug riscontrato nel secondo run reale di questo generatore sulla stessa cartella target fissa).
        SqliteConnection.ClearAllPools();
        // Percorso del prodotto (WorkspaceLocalPaths), che rispetta LocalApplicationDataOverrideForTests impostato da
        // TestLocalAppDataFixture. Environment.GetFolderPath puntava al %LOCALAPPDATA% reale: in test la cache vera non
        // veniva mai cancellata e il bootstrap finale riusava la fotografia della base demo (169 Test invece di N).
        var workspaceDir = WorkspaceLocalPaths.GetWorkspaceDirectory(cartella);
        if (Directory.Exists(workspaceDir)) Directory.Delete(workspaceDir, recursive: true);
    }
}
