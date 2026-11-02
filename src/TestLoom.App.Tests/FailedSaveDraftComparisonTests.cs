using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Confronto esclusivamente READ-ONLY: non deve mai modificare file o usare
/// l'indice SQLite. Per sicurezza si controllano gli SHA dei byte originali.
/// </summary>
public sealed class FailedSaveDraftComparisonTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_CompareData_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_CompareLocal_" + Guid.NewGuid().ToString("N"));

    private (string Draft, string Current, FailedSaveDraftInspector.DraftRow Row, Guid Uid)
        Setup(string domain, bool createCurrent = true, bool altered = false, Guid? currentUid = null)
    {
        var uid = Guid.NewGuid();
        const string id = "QA-20261010";
        var snapshot = new { Uid = uid, Id = id, RevisioneDati = 3L, Titolo = "Originale" };
        var path = FailedSaveDraftService.Save(_shared, domain, uid, 3, "QA",
            snapshot, "Salvataggio fallito", _local);
        var paths = new DataPaths(_shared);
        var current = domain switch
        {
            "change-request" => paths.ChangeRequestFile(id),
            // Compatibility fixture for older SegnalazioneReteEditor builds that
            // stored a NonConformita payload using the "segnalazione" domain tag.
            "segnalazione" => paths.NonConformitaFile(id),
            _ => paths.NonConformitaFile(id)
        };
        if (createCurrent)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            JsonIo.WriteAtomic(current, new
            {
                Uid = currentUid ?? uid, Id = id, RevisioneDati = altered ? 4L : 3L,
                Titolo = altered ? "Modificato" : "Originale"
            });
        }
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_shared, 0, 10, _local));
        return (path, current, row, uid);
    }

    [Fact]
    public void SegnalazioneRealeSenzaCampoId_ConfrontoDovrebbeUsareSoloUid()
    {
        var uid = Guid.NewGuid();
        var original = new TestLoom.App.Models.SegnalazioneDaClassificare
        {
            Uid = uid, RevisioneDati = 5, TestId = "TC-SEG-1"
        };
        var modified = new TestLoom.App.Models.SegnalazioneDaClassificare
        {
            Uid = uid, RevisioneDati = 5, TestId = "TC-SEG-2"
        };
        var draft = FailedSaveDraftService.Save(_shared, "segnalazione", uid, 5,
            "QA", modified, "Save fallito", _local, baseSnapshot: original);
        var canonical = new DataPaths(_shared).SegnalazioneFile(uid);
        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        JsonIo.WriteAtomic(canonical, original);
        var before = File.ReadAllBytes(canonical);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_shared, 0, 10, _local));

        var result = FailedSaveDraftComparisonService.Compare(_shared, row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.DifferentLocal, result.State);
        Assert.Contains(result.Differences!.Items, d => d.Path == "/TestId");
        Assert.Equal(before, File.ReadAllBytes(canonical));
        Assert.True(File.Exists(draft));
    }

    [Theory]
    [InlineData("change-request")]
    [InlineData("segnalazione")]
    [InlineData("nonconformita")]
    public void PayloadIdentico_NonScriveNeiDatiOperativi(string domain)
    {
        var x = Setup(domain);
        var beforeCurrent = File.ReadAllBytes(x.Current);
        var beforeDraft = File.ReadAllBytes(x.Draft);

        var result = FailedSaveDraftComparisonService.Compare(_shared, x.Row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.IdenticalLocal, result.State);
        Assert.Equal(3L, result.LocalRevision);
        Assert.NotNull(result.Differences);
        Assert.False(result.Differences.HasDifferences);
        Assert.Contains("cloud", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeCurrent, File.ReadAllBytes(x.Current));
        Assert.Equal(beforeDraft, File.ReadAllBytes(x.Draft));
    }

    [Theory]
    [InlineData("change-request")]
    [InlineData("segnalazione")]
    public void RevisioniOPayloadDiversi_VengonoSegnalatiSenzaMerge(string domain)
    {
        var x = Setup(domain, altered: true);
        var before = File.ReadAllBytes(x.Current);

        var result = FailedSaveDraftComparisonService.Compare(_shared, x.Row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.DifferentLocal, result.State);
        Assert.Equal(4L, result.LocalRevision);
        Assert.Contains("Modificato", result.LocalPreview);
        Assert.NotNull(result.Differences);
        Assert.Contains(result.Differences.Items, d => d.Path == "/Titolo");
        Assert.Contains(result.Differences.Items, d => d.Path == "/RevisioneDati");
        Assert.DoesNotContain("Modificato", result.Differences.Details);
        Assert.Equal(before, File.ReadAllBytes(x.Current));
    }

    [Fact]
    public void TreVersioni_ModificheDistinte_ElencaCampiSenzaMerge()
    {
        var uid = Guid.NewGuid();
        var before = new { Uid = uid, Id = "QA-3WAY", RevisioneDati = 3L,
            Titolo = "Originale", Responsabile = "Prima" };
        var edited = new { Uid = uid, Id = "QA-3WAY", RevisioneDati = 3L,
            Titolo = "Cambiato in bozza", Responsabile = "Prima" };
        var canonical = new { Uid = uid, Id = "QA-3WAY", RevisioneDati = 3L,
            Titolo = "Originale", Responsabile = "Modificato sul disco" };
        var path = FailedSaveDraftService.Save(_shared, "change-request", uid, 3,
            "QA", edited, "Save fallito", _local, baseSnapshot: before);
        var canonicalPath = new DataPaths(_shared).ChangeRequestFile("QA-3WAY");
        JsonIo.WriteAtomic(canonicalPath, canonical);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_shared, 0, 10, _local));

        var result = FailedSaveDraftComparisonService.Compare(_shared, row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.DifferentLocal, result.State);
        Assert.NotNull(result.ThreeWaySummary);
        Assert.Contains("Nessun campo sovrapposto", result.ThreeWaySummary);
        Assert.Equal(new[] { "Titolo" }, result.RecoverableFields);
        Assert.Empty(result.ConflictingFields!);
        Assert.DoesNotContain("Cambiato in bozza", result.ThreeWaySummary);
        Assert.True(File.Exists(path));
        Assert.Contains("Modificato sul disco", File.ReadAllText(canonicalPath));
    }

    [Fact]
    public void TreVersioni_StessoCampoModificatoDaDueLati_SegnalaSovrapposizione()
    {
        var uid = Guid.NewGuid();
        var before = new { Uid = uid, Id = "QA-OVERLAP", RevisioneDati = 1L, Titolo = "Prima" };
        var edited = new { Uid = uid, Id = "QA-OVERLAP", RevisioneDati = 1L, Titolo = "User" };
        var canonical = new { Uid = uid, Id = "QA-OVERLAP", RevisioneDati = 1L, Titolo = "Other" };
        FailedSaveDraftService.Save(_shared, "change-request", uid, 1,
            "QA", edited, "Save fallito", _local, baseSnapshot: before);
        JsonIo.WriteAtomic(new DataPaths(_shared).ChangeRequestFile("QA-OVERLAP"), canonical);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_shared, 0, 10, _local));

        var result = FailedSaveDraftComparisonService.Compare(_shared, row, _local);

        Assert.Contains("/Titolo", result.ThreeWaySummary);
        Assert.Contains("Campi sovrapposti", result.ThreeWaySummary);
        Assert.DoesNotContain("Titolo", result.RecoverableFields!);
        Assert.Contains("Titolo", result.ConflictingFields!);
        Assert.Contains("non verificato", result.ThreeWaySummary);
    }

    [Fact]
    public void TreVersioni_CampoCambiatoSoloSulDocumentoLocale_NonERecuperabileDallaBozza()
    {
        var uid = Guid.NewGuid();
        var before = new { Uid = uid, Id = "QA-CURRENT-ONLY", RevisioneDati = 1L, Titolo = "Prima" };
        var draft = new { Uid = uid, Id = "QA-CURRENT-ONLY", RevisioneDati = 1L, Titolo = "Prima" };
        var canonical = new { Uid = uid, Id = "QA-CURRENT-ONLY", RevisioneDati = 2L, Titolo = "Aggiornato localmente" };
        FailedSaveDraftService.Save(_shared, "change-request", uid, 1,
            "QA", draft, "Conflitto", _local, baseSnapshot: before);
        JsonIo.WriteAtomic(new DataPaths(_shared).ChangeRequestFile("QA-CURRENT-ONLY"), canonical);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_shared, 0, 10, _local));

        var result = FailedSaveDraftComparisonService.Compare(_shared, row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.DifferentLocal, result.State);
        Assert.DoesNotContain("Titolo", result.RecoverableFields!);
        Assert.Empty(result.ConflictingFields!);
        Assert.Contains("non selezionabili", result.ThreeWaySummary);
    }

    [Fact]
    public void DocumentoMancante_NonVieneRicreato()
    {
        var x = Setup("change-request", createCurrent: false);
        var result = FailedSaveDraftComparisonService.Compare(_shared, x.Row, _local);
        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.MissingLocal, result.State);
        Assert.False(File.Exists(x.Current));
        Assert.True(File.Exists(x.Draft));
    }

    [Fact]
    public void UidDiverso_BloccaQualsiasiConclusioneDiUguaglianza()
    {
        var x = Setup("nonconformita", currentUid: Guid.NewGuid());
        var before = File.ReadAllBytes(x.Current);

        var result = FailedSaveDraftComparisonService.Compare(_shared, x.Row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.DifferentUid, result.State);
        Assert.Contains("UID", result.Explanation);
        Assert.Equal(before, File.ReadAllBytes(x.Current));
    }

    [Fact]
    public void Tombstone_NonVieneInterpretatoComeRecordDaSovrascrivere()
    {
        var x = Setup("change-request");
        JsonIo.WriteAtomic(x.Current, new Dictionary<string, object>
        {
            ["Uid"] = x.Uid, ["Id"] = "QA-20261010",
            ["RevisioneDati"] = 6L, ["_ditmDeleted"] = true
        });

        var result = FailedSaveDraftComparisonService.Compare(_shared, x.Row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.TombstoneLocal, result.State);
        Assert.True(File.Exists(x.Current));
    }

    [Fact]
    public void FileDiBozzaCambiatoDopoSelezione_ConfrontoInterrotto()
    {
        var x = Setup("change-request");
        var entry = JsonIo.TryRead<FailedSaveDraftService.DraftEnvelope>(x.Draft, out var err)!;
        Assert.Null(err);
        entry.Error = "Modificato dall'esterno";
        JsonIo.WriteAtomic(x.Draft, entry);

        var result = FailedSaveDraftComparisonService.Compare(_shared, x.Row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.DraftChanged, result.State);
        Assert.True(File.Exists(x.Current));
    }

    [Fact]
    public void CopiaLocaleConRevisioneVecchia_VieneSegnalataComeStale()
    {
        var x = Setup("change-request");
        var current = new { Uid = x.Uid, Id = "QA-20261010",
            RevisioneDati = 1L, Titolo = "Originale" };
        JsonIo.WriteAtomic(x.Current, current);
        var unchanged = File.ReadAllBytes(x.Current);

        var result = FailedSaveDraftComparisonService.Compare(_shared, x.Row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.StaleLocal, result.State);
        Assert.Contains("replica vecchia", result.Explanation);
        Assert.Equal(unchanged, File.ReadAllBytes(x.Current));
    }

    [Fact]
    public void DocumentoCanonicoConIdErrato_VieneSegnalato()
    {
        var x = Setup("nonconformita");
        JsonIo.WriteAtomic(x.Current, new { Uid = x.Uid, Id = "ALTRO-ID",
            RevisioneDati = 3L, Titolo = "Originale" });

        var result = FailedSaveDraftComparisonService.Compare(_shared, x.Row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.DifferentId, result.State);
    }

    [Fact]
    public void DocumentoCanonicoCrConIdErrato_VieneSegnalato()
    {
        var x = Setup("change-request");
        JsonIo.WriteAtomic(x.Current, new { Uid = x.Uid, Id = "CR-ALTRO-ID",
            RevisioneDati = 3L, Titolo = "Originale" });

        var result = FailedSaveDraftComparisonService.Compare(_shared, x.Row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.DifferentId, result.State);
    }

    [Fact]
    public void JsonCanonicoCorrotto_DiventaInvalidLocalENonInvalidDraft()
    {
        var x = Setup("change-request");
        File.WriteAllText(x.Current, "{ broken");

        var result = FailedSaveDraftComparisonService.Compare(_shared, x.Row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.InvalidLocal, result.State);
        Assert.Equal("{ broken", File.ReadAllText(x.Current));
    }

    [Fact]
    public void BozzaDiAltroWorkspace_NonPuoEssereConfrontata()
    {
        var x = Setup("change-request");
        Assert.Throws<UnauthorizedAccessException>(() =>
            FailedSaveDraftComparisonService.Compare(_shared + "-other", x.Row, _local));
    }

    [Fact]
    public void PayloadPrivoDiId_InterrompeIlConfrontoSenzaLeggereAltro()
    {
        var uid = Guid.NewGuid();
        var draft = FailedSaveDraftService.Save(_shared, "change-request", uid, 0, "QA",
            new { Uid = uid, Titolo = "Senza id" }, "Offline", _local);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_shared, 0, 10, _local));

        var result = FailedSaveDraftComparisonService.Compare(_shared, row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.InvalidDraft, result.State);
        Assert.True(File.Exists(draft));
    }

    [Fact]
    public void PayloadCrConIdNonValido_InterrompeIlConfronto()
    {
        var uid = Guid.NewGuid();
        var draft = FailedSaveDraftService.Save(_shared, "change-request", uid, 0, "QA",
            new { Uid = uid, Id = "../ALTRO", Titolo = "Non valido" }, "Offline", _local);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_shared, 0, 10, _local));

        var result = FailedSaveDraftComparisonService.Compare(_shared, row, _local);

        Assert.Equal(FailedSaveDraftComparisonService.ComparisonState.InvalidDraft, result.State);
        Assert.True(File.Exists(draft));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_shared)) Directory.Delete(_shared, true); } catch { }
        try { if (Directory.Exists(_local)) Directory.Delete(_local, true); } catch { }
    }
}
