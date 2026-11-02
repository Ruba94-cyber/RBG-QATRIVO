using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Errori dopo il punto di commit (review P1.11): la ricevuta nel ledger è il commit. Nessun passo successivo lo riporta come
/// fallito, le azioni differite partono una sola volta e la recovery non ripristina una transazione già confermata.
/// </summary>
public sealed class WorkflowTransactionPostCommitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TxPostCommit_" + Guid.NewGuid().ToString("N"));

    public WorkflowTransactionPostCommitTests()
    {
        Directory.CreateDirectory(_root);
        new DataPaths(_root).EnsureStructure();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ManifestNonAggiornabileDopoLaRicevuta_IlCommitRiesceELeAzioniDifferitePartonoUnaVolta()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-P11-010");
        var uid = Guid.NewGuid();
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P11-010", RevisioneDati = 1 });
        var prima = File.ReadAllBytes(path);
        var eseguite = 0;

        var scope = WorkflowTransactionService.Begin(paths, "post-commit-manifest");
        var txId = scope.Id;
        var manifest = ManifestTransazione(paths, txId);
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P11-010", RevisioneDati = 2 });
        WorkflowTransactionService.DeferAfterCommit(() => eseguite++);

        // Il manifest è bloccato dopo la pubblicazione della ricevuta e prima dell'aggiornamento a Committed.
        using (new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            scope.Commit();
        }
        scope.Dispose();

        Assert.Equal(1, eseguite);
        var dopo = File.ReadAllBytes(path);
        Assert.NotEqual(prima, dopo);
        Assert.True(WorkflowCommitLedgerService.TryGetReceipt(paths, DeviceIdentityService.GetOrCreate(), txId, out _, out var errore), errore);

        // Il manifest torna scrivibile: il recupero riconosce il commit dal ledger, non ripristina nulla e pulisce la directory.
        WorkflowTransactionService.RecoverPending(paths);
        Assert.Equal(dopo, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(TransazioneDirectory(paths, txId)));
        Assert.Equal(1, eseguite);
    }

    [Fact]
    public void RegistrazioneNonRiuscitaDopoLoSpostamento_NonFallisceLaScrittura_MaIlCommitVieneRifiutato()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-P11-011");
        var uid = Guid.NewGuid();
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P11-011", RevisioneDati = 1 });
        var prima = File.ReadAllBytes(path);

        var scope = WorkflowTransactionService.Begin(paths, "registrazione-fallita");
        try
        {
            var manifest = ManifestTransazione(paths, scope.Id);
            JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P11-011", RevisioneDati = 2 });
            var dopo = File.ReadAllBytes(path);

            // La mutazione è già avvenuta sul disco: la registrazione finale fallisce ma il chiamante non riceve un errore.
            using (new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                WorkflowTransactionService.AfterWrite(path, dopo);
            }

            // Il fault registrato rende il commit inaccettabile: nessuna ricevuta, quindi il rollback ripristina il before-image.
            Assert.Throws<IOException>(() => scope.Commit());
        }
        finally
        {
            scope.Dispose();
        }

        Assert.Equal(prima, File.ReadAllBytes(path));
    }

    private static string ManifestTransazione(DataPaths paths, Guid txId) =>
        Path.Combine(TransazioneDirectory(paths, txId), "manifest.json");

    private static string TransazioneDirectory(DataPaths paths, Guid txId) =>
        Path.Combine(WorkflowTransactionService.WorkspaceDirectory(paths.Root), $"tx-{txId:N}");
}
