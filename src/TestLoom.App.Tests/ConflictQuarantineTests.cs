using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Le copie di conflitto OneDrive escono dalle cartelle condivise dei record e compaiono solo nella sezione Conflitti:
/// (1) fuori dall'inventario operativo, ma dentro il backup; (2) ritirate in una quarantena durevole prima di essere rimosse;
/// (3) mai ritirate se illeggibili, instabili, di un altro record o senza canonico; (4) la guardia di scrittura continua a
/// proteggere il conflitto finche' non e' risolto; (5) idempotenza e consegna tardiva.
/// </summary>
public sealed class ConflictQuarantineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_Quarantena_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        FileProblemToleranceTracker.NowUtcOverrideForTests = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DataPaths Paths()
    {
        var p = new DataPaths(_root);
        p.EnsureStructure();
        return p;
    }

    private static NonConformita Nc(string id, string descr, Guid? uid = null) =>
        new() { Id = id, Descrizione = descr, Uid = uid ?? Guid.NewGuid() };

    private (string Canonico, string Copia, NonConformita Nc) CreaCoppia(DataPaths paths, string id = "NC-Q1", string suffisso = " (Laptop)")
    {
        var nc = Nc(id, "versione mia");
        var canonico = paths.NonConformitaFile(id);
        JsonIo.WriteAtomic(canonico, nc);
        var altra = Nc(id, "versione dell'altro PC", nc.Uid);
        var copia = canonico.Replace(".json", suffisso + ".json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(copia, altra);
        return (canonico, copia, nc);
    }

    [Fact]
    public void InventarioOperativo_EscludeLeCopie_MaLeRendeDisponibiliPerIlBackup()
    {
        var paths = Paths();
        var (canonico, copia, _) = CreaCoppia(paths);

        var operativi = WorkspaceOperationalFileEnumerator.EnumerateManagedJson(_root).ToArray();
        var copie = WorkspaceOperationalFileEnumerator.EnumerateConflictCopies(_root).ToArray();

        Assert.Contains(canonico, operativi);
        Assert.DoesNotContain(copia, operativi);
        Assert.Equal(copia, Assert.Single(copie), ignoreCase: true);
    }

    [Fact]
    public void FileConNomeSimileMaSenzaCanonico_RestaNellInventario()
    {
        var paths = Paths();
        var orfano = Path.Combine(paths.NonConformitaDir, "nc_NC-9 (Laptop).json");
        JsonIo.WriteAtomic(orfano, Nc("NC-9", "senza canonico"));

        Assert.Contains(orfano, WorkspaceOperationalFileEnumerator.EnumerateManagedJson(_root));
        Assert.Empty(WorkspaceOperationalFileEnumerator.EnumerateConflictCopies(_root));
    }

    [Fact]
    public void Quarantena_RitiraLaCopia_EnvelopeDurevoleConPayloadOriginale()
    {
        var paths = Paths();
        var (canonico, copia, nc) = CreaCoppia(paths);
        var byteCopia = File.ReadAllBytes(copia);
        var byteCanonico = File.ReadAllBytes(canonico);

        var report = ConflictCopyQuarantineService.Run(paths, "Op");

        Assert.Equal(1, report.Ritirate);
        Assert.False(File.Exists(copia));
        Assert.Equal(byteCanonico, File.ReadAllBytes(canonico)); // il canonico non e' toccato
        var voce = Assert.Single(ConflictCopyQuarantineService.List(_root));
        Assert.Equal(byteCopia, voce.Payload);
        Assert.Equal(DomainRecoveryCategory.NonConformita, voce.Envelope.Category);
        Assert.Equal(nc.Uid, voce.Envelope.EntityUid);
        Assert.False(voce.Envelope.IdenticalToCanonical);
        Assert.True(ConflictCopyQuarantineService.HasOpen(_root, nc.Uid));
        // L'envelope e' un JSON TestLoom gestito: entra nei backup.
        Assert.Contains(voce.EnvelopePaths[0], WorkspaceOperationalFileEnumerator.EnumerateManagedJson(_root), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quarantena_CopiaIdentica_SegnalataComeTale()
    {
        var paths = Paths();
        var nc = Nc("NC-Q2", "uguale");
        var canonico = paths.NonConformitaFile("NC-Q2");
        JsonIo.WriteAtomic(canonico, nc);
        File.Copy(canonico, canonico.Replace(".json", "-PC2.json"));

        Assert.Equal(1, ConflictCopyQuarantineService.Run(paths, "Op").Ritirate);
        Assert.True(Assert.Single(ConflictCopyQuarantineService.List(_root)).Envelope.IdenticalToCanonical);
    }

    [Fact]
    public void Quarantena_EDempotente_ESicuraSeLaCopiaRiappare()
    {
        var paths = Paths();
        var (_, copia, nc) = CreaCoppia(paths);
        var bytes = File.ReadAllBytes(copia);

        ConflictCopyQuarantineService.Run(paths, "Op");
        Assert.Equal(0, ConflictCopyQuarantineService.Run(paths, "Op").Ritirate);

        // Crash fra "envelope scritto" e "copia rimossa": la copia riappare, stesso Id, nessun duplicato.
        File.WriteAllBytes(copia, bytes);
        Assert.Equal(1, ConflictCopyQuarantineService.Run(paths, "Op").Ritirate);
        Assert.False(File.Exists(copia));
        Assert.Single(ConflictCopyQuarantineService.List(_root));
        Assert.True(ConflictCopyQuarantineService.HasOpen(_root, nc.Uid));
    }

    [Fact]
    public void Quarantena_DueDispositivi_StessoConflitto_ElencoDeduplicato()
    {
        var paths = Paths();
        var (_, copia, _) = CreaCoppia(paths);
        ConflictCopyQuarantineService.Run(paths, "Op");
        var voce = Assert.Single(ConflictCopyQuarantineService.List(_root));

        // L'altro PC ha ritirato la stessa copia: envelope con nome-dispositivo diverso, stesso Id.
        var altroNome = Path.Combine(Path.GetDirectoryName(voce.EnvelopePaths[0])!,
            $"conflict_{voce.Envelope.EntityUid:N}_{voce.Id}_ALTROPC.json");
        File.Copy(voce.EnvelopePaths[0], altroNome);

        var elenco = ConflictCopyQuarantineService.List(_root);
        var unica = Assert.Single(elenco);
        Assert.Equal(2, unica.EnvelopePaths.Count);
        Assert.False(File.Exists(copia));
    }

    [Fact]
    public void Quarantena_NonRitira_IllegibileRecordDiversoOSenzaCanonico()
    {
        var paths = Paths();
        // illeggibile
        var (_, copiaRotta, _) = CreaCoppia(paths, "NC-R1");
        File.WriteAllText(copiaRotta, "{ non json");
        // altro record con nome simile (UID diverso)
        var nc2 = Nc("NC-R2", "mio");
        var can2 = paths.NonConformitaFile("NC-R2");
        JsonIo.WriteAtomic(can2, nc2);
        var diverso = can2.Replace(".json", " (Laptop).json");
        JsonIo.WriteAtomic(diverso, Nc("NC-R2", "altro record", Guid.NewGuid()));

        var report = ConflictCopyQuarantineService.Run(paths, "Op");

        Assert.Equal(0, report.Ritirate);
        Assert.True(File.Exists(copiaRotta));
        Assert.True(File.Exists(diverso));
        Assert.Empty(ConflictCopyQuarantineService.List(_root));
    }

    [Fact]
    public void Quarantena_RispettaEtaMinima()
    {
        var paths = Paths();
        var (_, copia, _) = CreaCoppia(paths);

        var report = ConflictCopyQuarantineService.Run(paths, "Op", etaMinima: TimeSpan.FromMinutes(2));

        Assert.Equal(0, report.Ritirate);
        Assert.True(File.Exists(copia));
    }

    [Fact]
    public void Guardia_BloccaIlSalvataggioFinoAllaRisoluzione_AncheDopoIlRitiro()
    {
        var paths = Paths();
        var (canonico, _, nc) = CreaCoppia(paths);
        ConflictCopyQuarantineService.Run(paths, "Op");

        var ex = Assert.Throws<IOException>(() =>
            VisibleConflictWriteGuard.EnsureNoVisibleFork(canonico, nc.Uid, null, null, _root));
        Assert.Contains("Conflitt", ex.Message);

        // Dentro l'ambito di risoluzione di QUELLA copia la guardia non blocca; fuori si'.
        var voce = Assert.Single(ConflictCopyQuarantineService.List(_root));
        using (VisibleConflictWriteGuard.BeginResolution(new[] { voce.Id }))
            VisibleConflictWriteGuard.EnsureNoVisibleFork(canonico, nc.Uid, null, null, _root);
        Assert.Throws<IOException>(() => VisibleConflictWriteGuard.EnsureNoVisibleFork(canonico, nc.Uid, null, null, _root));

        // Chiuso il conflitto, la guardia non blocca piu'.
        ConflictCopyQuarantineService.Close(_root, voce);
        VisibleConflictWriteGuard.EnsureNoVisibleFork(canonico, nc.Uid, null, null, _root);
        Assert.False(ConflictCopyQuarantineService.HasOpen(_root, nc.Uid));
    }

    [Fact]
    public void ConsegnaTardivaDiUnaCopiaGiaRisolta_NonRiapreIlConflitto()
    {
        var paths = Paths();
        var (_, copia, nc) = CreaCoppia(paths);
        var bytes = File.ReadAllBytes(copia);
        ConflictCopyQuarantineService.Run(paths, "Op");
        ConflictCopyQuarantineService.Close(_root, Assert.Single(ConflictCopyQuarantineService.List(_root)));

        File.WriteAllBytes(copia, bytes); // OneDrive la riconsegna dopo la risoluzione
        var report = ConflictCopyQuarantineService.Run(paths, "Op");

        Assert.Equal(1, report.GiaChiuse);
        Assert.False(File.Exists(copia));
        Assert.Empty(ConflictCopyQuarantineService.List(_root));
        Assert.False(ConflictCopyQuarantineService.HasOpen(_root, nc.Uid));
    }

    [Fact]
    public void ArchivePayload_ConservaByteDiUnaVersioneCorrottaNelRecoveryBin()
    {
        var paths = Paths();
        var canonico = paths.NonConformitaFile("NC-AP");
        JsonIo.WriteAtomic(canonico, Nc("NC-AP", "x"));
        var corrotto = System.Text.Encoding.UTF8.GetBytes("{ \"Id\": \"NC-AP\", troncato");

        var rec = DomainRecoveryService.ArchivePayload(paths, canonico, DomainRecoveryCategory.NonConformita, corrotto, "Op", "test");

        var salvato = Assert.Single(DomainRecoveryService.GetAll(paths), r => r.RecoveryId == rec.RecoveryId);
        Assert.True(salvato.VersioneScartata);
        Assert.Equal(corrotto, Convert.FromBase64String(salvato.PayloadBase64));
        // Il ripristino non sovrascrive mai un canonico diverso.
        Assert.Throws<IOException>(() => DomainRecoveryService.Restore(paths, rec.RecoveryId, "Op"));
    }
}
