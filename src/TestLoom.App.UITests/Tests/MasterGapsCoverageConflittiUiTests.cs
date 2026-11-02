using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura FlaUI della card "Conflitti nei cataloghi master" (Impostazioni, sezione Generale). Il conflitto e' seminato
/// sul workspace PRIMA dell'avvio di TestLoom con la stessa tecnica di MasterCatalogMultiPcTests.CreaConflittoRequisiti:
/// una voce di Requisiti con due batch concorrenti del journal Phase D, pubblicati da due device sintetici a partire
/// dalla stessa testa. Dopo l'avvio il JSON non viene piu' toccato: lo stato visibile in UI e' quello del journal.
/// Scenari: elenco delle due versioni dopo Aggiorna; "Usa questa versione" confermato (conflitto risolto, catalogo
/// con il valore scelto); "Usa questa versione" annullato (nessuna scrittura nel journal).
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Conflitti")]
public sealed class MasterGapsCoverageConflittiUiTests : UiTestBase
{
    private const string ChiaveVoce = "REQ-CONF-01";
    private const string TestoBase = "testo base";
    private const string TestoVersioneB = "versione del PC B";
    private const string TestoVersioneC = "versione del PC C";
    private const string DispositivoB = "b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0";
    private const string DispositivoC = "c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0";
    private const string Catalogo = "requisiti";

    public MasterGapsCoverageConflittiUiTests()
        : base(SeminaConflittoRequisito(nameof(MasterGapsCoverageConflittiUiTests))) { }

    /// <summary>Workspace Master pronto con una voce di Requisiti in conflitto (due versioni concorrenti).</summary>
    private static TestWorkspace SeminaConflittoRequisito(string prefisso)
    {
        var ws = TestWorkspace.CreateLoggedIn(prefisso);
        var root = ws.WorkspaceDir;
        var paths = new DataPaths(root);
        paths.EnsureStructure();

        // Voce base nel JSON legacy, poi migrazione a Phase D come fa l'app all'attivazione.
        new MasterDataRepository<Requisito>(paths.RequisitiFile)
            .Save(new List<Requisito> { new() { Id = ChiaveVoce, Testo = TestoBase } });
        PhaseDMasterBootstrapService.EnsureReady(paths);

        // Testa osservata prima delle due modifiche concorrenti: base comune di entrambi i device.
        var repo = new MasterDataRepository<Requisito>(paths.RequisitiFile);
        var uid = MasterEntityIdentityService.GetRequired(repo.Load().Single(r => r.Id == ChiaveVoce));
        var vvBase = new PhaseDMasterMaterializer(root).LoadCatalog<Requisito>(root, Catalogo).Heads[uid].VersionVector;

        var rigaB = repo.Load().Single(r => r.Id == ChiaveVoce);
        rigaB.Testo = TestoVersioneB;
        PubblicaBatch(root, DispositivoB, MutazioneUpsert(rigaB, vvBase, DispositivoB));

        var rigaC = repo.Load().Single(r => r.Id == ChiaveVoce);
        rigaC.Testo = TestoVersioneC;
        PubblicaBatch(root, DispositivoC, MutazioneUpsert(rigaC, vvBase, DispositivoC));
        return ws;
    }

    /// <summary>Mutation Upsert nel formato di PhaseDMasterRepositoryEngine.CreateUpsert, firmata dal device indicato.</summary>
    private static PhaseDMasterMutation MutazioneUpsert(Requisito riga, IReadOnlyDictionary<string, long> vvOsservato, string dispositivo)
    {
        var payload = JsonSerializer.Serialize(riga, JsonIo.CompactOptions);
        return new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = Catalogo,
            EntityUid = MasterEntityIdentityService.GetRequired(riga),
            Operation = PhaseDMasterMutationOperation.Upsert,
            VersionVector = PhaseDVersionVector.Increment(vvOsservato, dispositivo),
            PayloadJson = payload,
            PayloadSha256 = PhaseDMasterMutation.HashPayload(payload),
            BusinessKey = PhaseDMasterBusinessKeyService.Get(riga),
            TimestampUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Pubblica un batch (sequenza 1) nel journal del device indicato, con lo stesso formato di Append.</summary>
    private static void PubblicaBatch(string root, string dispositivo, PhaseDMasterMutation mutazione)
    {
        var batch = new PhaseDMasterMutationBatch
        {
            BatchId = Guid.NewGuid(),
            DeviceId = dispositivo,
            DeviceSequence = 1,
            CreatedUtc = DateTime.UtcNow,
            Mutations = new List<PhaseDMasterMutation> { mutazione },
        };
        batch.Validate();
        var current = PhaseDMasterJournalService.CurrentPath(root, dispositivo);
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.AppendAllText(current, JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine);
    }

    /// <summary>Identificativo della versione concorrente il cui payload contiene il testo indicato (letto dal journal).</summary>
    private string IdVersione(string testo)
    {
        var root = Workspace.WorkspaceDir;
        var conflitto = Assert.Single(new PhaseDMasterMaterializer(root).LoadConflicts(root, Catalogo));
        var head = conflitto.ConcurrentHeads.Single(m =>
            JsonSerializer.Deserialize<Requisito>(m.PayloadJson, JsonIo.Options)!.Testo == testo);
        return PhaseDMasterConflictResolver.VersionIdOf(head.Operation, head.PayloadSha256);
    }

    /// <summary>Id del pulsante "Usa questa versione" della card: stesso schema di MasterConflittoVersioneRiga.AutomationId.</summary>
    private static string IdPulsanteUsa(string versionId) =>
        $"MasterConflict_Usa_{(versionId.Length >= 8 ? versionId[..8] : versionId)}_Button";

    /// <summary>Conflitti ancora presenti nel journal, letti dal percorso reale usato dall'app.</summary>
    private int ConflittiNelJournal() => PhaseDMasterConflictResolver.Elenca(Workspace.WorkspaceDir).Count;

    /// <summary>Testo del Requisito persistito, letto con il repository reale (Phase D).</summary>
    private string TestoPersistito() =>
        new MasterDataRepository<Requisito>(new DataPaths(Workspace.WorkspaceDir).RequisitiFile)
            .Load().Single(r => r.Id == ChiaveVoce).Testo;

    /// <summary>Apre Impostazioni, Generale (dove vive la card) e preme Aggiorna: attende il titolo del conflitto.
    /// Il Border della card non e' esposto a UI Automation (come RecordFork_Card): si ancora ai figli che lo sono.</summary>
    private Window ApriCardConflitti()
    {
        var window = App.WaitForMainWindow();
        new MainShellPage(window).GoToImpostazioni().VaiA("generale");
        ElementFinders.WaitButtonById(window, "MasterConflict_Aggiorna_Button").SafeInvoke();
        Wait.ForElement(() => window.FindFirstDescendant(cf => cf.ByName($"{Catalogo} · {ChiaveVoce}")),
            "titolo del conflitto nella card dopo Aggiorna");
        return window;
    }

    [Fact]
    public void Conflitto_AggiornaMostraLeDueVersioni_E_UsaQuestaVersioneRisolveELaVoceMostraLaScelta()
    {
        try
        {
            var window = ApriCardConflitti();
            var idB = IdPulsanteUsa(IdVersione(TestoVersioneB));
            var idC = IdPulsanteUsa(IdVersione(TestoVersioneC));
            ElementFinders.WaitById(window, idB);
            ElementFinders.WaitById(window, idC);
            Assert.Equal(2, PhaseDMasterConflictResolver.Elenca(Workspace.WorkspaceDir).Single().Versioni.Count);

            ElementFinders.WaitButtonById(window, idC).SafeInvoke();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma 'Usa questa versione'");

            Wait.ForCondition(() => ElementFinders.TryFindById(window, idB) == null && ElementFinders.TryFindById(window, idC) == null,
                "pulsanti delle versioni spariti dalla card dopo la risoluzione");
            Wait.ForCondition(() => ConflittiNelJournal() == 0, "journal senza conflitti dopo la scelta");
            Assert.Equal(TestoVersioneC, TestoPersistito());

            // Il catalogo mostra il valore scelto: la riga di REQ-CONF-01 nella sezione Requisiti.
            var requisiti = new ImpostazioniPage(window).VaiARequisiti();
            Assert.Contains(ChiaveVoce, requisiti.ChiaviVisibili());
            Assert.Contains(TestoVersioneC, TestiRiga(window, ChiaveVoce));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Conflitto_AggiornaMostraLeDueVersioni_E_UsaQuestaVersioneRisolveELaVoceMostraLaScelta), ex);
        }
    }

    [Fact]
    public void Conflitto_UsaQuestaVersioneAnnullata_NonScriveNelJournalENonRisolve()
    {
        try
        {
            var window = ApriCardConflitti();
            var idB = IdPulsanteUsa(IdVersione(TestoVersioneB));
            var idC = IdPulsanteUsa(IdVersione(TestoVersioneC));
            ElementFinders.WaitById(window, idB);
            ElementFinders.WaitById(window, idC);
            var mutazioniPrima = PhaseDMasterJournalService.ReadAllMutations(Workspace.WorkspaceDir).Count;

            ElementFinders.WaitButtonById(window, idC).SafeInvoke();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma dopo No");

            Assert.NotNull(ElementFinders.TryFindById(window, idB));
            Assert.NotNull(ElementFinders.TryFindById(window, idC));
            Assert.Equal(mutazioniPrima, PhaseDMasterJournalService.ReadAllMutations(Workspace.WorkspaceDir).Count);
            Assert.Equal(1, ConflittiNelJournal());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Conflitto_UsaQuestaVersioneAnnullata_NonScriveNelJournalENonRisolve), ex);
        }
    }

    /// <summary>Testi (celle) della riga della voce indicata nella griglia Requisiti.</summary>
    private static List<string> TestiRiga(AutomationElement scope, string chiave)
    {
        var riga = Wait.ForElement(() => scope.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(r => r.FindAllDescendants(cf => cf.ByAutomationId("CellTextBlock")).Any(c => c.Name == chiave)),
            $"riga '{chiave}' nel catalogo Requisiti");
        return riga.FindAllDescendants(cf => cf.ByAutomationId("CellTextBlock")).Select(c => c.Name ?? string.Empty).ToList();
    }
}
