using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using TestLoom.App.Views.Controls;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class HomeFiltroCampoSistemaConteggioHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeVciConteggio_" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeVciConfig_" + Guid.NewGuid().ToString("N"));
    private AppState? _state;

    public void Dispose()
    {
        _state?.Dispose();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, recursive: true); } catch { } }
        if (Directory.Exists(_configDir)) { try { Directory.Delete(_configDir, recursive: true); } catch { } }
    }

    [Fact]
    public void Home_VciUsb_TestLoom_AggiornaRiepilogoUIConStessoConteggioListaTest() => LayoutHeadlessSessione.Esegui(() =>
    {
        var state = CreaStatoConTestVciUsb();
        var home = new HomeViewModel(state, _ => { }, aggiornaInBackground: true);
        LayoutHeadlessSessione.AttendiComando(home.AttendiAggiornamentoAsync(), TimeSpan.FromSeconds(30));

        var view = new HomeView { DataContext = home };
        var window = new Window { Width = 1440, Height = 900, Content = view };
        try
        {
            LayoutHeadlessSessione.Mostra(window);

            var apriAvanzati = view.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "Home_FiltriAvanzati_Apri_Button");
            apriAvanzati.Command!.Execute(apriAvanzati.CommandParameter);
            LayoutHeadlessSessione.Assesta(window);
            Assert.True(home.MostraAltriFiltriDashboard);

            var filtri = view.GetVisualDescendants().OfType<FiltriCampiSistemaView>().Single();
            Assert.True(filtri.IsVisible, "il filtro VCI deve essere nel pannello avanzato aperto");
            var apriVci = view.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "FiltroCampoSistema_vci-connection_Button");
            FiltroMultiSelectFlyoutHelper.Apri(apriVci, home.FiltriCampiCustomDashboard.Single(f => f.Chiave == "vci-connection").Filtro);
            LayoutHeadlessSessione.Assesta(window);

            var flyout = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(apriVci));
            var contenuto = Assert.IsAssignableFrom<Control>(flyout.Content);
            var listaOpzioni = contenuto.GetVisualDescendants().OfType<ListBox>()
                .Single(l => AutomationProperties.GetAutomationId(l) == "FiltroMultiSelect_Lista");
            Assert.Equal(2, listaOpzioni.ItemCount);
            var checkboxUsb = contenuto.GetVisualDescendants().OfType<CheckBox>()
                .Single(c => string.Equals(c.Content?.ToString(), "VCI USB", StringComparison.Ordinal));

            var notificheConteggio = 0;
            home.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HomeViewModel.TestoFiltroAttivo)) notificheConteggio++;
            };
            checkboxUsb.IsChecked = true;
            LayoutHeadlessSessione.Assesta(window);
            LayoutHeadlessSessione.AttendiComando(home.AttendiAggiornamentoAsync(), TimeSpan.FromSeconds(30));
            LayoutHeadlessSessione.Assesta(window);

            var riepilogo = view.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => AutomationProperties.GetAutomationId(t) == "Home_FiltroAttivo_Text");
            Assert.True(riepilogo.IsVisible, "il riepilogo del filtro attivo deve essere visibile dopo la selezione");
            Assert.True(notificheConteggio > 0, "il refresh non ha notificato il cambio del riepilogo");
            Assert.Equal(home.TestoFiltroAttivo, riepilogo.Text);
            Assert.Equal(string.Format(LocalizationService.Instance.Traduci("FiltroAttivoSingolareFmt"), 1), riepilogo.Text);

            var listaTest = new TestCaseListViewModel(state, _ => { });
            var filtroLista = listaTest.FiltriCampiCustomLista.Single(f => f.Chiave == "vci-connection");
            filtroLista.Filtro.Opzioni.Single(o => o.Valore == "VCI USB").Selezionato = true;
            listaTest.Aggiorna();

            Assert.Equal(1, listaTest.TotaleRisultati);
            Assert.Equal(listaTest.TotaleRisultati, home.TestCaseNelFiltro);
            Assert.Equal("VCI-USB-TestLoom", Assert.Single(listaTest.Righe).Id);
            Console.WriteLine($"HOME_VCI_RIEPILOGO|testo={riepilogo.Text}|home={home.TestCaseNelFiltro}|lista={listaTest.TotaleRisultati}|notifiche={notificheConteggio}");
        }
        finally
        {
            window.Hide();
        }
    });

    private AppState CreaStatoConTestVciUsb()
    {
        SeedDemoDataService.Genera(_dir);
        foreach (var path in Directory.GetFiles(_dir, "testcase_CORROTTO-TEST-99.json", SearchOption.AllDirectories))
            File.Delete(path);

        Directory.CreateDirectory(_configDir);
        var state = new AppState(new AppConfigService(Path.Combine(_configDir, "config.json")), runBackgroundMaintenance: false);
        _state = state;
        state.InizializzaCartella(_dir);
        var master = state.Master.Operatori.Load().Single(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));

        var campi = state.Master.CampiSistemaDettagli.Load();
        campi.Add(new CampoSistemaDefinizione
        {
            Chiave = "vci-connection", Etichetta = "VCI Connection", Gruppo = GruppoCampoSistema.Dettagli,
            Ruolo = RuoloCampoSistema.Nessuno, UsabileComeFiltro = true, Tipo = TipoCampoStrumento.Lista,
            OpzioniLista = new List<string> { "VCI USB", "VCI LAN" },
        });
        state.Master.CampiSistemaDettagli.Save(campi);

        var veicolo = state.Master.Veicoli.Load().First(v => !v.Archiviata).Nome;
        var componente = state.Master.Componente.Load().First(e => !e.Archiviata).Nome;
        var test = new TestCase
        {
            Id = "VCI-USB-TestLoom", Componente = { componente }, Veicolo = veicolo, Veicoli = { veicolo },
            Assegnatario = "Luigi", StatoLavoro = StatoLavoroTest.InCorso,
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
        };
        test.CampiCustom["vci-connection"] = "VCI USB";
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Operatore = "Luigi", Data = DateTime.Now,
            Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme, Componente = new EcuSelection(new[] { componente }) } },
        });
        state.TestCases.Save(test, isNew: true);
        state.TestCases.RefreshIndex();
        return state;
    }
}
