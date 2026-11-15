using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using System.Text.Json;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class AuditColonnePersonalizzabiliHeadlessTests
{
    private const int Requisiti = 400;
    private const int Componente = 6;
    private const string ChiaveLayout = "Audit.Matrice.Layout";

    [Fact]
    public void ColonnaNascosta_NonRenderizzata_PreferenzaRiapplicata_RipristinoPredefinito()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "TestLoom_AuditColonne_" + Guid.NewGuid().ToString("N"));
            using var state = TestStateFactory.Create();
            Directory.CreateDirectory(dir);
            try
            {
                state.InizializzaCartella(dir);
                state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Veicolo-A" } });
                state.Master.Componente.Save(Enumerable.Range(1, Componente).Select(i => new Componente { Nome = $"Componente{i:00}", VeicoliAssociati = { "Veicolo-A" } }).ToList());
                state.Master.Requisiti.Save(Enumerable.Range(1, Requisiti).Select(i => new Requisito { Id = $"ST{i:0000}", Testo = $"Req {i}" }).ToList());
                ViewStateService.Configura(state);
                ViewStateService.ScriviPerOperatore(ChiaveLayout, null);

                var vm = new AuditViewModel(state) { SottoveicoloSelezionato = "Veicolo-A" };
                var vista = new AuditView { DataContext = vm };
                var finestra = new Window { Width = 1280, Height = 672, Content = vista };
                try
                {
                    LayoutHeadlessSessione.Mostra(finestra);
                    LayoutHeadlessSessione.Assesta(finestra);
                    Assert.Equal(Componente + 1, Intestazioni(vista).Count()); // Requisito + 6 componente
                    Assert.Contains(vista.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetAutomationId(b) == "Audit_Colonne_Button");

                    // Nasconde componente03 dal menu Colonne.
                    Click(Voce(vista, vm, "Componente03"), false);
                    LayoutHeadlessSessione.Assesta(finestra);
                    Assert.DoesNotContain("AUDIT:COLONNA:componente:componente03", Intestazioni(vista));
                    Assert.Equal(Componente, Intestazioni(vista).Count()); // Requisito + 5 componente
                    Assert.Empty(CelleEcu(vista, "ST0001", "AUDIT:CELLA:componente:componente03"));
                    Assert.Single(CelleEcu(vista, "ST0001", "AUDIT:CELLA:componente:componente02"));
                    // Virtualizzazione e altezza compatta invariate.
                    Assert.False(TestoVisibile(vista, "ST0400"));
                    Assert.Equal(26, Riga(vista, "ST0001").Height, 1);
                    using (var json = JsonDocument.Parse(ViewStateService.LeggiPerOperatore(ChiaveLayout)!))
                        Assert.Equal("Componente03", json.RootElement.GetProperty("EcuNascoste")[0].GetString());

                    // L'ultima colonna visibile non si nasconde.
                    foreach (var e in new[] { "Componente01", "Componente02", "Componente04", "Componente05" }) Click(Voce(vista, vm, e), false);
                    Click(Voce(vista, vm, "Componente06"), false);
                    LayoutHeadlessSessione.Assesta(finestra);
                    Assert.Equal(new[] { "AUDIT:COLONNA:REQUISITO", "AUDIT:COLONNA:componente:componente06" }, Intestazioni(vista));
                    Assert.True(Voce(vista, vm, "Componente06").IsChecked);

                    // Preferenza riapplicata a una vista nuova (riavvio / riapertura pagina).
                    finestra.Hide();
                    var vista2 = new AuditView { DataContext = vm };
                    var finestra2 = new Window { Width = 1280, Height = 672, Content = vista2 };
                    try
                    {
                        LayoutHeadlessSessione.Mostra(finestra2);
                        LayoutHeadlessSessione.Assesta(finestra2);
                        Assert.Equal(new[] { "AUDIT:COLONNA:REQUISITO", "AUDIT:COLONNA:componente:componente06" }, Intestazioni(vista2));

                        // Ripristina predefinite: tutte le colonne tornano e la preferenza e' azzerata.
                        Click(vista2.CostruisciVociMenuColonne(vm).OfType<MenuItem>().Single(m =>
                            AutomationProperties.GetAutomationId(m) == "Audit_Colonne_Ripristina_MenuItem"), null);
                        LayoutHeadlessSessione.Assesta(finestra2);
                        Assert.Equal(Componente + 1, Intestazioni(vista2).Count());
                        Assert.Equal("Componente01", Intestazioni(vista2).ElementAt(1).Split(':').Last());
                        using var json = JsonDocument.Parse(ViewStateService.LeggiPerOperatore(ChiaveLayout)!);
                        Assert.Equal(0, json.RootElement.GetProperty("EcuNascoste").GetArrayLength());
                    }
                    finally { finestra2.Hide(); }
                }
                finally { finestra.Hide(); }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });
    }

    private static MenuItem Voce(AuditView vista, AuditViewModel vm, string componente) => vista.CostruisciVociMenuColonne(vm).OfType<MenuItem>()
        .Single(m => AutomationProperties.GetAutomationId(m) == "Audit_Colonne_Voce_" + componente);

    /// <summary>Simula il clic sulla voce: il toggle della casella avviene prima del Click, come in Avalonia.</summary>
    private static void Click(MenuItem voce, bool? nuovoStato)
    {
        if (nuovoStato is { } s) voce.IsChecked = s;
        voce.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static IEnumerable<string> Intestazioni(AuditView vista) => vista.FindControl<StackPanel>("MatriceIntestazione")!
        .Children.OfType<Border>().Select(b => (string)b.Tag!);

    private static Control Riga(AuditView vista, string id) => vista.FindControl<ItemsControl>("MatriceRighe")!
        .GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == "Audit_Matrice_Riga_" + id);

    private static IEnumerable<Control> CelleEcu(AuditView vista, string id, string tag) =>
        Riga(vista, id).GetVisualDescendants().OfType<Control>().Where(c => Equals(c.Tag, tag));

    private static bool TestoVisibile(Visual vista, string testo) =>
        vista.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == testo);
}
