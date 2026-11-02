using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Primo avvio su cartella vuota: "Solo configurazione" (senza Test ne' operatori demo) e "Carica demo completa"
/// seguito dal cambio PIN obbligatorio dell'account demo (PIN 1234 rifiutato, poi PIN nuovo, codice di recupero, Home).
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "PrimoAvvio")]
public sealed class ShellHomeSettingsCoverageFirstRunUiTests : UiTestBase
{
    private const string OperatoreDemo = "Operatore Prova A";
    private const string PinDemo = "1234";
    private const string PinNuovo = "5837";

    public ShellHomeSettingsCoverageFirstRunUiTests() : base(TestWorkspace.CreateEmptyFirstRun(nameof(ShellHomeSettingsCoverageFirstRunUiTests))) { }

    private string ImpostaCartellaVuota(Window window, string nome)
    {
        var vuota = Path.Combine(Workspace.RootDir, nome);
        Directory.CreateDirectory(vuota);
        ElementFinders.WaitTextBoxById(window, "Login_Cartella_TextBox").SetText(vuota);
        return vuota;
    }

    private static void AttendiMessaggioDemo(Window window, string inizio, TimeSpan timeout) =>
        Wait.ForCondition(() => ElementFinders.TryFindById(window, "Login_DemoMessaggio_Text") is { } m
                && VisibileInUia(m) && (m.Name ?? "").StartsWith(inizio, StringComparison.Ordinal),
            $"esito demo: '{inizio}'", timeout);

    [Fact]
    public void PrimoAvvio_ScegliCartellaDalPickerNativo_AggiornaIlPercorso()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var cartella = Path.Combine(Workspace.RootDir, "cartella-sintetica-picker");
            Directory.CreateDirectory(cartella);

            // Il pulsante non ha AutomationId: lingua del workspace fissata a italiano, quindi il nome accessibile
            // resta stabile. NativeFileDialogPage gestisce il common dialog FolderPicker (campo UIA 1152).
            var sfoglia = Wait.ForElement(
                () => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Sfoglia…"))),
                "pulsante Sfoglia del primo avvio", TimeSpan.FromSeconds(15));
            sfoglia.AsButton().SafeInvoke();

            NativeFileDialogPage.Attendi(App, TimeSpan.FromSeconds(20)).ConfermaPercorso(cartella);
            Wait.ForCondition(() => string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(ElementFinders.WaitTextBoxById(window, "Login_Cartella_TextBox").Text)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(cartella)), StringComparison.OrdinalIgnoreCase),
                "la cartella sintetica selezionata compare nel campo del primo avvio", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(PrimoAvvio_ScegliCartellaDalPickerNativo_AggiornaIlPercorso), ex); }
    }

    [Fact]
    public void PrimoAvvio_SoloConfigurazione_CreaLaBaseSenzaTestNeOperatori()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var vuota = ImpostaCartellaVuota(window, "solo-configurazione");

            ElementFinders.WaitButtonById(window, "Login_CaricaDemoConfigurazione_Button").SafeInvoke();
            AttendiMessaggioDemo(window, "Configurazione demo caricata senza Test", TimeSpan.FromSeconds(90));

            var paths = new DataPaths(vuota);
            Assert.True(File.Exists(paths.VersionManifestFile), "la sola configurazione scrive il manifest di versione");
            Assert.False(Directory.Exists(paths.TestCasesDir) && Directory.EnumerateFiles(paths.TestCasesDir, "*.json", SearchOption.AllDirectories).Any(),
                "la sola configurazione non crea nessun Test");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(PrimoAvvio_SoloConfigurazione_CreaLaBaseSenzaTestNeOperatori), ex); }
    }

    [Fact]
    public void PrimoAvvio_DemoCompleta_ObbligaCambioPinEPoiApreLaHome()
    {
        try
        {
            var window = App.WaitForMainWindow();
            ImpostaCartellaVuota(window, "demo-completa");

            ElementFinders.WaitButtonById(window, "Login_CaricaDemoCompleta_Button").SafeInvoke();
            AttendiMessaggioDemo(window, "", TimeSpan.FromSeconds(90));

            var login = new LoginPage(window);
            login.LoginAs(OperatoreDemo, PinDemo);
            Wait.ForCondition(() => IdVisibile(window, "Login_NuovoPin_TextBox"),
                "cambio PIN obbligatorio per l'account demo", TimeSpan.FromSeconds(20));

            // Il PIN demo 1234 non e' accettato come nuovo PIN.
            ElementFinders.WaitTextBoxById(window, "Login_NuovoPin_TextBox").SetText(PinDemo);
            ElementFinders.WaitTextBoxById(window, "Login_ConfermaNuovoPin_TextBox").SetText(PinDemo);
            ElementFinders.WaitButtonById(window, "Login_ImpostaPin_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "Login_Error_Text") is { } e && VisibileInUia(e)
                    && (e.Name ?? "").Contains("diverso da 1234", StringComparison.Ordinal),
                "il PIN 1234 viene rifiutato come nuovo PIN", TimeSpan.FromSeconds(15));

            // PIN nuovo valido: compare il codice di recupero da confermare prima di entrare nella Home.
            ElementFinders.WaitTextBoxById(window, "Login_NuovoPin_TextBox").SetText(PinNuovo);
            ElementFinders.WaitTextBoxById(window, "Login_ConfermaNuovoPin_TextBox").SetText(PinNuovo);
            ElementFinders.WaitButtonById(window, "Login_ImpostaPin_Button").SafeInvoke();
            Wait.ForCondition(() => IdVisibile(window, "Login_CodiceRecupero_Text"),
                "codice di recupero mostrato dopo il nuovo PIN", TimeSpan.FromSeconds(30));

            ElementFinders.WaitButtonById(window, "Login_CodiceRecuperoContinua_Button").SafeInvoke();
            Wait.ForCondition(() => IdVisibile(window, "Home_Title_Text"), "Home visibile dopo il cambio PIN", TimeSpan.FromSeconds(60));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(PrimoAvvio_DemoCompleta_ObbligaCambioPinEPoiApreLaHome), ex); }
    }
}
