using TestLoom.App.Models;
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
/// "Componente e funzionalita'" del veicolo (6.7.2): dalla pagina Veicoli la finestra elenca i componenti e, per ciascuna montata, le
/// funzionalita' che valgono su quell'componente. Un Master monta un componente sul veicolo, aggiunge una funzionalita' non ancora
/// associata e salva: le pagine componente e Funzionalita' devono mostrare la modifica senza riaprire la finestra, e dopo un
/// riavvio di TestLoom la finestra deve riproporre lo stesso stato. Dati sintetici: un veicolo e un componente di base, un componente da
/// montare, una funzionalita' limitata alla sola componente di base.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
[Trait("Area", "Veicoli")]
public sealed class VeicoliEcuFunzionalitaUiTests : IDisposable
{
    private const string VeicoloBase = TestWorkspace.DefaultVehicleName;
    private const string EcuBase = TestWorkspace.DefaultEcuName;
    private const string EcuDaMontare = "Componente-Alfa";
    private const string FunzioneDaAggiungere = "Funz-Gamma";

    private readonly TestWorkspace _workspace;

    public VeicoliEcuFunzionalitaUiTests() => _workspace = CreaWorkspace();

    public void Dispose() => _workspace.Dispose();

    /// <summary>Workspace con il veicolo e il componente di base del test, un componente in più da montare e una funzionalità valida solo
    /// sul componente di base (quindi non ancora associata al componente da montare).</summary>
    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(VeicoliEcuFunzionalitaUiTests), seedTestFixtures: true);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var componente = state.Master.Componente.Load();
        componente.Add(new Componente { Nome = EcuDaMontare, ModificatoDa = "UITests" });
        state.Master.Componente.Save(componente);
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = FunzioneDaAggiungere, TutteLeEcu = false, EcuAssociate = new List<string> { EcuBase }, ModificatoDa = "UITests" },
        });
        return ws;
    }

    [Fact]
    public void Associazioni_MontaEcuEAggiungeFunzione_SalvaERiflettonoNellePagineERiapreConLoStessoStato()
    {
        using (var app1 = TestLoomApplication.Launch(_workspace))
        {
            try
            {
                var principale = app1.WaitForMainWindow(TimeSpan.FromSeconds(30));
                Wait.ForCondition(() => MainShellPage.IsShown(principale), "shell principale visibile", TimeSpan.FromSeconds(30));
                var shell = new MainShellPage(principale, app1);
                shell.DismissNotificationsIfShown();

                var finestra = ApriFinestraAssociazioni(shell, principale);
                ElementFinders.WaitButtonById(finestra, "Assoc_Modifica_Button").SafeInvoke();
                Wait.ForCondition(() => ElementFinders.ExistsById(finestra, "Assoc_Annulla_Button"),
                    "modifica attiva nella finestra componente e funzionalita'", TimeSpan.FromSeconds(20));

                ImpostaSpunta(CasellaEcu(finestra, EcuDaMontare), accesa: true);
                EspandiEcu(finestra, EcuDaMontare);
                Wait.ForCondition(() => ElementFinders.ExistsById(finestra, "Assoc_ComboFunzione_ComboBox") || ElementFinders.ExistsById(finestra, "Assoc_SceltaFunzione_Button"),
                    "scelta di una funzionalita' da aggiungere", TimeSpan.FromSeconds(20));
                ElementFinders.WaitComboBoxById(finestra, "Assoc_ComboFunzione_ComboBox").SelectByText(FunzioneDaAggiungere);
                ElementFinders.WaitButtonById(finestra, "Assoc_AggiungiFunzione_Button").SafeInvoke();
                Wait.ForCondition(() => CasellaFunzione(finestra, FunzioneDaAggiungere) != null,
                    $"funzionalita' '{FunzioneDaAggiungere}' elencata sotto il componente montata", TimeSpan.FromSeconds(20));

                ElementFinders.WaitButtonById(finestra, "Assoc_Salva_Button").SafeInvoke();
                Wait.ForCondition(() => ElementFinders.ExistsById(finestra, "Assoc_Modifica_Button"),
                    "uscita dalla modifica dopo il salvataggio", TimeSpan.FromSeconds(20));
                ElementFinders.WaitButtonById(finestra, "Assoc_Chiudi_Button").SafeInvoke();
                Wait.ForCondition(() => app1.IsWindowClosedByContent("Assoc_ElencoEcu_Lista"),
                    "finestra componente e funzionalita' chiusa", TimeSpan.FromSeconds(15));

                // Le pagine componente e Funzionalita' mostrano la modifica senza riaprire la finestra del veicolo.
                var impostazioni = shell.GoToImpostazioni();
                var paginaEcu = impostazioni.VaiAEcu();
                var rigaEcu = Wait.ForElement(() => RigaConTesto(paginaEcu.Grid, EcuDaMontare),
                    $"riga '{EcuDaMontare}' nella pagina componente", TimeSpan.FromSeconds(20));
                Assert.Contains(TestiDiscendenti(rigaEcu), t => t.Contains(VeicoloBase, StringComparison.Ordinal));

                var paginaFunzionalita = impostazioni.VaiAFunzionalita();
                var rigaFunzionalita = Wait.ForElement(() => RigaConTesto(paginaFunzionalita.Grid, FunzioneDaAggiungere),
                    $"riga '{FunzioneDaAggiungere}' nella pagina Funzionalita'", TimeSpan.FromSeconds(20));
                Assert.Contains(TestiDiscendenti(rigaFunzionalita), t => t.Contains(EcuDaMontare, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                var dir = FailureArtifacts.Capture(app1, nameof(Associazioni_MontaEcuEAggiungeFunzione_SalvaERiflettonoNellePagineERiapreConLoStessoStato), ex, step: "sessione 1 - modifica e pagine");
                throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
            }
        }

        using (var app2 = TestLoomApplication.Launch(_workspace))
        {
            try
            {
                var principale = app2.WaitForMainWindow(TimeSpan.FromSeconds(30));
                Wait.ForCondition(() => MainShellPage.IsShown(principale), "shell principale visibile", TimeSpan.FromSeconds(30));
                var shell = new MainShellPage(principale, app2);
                shell.DismissNotificationsIfShown();

                var finestra = ApriFinestraAssociazioni(shell, principale);
                Assert.Equal(ToggleState.On, StatoSpunta(CasellaEcu(finestra, EcuDaMontare)));
                EspandiEcu(finestra, EcuDaMontare);
                Assert.Equal(ToggleState.On, StatoSpunta(CasellaFunzione(finestra, FunzioneDaAggiungere)
                    ?? throw new InvalidOperationException($"funzionalita' '{FunzioneDaAggiungere}' assente dopo il riavvio")));
                ElementFinders.WaitButtonById(finestra, "Assoc_Chiudi_Button").SafeInvoke();
            }
            catch (Exception ex)
            {
                var dir = FailureArtifacts.Capture(app2, nameof(Associazioni_MontaEcuEAggiungeFunzione_SalvaERiflettonoNellePagineERiapreConLoStessoStato), ex, step: "sessione 2 - riapertura dopo riavvio");
                throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
            }
        }
    }

    [Fact]
    public void Associazioni_ConModificheNonSalvate_ChiusuraEAnnullaChiedonoConferma_SenzaScrivereNulla()
    {
        using var app = TestLoomApplication.Launch(_workspace);
        try
        {
            var principale = app.WaitForMainWindow(TimeSpan.FromSeconds(30));
            Wait.ForCondition(() => MainShellPage.IsShown(principale), "shell principale visibile", TimeSpan.FromSeconds(30));
            var shell = new MainShellPage(principale, app);
            shell.DismissNotificationsIfShown();

            var finestra = ApriFinestraAssociazioni(shell, principale);
            ElementFinders.WaitButtonById(finestra, "Assoc_Modifica_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(finestra, "Assoc_Annulla_Button"),
                "modifica attiva nella finestra componente e funzionalita'", TimeSpan.FromSeconds(20));
            ImpostaSpunta(CasellaEcu(finestra, EcuDaMontare), accesa: true);

            // Chiudi con modifiche non salvate: TestLoom chiede conferma; "No" lascia la finestra aperta in modifica.
            ElementFinders.WaitButtonById(finestra, "Assoc_Chiudi_Button").SafeInvoke();
            new ConfirmDialogPage(app.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmNo();
            Wait.ForCondition(() => ElementFinders.ExistsById(finestra, "Assoc_Annulla_Button"),
                "la finestra resta in modifica dopo 'No'", TimeSpan.FromSeconds(10));
            Assert.Equal(ToggleState.On, StatoSpunta(CasellaEcu(finestra, EcuDaMontare)));

            // Annulla con modifiche: "Sì" scarta la spunta e riporta alla sola lettura.
            ElementFinders.WaitButtonById(finestra, "Assoc_Annulla_Button").SafeInvoke();
            new ConfirmDialogPage(app.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => ElementFinders.ExistsById(finestra, "Assoc_Modifica_Button"),
                "ritorno alla sola lettura dopo Annulla", TimeSpan.FromSeconds(20));
            Assert.Equal(ToggleState.Off, StatoSpunta(CasellaEcu(finestra, EcuDaMontare)));

            // Chiusura senza modifiche: nessuna conferma, la finestra si chiude.
            ElementFinders.WaitButtonById(finestra, "Assoc_Chiudi_Button").SafeInvoke();
            Wait.ForCondition(() => app.IsWindowClosedByContent("Assoc_ElencoEcu_Lista"),
                "finestra componente e funzionalita' chiusa senza conferma", TimeSpan.FromSeconds(15));

            // Nulla è stato scritto: la pagina componente non mostra il veicolo sul componente da montare.
            var paginaEcu = shell.GoToImpostazioni().VaiAEcu();
            var rigaEcu = Wait.ForElement(() => RigaConTesto(paginaEcu.Grid, EcuDaMontare),
                $"riga '{EcuDaMontare}' nella pagina componente", TimeSpan.FromSeconds(20));
            Assert.DoesNotContain(TestiDiscendenti(rigaEcu), t => t.Contains(VeicoloBase, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            var dir = FailureArtifacts.Capture(app, nameof(Associazioni_ConModificheNonSalvate_ChiusuraEAnnullaChiedonoConferma_SenzaScrivereNulla), ex, step: "conferme di chiusura e annulla");
            throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
        }
    }

    [Fact]
    public void Associazioni_LockAltruiMostraConflitto_EAllaRiaperturaPermetteSalvataggio()
    {
        var locksRemoti = new LockService("UITests-PC-remoto-associazioni");
        string percorsoEcu;
        string[] veicoliPrima;
        using (var state = new AppState(new AppConfigService(_workspace.ConfigPath), runBackgroundMaintenance: false))
        {
            percorsoEcu = state.Paths.EcuFile;
            veicoliPrima = state.Master.Componente.Load().Single(e => e.Nome == EcuDaMontare).VeicoliAssociati.OrderBy(v => v).ToArray();
        }

        const string operatoreRemoto = "Operatore lock remoto UI";
        var lockRemoto = locksRemoti.TryAcquire(percorsoEcu, operatoreRemoto);
        Assert.True(lockRemoto.Acquisito, "il workspace isolato accetta il lock di conflitto seminato dal test");

        try
        {
            using (var app = TestLoomApplication.Launch(_workspace))
            {
                try
                {
                    var principale = app.WaitForMainWindow(TimeSpan.FromSeconds(30));
                    Wait.ForCondition(() => MainShellPage.IsShown(principale), "shell principale visibile", TimeSpan.FromSeconds(30));
                    var shell = new MainShellPage(principale, app);
                    shell.DismissNotificationsIfShown();

                    var finestra = ApriFinestraAssociazioni(shell, principale);
                    ElementFinders.WaitButtonById(finestra, "Assoc_Modifica_Button").SafeInvoke();
                    Wait.ForCondition(() => ElementFinders.ExistsById(finestra, "Assoc_LockInfo_Text"),
                        "la finestra mostra il banner del lock detenuto dall'altra postazione", TimeSpan.FromSeconds(20));
                    Assert.Contains(operatoreRemoto, ElementFinders.WaitById(finestra, "Assoc_LockInfo_Text").Name ?? string.Empty);
                    Assert.True(ElementFinders.ExistsById(finestra, "Assoc_Modifica_Button"), "il conflitto lascia la finestra in sola lettura");
                    Assert.False(ElementFinders.ExistsById(finestra, "Assoc_Salva_Button"), "il conflitto non espone il comando di salvataggio");

                    ElementFinders.WaitButtonById(finestra, "Assoc_Chiudi_Button").SafeInvoke();
                    Wait.ForCondition(() => app.IsWindowClosedByContent("Assoc_ElencoEcu_Lista"),
                        "chiusura della finestra in sola lettura", TimeSpan.FromSeconds(15));

                    using (var verificaConflitto = new AppState(new AppConfigService(_workspace.ConfigPath), runBackgroundMaintenance: false))
                    {
                        var ecuDopoConflitto = verificaConflitto.Master.Componente.Load().Single(e => e.Nome == EcuDaMontare);
                        Assert.Equal(veicoliPrima, ecuDopoConflitto.VeicoliAssociati.OrderBy(v => v));
                    }

                    Assert.Equal(EsitoIo.Ok, locksRemoti.Release(percorsoEcu));

                    var riaperta = ApriFinestraAssociazioni(shell, principale);
                    ElementFinders.WaitButtonById(riaperta, "Assoc_Modifica_Button").SafeInvoke();
                    Wait.ForCondition(() => ElementFinders.ExistsById(riaperta, "Assoc_Annulla_Button"),
                        "dopo il rilascio del lock la nuova finestra entra in modifica", TimeSpan.FromSeconds(20));
                    Assert.False(ElementFinders.ExistsById(riaperta, "Assoc_LockInfo_Text"), "la nuova finestra non conserva il banner del conflitto precedente");

                    ImpostaSpunta(CasellaEcu(riaperta, EcuDaMontare), accesa: true);
                    ElementFinders.WaitButtonById(riaperta, "Assoc_Salva_Button").SafeInvoke();
                    Wait.ForCondition(() => ElementFinders.ExistsById(riaperta, "Assoc_Modifica_Button"),
                        "il salvataggio riporta la finestra in sola lettura", TimeSpan.FromSeconds(20));
                    ElementFinders.WaitButtonById(riaperta, "Assoc_Chiudi_Button").SafeInvoke();
                    Wait.ForCondition(() => app.IsWindowClosedByContent("Assoc_ElencoEcu_Lista"),
                        "chiusura della finestra dopo il salvataggio", TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    var dir = FailureArtifacts.Capture(app, nameof(Associazioni_LockAltruiMostraConflitto_EAllaRiaperturaPermetteSalvataggio), ex, step: "conflitto, rilascio e riapertura");
                    throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
                }
            }

            using var verifica = new AppState(new AppConfigService(_workspace.ConfigPath), runBackgroundMaintenance: false);
            var ecuSalvata = verifica.Master.Componente.Load().Single(e => e.Nome == EcuDaMontare);
            Assert.Contains(VeicoloBase, ecuSalvata.VeicoliAssociati);
        }
        finally
        {
            locksRemoti.Release(percorsoEcu);
        }
    }

    // ---------------------------------------------------------------- flusso condiviso

    /// <summary>Impostazioni → Veicoli → seleziona il veicolo di base → "Componente e funzionalita'". La finestra è posseduta dalla
    /// principale, quindi in UI Automation è un discendente della principale: si cerca per il suo contenuto.</summary>
    private static AutomationElement ApriFinestraAssociazioni(MainShellPage shell, Window principale)
    {
        shell.GoToImpostazioni().VaiA("veicoli");
        var griglia = ElementFinders.WaitById(principale, "GridPrincipale");
        var descrizioneRiga = $"riga del veicolo '{VeicoloBase}' nella griglia Veicoli";
        var rigaVeicolo = Wait.ForElement(
            () => griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(riga => riga.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Any(t => string.Equals(t.Name, VeicoloBase, StringComparison.Ordinal))),
            descrizioneRiga);
        if (rigaVeicolo.Patterns.SelectionItem.TryGetPattern(out var selezioneRiga))
        {
            selezioneRiga.Select();
        }
        else
        {
            // Le righe Avalonia DataGrid spesso non espongono SelectionItem: seleziona quella riga dalla tastiera,
            // usando il suo indice UIA per evitare un click sul Text discendente, che non ha un punto cliccabile.
            var righe = griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).ToList();
            var indiceRiga = righe.FindIndex(riga => riga.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Any(t => string.Equals(t.Name, VeicoloBase, StringComparison.Ordinal)));
            if (indiceRiga < 0) throw new InvalidOperationException($"{descrizioneRiga} non è più disponibile.");

            griglia.Focus();
            FlaUI.Core.Input.Keyboard.TypeSimultaneously(
                FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                FlaUI.Core.WindowsAPI.VirtualKeyShort.HOME);
            for (var i = 0; i < indiceRiga; i++)
                FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
        }
        Wait.ForCondition(() => ElementFinders.WaitButtonById(principale, "Veic_Associazioni_Button").IsEnabled,
            "pulsante componente e funzionalita' abilitato dopo la selezione");
        ElementFinders.WaitButtonById(principale, "Veic_Associazioni_Button").SafeInvoke();
        return Wait.ForElement(() => TrovaFinestra(principale, "Assoc_ElencoEcu_Lista"),
            "finestra componente e funzionalita' del veicolo", TimeSpan.FromSeconds(20));
    }

    /// <summary>Casella "montata" della riga componente: le righe dell'elenco ripetono gli AutomationId, quindi si cerca per il nome
    /// del componente (un dato seminato dal test), che è il testo della casella.</summary>
    private static AutomationElement CasellaEcu(AutomationElement finestra, string nomeEcu) =>
        Wait.ForElement(() => finestra.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox).And(cf.ByName(nomeEcu))).FirstOrDefault(),
            $"casella montata del componente '{nomeEcu}'", TimeSpan.FromSeconds(20));

    /// <summary>Casella di una funzionalità applicata sul componente espansa, o null se la funzionalità non è elencata.</summary>
    private static AutomationElement? CasellaFunzione(AutomationElement finestra, string nomeFunzione) =>
        finestra.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox).And(cf.ByName(nomeFunzione))).FirstOrDefault();

    /// <summary>Espande il dettaglio (funzionalità) della riga del componente montata.</summary>
    private static void EspandiEcu(AutomationElement finestra, string nomeEcu)
    {
        var riga = RigaDellaCasella(CasellaEcu(finestra, nomeEcu));
        Wait.ForElement(() => riga.FindFirstDescendant(cf => cf.ByAutomationId("Assoc_EcuEspandi_Button")),
            $"pulsante Espandi della riga del componente '{nomeEcu}'", TimeSpan.FromSeconds(20)).SafeInvoke();
    }
}
