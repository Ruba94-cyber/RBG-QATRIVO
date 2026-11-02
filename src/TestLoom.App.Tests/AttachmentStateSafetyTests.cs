using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class AttachmentStateSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_attachment_safety_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EliminazioneAllegato_MantieneUnaCopiaRecuperabileSeIlJsonLoReferenziaAncora()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var tc = new TestCase { Id = "TC-ATT-1", Componente = "BCM" };
        var source = Path.Combine(_dir, "trace.log");
        File.WriteAllText(source, "evidenza diagnostica");
        var allegato = AttachmentService.Salva(paths, tc, source, "Tester");
        tc.Allegati.Add(allegato);
        var prima = AttachmentService.PercorsoCompleto(paths, tc, allegato);
        Assert.True(File.Exists(prima));

        AttachmentService.Elimina(paths, tc, allegato);

        var recuperabile = AttachmentService.PercorsoCompleto(paths, tc, allegato);
        Assert.True(File.Exists(recuperabile));
        Assert.Equal("evidenza diagnostica", File.ReadAllText(recuperabile));
        Assert.Equal(allegato.Sha256, AttachmentService.CalcolaSha256(recuperabile));
    }

    [Fact]
    public void ScartoEsplicito_RipristinaNelPercorsoAttivo_UnAllegatoAutorevoleRimossoNelForm()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var tc = new TestCase { Id = "TC-ATT-RESTORE", Componente = "BCM" };
        var source = Path.Combine(_dir, "authoritative.log");
        File.WriteAllText(source, "stato autorevole");
        var allegato = AttachmentService.Salva(paths, tc, source, "Tester");
        tc.Allegati.Add(allegato);
        var snapshot = EntityStateFingerprint.Clone(tc);
        var active = Path.Combine(paths.AllegatiDir(tc), allegato.NomeFile);
        Assert.True(File.Exists(active));

        AttachmentService.Elimina(paths, tc, allegato);
        tc.Allegati.Remove(allegato);
        Assert.False(File.Exists(active));
        Assert.True(File.Exists(AttachmentService.PercorsoCompleto(paths, snapshot, snapshot.Allegati.Single())));

        DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, tc, snapshot);

        Assert.True(File.Exists(active));
        Assert.Equal("stato autorevole", File.ReadAllText(active));
        Assert.Equal(allegato.Sha256, AttachmentService.CalcolaSha256(active));
    }

    [Fact]
    public void ScartoEsplicito_EliminaAllegatoMaiAutorevole_AggiuntoNellaSessione()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var snapshot = new TestCase { Id = "TC-ATT-NEW", Componente = "BCM" };
        var working = EntityStateFingerprint.Clone(snapshot);
        var source = Path.Combine(_dir, "new-only.txt");
        File.WriteAllText(source, "solo bozza");
        var allegato = AttachmentService.Salva(paths, working, source, "Tester");
        working.Allegati.Add(allegato);
        var active = Path.Combine(paths.AllegatiDir(working), allegato.NomeFile);
        Assert.True(File.Exists(active));

        DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, working, snapshot, [allegato.Uid]);

        Assert.False(File.Exists(active));
        var recoveryDir = Path.Combine(paths.AllegatiDir(working), ".testloom-recovery");
        Assert.False(Directory.Exists(recoveryDir) && Directory.EnumerateFiles(recoveryDir, allegato.Uid.ToString("N") + ".*").Any());
    }

    [Fact]
    public void ScartoEsplicito_EliminaAncheRecovery_DiAllegatoNuovoPoiRimosso()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var snapshot = new TestCase { Id = "TC-ATT-NEW-REMOVED", Componente = "BCM" };
        var working = EntityStateFingerprint.Clone(snapshot);
        var source = Path.Combine(_dir, "new-then-removed.txt");
        File.WriteAllText(source, "mai autorevole");
        var allegato = AttachmentService.Salva(paths, working, source, "Tester");
        working.Allegati.Add(allegato);

        AttachmentService.Elimina(paths, working, allegato);
        working.Allegati.Remove(allegato);
        var recoveryDir = Path.Combine(paths.AllegatiDir(working), ".testloom-recovery");
        Assert.True(Directory.Exists(recoveryDir));
        Assert.NotEmpty(Directory.EnumerateFiles(recoveryDir, allegato.Uid.ToString("N") + ".*"));

        DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, working, snapshot, [allegato.Uid]);

        Assert.False(Directory.Exists(recoveryDir) && Directory.EnumerateFiles(recoveryDir, allegato.Uid.ToString("N") + ".*").Any());
    }

    [Fact]
    public void TrackingSporco_NonCancellaMaiUnAllegatoPresenteNelloSnapshotAutorevole()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var tc = new TestCase { Id = "TC-ATT-FALSE-POSITIVE", Componente = "BCM" };
        var source = Path.Combine(_dir, "keep.log");
        File.WriteAllText(source, "da conservare");
        var allegato = AttachmentService.Salva(paths, tc, source, "Tester");
        tc.Allegati.Add(allegato);
        var snapshot = EntityStateFingerprint.Clone(tc);
        var active = Path.Combine(paths.AllegatiDir(tc), allegato.NomeFile);

        // Simula un Reset/Add della collection UI che abbia marcato erroneamente l'UID come aggiunto in sessione.
        DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, tc, snapshot, [allegato.Uid]);

        Assert.True(File.Exists(active));
        Assert.Equal("da conservare", File.ReadAllText(active));
    }

    [Fact]
    [Trait("Category", "Lento")]
    public async Task AllegatoSuTestNuovo_RestaBozzaFinoAlSalvaEsplicito()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var operatore = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        state.ImpostaOperatore(operatore.Nome, "1234");
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        var source = Path.Combine(_dir, "evidence.txt");
        File.WriteAllText(source, "test evidence");

        await vm.AggiungiAllegatoSicuro(source);

        Assert.False(string.IsNullOrWhiteSpace(vm.TestCase.Id));
        Assert.Single(vm.TestCase.Allegati);
        Assert.True(vm.ModificheNonSalvate);
        Assert.Null(state.TestCases.GetById(vm.TestCase.Id));
        Assert.NotNull(state.BozzaAutosave.Carica(state.Config.OperatoreAttivo));
    }

    [Fact]
    [Trait("Category", "Lento")]
    public async Task ExportPolarionSuTestNuovo_NonCreaUnJsonCondivisoImplicito()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var operatore = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        state.ImpostaOperatore(operatore.Nome, "1234");
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        var output = Path.Combine(_dir, "ticketing-safe");

        await vm.CreaTicketSicuro(new RichiestaPacchettoPolarion(output, new HashSet<string> { PolarionSezioni.DatiGenerali }));

        Assert.False(string.IsNullOrWhiteSpace(vm.TestCase.Id));
        Assert.Null(state.TestCases.GetById(vm.TestCase.Id));
        Assert.True(Directory.Exists(output));
        Assert.NotEmpty(Directory.GetFiles(output, "*.zip"));
    }

    [Fact]
    public async Task ScartoChangeRequest_RipristinaUnAllegatoAutorevoleRimossoNellaSessione()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var cr = new ChangeRequest { Id = "CR-ATT-RESTORE" };
        var source = Path.Combine(_dir, "cr-authoritative.txt");
        File.WriteAllText(source, "evidenza CR autorevole");
        var allegato = await AttachmentService.SalvaAsync(paths, cr, source, "Tester");
        cr.Allegati.Add(allegato);
        var snapshot = EntityStateFingerprint.Clone(cr);
        var active = Path.Combine(paths.AllegatiDirChangeRequest(cr), allegato.NomeFile);

        AttachmentService.Elimina(paths, cr, allegato);
        cr.Allegati.Remove(allegato);
        Assert.False(File.Exists(active));

        DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, cr, snapshot);

        Assert.True(File.Exists(active));
        Assert.Equal("evidenza CR autorevole", File.ReadAllText(active));
        Assert.Equal(allegato.Sha256, AttachmentService.CalcolaSha256(active));
    }

    [Fact]
    public async Task ScartoChangeRequest_NonCancellaUnPathRimpiazzatoDaByteConcorrenti()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var authoritative = new ChangeRequest { Id = "CR-ATT-CONCURRENT" };
        var working = EntityStateFingerprint.Clone(authoritative);
        var source = Path.Combine(_dir, "same-name.txt");
        File.WriteAllText(source, "staging locale");
        var allegato = await AttachmentService.SalvaAsync(paths, working, source, "Tester");
        working.Allegati.Add(allegato);
        var active = Path.Combine(paths.AllegatiDirChangeRequest(working), allegato.NomeFile);

        // Simula una replica/operazione concorrente che occupa lo stesso path con contenuto diverso prima che
        // l'utente confermi lo scarto. L'UID locale da solo non autorizza mai la delete di questi nuovi byte.
        File.WriteAllText(active, "contenuto concorrente da preservare");

        DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, working, authoritative);

        Assert.True(File.Exists(active));
        Assert.Equal("contenuto concorrente da preservare", File.ReadAllText(active));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }
}
