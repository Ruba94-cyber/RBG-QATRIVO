using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Rimozione differita degli allegati: il binario lascia il percorso attivo solo dopo una persistenza
/// riuscita in cui il record non lo referenzia piu'. Abbandono e Unit of Work non persistita non toccano il file.</summary>
public sealed class AttachmentRemovalQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AttachmentRemovalQueue_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private DataPaths CreaPaths()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        return paths;
    }

    private string Sorgente(string nome, string contenuto)
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, nome);
        File.WriteAllText(file, contenuto);
        return file;
    }

    [Fact]
    public void Accoda_NonToccaIlBinarioNelPercorsoAttivo()
    {
        var paths = CreaPaths();
        var tc = new TestCase { Id = "TC-Q-1", Componente = "BCM" };
        var allegato = AttachmentService.Salva(paths, tc, Sorgente("a.log", "dati"), "Tester");
        tc.Allegati.Add(allegato);
        var attivo = Path.Combine(paths.AllegatiDir(tc), allegato.NomeFile);

        var coda = new AttachmentRemovalQueue();
        coda.Accoda(allegato);

        Assert.True(coda.HaPendenti);
        Assert.True(File.Exists(attivo));
    }

    [Fact]
    public void ApplicaDopoPersistenza_SeIlRecordPersistitoLoReferenziaAncora_NonRimuoveMaiIlBinario()
    {
        var paths = CreaPaths();
        var tc = new TestCase { Id = "TC-Q-2", Componente = "BCM" };
        var allegato = AttachmentService.Salva(paths, tc, Sorgente("b.log", "ancora referenziato"), "Tester");
        tc.Allegati.Add(allegato);
        var attivo = Path.Combine(paths.AllegatiDir(tc), allegato.NomeFile);
        var coda = new AttachmentRemovalQueue();
        coda.Accoda(allegato);

        coda.ApplicaDopoPersistenza(new[] { allegato }, a => AttachmentService.Elimina(paths, tc, a));

        Assert.True(File.Exists(attivo));
        Assert.Equal("ancora referenziato", File.ReadAllText(attivo));
        Assert.False(coda.HaPendenti);
    }

    [Fact]
    public void ApplicaDopoPersistenza_RecordSenzaRiferimento_SpostaIlBinarioInRecovery()
    {
        var paths = CreaPaths();
        var tc = new TestCase { Id = "TC-Q-3", Componente = "BCM" };
        var allegato = AttachmentService.Salva(paths, tc, Sorgente("c.log", "da rimuovere"), "Tester");
        tc.Allegati.Add(allegato);
        var attivo = Path.Combine(paths.AllegatiDir(tc), allegato.NomeFile);
        var coda = new AttachmentRemovalQueue();
        coda.Accoda(allegato);

        coda.ApplicaDopoPersistenza(Array.Empty<Allegato>(), a => AttachmentService.Elimina(paths, tc, a));

        Assert.False(File.Exists(attivo));
        Assert.Equal("da rimuovere", File.ReadAllText(AttachmentService.PercorsoCompleto(paths, tc, allegato)));
        Assert.False(coda.HaPendenti);
    }

    [Fact]
    public void Abbandona_RestituisceIStaccatiSenzaToccareIBinari()
    {
        var paths = CreaPaths();
        var tc = new TestCase { Id = "TC-Q-4", Componente = "BCM" };
        var allegato = AttachmentService.Salva(paths, tc, Sorgente("d.log", "abbandono"), "Tester");
        tc.Allegati.Add(allegato);
        var attivo = Path.Combine(paths.AllegatiDir(tc), allegato.NomeFile);
        var coda = new AttachmentRemovalQueue();
        coda.Accoda(allegato);

        var staccati = coda.Abbandona();

        Assert.Single(staccati);
        Assert.False(coda.HaPendenti);
        Assert.True(File.Exists(attivo));
    }

    [Fact]
    public void ApplicaDopoPersistenza_DentroUnitOfWork_EseguitaSoloAlCommit()
    {
        var paths = CreaPaths();
        var tc = new TestCase { Id = "TC-Q-5", Componente = "BCM" };
        var allegato = AttachmentService.Salva(paths, tc, Sorgente("e.log", "unit of work"), "Tester");
        tc.Allegati.Add(allegato);
        var attivo = Path.Combine(paths.AllegatiDir(tc), allegato.NomeFile);
        var coda = new AttachmentRemovalQueue();
        coda.Accoda(allegato);

        // Rollback: la CR/Test torna al JSON precedente, che referenzia ancora il binario. Nulla deve essere rimosso.
        using (var tx = WorkflowTransactionService.Begin(paths, "rimozione-allegato-rollback"))
        {
            coda.ApplicaDopoPersistenza(Array.Empty<Allegato>(), a => AttachmentService.Elimina(paths, tc, a));
            Assert.True(File.Exists(attivo));
            tx.Rollback();
        }

        Assert.True(File.Exists(attivo));
        Assert.True(coda.HaPendenti);

        // Commit: solo a questo punto il binario lascia il percorso attivo.
        using (var tx = WorkflowTransactionService.Begin(paths, "rimozione-allegato-commit"))
        {
            coda.ApplicaDopoPersistenza(Array.Empty<Allegato>(), a => AttachmentService.Elimina(paths, tc, a));
            Assert.True(File.Exists(attivo));
            tx.Commit();
        }

        Assert.False(File.Exists(attivo));
        Assert.False(coda.HaPendenti);
    }

    [Fact]
    public void AbbandonoConStaccatiNonPersistiti_RimuoveSoloLeAggiunteMaiAutorevoli()
    {
        var paths = CreaPaths();
        var autorevole = new TestCase { Id = "TC-Q-6", Componente = "BCM" };
        var persistito = AttachmentService.Salva(paths, autorevole, Sorgente("persistito.log", "autorevole"), "Tester");
        autorevole.Allegati.Add(persistito);
        var snapshot = EntityStateFingerprint.Clone(autorevole);
        var persistitoAttivo = Path.Combine(paths.AllegatiDir(autorevole), persistito.NomeFile);

        // Sessione: un allegato nuovo mai salvato, poi entrambi staccati dalla lista di lavoro.
        var lavoro = EntityStateFingerprint.Clone(autorevole);
        var sessione = AttachmentService.Salva(paths, lavoro, Sorgente("sessione.log", "solo sessione"), "Tester");
        lavoro.Allegati.Add(sessione);
        var sessioneAttiva = Path.Combine(paths.AllegatiDir(lavoro), sessione.NomeFile);
        var coda = new AttachmentRemovalQueue();
        coda.Accoda(persistito);
        coda.Accoda(sessione);
        lavoro.Allegati.Clear();

        // Abbandono esplicito: il cleanup riceve la copia di lavoro arricchita dagli staccati.
        var staccati = coda.Abbandona();
        var copia = EntityStateFingerprint.Clone(lavoro);
        AttachmentRemovalQueue.UnisciAllaCopiaDiLavoro(copia.Allegati, staccati);
        DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, copia, snapshot);

        Assert.True(File.Exists(persistitoAttivo));
        Assert.Equal("autorevole", File.ReadAllText(persistitoAttivo));
        Assert.False(File.Exists(sessioneAttiva));
    }
}

/// <summary>Helper condiviso dai test di abbandono: il cleanup post-conferma viene accodato sul Dispatcher UI, e
/// la coda del Dispatcher e' FIFO a parita' di priorita'. Attendere questa callback basta a osservarne l'effetto
/// senza sleep ne' polling.</summary>
internal static class AttachmentRemovalTestSupport
{
    internal static Task AttendiCallbackAbbandono()
    {
        var completata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => completata.SetResult(), Avalonia.Threading.DispatcherPriority.Background);
        return completata.Task;
    }
}
