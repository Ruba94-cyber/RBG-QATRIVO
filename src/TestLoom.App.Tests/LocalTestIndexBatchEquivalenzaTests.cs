using System.Diagnostics;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>
/// Equivalenza del percorso ottimizzato di LocalTestIndexStore.ApplyBatch (caricamento da zero, blocchi, FTS in blocco, trigger
/// sospesi, comandi preparati) con il percorso di riferimento: le stesse mutazioni applicate una alla volta con Upsert/Remove
/// (un'unica transazione per riga, DeleteProjection per ogni riga). Stesso input => stesse righe in ogni tabella di cache,
/// proiezione, FTS e fonti Home. Poi: rollback su interruzione, lettori concorrenti, avanzamento.
/// </summary>
public sealed class LocalTestIndexBatchEquivalenzaTests : IDisposable
{
    private readonly List<string> _cartelle = new();

    private string NuovaCartella(string nome)
    {
        var path = Path.Combine(Path.GetTempPath(), $"TestLoom_{nome}_{Guid.NewGuid():N}");
        _cartelle.Add(path);
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var c in _cartelle)
            try { if (Directory.Exists(c)) Directory.Delete(c, true); } catch { /* best-effort */ }
    }

    private LocalTestIndexStore NuovoStore(string root) => new(root, NuovaCartella("IdxLocal"));

    // ---------------------------------------------------------------------------------------------------------------
    // Dataset
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Test deterministico per (indice, variante): la stessa chiamata produce un oggetto nuovo e identico.</summary>
    private static TestCase Test(int i, int variante = 0, int? uidDi = null)
    {
        var tc = DatasetIndiceBatch.Crea(i, i % 2 == 0 ? DatasetIndiceBatch.FormaRicca : DatasetIndiceBatch.FormaLeggera);
        if (uidDi.HasValue)
        {
            var b = new byte[16];
            BitConverter.TryWriteBytes(b, uidDi.Value + 1);
            tc.Uid = new Guid(b);
        }
        if (variante > 0)
        {
            tc.DescrizioneBreve = $"Modificato v{variante} " + i;
            tc.Descrizione = $"Descrizione aggiornata v{variante} per {i} con parole ricercabili alfa{variante}";
            tc.RevisioneDati = 1 + variante;
            tc.UltimaModifica = tc.UltimaModifica.AddHours(variante);
        }
        // Record incompleti: nessun id, nessuna componente, nessun testo; Test eliminato.
        if (i % 11 == 0)
        {
            tc.Id = "";
            tc.Componente = new EcuSelection(Array.Empty<string>());
            tc.Descrizione = "";
            tc.DescrizioneBreve = "";
            tc.Veicoli = new List<string>();
            tc.RequisitoId = new List<string>();
            tc.Funzionalita = new List<string>();
        }
        if (i % 13 == 0) tc.Eliminato = true;
        if (i % 17 == 0) tc.CampiCustom["campo" + (i % 3)] = "valore" + i;
        return tc;
    }

    private static LocalIndexMutation Ins(string root, int i, int variante = 0, int? uidDi = null, int? pathDi = null)
    {
        var tc = Test(i, variante, uidDi);
        return LocalIndexMutation.Upsert(DatasetIndiceBatch.PathDi(root, pathDi ?? i), tc.UltimaModifica, 100 + variante, tc);
    }

    private static LocalIndexMutation Rim(string root, int i) => LocalIndexMutation.Remove(DatasetIndiceBatch.PathDi(root, i));

    private static List<LocalIndexMutation> PrimoCaricamento(string root, int quanti) =>
        Enumerable.Range(0, quanti).Select(i => Ins(root, i)).ToList();

    /// <summary>Secondo batch su indice popolato: aggiornamenti, invariati, nuovi, cancellazioni, doppioni nello stesso batch.</summary>
    private static List<LocalIndexMutation> SecondoBatch(string root, int esistenti)
    {
        var l = new List<LocalIndexMutation>();
        for (var i = 0; i < esistenti; i += 3) l.Add(Ins(root, i, variante: 1));             // aggiornati
        for (var i = 1; i < esistenti; i += 7) l.Add(Ins(root, i));                          // invariati (stesso hash)
        for (var i = 2; i < esistenti; i += 9) l.Add(Rim(root, i));                          // cancellati
        l.Add(Rim(root, 99_999));                                                            // cancellazione di un path inesistente
        for (var i = esistenti; i < esistenti + 150; i++) l.Add(Ins(root, i));               // nuovi
        l.Add(Ins(root, 4, variante: 2));                                                    // stesso path due volte: vince l'ultimo
        l.Add(Ins(root, 4, variante: 3));
        l.Add(Ins(root, esistenti + 500, uidDi: 5));                                         // stesso Uid su un altro path (copia)
        l.Add(Ins(root, 10, variante: 4, uidDi: 888_001, pathDi: 10));                       // il path cambia Uid (file sostituito)
        l.Add(Rim(root, 20));                                                                // move: remove + upsert dello stesso Uid
        l.Add(Ins(root, 20, uidDi: 20, pathDi: esistenti + 600));
        return l;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Fotografia del contenuto dell'indice
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record Fotografia(Dictionary<string, string[]> Tabelle, HashSet<string> Indici, HashSet<string> Trigger);

    private static Fotografia Fotografa(string databasePath)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        var tabelle = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> 'meta' " +
                              "AND name NOT LIKE 'test_search\\_%' ESCAPE '\\' ORDER BY name;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tabelle.Add(r.GetString(0));
        }
        var dump = new Dictionary<string, string[]>();
        foreach (var t in tabelle)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{t}\";";
            using var r = cmd.ExecuteReader();
            var escludi = new HashSet<int>();
            for (var k = 0; k < r.FieldCount; k++)
                if (r.GetName(k) == "indexed_utc_ticks") escludi.Add(k); // istante di scrittura: non e' contenuto
            var righe = new List<string>();
            while (r.Read())
            {
                var sb = new StringBuilder();
                for (var k = 0; k < r.FieldCount; k++)
                {
                    if (escludi.Contains(k)) continue;
                    sb.Append(r.IsDBNull(k) ? "<null>" : Convert.ToString(r.GetValue(k), System.Globalization.CultureInfo.InvariantCulture)).Append('\u001f');
                }
                righe.Add(sb.ToString());
            }
            righe.Sort(StringComparer.Ordinal);
            dump[t] = righe.ToArray();
        }
        HashSet<string> Nomi(string tipo)
        {
            var set = new HashSet<string>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT name || '|' || COALESCE(sql, '') FROM sqlite_master WHERE type = '{tipo}';";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
        return new Fotografia(dump, Nomi("index"), Nomi("trigger"));
    }

    private static void AssertUguali(Fotografia atteso, Fotografia reale, string contesto)
    {
        Assert.Equal(atteso.Tabelle.Keys.OrderBy(k => k), reale.Tabelle.Keys.OrderBy(k => k));
        foreach (var (tabella, righe) in atteso.Tabelle)
            Assert.True(righe.SequenceEqual(reale.Tabelle[tabella]),
                $"{contesto}: tabella {tabella} diversa (attese {righe.Length} righe, reali {reale.Tabelle[tabella].Length}).");
        Assert.Equal(atteso.Indici.OrderBy(x => x), reale.Indici.OrderBy(x => x));
        Assert.Equal(atteso.Trigger.OrderBy(x => x), reale.Trigger.OrderBy(x => x));
    }

    /// <summary>Riferimento: una mutazione alla volta con i metodi a singolo file (una transazione per riga).</summary>
    private static void ApplicaSequenziale(LocalTestIndexStore store, IEnumerable<LocalIndexMutation> mutazioni)
    {
        var ultimoPerPath = mutazioni.GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        foreach (var m in ultimoPerPath)
        {
            if (m.Kind == LocalIndexMutationKind.Remove) { store.Remove(m.Path); continue; }
            m.TestCase!.NormalizzaVeicoli();
            store.Upsert(m.Path, m.MtimeUtc, m.FileLength, m.TestCase);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Equivalenza
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CaricamentoDaZero_IdenticoAlPercorsoSequenziale()
    {
        var root = NuovaCartella("Root");
        var ottimizzato = NuovoStore(root);
        var riferimento = NuovoStore(root);

        ottimizzato.ApplyBatch(PrimoCaricamento(root, 600));
        ApplicaSequenziale(riferimento, PrimoCaricamento(root, 600));

        var atteso = Fotografa(riferimento.DatabasePath);
        Assert.NotEmpty(atteso.Tabelle["test_summary"]);
        Assert.NotEmpty(atteso.Tabelle["test_revision"]);
        Assert.NotEmpty(atteso.Tabelle["test_search"]);
        Assert.NotEmpty(atteso.Tabelle["home_source_candidate"]);
        AssertUguali(atteso, Fotografa(ottimizzato.DatabasePath), "da zero");
        Assert.Equal(600, ottimizzato.LoadAll().Count);
    }

    [Theory]
    [InlineData(10_000)]   // un solo blocco
    [InlineData(250)]      // piu' blocchi: le cancellazioni/orphan attraversano i confini di blocco
    public void BatchSuIndicePopolato_IdenticoAlPercorsoSequenziale(int dimensioneBlocco)
    {
        var root = NuovaCartella("Root");
        var ottimizzato = NuovoStore(root);
        var riferimento = NuovoStore(root);
        ottimizzato.DimensioneBloccoBatch = dimensioneBlocco;

        const int esistenti = 700;
        ottimizzato.ApplyBatch(PrimoCaricamento(root, esistenti));
        ApplicaSequenziale(riferimento, PrimoCaricamento(root, esistenti));
        AssertUguali(Fotografa(riferimento.DatabasePath), Fotografa(ottimizzato.DatabasePath), "dopo il primo caricamento");

        ottimizzato.ApplyBatch(SecondoBatch(root, esistenti));
        ApplicaSequenziale(riferimento, SecondoBatch(root, esistenti));

        var atteso = Fotografa(riferimento.DatabasePath);
        AssertUguali(atteso, Fotografa(ottimizzato.DatabasePath), $"secondo batch, blocco {dimensioneBlocco}");
        Assert.Equal(riferimento.LoadAll().Count, ottimizzato.LoadAll().Count);
        // Il vecchio Uid sostituito sul path 10 non ha piu' proiezione; l'ultima versione del path 4 e' la variante 3.
        Assert.Contains(atteso.Tabelle["test_summary"], r => r.Contains("Modificato v3 4"));
        Assert.DoesNotContain(atteso.Tabelle["test_summary"], r => r.Contains("Modificato v2 4"));
    }

    [Fact]
    public void BatchPiccoloSuIndicePopolato_IdenticoAlPercorsoSequenziale()
    {
        var root = NuovaCartella("Root");
        var ottimizzato = NuovoStore(root);
        var riferimento = NuovoStore(root);
        ottimizzato.ApplyBatch(PrimoCaricamento(root, 300));
        ApplicaSequenziale(riferimento, PrimoCaricamento(root, 300));

        var piccolo = new Func<List<LocalIndexMutation>>(() => new List<LocalIndexMutation>
        {
            Ins(root, 0, 1), Ins(root, 1, 1), Rim(root, 2), Ins(root, 300), Ins(root, 3, uidDi: 5, pathDi: 3), Ins(root, 0, 2),
        });
        ottimizzato.ApplyBatch(piccolo());
        ApplicaSequenziale(riferimento, piccolo());
        AssertUguali(Fotografa(riferimento.DatabasePath), Fotografa(ottimizzato.DatabasePath), "batch piccolo");
    }

    [Fact]
    public void ReconcileFull_SuIndicePopolato_IdenticoAUnIndiceCostruitoDaZero()
    {
        var root = NuovaCartella("Root");
        var inCorso = NuovoStore(root);
        var daZero = NuovoStore(root);

        IEnumerable<LocalIndexWriteEntry> V1() => Enumerable.Range(0, 400).Select(i => Entry(root, i, 0));
        IEnumerable<LocalIndexWriteEntry> V2() => Enumerable.Range(0, 450).Where(i => i % 10 != 5).Select(i => Entry(root, i, i % 3 == 0 ? 1 : 0));
        var data = new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);

        inCorso.ReconcileFull(V1(), data);
        inCorso.ReconcileFull(V2(), data);
        daZero.ReconcileFull(V2(), data);

        AssertUguali(Fotografa(daZero.DatabasePath), Fotografa(inCorso.DatabasePath), "ReconcileFull incrementale");
    }

    private static LocalIndexWriteEntry Entry(string root, int i, int variante)
    {
        var tc = Test(i, variante);
        return new LocalIndexWriteEntry(DatasetIndiceBatch.PathDi(root, i), tc.UltimaModifica, 100, tc);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Interruzione a meta' batch
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void InterruzioneNelCaricamentoDaZero_RollbackCompletoERicostruibile()
    {
        var root = NuovaCartella("Root");
        var store = NuovoStore(root);
        var vuoto = Fotografa(store.DatabasePath);

        store.DopoRigaBatchPerTest = n => { if (n == 300) throw new IOException("interruzione simulata"); };
        Assert.Throws<IOException>(() => store.ApplyBatch(PrimoCaricamento(root, 600)));

        // Nessuna riga, indici e trigger di revisione tutti presenti (il DDL e' stato annullato col resto).
        AssertUguali(vuoto, Fotografa(store.DatabasePath), "dopo il rollback");
        Assert.Empty(store.LoadAll());

        store.DopoRigaBatchPerTest = null;
        store.ApplyBatch(PrimoCaricamento(root, 600));
        var riferimento = NuovoStore(root);
        ApplicaSequenziale(riferimento, PrimoCaricamento(root, 600));
        AssertUguali(Fotografa(riferimento.DatabasePath), Fotografa(store.DatabasePath), "ricostruzione dopo il rollback");
    }

    [Fact]
    public void InterruzioneNelBloccoSuIndicePopolato_StatoCoerenteERicostruibile()
    {
        var root = NuovaCartella("Root");
        var store = NuovoStore(root);
        store.DimensioneBloccoBatch = 250;
        store.ApplyBatch(PrimoCaricamento(root, 700));
        var prima = Fotografa(store.DatabasePath);

        // Il secondo batch ha piu' di 250 righe: l'interruzione alla riga 400 cade nel secondo blocco.
        store.DopoRigaBatchPerTest = n => { if (n == 400) throw new IOException("interruzione simulata"); };
        Assert.Throws<IOException>(() => store.ApplyBatch(SecondoBatch(root, 700)));

        var dopo = Fotografa(store.DatabasePath);
        // Il blocco interrotto non lascia nulla: trigger e indici intatti; ogni Test in cache ha esattamente una proiezione e una riga FTS.
        Assert.Equal(prima.Indici.OrderBy(x => x), dopo.Indici.OrderBy(x => x));
        Assert.Equal(prima.Trigger.OrderBy(x => x), dopo.Trigger.OrderBy(x => x));
        Assert.Equal(dopo.Tabelle["test_case_cache"].Length, dopo.Tabelle["test_summary"].Length);
        Assert.Equal(dopo.Tabelle["test_summary"].Length, dopo.Tabelle["test_search"].Length);

        // Il successivo refresh (stesso batch) riporta l'indice al risultato di riferimento.
        store.DopoRigaBatchPerTest = null;
        store.ApplyBatch(SecondoBatch(root, 700));
        var riferimento = NuovoStore(root);
        ApplicaSequenziale(riferimento, PrimoCaricamento(root, 700));
        ApplicaSequenziale(riferimento, SecondoBatch(root, 700));
        AssertUguali(Fotografa(riferimento.DatabasePath), Fotografa(store.DatabasePath), "ripetizione dopo l'interruzione");
    }

    [Fact]
    public void BatchConUpsertSenzaTestCase_RollbackDelBloccoCorrente()
    {
        var root = NuovaCartella("Root");
        var store = NuovoStore(root);
        var vuoto = Fotografa(store.DatabasePath);
        var batch = PrimoCaricamento(root, 300);
        batch.Insert(150, new LocalIndexMutation(LocalIndexMutationKind.Upsert, DatasetIndiceBatch.PathDi(root, 9999), default, 0, null));
        Assert.Throws<InvalidDataException>(() => store.ApplyBatch(batch));
        AssertUguali(vuoto, Fotografa(store.DatabasePath), "Upsert senza TestCase");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Lettori concorrenti
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)] // caricamento da zero: il lettore vede vuoto oppure tutto
    [InlineData(true)]  // batch di aggiornamento a blocchi: ogni lettura e' uno snapshot coerente di un commit
    public void LettoriConcorrenti_NonSiBloccanoEVedonoSoloStatiCoerenti(bool suIndicePopolato)
    {
        var root = NuovaCartella("Root");
        var store = NuovoStore(root);
        store.DimensioneBloccoBatch = 1000;
        const int base0 = 3000;
        if (suIndicePopolato) store.ApplyBatch(PrimoCaricamento(root, base0));
        var batch = suIndicePopolato
            ? Enumerable.Range(0, base0).Select(i => Ins(root, i, variante: 1)).Concat(Enumerable.Range(base0, 3000).Select(i => Ins(root, i))).ToList()
            : PrimoCaricamento(root, 5000);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        var fine = false;
        var letture = 0;
        var incoerenze = new List<string>();
        long maxMs = 0;
        var lettore = Task.Run(() =>
        {
            using var c = new SqliteConnection(connectionString);
            c.Open();
            while (!Volatile.Read(ref fine))
            {
                var sw = Stopwatch.StartNew();
                using var tx = c.BeginTransaction();
                long Conta(string sql)
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
                var cache = Conta("SELECT COUNT(*) FROM test_case_cache");
                var summary = Conta("SELECT COUNT(*) FROM test_summary");
                var fts = Conta("SELECT COUNT(*) FROM test_search");
                var revisioni = Conta("SELECT COUNT(*) FROM test_revision");
                tx.Commit();
                sw.Stop();
                maxMs = Math.Max(maxMs, sw.ElapsedMilliseconds);
                letture++;
                if (cache != summary || summary != fts) incoerenze.Add($"cache={cache} summary={summary} fts={fts}");
                if (!suIndicePopolato && cache != 0 && cache != 5000) incoerenze.Add($"stato parziale visibile: {cache} righe");
                if (suIndicePopolato && (cache < base0 || (cache - base0) % 1000 != 0)) incoerenze.Add($"blocco parziale visibile: {cache} righe");
                _ = revisioni;
            }
        });

        var scrittura = Stopwatch.StartNew();
        store.ApplyBatch(batch);
        scrittura.Stop();
        Volatile.Write(ref fine, true);
        lettore.Wait();

        Assert.Empty(incoerenze);
        Assert.True(letture >= 1);
        // Nessun blocco lungo: il lettore WAL non attende lo scrittore (soglia generosa per macchine cariche).
        Assert.True(maxMs < 5000, $"lettura piu' lenta: {maxMs} ms (scrittura {scrittura.ElapsedMilliseconds} ms, {letture} letture)");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Avanzamento
    // ---------------------------------------------------------------------------------------------------------------

    private sealed class Raccolta : IProgress<LocalIndexBatchProgress>
    {
        public List<LocalIndexBatchProgress> Eventi { get; } = new();
        public void Report(LocalIndexBatchProgress value) => Eventi.Add(value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Avanzamento_ReportaRigheElaborateSuTotaleFinoAlCompletamento(bool suIndicePopolato)
    {
        var root = NuovaCartella("Root");
        var store = NuovoStore(root);
        store.DimensioneBloccoBatch = 400;
        if (suIndicePopolato) store.ApplyBatch(PrimoCaricamento(root, 100));
        var progress = new Raccolta();
        var batch = suIndicePopolato
            ? Enumerable.Range(0, 1000).Select(i => Ins(root, i, variante: 1)).ToList()
            : PrimoCaricamento(root, 1000);

        store.ApplyBatch(batch, progress);

        Assert.True(progress.Eventi.Count >= 4);
        Assert.All(progress.Eventi, e => Assert.Equal(1000, e.Totale));
        Assert.Equal(progress.Eventi.Select(e => e.Elaborate).OrderBy(x => x), progress.Eventi.Select(e => e.Elaborate));
        Assert.Equal(new LocalIndexBatchProgress(1000, 1000), progress.Eventi[^1]);
    }
}
