using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura FlaUI dei master data non ancora esercitati: modifica di una riga ESISTENTE di Requisiti (cella Testo,
/// F2 e Invio) con effetto verificato sul catalogo persistito; archiviazione e ripristino dall'Archivio; aggiunta e rimozione
/// di un guasto su un requisito Extra (scheda guasto di Impostazioni). Seme diretto via AppState prima dell'avvio, come le altre
/// classi Master. Niente click a coordinate: le righe si selezionano dalla tastiera.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
[Trait("Category", "CRUD")]
public sealed class MasterGapsCoverageMasterDataUiTests : UiTestBase
{
    // La griglia Requisiti mostra le voci in ordine decrescente di chiave: REQ-AAA-EDIT-01 e' l'ultima riga (vedi
    // ModificaCellaTestoUltimaRiga, che lo verifica prima di scrivere).
    private const string ReqModifica = "REQ-AAA-EDIT-01";
    private const string ReqArchivia = "REQ-ZZZ-ARCH-01";
    private const string ReqD4d = "REQ-D4-guasto";
    private const string TestoIniziale = "aaa testo iniziale";
    private const string TestoModificato = "testo modificato da FlaUI";
    private const string DtcNuovo = "P0400";
    private const string Componente = TestWorkspace.DefaultEcuName;
    private const string Veh = TestWorkspace.DefaultVehicleName;

    /// <summary>Id della voce guasto nell'albero: include veicolo ed componente scelti (Requis_Guasto_Voce_{Veicolo}_{Componente}_{Codice}).</summary>
    private static string IdVoceDtc => $"Requis_Guasto_Voce_{Veh}_{Componente}_{DtcNuovo}";

    public MasterGapsCoverageMasterDataUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(MasterGapsCoverageMasterDataUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);

        var componente = state.Master.Componente.Load();
        componente.First(e => e.Nome == Componente).VeicoliAssociati = new List<string> { Veh };
        state.Master.Componente.Save(componente);

        var extra = new Requisito { Id = ReqD4d, Testo = "Requisito Extra per guasto", ApplicabileBanco = true, ModificatoDa = "UITests" };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, new[] { Veh });
        state.Master.Requisiti.Save(new List<Requisito>
        {
            new() { Id = ReqArchivia, Testo = "zzz requisito da archiviare", ModificatoDa = "UITests" },
            new() { Id = ReqModifica, Testo = TestoIniziale, ModificatoDa = "UITests" },
            extra,
        });
        return ws;
    }

    /// <summary>Riga della griglia che contiene la chiave indicata in una cella (la prima colonna e' la chiave).</summary>
    private static AutomationElement RigaConChiave(AutomationElement griglia, string chiave) =>
        Wait.ForElement(() => griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(r => r.FindAllDescendants(cf => cf.ByAutomationId("CellTextBlock")).Any(c => c.Name == chiave)),
            $"riga '{chiave}' nella griglia");

    /// <summary>Testi delle celle della riga, nell'ordine delle colonne.</summary>
    private static List<string> TestiCelle(AutomationElement riga) =>
        riga.FindAllDescendants(cf => cf.ByAutomationId("CellTextBlock")).Select(c => c.Name ?? string.Empty).ToList();

    /// <summary>Modifica la cella Testo della riga corrente della griglia, come ScriviChiaveUltimaRiga di MasterDataEditorPage.
    /// F2 agisce sulla riga e colonna correnti: la voce deve quindi essere l'ultima visibile (verificato prima di scrivere).
    /// Senza il tasto Destra F2 aprirebbe la colonna Id, quindi si passa alla colonna Testo. Il testo attuale dell'editor
    /// viene verificato; il nuovo valore e' impostato via ValuePattern (la digitazione carattere per carattere perdeva
    /// lettere nell'editor di cella) e confermato con Invio.</summary>
    private void ModificaCellaTestoUltimaRiga(MasterDataEditorPage pagina, string chiave, string testoAttuale, string nuovo)
    {
        var griglia = pagina.Grid;
        Assert.Equal(chiave, pagina.ChiaviVisibili().Last());
        griglia.Focus();
        Keyboard.Type(VirtualKeyShort.RIGHT);
        Keyboard.Type(VirtualKeyShort.F2);
        var editor = Wait.ForElement(() => griglia.FindFirstDescendant(cf => cf.ByAutomationId("CellTextBox")),
            "editor della cella Testo");
        var testoAperto = editor.AsTextBox().Text;
        if (testoAperto != testoAttuale)
            throw new InvalidOperationException(
                $"F2 ha aperto l'editor con testo '{testoAperto}' (riga '{ChiaveRigaDi(editor)}'), atteso '{testoAttuale}' sulla riga '{chiave}'.");
        // Il testo intero via ValuePattern: la digitazione carattere per carattere perde lettere nell'editor di cella.
        editor.AsTextBox().Text = nuovo;
        Wait.ForCondition(() => editor.AsTextBox().Text == nuovo, $"testo '{nuovo}' impostato nell'editor");
        Keyboard.Type(VirtualKeyShort.ENTER);
        Wait.ForCondition(() => griglia.FindFirstDescendant(cf => cf.ByAutomationId("CellTextBox")) == null,
            "editor della cella chiuso dopo Invio");
    }

    /// <summary>Chiave (prima cella) della riga che contiene l'elemento indicato, o null se non e' in una riga.</summary>
    private static string? ChiaveRigaDi(AutomationElement elemento)
    {
        for (var nodo = elemento.Parent; nodo != null; nodo = nodo.Parent)
        {
            if (nodo.ControlType == ControlType.DataItem)
                return nodo.FindFirstDescendant(cf => cf.ByAutomationId("CellTextBlock"))?.Name;
        }
        return null;
    }

    /// <summary>Porta la riga corrente della griglia alla riga di indice dato: molte frecce Su per arrivare in cima (Ctrl+Home
    /// non si e' rivelato affidabile), poi tante frecce Giu' quante sono le righe prima del bersaglio. Solo tastiera.</summary>
    private static void Vai(AutomationElement griglia, int indice)
    {
        griglia.Focus();
        for (var i = 0; i < 60; i++) Keyboard.Type(VirtualKeyShort.UP);
        for (var i = 0; i < indice; i++) Keyboard.Type(VirtualKeyShort.DOWN);
    }

    /// <summary>Seleziona la riga di indice dato (il pannello guasto dipende dalla riga selezionata).</summary>
    private static void SelezionaRigaPerIndice(AutomationElement griglia, int indice) => Vai(griglia, indice);

    /// <summary>guasto del requisito come li legge l'app (embedded o archivio requisiti-guasto), dal repository reale.</summary>
    private List<DtcRequisito> DtcPersistiti(string id) =>
        new RequisitoDtcArchivio(new DataPaths(Workspace.WorkspaceDir)).Voci(Persistito(Workspace, id));

    /// <summary>Requisiti persistiti (repository reale), per chiave.</summary>
    private static Requisito Persistito(TestWorkspace ws, string id) =>
        new MasterDataRepository<Requisito>(new DataPaths(ws.WorkspaceDir).RequisitiFile).Load().Single(r => r.Id == id);

    [Fact]
    public void Requisito_ModificaRigaEsistente_SalvaEPersisteDopoRicaricamento()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var requisiti = new MainShellPage(window).GoToImpostazioni().VaiARequisiti();
            requisiti.IniziaModifica();
            ModificaCellaTestoUltimaRiga(requisiti, ReqModifica, TestoIniziale, TestoModificato);

            requisiti.Salva();
            Wait.ForCondition(requisiti.PuoModificare, "ritorno in sola lettura dopo Salva");
            Wait.ForCondition(() => Persistito(Workspace, ReqModifica).Testo == TestoModificato,
                "requisito modificato persistito nel catalogo");

            // Ricaricamento: lasciare la sezione e tornarci deve mostrare il valore salvato.
            var impostazioni = new ImpostazioniPage(window);
            impostazioni.VaiA("componente");
            var riaperti = impostazioni.VaiARequisiti();
            Assert.Contains(TestoModificato, TestiCelle(RigaConChiave(riaperti.Grid, ReqModifica)));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Requisito_ModificaRigaEsistente_SalvaEPersisteDopoRicaricamento), ex);
        }
    }

    [Fact]
    public void Requisito_ArchiviaESalva_PoiRipristinaDallArchivio_TornaNelCatalogo()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var impostazioni = new MainShellPage(window).GoToImpostazioni();
            var requisiti = impostazioni.VaiARequisiti();
            requisiti.IniziaModifica();
            Assert.Contains(ReqArchivia, requisiti.ChiaviVisibili());

            requisiti.ArchiviaRiga(ReqArchivia);
            Wait.ForCondition(() => !requisiti.ChiaviVisibili().Contains(ReqArchivia), "riga archiviata fuori dalla griglia");
            requisiti.Salva();
            Wait.ForCondition(requisiti.PuoModificare, "ritorno in sola lettura dopo Salva");
            Wait.ForCondition(() => Persistito(Workspace, ReqArchivia).Archiviata, "requisito marcato come archiviato nel catalogo");

            // Archivio: il requisito compare tra le voci archiviate e Ripristina lo riporta nel catalogo.
            impostazioni.VaiA("archivio");
            var griglia = ElementFinders.WaitById(window, "Archivio_Grid");
            Wait.ForElement(() => griglia.FindAllDescendants(cf => cf.ByAutomationId("CellTextBlock"))
                .FirstOrDefault(c => c.Name == ReqArchivia), $"'{ReqArchivia}' tra le voci dell'Archivio");
            ElementFinders.WaitButtonById(window, "Archivio_Ripristina_Button").SafeInvoke();
            Wait.ForCondition(() => !Persistito(Workspace, ReqArchivia).Archiviata, "requisito ripristinato nel catalogo");

            var riaperti = impostazioni.VaiARequisiti();
            Wait.ForCondition(() => riaperti.ChiaviVisibili().Contains(ReqArchivia), "requisito ripristinato visibile nella griglia");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Requisito_ArchiviaESalva_PoiRipristinaDallArchivio_TornaNelCatalogo), ex);
        }
    }

    [Fact]
    public void RequisitoD4d_AggiungeEdEliminaUnDtc_PersisteNelCatalogo()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var impostazioni = new MainShellPage(window).GoToImpostazioni();
            impostazioni.VaiA("requisitid4d");
            var extra = new MasterDataEditorPage(window, "Requis");
            Wait.ForCondition(() => extra.ChiaviVisibili().Contains(ReqD4d), "requisito Extra in griglia");
            extra.IniziaModifica();
            SelezionaRigaPerIndice(extra.Grid, extra.ChiaviVisibili().ToList().IndexOf(ReqD4d));

            ElementFinders.WaitById(window, "Requis_Guasto_Expander").Patterns.ExpandCollapse.PatternOrDefault?.Expand();
            ElementFinders.WaitComboBoxById(window, "Requis_Guasto_Veicolo_Combo").SelectByText(Veh);
            ElementFinders.WaitComboBoxById(window, "Requis_Guasto_Componente_Combo").SelectByText(Componente);
            ElementFinders.WaitTextBoxById(window, "Requis_Guasto_Codice_TextBox").SetText(DtcNuovo);
            ElementFinders.WaitButtonById(window, "Requis_Guasto_Aggiungi_Button").SafeInvoke();
            ElementFinders.WaitById(window, IdVoceDtc);

            extra.Salva();
            Wait.ForCondition(extra.PuoModificare, "ritorno in sola lettura dopo Salva (aggiunta guasto)");
            Wait.ForCondition(() => DtcPersistiti(ReqD4d).Any(v => v.Codice == DtcNuovo),
                "Guasto aggiunto persistito nel requisito Extra");

            // Rimozione: stesso percorso, poi il pulsante di rimozione della voce guasto (✕ dentro la voce).
            extra.IniziaModifica();
            SelezionaRigaPerIndice(extra.Grid, extra.ChiaviVisibili().ToList().IndexOf(ReqD4d));
            ElementFinders.WaitById(window, "Requis_Guasto_Expander").Patterns.ExpandCollapse.PatternOrDefault?.Expand();
            var voce = ElementFinders.WaitById(window, IdVoceDtc);
            var rimuovi = Wait.ForElement(() => voce.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("✕"))),
                "pulsante di rimozione della voce guasto");
            rimuovi.AsButton().SafeInvoke();
            Wait.ForCondition(() => ElementFinders.TryFindById(window, IdVoceDtc) == null,
                "voce guasto rimossa dall'albero");

            extra.Salva();
            Wait.ForCondition(extra.PuoModificare, "ritorno in sola lettura dopo Salva (rimozione guasto)");
            Wait.ForCondition(() => !DtcPersistiti(ReqD4d).Any(v => v.Codice == DtcNuovo),
                "Guasto rimosso dal requisito Extra nel catalogo");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RequisitoD4d_AggiungeEdEliminaUnDtc_PersisteNelCatalogo), ex);
        }
    }
}
