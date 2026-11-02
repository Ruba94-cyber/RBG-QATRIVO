using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// New concurrency preflight. These fixtures are disposable; never pointed
/// to an actual synchronized corporate workspace.
/// </summary>
public sealed class VisibleConflictWriteGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ForkGuard_" + Guid.NewGuid().ToString("N"));

    private (string Canonical, Guid Uid) Create(string id = "CR-2026-0001")
    {
        Directory.CreateDirectory(_dir);
        var uid = Guid.NewGuid();
        var path = Path.Combine(_dir, "cr_" + id + ".json");
        JsonIo.WriteAtomic(path, new { Uid = uid, Id = id, Titolo = "Originale", RevisioneDati = 1 });
        return (path, uid);
    }

    [Fact]
    public void CopiaDivergente_ProteggeEntrambeLeVersioniSenzaScrivere()
    {
        var record = Create();
        var alternative = record.Canonical.Replace(".json", " (Laptop-Paolo).json");
        JsonIo.WriteAtomic(alternative, new { Uid = record.Uid, Id = "CR-2026-0001",
            Titolo = "Modifica da un altro PC", RevisioneDati = 1 });
        var before = File.ReadAllBytes(record.Canonical);
        var otherBefore = File.ReadAllBytes(alternative);

        var error = Assert.Throws<IOException>(() =>
            VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid));

        Assert.Contains("divergenti", error.Message);
        Assert.Equal(before, File.ReadAllBytes(record.Canonical));
        Assert.Equal(otherBefore, File.ReadAllBytes(alternative));
    }

    [Fact]
    public void PercorsoNormaleSenzaFork_NonRileggeIlPayloadCanonico()
    {
        var record = Create("CR-FAST-PATH");
        // JSON volutamente non valido: la verifica rapida non deve idratare
        // ne' deserializzare l'intero file a ogni singolo salvataggio.
        File.WriteAllText(record.Canonical, "{ invalid-large-payload");

        VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid);

        Assert.Equal("{ invalid-large-payload", File.ReadAllText(record.Canonical));
    }

    [Fact]
    public void CopiaIdentica_NonBloccaUnSalvataggioOrdinario()
    {
        var record = Create();
        File.Copy(record.Canonical, record.Canonical.Replace(".json", "-ALTROPC.json"));
        VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid);
    }

    [Fact]
    public void CopiaDiUnAltroUid_NonBloccaIlRecord()
    {
        var record = Create();
        JsonIo.WriteAtomic(record.Canonical.Replace(".json", " (ALTROPC).json"),
            new { Uid = Guid.NewGuid(), Id = "ALTRO", Titolo = "Altro" });
        VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid);
    }

    [Fact]
    public void IdCheSonoPrefissiNonVengonoConfusi()
    {
        var record = Create("CR-2");
        JsonIo.WriteAtomic(Path.Combine(_dir, "cr_CR-20.json"),
            new { Uid = record.Uid, Id = "CR-20", Titolo = "Altro" });
        VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid);
    }

    [Fact]
    public void PrefissiConUidEstranei_NonApronoIlDocumentoCanonico()
    {
        var record = Create("CR-MOLTI");
        // Anche un payload canonico momentaneamente incompleto non viene
        // letto dal preflight se nessun fratello rivendica questo UID.
        // La validazione del repository resta obbligatoria e separata.
        File.WriteAllText(record.Canonical, "{json non pronto");
        for (var i = 0; i < 256; i++)
            JsonIo.WriteAtomic(Path.Combine(_dir, $"cr_CR-MOLTI-{i:D4}.json"),
                new { Uid = Guid.NewGuid(), Id = $"CR-MOLTI-{i:D4}", Titolo = "Record distinto" });

        VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid);

        Assert.Equal("{json non pronto", File.ReadAllText(record.Canonical));
    }

    [Fact]
    public void IdEstesoConJsonCorrotto_NonBloccaUnRecordDiverso()
    {
        var record = Create("CR-2");
        var sibling = Path.Combine(_dir, "cr_CR-2-ANALISI.json");
        File.WriteAllText(sibling, "{ file dell'altro record ancora incompleto");

        VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid);

        Assert.True(File.Exists(sibling));
        Assert.Contains("incompleto", File.ReadAllText(sibling));
    }

    [Fact]
    public void IdEstesoConUidUgualeEDatiDiversi_BloccaComunqueIlFork()
    {
        var record = Create("CR-2");
        var fork = Path.Combine(_dir, "cr_CR-2-ALTRA-REVISIONE.json");
        JsonIo.WriteAtomic(fork, new { Uid = record.Uid, Id = "CR-2",
            Titolo = "Conflitto su vecchio nome", RevisioneDati = 2 });
        var before = File.ReadAllBytes(record.Canonical);
        var other = File.ReadAllBytes(fork);

        Assert.Throws<IOException>(() =>
            VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid));

        Assert.Equal(before, File.ReadAllBytes(record.Canonical));
        Assert.Equal(other, File.ReadAllBytes(fork));
    }

    [Theory]
    [InlineData(" (Secondo-PC)")]
    [InlineData("-PC2")]
    [InlineData("-ALTROPC")]
    [InlineData("-conflitto")]
    public void CopiaConSuffissoRiconosciutoESenzaUid_BloccaLaScrittura(string suffix)
    {
        var record = Create();
        var fork = record.Canonical.Replace(".json", suffix + ".json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(fork, new { Id = "CR-2026-0001", Titolo = "UID perso" });
        var before = File.ReadAllBytes(record.Canonical);

        Assert.Throws<IOException>(() =>
            VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid));

        Assert.Equal(before, File.ReadAllBytes(record.Canonical));
        Assert.True(File.Exists(fork));
    }

    [Fact]
    public void MoltiRecordConIdSimile_NonCausanoBlocchiSpuri()
    {
        var record = Create("CR-2");
        for (var i = 0; i < 512; i++)
        {
            // Non sono copie OneDrive: ID di lavoro distinti con nome simile.
            var path = Path.Combine(_dir, $"cr_CR-2-QA-{i:D5}.json");
            JsonIo.WriteAtomic(path, new { Id = $"CR-2-QA-{i:D5}",
                Uid = Guid.NewGuid(), Titolo = "Record indipendente" });
        }

        VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid);

        Assert.Equal(513, Directory.EnumerateFiles(_dir, "*.json", SearchOption.TopDirectoryOnly).Count());
    }

    [Fact]
    public void CopiaSenzaUid_RimaneIntegraEMetteIlRecordInSolaLettura()
    {
        var record = Create();
        var other = record.Canonical.Replace(".json", " (PC-remoto).json",
            StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(other, new { Id = "CR-2026-0001", Titolo = "UID assente" });
        var saved = File.ReadAllBytes(record.Canonical);
        var fork = File.ReadAllBytes(other);

        var error = Assert.Throws<IOException>(() =>
            VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid));

        Assert.Contains("UID", error.Message);
        Assert.Equal(saved, File.ReadAllBytes(record.Canonical));
        Assert.Equal(fork, File.ReadAllBytes(other));
    }

    [Fact]
    public void ReplicaRemotaNonAncoraVisibile_NonPuoEssereCertificataPoiVieneBloccata()
    {
        var local = Create("CR-DELAYED-SYNC");
        var remote = Path.Combine(_dir, "remota");
        Directory.CreateDirectory(remote);
        var remoteFile = Path.Combine(remote, Path.GetFileName(local.Canonical));
        JsonIo.WriteAtomic(remoteFile, new
        {
            Uid = local.Uid, Id = "CR-DELAYED-SYNC",
            Titolo = "Modificato in un secondo PC", RevisioneDati = 1
        });

        // Due filesystem realmente indipendenti: prima della consegna tramite
        // OneDrive il filesystem locale non può vedere né bloccare la versione remota.
        VisibleConflictWriteGuard.EnsureNoVisibleFork(local.Canonical, local.Uid);

        var conflict = local.Canonical.Replace(".json", " (Secondo-PC).json",
            StringComparison.OrdinalIgnoreCase);
        File.Copy(remoteFile, conflict);
        var localBytes = File.ReadAllBytes(local.Canonical);
        var remoteBytes = File.ReadAllBytes(conflict);

        Assert.Throws<IOException>(() =>
            VisibleConflictWriteGuard.EnsureNoVisibleFork(local.Canonical, local.Uid));

        Assert.Equal(localBytes, File.ReadAllBytes(local.Canonical));
        Assert.Equal(remoteBytes, File.ReadAllBytes(conflict));
    }

    [Fact]
    public void CopiaCorrotta_RestaSuDiscoESospendeSoloQuellaEntita()
    {
        var record = Create();
        var other = record.Canonical.Replace(".json", " (Laptop).json");
        File.WriteAllText(other, "{broken");

        Assert.Throws<IOException>(() =>
            VisibleConflictWriteGuard.EnsureNoVisibleFork(record.Canonical, record.Uid));
        Assert.True(File.Exists(record.Canonical));
        Assert.Equal("{broken", File.ReadAllText(other));
    }

    [Fact]
    public void AltroRecordSenzaCopiaConflittualeRestaModificabile()
    {
        var a = Create("CR-0001");
        var b = Create("CR-0002");
        JsonIo.WriteAtomic(a.Canonical.Replace(".json", "-PC2.json"),
            new { Uid = a.Uid, Id = "CR-0001", Titolo = "Fork" });

        Assert.Throws<IOException>(() => VisibleConflictWriteGuard.EnsureNoVisibleFork(a.Canonical, a.Uid));
        VisibleConflictWriteGuard.EnsureNoVisibleFork(b.Canonical, b.Uid);
    }

    [Fact]
    public void EliminazioneCr_ForkVisibile_ConservaOriginaleECopiaConcorrente()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var repo = new ChangeRequestRepository(paths);
        var cr = new ChangeRequest { Id = "CR-DELETE-FORK", Titolo = "Originale" };
        repo.Save(cr);
        var path = paths.ChangeRequestFile(cr.Id);
        var fork = path.Replace(".json", " (PC2).json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(fork, new ChangeRequest
        {
            Id = cr.Id, Uid = cr.Uid, Titolo = "Aggiornata altrove",
            RevisioneDati = cr.RevisioneDati
        });
        var before = File.ReadAllBytes(path);
        var alternate = File.ReadAllBytes(fork);

        Assert.Throws<IOException>(() => repo.EliminaDefinitivamente(cr));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(alternate, File.ReadAllBytes(fork));
    }

    [Fact]
    public void EliminazioneNc_ForkVisibile_ConservaOriginaleECopiaConcorrente()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var repo = new NonConformitaRepository(paths);
        var nc = new NonConformita { Id = "NC-DELETE-FORK" };
        repo.Save(nc);
        var path = paths.NonConformitaFile(nc.Id);
        var fork = path.Replace(".json", " (PC2).json", StringComparison.OrdinalIgnoreCase);
        var otherNc = EntityStateFingerprint.Clone(nc);
        otherNc.Descrizione = "Fork da un altro PC";
        JsonIo.WriteAtomic(fork, otherNc);
        var before = File.ReadAllBytes(path);
        var alternate = File.ReadAllBytes(fork);

        Assert.Throws<IOException>(() => repo.Elimina(nc));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(alternate, File.ReadAllBytes(fork));
    }

    [Fact]
    public void RepoChangeRequest_NonSovrascriveSeCopiaArrivaDopoRefreshIndice()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var repo = new ChangeRequestRepository(paths);
        var cr = new ChangeRequest { Id = "CR-RACE-10", Titolo = "prima" };
        repo.Save(cr);
        repo.RefreshIndex();
        var path = paths.ChangeRequestFile(cr.Id);
        var alternative = path.Replace(".json", " (Laptop).json");
        JsonIo.WriteAtomic(alternative, new ChangeRequest
        {
            Id = cr.Id, Uid = cr.Uid, Titolo = "seconda",
            RevisioneDati = cr.RevisioneDati
        });
        var before = File.ReadAllBytes(path);
        var otherBefore = File.ReadAllBytes(alternative);

        cr.Titolo = "terza";
        Assert.Throws<IOException>(() => repo.Save(cr, cr.RevisioneDati));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(otherBefore, File.ReadAllBytes(alternative));
    }

    /// <summary>Merge automatico di un fork: una copia di uno dei due rami dichiarati non blocca il salvataggio che la risolve.</summary>
    [Fact]
    public void RamoDichiarato_NonBloccaLaPropriaCopiaDiConflitto()
    {
        var uid = Guid.NewGuid();
        var (canonical, copiaRamoA, hashRamoA) = ScriviTestConCopia(uid, "T-GUARD-0001", "Canonica", "Ramo A");

        VisibleConflictWriteGuard.EnsureNoVisibleFork(canonical, uid,
            new HashSet<string>(new[] { hashRamoA }, StringComparer.OrdinalIgnoreCase));

        Assert.True(File.Exists(copiaRamoA));
    }

    /// <summary>Un'altra copia divergente, non dichiarata come ramo del fork, continua a bloccare il salvataggio e nulla viene scritto.</summary>
    [Fact]
    public void CopiaNonDichiarata_BloccaComunqueConRamiDichiarati()
    {
        var uid = Guid.NewGuid();
        var (canonical, copiaRamoA, hashRamoA) = ScriviTestConCopia(uid, "T-GUARD-0002", "Canonica", "Ramo A");
        var copiaIgnota = canonical.Replace(".json", "-OneDriveC.json");
        JsonIo.WriteAtomic(copiaIgnota, new TestCase { Uid = uid, Id = "T-GUARD-0002", Descrizione = "versione sconosciuta" });
        var before = File.ReadAllBytes(canonical);
        var ignotaBefore = File.ReadAllBytes(copiaIgnota);

        var error = Assert.Throws<IOException>(() => VisibleConflictWriteGuard.EnsureNoVisibleFork(canonical, uid,
            new HashSet<string>(new[] { hashRamoA }, StringComparer.OrdinalIgnoreCase)));

        Assert.Contains("divergenti", error.Message);
        Assert.Equal(before, File.ReadAllBytes(canonical));
        Assert.Equal(ignotaBefore, File.ReadAllBytes(copiaIgnota));
        Assert.True(File.Exists(copiaRamoA));
    }

    /// <summary>Canonica + copia di ramo (OneDriveA), entrambe Test con lo stesso UID e contenuto diverso. Restituisce l'hash
    /// della copia di ramo come lo calcola il fork (TestCaseFingerprint su TestCase letto da disco).</summary>
    private (string Canonical, string CopiaRamoA, string HashRamoA) ScriviTestConCopia(Guid uid, string id, string descrCanonica, string descrRamo)
    {
        Directory.CreateDirectory(_dir);
        var canonical = Path.Combine(_dir, "testcase_" + id + ".json");
        JsonIo.WriteAtomic(canonical, new TestCase { Uid = uid, Id = id, Descrizione = descrCanonica });
        var copiaRamoA = canonical.Replace(".json", "-OneDriveA.json");
        var ramo = new TestCase { Uid = uid, Id = id, Descrizione = descrRamo };
        JsonIo.WriteAtomic(copiaRamoA, ramo);
        var letta = JsonIo.TryParse<TestCase>(File.ReadAllBytes(copiaRamoA), out _) ?? throw new InvalidOperationException("copia di ramo non leggibile");
        return (canonical, copiaRamoA, TestCaseFingerprint.Compute(letta));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}
