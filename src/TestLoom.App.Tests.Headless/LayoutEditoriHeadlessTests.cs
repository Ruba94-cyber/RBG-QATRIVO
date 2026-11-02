using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using static TestLoom.App.Tests.LayoutHeadlessMisure;

namespace TestLoom.App.Tests;

/// <summary>Editor di Change Request, Segnalazione di rete e dettaglio anomalia a 1280x672 (Category=LayoutHeadless). Il contenuto
/// scorre dentro lo ScrollViewer; i pulsanti di conferma restano nel footer visibile, fuori dallo scroll e dentro la finestra; le
/// card a colonne (WrapPanel ColonneEditor) vanno su una colonna sola in modo Compatto. Il dettaglio anomalia e' una finestra: si
/// apre con la dimensione che AdattamentoDialogo le assegna su un'area di lavoro di 1280x672 (1232x624 DIP).</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutEditoriHeadlessTests
{
    [Fact]
    public void EditorCr1280x672_ContenutoScorreEAzioniInBarraEColonneSuUnaSola() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var fixture = CreaStato();
            var vm = new ChangeRequestEditorViewModel(fixture.State, null, () => { }, _ => { });
            vm.TemplateSelezionato = fixture.Template;
            var editor = new ChangeRequestEditorView { DataContext = vm };
            var finestra = MostraInSidebar(editor, 1280, 672);
            try
            {
                Assert.Equal(ModoLayout.Compatto, LayoutAdattivo.ModoCorrente);
                var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                    $"il contenuto della CR non scorre: {scroll.Extent.Height:0} <= {scroll.Viewport.Height:0}");

                // I pulsanti della barra in alto restano sempre dentro la finestra.
                AsserisciDentroFinestra(Trova<Button>(editor, "Cr_TornaAllaLista_Button"), finestra, "Torna alla lista");

                AsserisciColonneSingole(editor, finestra, "CR");
            }
            finally
            {
                finestra.Hide();
            }
        });

    /// <summary>Sopra soglia (piu' di dieci Test candidati) il selettore dei Test e' la ricerca collegata al motore del ViewModel: il
    /// ComboBox con l'elenco intero resta nascosto. Il menu lo popola il motore (vedi ChangeRequestEditorView.RicercaTestCorrente).</summary>
    [Fact]
    public void EditorCr1280x672_SopraSogliaIlSelettoreDeiTestCercaNelMotoreDelViewModel() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var fixture = CreaStato();
            for (var i = 0; i < 12; i++)
            {
                fixture.State.TestCases.Save(new TestCase
                {
                    Id = $"TEST-{i:D3}", Componente = "BCM", DescrizioneBreve = $"Descrizione {i}", DataCreazione = new DateTime(2026, 1, 1).AddDays(i),
                }, isNew: true);
            }
            var vm = new ChangeRequestEditorViewModel(fixture.State, null, () => { }, _ => { });
            // I Test candidati si pubblicano sul thread UI: si fa passare il dispatcher (non si blocca il thread che lo serve).
            var scadenza = DateTime.UtcNow.AddSeconds(20);
            while (vm.TestDisponibili.Count < 12 && DateTime.UtcNow < scadenza)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Assert.Equal(12, vm.TestDisponibili.Count);
            vm.TemplateSelezionato = fixture.Template;
            Assert.NotNull(vm.PickerTestDisponibili);

            var editor = new ChangeRequestEditorView { DataContext = vm };
            var finestra = MostraInSidebar(editor, 1280, 672);
            try
            {
                var ricerca = Trova<AutoCompleteBox>(editor, "Cr_CollegaTestRicerca_AutoComplete");
                var combo = Trova<ComboBox>(editor, "Cr_CollegaTest_Combo");
                Assert.True(ricerca.IsEffectivelyVisible, "il selettore con ricerca deve essere visibile sopra soglia");
                Assert.False(combo.IsEffectivelyVisible, "l'elenco intero (ComboBox) resta nascosto sopra soglia");
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void EditorSegnalazione1280x672_PulsantiDiConfermaNelFooterFuoriDalloScroll() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var fixture = CreaStato();
            var vm = new SegnalazioneReteEditorViewModel(fixture.State, null, () => { });
            var editor = new SegnalazioneReteEditorView { DataContext = vm };
            var finestra = MostraInSidebar(editor, 1280, 672);
            try
            {
                Assert.Equal(ModoLayout.Compatto, LayoutAdattivo.ModoCorrente);
                var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                    $"il contenuto della Segnalazione non scorre: {scroll.Extent.Height:0} <= {scroll.Viewport.Height:0}");

                // Salva, Annulla ed Esporta stanno nel footer (riga 2), fuori dallo ScrollViewer, sempre visibili nella finestra.
                foreach (var id in new[] { "Segnalazione_Salva_Button", "Segnalazione_Annulla_Button", "Segnalazione_EsportaPacchetto_Button" })
                {
                    var pulsante = Trova<Button>(editor, id);
                    Assert.Null(pulsante.FindAncestorOfType<ScrollViewer>());
                    AsserisciDentroFinestra(pulsante, finestra, id);
                }

                AsserisciColonneSingole(editor, finestra, "Segnalazione");
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void DettaglioAnomalia1280x672_FooterVisibileEContenutoScorre() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var fixture = CreaStato();
            LayoutAdattivo.ReimpostaStato();
            LayoutAdattivo.Aggiorna(1280, 672);
            var nc = new NonConformita { Id = "NC-2026-0001", Descrizione = "Errore di prova", Owner = "Mario" };
            var vm = new AnomaliaDettaglioViewModel(fixture.State, nc, persistita: false);
            var finestra = new AnomaliaDettaglioWindow { DataContext = vm, Width = 1232, Height = 624 };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Contains("compatto", finestra.Classes);

                var scroll = finestra.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                    $"il dettaglio non scorre: {scroll.Extent.Height:0} <= {scroll.Viewport.Height:0}");

                var salva = Trova<Button>(finestra, "AnomaliaDettaglio_Salva_Button");
                Assert.Null(salva.FindAncestorOfType<ScrollViewer>());
                AsserisciDentroFinestra(salva, finestra, "Salva");
                AsserisciDentroFinestra(Trova<Button>(finestra, "AnomaliaDettaglio_Annulla_Button"), finestra, "Annulla");

                AsserisciColonneSingole(finestra, finestra, "Dettaglio anomalia");
            }
            finally
            {
                finestra.Hide();
            }
        });

    /// <summary>Ogni card a colonne con almeno due campi visibili va su una colonna sola in Compatto.</summary>
    private static void AsserisciColonneSingole(Visual radice, Window finestra, string nome)
    {
        var card = radice.GetVisualDescendants().OfType<WrapPanel>().Where(p => p.Classes.Contains("ColonneEditor")).ToList();
        Assert.NotEmpty(card);
        foreach (var pannello in card)
        {
            var campi = VisibiliDirettiDi(pannello);
            if (campi.Count < 2) continue;
            AsserisciUnaColonna(pannello, campi, $"{nome}: {pannello.Name ?? "card"}");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(AppState state, TestTemplate template, string dir)
        {
            State = state;
            Template = template;
            Dir = dir;
        }

        public AppState State { get; }
        public TestTemplate Template { get; }
        public string Dir { get; }

        public void Dispose()
        {
            State.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    /// <summary>Workspace con un operatore Master e un template CR con otto campi liberi, cosi' la CR ha un contenuto che
    /// supera l'altezza della finestra.</summary>
    private static Fixture CreaStato()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_LayoutEditori_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Editori", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        var campi = Enumerable.Range(1, 8).Select(i => new CampoTemplate { Tipo = TipoCampoTemplate.CampoLibero, Nome = $"Campo CR {i}" }).ToList();
        var template = new TestTemplate { Nome = "Change Request di prova", IsChangeRequest = true, CampiPianificazione = campi };
        state.Master.TestTemplate.Save(new List<TestTemplate> { template });
        return new Fixture(state, template, dir);
    }
}
