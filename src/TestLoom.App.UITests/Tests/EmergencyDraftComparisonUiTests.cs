using System.Security.Cryptography;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Scenario FlaUI UIA3: lettura bozza + documento canonico della stessa CR,
/// verificando l'assenza di modifiche. Tutti i documenti sono fixture temporanee
/// e il confronto NON chiama i metodi Save dei repository TestLoom.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Category", "Recovery")]
public sealed class EmergencyDraftComparisonUiTests : UiTestBase
{
    private const string CaseId = "CR-QA-COMPARE-01";
    public EmergencyDraftComparisonUiTests() : base(CreateWorkspace()) { }

    private static TestWorkspace CreateWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(EmergencyDraftComparisonUiTests));
        var cr = new ChangeRequest
        {
            Id = CaseId, Titolo = "QA_COMPARE_ORIGINAL", Descrizione = "Solo dati temporanei",
            RevisioneDati = 3, EcuImpattate = new() { TestWorkspace.DefaultEcuName }
        };
        var paths = new DataPaths(ws.WorkspaceDir);
        Directory.CreateDirectory(paths.ChangeRequestDir);
        JsonIo.WriteAtomic(paths.ChangeRequestFile(cr.Id), cr);
        FailedSaveDraftService.Save(ws.WorkspaceDir, "change-request", cr.Uid, 3,
            "QA FlaUI", cr, "Simulazione salvataggio fallito", baseSnapshot: cr);
        return ws;
    }

    private string Canonical => new DataPaths(Workspace.WorkspaceDir).ChangeRequestFile(CaseId);

    private FailedSaveDraftInspectorPage Open()
    {
        var w = App.WaitForMainWindow();
        var shell = new MainShellPage(w);
        shell.GoToImpostazioni().VaiA("generale");
        var dialog = new Post653ImpostazioniPage(w, App).EsaminaBozze();
        Wait.ForCondition(() => dialog.Status.Contains("Pagina 1"), "bozze caricate");
        dialog.SelectRow(0);
        Assert.True(dialog.CanCompare);
        return dialog;
    }

    [Fact]
    public void ConfrontoUguale_MostraEntrambeLeColonne_NonScriveSulDataset()
    {
        try
        {
            var before = File.ReadAllBytes(Canonical);
            var dialog = Open();
            dialog.CompareLocal();

            Wait.ForCondition(() => dialog.ComparisonStatus.Contains("Payload uguale"),
                "payload uguali sul solo filesystem locale");
            Assert.Contains("QA_COMPARE_ORIGINAL", dialog.Preview);
            Assert.Contains("QA_COMPARE_ORIGINAL", dialog.CanonicalLocalPreview);
            Assert.Contains("Nessuna differenza", dialog.FieldDifferences);
            Assert.Contains("OneDrive", dialog.ComparisonStatus);
            Assert.Equal(before, File.ReadAllBytes(Canonical));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ConfrontoUguale_MostraEntrambeLeColonne_NonScriveSulDataset), ex); }
    }

    [Fact]
    public void ConfrontoDivergente_IndicaRevisioneLocale_EConservaLaVersioneAttuale()
    {
        try
        {
            var current = JsonIo.TryRead<ChangeRequest>(Canonical, out var err)!;
            Assert.Null(err);
            current.RevisioneDati = 4;
            current.Titolo = "QA_COMPARE_DIVERSO";
            JsonIo.WriteAtomic(Canonical, current);
            var before = SHA256.HashData(File.ReadAllBytes(Canonical));

            var dialog = Open();
            dialog.CompareLocal();

            Wait.ForCondition(() => dialog.ComparisonStatus.Contains("Payload diverso"),
                "divergenza locale rilevata");
            Assert.Contains("revisione attesa 3", dialog.ComparisonStatus);
            Assert.Contains("QA_COMPARE_DIVERSO", dialog.CanonicalLocalPreview);
            Assert.Contains("QA_COMPARE_ORIGINAL", dialog.Preview);
            Assert.Contains("/Titolo", dialog.FieldDifferences);
            Assert.Contains("/RevisioneDati", dialog.FieldDifferences);
            Assert.DoesNotContain("QA_COMPARE_DIVERSO", dialog.FieldDifferences);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(Canonical)));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ConfrontoDivergente_IndicaRevisioneLocale_EConservaLaVersioneAttuale), ex); }
    }

    [Fact]
    public void DifferenzeCampiCustom_NonRiportanoIValoriDeiDatiSensibili()
    {
        try
        {
            var current = JsonIo.TryRead<ChangeRequest>(Canonical, out var err)!;
            Assert.Null(err);
            current.CampiCustom["ResponsabileReparto"] = "SEGRETO_QA_LOCAL";
            JsonIo.WriteAtomic(Canonical, current);
            var before = File.ReadAllBytes(Canonical);

            var dialog = Open();
            dialog.CompareLocal();

            Wait.ForCondition(() => dialog.FieldDifferences.Contains("/CampiCustom/ResponsabileReparto"),
                "campo aggiuntivo visibile fra le differenze");
            Assert.Contains("Solo documento locale", dialog.FieldDifferences);
            Assert.DoesNotContain("SEGRETO_QA_LOCAL", dialog.FieldDifferences);
            Assert.Equal(before, File.ReadAllBytes(Canonical));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(DifferenzeCampiCustom_NonRiportanoIValoriDeiDatiSensibili), ex); }
    }

    [Fact]
    public void DopoConfrontoAggiorna_PulisceLeDifferenzePrecedenti()
    {
        try
        {
            var current = JsonIo.TryRead<ChangeRequest>(Canonical, out var err)!;
            Assert.Null(err);
            current.Titolo = "DIVERSO_TEST_QA";
            JsonIo.WriteAtomic(Canonical, current);

            var dialog = Open();
            dialog.CompareLocal();
            Wait.ForCondition(() => dialog.FieldDifferences.Contains("/Titolo"),
                "diff visibile");
            dialog.Refresh();
            Wait.ForCondition(() => dialog.Status.Contains("Pagina 1") &&
                dialog.FieldDifferences.Contains("Differenze non ancora calcolate"),
                "reset del confronto dopo refresh");

            Assert.DoesNotContain("/Titolo", dialog.FieldDifferences);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(DopoConfrontoAggiorna_PulisceLeDifferenzePrecedenti), ex); }
    }

    [Fact]
    public void TreVersioni_CampiSovrapposti_LInterfacciaAvverteESenzaMerge()
    {
        try
        {
            var directory = FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir);
            foreach (var old in Directory.EnumerateFiles(directory, "*.json"))
                File.Delete(old); // solo dati della fixture isolata

            var original = JsonIo.TryRead<ChangeRequest>(Canonical, out var err)!;
            Assert.Null(err);
            var userEdit = JsonIo.TryRead<ChangeRequest>(Canonical, out err)!;
            Assert.Null(err);
            userEdit.Titolo = "PROPOSTA_OPERATORE";
            var localEdit = JsonIo.TryRead<ChangeRequest>(Canonical, out err)!;
            Assert.Null(err);
            localEdit.Titolo = "MODIFICA_ALTRO_PC";
            FailedSaveDraftService.Save(Workspace.WorkspaceDir, "change-request",
                original.Uid, 3, "QA FlaUI", userEdit, "Conflitto",
                baseSnapshot: original);
            JsonIo.WriteAtomic(Canonical, localEdit);
            var before = File.ReadAllBytes(Canonical);

            var dialog = Open();
            dialog.CompareLocal();
            Wait.ForCondition(() => dialog.FieldDifferences.Contains("Campi sovrapposti"),
                "sovrapposizione tre versioni");
            Assert.Contains("/Titolo", dialog.FieldDifferences);
            Assert.DoesNotContain("PROPOSTA_OPERATORE", dialog.FieldDifferences);
            Assert.DoesNotContain("MODIFICA_ALTRO_PC", dialog.FieldDifferences);
            Assert.Equal(before, File.ReadAllBytes(Canonical));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(TreVersioni_CampiSovrapposti_LInterfacciaAvverteESenzaMerge), ex); }
    }

    [Fact]
    public void DocumentoAssente_NonVieneRicreatoDalConfronto()
    {
        try
        {
            File.Delete(Canonical);
            var dialog = Open();
            dialog.CompareLocal();

            Wait.ForCondition(() => dialog.ComparisonStatus.Contains("non presente localmente"),
                "file canonico mancante segnalato");
            Assert.Contains("non disponibile", dialog.CanonicalLocalPreview);
            Assert.False(File.Exists(Canonical));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(DocumentoAssente_NonVieneRicreatoDalConfronto), ex); }
    }

    [Fact]
    public void UidDiverso_NonVieneInterpretatoComeRecordIdentico()
    {
        try
        {
            var current = JsonIo.TryRead<ChangeRequest>(Canonical, out var err)!;
            Assert.Null(err);
            current.Uid = Guid.NewGuid();
            JsonIo.WriteAtomic(Canonical, current);
            var before = File.ReadAllBytes(Canonical);

            var dialog = Open();
            dialog.CompareLocal();

            Wait.ForCondition(() => dialog.ComparisonStatus.Contains("UID non corrispondente"),
                "conflitto identità documento locale");
            Assert.Equal(before, File.ReadAllBytes(Canonical));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(UidDiverso_NonVieneInterpretatoComeRecordIdentico), ex); }
    }

    [Fact]
    public void RevisioneLocalePiuVecchia_ProduceAvvisoSenzaRiscrivereFile()
    {
        try
        {
            var current = JsonIo.TryRead<ChangeRequest>(Canonical, out var err)!;
            Assert.Null(err);
            current.RevisioneDati = 1;
            JsonIo.WriteAtomic(Canonical, current);
            var before = File.ReadAllBytes(Canonical);

            var dialog = Open();
            dialog.CompareLocal();

            Wait.ForCondition(() => dialog.ComparisonStatus.Contains("Revisione locale più vecchia"),
                "avviso relativo a replica locale potenzialmente datata");
            Assert.Contains("OneDrive", dialog.ComparisonStatus);
            Assert.Equal(before, File.ReadAllBytes(Canonical));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(RevisioneLocalePiuVecchia_ProduceAvvisoSenzaRiscrivereFile), ex); }
    }

    [Fact]
    public void TombstoneLocale_NonVieneMaiRipristinatoDalConfronto()
    {
        try
        {
            var current = JsonIo.TryRead<ChangeRequest>(Canonical, out var err)!;
            Assert.Null(err);
            JsonIo.WriteAtomic(Canonical, new Dictionary<string, object>
            {
                ["Uid"] = current.Uid, ["Id"] = current.Id,
                ["RevisioneDati"] = 8L, ["_ditmDeleted"] = true
            });
            var bytes = File.ReadAllBytes(Canonical);

            var dialog = Open();
            dialog.CompareLocal();

            Wait.ForCondition(() => dialog.ComparisonStatus.Contains("eliminato logicamente"),
                "tombstone locale visibile come avviso senza restore");
            Assert.Equal(bytes, File.ReadAllBytes(Canonical));
            Assert.Contains("_ditmDeleted", dialog.CanonicalLocalPreview);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(TombstoneLocale_NonVieneMaiRipristinatoDalConfronto), ex); }
    }

    [Fact]
    public void JsonCanonicoCorrotto_NonVieneNascostoOReinterpretato()
    {
        try
        {
            File.WriteAllText(Canonical, "{ invalid");
            var dialog = Open();
            dialog.CompareLocal();

            Wait.ForCondition(() => dialog.ComparisonStatus.Contains("non leggibile"),
                "documento locale corrotto segnalato");
            Assert.Equal("{ invalid", File.ReadAllText(Canonical));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(JsonCanonicoCorrotto_NonVieneNascostoOReinterpretato), ex); }
    }

    [Fact]
    public void IdCanonicoNonCorrispondente_VieneSegnalato()
    {
        try
        {
            var current = JsonIo.TryRead<ChangeRequest>(Canonical, out var err)!;
            Assert.Null(err);
            current.Id = "CR-ALTRO-ID";
            JsonIo.WriteAtomic(Canonical, current);
            var before = File.ReadAllBytes(Canonical);

            var dialog = Open();
            dialog.CompareLocal();

            Wait.ForCondition(() => dialog.ComparisonStatus.Contains("ID canonico diverso"),
                "ID locale discordante");
            Assert.Equal(before, File.ReadAllBytes(Canonical));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(IdCanonicoNonCorrispondente_VieneSegnalato), ex); }
    }

    [Fact]
    public void BozzaAlterataDopoApertura_RifiutaIlConfrontoSenzaSovrascritture()
    {
        try
        {
            var dialog = Open();
            var directory = FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir);
            var draft = Assert.Single(Directory.EnumerateFiles(directory, "*.json"));
            var entry = JsonIo.TryRead<FailedSaveDraftService.DraftEnvelope>(draft, out var err)!;
            Assert.Null(err);
            entry.Error = "DRAFT CAMBIATA";
            JsonIo.WriteAtomic(draft, entry);
            var before = File.ReadAllBytes(Canonical);

            dialog.CompareLocal();

            Wait.ForCondition(() => dialog.ComparisonStatus.Contains("Bozza modificata"),
                "stale draft detected");
            Assert.Equal(before, File.ReadAllBytes(Canonical));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(BozzaAlterataDopoApertura_RifiutaIlConfrontoSenzaSovrascritture), ex); }
    }
}
