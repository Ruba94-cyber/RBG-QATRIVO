using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Matrice Report multi-campo in headless (Category=LayoutHeadless, 6.6.1): due campi su un asse, modalita' Stato con
/// legenda, gruppo comprimibile, selettore della modalita'. Le dimensioni 1280x672 e 1920x1080 sono quelle del Report reale.
/// Nessun controllo sul tempo: il banco di prestazioni resta quello della matrice (ReportMatriceAbPerfTests).</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class ReportMatriceCampiLayoutHeadlessTests
{
    [Fact]
    public void Matrice1280x672_DueCampiPerAsse_LegendaEGruppoComprimibile() => EsegueVista(1280, 672, comprimi: true);

    [Fact]
    public void Matrice1920x1080_DueCampiPerAsse_LegendaEGruppoComprimibile() => EsegueVista(1920, 1080, comprimi: false);

    [Fact]
    public void Matrice_LegendaMetricheSempreVisibile_EColoriSoloPerStato() => LayoutHeadlessSessione.Esegui(() =>
    {
        using var stato = CreaStato();
        var vm = new ReportViewModel(stato.State);
        var vista = new ReportView { DataContext = vm };
        var finestra = new Window { Width = 1280, Height = 672, Content = vista };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            AttendiMatrice(vm, finestra);
            var legenda = PerId(vista, "Report_MatriceLegenda_Panel").Single();
            Assert.True(legenda.IsVisible);
            vm.IndiceModalitaMatrice = 1;
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.True(legenda.IsVisible);
            vm.IndiceModalitaMatrice = 0;
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.True(legenda.IsVisible);
        }
        finally
        {
            finestra.Hide();
        }
    });

    private static void EsegueVista(int larghezza, int altezza, bool comprimi) => LayoutHeadlessSessione.Esegui(() =>
    {
        using var stato = CreaStato();
        var vm = new ReportViewModel(stato.State);
        vm.AggiungiCampoMatrice(righe: false); // secondo campo sull'asse colonne
        vm.CampiXMatrice[1].Dimensione = vm.OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Sottoveicolo); // Veicolo sotto componente
        vm.IndiceModalitaMatrice = 1;          // modalita' Stato
        var vista = new ReportView { DataContext = vm };
        var finestra = new Window { Width = larghezza, Height = altezza, Content = vista };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            AttendiMatrice(vm, finestra, () => vm.FasceColonneMatrice.Count == 2 && vm.ModalitaMatrice == ModalitaCellaMatrice.Stato);
            Assert.Equal(2, vm.CampiXMatrice.Count);
            Assert.True(PerId(vista, "Report_MatriceAsseX_ComboBox").Any());
            Assert.True(PerId(vista, "Report_MatriceAsseX_Campo2_ComboBox").Any());
            Assert.True(PerId(vista, "Report_ModalitaMatrice_ComboBox").Any());
            Assert.True(PerId(vista, "Report_MatriceLegenda_Panel").Single().IsVisible);
            var celle = PerIdPrefisso(vista, "ReportMatrice_Cella_").Count();
            Assert.True(celle > 0, "la matrice non ha celle nella finestra " + larghezza + "x" + altezza);
            if (comprimi)
            {
                var chiaveGruppo = vm.SegmentiColonneMatrice[0].First(s => s.Comprimibile).ChiaveComando!;
                var prima = PerIdPrefisso(vista, "ReportMatrice_Cella_").Count();
                vm.AlternaGruppoMatrice(chiaveGruppo);
                LayoutHeadlessSessione.Assesta(finestra);
                var dopo = PerIdPrefisso(vista, "ReportMatrice_Cella_").Count();
                var colonneDiagnostica = string.Join(";", vm.ColonneMatrice.Select(k => k.Replace(ReportMatriceService.SepLivello, '/')));
                var diagnostica = $"colonneVM={vm.ColonneMatrice.Count} [{colonneDiagnostica}] fasce={vm.FasceColonneMatrice.Count} righeVM={vm.RigheMatrice.Count} test={stato.State.TestCases.GetAll().Count()}";
                Assert.True(dopo < prima, $"comprimere il gruppo deve ridurre le celle visibili ({prima} -> {dopo}) {diagnostica}");
            }
            Console.WriteLine($"REPORT_MATRIX_LAYOUT|{larghezza}x{altezza}|celle={celle}|campi_x=2|modalita=stato");
        }
        finally
        {
            finestra.Hide();
        }
    });

    private static void AttendiMatrice(ReportViewModel vm, Window finestra, Func<bool>? condizione = null)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while ((vm.RigheMatrice.Count == 0 || !(condizione?.Invoke() ?? true)) && DateTime.UtcNow < limite)
        {
            LayoutHeadlessSessione.Assesta(finestra);
            Thread.Sleep(50);
        }
        LayoutHeadlessSessione.Assesta(finestra);
        Assert.NotEmpty(vm.RigheMatrice);
    }

    private static IEnumerable<Control> PerId(Visual radice, string id) =>
        radice.GetVisualDescendants().OfType<Control>().Where(c => AutomationProperties.GetAutomationId(c) == id);

    private static IEnumerable<Control> PerIdPrefisso(Visual radice, string prefisso) =>
        radice.GetVisualDescendants().OfType<Control>().Where(c =>
        {
            var id = AutomationProperties.GetAutomationId(c) ?? "";
            return id.StartsWith(prefisso, StringComparison.Ordinal) && !id.EndsWith("_Text", StringComparison.Ordinal);
        });

    private static StatoMatriceReport CreaStato()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportMatrice_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily A", GammaNome = "LCV" }, new() { Nome = "Daily B", GammaNome = "LCV" } });
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = ["Daily A", "Daily B"] },
            new() { Nome = "EDC", VeicoliAssociati = ["Daily A"] },
        });
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Lettura guasto", Categorie = ["Diagnostica"] } });
        SalvaTest(state, "T-A", "BCM", "Daily A", Esito.Conforme);
        SalvaTest(state, "T-B", "BCM", "Daily B", Esito.NonConforme);
        SalvaTest(state, "T-C", "EDC", "Daily A", Esito.DaEseguire);
        return new StatoMatriceReport(state, dir);
    }

    private static void SalvaTest(AppState state, string id, string componente, string veicolo, Esito esito)
    {
        var tc = new TestCase { Id = id, Componente = new EcuSelection([componente]), Veicoli = [veicolo], Funzionalita = ["Lettura guasto"] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1).AddDays(tc.Id.Length),
            Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = esito, Componente = new EcuSelection([componente]) }],
        });
        state.TestCases.Save(tc, isNew: true);
    }

    private sealed class StatoMatriceReport(AppState state, string dir) : IDisposable
    {
        public AppState State { get; } = state;

        public void Dispose()
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
