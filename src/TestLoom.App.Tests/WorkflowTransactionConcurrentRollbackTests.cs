using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class WorkflowTransactionConcurrentRollbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TxConcurrentRollback_" + Guid.NewGuid().ToString("N"));

    public WorkflowTransactionConcurrentRollbackTests()
    {
        Directory.CreateDirectory(_root);
        new DataPaths(_root).EnsureStructure();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void Rollback_WriteFallitaSuFilePrimaAssente_NonCancellaFileConcorrente()
    {
        var paths = new DataPaths(_root);
        var path = Path.Combine(_root, "master-data", "race-create.json");
        // A differenza del test gemello sotto, qui non c'e' alcuna WriteAtomic precedente sullo stesso file che
        // crei "master-data" come effetto collaterale: senza questo, il writer "concorrente" fallirebbe con
        // DirectoryNotFoundException invece di simulare davvero la creazione concorrente che il test vuole
        // osservare (WriteAtomic crea la cartella solo DOPO aver serializzato, quindi troppo tardi per questo
        // scenario).
        Directory.CreateDirectory(Path.Combine(_root, "master-data"));

        using (WorkflowTransactionService.Begin(paths, "rollback-race-create"))
        {
            var payload = new CreaDestinazioneDuranteSerializzazione(path);
            Assert.Throws<IOException>(() => JsonIo.WriteAtomic(path, payload));
            Assert.Equal("contenuto-concorrente", File.ReadAllText(path));
            // Dispose esegue il rollback. La transazione ha solo Capture: nessuna write completata da annullare.
        }

        Assert.True(File.Exists(path));
        Assert.Equal("contenuto-concorrente", File.ReadAllText(path));
    }

    [Fact]
    public void Rollback_WriteCompletataPoiModificaConcorrente_NonSovrascriveIlConcorrente()
    {
        var paths = new DataPaths(_root);
        var path = Path.Combine(_root, "master-data", "race-write.json");
        JsonIo.WriteAtomic(path, new PayloadSemplice { Valore = "prima" });
        var prima = File.ReadAllBytes(path);

        var tx = WorkflowTransactionService.Begin(paths, "rollback-race-write");
        var transactionId = tx.Id;
        JsonIo.WriteAtomic(path, new PayloadSemplice { Valore = "locale" });
        var outputLocale = JsonSerializer.SerializeToUtf8Bytes(new PayloadSemplice { Valore = "locale" }, JsonIo.Options);

        File.WriteAllText(path, "contenuto-concorrente");

        Assert.Throws<IOException>(() => tx.Rollback());
        Assert.Equal("contenuto-concorrente", File.ReadAllText(path));

        // Porta la recovery al suo output noto e verifica che il secondo tentativo possa chiudere in sicurezza,
        // evitando di lasciare artefatti locali del test dopo aver dimostrato il fail-closed.
        File.WriteAllBytes(path, outputLocale);
        ScadiMarker(paths, transactionId);
        WorkflowTransactionService.RecoverPending(paths);
        Assert.Equal(prima, File.ReadAllBytes(path));
    }

    [Fact]
    public void Rollback_DeleteFisicaPoiRicreazioneConcorrente_NonSovrascriveIlConcorrente()
    {
        var paths = new DataPaths(_root);
        var path = Path.Combine(_root, "master-data", "race-delete.json");
        JsonIo.WriteAtomic(path, new PayloadSemplice { Valore = "prima" });
        var prima = File.ReadAllBytes(path);

        var tx = WorkflowTransactionService.Begin(paths, "rollback-race-delete");
        var transactionId = tx.Id;
        WorkflowTransactionService.DeleteFile(path);
        Assert.False(File.Exists(path));

        File.WriteAllText(path, "ricreato-da-altra-replica");

        Assert.Throws<IOException>(() => tx.Rollback());
        Assert.Equal("ricreato-da-altra-replica", File.ReadAllText(path));

        File.Delete(path);
        ScadiMarker(paths, transactionId);
        WorkflowTransactionService.RecoverPending(paths);
        Assert.Equal(prima, File.ReadAllBytes(path));
    }

    [Fact]
    public void Rollback_WriteEDelFileNuovoSenzaInterferenze_RipristinanoEsattamenteLoStatoIniziale()
    {
        var paths = new DataPaths(_root);
        var esistente = Path.Combine(_root, "master-data", "rollback-existing.json");
        var nuovo = Path.Combine(_root, "master-data", "rollback-new.json");
        JsonIo.WriteAtomic(esistente, new PayloadSemplice { Valore = "prima" });
        var prima = File.ReadAllBytes(esistente);

        using (WorkflowTransactionService.Begin(paths, "rollback-owned-state"))
        {
            JsonIo.WriteAtomic(esistente, new PayloadSemplice { Valore = "durante" });
            JsonIo.WriteAtomic(nuovo, new PayloadSemplice { Valore = "nuovo" });
            Assert.True(File.Exists(nuovo));
            // Dispose senza Commit: entrambi i path sono ancora esattamente l'output della transazione.
        }

        Assert.Equal(prima, File.ReadAllBytes(esistente));
        Assert.False(File.Exists(nuovo));
    }

    [Fact]
    public void Rollback_DeleteFisicaSenzaInterferenze_RipristinaLaBeforeImage()
    {
        var paths = new DataPaths(_root);
        var path = Path.Combine(_root, "master-data", "rollback-delete.json");
        JsonIo.WriteAtomic(path, new PayloadSemplice { Valore = "prima" });
        var prima = File.ReadAllBytes(path);

        using (WorkflowTransactionService.Begin(paths, "rollback-owned-delete"))
        {
            WorkflowTransactionService.DeleteFile(path);
            Assert.False(File.Exists(path));
        }

        Assert.Equal(prima, File.ReadAllBytes(path));
    }

    public sealed class CreaDestinazioneDuranteSerializzazione
    {
        private readonly string _path;

        public CreaDestinazioneDuranteSerializzazione(string path) => _path = path;

        public string Valore
        {
            get
            {
                File.WriteAllText(_path, "contenuto-concorrente");
                return "contenuto-locale";
            }
        }
    }

    public sealed class PayloadSemplice
    {
        public string Valore { get; set; } = "";
    }

    private static void ScadiMarker(DataPaths paths, Guid id)
    {
        var markerPath = Path.Combine(paths.Root, "_ditm", "transactions", $"{id:N}.pending.json");
        var marker = JsonSerializer.Deserialize<WorkflowTransactionMarker>(File.ReadAllBytes(markerPath), JsonIo.Options)!;
        marker.HeartbeatUtc = DateTime.UtcNow.AddMinutes(-10);
        File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker, JsonIo.Options));
    }
}
