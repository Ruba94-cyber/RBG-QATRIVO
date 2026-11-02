using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura della lingua francese (6.7.0): selezione da Impostazioni, verifica di etichette rappresentative
/// su ogni pagina principale e "detector" di testi residui in italiano. Il detector scorre l'albero UIA della
/// pagina raccogliendo Name di TextBlock/Button/TabItem/Header/MenuItem visibili, escludendo il contenuto di
/// DataGrid/tabelle/liste dati (le liste di navigazione della sidebar restano incluse), e segnala le parole
/// italiane tipiche (Salva, Annulla, Elimina, Impostazioni, Filtri, Nessun, Aggiungi, Chiudi, della/degli/delle).
/// I residui provenienti da ViewModel/Services C# sono marcati con Trait Category=BugAperto: non sono
/// correggibili dai file XAML della lingua e vanno sistemati da chi traduce i messaggi C#.
/// Ogni test lavora su un workspace temporaneo isolato (TestWorkspace.CreateLoggedIn), mai sui dati reali.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Francese")]
[Trait("Area", "Localizzazione")]
public sealed class Francese670UiTests : UiTestBase
{
    private static readonly string[] MarcatoriItaliano =
    {
        "Salva", "Annulla", "Elimina", "Impostazioni", "Filtri", "Nessun", "Aggiungi", "Chiudi",
        "Indietro", "Avanti", "Riprova", "Modifica", "Nuovo ", "Nuova ",
        " della ", " degli ", " delle ",
    };

    private static readonly ControlType[] TipiControllati =
    {
        ControlType.Text, ControlType.Button, ControlType.TabItem, ControlType.MenuItem, ControlType.Header,
    };

    private static readonly ControlType[] TipiDatiEsclusi =
    {
        ControlType.DataGrid, ControlType.Table, ControlType.DataItem, ControlType.Tree, ControlType.TreeItem,
    };

    private static readonly string[] SottosezioniImpostazioni =
    {
        "generale", "archivio", "operatori", "requisiti", "requisitid4d", "funzionalita", "componente", "veicoli",
        "strumenti", "tipiditest", "recovery",
    };

    public Francese670UiTests() : base(TestWorkspace.CreateLoggedIn(nameof(Francese670UiTests))) { }

    private string? Lingua()
    {
        using var doc = LeggiJson(Workspace.ConfigPath);
        return Proprieta(doc.RootElement, "Lingua")?.GetString();
    }

    /// <summary>Passa a Français da Impostazioni &gt; Generale e attende che config e titolo siano aggiornati.</summary>
    private Window PassaAlFrancese()
    {
        // Primo avvio di una cartella nuova: l'indice locale puo' richiedere qualche decina di secondi (vedi
        // AvvioWindow "Apertura di TestLoom..."), quindi il timeout e' quello dei test di avvio della suite tour.
        var window = App.WaitForMainWindow(TimeSpan.FromSeconds(120));
        var shell = new MainShellPage(window, App);
        shell.DismissNotificationsIfShown();
        shell.GoToImpostazioni().VaiA("generale");
        ElementFinders.WaitById(window, "Impostazioni_Lingua_Francais_RadioButton").SafeInvoke();
        Wait.ForCondition(() => Lingua() == "fr", "lingua 'fr' salvata nel config locale", TimeSpan.FromSeconds(15));
        Wait.ForCondition(() => ElementFinders.TryFindById(window, "Impostazioni_Title_Text")?.Name == "Général",
            "titolo impostazioni in francese", TimeSpan.FromSeconds(15));
        return window;
    }

    private static MainShellPage Shell(Window window, TestLoomApplication app)
    {
        var shell = new MainShellPage(window, app);
        shell.DismissNotificationsIfShown();
        return shell;
    }

    private static bool SottoAlberoNavigazione(ControlType tipo, string id) =>
        tipo == ControlType.List
            ? id is "Main_Navigation_List" or "Impostazioni_Sidebar_List"
            : id.StartsWith("Main_Nav_", StringComparison.Ordinal) || id.StartsWith("Impostazioni_Sub_", StringComparison.Ordinal);

    /// <summary>Raccoglie i testi italiani residui sotto <paramref name="nodo"/> (ricorsione limitata).</summary>
    private static void RaccogliResidui(AutomationElement nodo, string pagina, List<string> trovati, int livello = 0)
    {
        if (livello > 40) return;
        AutomationElement[] figli;
        try { figli = nodo.FindAllChildren(); }
        catch { return; }

        foreach (var figlio in figli)
        {
            ControlType tipo;
            string id, nome;
            try
            {
                if (figlio.Properties.IsOffscreen.ValueOrDefault) continue;
                tipo = figlio.ControlType;
                id = figlio.AutomationId ?? string.Empty;
                nome = figlio.Name ?? string.Empty;
            }
            catch { continue; }

            // La barra del titolo (Chiudi/Riduci a icona/Ripristino/Sistema) e' il chrome del sistema operativo,
            // localizzato dalla lingua di Windows e non dal dizionario TestLoom: non e' un testo dell'applicazione.
            if (tipo == ControlType.TitleBar) continue;
            if (TipiDatiEsclusi.Contains(tipo)) continue;
            if ((tipo is ControlType.List or ControlType.ListItem) && !SottoAlberoNavigazione(tipo, id)) continue;

            if (TipiControllati.Contains(tipo) && !string.IsNullOrWhiteSpace(nome))
            {
                var marcatore = MarcatoriItaliano.FirstOrDefault(m => nome.Contains(m, StringComparison.Ordinal));
                if (marcatore != null)
                    trovati.Add($"[{pagina}] \"{nome.Trim()}\" (contiene \"{marcatore.Trim()}\")");
            }
            RaccogliResidui(figlio, pagina, trovati, livello + 1);
        }
    }

    private static void AsseriscoNessunResiduo(List<string> residui)
    {
        Assert.True(residui.Count == 0,
            $"Testi italiani residui nell'interfaccia francese ({residui.Count}):\n  " + string.Join("\n  ", residui));
    }

    [Fact]
    public void Francese_SelezioneDaImpostazioni_AggiornaSubitoELaConfigLocale()
    {
        try
        {
            var window = PassaAlFrancese();
            Assert.Equal("Général", ElementFinders.WaitById(window, "Impostazioni_Title_Text").Name);
            Assert.Equal("fr", Lingua());
            Assert.True(ElementFinders.WaitById(window, "Impostazioni_Lingua_Francais_RadioButton").AsRadioButton().IsChecked == true,
                "il radio 'Français' e' selezionato");

            var residui = new List<string>();
            RaccogliResidui(window, "Impostazioni › Generale", residui);
            AsseriscoNessunResiduo(residui);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Francese_SelezioneDaImpostazioni_AggiornaSubitoELaConfigLocale), ex); }
    }

    [Fact]
    public void Francese_Home_EtichetteEResiduiItaliani()
    {
        try
        {
            var window = PassaAlFrancese();
            Shell(window, App).GoToHome();
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "Home_Title_Text")?.Name == "Accueil",
                "titolo Home in francese", TimeSpan.FromSeconds(15));
            Assert.True(TestoContieneVisibile(window, "Paramètres"), "voce di sidebar Paramètres visibile");

            var residui = new List<string>();
            RaccogliResidui(window, "Home", residui);
            AsseriscoNessunResiduo(residui);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Francese_Home_EtichetteEResiduiItaliani), ex); }
    }

    [Fact]
    public void Francese_ListaTest_EtichetteEResiduiItaliani()
    {
        try
        {
            var window = PassaAlFrancese();
            var lista = Shell(window, App).GoToTestList();
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "TestList_Title_Text")?.Name == "Tests",
                "titolo Lista Test in francese", TimeSpan.FromSeconds(15));
            Assert.Contains("Nouveau test", ElementFinders.WaitById(window, "TestList_New_Button").Name ?? string.Empty);

            var residui = new List<string>();
            RaccogliResidui(window, "Lista Test", residui);
            AsseriscoNessunResiduo(residui);
            _ = lista;
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Francese_ListaTest_EtichetteEResiduiItaliani), ex); }
    }

    [Fact]
    public void Francese_EditorTest_EtichetteEResiduiItaliani()
    {
        try
        {
            var window = PassaAlFrancese();
            var editor = Shell(window, App).GoToTestList().ClickNew();
            editor.ChooseTemplate();
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "Editor_Title_Text")?.Name == "Nouveau test",
                "editor del nuovo Test aperto in francese", TimeSpan.FromSeconds(20));
            Assert.Equal("← Retour à la liste", ElementFinders.WaitById(window, "Editor_Cancel_Button").Name);

            var residui = new List<string>();
            RaccogliResidui(window, "Editor Test", residui);
            AsseriscoNessunResiduo(residui);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Francese_EditorTest_EtichetteEResiduiItaliani), ex); }
    }

    [Fact]
    public void Francese_Report_EtichetteEResiduiItaliani()
    {
        try
        {
            var window = PassaAlFrancese();
            Shell(window, App).GoToReport();
            Wait.ForCondition(() => TestoVisibile(window, "Rapports") != null,
                "titolo Rapports visibile", TimeSpan.FromSeconds(20));

            var residui = new List<string>();
            RaccogliResidui(window, "Report", residui);
            AsseriscoNessunResiduo(residui);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Francese_Report_EtichetteEResiduiItaliani), ex); }
    }

    [Fact]
    public void Francese_Anomalie_EtichetteEResiduiItaliani()
    {
        try
        {
            var window = PassaAlFrancese();
            Shell(window, App).GoToAnomalie();
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "Anomalie_Title_Text")?.Name == "Anomalies et Ticketing",
                "titolo Anomalie in francese", TimeSpan.FromSeconds(15));
            Assert.Contains("Nouvelle Change request", ElementFinders.WaitById(window, "Anomalie_NuovaChangeRequest_Button").Name ?? string.Empty);

            var residui = new List<string>();
            RaccogliResidui(window, "Anomalie / CR", residui);
            AsseriscoNessunResiduo(residui);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Francese_Anomalie_EtichetteEResiduiItaliani), ex); }
    }

    [Fact]
    public void Francese_ChangeRequest_EditorEResiduiItaliani()
    {
        try
        {
            var window = PassaAlFrancese();
            var anomalie = Shell(window, App).GoToAnomalie();
            var cr = anomalie.NuovaChangeRequest();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Cr_Stato_Text"),
                "editor Change Request aperto", TimeSpan.FromSeconds(20));

            var residui = new List<string>();
            RaccogliResidui(window, "Change Request", residui);
            AsseriscoNessunResiduo(residui);
            _ = cr;
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Francese_ChangeRequest_EditorEResiduiItaliani), ex); }
    }

    [Fact]
    public void Francese_Audit_EtichetteEResiduiItaliani()
    {
        try
        {
            var window = PassaAlFrancese();
            Shell(window, App).GoToAudit();
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "Audit_Title_Text")?.Name == "Audit de conformité",
                "titolo Audit in francese", TimeSpan.FromSeconds(15));

            var residui = new List<string>();
            RaccogliResidui(window, "Audit", residui);
            AsseriscoNessunResiduo(residui);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Francese_Audit_EtichetteEResiduiItaliani), ex); }
    }

    /// <summary>Tutte le sottosezioni di Impostazioni (incluse le schede Master data: Operatori, componente, Veicoli,
    /// Requisiti, Funzionalita'), una per una nella stessa sessione.</summary>
    [Fact]
    public void Francese_Impostazioni_TutteLeSottosezioniEResiduiItaliani()
    {
        try
        {
            var window = PassaAlFrancese();
            var impostazioni = Shell(window, App).GoToImpostazioni();
            var residui = new List<string>();
            foreach (var sezione in SottosezioniImpostazioni)
            {
                impostazioni.VaiA(sezione);
                var voce = ElementFinders.WaitById(window, $"Impostazioni_Sub_{sezione}");
                Wait.ForCondition(() => voce.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault == true,
                    $"sottosezione '{sezione}' selezionata", TimeSpan.FromSeconds(10));
                RaccogliResidui(window, $"Impostazioni › {sezione}", residui);
            }
            AsseriscoNessunResiduo(residui);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Francese_Impostazioni_TutteLeSottosezioniEResiduiItaliani), ex); }
    }

    [Fact]
    public void Francese_Diagnostica_CentroIntegritaEResiduiItaliani()
    {
        try
        {
            var window = PassaAlFrancese();
            var shell = Shell(window, App);
            var impostazioni = shell.GoToImpostazioni();
            impostazioni.VaiA("generale");
            var diagnostica = new Post653ImpostazioniPage(window, App).ApriCentroIntegrita();
            _ = diagnostica;

            var finestra = App.WaitForWindowByContent("Integrity_Title_Text");
            Assert.Equal("Centre d'intégrité local — lecture seule",
                ElementFinders.WaitById(finestra, "Integrity_Title_Text").Name);

            var residui = new List<string>();
            RaccogliResidui(finestra, "Diagnostica", residui);
            AsseriscoNessunResiduo(residui);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Francese_Diagnostica_CentroIntegritaEResiduiItaliani), ex); }
    }
}
