using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class FailedSaveDraftRecoveryPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PrepareRecovery_" + Guid.NewGuid().ToString("N"));
    private readonly string _base = Path.Combine(Path.GetTempPath(), "TestLoom_LocalRecovery_" + Guid.NewGuid().ToString("N"));

    private (string Canonical, string Draft, FailedSaveDraftInspector.DraftRow Row, Guid Uid)
        Fixture(bool withBase = true)
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var canonical = paths.ChangeRequestFile("CR-RECOVERY-1");
        var before = new TestLoom.App.Models.ChangeRequest
        {
            Id = "CR-RECOVERY-1", Uid = uid, RevisioneDati = 3, Titolo = "Originale"
        };
        JsonIo.WriteAtomic(canonical, before);
        var edited = System.Text.Json.JsonSerializer.Deserialize<TestLoom.App.Models.ChangeRequest>(
            System.Text.Json.JsonSerializer.Serialize(before, JsonIo.Options), JsonIo.Options)!;
        edited.Titolo = "Modificato";
        var draft = FailedSaveDraftService.Save(_root, "change-request", uid, 3,
            "QA", edited, "Save fallito", _base, baseSnapshot: withBase ? before : null);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_root, 0, 5, _base));
        return (canonical, draft, row, uid);
    }

    [Fact]
    public void ConfermaEsplicita_BaseInvariata_PreparaSoloCopiaLocale()
    {
        var x = Fixture();
        var before = File.ReadAllBytes(x.Canonical);

        var result = FailedSaveDraftRecoveryPreparationService.Prepare(_root, x.Row, true, _base);

        Assert.True(File.Exists(result.LocalCandidatePath));
        Assert.StartsWith(FailedSaveDraftService.LocalDirectory(_root, _base),
            result.LocalCandidatePath);
        Assert.Contains("NON_AUTOREVOLE", result.LocalCandidatePath);
        Assert.Contains("IsAuthoritative", File.ReadAllText(result.LocalCandidatePath));
        Assert.Equal(before, File.ReadAllBytes(x.Canonical));
        Assert.True(File.Exists(x.Draft));
    }

    [Fact]
    public void BozzaCr_VerificataVieneLettaPerEditorSenzaScrivereIlCanonico()
    {
        var x = Fixture();
        var bytes = File.ReadAllBytes(x.Canonical);
        var snapshot = JsonIo.TryRead<TestLoom.App.Models.ChangeRequest>(x.Canonical, out _)!;

        var candidate = FailedSaveDraftRecoveryPreparationService.LoadChangeRequestForEditor(
            _root, x.Row, snapshot, 3, _base);

        Assert.Equal(x.Uid, candidate.Uid);
        Assert.Equal("CR-RECOVERY-1", candidate.Id);
        Assert.Equal("Modificato", candidate.Titolo);
        Assert.Equal(bytes, File.ReadAllBytes(x.Canonical));
        Assert.True(File.Exists(x.Draft));
    }

    [Fact]
    public void BozzaCr_BaseSuperata_PuoEsserePropostaNellaVersioneCorrenteSenzaScriverla()
    {
        var x = Fixture();
        var current = JsonIo.TryRead<TestLoom.App.Models.ChangeRequest>(x.Canonical, out _)!;
        current.RevisioneDati = 4;
        current.Titolo = "Modifica locale più recente";
        JsonIo.WriteAtomic(x.Canonical, current);
        current = JsonIo.TryRead<TestLoom.App.Models.ChangeRequest>(x.Canonical, out _)!;
        var bytes = File.ReadAllBytes(x.Canonical);

        var candidate = FailedSaveDraftRecoveryPreparationService.LoadChangeRequestForEditor(
            _root, x.Row, current, 4, _base);

        Assert.Equal("Modificato", candidate.Titolo);
        Assert.Equal(bytes, File.ReadAllBytes(x.Canonical));
        Assert.True(File.Exists(x.Draft));
    }

    [Fact]
    public void BozzaNc_VerificataVieneLettaPerEditorSenzaScrivereIlCanonico()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var before = new TestLoom.App.Models.NonConformita
        {
            Id = "NC-RECOVERY-1", Uid = Guid.NewGuid(), RevisioneDati = 2,
            DaSegnalazioneRete = true, Descrizione = "Originale"
        };
        JsonIo.WriteAtomic(paths.NonConformitaFile(before.Id), before);
        var edited = EntityStateFingerprint.Clone(before);
        edited.Descrizione = "Modificato";
        var draftPath = FailedSaveDraftService.Save(_root, "nonconformita", before.Uid, 2,
            "QA", edited, "Save fallito", _base, baseSnapshot: before);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_root, 0, 5, _base));
        var canonical = paths.NonConformitaFile(before.Id);

        JsonIo.WriteAtomic(canonical, new TestLoom.App.Models.NonConformita
        {
            Id = before.Id, Uid = before.Uid, RevisioneDati = 3,
            DaSegnalazioneRete = true, Descrizione = "Modifica locale più recente"
        });
        var current = JsonIo.TryRead<TestLoom.App.Models.NonConformita>(canonical, out _)!;
        var latestBytes = File.ReadAllBytes(canonical);

        var candidate = FailedSaveDraftRecoveryPreparationService.LoadNetworkNcForEditor(
            _root, row, current, 3, _base);

        Assert.Equal("Modificato", candidate.Descrizione);
        Assert.Equal(latestBytes, File.ReadAllBytes(canonical));
        Assert.True(File.Exists(draftPath));
    }

    [Fact]
    public void AnnullamentoNonCreaLaCartellaDiPreparazione()
    {
        var x = Fixture();
        Assert.Throws<OperationCanceledException>(() =>
            FailedSaveDraftRecoveryPreparationService.Prepare(_root, x.Row, false, _base));
        Assert.False(Directory.Exists(Path.Combine(
            FailedSaveDraftService.LocalDirectory(_root, _base), "prepared")));
    }

    [Fact]
    public void CopiaCanonicaCambiata_BloccaPreparazioneSenzaSovrascrivere()
    {
        var x = Fixture();
        var current = new { Id = "CR-RECOVERY-1", Uid = x.Uid, RevisioneDati = 4,
            Titolo = "Altro operatore" };
        JsonIo.WriteAtomic(x.Canonical, current);
        var bytes = File.ReadAllBytes(x.Canonical);

        Assert.Throws<IOException>(() =>
            FailedSaveDraftRecoveryPreparationService.Prepare(_root, x.Row, true, _base));
        Assert.Equal(bytes, File.ReadAllBytes(x.Canonical));
    }

    [Fact]
    public void SenzaFotografiaIniziale_IlRecuperoNonSiImprovvisa()
    {
        var x = Fixture(withBase: false);
        Assert.Throws<IOException>(() =>
            FailedSaveDraftRecoveryPreparationService.Prepare(_root, x.Row, true, _base));
    }

    [Fact]
    public void ForkVisibile_BloccaPreparazioneAncheSeCanonicoEInvariato()
    {
        var x = Fixture();
        var conflict = x.Canonical.Replace(".json", " (Laptop 2).json");
        JsonIo.WriteAtomic(conflict, new { Id = "CR-RECOVERY-1", Uid = x.Uid,
            RevisioneDati = 3, Titolo = "Conflitto remoto" });

        Assert.Throws<IOException>(() =>
            FailedSaveDraftRecoveryPreparationService.Prepare(_root, x.Row, true, _base));
        Assert.True(File.Exists(conflict));
        Assert.True(File.Exists(x.Draft));
    }

    [Fact]
    public void SegnalazioneSenzaId_UsaUidEPreparaSoloCopiaLocale()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var before = new TestLoom.App.Models.SegnalazioneDaClassificare
        {
            Uid = uid, RevisioneDati = 2, TestId = "TC-A"
        };
        var proposed = new TestLoom.App.Models.SegnalazioneDaClassificare
        {
            Uid = uid, RevisioneDati = 2, TestId = "TC-B"
        };
        var canonical = paths.SegnalazioneFile(uid);
        JsonIo.WriteAtomic(canonical, before);
        var bytes = File.ReadAllBytes(canonical);
        var draft = FailedSaveDraftService.Save(_root, "segnalazione", uid, 2,
            "QA", proposed, "Save fallito", _base, baseSnapshot: before);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_root, 0, 5, _base));

        var result = FailedSaveDraftRecoveryPreparationService.Prepare(_root, row, true, _base);

        Assert.True(File.Exists(result.LocalCandidatePath));
        Assert.Equal("segnalazione", result.Domain);
        Assert.Equal(bytes, File.ReadAllBytes(canonical));
        Assert.True(File.Exists(draft));
    }

    [Fact]
    public void VecchiaBozzaNcEtichettataSegnalazione_UsaLaPathCanonicaNc()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var before = new TestLoom.App.Models.NonConformita
        {
            Id = "NC-LEGACY-1", Uid = uid, RevisioneDati = 4, Descrizione = "Originale"
        };
        var proposed = EntityStateFingerprint.Clone(before);
        proposed.Descrizione = "Recupero";
        var canonical = paths.NonConformitaFile(before.Id);
        JsonIo.WriteAtomic(canonical, before);
        var draft = FailedSaveDraftService.Save(_root, "segnalazione", uid, 4,
            "QA", proposed, "Save fallito", _base, baseSnapshot: before);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_root, 0, 5, _base));
        var bytes = File.ReadAllBytes(canonical);

        var result = FailedSaveDraftRecoveryPreparationService.Prepare(_root, row, true, _base);

        Assert.Equal("segnalazione", result.Domain);
        Assert.Equal(bytes, File.ReadAllBytes(canonical));
        Assert.True(File.Exists(draft));
        Assert.True(File.Exists(result.LocalCandidatePath));
    }

    [Fact]
    public void DocumentoMancante_NonVieneRicreato()
    {
        var x = Fixture();
        File.Delete(x.Canonical);

        Assert.Throws<IOException>(() =>
            FailedSaveDraftRecoveryPreparationService.Prepare(_root, x.Row, true, _base));
        Assert.False(File.Exists(x.Canonical));
    }

    [Fact]
    public void BozzaCrConIdNonValido_NonPreparaUnaCopia()
    {
        var uid = Guid.NewGuid();
        var draft = FailedSaveDraftService.Save(_root, "change-request", uid, 3,
            "QA", new { Uid = uid, Id = "../FUORI", RevisioneDati = 3L, Titolo = "Alterato" },
            "Save fallito", _base,
            baseSnapshot: new { Uid = uid, Id = "../FUORI", RevisioneDati = 3L, Titolo = "Prima" });
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_root, 0, 5, _base));

        Assert.Throws<IOException>(() =>
            FailedSaveDraftRecoveryPreparationService.Prepare(_root, row, true, _base));
        Assert.False(Directory.Exists(Path.Combine(
            FailedSaveDraftService.LocalDirectory(_root, _base), "prepared")));
        Assert.True(File.Exists(draft));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        try { if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true); } catch { }
    }
}
