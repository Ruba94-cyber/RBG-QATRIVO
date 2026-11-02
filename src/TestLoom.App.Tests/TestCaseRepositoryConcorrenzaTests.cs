using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Repository Test con più postazioni sulla stessa cartella condivisa: cache non condivisa con il chiamante
/// (P1.3), lock di commit su cestino/ripristino/eliminazione definitiva prima della validazione (P1.4), conflitto quando
/// il file è stato rimosso fuori da TestLoom dopo l'apertura (P1.8).</summary>
public sealed class TestCaseRepositoryConcorrenzaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TcConcorrenza_" + Guid.NewGuid().ToString("N"));
    private readonly List<AppState> _postazioni = new();

    public void Dispose()
    {
        foreach (var postazione in _postazioni)
        {
            try { postazione.Dispose(); } catch { }
        }
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreaPostazione(string nome)
    {
        var stato = TestPostazioniCondivise.Apri(Path.Combine(_root, "data"), Path.Combine(_root, "local-" + nome));
        _postazioni.Add(stato);
        return stato;
    }

    private static TestCase NuovoTest(string id) => new()
    {
        Id = id,
        Componente = "BCM",
        Descrizione = "originale",
        DescrizioneBreve = "originale",
        StatoLavoro = StatoLavoroTest.Pianificato,
    };

    [Fact]
    public void Save_LaCacheNonCondividePiuLIstanzaDelChiamante_ModificheNonSalvateNonFiltrano()
    {
        var postazione = CreaPostazione("A");
        var tc = NuovoTest("TC-CACHE-0001");
        postazione.TestCases.Save(tc, isNew: true);

        tc.Descrizione = "modifica non salvata";  // l'editor o un'azione di massa la cambia dopo il salvataggio

        Assert.Equal("originale", postazione.TestCases.GetById(tc.Id)!.Descrizione);
    }

    [Fact]
    public void SoftDelete_AttendeIlLockDiCommitPrimaDellaValidazione()
    {
        var postazione = CreaPostazione("A");
        var tc = NuovoTest("TC-LOCK-0001");
        postazione.TestCases.Save(tc, isNew: true);
        var percorso = postazione.TestCases.PercorsoPer(tc);
        var cestino = postazione.Paths.CestinoFile(tc.Id);
        using var lockTenuto = TestCaseCommitLock.AcquireAll(tc.Id, new[] { percorso }); // un Save concorrente è in corso

        var spostamento = LavoroInBackground.Avvia(() => postazione.TestCases.SoftDelete(tc, "Operatore B"));
        Assert.False(spostamento.Attendi(TimeSpan.FromMilliseconds(400)), "SoftDelete è terminato senza attendere il lock di commit.");

        lockTenuto.Dispose();
        spostamento.Completa();
        Assert.False(FilePresente(postazione, percorso));
        Assert.True(File.Exists(cestino));
    }

    [Fact]
    public void Restore_AttendeIlLockDiCommitPrimaDellaValidazione()
    {
        var postazione = CreaPostazione("A");
        var tc = NuovoTest("TC-LOCK-0002");
        postazione.TestCases.Save(tc, isNew: true);
        postazione.TestCases.SoftDelete(tc, "Operatore B");
        var cestino = postazione.Paths.CestinoFile(tc.Id);
        using var lockTenuto = TestCaseCommitLock.AcquireAll(tc.Id, new[] { cestino });

        var ripristino = LavoroInBackground.Avvia(() => postazione.TestCases.Restore(tc, "Operatore B"));
        Assert.False(ripristino.Attendi(TimeSpan.FromMilliseconds(400)), "Restore è terminato senza attendere il lock di commit.");

        lockTenuto.Dispose();
        ripristino.Completa();
        Assert.False(FilePresente(postazione, cestino));
        Assert.True(FilePresente(postazione, postazione.TestCases.PercorsoPer(tc)));
    }

    [Fact]
    public void EliminaDefinitivamente_AttendeIlLockDiCommitPrimaDellaValidazione()
    {
        var postazione = CreaPostazione("A");
        var tc = NuovoTest("TC-LOCK-0003");
        postazione.TestCases.Save(tc, isNew: true);
        postazione.TestCases.SoftDelete(tc, "Operatore B");
        var cestino = postazione.Paths.CestinoFile(tc.Id);
        using var lockTenuto = TestCaseCommitLock.AcquireAll(tc.Id, new[] { cestino });

        var eliminazione = LavoroInBackground.Avvia(() => postazione.TestCases.EliminaDefinitivamente(tc));
        Assert.False(eliminazione.Attendi(TimeSpan.FromMilliseconds(400)), "EliminaDefinitivamente è terminato senza attendere il lock di commit.");

        lockTenuto.Dispose();
        eliminazione.Completa();
        Assert.False(FilePresente(postazione, cestino));
    }

    [Fact]
    public async Task SalvataggioEEliminazioneDaDuePostazioni_NonLasciano_LoStessoTestAttivoENelCestino()
    {
        var postazioneA = CreaPostazione("A");
        var postazioneB = CreaPostazione("B");
        for (var giro = 1; giro <= 6; giro++)
        {
            var tc = NuovoTest($"TC-RACE-{giro:D4}");
            postazioneA.TestCases.Save(tc, isNew: true);
            postazioneB.TestCases.RefreshIndex();
            var id = tc.Id;
            // Copie fresche da disco: ogni postazione lavora sulla propria istanza, come l'editor.
            var copiaA = postazioneA.TestCases.LoadCurrent(tc)!;
            var copiaB = postazioneB.TestCases.LoadCurrent(tc)!;
            copiaA.Descrizione = $"modifica dalla postazione A (giro {giro})";

            using var partenza = new ManualResetEventSlim(false);
            var salvataggio = Task.Run(() =>
            {
                partenza.Wait();
                TentaEsecuzione(() => postazioneA.TestCases.Save(copiaA, isNew: false, copiaA.RevisioneDati));
            });
            var eliminazione = Task.Run(() =>
            {
                partenza.Wait();
                TentaEsecuzione(() => postazioneB.TestCases.SoftDelete(copiaB, "Operatore B"));
            });
            partenza.Set();
            await Task.WhenAll(salvataggio, eliminazione);

            // Invariante: il Test esiste in UNA sola posizione, mai nel cestino e tra i Test attivi insieme.
            // Un tombstone committato (Test eliminato o spostato) non è una copia attiva.
            var copieAttive = Directory.EnumerateFiles(postazioneA.Paths.TestCasesDir, $"testcase_{id}.json", SearchOption.AllDirectories)
                .Count(percorso => FilePresente(postazioneA, percorso));
            var copieNelCestino = FilePresente(postazioneA, postazioneA.Paths.CestinoFile(id)) ? 1 : 0;
            Assert.Equal(1, copieAttive + copieNelCestino);
        }
    }

    [Fact]
    public void Save_DopoRimozioneDelFileFuoriDaDitm_NonRicreaUnaFotografiaVecchia()
    {
        var postazione = CreaPostazione("A");
        var tc = NuovoTest("TC-DEL-0001");
        postazione.TestCases.Save(tc, isNew: true);
        var percorso = postazione.TestCases.PercorsoPer(tc);
        File.Delete(percorso); // Test rimosso dalla cartella condivisa fuori da TestLoom mentre l'editor è aperto

        tc.Descrizione = "modifica dall'editor aperto";

        Assert.Throws<ConflittoRevisioneException>(() => postazione.TestCases.Save(tc, isNew: false, tc.RevisioneDati));
        Assert.False(File.Exists(percorso));
    }

    /// <summary>Un Test eliminato o spostato lascia un tombstone committato al posto del file: non è più una copia attiva.</summary>
    private static bool FilePresente(AppState postazione, string percorso) =>
        File.Exists(percorso) && !WorkflowTransactionVisibilityService.IsCommittedTombstone(postazione.Paths, percorso);

    private static void TentaEsecuzione(Action azione)
    {
        try { azione(); }
        catch (ConflittoRevisioneException) { } // la postazione che arriva dopo riceve il conflitto, come nell'editor
    }

    /// <summary>Esegue l'operazione su un thread dedicato: l'attesa del lock si verifica con un Join con timeout, senza
    /// dipendere dalla pianificazione dei continuation di xUnit né dal thread pool mentre il lock è tenuto.</summary>
    private sealed class LavoroInBackground
    {
        private readonly Thread _thread;
        private Exception? _errore;

        private LavoroInBackground(Action azione)
        {
            _thread = new Thread(() =>
            {
                try { azione(); }
                catch (Exception ex) { _errore = ex; }
            }) { IsBackground = true };
            _thread.Start();
        }

        public static LavoroInBackground Avvia(Action azione) => new(azione);

        /// <summary>Vero se l'operazione è terminata entro il timeout.</summary>
        public bool Attendi(TimeSpan timeout) => _thread.Join(timeout);

        /// <summary>Attende la fine e rilancia l'eccezione dell'operazione, se ce n'è stata una.</summary>
        public void Completa()
        {
            _thread.Join();
            if (_errore != null) throw new InvalidOperationException("Operazione in background terminata in errore.", _errore);
        }
    }
}
