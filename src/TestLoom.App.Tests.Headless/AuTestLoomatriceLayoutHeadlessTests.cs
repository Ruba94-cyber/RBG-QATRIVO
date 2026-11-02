using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using static TestLoom.App.Tests.LayoutHeadlessMisure;

namespace TestLoom.App.Tests;

/// <summary>Matrice Audit in headless (Category=LayoutHeadless). La misura riporta il numero di controlli creati nell'area della
/// matrice e il tempo di RicaricaMatrice (cambio di sottoveicolo: ricalcolo del VM e ricostruzione della vista) a 1700 requisiti x
/// 20 componente in una finestra di 1280x672; stampa una riga "AUDIT_MISURA" per il rapporto, senza soglie sul tempo (dipende dalla
/// macchina). I test funzionali verificano la virtualizzazione per righe: lo scorrimento raggiunge l'ultima riga, l'intestazione componente
/// resta sincronizzata con lo scorrimento orizzontale e i filtri stanno affiancati in modo compatto.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class AuditMatriceLayoutHeadlessTests
{
    private const int Requisiti = 1700;
    private const int Componente = 20;

    [Fact]
    public void Matrice1700RequisitiPer20Ecu_ControlliETempoDiRicaricaMatrice() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var stato = CreaStato(Requisiti, Componente);
            var vm = new AuditViewModel(stato.State) { SottoveicoloSelezionato = "Veicolo-A" };
            var vista = new AuditView { DataContext = vm };
            var finestra = new Window { Width = 1280, Height = 672, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                LayoutHeadlessSessione.Assesta(finestra);

                var misure = new List<double>();
                for (var i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    vm.SottoveicoloSelezionato = i % 2 == 0 ? "Veicolo-B" : "Veicolo-A";
                    LayoutHeadlessSessione.Assesta(finestra);
                    sw.Stop();
                    misure.Add(sw.Elapsed.TotalMilliseconds);
                }

                // Solo il VM, senza vista: la parte di ricalcolo che la modifica di vista non cambia.
                var vmSolo = new AuditViewModel(stato.State) { SottoveicoloSelezionato = "Veicolo-A" };
                var misureVm = new List<double>();
                for (var i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    vmSolo.SottoveicoloSelezionato = i % 2 == 0 ? "Veicolo-B" : "Veicolo-A";
                    sw.Stop();
                    misureVm.Add(sw.Elapsed.TotalMilliseconds);
                }

                var area = vista.FindControl<Control>("MatriceHost")!;
                var controlli = area.GetVisualDescendants().Count();
                var ordinate = misure.OrderBy(m => m).ToList();
                Console.WriteLine($"AUDIT_MISURA requisiti={vm.Righe.Count} componente={vm.EcuColonne.Count} controlli_matrice={controlli} " +
                    $"ricarica_ms={string.Join(",", misure.Select(m => m.ToString("0")))} mediana_ms={ordinate[ordinate.Count / 2]:0} " +
                    $"max_ms={ordinate[^1]:0} vm_solo_ms={string.Join(",", misureVm.Select(m => m.ToString("0")))}");

                Assert.Equal(Requisiti, vm.Righe.Count);
                Assert.Equal(Componente, vm.EcuColonne.Count);
                Assert.True(controlli > 0, "la matrice non ha creato controlli");
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void MatriceVirtualizzata_ScorrimentoRaggiungeLUltimaRigaEIntestazioneResta() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var stato = CreaStato(300, Componente);
            var vm = new AuditViewModel(stato.State) { SottoveicoloSelezionato = "Veicolo-A" };
            var vista = new AuditView { DataContext = vm };
            var finestra = MostraInSidebar(vista, 1280, 672);
            try
            {
                // Solo le righe a schermo sono costruite: la prima c'e', l'ultima no.
                Assert.True(TestoVisibile(vista, "ST0001"), "la prima riga non e' costruita");
                Assert.False(TestoVisibile(vista, "ST0300"), "tutte le righe sono costruite: la matrice non e' virtualizzata");

                // La matrice prende lo spazio residuo della pagina in modo compatto: non e' schiacciata e sta nella finestra.
                var scroll = vista.FindControl<ScrollViewer>("MatriceScroll")!;
                Assert.True(scroll.Bounds.Height >= 150, $"altezza della matrice {scroll.Bounds.Height:0} px (minimo 150: circa cinque righe)");
                AsserisciDentroFinestra(scroll, finestra, "area della matrice");

                // Lo scorrimento raggiunge l'ultima riga e rilascia quelle in cima.
                scroll.Offset = new Vector(0, scroll.Extent.Height);
                LayoutHeadlessSessione.Assesta(finestra);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(TestoVisibile(vista, "ST0300"), "l'ultima riga non e' raggiungibile con lo scorrimento");
                Assert.False(TestoVisibile(vista, "ST0001"), "le righe in cima restano costruite dopo lo scorrimento");

                // L'intestazione componente resta e segue lo scorrimento orizzontale del corpo.
                var intestazione = vista.FindControl<StackPanel>("MatriceIntestazione")!;
                Assert.True(scroll.Extent.Width > scroll.Viewport.Width, "la matrice con 20 componente deve scorrere in orizzontale");
                scroll.Offset = new Vector(120, scroll.Offset.Y);
                LayoutHeadlessSessione.Assesta(finestra);
                LayoutHeadlessSessione.Assesta(finestra);
                var traslazione = Assert.IsType<TranslateTransform>(intestazione.RenderTransform);
                Assert.Equal(-120, traslazione.X, 1);
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void MatriceVirtualizzata_FiltriAffiancatiInCompatto() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var stato = CreaStato(40, 4);
            var vm = new AuditViewModel(stato.State) { SottoveicoloSelezionato = "Veicolo-A" };
            var vista = new AuditView { DataContext = vm };
            var finestra = MostraInSidebar(vista, 1280, 672);
            try
            {
                var tipo = Trova<Button>(vista, "Audit_FiltroTipoRequisito_Button");
                var sottoveicolo = Trova<ComboBox>(vista, "Audit_Sottoveicolo_ComboBox");
                var yTipo = Rettangolo(tipo, finestra).Y;
                var ySottoveicolo = Rettangolo(sottoveicolo, finestra).Y;
                Assert.True(Math.Abs(yTipo - ySottoveicolo) < 40, $"filtri su righe diverse: {yTipo:0} e {ySottoveicolo:0}");
                Assert.True(Rettangolo(sottoveicolo, finestra).X > Rettangolo(tipo, finestra).X,
                    "il filtro Veicolo non e' a destra del filtro Tipo");
            }
            finally
            {
                finestra.Hide();
            }
        });

    /// <summary>True se un blocco di testo con quel contenuto e' costruito e visibile nella vista.</summary>
    private static bool TestoVisibile(Visual vista, string testo) =>
        vista.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == testo);

    private sealed class StatoAudit : IDisposable
    {
        public StatoAudit(AppState state, string dir)
        {
            State = state;
            Dir = dir;
        }

        public AppState State { get; }
        public string Dir { get; }

        public void Dispose()
        {
            State.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    /// <summary>Workspace di prova con due sottoveicoli, tutti i componenti associate a entrambi e n requisiti.</summary>
    private static StatoAudit CreaStato(int requisiti, int ecuN)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_AuditMatrice_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        var nomiEcu = Enumerable.Range(1, ecuN).Select(i => $"Componente{i:00}").ToList();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Veicolo-A" }, new() { Nome = "Veicolo-B" } });
        state.Master.Componente.Save(nomiEcu.Select(n => new Componente { Nome = n, VeicoliAssociati = { "Veicolo-A", "Veicolo-B" } }).ToList());
        state.Master.Requisiti.Save(Enumerable.Range(1, requisiti)
            .Select(i => new Requisito { Id = $"ST{i:0000}", Testo = $"Requisito di prova {i}" }).ToList());
        return new StatoAudit(state, dir);
    }
}
