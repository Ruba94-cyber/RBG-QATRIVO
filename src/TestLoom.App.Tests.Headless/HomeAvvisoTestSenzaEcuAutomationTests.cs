using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressione FlaUI (HomeDashboardTests.AvvisoTestSenzaEcu_ComparInHome_ENavigaAllaListaFiltrata): il test
/// falliva SEMPRE dopo il timeout intero di 15s, anche isolato, anche con l'avviso corretto a schermo (verificato
/// con screenshot/dump UIA sul fallimento reale). Causa radice NON è una lentezza di calcolo (i tre commit
/// sospettati — 9f4ca0a cache Home, 9a13c79 scaldamento avvio, c2f0af0 hash Requisiti — sono estranei: HomeView
/// stessa non è mai stata nella lista di pagine scaldate all'avvio, vedi App.axaml.cs.PagineDaScaldare, e la
/// riproduzione mostra <c>Home_AvvisoTestSenzaEcu_Text</c>/<c>_Button</c> già presenti e corretti nel dump UIA
/// al momento del fallimento). La causa vera è strutturale: un <see cref="Border"/> Avalonia non riceve un
/// AutomationPeer "control element" di default (IsControlElement()==false), quindi non compare MAI nella vista
/// Content/Control di UI Automation che FlaUI interroga — l'AutomationId messo su un Border è irraggiungibile
/// indipendentemente da quanto velocemente i dati sono pronti. Verificato empiricamente anche per
/// <see cref="ContentControl"/> (stessa esclusione).
///
/// Fix in HomeView.axaml: <c>AutomationProperties.IsControlElementOverride="True"</c> sul Border stesso forza
/// il peer a dichiararsi control element, rendendolo raggiungibile senza cambiare albero visuale/layout. Questo
/// test headless verifica direttamente la proprietà del peer (senza serve FlaUI/una finestra reale): fallisce
/// senza la correzione, verificato.
/// <para>
/// Vive nel progetto headless (Category=LayoutHeadless): il test crea controlli Avalonia, quindi gira sul thread del Dispatcher
/// UI della piattaforma headless (<see cref="LayoutHeadlessSessione.Esegui(System.Action)"/>). Nel progetto di test principale il
/// Dispatcher UI appartiene al thread di DispatcherTestPump: costruire HomeView su un thread xUnit fallisce con "The calling thread
/// cannot access this object because a different thread owns it" (causa del fallimento precedente, non dell'avviso).
/// </para>
/// </summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class HomeAvvisoTestSenzaEcuAutomationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeAvvisoAutomation_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStatoConTestSenzaEcu()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        state.TestCases.Save(new TestCase { Id = "TC-1", Componente = new EcuSelection() }, isNew: true);
        return state;
    }

    /// <summary>Trova il Border dell'avviso per AutomationId (stesso identificatore che FlaUI cerca), sia il
    /// Border sia il Text/Button figli lo condividono lo stesso schema di ricerca già in uso nella suite UI.</summary>
    private static Border TrovaBorderAvviso(Control root) =>
        root.GetLogicalDescendants().OfType<Border>()
            .Single(b => AutomationProperties.GetAutomationId(b) == "Home_AvvisoTestSenzaEcu_Border");

    [Fact]
    public void BorderAvviso_HaUnAutomationPeerRaggiungibileDaUiAutomation() => LayoutHeadlessSessione.Esegui(() =>
    {
        var state = CreaStatoConTestSenzaEcu();
        var vm = new HomeViewModel(state, _ => { });
        Assert.True(vm.MostraAvvisoTestSenzaEcu); // precondizione: il dato è corretto fin da subito.

        // Come le altre viste headless: la Home è montata in una finestra e il layout passa come nel ciclo UI reale, perché i binding
        // IsVisible si applicano solo dopo misura e disposizione del controllo.
        var view = new HomeView { DataContext = vm };
        var finestra = new Window { Width = 1400, Height = 900, Content = view };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);

            var border = TrovaBorderAvviso(view);
            Assert.True(border.IsVisible);

            var peer = ControlAutomationPeer.CreatePeerForElement(border);
            Assert.NotNull(peer);

            // Senza AutomationProperties.IsControlElementOverride="True" su questo Border, IsControlElement()
            // torna false (comportamento di default Avalonia per Border/ContentControl/Panel, verificato anche per
            // ContentControl durante l'investigazione): un peer non-control-element non compare nella vista
            // Content/Control di UI Automation che FlaUI interroga, quindi ElementFinders.ExistsById non lo trova
            // MAI, a prescindere da quanto i dati sottostanti siano già pronti.
            Assert.True(peer!.IsControlElement(),
                "il Border dell'avviso deve essere un control element per essere raggiungibile da FlaUI/UI Automation");
            Assert.Equal("Home_AvvisoTestSenzaEcu_Border", peer.GetAutomationId());
        }
        finally
        {
            finestra.Hide();
        }
    });
}
