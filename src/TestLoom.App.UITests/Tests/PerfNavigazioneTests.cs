using System.Diagnostics;
using System.Globalization;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Misura di prestazioni SKIPPATA di default (richiesta utente 2026-09-24: confrontare i tempi di cambio
/// pagina sull'app reale PRIMA/DOPO le correzioni di navigazione lenta di release/6.4.0, sulla cartella dati
/// reale). Attiva solo con la variabile d'ambiente <c>TestLoom_PERF_DATASET</c> puntata su una COPIA della cartella
/// dati (mai l'originale): senza, il test si auto-esclude come <see cref="FiltroAmbitoSenzaTestRealeTests"/>,
/// non fallisce. Non entra mai nella regressione normale: nessuna pipeline imposta quella variabile.
///
/// L'operatore di prova ("QA Perf") esiste SOLO nella copia dei dati passata da TestLoom_PERF_DATASET, seminato
/// qui prima di avviare RBG - QATRIVO.exe (stesso pattern di <see cref="TestWorkspace.CreateLoggedIn"/>, applicato però
/// a una cartella condivisa già esistente invece che a una vuota). Non decifra né riusa PIN reali.
///
/// Piano 6.4.0 FASE P1 (obiettivo prestazioni utente 2026-09-24): copre TUTTE le pagine principali e le
/// sotto-pagine catalogo pesanti di Impostazioni, più l'apertura di un Test nell'editor — non solo la rotta
/// originale a 6 tappe. Un giro "freddo" (il primo) e almeno 3 giri "caldo" (mediana calcolata su quelli, non
/// sulla semplice media) per ogni pagina. TestLoom_PERF_ASSERT_MS, se impostata, fa fallire il test quando la
/// mediana a caldo di una pagina supera la soglia in millisecondi indicata: resta un gate opt-in, nessuna
/// pipeline la imposta di default finché la release non raggiunge l'obiettivo su tutte le pagine.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Scale")]
[Trait("Category", "Performance")]
public sealed class PerfNavigazioneTests : IDisposable
{
    private const string Operatore = "QA Perf";
    private const string OperatorePin = "9137"; // PIN di prova, valido solo sulla copia dei dati del collaudo.

    /// <summary>1 giro freddo + questi giri caldo (>= 3, richiesto per una mediana robusta invece di due soli
    /// campioni).</summary>
    private const int NumeroGiriCaldo = 3;
    private const int NumeroGiri = 1 + NumeroGiriCaldo;

    /// <summary>Home -&gt; Test -&gt; Anomalie/CR -&gt; WU Status -&gt; Report -&gt; Audit -&gt; Master
    /// (Impostazioni) -&gt; Home. Gli id sono i NavItem.Id stabili di MainViewModel (mai il testo tradotto).</summary>
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

    /// <summary>Sotto-pagine catalogo di Impostazioni (NavItem.Id di ImpostazioniViewModel.SottoSezioni),
    /// misurate solo mentre la pagina Master è già aperta (click su "Impostazioni_Sub_{id}", stesso overlay
    /// globale delle pagine principali). "generale" escluso: è già la vista mostrata dal primo ingresso in
    /// Master, misurata sopra come voce "impostazioni" della Rotta.</summary>
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

    private readonly TestWorkspace? _workspace;
    private readonly string? _motivoSkip;
    private readonly string _datasetDir = "";

    public PerfNavigazioneTests()
    {
        var cartella = Environment.GetEnvironmentVariable("TestLoom_PERF_DATASET");
        if (string.IsNullOrWhiteSpace(cartella) || !Directory.Exists(cartella))
        {
            _motivoSkip = "TestLoom_PERF_DATASET non impostata o inesistente: test perf auto-escluso (non fallito).";
            return;
        }

        _datasetDir = cartella;
        SeminaOperatoreSullaCopia(cartella, Operatore, OperatorePin);
        _workspace = TestWorkspace.AdottaCartellaEsistente(nameof(PerfNavigazioneTests), cartella, Operatore);
    }

    /// <summary>Aggiunge l'operatore Master di prova direttamente ai dati Master della COPIA (stessa tecnica di
    /// TestWorkspace.SeedOperator: AppState su un config.json temporaneo separato, mai sulla UI), così
    /// AppState.RicaricaOperatoreCorrente lo trova davvero — impostare solo AppConfig.OperatoreAttivo senza un
    /// Operatore corrispondente lascerebbe OperatoreCorrente nullo e la pagina Master (Impostazioni)
    /// inaccessibile a un ruolo non riconosciuto.</summary>
    private static void SeminaOperatoreSullaCopia(string cartellaCondivisa, string nome, string pin)
    {
        var configPath = Path.Combine(Path.GetTempPath(), "TestLoom_UITests", "PerfSeed_" + Guid.NewGuid().ToString("N"), "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        try
        {
            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = cartellaCondivisa, Lingua = "it", OperatoreAttivo = "" });

            using var state = new AppState(configService, runBackgroundMaintenance: false);
            var operatori = state.Master.Operatori.Load();
            if (!operatori.Any(o => string.Equals(o.Nome, nome, StringComparison.OrdinalIgnoreCase)))
            {
                var master = new Operatore
                {
                    Nome = nome,
                    Iniziali = nome.Length >= 2 ? nome[..2].ToUpperInvariant() : nome.ToUpperInvariant(),
                    PinHash = PinService.Hash(pin),
                    RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()),
                    Ruolo = RuoloOperatore.Master,
                    RichiedeCambioPin = false,
                    ModificatoDa = "PerfUITests",
                };
                RuoloSigilloService.Sigilla(master); // 6.4.2: ruolo sigillato, altrimenti vale Viewer.
                operatori.Add(master);
                state.Master.Operatori.Save(operatori);
            }
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void MisuraNavigazione_SuArchivioReale()
    {
        if (_motivoSkip != null) return;

        var label = Environment.GetEnvironmentVariable("TestLoom_PERF_LABEL") ?? "run";
        var outputPath = Environment.GetEnvironmentVariable("TestLoom_PERF_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "ditm_perf_results.csv");
        ScriviIntestazioneSeAssente(outputPath);

        int? sogliaCaldoMs = int.TryParse(Environment.GetEnvironmentVariable("TestLoom_PERF_ASSERT_MS"), out var soglia)
            ? soglia : null;

        var bootstrapTimeoutSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("TestLoom_PERF_BOOTSTRAP_TIMEOUT_SECONDS"), out var t) ? t : 120;
        var navTimeoutSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("TestLoom_PERF_NAV_TIMEOUT_SECONDS"), out var nt) ? nt : 120;

        var startUtc = DateTime.UtcNow;
        var cronometroBootstrap = Stopwatch.StartNew();
        using var app = TestLoomApplication.Launch(_workspace!);
        var window = app.WaitForMainWindow(TimeSpan.FromSeconds(bootstrapTimeoutSeconds));
        var shell = new MainShellPage(window, app);
        shell.DismissNotificationsIfShown();
        new HomePage(shell.PageHost); // conferma di essere davvero in Home prima del primo giro.
        cronometroBootstrap.Stop();

        // Chiave = etichetta pagina (es. "Test", "Master.Componente"), valore = una entry per giro caldo (Secondi).
        var campioniCaldo = new Dictionary<string, List<double>>();
        void Registra(string etichetta, double secondi, bool caldo)
        {
            if (!caldo) return;
            if (!campioniCaldo.TryGetValue(etichetta, out var lista)) campioniCaldo[etichetta] = lista = new List<double>();
            lista.Add(secondi);
        }

        var righe = new List<string>
        {
            string.Join(",", CsvField(label), "0", CsvField("bootstrap"), CsvField(""), CsvField("bootstrap"),
                CsvField("Avvio->Home pronta"), cronometroBootstrap.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
        };
        for (var giro = 1; giro <= NumeroGiri; giro++)
        {
            var giroTipo = giro == 1 ? "freddo" : "caldo";
            var caldo = giro > 1;
            var daPagina = "home";
            foreach (var (navId, etichetta) in Rotta)
            {
                var secondi = MisuraClickFinoAScomparsaOverlay(window, navId, navTimeoutSeconds);
                righe.Add(RigaCsv(label, giro, giroTipo, daPagina, navId, etichetta, secondi));
                Registra(etichetta, secondi, caldo);
                daPagina = navId;

                if (navId == "impostazioni")
                {
                    foreach (var (subId, subEtichetta) in SottoImpostazioni)
                    {
                        var secondiSub = MisuraClickSubImpostazioniFinoAScomparsaOverlay(window, subId, navTimeoutSeconds);
                        righe.Add(RigaCsv(label, giro, giroTipo, "impostazioni", $"impostazioni.{subId}", subEtichetta, secondiSub));
                        Registra(subEtichetta, secondiSub, caldo);
                    }
                    // Torna su "generale" cosi' il prossimo giro/nav riparte da uno stato noto.
                    ElementFinders.WaitButtonById(window, "Impostazioni_Sub_generale").SafeInvoke();
                }
            }

            // Apertura di un Test esistente nell'editor: dalla lista appena visitata in questo giro (Rotta
            // include "test" per prima), non richiede una query di ricerca — basta la prima riga visibile,
            // sufficiente per misurare il costo di apertura editor su un archivio a piena scala (a differenza
            // del contenuto della riga, che qui non conta).
            ElementFinders.WaitButtonById(window, "Main_Nav_test").SafeInvoke();
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, "Main_CaricamentoPagina_Border"),
                "scomparsa overlay tornando su Test prima di aprire l'editor", TimeSpan.FromSeconds(navTimeoutSeconds));
            var secondiEditor = MisuraAperturaPrimoTest(window, navTimeoutSeconds);
            righe.Add(RigaCsv(label, giro, giroTipo, "test", "test.editor", "Apertura Test (editor)", secondiEditor));
            Registra("Apertura Test (editor)", secondiEditor, caldo);
            ElementFinders.WaitButtonById(window, "Editor_Cancel_Button").SafeInvoke();
        }
        File.AppendAllLines(outputPath, righe, Encoding.UTF8);

        // Riepilogo mediane a caldo (>= 3 campioni per pagina) — colonna aggiuntiva rispetto al CSV grezzo,
        // usata direttamente per la tabella di docs/PERF_6.4.0_BASELINE.md senza dover ricalcolare a mano.
        var riepilogoPath = outputPath + ".mediane.csv";
        var intestazioneRiepilogoAssente = !File.Exists(riepilogoPath);
        var righeRiepilogo = new List<string>();
        if (intestazioneRiepilogoAssente) righeRiepilogo.Add("Label,Pagina,MedianaCaldoSecondi,NumeroCampioni,SogliaMsAssert,Esito");
        var superamentiSoglia = new List<string>();
        foreach (var (etichetta, campioni) in campioniCaldo)
        {
            var mediana = Mediana(campioni);
            var esito = "n/d";
            if (sogliaCaldoMs.HasValue)
            {
                var entroSoglia = mediana * 1000.0 <= sogliaCaldoMs.Value;
                esito = entroSoglia ? "OK" : "FAIL";
                if (!entroSoglia)
                    superamentiSoglia.Add($"{etichetta}: mediana {mediana * 1000:F0} ms > soglia {sogliaCaldoMs.Value} ms");
            }
            righeRiepilogo.Add(string.Join(",", CsvField(label), CsvField(etichetta),
                mediana.ToString("F3", CultureInfo.InvariantCulture), campioni.Count.ToString(CultureInfo.InvariantCulture),
                sogliaCaldoMs?.ToString(CultureInfo.InvariantCulture) ?? "", esito));
        }
        File.AppendAllLines(riepilogoPath, righeRiepilogo, Encoding.UTF8);

        // Chiude l'app PRIMA di leggere performance.ndjson: alcuni eventi (es. journal.refresh in chiusura)
        // sono irrilevanti qui, ma soprattutto lo store SQLite/ndjson va letto a scrittura ferma.
        app.Dispose();

        try
        {
            ScriviRecordPerformanceNdjson(outputPath, label, startUtc);
        }
        catch (Exception ex)
        {
            // La misura click->overlay sopra è già salvata: la telemetria interna è un arricchimento, non deve
            // far fallire il collaudo se lo store locale non si trova o non si legge.
            File.AppendAllLines(outputPath + ".ndjson-errori.log",
                new[] { $"{DateTime.UtcNow:O} label={label}: {ex}" });
        }

        // Gate opt-in (TestLoom_PERF_ASSERT_MS): resta DISATTIVO di default, nessuna pipeline lo imposta finché la
        // release non raggiunge l'obiettivo utente (700 ms a caldo) su TUTTE le pagine misurate qui sopra.
        if (sogliaCaldoMs.HasValue && superamentiSoglia.Count > 0)
        {
            Assert.Fail("Pagine oltre la soglia TestLoom_PERF_ASSERT_MS=" + sogliaCaldoMs.Value + " ms (mediana a caldo):"
                + Environment.NewLine + string.Join(Environment.NewLine, superamentiSoglia));
        }
    }

    private static double Mediana(List<double> valori)
    {
        var ordinati = valori.OrderBy(v => v).ToList();
        var n = ordinati.Count;
        if (n == 0) return 0;
        return n % 2 == 1 ? ordinati[n / 2] : (ordinati[n / 2 - 1] + ordinati[n / 2]) / 2.0;
    }

    private static double MisuraAperturaPrimoTest(Window window, int timeoutSeconds)
    {
        var pulsante = Wait.Until(
            () => window.FindAllDescendants(cf => cf.ByAutomationId("TestList_Open_Button")).FirstOrDefault(),
            e => e != null, TimeSpan.FromSeconds(timeoutSeconds), "prima riga Test disponibile")!;
        var cronometro = Stopwatch.StartNew();
        pulsante.AsButton().SafeInvoke();
        Wait.ForCondition(
            () => ElementFinders.ExistsById(window, "Editor_Title_Text") || ElementFinders.ExistsById(window, "Editor_TemplatePicker_Button"),
            "apertura editor Test", TimeSpan.FromSeconds(timeoutSeconds));
        cronometro.Stop();
        return cronometro.Elapsed.TotalSeconds;
    }

    private static double MisuraClickFinoAScomparsaOverlay(Window window, string navId, int timeoutSeconds)
    {
        var pulsante = ElementFinders.WaitButtonById(window, $"Main_Nav_{navId}");
        var cronometro = Stopwatch.StartNew();
        pulsante.SafeInvoke();
        Wait.ForCondition(
            () => !ElementFinders.ExistsById(window, "Main_CaricamentoPagina_Border"),
            $"scomparsa overlay 'Main_CaricamentoPagina_Border' dopo la navigazione verso '{navId}'",
            TimeSpan.FromSeconds(timeoutSeconds));
        cronometro.Stop();
        return cronometro.Elapsed.TotalSeconds;
    }

    /// <summary>Stesso principio di MisuraClickFinoAScomparsaOverlay ma per un pulsante "Impostazioni_Sub_{id}"
    /// della sidebar interna di Master, invece di "Main_Nav_{id}" della sidebar principale.</summary>
    private static double MisuraClickSubImpostazioniFinoAScomparsaOverlay(Window window, string subId, int timeoutSeconds)
    {
        var pulsante = ElementFinders.WaitButtonById(window, $"Impostazioni_Sub_{subId}");
        var cronometro = Stopwatch.StartNew();
        pulsante.SafeInvoke();
        Wait.ForCondition(
            () => !ElementFinders.ExistsById(window, "Main_CaricamentoPagina_Border"),
            $"scomparsa overlay 'Main_CaricamentoPagina_Border' dopo la navigazione verso 'impostazioni.{subId}'",
            TimeSpan.FromSeconds(timeoutSeconds));
        cronometro.Stop();
        return cronometro.Elapsed.TotalSeconds;
    }

    private static string RigaCsv(string label, int giro, string giroTipo, string daPagina, string versoNavId,
        string versoEtichetta, double secondi) =>
        string.Join(",", CsvField(label), giro.ToString(CultureInfo.InvariantCulture), CsvField(giroTipo),
            CsvField(daPagina), CsvField(versoNavId), CsvField(versoEtichetta),
            secondi.ToString("F3", CultureInfo.InvariantCulture),
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

    private static void ScriviIntestazioneSeAssente(string outputPath)
    {
        if (File.Exists(outputPath)) return;
        File.WriteAllText(outputPath,
            "Label,Giro,TipoGiro,DaPagina,VersoNavId,VersoEtichetta,Secondi,TimestampUtc" + Environment.NewLine,
            Encoding.UTF8);
    }

    /// <summary>Legge i record navigazione.costruzione/navigazione.pagina scritti da PerformanceDiagnosticsService
    /// nel performance.ndjson locale del workspace usato in QUESTA esecuzione (%LOCALAPPDATA%\TestLoom\workspaces\
    /// &lt;hash&gt;\performance.ndjson, dove l'hash dipende dal percorso della cartella condivisa). Non
    /// ricalcola l'hash SHA256 interno di LocalTestIndexStore: sceglie invece, tra le cartelle sotto
    /// %LOCALAPPDATA%\TestLoom\workspaces, quella modificata più di recente durante la finestra [startUtc, ora] —
    /// valido perché ogni esecuzione di questo test usa una cartella dati appena estratta (hash mai visto
    /// prima) e la macchina è dedicata a questo collaudo (nessun altro RBG - QATRIVO.exe in esecuzione).</summary>
    private static void ScriviRecordPerformanceNdjson(string outputPath, string label, DateTime startUtc)
    {
        var workspacesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestLoom", "workspaces");
        if (!Directory.Exists(workspacesRoot)) return;

        var candidato = new DirectoryInfo(workspacesRoot).GetDirectories()
            .Select(d => new { Dir = d, Ndjson = Path.Combine(d.FullName, "performance.ndjson") })
            .Where(x => File.Exists(x.Ndjson) && File.GetLastWriteTimeUtc(x.Ndjson) >= startUtc.AddSeconds(-2))
            .OrderByDescending(x => File.GetLastWriteTimeUtc(x.Ndjson))
            .FirstOrDefault();
        if (candidato == null) return;

        var eventiPath = outputPath + ".ndjson-eventi.csv";
        var intestazioneAssente = !File.Exists(eventiPath);
        var righe = new List<string>();
        if (intestazioneAssente) righe.Add("Label,TimestampUtc,Operation,ElapsedMs,Detail");

        foreach (var evt in PerformanceDiagnosticsService.ReadRecent(candidato.Dir.FullName, max: 5000))
        {
            if (evt.TimestampUtc < startUtc) continue;
            // Non filtra solo "navigazione.*": su archivi grandi la fase dominante di un cambio pagina può
            // essere testcases.full-reconciliation/journal.refresh/storage.maintenance/workspace.bootstrap.totale
            // innescata dallo stesso giro, non la sola costruzione ViewModel misurata da navigazione.costruzione.
            righe.Add(string.Join(",",
                CsvField(label), evt.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                CsvField(evt.Operation), evt.ElapsedMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                CsvField(evt.Detail)));
        }
        File.AppendAllLines(eventiPath, righe, Encoding.UTF8);
    }

    private static string CsvField(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public void Dispose()
    {
        _workspace?.Dispose();
    }
}
