using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Pager delle segnalazioni da classificare nella pagina Anomalie a 1280x672 (Category=LayoutHeadless). L'intestazione
/// conta l'intero elenco (TotaleSegnalazioni), non la sola pagina. La card resta nascosta nella pagina per scelta di prodotto
/// (docs/TestLoom_UI_UX_EXECUTION_CHECKLIST.md): il test la rende visibile solo per misurare il layout che avrebbe.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class SegnalazioniPagerHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SegPager_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState Stato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Gamma.Save([new Gamma { Nome = "GAMMA-A", ModificatoDa = "Seed" }, new Gamma { Nome = "GAMMA-B", ModificatoDa = "Seed" }]);
        state.Master.Veicoli.Save([
            new Veicolo { Nome = "VEI-1", GammaNome = "GAMMA-A", ModificatoDa = "Seed" },
            new Veicolo { Nome = "VEI-2", GammaNome = "GAMMA-B", ModificatoDa = "Seed" },
            new Veicolo { Nome = "VEI-3", GammaNome = "GAMMA-A", ModificatoDa = "Seed" },
        ]);
        state.Master.Componente.Save([new Componente { Nome = "Componente-0", ModificatoDa = "Seed" }, new Componente { Nome = "Componente-1", ModificatoDa = "Seed" },
            new Componente { Nome = "Componente-2", ModificatoDa = "Seed" }, new Componente { Nome = "Componente-3", ModificatoDa = "Seed" }]);
        state.Master.Operatori.Save([TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master SegPager", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Seed"
        })]);
        state.RicaricaTutto();
        Assert.True(state.ImpostaOperatore("Master SegPager", "1234"));
        return state;
    }

    /// <summary>Test con due esecuzioni da tre condizioni (stessa fixture del test di background della pagina Anomalie).</summary>
    private static TestCase CreaTest(string id, int indice)
    {
        var esecuzioni = new List<Esecuzione>();
        for (var e = 0; e < 2; e++)
        {
            var condizioni = new List<Condizione>();
            for (var c = 0; c < 3; c++)
            {
                var numero = indice * 6 + e * 3 + c;
                condizioni.Add(new Condizione
                {
                    Funzionalita = numero % 4 == 0 ? "" : $"FUNZ-{numero}",
                    Componente = $"Componente-{numero % 4}",
                    DescrizioneProblema = $"Problema {numero}",
                    Nota = $"Nota {numero}",
                });
            }
            esecuzioni.Add(new Esecuzione { Iterazione = e + 1, Matricola = $"Matricola-{id}-{e}", Condizioni = condizioni });
        }
        return new TestCase
        {
            Id = id, Componente = $"Componente-{indice % 4}", Veicolo = "VEI-1",
            Severita = indice % 2 == 0 ? SeveritaAnomalia.High : null,
            Esecuzioni = esecuzioni,
        };
    }

    /// <summary>Segnalazioni da classificare su 12 Test (alcune gia' classificate, escluse dall'elenco).</summary>
    private static void CreaSegnalazioni(AppState s, int quante)
    {
        var tests = Enumerable.Range(0, 12).Select(i => CreaTest($"TEST-{i:D2}", i)).ToList();
        foreach (var t in tests) s.TestCases.Save(t, isNew: true);
        for (var i = 0; i < quante; i++)
        {
            var test = tests[i % tests.Count];
            var condizioni = test.Esecuzioni.SelectMany(e => e.Condizioni).ToList();
            var condizioneUid = i % 7 == 0 ? Guid.NewGuid() : condizioni[i % condizioni.Count].Uid;
            s.Segnalazioni.Save(new SegnalazioneDaClassificare
            {
                Uid = Guid.NewGuid(),
                TestId = i % 19 == 0 ? "TEST-INESISTENTE" : test.Id,
                CondizioneUid = condizioneUid,
                Stato = i % 11 == 0 ? StatoSegnalazione.Classificata : StatoSegnalazione.DaClassificare,
                CreataIl = new DateTime(2026, 3, 1).AddMinutes(i),
                CreataDa = "Seed",
            });
        }
    }

    private static T Automazione<T>(Visual radice, string automationId) where T : Visual =>
        radice.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    [Fact]
    public async Task Segnalazioni_IntestazioneEPagerA1280x672()
    {
        const int quante = 230;
        // Le segnalazioni con i % 11 == 0 sono gia' classificate e restano fuori dall'elenco: 209 da classificare, tre pagine da 100.
        var totale = Enumerable.Range(0, quante).Count(i => i % 11 != 0);
        var (nc, finestra, vista) = LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = Stato();
            CreaSegnalazioni(stato, quante);
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            var nc = new NonConformitaViewModel(stato, _ => { });
            var vista = new NonConformitaView();
            LayoutHeadlessSessione.Mostra(finestra);
            vista.DataContext = nc;   // dopo la finestra: la pagina legge il modo di layout reale alla creazione
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            return (nc, finestra, vista);
        });
        try
        {
            // Il calcolo delle segnalazioni gira in background (app reale): si attende e si applica sul thread UI.
            await nc.AttendiAggiornamentoAsync();
            LayoutHeadlessSessione.Esegui(() => LayoutHeadlessSessione.Assesta(finestra));

            LayoutHeadlessSessione.Esegui(() =>
            {
                Assert.Equal(totale, nc.TotaleSegnalazioni);
                Assert.Equal(100, nc.Segnalazioni.Count);

                // Card nascosta per scelta di prodotto; la si mostra solo per misurare il layout.
                var card = vista.FindControl<Border>("CardSegnalazioni")!;
                Assert.False(card.IsVisible);
                card.IsVisible = true;
                LayoutHeadlessSessione.Assesta(finestra);

                var intestazione = Automazione<TextBlock>(vista, "Anomalie_Segnalazioni_Totale_Text");
                Assert.Contains(totale.ToString(), intestazione.Text);

                var testo = Automazione<TextBlock>(vista, "Anomalie_PaginazioneSegnalazioni_Text");
                var prima = Automazione<Button>(vista, "Anomalie_Segnalazioni_PrimaPagina_Button");
                var precedente = Automazione<Button>(vista, "Anomalie_Segnalazioni_PaginaPrecedente_Button");
                var successiva = Automazione<Button>(vista, "Anomalie_Segnalazioni_PaginaSuccessiva_Button");
                Assert.Contains("1 / 3", testo.Text);
                Assert.False(prima.IsEnabled);
                Assert.False(precedente.IsEnabled);
                Assert.True(successiva.IsEnabled);

                // Il comando del pulsante (stesso effetto del click); RaiseEvent(ClickEvent) non lo esegue.
                successiva.Command!.Execute(successiva.CommandParameter);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Contains("2 / 3", testo.Text);
                Assert.True(precedente.IsEnabled);
                Assert.Equal(100, nc.Segnalazioni.Count);

                // Lista virtualizzata: le righe materializzate restano poche rispetto alla pagina da 100.
                var lista = vista.FindControl<ListBox>("ListaSegnalazioni")!;
                var materializzate = lista.GetVisualDescendants().OfType<ListBoxItem>().Count();
                Assert.InRange(materializzate, 1, 99);

                // Regola del modo Compatto: pulsanti cliccabili alti almeno 32 px (misura dal layout reale).
                Assert.All(new[] { prima, precedente, successiva },
                    b => Assert.True(b.Bounds.Height >= 32 - 0.5, $"pulsante del pager alto {b.Bounds.Height:0} px, sotto 32 px"));

                // Pager sotto la lista e dentro la finestra a 672 px di altezza.
                var fondoLista = lista.TranslatePoint(new Point(0, lista.Bounds.Height), finestra)!.Value.Y;
                var inizioPager = successiva.TranslatePoint(new Point(0, 0), finestra)!.Value.Y;
                var fondoPager = successiva.TranslatePoint(new Point(0, successiva.Bounds.Height), finestra)!.Value.Y;
                Assert.True(inizioPager >= fondoLista - 0.5, $"pager sopra la lista: lista finisce a {fondoLista:0}, pager da {inizioPager:0}");
                Assert.True(fondoPager <= finestra.Bounds.Height + 0.5,
                    $"pager fuori dalla finestra: fondo {fondoPager:0} px su altezza {finestra.Bounds.Height:0} px");
            });
        }
        finally
        {
            LayoutHeadlessSessione.Esegui(() => finestra.Hide());
        }
    }
}
