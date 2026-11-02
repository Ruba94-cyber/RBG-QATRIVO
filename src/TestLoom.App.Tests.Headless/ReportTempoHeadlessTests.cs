using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Report in modalita' temporale (TIME, 6.6.1) in headless: selettore Tempo totale / Tempo medio, base "Tutte le
/// esecuzioni" / "Ultimo stato" con il suo selettore visibile solo nelle modalita' temporali, filtro sulla durata che non cambia
/// gli assi. Un Test con due revisioni (20 e 10 minuti) su una sola combinazione: tutte le esecuzioni 30 (2/2), ultimo stato 10 (1/1).</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class ReportTempoHeadlessTests
{
    private const string CellaDiagnosticaBcm = "ReportMatrice_Cella_Diagnostica_BCM_Text";

    [Fact]
    public void Report_FiltroGammaSalvatoCompareNelRiepilogoAncheSenzaGammaNegliAssi() => LayoutHeadlessSessione.Esegui(() =>
    {
        using var stato = CreaStato();
        var prima = new ReportViewModel(stato.State);
        var primaFinestra = new Window { Width = 1920, Height = 1080, Content = new ReportView { DataContext = prima } };
        try
        {
            LayoutHeadlessSessione.Mostra(primaFinestra);
            AttendiMatrice(prima, primaFinestra);
            var gamma = prima.FiltriMatrice.Single(f => f.Dimensione.Id == DimensioniMatrice.Gamma);
            gamma.Filtro.Opzioni.Single(o => o.Valore == "LCV").Selezionato = true;
            LayoutHeadlessSessione.Assesta(primaFinestra);
        }
        finally { primaFinestra.Hide(); }

        var vm = new ReportViewModel(stato.State);
        var vista = new ReportView { DataContext = vm };
        var finestra = new Window { Width = 1920, Height = 1080, Content = vista };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            AttendiMatrice(vm, finestra);
            var riepilogo = PerId(vista, "Report_FiltriAttivi_Text").OfType<TextBlock>().Single();
            Assert.True(riepilogo.IsVisible);
            Assert.Contains("LCV", riepilogo.Text);
            Assert.DoesNotContain(vm.CampiXMatrice.Concat(vm.CampiYMatrice), c => c.Dimensione?.Id == DimensioniMatrice.Gamma);
            var template = Assert.Single(vm.FiltroTemplate.Opzioni.Take(1));
            template.Selezionato = true;
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.Contains(template.Etichetta, riepilogo.Text);
            var lingua = LocalizationService.Instance.Lingua;
            try
            {
                LocalizationService.Instance.Lingua = "en";
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Contains("Active filters", riepilogo.Text);
            }
            finally { LocalizationService.Instance.Lingua = lingua; }
        }
        finally { finestra.Hide(); }
    });

    [Fact]
    public void ReportTempoHeadless_TotaleMedioEBase_AggiornanoLeCelleESelettoreBase() => LayoutHeadlessSessione.Esegui(() =>
    {
        using var stato = CreaStato();
        var vm = new ReportViewModel(stato.State);
        var vista = new ReportView { DataContext = vm };
        var finestra = new Window { Width = 1920, Height = 1080, Content = vista };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            AttendiMatrice(vm, finestra);
            Assert.DoesNotContain(vm.FiltriMatrice, f => f.Dimensione.Id == DimensioniMatrice.Componente
                || f.Dimensione.Id == DimensioniMatrice.Sottoveicolo
                || f.Dimensione.Id == DimensioniMatrice.Template);
            Assert.Empty(vista.GetVisualDescendants().OfType<GraficoCard>());
            Assert.False(PerId(vista, "Report_BaseTempo_ComboBox").Single().IsVisible);

            vm.IndiceModalitaMatrice = 3; // Tempo totale, base "Tutte le esecuzioni"
            AttendiTesto(vista, finestra, CellaDiagnosticaBcm, "30 min · 2/2");
            Assert.True(PerId(vista, "Report_BaseTempo_ComboBox").Single().IsVisible);

            vm.IndiceBaseTempo = 1; // Ultimo stato: solo la revisione piu' recente (10 minuti)
            AttendiTesto(vista, finestra, CellaDiagnosticaBcm, "10 min · 1/1");

            vm.IndiceModalitaMatrice = 4; // Tempo medio sulla stessa base
            vm.IndiceBaseTempo = 0;
            AttendiTesto(vista, finestra, CellaDiagnosticaBcm, "15 min · 2/2");

            vm.IndiceModalitaMatrice = 0; // Stato %: il selettore di base sparisce
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.False(PerId(vista, "Report_BaseTempo_ComboBox").Single().IsVisible);
        }
        finally
        {
            finestra.Hide();
        }
    });

    [Fact]
    public void ReportTempoHeadless_FiltroOperazione_RiduceLeOperazioniSenzaCambiareGliAssi() => LayoutHeadlessSessione.Esegui(() =>
    {
        using var stato = CreaStato();
        var vm = new ReportViewModel(stato.State);
        var vista = new ReportView { DataContext = vm };
        var finestra = new Window { Width = 1920, Height = 1080, Content = vista };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            AttendiMatrice(vm, finestra);
            vm.IndiceModalitaMatrice = 3;
            AttendiTesto(vista, finestra, CellaDiagnosticaBcm, "30 min · 2/2");
            var campiX = vm.CampiXMatrice.Count;
            var campiY = vm.CampiYMatrice.Count;

            vm.OperatoreFiltroTempoOperazione = 0; // maggiore di
            vm.TestoFiltroTempoOperazione = "15";
            AttendiTesto(vista, finestra, CellaDiagnosticaBcm, "20 min · 1/1");
            var riepilogo = PerId(vista, "Report_FiltriAttivi_Text").OfType<TextBlock>().Single();
            Assert.True(riepilogo.IsVisible);
            Assert.Contains("15", riepilogo.Text);

            Assert.Equal(campiX, vm.CampiXMatrice.Count);
            Assert.Equal(campiY, vm.CampiYMatrice.Count);
        }
        finally
        {
            finestra.Hide();
        }
    });

    private static void AttendiMatrice(ReportViewModel vm, Window finestra)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (vm.RigheMatrice.Count == 0 && DateTime.UtcNow < limite)
        {
            LayoutHeadlessSessione.Assesta(finestra);
            Thread.Sleep(50);
        }
        LayoutHeadlessSessione.Assesta(finestra);
        Assert.NotEmpty(vm.RigheMatrice);
    }

    private static void AttendiTesto(Visual vista, Window finestra, string automationId, string atteso)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        string? ultimo = null;
        while (DateTime.UtcNow < limite)
        {
            LayoutHeadlessSessione.Assesta(finestra);
            ultimo = PerId(vista, automationId).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault();
            if (ultimo == atteso) return;
            Thread.Sleep(50);
        }
        Assert.Equal(atteso, ultimo);
    }

    private static IEnumerable<Control> PerId(Visual radice, string id) =>
        radice.GetVisualDescendants().OfType<Control>().Where(c => AutomationProperties.GetAutomationId(c) == id);

    private static StatoTempoReport CreaStato()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportTempo_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily A", GammaNome = "LCV" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["Daily A"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Lettura guasto", Categorie = ["Diagnostica"] } });

        var tc = new TestCase { Id = "T-TEMPO", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"] };
        tc.Esecuzioni.Add(RevisioneTempo(new DateTime(2026, 1, 10), 20));
        tc.Esecuzioni.Add(RevisioneTempo(new DateTime(2026, 1, 11), 10));
        state.TestCases.Save(tc, isNew: true);
        return new StatoTempoReport(state, dir);
    }

    private static Esecuzione RevisioneTempo(DateTime data, int minuti) => new()
    {
        Data = data,
        Operatore = "Op",
        Esito = Esito.Conforme,
        Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]), DurataMinuti = minuti }],
    };

    private sealed class StatoTempoReport(AppState state, string dir) : IDisposable
    {
        public AppState State { get; } = state;

        public void Dispose()
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
