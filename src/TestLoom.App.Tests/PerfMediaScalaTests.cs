using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
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
/// Benchmark a scala media (5.000 e 20.000 Test) dei percorsi che l'operatore vive come attesa: avvio a caldo di
/// AppState con indice SQLite locale gia' presente, apertura di Home, filtro testo della Lista Test, apertura e
/// filtro di stato di Anomalie/CR, apertura di WU Status, query della ricerca globale.
///
/// Ogni percorso: 1 warm-up non cronometrato + 5 misure con Stopwatch, GC stabilizzato prima di ogni misura. Riportati:
/// p50, max, pausa GC (p50, durante l'operazione), delta del working set e i contatori PerfContatori dell'ultimo giro
/// (fingerprint ricalcolati e riusati, Load() dei master, refresh Phase D, scansioni complete dei Test).
///
/// Nessuna soglia assoluta e nessuna asserzione sui tempi: la scala lunga (50k/100k) resta alla suite Scale finale.
/// Il workspace sintetico usa i generatori condivisi (BancoPerf100kGeneratoreTests, BancoScaleSeedCore) e vive sotto
/// .ai/tmp della radice del repository (cartella non versionata), rimossa a fine test. %LOCALAPPDATA% e' gia'
/// reindirizzato da <see cref="TestLocalAppDataFixture"/>.
///
/// Output: .ai/artifacts/perf/media-scala-{etichetta}-{N}k.md (tabella) e .tsv (dati). Etichetta da
/// TestLoom_PERF_MEDIA_LABEL (default "corrente").
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfMediaScalaTests
{
    private const int CampioniMisurati = 5;

    // Termini diversi a ogni giro (warm-up incluso): ogni misura cambia davvero filtro o query.
    private static readonly string[] TerminiFiltroTest =
    {
        "banco scenario 17", "banco scenario 42", "banco scenario 99",
        "banco scenario 123", "banco scenario 301", "banco scenario 7",
    };
    private static readonly string[] TerminiRicercaGlobale =
    {
        "Operatore-Bench-07", "scenario banco 17", "REQ-SCALE-0",
        "Componente-Scale-00", "Veicolo-Scale-0", "banco 42",
    };

    // Tre valori consecutivi sempre distinti: ogni cambio di filtro ricarica la pagina.
    private static readonly StatoNonConformita?[] StatiFiltroNc = { null, StatoNonConformita.Chiusa, StatoNonConformita.Aperta };

    private readonly ITestOutputHelper _output;
    public PerfMediaScalaTests(ITestOutputHelper output) => _output = output;

    private sealed record RigaPerf(string Percorso, double P50, double Max, double DeltaWsMb, double[] Campioni,
        PerfContatori.Snapshot Contatori, double GcPausaP50Ms);

    /// <summary>
    /// Carico Audit filtrato per componente e lettura completa di QueryAll, con byte allocati (GC.GetTotalAllocatedBytes) e pausa
    /// GC misurate insieme al tempo, a 20k e 50k. Stesso workspace e stesso metodo della misura QueryAll (1 warm-up e 5
    /// misure, GC stabilizzato, p50). Il percorso Audit replica RicaricaMatrice: QueryAll con i parametri di default
    /// (prima della modifica: tetto di 20.000 e ripiego su GetAll() dopo l'eccezione; dopo: nessun tetto), indice per
    /// coppia Componente-Requisito e Matrice. La firma confronta il contenuto dei risultati: stessa firma = stessi dati.
    /// Attiva solo con TestLoom_PERF_ARCHIVIO=1. Output: .ai/artifacts/perf/archivio-gc-audit-{etichetta}-{N}k.md e .tsv.
    /// </summary>
    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    [InlineData(50_000)]
    public void MisuraAuditEcuEAllocazioniScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_ARCHIVIO"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;

        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radiceRepo = TrovaRadiceRepo();
        var baseTemporanea = radiceRepo != null
            ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-media")
            : Path.Combine(Path.GetTempPath(), "TestLoom_PerfMediaScala");
        var root = Path.Combine(baseTemporanea, $"audit-gc-{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var configPath = Path.Combine(root, "local", "config.json");
        var righe = new List<RigaGc>();
        var contesto = new List<string>();
        try
        {
            var configService = CostruisciWorkspace(scala, dataRoot, configPath);
            ImpostaMarcatoreIndice(dataRoot, presente: true);
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            var archivio = new LocalTestArchiveQueryService(state.TestCases.LocalIndexDatabasePath!, state.TestCases.IndexRecovery);
            var tutteLeEcu = state.Master.Componente.Load();
            var requisiti = state.Master.Requisiti.Load();
            var risoluzioni = state.Master.RisoluzioniConflittoAudit.Load();
            var valutazioni = state.Master.ValutazioniRequisitoEcuVeicolo.Load();
            var (reqStandard, reqD4d) = AuditMatriceService.SeparaPerTipo(requisiti);
            // Sottoveicolo con piu' Componenti associati (caso realistico) e insieme di tutti i componenti attive (caso peggiore).
            var veicolo = state.Master.Veicoli.Load().Where(v => !v.Archiviata).Select(v => v.Nome)
                .OrderByDescending(n => AuditMatriceService.EcuDelSottoveicolo(tutteLeEcu, n).Count).First();
            var ecuVeicolo = AuditMatriceService.EcuDelSottoveicolo(tutteLeEcu, veicolo);
            var ecuTutte = tutteLeEcu.Where(e => !e.Archiviata).Select(e => e.Nome).ToList();
            // Confronto cella per cella (fuori dal cronometro): percorso di produzione contro QueryAll su SQLite, per il
            // sottoveicolo e per tutti i componenti. Stati, campi, insieme e ordine dei candidati devono coincidere del tutto.
            foreach (var (nome, insiemeEcu) in new[] { ("sottoveicolo", ecuVeicolo), ("tutti i componenti attive", ecuTutte) })
            {
                var (_, celleRiferimento) = CalcolaMatriceAudit(veicolo, tutteLeEcu, reqStandard, reqD4d, risoluzioni, valutazioni,
                    archivio.QueryAll(new LocalTestArchiveQuery { AllowedEcus = insiemeEcu }));
                var (_, cellePercorso) = CalcolaMatriceAudit(veicolo, tutteLeEcu, reqStandard, reqD4d, risoluzioni, valutazioni,
                    LeggiTestDelleEcuReplica(archivio, state.TestCases.GetAll(), insiemeEcu) ?? state.TestCases.GetAll());
                var confronto = DiagnosiCelle(celleRiferimento, cellePercorso);
                contesto.Add($"Confronto celle, {nome} (percorso di produzione contro QueryAll): {confronto}");
                Assert.Equal(0, confronto.StatoODiversi);
                Assert.Equal(0, confronto.SoloOrdineDiversi);
                Assert.Equal(0, confronto.InsiemeDiversi);
            }
            contesto.Add($"Sottoveicolo {veicolo}: {ecuVeicolo.Count} componente. Test attivi letti: archivio intero " +
                $"{archivio.QuerySlice(new LocalTestArchiveQuery(), 0, 1).TotalCount}; componente del sottoveicolo " +
                $"{archivio.QuerySlice(new LocalTestArchiveQuery { AllowedEcus = ecuVeicolo }, 0, 1).TotalCount}; tutti i componenti attive " +
                $"{archivio.QuerySlice(new LocalTestArchiveQuery { AllowedEcus = ecuTutte }, 0, 1).TotalCount}.");

            righe.Add(MisuraGc("QueryAll su tutti i Test (Audit/Report), senza tetto",
                () => archivio.QueryAll(new LocalTestArchiveQuery(), maximumRows: long.MaxValue),
                lista => (lista.Count, FirmaTest(lista))));
            // Stessa lettura con la proiezione Audit/Report (QueryAllAnalisi): la firma usa solo campi proiettati e deve coincidere.
            righe.Add(MisuraGc("QueryAllAnalisi su tutti i Test (proiezione Audit/Report), senza tetto",
                () => archivio.QueryAllAnalisi(new LocalTestArchiveQuery()),
                lista => (lista.Count, FirmaTest(lista))));
            righe.Add(MisuraGc("QueryAll senza tetto, con GCLatencyMode.SustainedLowLatency (esperimento, solo banco)",
                () => QueryAllConModalitaGcBassaLatenza(archivio),
                lista => (lista.Count, FirmaTest(lista))));
            righe.Add(MisuraGc("Audit, componente del sottoveicolo (selettivo): percorso di produzione",
                () => CalcolaMatriceAudit(veicolo, tutteLeEcu, reqStandard, reqD4d, risoluzioni, valutazioni,
                    LeggiTestDelleEcuReplica(archivio, state.TestCases.GetAll(), ecuVeicolo) ?? state.TestCases.GetAll()),
                r => (r.TestLetti, FirmaCelle(r.Celle))));
            righe.Add(MisuraGc("Audit, tutti i componenti attive (non selettivo): percorso di produzione",
                () => CalcolaMatriceAudit(veicolo, tutteLeEcu, reqStandard, reqD4d, risoluzioni, valutazioni,
                    LeggiTestDelleEcuReplica(archivio, state.TestCases.GetAll(), ecuTutte) ?? state.TestCases.GetAll()),
                r => (r.TestLetti, FirmaCelle(r.Celle))));
            righe.Add(MisuraGc("Filtro in memoria DaSnapshot, componente del sottoveicolo (confronto)",
                () => CalcolaMatriceAudit(veicolo, tutteLeEcu, reqStandard, reqD4d, risoluzioni, valutazioni,
                    AuditSelezioneTestPerEcu.DaSnapshot(state.TestCases.GetAll(), ecuVeicolo)),
                r => (r.TestLetti, FirmaCelle(r.Celle))));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }

        var md = ComponiMarkdownGc(scala, etichetta, righe, contesto);
        _output.WriteLine(md);
        if (radiceRepo == null)
        {
            _output.WriteLine("ARTEFATTI|radice_repo_non_trovata=true|scritti=false");
            return;
        }
        var suffisso = $"{scala / 1000}k";
        var cartellaPerf = Path.Combine(radiceRepo, ".ai", "artifacts", "perf");
        Directory.CreateDirectory(cartellaPerf);
        File.WriteAllText(Path.Combine(cartellaPerf, $"archivio-gc-audit-{etichetta}-{suffisso}.md"), md, Encoding.UTF8);
        File.WriteAllText(Path.Combine(cartellaPerf, $"archivio-gc-audit-{etichetta}-{suffisso}.tsv"), ComponiTsvGc(righe), Encoding.UTF8);
        _output.WriteLine($"ARTEFATTI|cartella={cartellaPerf}|etichetta={etichetta}|scala={suffisso}");
    }

    /// <summary>Esperimento di misura: stessa lettura di QueryAll con la modalita' GC a bassa latenza per la sola durata
    /// della chiamata (ripristinata nel finally). Non e' un percorso di produzione.</summary>
    private static IReadOnlyList<TestCase> QueryAllConModalitaGcBassaLatenza(LocalTestArchiveQueryService archivio)
    {
        var precedente = System.Runtime.GCSettings.LatencyMode;
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        try { return archivio.QueryAll(new LocalTestArchiveQuery(), maximumRows: long.MaxValue); }
        finally { System.Runtime.GCSettings.LatencyMode = precedente; }
    }

    /// <summary>Percorso Audit di RicaricaMatrice (TryLeggiTestDelleEcuDaIndice), con la stessa scelta di produzione
    /// (AuditSelezioneTestPerEcu.Leggi): null se la lettura fallisce, cosi' il chiamante ripiega su GetAll(). Nel banco
    /// il crash log non viene scritto.</summary>
    private static IReadOnlyList<TestCase>? LeggiTestDelleEcuReplica(
        LocalTestArchiveQueryService archivio, IReadOnlyList<TestCase> attivi, IReadOnlyCollection<string> componente)
    {
        if (componente.Count == 0) return new List<TestCase>();
        try { return AuditSelezioneTestPerEcu.Leggi(archivio, attivi, componente); }
        catch (Exception) { return null; }
    }

    /// <summary>Indice per coppia Componente-Requisito e Matrice (standard e Extra), come in RicaricaMatrice.</summary>
    private static (int TestLetti, List<CellaMatriceAudit> Celle) CalcolaMatriceAudit(
        string veicolo,
        IReadOnlyList<Componente> tutteLeEcu,
        IReadOnlyList<Requisito> reqStandard,
        IReadOnlyList<Requisito> reqD4d,
        IReadOnlyList<RisoluzioneConflittoAudit> risoluzioni,
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> valutazioni,
        IReadOnlyList<TestCase> testCases)
    {
        var indice = AuditMatriceService.IndicizzaTestPerEcuERequisito(testCases);
        var celle = AuditMatriceService.Matrice(veicolo, tutteLeEcu, reqStandard, testCases, risoluzioni, indice, valutazioni);
        if (reqD4d.Count > 0)
            celle.AddRange(AuditMatriceService.Matrice(veicolo, tutteLeEcu, reqD4d, testCases, risoluzioni, indice, valutazioni));
        return (testCases.Count, celle);
    }

    /// <summary>Confronto cella per cella: differenze di stato/punteggio/campi, insieme dei candidati e solo ordine dei candidati.</summary>
    private sealed record ConfrontoCelle(int Celle, int StatoODiversi, int SoloOrdineDiversi, int InsiemeDiversi, string Esempi)
    {
        public override string ToString() =>
            $"celle={Celle}; stato o campi diversi={StatoODiversi}; stessi candidati in ordine diverso={SoloOrdineDiversi}; " +
            $"insieme di candidati diverso={InsiemeDiversi}. Esempi: {Esempi}";
    }

    private static ConfrontoCelle DiagnosiCelle(List<CellaMatriceAudit> a, List<CellaMatriceAudit> b)
    {
        if (a.Count != b.Count) return new ConfrontoCelle(a.Count, int.MaxValue, 0, int.MaxValue, $"numero di celle diverso: {a.Count} contro {b.Count}");
        var statoDiverso = 0;
        var insiemeDiverso = 0;
        var soloOrdineDiverso = 0;
        var esempi = new List<string>();
        for (var i = 0; i < a.Count; i++)
        {
            var campiA = CampiCella(a[i]);
            var campiB = CampiCella(b[i]);
            var candA = a[i].Candidati.Select(c => FormattableString.Invariant($"{c.TestCaseId}:{c.Punteggio}:{c.Data:O}")).ToList();
            var candB = b[i].Candidati.Select(c => FormattableString.Invariant($"{c.TestCaseId}:{c.Punteggio}:{c.Data:O}")).ToList();
            if (campiA != campiB)
            {
                statoDiverso++;
                if (esempi.Count < 2) esempi.Add($"[{a[i].Componente}/{a[i].RequisitoId}] {campiA} <> {campiB}");
            }
            else if (!candA.SequenceEqual(candB))
            {
                if (candA.Order(StringComparer.Ordinal).SequenceEqual(candB.Order(StringComparer.Ordinal))) soloOrdineDiverso++;
                else insiemeDiverso++;
                if (esempi.Count < 2) esempi.Add($"[{a[i].Componente}/{a[i].RequisitoId}] candidati {string.Join(",", candA.Take(3))} <> {string.Join(",", candB.Take(3))}");
            }
        }
        return new ConfrontoCelle(a.Count, statoDiverso, soloOrdineDiverso, insiemeDiverso, string.Join(" || ", esempi));
    }

    private static string CampiCella(CellaMatriceAudit c) =>
        FormattableString.Invariant($"{c.Componente}|{c.RequisitoId}|{c.Stato}|{c.Punteggio}|{c.Origine}|{c.DataEsecuzione:O}|{c.InserimentoManuale}|{c.ConflittoCollisioneCella}|{c.Tooltip}|{c.Nota}");

    private static string FirmaTest(IReadOnlyList<TestCase> lista)
    {
        var sb = new StringBuilder();
        foreach (var tc in lista)
            sb.Append(FormattableString.Invariant($"{tc.Uid:N}|{tc.Id}|{tc.UltimaModifica.Ticks}|{tc.Componente.Count}|{tc.Eliminato}\n"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static string FirmaCelle(List<CellaMatriceAudit> celle)
    {
        var sb = new StringBuilder();
        foreach (var c in celle)
        {
            sb.Append(FormattableString.Invariant(
                $"{c.Componente}|{c.RequisitoId}|{c.Stato}|{c.Punteggio}|{c.Origine}|{c.DataEsecuzione:O}|{c.InserimentoManuale}|{c.ConflittoCollisioneCella}|{c.Tooltip}|{c.Nota}|"));
            foreach (var cand in c.Candidati)
                sb.Append(FormattableString.Invariant($"[{cand.TestCaseId}:{cand.Punteggio}:{cand.Data:O}]"));
            sb.Append('\n');
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private RigaGc MisuraGc<T>(string percorso, Func<T> operazione, Func<T, (int Righe, string Firma)> riepilogo)
    {
        riepilogo(operazione()); // warm-up, non misurato
        var tempi = new double[CampioniMisurati];
        var pausaMs = new double[CampioniMisurati];
        var pausaPercento = new double[CampioniMisurati];
        var allocatiMb = new double[CampioniMisurati];
        (int Righe, string Firma) ultimo = (0, "");
        for (var i = 0; i < CampioniMisurati; i++)
        {
            StabilizzaGc();
            var pausaPrima = GC.GetTotalPauseDuration();
            var allocatiPrima = GC.GetTotalAllocatedBytes(precise: false);
            var sw = Stopwatch.StartNew();
            var risultato = operazione();
            tempi[i] = sw.Elapsed.TotalMilliseconds;
            pausaMs[i] = (GC.GetTotalPauseDuration() - pausaPrima).TotalMilliseconds;
            allocatiMb[i] = (GC.GetTotalAllocatedBytes(precise: false) - allocatiPrima) / 1048576d;
            pausaPercento[i] = tempi[i] > 0 ? 100d * pausaMs[i] / tempi[i] : 0d;
            ultimo = riepilogo(risultato);   // fuori dalla finestra cronometrata
        }
        var ordinati = tempi.OrderBy(v => v).ToArray();
        var riga = new RigaGc(percorso, Mediana(ordinati), ordinati[^1],
            Mediana(pausaMs.OrderBy(v => v).ToArray()), Mediana(pausaPercento.OrderBy(v => v).ToArray()),
            Mediana(allocatiMb.OrderBy(v => v).ToArray()), ultimo.Righe, ultimo.Firma);
        _output.WriteLine($"BENCHMARK-GC|percorso={percorso}|p50={F(riga.P50)}|max={F(riga.Max)}|pausaGcP50Ms={F(riga.PausaP50Ms)}" +
            $"|pausaGcPercento={F(riga.PausaPercento)}|allocatoMbP50={F(riga.AllocatoMbP50)}|righe={riga.Righe.ToString(CultureInfo.InvariantCulture)}" +
            $"|firma={riga.Firma}|campioni={string.Join(';', tempi.Select(F))}");
        return riga;
    }

    private static string ComponiMarkdownGc(int scala, string etichetta, IReadOnlyList<RigaGc> righe, IReadOnlyList<string> contesto)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Audit componente e lettura completa: tempo, byte allocati e pausa GC, {scala / 1000}k Test, etichetta {etichetta}");
        sb.AppendLine();
        foreach (var c in contesto) sb.AppendLine($"- {c}");
        sb.AppendLine();
        sb.AppendLine("| Percorso | p50 ms | max ms | Pausa GC p50 ms | Pausa GC % del tempo (mediana) | MB allocati (p50) | Righe | Firma |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var r in righe)
            sb.AppendLine($"| {r.Percorso} | {F(r.P50)} | {F(r.Max)} | {F(r.PausaP50Ms)} | {F(r.PausaPercento)} | {F(r.AllocatoMbP50)} | {r.Righe.ToString(CultureInfo.InvariantCulture)} | {r.Firma} |");
        return sb.ToString();
    }

    private static string ComponiTsvGc(IReadOnlyList<RigaGc> righe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("percorso\tp50_ms\tmax_ms\tpausa_gc_p50_ms\tpausa_gc_percento\tmb_allocati_p50\trighe\tfirma");
        foreach (var r in righe)
            sb.AppendLine(string.Join('\t', r.Percorso, F(r.P50), F(r.Max), F(r.PausaP50Ms), F(r.PausaPercento), F(r.AllocatoMbP50),
                r.Righe.ToString(CultureInfo.InvariantCulture), r.Firma));
        return sb.ToString();
    }

    private sealed record RigaGc(string Percorso, double P50, double Max, double PausaP50Ms, double PausaPercento,
        double AllocatoMbP50, int Righe, string Firma);

    /// <summary>
    /// Lettura completa dell'archivio Test (QueryAll, usata da Audit e Report) misurata da sola a 5k, 20k e 50k: stesso
    /// workspace e stesso metodo del giro standard (1 warm-up + 5 misure, GC stabilizzato, p50/max), senza gli altri
    /// percorsi. Attiva solo con TestLoom_PERF_ARCHIVIO=1, quindi il giro standard non cambia. TestLoom_PERF_MEDIA_SCALE e
    /// TestLoom_PERF_MEDIA_LABEL valgono come nel giro standard.
    /// Output: .ai/artifacts/perf/archivio-queryall-{etichetta}-{N}k.md e .tsv.
    /// </summary>
    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    [InlineData(50_000)]
    public void MisuraLetturaCompletaArchivioScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_ARCHIVIO"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;

        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radiceRepo = TrovaRadiceRepo();
        var baseTemporanea = radiceRepo != null
            ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-media")
            : Path.Combine(Path.GetTempPath(), "TestLoom_PerfMediaScala");
        var root = Path.Combine(baseTemporanea, $"archivio-{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var configPath = Path.Combine(root, "local", "config.json");
        var righe = new List<RigaPerf>();
        var contesto = new List<string>();
        try
        {
            var configService = CostruisciWorkspace(scala, dataRoot, configPath);
            // Come nel giro standard: marcatore presente, cioe' il percorso di produzione con riuso degli hash.
            ImpostaMarcatoreIndice(dataRoot, presente: true);
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            var archivio = new LocalTestArchiveQueryService(state.TestCases.LocalIndexDatabasePath!, state.TestCases.IndexRecovery);
            var totale = archivio.QuerySlice(new LocalTestArchiveQuery(), 0, 1).TotalCount;
            contesto.Add($"Test nell'archivio letti da QueryAll: {totale.ToString(CultureInfo.InvariantCulture)} (conteggio atteso).");
            righe.Add(Misura("archivio Test, QueryAll su tutti i Test (Audit/Report)",
                () => archivio.QueryAll(new LocalTestArchiveQuery(), maximumRows: 100_000), _ => { }));
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
        File.WriteAllText(Path.Combine(cartellaPerf, $"archivio-queryall-{etichetta}-{suffisso}.md"), md, Encoding.UTF8);
        File.WriteAllText(Path.Combine(cartellaPerf, $"archivio-queryall-{etichetta}-{suffisso}.tsv"), ComponiTsv(righe), Encoding.UTF8);
        _output.WriteLine($"ARTEFATTI|cartella={cartellaPerf}|etichetta={etichetta}|scala={suffisso}");
    }

    /// <summary>
    /// Report (ReportViewModel) con filtro attivo a scala media: ogni cambio di filtro e ogni rivisita con Aggiorna() a
    /// filtro invariato passano da LocalTestArchiveQueryService.QueryAll (TryLeggiRisultatoFiltratoDaIndice), poi da
    /// anteprima e matrice. Tre percorsi: componente selettiva (fra 1% e 5% dell'archivio), data da non selettiva (tutti i Test),
    /// rivisita con Aggiorna() a filtro invariato. Per ciascuno: p50, max, pausa GC, byte allocati e una firma del risultato
    /// visibile (anteprima, totali, celle) per confrontare prima e dopo sullo stesso banco. Attiva solo con
    /// TestLoom_PERF_ARCHIVIO=1. Output: .ai/artifacts/perf/report-filtro-{etichetta}-{N}k.md e .tsv.
    /// </summary>
    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public void MisuraReportFiltratoEAllocazioniScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_ARCHIVIO"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;

        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radiceRepo = TrovaRadiceRepo();
        var baseTemporanea = radiceRepo != null
            ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-media")
            : Path.Combine(Path.GetTempPath(), "TestLoom_PerfMediaScala");
        var root = Path.Combine(baseTemporanea, $"report-{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var configPath = Path.Combine(root, "local", "config.json");
        var righe = new List<RigaGc>();
        var contesto = new List<string>();
        try
        {
            var configService = CostruisciWorkspace(scala, dataRoot, configPath);
            ImpostaMarcatoreIndice(dataRoot, presente: true);
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            var archivio = new LocalTestArchiveQueryService(state.TestCases.LocalIndexDatabasePath!, state.TestCases.IndexRecovery);
            var totale = archivio.QuerySlice(new LocalTestArchiveQuery(), 0, 1).TotalCount;
            // Due componente selettive (fino al 5% dell'archivio) con valori distinti: ogni cambio di filtro forza il ricalcolo.
            var abbinate = state.Master.Componente.Load().Where(e => !e.Archiviata).Select(e => e.Nome)
                .Select(n => (Nome: n, Test: archivio.QuerySlice(new LocalTestArchiveQuery { Componente = n }, 0, 1).TotalCount))
                .Where(x => x.Test > 0).OrderByDescending(x => x.Test).ThenBy(x => x.Nome, StringComparer.Ordinal).ToList();
            var selettive = abbinate.Where(x => x.Test <= totale * 0.05).ToList();
            if (selettive.Count < 2) selettive = abbinate.OrderBy(x => x.Test).ToList();
            if (selettive.Count < 2) throw new InvalidOperationException("Il banco Report richiede almeno due componente con Test.");
            var ecuA = selettive[0].Nome;
            var ecuB = selettive[1].Nome;
            contesto.Add($"Test attivi {totale.ToString(CultureInfo.InvariantCulture)}; componente selettive: {ecuA} ({selettive[0].Test} Test) e " +
                $"{ecuB} ({selettive[1].Test} Test); componente con almeno un Test: {abbinate.Count}.");

            var vm = new ReportViewModel(state, aggiornaInBackground: true);
            var alternanza = 0;
            var dataA = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var dataB = new DateTimeOffset(2000, 1, 2, 0, 0, 0, TimeSpan.Zero);

            righe.Add(MisuraGc("Report, componente selettiva (QueryAll), cambio filtro",
                () => { vm.FiltroEcu = alternanza++ % 2 == 0 ? ecuA : ecuB; vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult(); return vm.TotaleRisultatiAnteprima; },
                _ => (vm.TotaleRisultatiAnteprima, FirmaReport(vm))));

            // Data da non selettiva: il filtro resta attivo (quindi QueryAll) ma abbina quasi tutto l'archivio.
            vm.FiltroEcu = null;
            righe.Add(MisuraGc("Report, data da non selettiva (QueryAll su quasi tutti i Test), cambio filtro",
                () => { vm.DataDa = alternanza++ % 2 == 0 ? dataA : dataB; vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult(); return vm.TotaleRisultatiAnteprima; },
                _ => (vm.TotaleRisultatiAnteprima, FirmaReport(vm))));

            // Rivisita della pagina con filtro invariato: Aggiorna() senza nuovi dati.
            vm.DataDa = dataA;
            righe.Add(MisuraGc("Report, rivisita Aggiorna() a filtro invariato (QueryAll)",
                () => { vm.Aggiorna(); vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult(); return vm.TotaleRisultatiAnteprima; },
                _ => (vm.TotaleRisultatiAnteprima, FirmaReport(vm))));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Report con filtro, {scala / 1000}k Test, etichetta {etichetta}");
        foreach (var c in contesto) sb.AppendLine($"- {c}");
        sb.AppendLine();
        sb.AppendLine("| Percorso | p50 ms | max ms | pausa GC p50 ms | pausa GC % | MB allocati p50 | righe | firma |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var r in righe)
            sb.AppendLine($"| {r.Percorso} | {F(r.P50)} | {F(r.Max)} | {F(r.PausaP50Ms)} | {F(r.PausaPercento)} | {F(r.AllocatoMbP50)} | {r.Righe.ToString(CultureInfo.InvariantCulture)} | {r.Firma} |");
        var md = sb.ToString();
        _output.WriteLine(md);
        if (radiceRepo == null)
        {
            _output.WriteLine("ARTEFATTI|radice_repo_non_trovata=true|scritti=false");
            return;
        }
        var suffisso = $"{scala / 1000}k";
        var cartellaPerf = Path.Combine(radiceRepo, ".ai", "artifacts", "perf");
        Directory.CreateDirectory(cartellaPerf);
        File.WriteAllText(Path.Combine(cartellaPerf, $"report-filtro-{etichetta}-{suffisso}.md"), md, Encoding.UTF8);
        File.WriteAllText(Path.Combine(cartellaPerf, $"report-filtro-{etichetta}-{suffisso}.tsv"), ComponiTsvGc(righe), Encoding.UTF8);
        _output.WriteLine($"ARTEFATTI|cartella={cartellaPerf}|etichetta={etichetta}|scala={suffisso}");
    }

    /// <summary>
    /// Report con calcolo in background (giri, vedi ReportViewModel.Giro): per ogni percorso misura quanto resta bloccato
    /// il thread che chiama il cambio filtro (solo l'assegnazione), quanto dura fino ai dati applicati (assegnazione piu'
    /// <c>AttendiAggiornamentoAsync</c>) e quanti ricalcoli produce. Un ricalcolo legge una volta sola i tre cataloghi
    /// master usati dalla matrice (Veicoli, Funzionalita, TestTemplate): ricalcoli = letture catalogo / 3. Il percorso
    /// "tre cambi" conta i ricalcoli di tre assegnazioni consecutive senza attese in mezzo. Stessi percorsi del banco
    /// Report, con la firma del risultato visibile per confrontare prima e dopo. Attiva solo con TestLoom_PERF_ARCHIVIO=1.
    /// Output: .ai/artifacts/perf/report-giro-{etichetta}-{N}k.md.
    /// </summary>
    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public void MisuraReportGiroNonBloccanteScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_ARCHIVIO"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;

        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radiceRepo = TrovaRadiceRepo();
        var baseTemporanea = radiceRepo != null
            ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-media")
            : Path.Combine(Path.GetTempPath(), "TestLoom_PerfMediaScala");
        var root = Path.Combine(baseTemporanea, $"giro-{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var configPath = Path.Combine(root, "local", "config.json");
        var righe = new List<string>();
        var contesto = new List<string>();
        try
        {
            var configService = CostruisciWorkspace(scala, dataRoot, configPath);
            ImpostaMarcatoreIndice(dataRoot, presente: true);
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            var archivio = new LocalTestArchiveQueryService(state.TestCases.LocalIndexDatabasePath!, state.TestCases.IndexRecovery);
            var totale = archivio.QuerySlice(new LocalTestArchiveQuery(), 0, 1).TotalCount;
            var abbinate = state.Master.Componente.Load().Where(e => !e.Archiviata).Select(e => e.Nome)
                .Select(n => (Nome: n, Test: archivio.QuerySlice(new LocalTestArchiveQuery { Componente = n }, 0, 1).TotalCount))
                .Where(x => x.Test > 0).OrderByDescending(x => x.Test).ThenBy(x => x.Nome, StringComparer.Ordinal).ToList();
            var selettive = abbinate.Where(x => x.Test <= totale * 0.05).ToList();
            if (selettive.Count < 2) selettive = abbinate.OrderBy(x => x.Test).ToList();
            if (selettive.Count < 2) throw new InvalidOperationException("Il banco Report richiede almeno due componente con Test.");
            var ecuA = selettive[0].Nome;
            var ecuB = selettive[1].Nome;
            contesto.Add($"Test attivi {totale.ToString(CultureInfo.InvariantCulture)}; componente selettive: {ecuA} ({selettive[0].Test} Test) e " +
                $"{ecuB} ({selettive[1].Test} Test).");

            var vm = new ReportViewModel(state, aggiornaInBackground: true);
            vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult();
            var dataA = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

            righe.Add(MisuraGiroPercorso("Componente selettiva, cambio filtro", vm,
                () => { vm.FiltroEcu = null; vm.DataDa = null; },
                () => vm.FiltroEcu = ecuA));
            righe.Add(MisuraGiroPercorso("data da non selettiva (tutti i Test), cambio filtro", vm,
                () => { vm.FiltroEcu = null; vm.DataDa = null; },
                () => vm.DataDa = dataA));
            righe.Add(MisuraGiroPercorso("tre cambi di componente ravvicinati", vm,
                () => { vm.FiltroEcu = null; vm.DataDa = null; },
                () => { vm.FiltroEcu = ecuA; vm.FiltroEcu = ecuB; vm.FiltroEcu = ecuA; }));
            righe.Add(MisuraGiroPercorso("rivisita Aggiorna() a filtro invariato", vm,
                () => { },
                () => vm.Aggiorna()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Report con calcolo in background, {scala / 1000}k Test, etichetta {etichetta}");
        foreach (var c in contesto) sb.AppendLine($"- {c}");
        sb.AppendLine();
        sb.AppendLine("| Percorso | blocco p50 ms | blocco p95 ms | dati p50 ms | dati p95 ms | ricalcoli p50 | firma |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach (var r in righe) sb.AppendLine(r);
        var md = sb.ToString();
        _output.WriteLine(md);
        if (radiceRepo == null)
        {
            _output.WriteLine("ARTEFATTI|radice_repo_non_trovata=true|scritti=false");
            return;
        }
        var suffisso = $"{scala / 1000}k";
        var cartellaPerf = Path.Combine(radiceRepo, ".ai", "artifacts", "perf");
        Directory.CreateDirectory(cartellaPerf);
        File.WriteAllText(Path.Combine(cartellaPerf, $"report-giro-{etichetta}-{suffisso}.md"), md, Encoding.UTF8);
        _output.WriteLine($"ARTEFATTI|cartella={cartellaPerf}|etichetta={etichetta}|scala={suffisso}");
    }

    /// <summary>Un percorso del banco giri. Warm-up non misurato, poi CampioniMisurati campioni: ogni campione riporta il
    /// vm allo stato neutro (fuori dalla misura), cronometra la sequenza di assegnazioni (blocco del chiamante), attende i
    /// dati applicati (dati) e conta le letture catalogo fatte nel frattempo (ricalcoli = letture / 3). Firma fuori misura.</summary>
    private string MisuraGiroPercorso(string percorso, ReportViewModel vm, Action ripristino, Action sequenza)
    {
        ripristino();
        sequenza();
        vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult();
        var blocco = new double[CampioniMisurati];
        var dati = new double[CampioniMisurati];
        var ricalcoli = new double[CampioniMisurati];
        var firma = "";
        for (var i = 0; i < CampioniMisurati; i++)
        {
            ripristino();
            vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult();
            StabilizzaGc();
            var letturePrima = PerfContatori.LeggiSnapshot().MasterLoad;
            var sw = Stopwatch.StartNew();
            sequenza();
            blocco[i] = sw.Elapsed.TotalMilliseconds;
            vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult();
            dati[i] = sw.Elapsed.TotalMilliseconds;
            ricalcoli[i] = (PerfContatori.LeggiSnapshot().MasterLoad - letturePrima) / 3d;
            firma = FirmaReport(vm);
        }
        return $"| {percorso} | {F(Percentile(blocco, 0.5))} | {F(Percentile(blocco, 0.95))} | {F(Percentile(dati, 0.5))} | " +
            $"{F(Percentile(dati, 0.95))} | {F(Percentile(ricalcoli, 0.5))} | {firma} |";
    }

    /// <summary>Percentile con il metodo "nearest rank" sui campioni (p50 e p95 con pochi campioni).</summary>
    private static double Percentile(double[] valori, double p)
    {
        var ordinati = valori.OrderBy(v => v).ToArray();
        var indice = Math.Clamp((int)Math.Ceiling(p * ordinati.Length) - 1, 0, ordinati.Length - 1);
        return ordinati[indice];
    }

    /// <summary>Firma del risultato visibile del Report: anteprima (ordine e campi), conteggio, colonne, celle della
    /// finestra di matrice e totale generale. Stessa firma = stessi dati: serve al confronto prima/dopo sullo stesso banco.</summary>
    private static string FirmaReport(ReportViewModel vm)
    {
        var sb = new StringBuilder();
        sb.Append(FormattableString.Invariant($"anteprima={vm.TotaleRisultatiAnteprima};"));
        foreach (var r in vm.Anteprima)
            sb.Append(FormattableString.Invariant($"[{r.Id}:{r.Componente}:{r.Veicolo}:{r.Template}:{r.Esito}:{r.Data}]"));
        sb.Append(FormattableString.Invariant($";colonne={string.Join('|', vm.ColonneMatrice)};"));
        foreach (var riga in vm.RigheMatrice)
            foreach (var cella in riga.Celle)
                sb.Append(FormattableString.Invariant($"[{riga.Riga}:{cella.Colonna}:{cella.Conformi}/{cella.Valutati}/{cella.Totali}:{cella.NonApplicabile}]"));
        sb.Append(System.Text.Json.JsonSerializer.Serialize(vm.TotaleGeneraleMatrice));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>
    /// Apertura di Home a scala media con tutte le sezioni visibili (layout predefinito) e con solo Riepilogo e Recenti
    /// visibili: stessa misura "Home (apertura)" del giro standard (costruzione di HomeViewModel, 1 warm-up + 5 misure,
    /// GC stabilizzato). Attiva solo con TestLoom_PERF_HOME_SEZIONI=1, quindi il giro standard non cambia. La preferenza di
    /// layout del secondo percorso vive nel config del workspace temporaneo, rimosso a fine test.
    /// Output: .ai/artifacts/perf/home-sezioni-{etichetta}-{N}k.md e .tsv. TestLoom_PERF_MEDIA_SCALE e TestLoom_PERF_MEDIA_LABEL come nel giro standard.
    /// </summary>
    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public void MisuraHomeSezioniRidotteScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_HOME_SEZIONI"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;

        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radiceRepo = TrovaRadiceRepo();
        var baseTemporanea = radiceRepo != null
            ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-media")
            : Path.Combine(Path.GetTempPath(), "TestLoom_PerfMediaScala");
        var root = Path.Combine(baseTemporanea, $"home-sezioni-{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var configPath = Path.Combine(root, "local", "config.json");
        var righe = new List<RigaPerf>();
        var contesto = new List<string>();
        try
        {
            var configService = CostruisciWorkspace(scala, dataRoot, configPath);
            // Come nel giro standard: marcatore presente, cioe' il percorso di produzione con riuso degli hash.
            ImpostaMarcatoreIndice(dataRoot, presente: true);
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            _ = state.TestCases.Count();

            // Apertura come la vive la UI (aggiornaInBackground): due righe per percorso, blocco del thread e fino a dati applicati.
            var (bloccoTutte, finoTutte) = MisuraAperturaHome("Home (apertura) - tutte le sezioni",
                () => new HomeViewModel(state, _ => { }, aggiornaInBackground: true));
            righe.Add(bloccoTutte);
            righe.Add(finoTutte);

            // Preferenza personale di layout: solo Riepilogo e Recenti visibili, salvata nel config di questo workspace.
            var impostazione = new HomeViewModel(state, _ => { });
            foreach (var id in HomeSezioni.Predefinite)
                if (id != HomeSezioni.Riepilogo && id != HomeSezioni.Recenti) impostazione.ImpostaSezioneVisibile(id, false);
            contesto.Add("Sezioni visibili nel secondo percorso: " +
                string.Join(", ", impostazione.OrdineSezioni.Where(id => impostazione.SezioneVisibile(id))) + ".");
            var (bloccoRidotte, finoRidotte) = MisuraAperturaHome("Home (apertura) - solo riepilogo+recenti",
                () => new HomeViewModel(state, _ => { }, aggiornaInBackground: true));
            righe.Add(bloccoRidotte);
            righe.Add(finoRidotte);
            impostazione.RipristinaLayoutPredefinitoCommand.Execute(null);
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
        File.WriteAllText(Path.Combine(cartellaPerf, $"home-sezioni-{etichetta}-{suffisso}.md"), md, Encoding.UTF8);
        File.WriteAllText(Path.Combine(cartellaPerf, $"home-sezioni-{etichetta}-{suffisso}.tsv"), ComponiTsv(righe), Encoding.UTF8);
        _output.WriteLine($"ARTEFATTI|cartella={cartellaPerf}|etichetta={etichetta}|scala={suffisso}");
    }

    /// <summary>
    /// Workspace sintetico alla scala richiesta: base realistica (SeedDemoDataService), cataloghi a 1.000 voci e
    /// RevisioniUdt a N voci tramite i repository reali, Test/NC/CR scritti con i generatori condivisi, purga della cache
    /// locale e bootstrap finale che costruisce gli indici SQLite usati dal percorso a caldo.
    /// </summary>
    private AppConfigService CostruisciWorkspace(int scala, string dataRoot, string configPath)
    {
        var swTotale = Stopwatch.StartNew();

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
        var crTarget = Math.Max(1, scala / 50);

        var testIds = BancoPerf100kGeneratoreTests.CreaTestRealistici(paths, Math.Max(0, scala - testAttivi), testEsistenti,
            ecuNomi, veicoliNomi, funzionalitaNomi, requisitoId, revisioniUdtChiavi);
        BancoPerf100kGeneratoreTests.CreaNonConformitaRealistiche(paths, Math.Max(0, scala - ncAttive), ncEsistenti, testIds);
        BancoPerf100kGeneratoreTests.CreaChangeRequestRealistiche(paths, Math.Max(0, crTarget - crEsistenti), crEsistenti, testIds);

        // Stessa purga del generatore prima del bootstrap finale: senza, AppState riprenderebbe la fotografia locale
        // costruita sulla base demo e non vedrebbe i Test/NC appena scritti.
        BancoPerf100kGeneratoreTests.PurgaCacheWorkspaceLocale(dataRoot);

        var swBootstrap = Stopwatch.StartNew();
        using (var statoFinale = new AppState(configService, runBackgroundMaintenance: false))
        {
            var testCount = statoFinale.TestCases.Count();
            var ncCount = statoFinale.NonConformita.CountAll;
            swBootstrap.Stop();
            _output.WriteLine($"FASE|nome=bootstrap_freddo|scala={scala}|durata_ms={swBootstrap.ElapsedMilliseconds}|test={testCount}|nc={ncCount}");
            Assert.True(testCount >= scala, $"Test attivi attesi >= {scala}, letti {testCount}.");
            Assert.True(ncCount >= scala, $"NC attive attese >= {scala}, lette {ncCount}.");
        }

        // Operatore di sessione come nel banco PerfInProcessPagineTests: esiste nella base demo.
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Operatore Prova A" });
        _output.WriteLine($"FASE|nome=costruzione_workspace|scala={scala}|durata_ms={swTotale.ElapsedMilliseconds}");
        return configService;
    }

    /// <summary>
    /// Diagnostica una tantum (non in tabella): tempo di ciascuna fase del bootstrap a caldo, con un sink sincrono
    /// per IProgress&lt;BootstrapProgress&gt;.
    /// </summary>
    private void RegistraTimelineBootstrap(int scala, AppConfigService configService)
    {
        var fasi = new List<string>();
        var sink = new ProgressSincrono(p => fasi.Add($"{p.Fase}@{p.Trascorso.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}"));
        using (var stato = new AppState(configService, runBackgroundMaintenance: false, progress: sink))
        {
            _ = stato.TestCases.Count();
        }
        _output.WriteLine($"TIMELINE|scala={scala}|{string.Join(" > ", fasi)}");
    }

    /// <summary>
    /// Verifica dell'invariante che rende lecito il riuso dell'hash memorizzato: per ogni riga dell'indice l'hash
    /// salvato deve coincidere con il ricalcolo sul Test caricato (stessa normalizzazione del bootstrap).
    /// </summary>
    private string VerificaFingerprintMemorizzati(int scala, string dataRoot)
    {
        var indice = new LocalTestIndexStore(dataRoot);
        var elementi = indice.LoadAll();
        var difformi = 0;
        foreach (var e in elementi)
            if (!string.Equals(e.ContentHash, TestCaseFingerprint.Compute(e.TestCase), StringComparison.OrdinalIgnoreCase))
                difformi++;
        var marcatore = indice.HashIndicizzatiRiusabili;
        _output.WriteLine($"FINGERPRINT|scala={scala}|righe_indice={elementi.Count}|difformi={difformi}|marcatore_riusabile={marcatore}");
        return $"Righe indice SQLite: {elementi.Count}; fingerprint memorizzati uguali al ricalcolo: {elementi.Count - difformi}/{elementi.Count}; " +
            $"marcatore di riuso dell'algoritmo: {(marcatore ? "presente" : "assente")}.";
    }

    /// <summary>Imposta il marcatore di algoritmo dell'indice Test (meta content_hash_algo): presente = riuso degli hash,
    /// assente = ricalcolo come prima della correzione. Nessun'altra riga dell'indice viene toccata.</summary>
    private static void ImpostaMarcatoreIndice(string dataRoot, bool presente)
    {
        var database = new LocalTestIndexStore(dataRoot).DatabasePath;
        using var connessione = new SqliteConnection($"Data Source={database}");
        connessione.Open();
        using var comando = connessione.CreateCommand();
        if (presente)
        {
            comando.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES('content_hash_algo', $valore);";
            comando.Parameters.AddWithValue("$valore", TestCaseFingerprint.VersioneAlgoritmo);
        }
        else comando.CommandText = "DELETE FROM meta WHERE key = 'content_hash_algo';";
        comando.ExecuteNonQuery();
    }

    /// <summary>1 warm-up non cronometrato, poi 5 misure. GC stabilizzato prima di ogni misura, fuori dal cronometro.
    /// I contatori PerfContatori sono azzerati prima di ogni misura e letti subito dopo: riflettono il singolo giro.
    /// <paramref name="dopo"/> (es. Dispose) viene eseguito sempre fuori dal cronometro.</summary>
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
            // Pausa GC del processo durante la sola operazione cronometrata (GC indotte dalle allocazioni del percorso).
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

    /// <summary>Apertura di Home come la vive la UI, due tempi per giro (1 warm-up + 5 misure, GC stabilizzato):
    /// "blocco" = quanto resta occupato il thread che costruisce la Home, senza attendere i dati; "fino a dati applicati" =
    /// costruzione + attesa dei dati (<see cref="HomeViewModel.AttendiAggiornamentoAsync"/>). I contatori sono letti dopo
    /// l'attesa e valgono per l'intero giro. Con il calcolo sincrono i due tempi coincidono.</summary>
    private (RigaPerf Blocco, RigaPerf FinoADati) MisuraAperturaHome(string percorso, Func<HomeViewModel> crea)
    {
        var warmUp = crea();
        warmUp.AttendiAggiornamentoAsync().GetAwaiter().GetResult();
        Scarta(warmUp);

        var blocco = new double[CampioniMisurati];
        var fino = new double[CampioniMisurati];
        var pauseGc = new double[CampioniMisurati];
        PerfContatori.Snapshot contatori = default;
        var wsBase = MemoriaWorkingSetMb();
        for (var i = 0; i < CampioniMisurati; i++)
        {
            StabilizzaGc();
            PerfContatori.Reset();
            var pausaPrima = GC.GetTotalPauseDuration();
            var sw = Stopwatch.StartNew();
            var vm = crea();
            blocco[i] = sw.Elapsed.TotalMilliseconds;
            vm.AttendiAggiornamentoAsync().GetAwaiter().GetResult();
            fino[i] = sw.Elapsed.TotalMilliseconds;
            pauseGc[i] = (GC.GetTotalPauseDuration() - pausaPrima).TotalMilliseconds;
            contatori = PerfContatori.LeggiSnapshot();
            Scarta(vm);
        }
        var deltaWs = MemoriaWorkingSetMb() - wsBase;
        return (RigaDaCampioni(percorso + " - blocco thread UI", blocco, deltaWs, contatori, pauseGc),
            RigaDaCampioni(percorso + " - fino a dati applicati", fino, deltaWs, contatori, pauseGc));
    }

    private RigaPerf MisuraAzione(string percorso, Action operazione) =>
        Misura<int>(percorso, () => { operazione(); return 0; }, _ => { });

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
        sb.AppendLine($"# Misure prestazioni scala media — {etichetta} — {scala} Test — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine($"Dataset: Test={scala}, NC={scala}, CR={Math.Max(1, scala / 50)}, RevisioniUdt={scala}, " +
            $"cataloghi={BancoScaleSeedCore.TargetCatalogoDefault} voci. Ambiente: {RuntimeInformation.OSDescription}, " +
            $"{Environment.ProcessorCount} thread logici, {RuntimeInformation.FrameworkDescription}, memoria disponibile al GC " +
            $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0:F1} GB.");
        sb.AppendLine();
        sb.AppendLine("1 warm-up non cronometrato + 5 misure per percorso; p50 = mediana delle 5 misure. " +
            "ΔWS = working set dopo le 5 misure meno quello dopo il warm-up. Contatori = ultimo giro misurato.");
        foreach (var riga in contesto)
        {
            sb.AppendLine();
            sb.AppendLine(riga);
        }
        sb.AppendLine();
        sb.AppendLine("| Percorso | p50 (ms) | max (ms) | Pausa GC p50 (ms) | ΔWS (MB) | Fingerprint ricalcolati | Fingerprint riusati | Load master | Refresh Phase D (n / ms) | Refresh accorpati | Test GetAll | campioni (ms) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var riga in righe)
        {
            var c = riga.Contatori;
            sb.AppendLine($"| {riga.Percorso} | {F(riga.P50)} | {F(riga.Max)} | {F(riga.GcPausaP50Ms)} | " +
                $"{riga.DeltaWsMb.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)} | " +
                $"{c.FingerprintCalcolati} | {c.FingerprintRiusati} | {c.MasterLoad} | " +
                $"{c.PhaseDRefreshEseguiti} / {F(c.PhaseDRefreshMs)} | {c.PhaseDRefreshAccorpati} | {c.TestCaseGetAll} | " +
                $"{string.Join("; ", riga.Campioni.Select(F))} |");
        }
        return sb.ToString();
    }

    private static string ComponiTsv(IReadOnlyList<RigaPerf> righe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("percorso\tp50_ms\tmax_ms\tgc_pausa_p50_ms\tdelta_ws_mb\tfingerprint_calcolati\tfingerprint_riusati\tmaster_load\tphased_refresh_n\tphased_refresh_ms\tphased_refresh_accorpati\ttest_getall");
        foreach (var riga in righe)
        {
            var c = riga.Contatori;
            sb.AppendLine(string.Join("\t", riga.Percorso, F(riga.P50), F(riga.Max), F(riga.GcPausaP50Ms),
                riga.DeltaWsMb.ToString("F1", CultureInfo.InvariantCulture), c.FingerprintCalcolati, c.FingerprintRiusati,
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

    /// <summary>Sink sincrono per IProgress: Progress&lt;T&gt; pubblicherebbe sul SynchronizationContext, qui assente.</summary>
    private sealed class ProgressSincrono : IProgress<BootstrapProgress>
    {
        private readonly Action<BootstrapProgress> _azione;
        public ProgressSincrono(Action<BootstrapProgress> azione) => _azione = azione;
        public void Report(BootstrapProgress value) => _azione(value);
    }
}
