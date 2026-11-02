using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Tests.Perf;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>
/// Banco di misura della riconciliazione dell'indice locale (Test, NC, CR) per il ciclo prestazioni 6.6.1 (wt-p661-avvio).
///
/// Gating: TestLoom_PERF_RICONCILIAZIONE=1 (senza variabile il test esce subito, come gli altri banchi). TestLoom_PERF_RICON_SCALE
/// limita il giro a una scala (5000, 20000, 100000). TestLoom_PERF_RICON_LABEL dà l'etichetta ai file (default "corrente").
///
/// Solo misura: nessuna asserzione sui tempi. Ogni riga usa l'API dei repository (cold build, avvio a caldo, riconciliazione
/// profonda, refresh incrementale). Le righe "di cui" ripetono a parte i passi del percorso reale (enumerazione dei file,
/// lettura/parsing JSON in parallelo, fingerprint sequenziale, ReconcileFull su SQLite) per attribuire il tempo.
/// Dataset sintetico generato dai generatori condivisi; workspace sotto .ai/tmp della radice del repository, rimosso a fine test.
/// Output: .ai/artifacts/perf/riconciliazione-{etichetta}-{N}k.md e .tsv (cartella non versionata).
/// A 100k il numero di campioni e' 1 (misura finale una tantum).
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfRiconciliazioneIndiceTests
{
    private static readonly int[] DimensioniRefreshPaths = { 1, 10, 100 };

    private readonly ITestOutputHelper _output;
    public PerfRiconciliazioneIndiceTests(ITestOutputHelper output) => _output = output;

    private sealed record Riga(string Misura, double[] Valori, string Nota);

    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    [InlineData(100_000)]
    public void MisuraRiconciliazioneIndice(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_RICONCILIAZIONE"), "1", StringComparison.Ordinal))
        {
            _output.WriteLine("TestLoom_PERF_RICONCILIAZIONE non impostata a 1: banco escluso (non eseguito).");
            return;
        }
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_RICON_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;

        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_RICON_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var campioni = scala >= 100_000 ? 1 : 3;
        var radiceRepo = TrovaRadiceRepo();
        var baseTemporanea = radiceRepo != null
            ? Path.Combine(radiceRepo, ".ai", "tmp", "perf-ricon")
            : Path.Combine(Path.GetTempPath(), "TestLoom_PerfRicon");
        var root = Path.Combine(baseTemporanea, $"{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var righe = new List<Riga>();
        var contesto = new List<string>();
        try
        {
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            var sw = Stopwatch.StartNew();
            CreaDataset(paths, scala);
            contesto.Add($"Dataset generato in {sw.Elapsed.TotalSeconds:F1} s: {scala:N0} Test, {scala:N0} NC, {Math.Max(1, scala / 50):N0} CR. Campioni per riga: {campioni}.");
            contesto.Add($"Processori logici: {Environment.ProcessorCount}; grado di parallelismo lettura JSON: {Math.Clamp(Environment.ProcessorCount, 2, 8)}.");

            MisuraTest(paths, dataRoot, Path.Combine(root, "local-test"), scala, campioni, righe, contesto);
            // TestLoom_PERF_RICON_SOLO_TEST=1 salta NC e CR (il cold build dei Test si misura da solo, piu' in fretta).
            if (Environment.GetEnvironmentVariable("TestLoom_PERF_RICON_SOLO_TEST") != "1")
            {
                MisuraNc(paths, dataRoot, Path.Combine(root, "local-nc"), scala, campioni, righe);
                MisuraCr(paths, Path.Combine(root, "local-cr"), scala, campioni, righe);
            }
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
        var cartellaPerf = Path.Combine(radiceRepo, ".ai", "artifacts", "perf");
        Directory.CreateDirectory(cartellaPerf);
        var suffisso = $"{scala / 1000}k";
        File.WriteAllText(Path.Combine(cartellaPerf, $"riconciliazione-{etichetta}-{suffisso}.md"), md, Encoding.UTF8);
        File.WriteAllText(Path.Combine(cartellaPerf, $"riconciliazione-{etichetta}-{suffisso}.tsv"), ComponiTsv(righe), Encoding.UTF8);
        _output.WriteLine($"ARTEFATTI|cartella={cartellaPerf}|etichetta={etichetta}|scala={suffisso}");
    }

    // ---------------------------------------------------------------- Test (TestCaseRepository / LocalTestIndexStore)

    private static void MisuraTest(DataPaths paths, string dataRoot, string localBase, int scala, int campioni,
        List<Riga> righe, List<string> contesto)
    {
        // Cold build: indice vuoto a ogni campione. RefreshIndex legge tutti i JSON e fa ReconcileFull.
        var freddo = new List<double>();
        long fingerprintFreddo = 0;
        for (var i = 0; i < campioni; i++)
        {
            Svuota(localBase);
            var repo = new TestCaseRepository(paths, new LocalTestIndexStore(dataRoot, localBase));
            var fp0 = PerfContatori.LeggiSnapshot().FingerprintCalcolati;
            freddo.Add(Cronometra(() => repo.RefreshIndex()));
            fingerprintFreddo = PerfContatori.LeggiSnapshot().FingerprintCalcolati - fp0;
            Assert.Equal(scala, repo.GetAll().Count);
        }
        righe.Add(new Riga("Test - cold build (RefreshIndex su indice vuoto)", freddo.ToArray(),
            $"fingerprint ricalcolati={fingerprintFreddo:N0}"));

        // Avvio a caldo: costruzione del repository sullo snapshot gia' presente (LoadAll + riuso hash + lookup).
        var caldo = new List<double>();
        long fingerprintCaldo = 0;
        for (var i = 0; i < campioni; i++)
        {
            TestCaseRepository? repo = null;
            var fp0 = PerfContatori.LeggiSnapshot().FingerprintCalcolati;
            caldo.Add(Cronometra(() => repo = new TestCaseRepository(paths, new LocalTestIndexStore(dataRoot, localBase))));
            fingerprintCaldo = PerfContatori.LeggiSnapshot().FingerprintCalcolati - fp0;
            Assert.Equal(scala, repo!.GetAll().Count);
        }
        righe.Add(new Riga("Test - avvio a caldo (costruzione repository su snapshot)", caldo.ToArray(),
            $"fingerprint ricalcolati={fingerprintCaldo:N0}"));

        var letturaRighe = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            var store = new LocalTestIndexStore(dataRoot, localBase);
            IReadOnlyList<LocalIndexedTest>? elementi = null;
            letturaRighe.Add(Cronometra(() => elementi = store.LoadAll()));
            Assert.Equal(scala, elementi!.Count);
        }
        righe.Add(new Riga("  di cui: LoadAll (righe SQLite + parse JSON in parallelo)", letturaRighe.ToArray(), ""));

        // Riconciliazione profonda su indice invariato: RefreshIndex rilegge tutti i JSON e riscrive le righe.
        var profonda = new List<double>();
        long fingerprintProfonda = 0;
        for (var i = 0; i < campioni; i++)
        {
            var repo = new TestCaseRepository(paths, new LocalTestIndexStore(dataRoot, localBase));
            var fp0 = PerfContatori.LeggiSnapshot().FingerprintCalcolati;
            profonda.Add(Cronometra(() => repo.RefreshIndex()));
            fingerprintProfonda = PerfContatori.LeggiSnapshot().FingerprintCalcolati - fp0;
        }
        righe.Add(new Riga("Test - riconciliazione profonda (RefreshIndex su indice invariato)", profonda.ToArray(),
            $"fingerprint ricalcolati={fingerprintProfonda:N0}"));

        // Refresh incrementale: k file modificati sul disco, RefreshPaths sui soli percorsi cambiati.
        var elencoRefresh = ElencaTest(paths);
        var repoIncrementale = new TestCaseRepository(paths, new LocalTestIndexStore(dataRoot, localBase));
        repoIncrementale.RefreshIndex();
        var offset = 0;
        foreach (var k in DimensioniRefreshPaths)
        {
            var valori = new List<double>();
            for (var i = 0; i < campioni; i++)
            {
                var percorsi = new List<string>(k);
                for (var j = 0; j < k; j++)
                {
                    var percorso = elencoRefresh[(offset + j) % elencoRefresh.Count].Path;
                    MutaTest(percorso, $"banco-{k}-{i}-{j}");
                    percorsi.Add(percorso);
                }
                offset += k;
                valori.Add(Cronometra(() => repoIncrementale.RefreshPaths(percorsi)));
            }
            righe.Add(new Riga($"Test - RefreshPaths ({k} file modificati)", valori.ToArray(), ""));
        }

        // Scomposizione della riconciliazione profonda: enumerazione, lettura JSON, post-elaborazione, ReconcileFull.
        var enumerazione = new List<double>();
        List<(string Path, DateTime Mtime, long Length)> elenco = new();
        for (var i = 0; i < campioni; i++)
            enumerazione.Add(Cronometra(() => elenco = ElencaTest(paths)));
        righe.Add(new Riga("  di cui: enumerazione file Test (FileInfo, mtime e lunghezza)", enumerazione.ToArray(),
            $"file={elenco.Count:N0}"));

        var lettura = new List<double>();
        (TestCase? Valore, string? Errore)[] letti = Array.Empty<(TestCase?, string?)>();
        for (var i = 0; i < campioni; i++)
        {
            var percorsi = elenco.Select(e => e.Path).ToList();
            lettura.Add(Cronometra(() => letti = LeggiJsonParallelo<TestCase>(percorsi)));
        }
        righe.Add(new Riga("  di cui: lettura+parse JSON Test (parallela, come RefreshIndex)", lettura.ToArray(),
            $"letti={letti.Count(x => x.Valore != null):N0}, errori={letti.Count(x => x.Valore == null):N0}"));

        // Post-elaborazione sequenziale per Test (NormalizzaVeicoli + MigraInLettura + Stamp), come nel ciclo di RefreshIndex.
        var validi = new List<(string Path, TestCase Tc)>();
        for (var i = 0; i < elenco.Count; i++)
            if (letti[i].Valore is { } tcLetto) validi.Add((elenco[i].Path, tcLetto));
        var postElaborazione = new List<double>();
        for (var i = 0; i < campioni; i++)
            postElaborazione.Add(Cronometra(() =>
            {
                foreach (var (percorso, tc) in validi)
                {
                    tc.NormalizzaVeicoli();
                    TestCaseLegacyMigrations.MigraInLettura(tc, percorso);
                    TestCaseFingerprint.Stamp(tc);
                }
            }));
        righe.Add(new Riga("  di cui: post-elaborazione sequenziale (Normalizza+Migra+Stamp)", postElaborazione.ToArray(), ""));

        var voci = new List<LocalIndexWriteEntry>(validi.Count);
        for (var i = 0; i < elenco.Count; i++)
        {
            var tc = letti[i].Valore;
            if (tc == null) continue;
            voci.Add(new LocalIndexWriteEntry(elenco[i].Path, elenco[i].Mtime, elenco[i].Length, tc,
                TestCaseFingerprint.GetStored(tc)));
        }

        // ReconcileFull su indice gia' popolato con gli stessi dati (il percorso profondo lo esegue sempre).
        var storeCaldo = new LocalTestIndexStore(dataRoot, localBase);
        var riconciliazioneCaldo = new List<double>();
        for (var i = 0; i < campioni; i++)
            riconciliazioneCaldo.Add(Cronometra(() => storeCaldo.ReconcileFull(voci, DateTime.UtcNow)));
        righe.Add(new Riga("  di cui: ReconcileFull Test su indice gia' allineato (SQLite)", riconciliazioneCaldo.ToArray(), ""));

        var riconciliazioneFreddo = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            var localVuoto = Path.Combine(Path.GetDirectoryName(localBase)!, $"local-test-vuoto-{i}");
            Svuota(localVuoto);
            var storeVuoto = new LocalTestIndexStore(dataRoot, localVuoto);
            riconciliazioneFreddo.Add(Cronometra(() => storeVuoto.ReconcileFull(voci, DateTime.UtcNow)));
        }
        righe.Add(new Riga("  di cui: ReconcileFull Test da indice vuoto (SQLite, proiezioni complete)", riconciliazioneFreddo.ToArray(), ""));
    }

    // ---------------------------------------------------------------- NC (NonConformitaRepository / LocalNonConformitaIndexStore)

    private static void MisuraNc(DataPaths paths, string dataRoot, string localBase, int scala, int campioni, List<Riga> righe)
    {
        var freddo = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            Svuota(localBase);
            var repo = new NonConformitaRepository(paths, null, new LocalNonConformitaIndexStore(dataRoot, localBase));
            freddo.Add(Cronometra(() => repo.RefreshIndex()));
            Assert.Equal(scala, repo.CountAll);
        }
        righe.Add(new Riga("NC - cold build (RefreshIndex su indice vuoto)", freddo.ToArray(), ""));

        var caldo = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            NonConformitaRepository? repo = null;
            caldo.Add(Cronometra(() => repo = new NonConformitaRepository(paths, null, new LocalNonConformitaIndexStore(dataRoot, localBase))));
            Assert.Equal(scala, repo!.CountAll);
        }
        righe.Add(new Riga("NC - avvio a caldo (costruzione repository su snapshot)", caldo.ToArray(), ""));

        var letturaRighe = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            var store = new LocalNonConformitaIndexStore(dataRoot, localBase);
            IReadOnlyList<LocalIndexedNc>? elementi = null;
            letturaRighe.Add(Cronometra(() => elementi = store.LoadAll()));
            Assert.Equal(scala, elementi!.Count);
        }
        righe.Add(new Riga("  di cui: LoadAll (righe SQLite + parse JSON in parallelo)", letturaRighe.ToArray(), ""));

        // Primo RefreshIndex di un processo (come all'avvio di AppState) e successivo (come il giro periodico di RileggiDaDisco).
        var primo = new List<double>();
        var successivo = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            var repo = new NonConformitaRepository(paths, null, new LocalNonConformitaIndexStore(dataRoot, localBase));
            primo.Add(Cronometra(() => repo.RefreshIndex()));
            successivo.Add(Cronometra(() => repo.RefreshIndex()));
        }
        righe.Add(new Riga("NC - primo RefreshIndex del processo (avvio, validazione profonda)", primo.ToArray(), "legge tutti i JSON"));
        righe.Add(new Riga("NC - RefreshIndex successivo, stesso processo (ramo non profondo)", successivo.ToArray(), ""));

        var repoIncrementale = new NonConformitaRepository(paths, null, new LocalNonConformitaIndexStore(dataRoot, localBase));
        repoIncrementale.RefreshIndex();
        var elencoRefresh = ElencaNc(paths);
        var offset = 0;
        foreach (var k in DimensioniRefreshPaths)
        {
            var valori = new List<double>();
            for (var i = 0; i < campioni; i++)
            {
                var percorsi = new List<string>(k);
                for (var j = 0; j < k; j++)
                {
                    var percorso = elencoRefresh[(offset + j) % elencoRefresh.Count].Path;
                    MutaNc(percorso, $"banco-{k}-{i}-{j}");
                    percorsi.Add(percorso);
                }
                offset += k;
                valori.Add(Cronometra(() => repoIncrementale.RefreshPaths(percorsi)));
            }
            righe.Add(new Riga($"NC - RefreshPaths ({k} file modificati)", valori.ToArray(), ""));
        }

        // Scomposizione della validazione profonda: enumerazione, lettura JSON, fingerprint sequenziale, ReconcileFull.
        var enumerazione = new List<double>();
        List<(string Path, DateTime Mtime, long Length)> elenco = new();
        for (var i = 0; i < campioni; i++)
            enumerazione.Add(Cronometra(() => elenco = ElencaNc(paths)));
        righe.Add(new Riga("  di cui: enumerazione file NC (FileInfo, mtime e lunghezza)", enumerazione.ToArray(), $"file={elenco.Count:N0}"));

        var lettura = new List<double>();
        (NonConformita? Valore, string? Errore)[] letti = Array.Empty<(NonConformita?, string?)>();
        for (var i = 0; i < campioni; i++)
        {
            var percorsi = elenco.Select(e => e.Path).ToList();
            lettura.Add(Cronometra(() => letti = LeggiJsonParallelo<NonConformita>(percorsi)));
        }
        righe.Add(new Riga("  di cui: lettura+parse JSON NC (parallela, come RefreshIndex)", lettura.ToArray(),
            $"letti={letti.Count(x => x.Valore != null):N0}, errori={letti.Count(x => x.Valore == null):N0}"));

        var voci = new List<LocalIndexWriteEntryNc>(elenco.Count);
        var fingerprint = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            fingerprint.Add(Cronometra(() =>
            {
                voci.Clear();
                for (var j = 0; j < elenco.Count; j++)
                {
                    var nc = letti[j].Valore;
                    if (nc == null) continue;
                    voci.Add(new LocalIndexWriteEntryNc(elenco[j].Path, elenco[j].Mtime, elenco[j].Length, nc,
                        EntityStateFingerprint.Compute(nc)));
                }
            }));
        }
        righe.Add(new Riga("  di cui: fingerprint NC sequenziale (EntityStateFingerprint.Compute su tutti)", fingerprint.ToArray(), ""));

        var riconciliazioneVuoto = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            var localVuoto = Path.Combine(Path.GetDirectoryName(localBase)!, $"local-nc-vuoto-{i}");
            Svuota(localVuoto);
            var storeVuoto = new LocalNonConformitaIndexStore(dataRoot, localVuoto);
            riconciliazioneVuoto.Add(Cronometra(() => storeVuoto.ReconcileFull(voci, DateTime.UtcNow)));
        }
        righe.Add(new Riga("  di cui: ReconcileFull NC da indice vuoto (SQLite)", riconciliazioneVuoto.ToArray(), ""));
    }

    // ---------------------------------------------------------------- CR (ChangeRequestRepository)

    private static void MisuraCr(DataPaths paths, string localBase, int scala, int campioni, List<Riga> righe)
    {
        var numeroCr = Math.Max(1, scala / 50);
        var freddo = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            Svuota(localBase);
            var repo = new ChangeRequestRepository(paths, null, localBase);
            freddo.Add(Cronometra(() => repo.RefreshIndex()));
        }
        righe.Add(new Riga($"CR ({numeroCr:N0} record) - cold build (RefreshIndex su indice vuoto)", freddo.ToArray(), ""));

        var caldo = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            ChangeRequestRepository? repo = null;
            caldo.Add(Cronometra(() => repo = new ChangeRequestRepository(paths, null, localBase)));
            _ = repo!.Count(includeArchived: true);
        }
        righe.Add(new Riga($"CR ({numeroCr:N0} record) - avvio a caldo (costruzione repository su snapshot)", caldo.ToArray(), ""));

        var primo = new List<double>();
        var successivo = new List<double>();
        for (var i = 0; i < campioni; i++)
        {
            var repo = new ChangeRequestRepository(paths, null, localBase);
            primo.Add(Cronometra(() => repo.RefreshIndex()));
            successivo.Add(Cronometra(() => repo.RefreshIndex()));
        }
        righe.Add(new Riga("CR - primo RefreshIndex del processo", primo.ToArray(), ""));
        righe.Add(new Riga("CR - RefreshIndex successivo, stesso processo", successivo.ToArray(), ""));
    }

    // ---------------------------------------------------------------- utilita' di misura e scrittura

    internal static void CreaDataset(DataPaths paths, int scala)
    {
        var componente = Enumerable.Range(0, 200).Select(i => $"Componente-{i:D3}").ToList();
        var veicoli = Enumerable.Range(0, 100).Select(i => $"Veicolo-{i:D3}").ToList();
        var funzionalita = Enumerable.Range(0, 100).Select(i => $"Funzione-{i:D3}").ToList();
        var requisiti = Enumerable.Range(0, 500).Select(i => $"REQ-SCALE-{i:D4}").ToList();
        var revisioniUdt = Enumerable.Range(0, 1000).Select(BancoScaleSeedCore.ChiaveRevisioneUdtScale).ToList();
        var testIds = BancoPerf100kGeneratoreTests.CreaTestRealistici(paths, scala, 0, componente, veicoli, funzionalita, requisiti, revisioniUdt);
        BancoPerf100kGeneratoreTests.CreaNonConformitaRealistiche(paths, scala, 0, testIds);
        BancoPerf100kGeneratoreTests.CreaChangeRequestRealistiche(paths, Math.Max(1, scala / 50), 0, testIds);
    }

    internal static List<(string Path, DateTime Mtime, long Length)> ElencaTest(DataPaths paths)
    {
        var elenco = new List<(string Path, DateTime Mtime, long Length)>();
        foreach (var file in new DirectoryInfo(paths.TestCasesDir).EnumerateFiles("testcase_*.json", SearchOption.AllDirectories))
            elenco.Add((file.FullName, file.LastWriteTimeUtc, file.Length));
        return elenco;
    }

    private static List<(string Path, DateTime Mtime, long Length)> ElencaNc(DataPaths paths)
    {
        var elenco = new List<(string Path, DateTime Mtime, long Length)>();
        foreach (var file in new DirectoryInfo(paths.NonConformitaDir).EnumerateFiles("nc_*.json"))
            elenco.Add((file.FullName, file.LastWriteTimeUtc, file.Length));
        return elenco;
    }

    private static (T? Valore, string? Errore)[] LeggiJsonParallelo<T>(IReadOnlyList<string> percorsi) where T : class
    {
        var risultati = new (T? Valore, string? Errore)[percorsi.Count];
        var opzioni = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) };
        Parallel.For(0, percorsi.Count, opzioni, i =>
        {
            var valore = JsonIo.TryRead<T>(percorsi[i], out var errore);
            risultati[i] = (valore, errore);
        });
        return risultati;
    }

    internal static void MutaTest(string percorso, string marcatura)
    {
        var tc = JsonIo.TryRead<TestCase>(percorso, out var errore)
            ?? throw new InvalidOperationException($"Lettura Test fallita: {errore}");
        tc.DescrizioneBreve = marcatura;
        File.WriteAllBytes(percorso, JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options));
    }

    private static void MutaNc(string percorso, string marcatura)
    {
        var nc = JsonIo.TryRead<NonConformita>(percorso, out var errore)
            ?? throw new InvalidOperationException($"Lettura NC fallita: {errore}");
        nc.Descrizione = marcatura;
        File.WriteAllBytes(percorso, JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options));
    }

    private static void Svuota(string cartella)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(cartella)) Directory.Delete(cartella, recursive: true);
        Directory.CreateDirectory(cartella);
    }

    private static double Cronometra(Action azione)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var sw = Stopwatch.StartNew();
        azione();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double Mediana(double[] valori)
    {
        var ordinati = valori.OrderBy(x => x).ToArray();
        var n = ordinati.Length;
        if (n == 0) return 0;
        return n % 2 == 1 ? ordinati[n / 2] : (ordinati[n / 2 - 1] + ordinati[n / 2]) / 2.0;
    }

    private static string F(double valore) => valore.ToString("F1", CultureInfo.InvariantCulture);

    private static string ComponiMarkdown(int scala, string etichetta, IReadOnlyList<Riga> righe, IReadOnlyList<string> contesto)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Riconciliazione indice locale - {etichetta} - {scala:N0}");
        sb.AppendLine();
        foreach (var riga in contesto) sb.AppendLine($"- {riga}");
        sb.AppendLine();
        sb.AppendLine("| Misura | p50 ms | max ms | campioni | nota |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach (var riga in righe)
            sb.AppendLine($"| {riga.Misura} | {F(Mediana(riga.Valori))} | {F(riga.Valori.Max())} | {riga.Valori.Length} | {riga.Nota} |");
        return sb.ToString();
    }

    private static string ComponiTsv(IReadOnlyList<Riga> righe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("misura\tp50_ms\tmax_ms\tcampioni\tnota");
        foreach (var riga in righe)
            sb.AppendLine($"{riga.Misura}\t{F(Mediana(riga.Valori))}\t{F(riga.Valori.Max())}\t{riga.Valori.Length}\t{riga.Nota}");
        return sb.ToString();
    }

    private static string? TrovaRadiceRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
        return null;
    }
}
