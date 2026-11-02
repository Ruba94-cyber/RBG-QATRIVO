using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>"Componente e funzionalità" del veicolo in headless (Category=LayoutHeadless): la finestra si apre ed elenca i componenti con la
/// casella di montaggio sullo stato del veicolo; il pulsante della pagina Veicoli è abilitato solo con un veicolo selezionato.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class AssociazioniVeicoloHeadlessTests
{
    [Fact]
    public void FinestraElencaLeEcuEMostraLoStatoDiMontaggio() => LayoutHeadlessSessione.Esegui(() =>
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            stato.Master.Componente.Save(new List<Componente>
            {
                new() { Nome = "Componente-A", VeicoliAssociati = new List<string> { "VEI-1" } },
                new() { Nome = "Componente-B", VeicoliAssociati = new List<string> { "VEI-2" } },
            });
            stato.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi", TutteLeEcu = true } });

            using var vm = new AssociazioniVeicoloViewModel(stato, "VEI-1");
            var finestra = new AssociazioniVeicoloWindow { DataContext = vm, Width = 900, Height = 660 };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                LayoutHeadlessSessione.Assesta(finestra);

                var lista = PerId(finestra, "Assoc_ElencoEcu_Lista").OfType<ListBox>().Single();
                Assert.Equal(2, lista.ItemCount);
                var caselle = PerId(finestra, "Assoc_EcuMontata_CheckBox").OfType<CheckBox>().ToList();
                Assert.Equal(2, caselle.Count);
                Assert.True(caselle[0].IsChecked == true);   // componente-A è montata su VEI-1
                Assert.True(caselle[1].IsChecked != true);   // componente-B no
                Assert.True(PerId(finestra, "Assoc_Modifica_Button").OfType<Button>().Single().IsVisible); // Master: può modificare
            }
            finally
            {
                finestra.Hide();
            }
        }
        finally
        {
            MasterHeadless.Pulisci(dir);
        }
    });

    [Fact]
    public void PaginaVeicoli_PulsanteAssociazioniAbilitatoSoloConVeicoloSelezionato() => LayoutHeadlessSessione.Esegui(() =>
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            stato.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "VEI-1" }, new() { Nome = "VEI-2" } });
            var vm = new VeicoliViewModel(stato);
            var vista = new VeicoliView { DataContext = vm };
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);

                var pulsante = PerId(vista, "Veic_Associazioni_Button").OfType<Button>().Single();
                Assert.False(pulsante.IsEnabled);

                vm.ElementoSelezionato = vm.ItemsAttivi.First(v => v.Nome == "VEI-1");
                LayoutHeadlessSessione.Assesta(finestra);

                Assert.True(pulsante.IsEnabled);
            }
            finally
            {
                finestra.Hide();
            }
        }
        finally
        {
            MasterHeadless.Pulisci(dir);
        }
    });

    private static IEnumerable<Control> PerId(Visual radice, string id) =>
        radice.GetVisualDescendants().OfType<Control>().Where(c => AutomationProperties.GetAutomationId(c) == id);
}
