using System.Globalization;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Tests.Perf;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Equivalenza dell'indice locale incrementale con la ricostruzione completa (ciclo prestazioni 6.6.1, wt-p661-avvio).
///
/// Copre, per Test e NC: modifiche, aggiunte, eliminazioni, tombstone, mtime invariato con contenuto cambiato (stessa e
/// diversa lunghezza), file corrotto (prima e dopo la costruzione), copia di conflitto OneDrive e riconciliazione
/// interrotta a meta' transazione (nessuna scrittura parziale, riavvio coerente).
///
/// Il confronto usa il contenuto deterministico delle tabelle SQLite (senza marcatori temporali) e le viste dei
/// repository. Se TestLoom_RICON_DUMP_DIR e' impostata, i dump vengono scritti in quella cartella per confrontare il
/// contenuto dell'indice fra versioni del codice (stesso dataset, stesse mutazioni).
/// </summary>
public sealed class LocalIndexRiconciliazioneIncrementaleTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataRoot;
    private readonly DataPaths _paths;

    public LocalIndexRiconciliazioneIncrementaleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TestLoom_RiconInc", Guid.NewGuid().ToString("N"));
        _dataRoot = Path.Combine(_root, "data");
        _paths = new DataPaths(_dataRoot);
        _paths.EnsureStructure();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------ Test (TestCaseRepository / LocalTestIndexStore)

    [Fact]
    public void Test_RiconciliazioneProfonda_UgualeAllaRicostruzioneDaZero_DopoMutazioni()
    {
        CreaDataset(120);
        var originali = ElencoTest();
        var store = new LocalTestIndexStore(_dataRoot, Path.Combine(_root, "locale-inc"));
        var repo = new TestCaseRepository(_paths, store);
        repo.RefreshIndex();
        MutaTest(originali);

        repo.RefreshIndex();
        var incrementale = StatoTest(repo, store);
        var completa = RicostruzioneTestDaZero("completa");
        Esporta("test-profonda-incrementale", incrementale.Dati);
        Esporta("test-profonda-completa", completa.Dati);
        Assert.Equal(completa.Viste, incrementale.Viste);
        Assert.Equal(completa.Dati, incrementale.Dati);
    }

    [Fact]
    public void Test_RefreshPaths_UgualeAllaRicostruzioneDaZero_DopoMutazioni()
    {
        CreaDataset(120);
        var originali = ElencoTest();
        var store = new LocalTestIndexStore(_dataRoot, Path.Combine(_root, "locale-inc"));
        var repo = new TestCaseRepository(_paths, store);
        repo.RefreshIndex();
        var toccati = MutaTest(originali);

        // Percorso veloce guidato dal watcher: vengono passati tutti i percorsi scritti, anche quelli con mtime invariato.
        repo.RefreshPaths(toccati);
        var incrementale = StatoTest(repo, store);
        var completa = RicostruzioneTestDaZero("completa-paths");
        Esporta("test-paths-incrementale", incrementale.Dati);
        Esporta("test-paths-completa", completa.Dati);
        Assert.Equal(completa.Viste, incrementale.Viste);
        Assert.Equal(completa.Dati, incrementale.Dati);
    }

    [Fact]
    public void Test_FileCorrottoPrimaDellaCostruzione_RiparatoPoiUgualeAllaRicostruzione()
    {
        CreaDataset(60);
        var originali = ElencoTest();
        File.WriteAllText(originali[0], "{ corrotto");
        File.WriteAllText(originali[1], "{ corrotto");
        File.WriteAllText(originali[2], "{ corrotto");
        var store = new LocalTestIndexStore(_dataRoot, Path.Combine(_root, "locale-inc"));
        var repo = new TestCaseRepository(_paths, store);
        repo.RefreshIndex();

        // Riparazione di due file (con un Test valido nuovo): il terzo resta illeggibile e fuori dall'indice in entrambe le costruzioni.
        foreach (var (percorso, id) in new[] { (originali[0], "TC-RIPARATO-0"), (originali[1], "TC-RIPARATO-1") })
        {
            var riparato = LeggiTest(originali[10]);
            riparato.Id = id;
            riparato.Uid = UidDeterministico("riparato-" + id);
            ScriviTest(percorso, riparato);
        }
        repo.RefreshIndex();
        var incrementale = StatoTest(repo, store);
        var completa = RicostruzioneTestDaZero("completa-corrotto");
        Assert.Equal(completa.Viste, incrementale.Viste);
        Assert.Equal(completa.Dati, incrementale.Dati);
    }

    [Fact]
    public void Test_FileCorrottoDopoLaCostruzione_MantieniUltimaVersioneBuona_PoiRiallineaAlRipristino()
    {
        CreaDataset(60);
        var originali = ElencoTest();
        var store = new LocalTestIndexStore(_dataRoot, Path.Combine(_root, "locale-inc"));
        var repo = new TestCaseRepository(_paths, store);
        repo.RefreshIndex();
        var idValido = LeggiTest(originali[0]).Id;

        File.WriteAllText(originali[0], "{ corrotto");
        repo.RefreshIndex();
        // Limite preesistente documentato: ultima versione buona mantenuta e segnalata (non e' un'eliminazione).
        Assert.Contains(repo.GetAll(), t => t.Id == idValido);
        Assert.Contains(originali[0], repo.FileConProblemi.Select(p => p.Percorso).Concat(repo.PercorsiConProblemiTracciati));

        var riparato = LeggiTest(originali[10]);
        riparato.Id = idValido;
        riparato.Uid = UidDeterministico("riparato-" + idValido);
        ScriviTest(originali[0], riparato);
        repo.RefreshIndex();
        var incrementale = StatoTest(repo, store);
        var completa = RicostruzioneTestDaZero("completa-corrotto-dopo");
        Assert.Equal(completa.Viste, incrementale.Viste);
        Assert.Equal(completa.Dati, incrementale.Dati);
    }

    [Fact]
    public void Test_RiconciliazioneInterrotta_NessunaScritturaParziale_RiavvioCoerente()
    {
        CreaDataset(60);
        var originali = ElencoTest();
        var locale = Path.Combine(_root, "locale-inc");
        var store = new LocalTestIndexStore(_dataRoot, locale);
        var repo = new TestCaseRepository(_paths, store);
        repo.RefreshIndex();
        var prima = StatoTest(repo, store);

        MutaTest(originali);
        // Un trigger fa fallire a meta' transazione l'inserimento di una riga della riconciliazione profonda.
        InstallaTrigger(store.DatabasePath, "test_case_cache", originali[3], "banco_interrompi");
        repo.RefreshIndex();
        RimuoviTrigger(store.DatabasePath, "banco_interrompi");

        // Nessuna scrittura parziale: l'indice e' identico allo stato dell'ultimo commit.
        Assert.Equal(prima.Dati, Dump(store.DatabasePath));

        // Riavvio: nuova istanza sullo stesso indice, coerente con l'ultimo commit.
        var riavvio = new TestCaseRepository(_paths, new LocalTestIndexStore(_dataRoot, locale));
        Assert.Equal(prima.Viste, VisteTest(riavvio));

        // La riconciliazione successiva riallinea l'indice alla ricostruzione da zero.
        riavvio.RefreshIndex();
        var incrementale = StatoTest(riavvio, new LocalTestIndexStore(_dataRoot, locale));
        var completa = RicostruzioneTestDaZero("completa-interrotta");
        Assert.Equal(completa.Viste, incrementale.Viste);
        Assert.Equal(completa.Dati, incrementale.Dati);
    }

    [Fact]
    public void Test_RiconciliazioneProfondaSenzaModifiche_NonRiscriveRighe()
    {
        CreaDataset(60);
        var store = new LocalTestIndexStore(_dataRoot, Path.Combine(_root, "locale-inc"));
        new TestCaseRepository(_paths, store).RefreshIndex();
        var prima = store.RigheScritteTotali;

        // Riavvio sullo snapshot e riconciliazione profonda su dati invariati: nessuna riga deve essere riscritta.
        new TestCaseRepository(_paths, store).RefreshIndex();
        Assert.Equal(0, store.RigheScritteTotali - prima);
    }

    [Fact]
    public void Test_RiconciliazioneCompleta_RicreaGliIndiciSecondariIdentici()
    {
        // Il caricamento completo su indice vuoto sospende gli indici secondari e li ricrea nella stessa transazione:
        // l'elenco e l'SQL degli indici devono restare quelli dello schema.
        CreaDataset(60);
        var store = new LocalTestIndexStore(_dataRoot, Path.Combine(_root, "locale-indici"));
        var indiciDelloSchema = Indici(store.DatabasePath);
        new TestCaseRepository(_paths, store).RefreshIndex();
        Assert.Equal(indiciDelloSchema, Indici(store.DatabasePath));
    }

    private static string Indici(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var comando = connection.CreateCommand();
        comando.CommandText = "SELECT name || '|' || COALESCE(sql, '') FROM sqlite_master WHERE type = 'index' ORDER BY name;";
        using var lettore = comando.ExecuteReader();
        var righe = new List<string>();
        while (lettore.Read()) righe.Add(lettore.GetString(0));
        return string.Join("\n", righe);
    }

    [Fact]
    public void Test_RefreshPaths_ScriveSoloLeRigheCambiate()
    {
        CreaDataset(60);
        var originali = ElencoTest();
        var store = new LocalTestIndexStore(_dataRoot, Path.Combine(_root, "locale-inc"));
        var repo = new TestCaseRepository(_paths, store);
        repo.RefreshIndex();
        ModificaTest(originali[0], "una-sola-modifica");
        var prima = store.RigheScritteTotali;

        repo.RefreshPaths(new[] { originali[0] });
        Assert.Equal(1, store.RigheScritteTotali - prima);
    }

    // ------------------------------------------------------------------ NC (NonConformitaRepository / LocalNonConformitaIndexStore)

    [Fact]
    public void Nc_RiconciliazioneProfonda_UgualeAllaRicostruzioneDaZero_DopoMutazioni()
    {
        CreaDataset(120);
        var originali = ElencoNc();
        var store = new LocalNonConformitaIndexStore(_dataRoot, Path.Combine(_root, "nc-inc"));
        new NonConformitaRepository(_paths, null, store).RefreshIndex();
        MutaNc(originali);

        // Riavvio sullo snapshot: il primo RefreshIndex di un processo e' una validazione profonda (rilegge tutti i JSON).
        var riavvio = new NonConformitaRepository(_paths, null, store);
        riavvio.RefreshIndex();
        var incrementale = StatoNc(riavvio, store);
        var completa = RicostruzioneNcDaZero("nc-completa");
        Esporta("nc-profonda-incrementale", incrementale.Dati);
        Esporta("nc-profonda-completa", completa.Dati);
        Assert.Equal(completa.Viste, incrementale.Viste);
        Assert.Equal(completa.Dati, incrementale.Dati);
    }

    [Fact]
    public void Nc_RefreshPaths_UgualeAllaRicostruzioneDaZero_DopoMutazioni()
    {
        CreaDataset(120);
        var originali = ElencoNc();
        var store = new LocalNonConformitaIndexStore(_dataRoot, Path.Combine(_root, "nc-inc"));
        var repo = new NonConformitaRepository(_paths, null, store);
        repo.RefreshIndex();
        var toccati = MutaNc(originali);

        repo.RefreshPaths(toccati);
        var incrementale = StatoNc(repo, store);
        var completa = RicostruzioneNcDaZero("nc-completa-paths");
        Esporta("nc-paths-incrementale", incrementale.Dati);
        Esporta("nc-paths-completa", completa.Dati);
        Assert.Equal(completa.Viste, incrementale.Viste);
        Assert.Equal(completa.Dati, incrementale.Dati);
    }

    [Fact]
    public void Nc_RiconciliazioneInterrotta_NessunaScritturaParziale_RiavvioCoerente()
    {
        CreaDataset(60);
        var originali = ElencoNc();
        var store = new LocalNonConformitaIndexStore(_dataRoot, Path.Combine(_root, "nc-inc"));
        new NonConformitaRepository(_paths, null, store).RefreshIndex();
        var prima = StatoNc(new NonConformitaRepository(_paths, null, store), store);

        MutaNc(originali);
        InstallaTrigger(store.DatabasePath, "nc_cache", originali[3], "banco_interrompi_nc");
        new NonConformitaRepository(_paths, null, store).RefreshIndex();
        RimuoviTrigger(store.DatabasePath, "banco_interrompi_nc");

        Assert.Equal(prima.Dati, Dump(store.DatabasePath));
        var riavvio = new NonConformitaRepository(_paths, null, store);
        Assert.Equal(prima.Viste, VisteNc(riavvio));

        riavvio.RefreshIndex();
        var incrementale = StatoNc(riavvio, store);
        var completa = RicostruzioneNcDaZero("nc-completa-interrotta");
        Assert.Equal(completa.Viste, incrementale.Viste);
        Assert.Equal(completa.Dati, incrementale.Dati);
    }

    [Fact]
    public void Nc_RiconciliazioneProfondaSenzaModifiche_NonRiscriveRighe()
    {
        CreaDataset(60);
        var store = new LocalNonConformitaIndexStore(_dataRoot, Path.Combine(_root, "nc-inc"));
        new NonConformitaRepository(_paths, null, store).RefreshIndex();
        var prima = store.RigheScritteTotali;

        new NonConformitaRepository(_paths, null, store).RefreshIndex();
        Assert.Equal(0, store.RigheScritteTotali - prima);
    }

    /// <summary>Golden test (attivo solo con variabili d'ambiente): genera un dataset fisso oppure produce i dump dell'indice
    /// su quel dataset, per confrontare il contenuto delle tabelle fra versioni del codice.
    /// TestLoom_RICON_GOLDEN_GENERA = cartella di destinazione del dataset; TestLoom_RICON_GOLDEN_DATA = dataset da usare;
    /// TestLoom_RICON_GOLDEN_OUT = prefisso dei file di dump.</summary>
    [Fact]
    public void Golden_DumpIndiceSuDatasetFisso()
    {
        var genera = Environment.GetEnvironmentVariable("TestLoom_RICON_GOLDEN_GENERA");
        if (!string.IsNullOrWhiteSpace(genera))
        {
            CreaDataset(300);
            CopiaCartella(_dataRoot, genera);
            return;
        }
        var sorgente = Environment.GetEnvironmentVariable("TestLoom_RICON_GOLDEN_DATA");
        var uscita = Environment.GetEnvironmentVariable("TestLoom_RICON_GOLDEN_OUT");
        if (string.IsNullOrWhiteSpace(sorgente) || string.IsNullOrWhiteSpace(uscita)) return;

        // Stesso dataset fisso e stessa sequenza di mutazioni, in due modalita': riconciliazione profonda e RefreshPaths.
        foreach (var modalita in new[] { "profonda", "paths" })
        {
            var dataRoot = Path.Combine(_root, "golden-" + modalita);
            CopiaCartella(sorgente, dataRoot);
            var paths = new DataPaths(dataRoot);
            var originali = Directory.GetFiles(paths.TestCasesDir, "testcase_*.json", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            var originaliNc = Directory.GetFiles(paths.NonConformitaDir, "nc_*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal).ToList();

            var storeT = new LocalTestIndexStore(dataRoot, Path.Combine(_root, "golden-t-" + modalita));
            var repoT = new TestCaseRepository(paths, storeT);
            repoT.RefreshIndex();
            var storeN = new LocalNonConformitaIndexStore(dataRoot, Path.Combine(_root, "golden-n-" + modalita));
            var repoN = new NonConformitaRepository(paths, null, storeN);
            repoN.RefreshIndex();

            var toccatiT = MutaTest(originali);
            var toccatiN = MutaNc(originaliNc);
            if (modalita == "profonda")
            {
                repoT.RefreshIndex();
                new NonConformitaRepository(paths, null, storeN).RefreshIndex();
            }
            else
            {
                repoT.RefreshPaths(toccatiT);
                repoN.RefreshPaths(toccatiN);
            }
            File.WriteAllText($"{uscita}-test-{modalita}.txt", Dump(storeT.DatabasePath, mascheraTempi: true));
            File.WriteAllText($"{uscita}-nc-{modalita}.txt", Dump(storeN.DatabasePath, mascheraTempi: true));
        }
    }

    /// <summary>Uid stabile derivato da un seme (dataset di golden test: il Uid deve ripetersi fra un run e l'altro).</summary>
    private static Guid UidDeterministico(string seme) =>
        new(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("banco-uid|" + seme)));

    /// <summary>Copia ricorsiva di una cartella conservando gli mtime (il dump li include, salvo mascheramento).</summary>
    private static void CopiaCartella(string sorgente, string destinazione)
    {
        Directory.CreateDirectory(destinazione);
        foreach (var cartella in Directory.GetDirectories(sorgente, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destinazione, Path.GetRelativePath(sorgente, cartella)));
        foreach (var file in Directory.GetFiles(sorgente, "*", SearchOption.AllDirectories))
        {
            var copia = Path.Combine(destinazione, Path.GetRelativePath(sorgente, file));
            File.Copy(file, copia, overwrite: true);
            File.SetLastWriteTimeUtc(copia, File.GetLastWriteTimeUtc(file));
        }
    }

    // ------------------------------------------------------------------ dataset e mutazioni

    private void CreaDataset(int numeroTest)
    {
        var componente = Enumerable.Range(0, 20).Select(i => $"Componente-{i:D3}").ToList();
        var veicoli = Enumerable.Range(0, 10).Select(i => $"Veicolo-{i:D3}").ToList();
        var funzionalita = Enumerable.Range(0, 10).Select(i => $"Funzione-{i:D3}").ToList();
        var requisiti = Enumerable.Range(0, 30).Select(i => $"REQ-RIC-{i:D3}").ToList();
        var revisioniUdt = Enumerable.Range(0, 50).Select(BancoScaleSeedCore.ChiaveRevisioneUdtScale).ToList();
        var ids = BancoPerf100kGeneratoreTests.CreaTestRealistici(_paths, numeroTest, 0, componente, veicoli, funzionalita, requisiti, revisioniUdt);
        BancoPerf100kGeneratoreTests.CreaNonConformitaRealistiche(_paths, numeroTest, 0, ids);
        BancoPerf100kGeneratoreTests.CreaChangeRequestRealistiche(_paths, 5, 0, ids);
    }

    private List<string> ElencoTest() =>
        Directory.GetFiles(_paths.TestCasesDir, "testcase_*.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

    private List<string> ElencoNc() =>
        Directory.GetFiles(_paths.NonConformitaDir, "nc_*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

    /// <summary>Insieme fisso di mutazioni sui Test. Restituisce i percorsi scritti (quelli che il watcher segnalerebbe).</summary>
    private static List<string> MutaTest(List<string> originali)
    {
        var toccati = new List<string>();
        for (var i = 0; i < 20; i++) { ModificaTest(originali[i], $"mod-{i}"); toccati.Add(originali[i]); }
        for (var i = 20; i < 30; i++) { File.Delete(originali[i]); toccati.Add(originali[i]); }
        for (var k = 0; k < 10; k++) toccati.Add(NuovoTestDa(originali[30 + k], $"TC-NUOVO-{k:D3}"));
        foreach (var i in new[] { 40, 41 }) toccati.Add(CopiaConflitto(originali[i]));
        for (var i = 42; i < 47; i++)
        {
            ModificaMtimeInvariata(originali[i], stessaLunghezza: i < 45);
            toccati.Add(originali[i]);
        }
        for (var i = 47; i < 49; i++) { File.WriteAllText(originali[i], "{\"_ditmDeleted\":true}"); toccati.Add(originali[i]); }
        return toccati;
    }

    /// <summary>Insieme fisso di mutazioni sulle NC (stessa struttura dei Test).</summary>
    private static List<string> MutaNc(List<string> originali)
    {
        var toccati = new List<string>();
        for (var i = 0; i < 20; i++) { ModificaNc(originali[i], $"mod-{i}"); toccati.Add(originali[i]); }
        for (var i = 20; i < 30; i++) { File.Delete(originali[i]); toccati.Add(originali[i]); }
        for (var k = 0; k < 10; k++) toccati.Add(NuovaNcDa(originali[30 + k], $"NC-NUOVA-{k:D3}"));
        foreach (var i in new[] { 40, 41 }) toccati.Add(CopiaConflitto(originali[i]));
        for (var i = 42; i < 47; i++) { ModificaMtimeInvariataNc(originali[i], stessaLunghezza: i < 45); toccati.Add(originali[i]); }
        for (var i = 47; i < 49; i++) { File.WriteAllText(originali[i], "{\"_ditmDeleted\":true}"); toccati.Add(originali[i]); }
        return toccati;
    }

    private static TestCase LeggiTest(string percorso) =>
        JsonIo.TryRead<TestCase>(percorso, out var errore) ?? throw new InvalidOperationException($"Lettura Test fallita: {errore}");

    private static void ScriviTest(string percorso, TestCase tc) =>
        File.WriteAllBytes(percorso, JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options));

    private static void ModificaTest(string percorso, string marcatura)
    {
        var tc = LeggiTest(percorso);
        tc.DescrizioneBreve = marcatura;
        ScriviTest(percorso, tc);
    }

    private static string NuovoTestDa(string modello, string nuovoId)
    {
        var tc = LeggiTest(modello);
        tc.Id = nuovoId;
        tc.Uid = UidDeterministico(nuovoId);
        var percorso = Path.Combine(Path.GetDirectoryName(modello)!, $"testcase_{nuovoId}.json");
        ScriviTest(percorso, tc);
        return percorso;
    }

    private static string CopiaConflitto(string originale)
    {
        var copia = Path.Combine(Path.GetDirectoryName(originale)!,
            Path.GetFileNameWithoutExtension(originale) + "-COPIA.json");
        File.Copy(originale, copia);
        return copia;
    }

    private static void ModificaMtimeInvariata(string percorso, bool stessaLunghezza)
    {
        var mtime = File.GetLastWriteTimeUtc(percorso);
        var tc = LeggiTest(percorso);
        var vecchio = tc.DescrizioneBreve ?? "";
        // Stessa lunghezza: cambia solo il primo carattere. Lunghezza diversa: si aggiunge un suffisso.
        tc.DescrizioneBreve = stessaLunghezza && vecchio.Length >= 2
            ? (vecchio[0] == 'Q' ? "R" : "Q") + vecchio[1..]
            : vecchio + "-variato";
        ScriviTest(percorso, tc);
        File.SetLastWriteTimeUtc(percorso, mtime);
    }

    private static NonConformita LeggiNc(string percorso) =>
        JsonIo.TryRead<NonConformita>(percorso, out var errore) ?? throw new InvalidOperationException($"Lettura NC fallita: {errore}");

    private static void ScriviNc(string percorso, NonConformita nc) =>
        File.WriteAllBytes(percorso, JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options));

    private static void ModificaNc(string percorso, string marcatura)
    {
        var nc = LeggiNc(percorso);
        nc.Descrizione = marcatura;
        ScriviNc(percorso, nc);
    }

    private static string NuovaNcDa(string modello, string nuovoId)
    {
        var nc = LeggiNc(modello);
        nc.Id = nuovoId;
        nc.Uid = UidDeterministico(nuovoId);
        var percorso = Path.Combine(Path.GetDirectoryName(modello)!, $"nc_{nuovoId}.json");
        ScriviNc(percorso, nc);
        return percorso;
    }

    private static void ModificaMtimeInvariataNc(string percorso, bool stessaLunghezza)
    {
        var mtime = File.GetLastWriteTimeUtc(percorso);
        var nc = LeggiNc(percorso);
        var vecchio = nc.Descrizione ?? "";
        nc.Descrizione = stessaLunghezza && vecchio.Length >= 2
            ? (vecchio[0] == 'Q' ? "R" : "Q") + vecchio[1..]
            : vecchio + "-variato";
        ScriviNc(percorso, nc);
        File.SetLastWriteTimeUtc(percorso, mtime);
    }

    // ------------------------------------------------------------------ stato, viste e dump deterministico

    private sealed record Stato(string Dati, string Viste);

    private Stato StatoTest(TestCaseRepository repo, LocalTestIndexStore store) =>
        new(Dump(store.DatabasePath), VisteTest(repo));

    private Stato StatoNc(NonConformitaRepository repo, LocalNonConformitaIndexStore store) =>
        new(Dump(store.DatabasePath), VisteNc(repo));

    private Stato RicostruzioneTestDaZero(string nome)
    {
        var store = new LocalTestIndexStore(_dataRoot, Path.Combine(_root, "locale-" + nome));
        var repo = new TestCaseRepository(_paths, store);
        repo.RefreshIndex();
        return StatoTest(repo, store);
    }

    private Stato RicostruzioneNcDaZero(string nome)
    {
        var store = new LocalNonConformitaIndexStore(_dataRoot, Path.Combine(_root, "locale-" + nome));
        var repo = new NonConformitaRepository(_paths, null, store);
        repo.RefreshIndex();
        return StatoNc(repo, store);
    }

    private static string VisteTest(TestCaseRepository repo) =>
        string.Join('\n', repo.GetAll().Select(t => $"TEST|{t.Uid:N}|{t.Id}|{t.RevisioneDati}").OrderBy(x => x, StringComparer.Ordinal))
        + "\n" + string.Join('\n', repo.FileConProblemi.Select(p => $"PROBLEMA|{p.Percorso}").OrderBy(x => x, StringComparer.Ordinal))
        + "\n" + string.Join('\n', repo.CopieDiConflitto.Select(p => $"COPIA|{p.Percorso}").OrderBy(x => x, StringComparer.Ordinal));

    private static string VisteNc(NonConformitaRepository repo) =>
        string.Join('\n', repo.GetAll().Select(n => $"NC|{n.Uid:N}|{n.Id}|{n.RevisioneDati}").OrderBy(x => x, StringComparer.Ordinal))
        + "\n" + string.Join('\n', repo.FileConProblemi.Select(p => $"PROBLEMA|{p.Percorso}").OrderBy(x => x, StringComparer.Ordinal))
        + "\n" + string.Join('\n', repo.CopieDiConflitto.Select(p => $"COPIA|{p.Percorso}").OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Contenuto deterministico di tutte le tabelle utili dell'indice (senza le tabelle ombra di FTS5 e senza
    /// marcatori temporali di indicizzazione), con righe ordinate.</summary>
    private string Dump(string databasePath, bool mascheraTempi = false)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        var tabelle = new List<string>();
        using (var elenco = connection.CreateCommand())
        {
            elenco.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var lettore = elenco.ExecuteReader();
            while (lettore.Read()) tabelle.Add(lettore.GetString(0));
        }

        // fid (chiave INTEGER assegnata in ordine di inserimento) non e' un contenuto: nel dump diventa il percorso.
        var fidPath = new Dictionary<long, string>();
        if (tabelle.Contains("nc_filter"))
        {
            using var mappa = connection.CreateCommand();
            mappa.CommandText = "SELECT fid, path FROM nc_filter;";
            using var lettoreMappa = mappa.ExecuteReader();
            while (lettoreMappa.Read()) fidPath[lettoreMappa.GetInt64(0)] = lettoreMappa.GetString(1);
        }

        var sb = new StringBuilder();
        foreach (var tabella in tabelle)
        {
            if (EOmbraFts(tabella)) continue;
            sb.Append("## ").AppendLine(tabella);
            using var comando = connection.CreateCommand();
            // Il contatore di revisione (meta.revision, P2.6) conta le scritture: per costruzione diverge fra ricostruzione
            // completa e incrementale. Non e' contenuto, quindi non entra nel confronto.
            comando.CommandText = tabella == "meta"
                ? "SELECT * FROM meta WHERE key <> 'revision';"
                : $"SELECT * FROM \"{tabella}\";";
            using var lettore = comando.ExecuteReader();
            var colonne = new List<int>();
            for (var i = 0; i < lettore.FieldCount; i++)
            {
                var nome = lettore.GetName(i);
                if (nome.Equals("indexed_utc_ticks", StringComparison.OrdinalIgnoreCase)) continue;
                if (mascheraTempi && nome.Equals("mtime_utc_ticks", StringComparison.OrdinalIgnoreCase)) continue;
                colonne.Add(i);
            }
            var righe = new List<string>();
            while (lettore.Read())
            {
                var riga = string.Join("|", colonne.Select(i => lettore.IsDBNull(i)
                    ? "<null>"
                    : lettore.GetName(i).Equals("fid", StringComparison.OrdinalIgnoreCase)
                        ? "fid:" + (fidPath.TryGetValue(lettore.GetInt64(i), out var percorsoFid) ? percorsoFid : "?")
                        : Convert.ToString(lettore.GetValue(i), CultureInfo.InvariantCulture)));
                // La radice temporanea del run non e' un contenuto: nei dump diventa un segnaposto stabile.
                riga = riga.Replace(_root, "<ROOT>", StringComparison.OrdinalIgnoreCase);
                if (riga.Contains("last_full_scan_utc", StringComparison.Ordinal)) continue;
                righe.Add(riga);
            }
            righe.Sort(StringComparer.Ordinal);
            foreach (var riga in righe) sb.AppendLine(riga);
        }
        return sb.ToString();
    }

    private static bool EOmbraFts(string nome) =>
        nome.EndsWith("_data", StringComparison.Ordinal) || nome.EndsWith("_idx", StringComparison.Ordinal)
        || nome.EndsWith("_docsize", StringComparison.Ordinal) || nome.EndsWith("_config", StringComparison.Ordinal)
        || nome.EndsWith("_content", StringComparison.Ordinal);

    private static void Esporta(string nome, string contenuto)
    {
        var cartella = Environment.GetEnvironmentVariable("TestLoom_RICON_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(cartella)) return;
        Directory.CreateDirectory(cartella);
        File.WriteAllText(Path.Combine(cartella, nome + ".txt"), contenuto, Encoding.UTF8);
    }

    private static void InstallaTrigger(string databasePath, string tabella, string percorso, string nome)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var comando = connection.CreateCommand();
        comando.CommandText = $"CREATE TRIGGER IF NOT EXISTS {nome} BEFORE INSERT ON {tabella} " +
            $"WHEN NEW.path = '{percorso.Replace("'", "''")}' BEGIN SELECT RAISE(ABORT, 'interruzione simulata'); END;";
        comando.ExecuteNonQuery();
    }

    private static void RimuoviTrigger(string databasePath, string nome)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var comando = connection.CreateCommand();
        comando.CommandText = $"DROP TRIGGER IF EXISTS {nome};";
        comando.ExecuteNonQuery();
    }
}
