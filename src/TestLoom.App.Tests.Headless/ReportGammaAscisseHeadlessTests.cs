using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>
/// Segnalazione utente (2026-10): nel Report, gamma sulle ascisse, tolte tutte le voci dalle ascisse e gamma rimessa: la gamma scelta
/// "resta visualizzata" anche cambiando filtro. Il test monta la pagina vera e controlla ogni testo a schermo: una gamma non
/// selezionata non deve comparire in nessuna intestazione, pulsante di filtro o riepilogo.
/// </summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class ReportGammaAscisseHeadlessTests
{
    private const string Alfa = "GAMMA-ALFA", Beta = "GAMMA-BETA";

    private static (AppState State, string Dir) CreaStato()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportGammaAscisse_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        state.Master.Gamma.Save(new List<Gamma> { new() { Nome = Alfa }, new() { Nome = Beta } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily A", GammaNome = Alfa }, new() { Nome = "Daily B", GammaNome = Beta } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["Daily A", "Daily B"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Lettura guasto", Categorie = ["Diagnostica"] } });
        foreach (var (id, veicolo) in new[] { ("T-A", "Daily A"), ("T-B", "Daily B") })
        {
            var tc = new TestCase { Id = id, Componente = new EcuSelection(["BCM"]), Veicoli = [veicolo], Funzionalita = ["Lettura guasto"] };
            tc.Esecuzioni.Add(new Esecuzione
            {
                Data = new DateTime(2026, 1, 10), Operatore = "Op", Esito = Esito.Conforme,
                Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]) }]
            });
            state.TestCases.Save(tc, isNew: true);
        }
        return (state, dir);
    }

    private static List<string> TestiAVideo(Control radice)
    {
        var testi = radice.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t.Text != null).Select(t => t.Text!).ToList();
        testi.AddRange(radice.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible && b.Content is string).Select(b => (string)b.Content!));
        return testi;
    }

    private static void Assesta(ReportViewModel vm, Window finestra)
    {
        LayoutHeadlessSessione.AttendiComando(vm.AttendiAggiornamentoAsync(), TimeSpan.FromSeconds(30));
        LayoutHeadlessSessione.Assesta(finestra);
    }

    private static void SoloGamma(ReportViewModel vm, string gamma)
    {
        var filtro = vm.FiltriMatrice.Single(f => f.Dimensione.Id == DimensioniMatrice.Gamma).Filtro;
        foreach (var o in filtro.Opzioni) o.Selezionato = o.Valore == gamma;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GammaSulleAscisse_TolteLeVociERimessa_ASchermoNonResta_LaGammaVecchia(bool inBackground) => LayoutHeadlessSessione.Esegui(() =>
    {
        var (state, dir) = CreaStato();
        try
        {
            var vm = new ReportViewModel(state, aggiornaInBackground: inBackground);
            var vista = new ReportView { DataContext = vm };
            var finestra = new Window { Width = 1920, Height = 1080, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                Assesta(vm, finestra);

                // Gamma sulle ascisse, componente sulle righe.
                while (vm.CampiXMatrice.Count > 1) vm.RimuoviCampoMatrice(vm.CampiXMatrice[^1]);
                vm.CampiXMatrice[0].Dimensione = vm.OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Esito);
                while (vm.CampiYMatrice.Count == 0) vm.AggiungiCampoMatrice(righe: true);
                vm.CampiYMatrice[0].Dimensione = vm.OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Componente);
                vm.CampiXMatrice[0].Dimensione = vm.OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Gamma);
                SoloGamma(vm, Alfa);
                Assesta(vm, finestra);
                var fase1 = TestiAVideo(vista);
                Assert.Contains(fase1, t => t.Contains(Alfa));
                Assert.DoesNotContain(fase1, t => t.Contains(Beta));

                // Tolte tutte le voci dalle ascisse.
                while (vm.CampiXMatrice.Count > 0) vm.RimuoviCampoMatrice(vm.CampiXMatrice[0]);
                Assesta(vm, finestra);
                Assert.DoesNotContain(TestiAVideo(vista), t => t.Contains(Beta));

                // Gamma rimessa sulle ascisse.
                vm.AggiungiCampoMatrice(righe: false);
                vm.CampiXMatrice[0].Dimensione = vm.OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Gamma);
                Assesta(vm, finestra);
                var fase3 = TestiAVideo(vista);
                Assert.Contains(fase3, t => t.Contains(Alfa));
                Assert.DoesNotContain(fase3, t => t.Contains(Beta));

                // Cambio di gamma: a schermo deve comparire solo la nuova.
                SoloGamma(vm, Beta);
                Assesta(vm, finestra);
                var fase4 = TestiAVideo(vista);
                Assert.Contains(fase4, t => t.Contains(Beta));
                Assert.DoesNotContain(fase4, t => t.Contains(Alfa));
            }
            finally { finestra.Hide(); }
        }
        finally { state.Dispose(); try { Directory.Delete(dir, recursive: true); } catch { } }
    });

    /// <summary>Configurazione del config.json dell'utente: ascisse con gamma e sottoveicolo, nessun campo sulle righe. L'ultimo campo della
    /// matrice non si puo' togliere: la pressione ripetuta di "✕" non deve lasciare la gamma vecchia a schermo.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AscisseConGammaESottoveicolo_RigheVuote_TogliereTutteLeVociERimetterle(bool inBackground) => LayoutHeadlessSessione.Esegui(() =>
    {
        var (state, dir) = CreaStato();
        try
        {
            var vm = new ReportViewModel(state, aggiornaInBackground: inBackground);
            var vista = new ReportView { DataContext = vm };
            var finestra = new Window { Width = 1920, Height = 1080, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                Assesta(vm, finestra);
                DimensioneMatriceOpzione Op(string id) => vm.OpzioniDimensioniMatrice.First(d => d.Id == id);

                while (vm.CampiYMatrice.Count > 0) vm.RimuoviCampoMatrice(vm.CampiYMatrice[0]);
                while (vm.CampiXMatrice.Count > 2) vm.RimuoviCampoMatrice(vm.CampiXMatrice[^1]);
                while (vm.CampiXMatrice.Count < 2) vm.AggiungiCampoMatrice(righe: false);
                vm.CampiXMatrice[0].Dimensione = Op(DimensioniMatrice.Esito);
                vm.CampiXMatrice[1].Dimensione = Op(DimensioniMatrice.Template);
                vm.CampiXMatrice[0].Dimensione = Op(DimensioniMatrice.Gamma);
                vm.CampiXMatrice[1].Dimensione = Op(DimensioniMatrice.Sottoveicolo);
                SoloGamma(vm, Alfa);
                Assesta(vm, finestra);
                Assert.DoesNotContain(TestiAVideo(vista), t => t.Contains(Beta));

                // "Toglie tutte le voci": l'ultima non si puo' rimuovere, le altre si.
                for (var i = 0; i < 4; i++) { if (vm.CampiXMatrice.Count > 0) vm.RimuoviCampoMatrice(vm.CampiXMatrice[0]); }
                Assesta(vm, finestra);
                Assert.Single(vm.CampiXMatrice);
                Assert.DoesNotContain(TestiAVideo(vista), t => t.Contains(Beta));

                // Rimette gamma sulle ascisse e cambia filtro.
                vm.CampiXMatrice[0].Dimensione = Op(DimensioniMatrice.Gamma);
                Assesta(vm, finestra);
                var dopoRimessa = TestiAVideo(vista);
                Assert.Contains(dopoRimessa, t => t.Contains(Alfa));
                Assert.DoesNotContain(dopoRimessa, t => t.Contains(Beta));

                SoloGamma(vm, Beta);
                Assesta(vm, finestra);
                var dopoCambio = TestiAVideo(vista);
                Assert.Contains(dopoCambio, t => t.Contains(Beta));
                Assert.DoesNotContain(dopoCambio, t => t.Contains(Alfa));
            }
            finally { finestra.Hide(); }
        }
        finally { state.Dispose(); try { Directory.Delete(dir, recursive: true); } catch { } }
    });
}
