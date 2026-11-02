using System.Linq;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Wait = TestLoom.App.UITests.Infrastructure.Wait;
using static TestLoom.App.UITests.Helpers.CaselleUia;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Catalogo dei campi di sistema (6.7.2): un campo Lista ha il pulsante "Opzioni" (editor a una voce per riga, che si apre
/// subito quando il campo diventa Lista senza opzioni) e un campo Numero ha la casella "Unita'" nella stessa colonna
/// azioni. Il catalogo si apre dal template come nel percorso reale dell'utente (Impostazioni → Tipi di test → Gestisci
/// campi → catalogo). Le modifiche si verificano dopo un riavvio di TestLoom, rileggendo il catalogo.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
[Trait("Area", "CampiSistema")]
public sealed class CampiSistemaOpzioniUnitaUiTests : IDisposable
{
    private const string EtichettaLista = "Canale Test";
    private const string EtichettaNumero = "Pressione Test";
    private const string TipoLista = "Lista a tendina";
    private const string TipoNumero = "Numero";
    private const string OpzioniAttese = "Opzioni (2)";
    private const string UnitaAttesa = "bar";

    private readonly TestWorkspace _workspace;

    public CampiSistemaOpzioniUnitaUiTests() => _workspace = TestWorkspace.CreateLoggedIn(nameof(CampiSistemaOpzioniUnitaUiTests), seedTestFixtures: true);

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void CampoLista_OpzioniSalvateEVisibiliDopoRiavvio()
    {
        var chiave = ChiaveDi(EtichettaLista);
        using (var app1 = TestLoomApplication.Launch(_workspace))
        {
            try
            {
                var principale = PrimaFinestra(app1);
                var shell = new MainShellPage(principale, app1);
                shell.DismissNotificationsIfShown();
                var catalogo = ApriCatalogoInModifica(shell, principale);

                NuovoCampo(catalogo, EtichettaLista, TipoLista);
                // Il campo è diventato Lista senza opzioni: l'editor delle opzioni si apre subito.
                AutomationElement finestraOpzioni;
                try
                {
                    finestraOpzioni = Wait.ForElement(() => TrovaFinestra(principale, "ListaOpzioni_Testo_TextBox"),
                        "editor delle opzioni aperto dopo la scelta del tipo Lista", TimeSpan.FromSeconds(20));
                }
                catch (TimeoutException ex)
                {
                    var riga = UltimaRiga(catalogo);
                    var selettore = riga?.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox));
                    throw new TimeoutException($"{ex.Message} Stato riga nuova: selettore '{selettore?.Name}', " +
                        $"pulsante Opzioni '{ElementoConPrefisso(riga, "CampoSist_Opzioni_")?.Name ?? "assente"}', " +
                        $"testi '{string.Join(" | ", riga?.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name) ?? Array.Empty<string>())}'.", ex);
                }
                ElementFinders.WaitTextBoxById(finestraOpzioni, "ListaOpzioni_Testo_TextBox").SetText("VCI USB\nVCI LAN");
                ElementFinders.WaitButtonById(finestraOpzioni, "ListaOpzioni_Salva_Button").SafeInvoke();
                Wait.ForCondition(() => TrovaFinestra(principale, "ListaOpzioni_Testo_TextBox") == null,
                    "editor delle opzioni chiuso dopo Salva", TimeSpan.FromSeconds(15));
                // Prima del salvataggio la chiave della riga è ancora vuota: il pulsante si cerca per prefisso dentro la riga.
                Wait.ForCondition(() => ElementoConPrefisso(UltimaRiga(catalogo), "CampoSist_Opzioni_")?.Name == OpzioniAttese,
                    $"pulsante Opzioni del campo '{EtichettaLista}' con '{OpzioniAttese}'", TimeSpan.FromSeconds(15));

                SalvaCatalogo(catalogo);
            }
            catch (Exception ex)
            {
                var dir = FailureArtifacts.Capture(app1, nameof(CampoLista_OpzioniSalvateEVisibiliDopoRiavvio), ex, step: "sessione 1 - campo Lista con opzioni");
                throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
            }
        }

        using (var app2 = TestLoomApplication.Launch(_workspace))
        {
            try
            {
                var principale = PrimaFinestra(app2);
                var shell = new MainShellPage(principale, app2);
                shell.DismissNotificationsIfShown();
                var catalogo = ApriCatalogo(shell, principale);
                Wait.ForCondition(() => ElementFinders.WaitById(catalogo, $"CampoSist_Opzioni_{chiave}").Name == OpzioniAttese,
                    $"pulsante Opzioni del campo '{EtichettaLista}' con '{OpzioniAttese}' dopo il riavvio", TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                var dir = FailureArtifacts.Capture(app2, nameof(CampoLista_OpzioniSalvateEVisibiliDopoRiavvio), ex, step: "sessione 2 - riapertura catalogo");
                throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
            }
        }
    }

    [Fact]
    public void CampoNumero_UnitaSalvataEVisibileDopoRiavvio()
    {
        var chiave = ChiaveDi(EtichettaNumero);
        using (var app1 = TestLoomApplication.Launch(_workspace))
        {
            try
            {
                var principale = PrimaFinestra(app1);
                var shell = new MainShellPage(principale, app1);
                shell.DismissNotificationsIfShown();
                var catalogo = ApriCatalogoInModifica(shell, principale);

                NuovoCampo(catalogo, EtichettaNumero, TipoNumero);
                var unita = Wait.ForElement(() => ElementoConPrefisso(UltimaRiga(catalogo), "CampoSist_Unita_"),
                    "casella Unita' del campo Numero", TimeSpan.FromSeconds(15));
                unita.AsTextBox().SetText(UnitaAttesa);

                SalvaCatalogo(catalogo);
            }
            catch (Exception ex)
            {
                var dir = FailureArtifacts.Capture(app1, nameof(CampoNumero_UnitaSalvataEVisibileDopoRiavvio), ex, step: "sessione 1 - campo Numero con unita'");
                throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
            }
        }

        using (var app2 = TestLoomApplication.Launch(_workspace))
        {
            try
            {
                var principale = PrimaFinestra(app2);
                var shell = new MainShellPage(principale, app2);
                shell.DismissNotificationsIfShown();
                var catalogo = ApriCatalogo(shell, principale);
                var unita = Wait.ForElement(() => ElementFinders.TryFindById(catalogo, $"CampoSist_Unita_{chiave}"),
                    "casella Unita' del campo Numero dopo il riavvio", TimeSpan.FromSeconds(15));
                Wait.ForCondition(() => unita.AsTextBox().Text == UnitaAttesa,
                    $"unita' '{UnitaAttesa}' letta dal catalogo dopo il riavvio", TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                var dir = FailureArtifacts.Capture(app2, nameof(CampoNumero_UnitaSalvataEVisibileDopoRiavvio), ex, step: "sessione 2 - riapertura catalogo");
                throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
            }
        }
    }

    // ---------------------------------------------------------------- flusso condiviso

    private static Window PrimaFinestra(TestLoomApplication app)
    {
        var principale = app.WaitForMainWindow(TimeSpan.FromSeconds(30));
        Wait.ForCondition(() => MainShellPage.IsShown(principale), "shell principale visibile", TimeSpan.FromSeconds(30));
        return principale;
    }

    /// <summary>Chiave generata dal servizio del catalogo per un'etichetta nuova (stessa regola della UI in un catalogo senza
    /// altri campi, come nei test del catalogo esistenti).</summary>
    private static string ChiaveDi(string etichetta) =>
        CampoSistemaCatalogoService.GeneraChiaveDaEtichetta(etichetta, Enumerable.Empty<string>());

    /// <summary>Impostazioni → Tipi di test → Modifica → Gestisci campi → Gestisci catalogo, in sola lettura.</summary>
    private static AutomationElement ApriCatalogo(MainShellPage shell, Window principale)
    {
        shell.GoToImpostazioni().VaiA("tipiditest");
        Wait.ForCondition(() => ElementFinders.ExistsById(principale, "GridPrincipale"), "griglia Tipi di test caricata");
        ElementFinders.WaitButtonById(principale, "Tpl_Modifica_Button").SafeInvoke();
        ElementFinders.WaitButtonById(principale, "TplGestisci_Button").SafeInvoke();
        var tpl = Wait.ForElement(() => TrovaFinestra(principale, "TplEditor_Chiudi_Button"), "editor del tipo di test");
        ElementFinders.WaitButtonById(tpl, "TplGestisciCampi_Dettagli").SafeInvoke();
        var campi = Wait.ForElement(() => TrovaFinestra(principale, "CampiEditor_Chiudi_Button"), "editor dei campi del template");
        ElementFinders.WaitButtonById(campi, "TplGestisciCatalogo_Button").SafeInvoke();
        return Wait.ForElement(() => TrovaFinestra(principale, "CampoSist_Modifica_Button"), "catalogo dei campi di sistema");
    }

    /// <summary>Come <see cref="ApriCatalogo"/>, poi "Modifica": il catalogo è in modifica e "+ Nuovo" è disponibile.</summary>
    private static AutomationElement ApriCatalogoInModifica(MainShellPage shell, Window principale)
    {
        var catalogo = ApriCatalogo(shell, principale);
        ElementFinders.WaitButtonById(catalogo, "CampoSist_Modifica_Button").SafeInvoke();
        Wait.ForCondition(() => ElementFinders.ExistsById(catalogo, "CampoSist_Nuovo_Button"), "pulsante Nuovo del catalogo", TimeSpan.FromSeconds(15));
        return catalogo;
    }

    /// <summary>"+ Nuovo" aggiunge una riga in fondo, già in modifica sull'Etichetta: si scrive il nome e si sceglie il tipo.</summary>
    private static void NuovoCampo(AutomationElement catalogo, string etichetta, string tipo)
    {
        ElementFinders.WaitButtonById(catalogo, "CampoSist_Nuovo_Button").SafeInvoke();
        var editor = Wait.ForElement(() => UltimaRiga(catalogo)?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)),
            "editor dell'Etichetta della riga nuova", TimeSpan.FromSeconds(15));
        editor.AsTextBox().SetText(etichetta);
        // Come negli altri editor DataGrid di Avalonia: il fuoco sulla spunta della stessa riga conferma la cella.
        UltimaRiga(catalogo)?.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox))?.Focus();

        // Il campo nuovo parte senza un tipo noto: prima "Testo libero", poi il tipo voluto. Il passaggio da un tipo noto
        // a Lista e' quello per cui l'editor delle opzioni si apre subito (OnTipoCambiato / DeveProporre).
        SceglieTipo(catalogo, "Testo libero");
        if (tipo != "Testo libero") SceglieTipoConTastiera(catalogo, tipo);
    }

    /// <summary>Sceglie il tipo con le frecce, col selettore a fuoco: solo così il cambio avviene con il fuoco nel selettore
    /// (OnTipoCambiato richiede IsKeyboardFocusWithin). Il testo mostrato dal selettore è il primo Text discendente.</summary>
    private static void SceglieTipoConTastiera(AutomationElement catalogo, string tipo)
    {
        var selettore = Wait.ForElement(() => UltimaRiga(catalogo)?.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox)),
            $"selettore Tipo della riga nuova per '{tipo}'", TimeSpan.FromSeconds(15));
        selettore.Focus();
        for (var i = 0; i < 8; i++)
        {
            if (selettore.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text))?.Name == tipo) return;
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
            Wait.TryForCondition(() => false, TimeSpan.FromMilliseconds(150));
        }
        throw new InvalidOperationException($"Tipo '{tipo}' non raggiunto con le frecce del selettore.");
    }

    /// <summary>Apre il selettore Tipo della riga nuova con un clic (il fuoco resta nel selettore, come per un utente) e
    /// sceglie la voce con un clic.</summary>
    private static void SceglieTipo(AutomationElement catalogo, string tipo)
    {
        var selettore = Wait.ForElement(() => UltimaRiga(catalogo)?.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox)),
            $"selettore Tipo della riga nuova per '{tipo}'", TimeSpan.FromSeconds(15)).AsComboBox();
        selettore.Click();
        Wait.ForCondition(() => selettore.Items.Any(i => i.Text == tipo), $"voce '{tipo}' nel selettore Tipo", TimeSpan.FromSeconds(10));
        selettore.Items.First(i => i.Text == tipo).Click();
    }

    private static void SalvaCatalogo(AutomationElement catalogo)
    {
        ElementFinders.WaitButtonById(catalogo, "CampoSist_Salva_Button").SafeInvoke();
        Wait.ForCondition(() => ElementFinders.ExistsById(catalogo, "CampoSist_Modifica_Button"),
            "catalogo salvato (fuori dalla modifica)", TimeSpan.FromSeconds(20));
    }

    /// <summary>Primo discendente della riga il cui AutomationId inizia col prefisso (le chiavi dei campi nuovi nascono al salvataggio).</summary>
    private static AutomationElement? ElementoConPrefisso(AutomationElement? riga, string prefisso) =>
        riga?.FindAllDescendants().FirstOrDefault(d => (d.Properties.AutomationId.ValueOrDefault ?? "").StartsWith(prefisso, StringComparison.Ordinal));

    private static AutomationElement? UltimaRiga(AutomationElement catalogo) =>
        catalogo.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).LastOrDefault();
}
