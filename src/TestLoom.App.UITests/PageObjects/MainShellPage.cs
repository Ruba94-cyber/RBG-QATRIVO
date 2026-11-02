using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>La shell principale (sidebar di navigazione + host della pagina corrente) mostrata dopo il login.
/// Gli Id di navigazione ("home","test","anomalie","wustatus","report","audit","impostazioni") sono gli stessi
/// NavItem.Id stabili definiti in MainViewModel — mai il testo tradotto del menu.</summary>
public sealed class MainShellPage
{
    private readonly Window _window;
    private readonly Infrastructure.TestLoomApplication? _app;

    public MainShellPage(Window window) => _window = window;
    public MainShellPage(Window window, Infrastructure.TestLoomApplication app) { _window = window; _app = app; }

    public Window Window => _window;

    public static bool IsShown(Window window) => ElementFinders.ExistsById(window, "Main_Navigation_List");

    public void NavigateTo(string navId)
    {
        DismissNotificationsIfShown();
        ElementFinders.WaitById(_window, $"Main_Nav_{navId}").SafeInvoke();
    }

    /// <summary>Il centro notifiche interno si apre da solo se ci sono notifiche non lette al primo giro utile
    /// dopo un login/avvio (MostraNotificheNonLetteAllAvvioSeNecessario) — un operatore appena creato dal seed
    /// del workspace di test può già averne (es. notifiche di sistema legate al bootstrap). È un overlay modale
    /// che intercetta i click sulla sidebar sottostante, quindi va chiuso prima di qualunque navigazione.</summary>
    public void DismissNotificationsIfShown()
    {
        var closeButton = ElementFinders.TryFindById(_window, "Main_NotificationsClose_Button");
        if (closeButton == null) return;
        closeButton.AsButton().SafeInvoke();
        Wait.ForCondition(() => ElementFinders.TryFindById(_window, "Main_NotificationsClose_Button") == null,
            "chiusura centro notifiche");
    }

    /// <summary>Scope per cercare il contenuto della pagina corrente: la finestra stessa, non un contenitore
    /// dedicato — il ContentControl "Main_PageHost_Content" porta un AutomationId ma Avalonia non lo espone
    /// come AutomationElement via UI Automation (verificato con FlaUI: i marcatori delle singole pagine, es.
    /// "Home_Title_Text", restano comunque raggiungibili direttamente dalla finestra).</summary>
    public AutomationElement PageHost => _window;

    public bool HasNavigationError => ElementFinders.ExistsById(_window, "Main_NavigationError_Text");

    public void OpenAccountMenu() =>
        ElementFinders.WaitButtonById(_window, "Main_AccountMenu_Button").SafeInvoke();

    /// <summary>Logout completo: apre il menu account e clicca "Esci" — dopo questa chiamata la stessa
    /// finestra torna a mostrare FirstRunView (vedi LoginPage), coerente con come MainViewModel riusa la
    /// stessa istanza di FirstRunViewModel per login iniziale e login di ritorno.</summary>
    public void Logout()
    {
        OpenAccountMenu();
        ElementFinders.WaitById(_window, "Main_Logout_MenuItem").SafeInvoke();
        Wait.ForCondition(() => LoginPage.IsShown(_window), "schermata di login dopo logout");
    }

    public HomePage GoToHome() { NavigateTo("home"); return new HomePage(PageHost); }
    public TestListPage GoToTestList() { NavigateTo("test"); return new TestListPage(_window, PageHost); }
    public NonConformitaPage GoToAnomalie() { NavigateTo("anomalie"); return new NonConformitaPage(PageHost); }
    public ImpostazioniPage GoToImpostazioni() { NavigateTo("impostazioni"); return new ImpostazioniPage(PageHost); }
    public AuditPage GoToAudit()
    {
        NavigateTo("audit");
        if (_app == null) throw new InvalidOperationException("MainShellPage(window, app) richiesto per GoToAudit: la pagina apre dialoghi Flyout risolti come finestre separate.");
        return new AuditPage(_window, PageHost, _app);
    }

    public ReportPage GoToReport()
    {
        NavigateTo("report");
        if (_app == null) throw new InvalidOperationException("MainShellPage(window, app) richiesto per GoToReport: la marcatura N.A. apre TextEditDialog come finestra separata.");
        return new ReportPage(_window, PageHost, _app);
    }
}
