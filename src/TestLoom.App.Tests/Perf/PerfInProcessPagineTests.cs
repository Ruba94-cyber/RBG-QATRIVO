using System.Diagnostics;
using System.Globalization;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests.Perf;

/// <summary>
/// HARNESS di misura in-process delle pagine (Piano 6.4.0, banco a piena scala 2026-09-25): stesso principio
/// del guardrail FlaUI <see cref="TestLoom.App.UITests.Tests.PerfNavigazioneTests"/> ("1 giro freddo + 3 giri
/// caldo, mediana del caldo, stessa sequenza di navigazione") ma senza processo/UI reale — costruisce/aggiorna
/// gli stessi ViewModel che l'app costruisce davvero, sul percorso di codice reale (MainViewModel, quando
/// costruibile headless — vedi DispatcherTestPump.cs e i test esistenti che lo fanno, es.
/// MainViewModelPageCachingTests: senza un'applicazione Avalonia nel processo, MainViewModel.MostraPaginaDopoFrame gira
/// SINCRONA — nessun await reale — quindi il tempo intorno a "SelectedNavItem = ..." è già la stessa finestra
/// che l'app misura tra il click e la sparizione di Main_CaricamentoPagina_Border).
///
/// SKIPPATO di default: attivo solo con la variabile d'ambiente <c>TestLoom_PERF_INPROCESS_DATASET</c> puntata su
/// un banco di prova (es. quello generato da BancoPerf100kGeneratoreTests). La cartella indicata NON viene MAI
/// misurata direttamente: viene copiata in una cartella di lavoro temporanea (il read-model SQLite Phase D
/// vive in %LOCALAPPDATA%/TestLoom/workspaces/&lt;hash della root&gt;, quindi una root diversa = read-model
/// nuovo/a freddo, condizione corretta per il giro 1). <c>TestLoom_PERF_INPROCESS_OUTPUT</c> (opzionale) sceglie
/// dove salvare la tabella markdown; <c>TestLoom_PERF_INPROCESS_LABEL</c> (opzionale) etichetta la riga di intestazione.
///
/// AppState è costruita con gli stessi parametri di default usati da tutti i benchmark esistenti di questo
/// repository (ScalePerformanceTests/FullScaleNavigationTests/DemoDataScaleSeederTests):
/// runBackgroundMaintenance:false. A differenza dell'avvio reale (App.axaml.cs non lo passa, quindi vale il
/// default true) qui viene disattivato di proposito: un giro di manutenzione periodica in background che
/// scatta a metà di una misura introdurrebbe un rumore non riproducibile sui tempi di navigazione, che è
/// esattamente ciò che questo harness deve misurare con precisione — bootstrap/indicizzazione (l'unica parte
/// che "influenza lo stato a caldo" richiesta dal piano) restano comunque quelli reali, invariati.
///
/// Comando:
///   TestLoom_PERF_INPROCESS_DATASET=/home/user/testloom-bench/base \
///   TestLoom_PERF_INPROCESS_OUTPUT=/home/user/TestLoom/.ai/artifacts/perf-inprocess-baseline.md \
///   dotnet test src/TestLoom.App.Tests -c Release --filter FullyQualifiedName~PerfInProcessPagineTests
/// </summary>
public sealed class PerfInProcessPagineTests
{
    /// <summary>Riusa l'operatore Master già presente nella base realistica generata da
    /// SeedDemoDataService.GeneraDemoPulita invece di aggiungerne uno con Load()+Save(): la copia di lavoro ha
    /// un percorso diverso dal banco, e un salvataggio master dallo stesso PC su una cartella copiata fallisce
    /// per un difetto preesistente del contatore locale DeviceSequence Phase D (indicizzato per percorso,
    /// riparte da 1 mentre il checkpoint del device copre già le sequenze precedenti: vedi il ramo
    /// fix/6.4.0-phased-sequenza-dispositivo). L'harness misura solo letture/navigazione: nessuna scrittura.</summary>
    private const string Operatore = "Operatore Prova A";
    private const int NumeroGiriCaldo = 3;
    private const int NumeroGiri = 1 + NumeroGiriCaldo;

    /// <summary>Stessa rotta principale di PerfNavigazioneTests (Home è già visitata durante la costruzione di
    /// MainViewModel, misurata a parte come riga "Home (costruzione MainViewModel)").</summary>
    private static readonly (string NavId, string Etichetta)[] Rotta =
    {
        ("test", "Test"),
        ("anomalie", "Anomalie/CR"),
        ("wustatus", "WU Status"),
        ("report", "Report"),
        ("audit", "Audit"),
        ("impostazioni", "Master"),
        ("home", "Home"),
    };

    /// <summary>Stesse sotto-pagine catalogo di PerfNavigazioneTests.SottoImpostazioni.</summary>
    private static readonly (string SubId, string Etichetta)[] SottoImpostazioni =
    {
        ("archivio", "Master.Archivio"),
        ("operatori", "Master.Operatori"),
        ("requisiti", "Master.Requisiti"),
        ("funzionalita", "Master.Funzionalita"),
        ("componente", "Master.Componente"),
        ("veicoli", "Master.Veicoli"),
        ("strumenti", "Master.Strumenti"),
        ("tipiditest", "Master.TipiDiTest"),
    };

    private readonly ITestOutputHelper _output;
    public PerfInProcessPagineTests(ITestOutputHelper output) => _output = output;

    private sealed record Campione(double Ms, PerfContatori.Snapshot ContatoriDelta, int Gc2Delta);
    private sealed record RigaPagina(string Pagina, double FreddoMs, List<Campione> Caldi);

    [Fact]
    public void MisuraNavigazioneInProcess()
    {
        var datasetDir = Environment.GetEnvironmentVariable("TestLoom_PERF_INPROCESS_DATASET");
        if (string.IsNullOrWhiteSpace(datasetDir) || !Directory.Exists(datasetDir))
        {
            _output.WriteLine("TestLoom_PERF_INPROCESS_DATASET non impostata o inesistente: harness auto-escluso (non fallito).");
            return;
        }

        var label = Environment.GetEnvironmentVariable("TestLoom_PERF_INPROCESS_LABEL") ?? "run";
        var outputPath = Environment.GetEnvironmentVariable("TestLoom_PERF_INPROCESS_OUTPUT");

        var workRoot = Path.Combine(Path.GetTempPath(), "TestLoom_PerfInProcess_" + Guid.NewGuid().ToString("N"));
        var workDir = Path.Combine(workRoot, "data");
        var configPath = Path.Combine(workRoot, "local", "config.json");
        try
        {
            var swCopia = Stopwatch.StartNew();
            CopiaCartella(datasetDir, workDir);
            swCopia.Stop();
            _output.WriteLine($"FASE|nome=copia_banco|durata_ms={swCopia.ElapsedMilliseconds}|da={datasetDir}|a={workDir}");

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = workDir, Lingua = "it", OperatoreAttivo = Operatore });

            var righe = new List<RigaPagina>();
            void Misura(string pagina, int giro, Action azione)
            {
                PerfContatori.Reset();
                var gc2Prima = GC.CollectionCount(2);
                var sw = Stopwatch.StartNew();
                azione();
                var ms = sw.Elapsed.TotalMilliseconds;
                var contatori = PerfContatori.LeggiSnapshot();
                var gc2Delta = GC.CollectionCount(2) - gc2Prima;
                _output.WriteLine($"BENCHMARK|giro={giro}|pagina={pagina}|ms={ms:F1}|masterLoad={contatori.MasterLoad}" +
                    $"|phaseDRefreshEseguiti={contatori.PhaseDRefreshEseguiti}|phaseDRefreshAccorpati={contatori.PhaseDRefreshAccorpati}" +
                    $"|phaseDRefreshMs={contatori.PhaseDRefreshMs:F1}|loadCatalog={contatori.LoadCatalog}" +
                    $"|testCaseGetAll={contatori.TestCaseGetAll}|gc2={gc2Delta}");

                var esistente = righe.FirstOrDefault(r => r.Pagina == pagina);
                if (giro == 1)
                {
                    if (esistente == null) righe.Add(new RigaPagina(pagina, ms, new List<Campione>()));
                    else throw new InvalidOperationException($"Pagina '{pagina}' misurata due volte al giro freddo.");
                }
                else
                {
                    esistente = esistente ?? throw new InvalidOperationException($"Pagina '{pagina}' misurata a caldo prima del giro freddo.");
                    esistente.Caldi.Add(new Campione(ms, contatori, gc2Delta));
                }
            }

            // Bootstrap: stesso passo dell'avvio reale (App.axaml.cs: new AppState(configService, ...)).
            AppState? state = null;
            var swBootstrap = Stopwatch.StartNew();
            state = new AppState(configService, runBackgroundMaintenance: false);
            swBootstrap.Stop();
            _output.WriteLine($"FASE|nome=bootstrap|durata_ms={swBootstrap.ElapsedMilliseconds}" +
                $"|test={state.TestCases.Count()}|nc={state.NonConformita.CountAll}");

            try
            {
                MainViewModel? main = null;
                for (var giro = 1; giro <= NumeroGiri; giro++)
                {
                    if (giro == 1)
                    {
                        // Home è già navigata dentro il costruttore di MainViewModel (SelectedNavItem =
                        // NavItems[0]): la prima apertura di Home E il costruttore di MainViewModel sono la
                        // STESSA finestra di lavoro sincrono, misurata qui come un'unica riga.
                        Misura("Home (+costruzione MainViewModel)", giro, () => { main = new MainViewModel(state); });
                    }
                    else
                    {
                        Misura("Home (+costruzione MainViewModel)", giro, () =>
                        {
                            main!.SelectedNavItem = main.NavItems.First(n => n.Id == "home");
                        });
                    }

                    foreach (var (navId, etichetta) in Rotta)
                    {
                        Misura(etichetta, giro, () =>
                        {
                            main!.SelectedNavItem = main.NavItems.First(n => n.Id == navId);
                        });

                        if (navId == "impostazioni")
                        {
                            var impostazioni = (ImpostazioniViewModel)main!.CurrentPage!;
                            foreach (var (subId, subEtichetta) in SottoImpostazioni)
                            {
                                Misura(subEtichetta, giro, () =>
                                {
                                    impostazioni.SottoSezioneSelezionata = impostazioni.SottoSezioni.First(s => s.Id == subId);
                                });
                            }
                        }
                    }

                    // Apertura di un Test esistente in TestCaseEditorViewModel: stesso costruttore usato da
                    // MainViewModel.NavigaATestCaseCore, chiamato direttamente perché quel metodo è privato
                    // (stesso pattern già usato da ScalePerformanceTests/FullScaleNavigationTests).
                    var primoTestId = state.TestCases.GetAll().First().Id;
                    Misura("Apertura Test (editor)", giro, () =>
                    {
                        _ = new TestCaseEditorViewModel(state, primoTestId, () => { }, _ => { });
                    });
                }
            }
            finally
            {
                state?.Dispose();
            }

            var markdown = ComponiMarkdown(label, righe);
            _output.WriteLine(markdown);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                File.WriteAllText(outputPath, markdown, Encoding.UTF8);
                _output.WriteLine($"Tabella scritta su {outputPath}");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string ComponiMarkdown(string label, List<RigaPagina> righe)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Baseline prestazioni in-process — {label} — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("| Pagina | Freddo (ms) | Caldo mediana (ms) | Campioni caldo | MasterLoad (caldo) | PhaseDRefresh eseguiti/accorpati (caldo) | LoadCatalog (caldo) | TestCase.GetAll (caldo) | GC gen2 (caldo) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var riga in righe)
        {
            var mediana = Mediana(riga.Caldi.Select(c => c.Ms).ToList());
            // Contatori del giro caldo rappresentativo = ultimo giro caldo (più stabile: le cache/i read-model
            // locali si sono già assestati dopo il primo giro caldo).
            var ultimo = riga.Caldi.LastOrDefault();
            sb.AppendLine($"| {riga.Pagina} | {riga.FreddoMs:F1} | {mediana:F1} | {riga.Caldi.Count} | " +
                $"{ultimo?.ContatoriDelta.MasterLoad ?? 0} | " +
                $"{ultimo?.ContatoriDelta.PhaseDRefreshEseguiti ?? 0}/{ultimo?.ContatoriDelta.PhaseDRefreshAccorpati ?? 0} | " +
                $"{ultimo?.ContatoriDelta.LoadCatalog ?? 0} | {ultimo?.ContatoriDelta.TestCaseGetAll ?? 0} | {ultimo?.Gc2Delta ?? 0} |");
        }
        return sb.ToString();
    }

    private static double Mediana(List<double> valori)
    {
        if (valori.Count == 0) return 0;
        var ordinati = valori.OrderBy(v => v).ToList();
        var n = ordinati.Count;
        return n % 2 == 1 ? ordinati[n / 2] : (ordinati[n / 2 - 1] + ordinati[n / 2]) / 2.0;
    }

    /// <summary>Copia integrale preservando anche gli orari dei file (come una copia/sync reale che li
    /// conserva): il salto delle sorgenti journal immutabili già acquisite (CanSkipImmutableSource) confronta
    /// lunghezza+mtime+sha attesi, quindi orari preservati rendono la copia più fedele all'originale. Non è
    /// necessario alla correttezza: con orari diversi le sorgenti vengono semplicemente rilette.</summary>
    private static void CopiaCartella(string sorgente, string destinazione)
    {
        Directory.CreateDirectory(destinazione);
        foreach (var dir in Directory.EnumerateDirectories(sorgente, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destinazione, Path.GetRelativePath(sorgente, dir)));
        foreach (var file in Directory.EnumerateFiles(sorgente, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(destinazione, Path.GetRelativePath(sorgente, file));
            File.Copy(file, dest, overwrite: true);
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file));
            File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(file));
        }
    }
}
