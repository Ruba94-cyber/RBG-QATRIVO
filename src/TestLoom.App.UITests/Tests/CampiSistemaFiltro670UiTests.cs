using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Keyboard = FlaUI.Core.Input.Keyboard;
using VirtualKeyShort = FlaUI.Core.WindowsAPI.VirtualKeyShort;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Storia utente 6.7.0 (RELEASE_NOTES_6.7.0.md, "Filtri sui campi di sistema"): un Master crea un campo di sistema in
/// Impostazioni → Tipi di test → Modifica → Gestisci campi → Gestione campi di sistema, lo marca «Usabile come filtro»,
/// lo aggiunge al template e ne assegna il valore a due Test; poi filtra Home e Report per quel campo.
/// Il pannello Impostazioni non ha una voce propria per il catalogo: il catalogo si apre solo dall'editor dei campi
/// del template, che è il percorso reale dell'utente.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class CampiSistemaFiltro670UiTests : UiTestBase
{
    private const string Etichetta = "Area Prova";
    private const string ValoreA = "Alfa";
    private const string ValoreB = "Beta";
    private const string TestA = "T-CS1";
    private const string TestB = "T-CS2";
    private readonly EventWaitHandle _appReadyLock;
    private readonly EventWaitHandle _startLockAttempt;
    private readonly EventWaitHandle _enteredLockContention;
    private readonly EventWaitHandle _releaseLockContention;
    private readonly string _lockOwner;

    private sealed record SetupAvvio(
        TestWorkspace Workspace,
        string LockOwner,
        EventWaitHandle AppReadyLock,
        EventWaitHandle StartLockAttempt,
        EventWaitHandle EnteredLockContention,
        EventWaitHandle ReleaseLockContention);

    public CampiSistemaFiltro670UiTests() : this(CreaWorkspaceConBarriera()) { }

    private CampiSistemaFiltro670UiTests(SetupAvvio setup) : base(setup.Workspace)
    {
        _lockOwner = setup.LockOwner;
        _appReadyLock = setup.AppReadyLock;
        _startLockAttempt = setup.StartLockAttempt;
        _enteredLockContention = setup.EnteredLockContention;
        _releaseLockContention = setup.ReleaseLockContention;
    }

    private static SetupAvvio CreaWorkspaceConBarriera()
    {
        var workspace = CreaWorkspace();
        var nomeEvento = $"Local\\TestLoom.App.UITests.CreaRapida.{Guid.NewGuid():N}";
        var proprietarioLock = $"UITest-CreaRapida-{Guid.NewGuid():N}";
        EventWaitHandle? appReady = null;
        EventWaitHandle? start = null;
        EventWaitHandle? contesa = null;
        EventWaitHandle? rilascio = null;
        try
        {
            appReady = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, nomeEvento + ".ready");
            start = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, nomeEvento + ".start");
            contesa = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, nomeEvento + ".entered");
            rilascio = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, nomeEvento + ".release");
            TestLoomApplication.SetTestStartupEnvironmentVariable(
                workspace,
                TestLoomApplication.LockContesaBarrierEnvironmentVariable,
                $"{nomeEvento}|{proprietarioLock}");
            return new SetupAvvio(workspace, proprietarioLock, appReady, start, contesa, rilascio);
        }
        catch
        {
            rilascio?.Dispose();
            contesa?.Dispose();
            start?.Dispose();
            appReady?.Dispose();
            workspace.Dispose();
            throw;
        }
    }

    public override void Dispose()
    {
        _startLockAttempt.Set();
        _releaseLockContention.Set();
        try { base.Dispose(); }
        finally
        {
            _releaseLockContention.Dispose();
            _enteredLockContention.Dispose();
            _startLockAttempt.Dispose();
            _appReadyLock.Dispose();
        }
    }

    internal static TestWorkspace CreaWorkspace() => SeminaWorkspace(nameof(CampiSistemaFiltro670UiTests));

    /// <summary>Workspace con template di base (QA Smoke Template) e due Test senza il campo: il campo lo crea la UI.</summary>
    internal static TestWorkspace SeminaWorkspace(string prefisso)
    {
        var ws = TestWorkspace.CreateLoggedIn(prefisso, seedTestFixtures: true);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var template = state.Master.TestTemplate.Load().First(t => !t.Archiviata);
        foreach (var id in new[] { TestA, TestB })
        {
            state.TestCases.Save(new TestCase
            {
                Id = id,
                DescrizioneBreve = $"Test {id}",
                Componente = new EcuSelection([TestWorkspace.DefaultEcuName]),
                TestTemplateUid = template.Uid,
                ModificatoDa = "UITests",
            }, isNew: true);
        }
        return ws;
    }

    // ---------------------------------------------------------------- flusso UI condiviso

    /// <summary>Impostazioni → Tipi di test → template → Gestisci campi (Dettagli) → catalogo campi di sistema.</summary>
    private static (Window tpl, Window campi, Window catalogo) ApriCatalogo(Window window, TestLoomApplication app)
    {
        new MainShellPage(window).GoToImpostazioni().VaiA("tipiditest");
        Wait.ForCondition(() => ElementFinders.ExistsById(window, "GridPrincipale"), "griglia Tipi di test caricata");
        ElementFinders.WaitButtonById(window, "Tpl_Modifica_Button").SafeInvoke();
        ElementFinders.WaitButtonById(window, "TplGestisci_Button").SafeInvoke();
        var tpl = app.WaitForWindowByContent("TplEditor_Chiudi_Button");
        ElementFinders.WaitButtonById(tpl, "TplGestisciCampi_Dettagli").SafeInvoke();
        var campi = FinestraPiuInterna(window, "CampiEditor_Chiudi_Button");
        ElementFinders.WaitButtonById(campi, "TplGestisciCatalogo_Button").SafeInvoke();
        var catalogo = TrovaCatalogo(campi);
        return (tpl, campi, catalogo);
    }

    /// <summary>Il catalogo è una finestra posseduta da 'Gestisci campi': Avalonia la espone come figlia UIA della finestra
    /// proprietaria, non come finestra di primo livello. WaitForWindowByContent restituirebbe quindi la finestra principale
    /// e Close() chiuderebbe il bersaglio sbagliato. Si cerca la finestra discendente di 'Gestisci campi' che contiene il
    /// catalogo.</summary>
    private static Window TrovaCatalogo(Window radice) => FinestraPiuInterna(radice, "CampoSist_Modifica_Button");

    /// <remarks>Le finestre annidate (template → 'Gestisci campi' → catalogo) contengono tutte il pulsante nel loro
    /// sottoalbero: si sceglie la più interna, cioè quella senza un'altra finestra discendente che lo contenga.</remarks>
    private static Window FinestraPiuInterna(Window radice, string automationId) =>
        Wait.ForElement(
            () => radice.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
                .Where(w => ElementFinders.ExistsById(w, automationId))
                .FirstOrDefault(w => !w.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
                    .Any(d => ElementFinders.ExistsById(d, automationId))),
            $"finestra più interna con AutomationId '{automationId}'").AsWindow();

    private static void ChiudiEditorTemplate(Window tpl, Window campi, TestLoomApplication app)
    {
        ElementFinders.WaitButtonById(campi, "CampiEditor_Chiudi_Button").SafeInvoke();
        Wait.ForCondition(() => app.IsWindowClosedByContent("CampiEditor_Chiudi_Button"), "chiusura di 'Gestisci campi'");
        ElementFinders.WaitButtonById(tpl, "TplEditor_Chiudi_Button").SafeInvoke();
        Wait.ForCondition(() => app.IsWindowClosedByContent("TplEditor_Chiudi_Button"), "chiusura dell'editor del template");
    }

    private static AutomationElement Griglia(Window catalogo) =>
        ElementFinders.WaitById(catalogo, "GridPrincipale");

    /// <summary>La colonna Etichetta del catalogo non ha AutomationId sulle celle: la riga si riconosce dal testo di una
    /// cella (TextBlock) o, in modifica, dal valore dell'editor (TextBox) di una cella.</summary>
    private static bool RigaHaTesto(AutomationElement riga, string testo) =>
        riga.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Any(t => t.Name == testo)
        || riga.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Any(e => e.AsTextBox().Text == testo);

    /// <summary>La riga nuova è l'ultima della griglia: "+ Nuovo" la aggiunge in fondo e la View la porta in vista (ScrollIntoView)
    /// e la mette in modifica (BeginEdit). Lo stato di selezione UIA non è esposto dalle righe DataGrid di Avalonia, quindi non si usa.</summary>
    private static AutomationElement RigaSelezionata(Window catalogo) =>
        Wait.ForElement(
            () => Griglia(catalogo).FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).LastOrDefault(),
            "riga nuova (ultima) nel catalogo");

    /// <summary>Riga per id stabile CampoSist_Riga_{Chiave} (impostato dalla View al caricamento della riga).</summary>
    private static AutomationElement RigaPerChiave(Window catalogo, string chiave) =>
        Wait.ForElement(() => ElementFinders.TryFindById(catalogo, $"CampoSist_Riga_{chiave}"),
            $"riga del catalogo con id CampoSist_Riga_{chiave}");

    /// <summary>Scrive l'Etichetta della riga selezionata: la View mette la cella in modifica dopo "+ Nuovo" (BeginEdit), quindi
    /// l'editor è già aperto; se non lo è, F2 lo apre. Invio conferma.</summary>
    private static void RinominaRigaSelezionata(Window catalogo, string nuova)
    {
        var riga = RigaSelezionata(catalogo);
        var editor = Wait.ForElement(
            () => riga.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)),
            "editor della cella Etichetta della riga selezionata (BeginEdit dalla View)");
        editor.AsTextBox().SetText(nuova);
        // Il DataGrid di Avalonia (BeginEdit) non chiude l'editor con Invio: spostare il fuoco sulla spunta della stessa riga
        // conferma la cella senza tastiera (un Tab uscirebbe dalla finestra). La verifica finale è sulla chiave/id dopo il salvataggio.
        riga.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox))?.Focus();
    }

    private static void ImpostaFiltro(AutomationElement riga, string etichetta, bool accesa)
    {
        var target = accesa ? ToggleState.On : ToggleState.Off;
        var checkbox = riga.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox))
            ?? throw new InvalidOperationException($"Nessuna spunta 'Filtro' nella riga '{etichetta}'.");
        var stato = () => checkbox.Patterns.Toggle.Pattern.ToggleState.Value;
        // La riga può essere appena fuori vista: il fuoco sulla spunta la porta in vista prima di cambiarne lo stato.
        checkbox.Focus();
        var abilitata = checkbox.IsEnabled;
        for (var i = 0; i < 4 && stato() != target; i++)
            checkbox.Patterns.Toggle.Pattern.Toggle();
        Assert.True(stato() == target,
            $"Spunta 'Filtro' di '{etichetta}': stato {stato()}, atteso {target} (abilitata prima del cambio={abilitata}).");
    }

    /// <summary>Crea il campo nel catalogo (Modifica → Nuovo → etichetta → Filtro → Salva), poi lo aggiunge al template.</summary>
    private static string CreaCampoETrovaChiave(TestLoomApplication app, Window window)
    {
        var (tpl, campi, catalogo) = ApriCatalogo(window, app);
        ElementFinders.WaitButtonById(catalogo, "CampoSist_Modifica_Button").SafeInvoke();
        ElementFinders.WaitButtonById(catalogo, "CampoSist_Nuovo_Button").SafeInvoke();
        RinominaRigaSelezionata(catalogo, Etichetta);
        ImpostaFiltro(RigaSelezionata(catalogo), Etichetta, accesa: true);
        ElementFinders.WaitButtonById(catalogo, "CampoSist_Salva_Button").SafeInvoke();
        Wait.ForCondition(() => ElementFinders.ExistsById(catalogo, "CampoSist_Modifica_Button"), "catalogo salvato (fuori dalla modifica)");
        var chiaveSalvata = CampoSistemaCatalogoService.GeneraChiaveDaEtichetta(Etichetta, Enumerable.Empty<string>());
        RigaPerChiave(catalogo, chiaveSalvata); // verifica dell'id stabile dopo il salvataggio
        ChiudiFinestra(app, catalogo, "CampoSist_Modifica_Button");

        // Il catalogo viene riletto dall'editor del template solo dopo la chiusura, quindi "Aggiungi campo" propone il nuovo campo.
        // Il pulsante sta in fondo alla finestra: si scorre tutta la finestra prima di invocarlo (un Invoke fuori vista è rifiutato).
        ScorriFinestraAFondo(campi);
        var aggiungi = ElementFinders.WaitButtonById(campi, "TplCampo_Aggiungi_Button");
        try { aggiungi.SafeInvoke(); }
        catch (Exception ex) { throw new InvalidOperationException($"{ex.Message}\n{DumpStatoUia(app, campi, aggiungi)}", ex); }
        var combo = Wait.ForElement(() => campi.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
            .Where(c => IdSicuro(c).StartsWith("TplCampo_Riga_", StringComparison.Ordinal)
                && IdSicuro(c).EndsWith("_Sistema_ComboBox", StringComparison.Ordinal))
            .LastOrDefault(), "combo 'Campo di sistema' della nuova riga");
        combo.AsComboBox().SelectByText(Etichetta);

        var chiave = CampoSistemaCatalogoService.GeneraChiaveDaEtichetta(Etichetta, Enumerable.Empty<string>());
        ChiudiEditorTemplate(tpl, campi, app);
        // Il template è in modifica nella pagina Tipi di test: senza salvataggio la navigazione chiede conferma
        // ("Modifiche non salvate") e blocca i passi successivi. Stesso percorso di CardClassicheOrdineUiTests.
        ElementFinders.WaitButtonById(window, "Tpl_Salva_Button").SafeInvoke();
        return chiave;
    }

    /// <summary>Assegna i due valori a due Test dal loro editor (Modifica → campo → Salva).</summary>
    private static void AssegnaValori(Window window, TestLoomApplication app)
    {
        var lista = new MainShellPage(window).GoToTestList();
        var editor = lista.OpenRowById(TestA);
        editor.EnterEditMode();
        editor.SetCampoTesto(Etichetta, ValoreA);
        editor.Save();
        Wait.ForCondition(() => !ElementFinders.ExistsById(window, "Editor_Save_Button"), "salvataggio Test A");

        lista = new MainShellPage(window).GoToTestList();
        editor = lista.OpenRowById(TestB);
        editor.EnterEditMode();
        editor.SetCampoTesto(Etichetta, ValoreB);
        editor.Save();
        Wait.ForCondition(() => !ElementFinders.ExistsById(window, "Editor_Save_Button"), "salvataggio Test B");
    }

    /// <summary>Porta in fondo tutti i contenitori scorrevoli della finestra (come ReportPage.Scorri): i pulsanti in fondo
    /// (es. "+ Aggiungi campo") non sono invocabili finché non sono in vista.</summary>
    private static void ScorriFinestraAFondo(Window finestra)
    {
        foreach (var contenitore in finestra.FindAllDescendants())
        {
            // Un elemento può esporre ScrollPattern senza accettare SetScrollPercent (es. già in fondo o non scorrevole): si salta.
            try
            {
                var scroll = contenitore.Patterns.Scroll.PatternOrDefault;
                if (scroll != null && scroll.VerticallyScrollable.ValueOrDefault)
                    scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 100);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Diagnostica UIA di un pulsante non invocabile: stato Enabled/Offscreen delle finestre top-level e della
    /// catena di antenati del pulsante (fino alla finestra), più i figli diretti della finestra con il loro stato.</summary>
    private static string DumpStatoUia(TestLoomApplication app, Window finestra, AutomationElement pulsante)
    {
        var sb = new System.Text.StringBuilder();
        static string Riga(AutomationElement e)
        {
            try
            {
                return $"[{e.ControlType}] Id='{e.AutomationId}' Name='{e.Name}' Enabled={e.IsEnabled} Offscreen={e.IsOffscreen} Rect={e.BoundingRectangle}";
            }
            catch (Exception ex) { return $"(lettura fallita: {ex.Message})"; }
        }
        sb.AppendLine("Finestre top-level:");
        try
        {
            foreach (var w in app.TopLevelWindows())
                sb.AppendLine($"  '{w.Title}' Enabled={w.IsEnabled} Offscreen={w.IsOffscreen}");
        }
        catch (Exception ex) { sb.AppendLine($"  (non enumerabili: {ex.Message})"); }
        sb.AppendLine("Finestra campi: " + Riga(finestra));
        sb.AppendLine("Antenati del pulsante TplCampo_Aggiungi_Button:");
        try
        {
            var e = pulsante;
            for (var profondita = 0; e != null && profondita < 25; profondita++)
            {
                sb.AppendLine("  " + Riga(e));
                e = e.Parent;
            }
        }
        catch (Exception ex) { sb.AppendLine($"  (risalita fallita: {ex.Message})"); }
        sb.AppendLine("Figli diretti della finestra campi:");
        try
        {
            foreach (var figlio in finestra.FindAllChildren())
                sb.AppendLine("  " + Riga(figlio));
        }
        catch (Exception ex) { sb.AppendLine($"  (figli non leggibili: {ex.Message})"); }
        var testo = sb.ToString();
        Console.WriteLine(testo);
        return testo;
    }

    /// <summary>Chiude una finestra di dialogo del catalogo. Window.Close() invoca il pulsante di chiusura della finestra e
    /// nell'esecuzione del 2026-10-13 risultava non invocabile (ElementNotEnabled): si prova il WindowPattern, poi Alt+F4 sulla
    /// finestra a fuoco. Se la finestra non si chiude, l'errore porta con sé la diagnostica UIA della finestra.</summary>
    private static void ChiudiFinestra(TestLoomApplication app, Window finestra, string marcatoreContenuto)
    {
        var prima = DumpStatoUia(app, finestra, finestra);
        finestra.Patterns.Window.Pattern.Close();
        try
        {
            Wait.ForCondition(() => app.IsWindowClosedByContent(marcatoreContenuto), "chiusura della finestra del catalogo", TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{ex.Message}\nPRIMA:\n{prima}\nDOPO:\n{DumpStatoUia(app, finestra, finestra)}", ex);
        }
    }

    /// <summary>AutomationId letto in modo sicuro: alcuni elementi UIA non espongono la proprietà e la lettura lancia.</summary>
    private static string IdSicuro(AutomationElement e)
    {
        try { return e.AutomationId ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private static string IdFiltroHome(string chiave) => $"FiltroCampoSistema_{chiave}_Button";

    /// <summary>Apre il flyout del filtro, seleziona il valore e lo richiude con Esc.</summary>
    private static void SelezionaValoreFiltro(Window window, TestLoomApplication app, string chiave, string valore)
    {
        ElementFinders.WaitButtonById(window, IdFiltroHome(chiave)).SafeInvoke();
        var flyout = new FiltroMultiSelectPage(app);
        flyout.SelezionaVoce(valore);
        ElementActions.PressKey(window, VirtualKeyShort.ESCAPE);
    }

    // ---------------------------------------------------------------- fatti

    [Fact]
    public void CampoSistema_CreatoInImpostazioni_AssegnatoAiTest_FiltraHomeEReport()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var chiave = CreaCampoETrovaChiave(App, window);
            AssegnaValori(window, App);

            // Home: il filtro compare e, selezionando un valore, il suo riepilogo lo mostra.
            var home = new MainShellPage(window).GoToHome();
            home.ApriFiltriAvanzati();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, IdFiltroHome(chiave)), "filtro del campo visibile in Home");
            SelezionaValoreFiltro(window, App, chiave, ValoreA);
            Wait.ForCondition(() => ElementFinders.WaitButtonById(window, IdFiltroHome(chiave)).Name.Contains(ValoreA),
                "filtro Home riepiloga il valore selezionato");

            // Il valore selezionato resta dopo aver lasciato Home e tornando.
            new MainShellPage(window).GoToTestList();
            home = new MainShellPage(window).GoToHome();
            home.ApriFiltriAvanzati();
            Wait.ForCondition(() => ElementFinders.WaitButtonById(window, IdFiltroHome(chiave)).Name.Contains(ValoreA),
                "selezione Home persistita dopo aver cambiato pagina");

            // Report: il filtro del campo compare nello stesso modo.
            var report = new MainShellPage(window, App).GoToReport();
            report.ApriFiltriAvanzati();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, IdFiltroHome(chiave)), "filtro del campo visibile nel Report");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CampoSistema_CreatoInImpostazioni_AssegnatoAiTest_FiltraHomeEReport), ex); }
    }

    [Fact]
    public void CampoSistema_FlagDisattivato_RimuoveIlFiltroDaHomeEReport()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var chiave = CreaCampoETrovaChiave(App, window);
            var home = new MainShellPage(window).GoToHome();
            home.ApriFiltriAvanzati();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, IdFiltroHome(chiave)),
                "filtro presente prima della disattivazione");

            // Disattivazione: stesso catalogo, spunta Filtro tolta e salvataggio.
            var (tpl, campi, catalogo) = ApriCatalogo(window, App);
            ElementFinders.WaitButtonById(catalogo, "CampoSist_Modifica_Button").SafeInvoke();
            ImpostaFiltro(RigaPerChiave(catalogo, chiave), Etichetta, accesa: false);
            ElementFinders.WaitButtonById(catalogo, "CampoSist_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(catalogo, "CampoSist_Modifica_Button"), "catalogo salvato");
            ChiudiFinestra(App, catalogo, "CampoSist_Modifica_Button");
            ChiudiEditorTemplate(tpl, campi, App);
            ElementFinders.WaitButtonById(window, "Tpl_Salva_Button").SafeInvoke();

            home = new MainShellPage(window).GoToHome();
            home.ApriFiltriAvanzati();
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, IdFiltroHome(chiave)), "filtro rimosso da Home");
            var report = new MainShellPage(window, App).GoToReport();
            report.ApriFiltriAvanzati();
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, IdFiltroHome(chiave)), "filtro rimosso dal Report");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CampoSistema_FlagDisattivato_RimuoveIlFiltroDaHomeEReport), ex); }
    }

    [Fact]
    public void CampoSistema_CreazioneRapidaLockConteso_DisabilitaAnnullaERestaAperta_SenzaVoceScollegata()
    {
        const string etichetta = "Campo rapido UI 6.7.5";
        var chiave = CampoSistemaCatalogoService.GeneraChiaveDaEtichetta(etichetta, Enumerable.Empty<string>());
        AppState? statoProprietarioLock = null;
        string? percorsoLockConteso = null;
        string? sessioneLockContesa = null;
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiA("tipiditest");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "GridPrincipale"), "griglia Tipi di test caricata");
            ElementFinders.WaitButtonById(window, "Tpl_Modifica_Button").SafeInvoke();
            ElementFinders.WaitButtonById(window, "TplGestisci_Button").SafeInvoke();

            var tpl = App.WaitForWindowByContent("TplEditor_Chiudi_Button");
            ElementFinders.WaitButtonById(tpl, "TplGestisciCampi_Dettagli").SafeInvoke();
            var campi = FinestraPiuInterna(window, "CampiEditor_Chiudi_Button");

            ScorriFinestraAFondo(campi);
            ElementFinders.WaitButtonById(campi, "TplCampo_Aggiungi_Button").SafeInvoke();

            AutomationElement? ComboCampoSistemaAggiunto() =>
                campi.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
                    .Where(c => IdSicuro(c).StartsWith("TplCampo_Riga_", StringComparison.Ordinal)
                        && IdSicuro(c).EndsWith("_Sistema_ComboBox", StringComparison.Ordinal))
                    .LastOrDefault();

            var combo = Wait.ForElement(() => ComboCampoSistemaAggiunto(),
                "combo 'Campo di sistema' della nuova riga");
            combo.AsComboBox().SelectByText("+ Crea nuovo campo...");

            AutomationElement? ContenitoreFlyoutCreaCampo()
            {
                foreach (var finestra in App.TopLevelWindows())
                {
                    var titolo = finestra.FindFirstDescendant(cf =>
                        cf.ByControlType(ControlType.Text).And(cf.ByName("Nuovo campo del catalogo")));
                    if (titolo == null) continue;

                    var antenato = titolo.Parent;
                    while (antenato != null)
                    {
                        var casellaNome = antenato.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
                        var pulsanteCrea = antenato.FindFirstDescendant(cf =>
                            cf.ByControlType(ControlType.Button).And(cf.ByName("Crea")));
                        if (casellaNome != null && pulsanteCrea != null) return antenato;
                        antenato = antenato.Parent;
                    }
                }
                return null;
            }

            var flyout = Wait.ForElement(() => ContenitoreFlyoutCreaCampo(),
                "flyout di creazione rapida del campo di sistema");
            Wait.ForElement(() => flyout.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)),
                "casella nome del campo nel flyout").AsTextBox().SetText(etichetta);
            var pulsanteCreaRapido = Wait.ForElement(() => flyout.FindFirstDescendant(cf =>
                    cf.ByControlType(ControlType.Button).And(cf.ByName("Crea"))),
                "pulsante Crea nel flyout").AsButton();
            _appReadyLock.Reset();
            _startLockAttempt.Reset();
            _enteredLockContention.Reset();
            _releaseLockContention.Reset();
            pulsanteCreaRapido.SafeInvoke();
            Assert.True(_appReadyLock.WaitOne(TimeSpan.FromSeconds(10)),
                "TestLoom deve superare il preflight VM ed entrare nel seam reale di acquisizione del lock");

            statoProprietarioLock = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
            percorsoLockConteso = statoProprietarioLock.Paths.CampiSistemaDettagliFile;
            var lockTest = statoProprietarioLock.Locks.TryAcquire(percorsoLockConteso, _lockOwner);
            sessioneLockContesa = lockTest.Acquisito ? lockTest.DetenutoDa?.SessionId : null;
            Assert.True(lockTest.Acquisito && !string.IsNullOrWhiteSpace(sessioneLockContesa),
                "il test deve possedere il sidecar .lock del catalogo prima del tentativo TestLoom");
            _startLockAttempt.Set();
            Assert.True(_enteredLockContention.WaitOne(TimeSpan.FromSeconds(10)),
                "TestLoom deve ricevere il conflitto reale con il lock holder del test prima di restare in attesa");
            var holderAtteso = statoProprietarioLock.Locks.GetActiveLock(percorsoLockConteso);
            Assert.Equal(_lockOwner, holderAtteso?.Operatore);
            Assert.Equal(sessioneLockContesa, holderAtteso?.SessionId);

            var annullaDuranteCreazione = Wait.ForElement(() => flyout.FindFirstDescendant(cf =>
                    cf.ByControlType(ControlType.Button).And(cf.ByAutomationId("TplCreaNuovoCampo_Annulla_Button"))),
                "pulsante Annulla del flyout di creazione rapida");
            try
            {
                Wait.ForCondition(() => ContenitoreFlyoutCreaCampo() != null
                    && !pulsanteCreaRapido.IsEnabled
                    && !annullaDuranteCreazione.IsEnabled
                    && ComboCampoSistemaAggiunto()?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text))?.Name != etichetta,
                    "creazione rapida ancora pendente dopo il conflitto reale sul sidecar cross-process", TimeSpan.FromSeconds(5));
                Assert.False(pulsanteCreaRapido.IsEnabled,
                    "Crea deve restare disabilitato finché la creazione rapida è in attesa.");
                Assert.False(annullaDuranteCreazione.IsEnabled,
                    "Annulla deve restare disabilitato finché la creazione rapida è in corso.");

                using (var statoDuranteAttesa = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false))
                {
                    Assert.DoesNotContain(statoDuranteAttesa.Master.CampiSistemaDettagli.Load(), c => c.Chiave == chiave);
                }

                ElementActions.PressKey(campi, VirtualKeyShort.ESCAPE);
                Wait.ForCondition(() => ContenitoreFlyoutCreaCampo() != null
                    && !pulsanteCreaRapido.IsEnabled
                    && !annullaDuranteCreazione.IsEnabled,
                    "il flyout resta aperto dopo Escape mentre la creazione è pendente", TimeSpan.FromSeconds(3));

                // Click reale sul titolo UIA fuori dal flyout: deve essere annullato come light-dismiss.
                ElementFinders.WaitById(campi, "CampiTemplate_Titolo_Text").Click();
                Wait.ForCondition(() => ContenitoreFlyoutCreaCampo() != null
                    && !pulsanteCreaRapido.IsEnabled
                    && !annullaDuranteCreazione.IsEnabled,
                    "il flyout resta aperto dopo il light-dismiss mentre la creazione è pendente", TimeSpan.FromSeconds(3));
            }
            finally
            {
                _releaseLockContention.Set();
            }

            Wait.ForCondition(() => ContenitoreFlyoutCreaCampo() != null
                && pulsanteCreaRapido.IsEnabled
                && annullaDuranteCreazione.IsEnabled
                && ComboCampoSistemaAggiunto()?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text))?.Name != etichetta,
                "esito del conflitto mostrato senza creare o selezionare la voce",
                TimeSpan.FromSeconds(15));
            annullaDuranteCreazione.SafeInvoke();
            Wait.ForCondition(() => ContenitoreFlyoutCreaCampo() == null, "chiusura volontaria del flyout dopo il conflitto");

            ChiudiEditorTemplate(tpl, campi, App);

            using var statoPersistito = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
            var templateSalvato = statoPersistito.Master.TestTemplate.Load().First(t => !t.Archiviata);
            Assert.DoesNotContain(statoPersistito.Master.CampiSistemaDettagli.Load(), c => c.Chiave == chiave);
            Assert.DoesNotContain(templateSalvato.Sezioni.SelectMany(s => s.Campi),
                c => c.Tipo == TipoCampoTemplate.CampoSistema && c.CampoSistemaChiave == chiave);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CampoSistema_CreazioneRapidaLockConteso_DisabilitaAnnullaERestaAperta_SenzaVoceScollegata), ex);
        }
        finally
        {
            _startLockAttempt.Set();
            _releaseLockContention.Set();
            if (statoProprietarioLock != null)
            {
                if (!string.IsNullOrWhiteSpace(percorsoLockConteso) && !string.IsNullOrWhiteSpace(sessioneLockContesa))
                    statoProprietarioLock.Locks.ReleaseIfSession(percorsoLockConteso, sessioneLockContesa);
                statoProprietarioLock.Dispose();
            }
        }
    }

}
