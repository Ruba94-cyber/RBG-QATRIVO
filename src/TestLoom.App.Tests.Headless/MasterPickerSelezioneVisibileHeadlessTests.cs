using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class MasterPickerSelezioneVisibileHeadlessTests
{
    [Fact]
    public void FunzionalitaEcuPicker_SelezionaSoloVisibiliEDeselezionaSingolaSenzaPerdereNascoste() => LayoutHeadlessSessione.Esegui(() =>
    {
        var state = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var componente = Enumerable.Range(0, 13).Select(i => new Componente { Nome = $"Componente-{i:00}" }).ToList();
            state.Master.Componente.Save(componente);
            state.Master.Funzionalita.Save(new List<Funzionalita>
            {
                new() { Nome = "Legacy dinamica", TutteLeEcu = false, EcuAssociate = new() { "Componente-12" } }
            });
            using var vm = new FunzionalitaViewModel(state);
            LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
            var funzionalita = vm.ItemsAttivi.Single();
            var vista = new FunzionalitaView { DataContext = vm };
            var finestra = new Window { Width = 1500, Height = 850, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                LayoutHeadlessSessione.Assesta(finestra);
                var bottone = PerId<Button>(vista, "Funz_EcuAssociate_Button");
                bottone.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var flyout = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(bottone));
                var contenuto = Assert.IsAssignableFrom<Control>(flyout.Content);
                var ricerca = contenuto.GetVisualDescendants().OfType<TextBox>().Single();
                var seleziona = PerId<Button>(contenuto, "Funz_Componente_SelectVisible_Button");
                ricerca.Text = "Componente-0";
                LayoutHeadlessSessione.Assesta(finestra);
                seleziona.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                LayoutHeadlessSessione.Assesta(finestra);

                var checkboxSingola = contenuto.GetVisualDescendants().OfType<CheckBox>()
                    .Single(c => c.Content?.ToString() == "Componente-01");
                checkboxSingola.IsChecked = false;

                Assert.False(funzionalita.TutteLeEcu);
                Assert.DoesNotContain("Componente-01", funzionalita.EcuAssociate);
                Assert.Contains("Componente-00", funzionalita.EcuAssociate);
                Assert.Contains("Componente-12", funzionalita.EcuAssociate); // era fuori filtro durante le azioni di massa
                Assert.Equal(10, funzionalita.EcuAssociate.Count);
                Assert.True(checkboxSingola.IsChecked == false);
            }
            finally { finestra.Hide(); }
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    [Fact]
    public void FunzionalitaEcuPicker_AperturaESalvataggioMantengonoTutteLeEcuDinamiche() => LayoutHeadlessSessione.Esegui(() =>
    {
        var state = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            state.Master.Componente.Save(new List<Componente> { new() { Nome = "LEGACY-PICK-A" }, new() { Nome = "LEGACY-PICK-B" } });
            state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Legacy", TutteLeEcu = true } });
            using var vm = new FunzionalitaViewModel(state);
            LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
            var funzionalita = vm.ItemsAttivi.Single();
            var vista = new FunzionalitaView { DataContext = vm };
            var finestra = new Window { Width = 1500, Height = 850, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                LayoutHeadlessSessione.Assesta(finestra);
                var bottone = PerId<Button>(vista, "Funz_EcuAssociate_Button");
                bottone.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var flyout = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(bottone));
                var contenuto = Assert.IsAssignableFrom<Control>(flyout.Content);
                var caselle = contenuto.GetVisualDescendants().OfType<CheckBox>().ToList();

                Assert.Equal(new[] { "LEGACY-PICK-A", "LEGACY-PICK-B" }, caselle.Select(c => c.Content?.ToString()));
                Assert.All(caselle, c => Assert.True(c.IsChecked));
                Assert.True(funzionalita.TutteLeEcu);
                Assert.Empty(funzionalita.EcuAssociate);
                Assert.DoesNotContain(contenuto.GetVisualDescendants().OfType<CheckBox>(), c => c.Content?.ToString() == LocalizationService.Instance.Traduci("TutteLeEcu"));

                vm.SalvaCommand.Execute(null);
                state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "LEGACY-PICK-FUTURA" }).ToList());
                var salvata = state.Master.Funzionalita.Load().Single();
                Assert.True(salvata.TutteLeEcu);
                Assert.Empty(salvata.EcuAssociate);
                Assert.True(salvata.ValeSu("LEGACY-PICK-FUTURA", null));
            }
            finally { finestra.Hide(); }
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    [Fact]
    public void EcuVeicoliPicker_SelezionaVisibiliESingolaDeselezionePreservanoLeScelteNascoste() => LayoutHeadlessSessione.Esegui(() =>
    {
        var state = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var veicoli = Enumerable.Range(0, 13).Select(i => $"VEI-PICK-{i:00}").ToList();
            state.Master.Veicoli.Save(veicoli.Select(nome => new Veicolo { Nome = nome }).ToList());
            state.Master.Componente.Save(new List<Componente>
            {
                new() { Nome = "PICKER-componente", VeicoliAssociati = new() { veicoli[12] } }
            });
            using var vm = new EcuViewModel(state);
            LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
            var componente = vm.ItemsAttivi.Single();
            var vista = new EcuView { DataContext = vm };
            var finestra = new Window { Width = 1500, Height = 850, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                LayoutHeadlessSessione.Assesta(finestra);
                var bottone = PerId<Button>(vista, "Componente_Veicoli_Button");
                bottone.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var flyout = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(bottone));
                var contenuto = Assert.IsAssignableFrom<Control>(flyout.Content);
                var ricerca = contenuto.GetVisualDescendants().OfType<TextBox>().Single();
                ricerca.Text = "VEI-PICK-0";
                LayoutHeadlessSessione.Assesta(finestra);
                PerId<Button>(contenuto, "Componente_Veicoli_SelectVisible_Button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                LayoutHeadlessSessione.Assesta(finestra);
                contenuto.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content?.ToString() == veicoli[1]).IsChecked = false;

                Assert.Contains(veicoli[0], componente.VeicoliAssociati);
                Assert.DoesNotContain(veicoli[1], componente.VeicoliAssociati);
                Assert.Contains(veicoli[12], componente.VeicoliAssociati);
                Assert.Equal(10, componente.VeicoliAssociati.Count);
            }
            finally { finestra.Hide(); }
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    [Fact]
    public void FinestraVeicolo_SelezionaSoloRigheFiltrateEDeselSingola() => LayoutHeadlessSessione.Esegui(() =>
    {
        var state = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            state.Master.Componente.Save(Enumerable.Range(0, 13).Select(i => new Componente { Nome = $"Componente-{i:00}" }).ToList());
            state.Master.Funzionalita.Save(new List<Funzionalita>());
            using var vm = new AssociazioniVeicoloViewModel(state, "VEI-1");
            var finestra = new AssociazioniVeicoloWindow { DataContext = vm, Width = 900, Height = 660 };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(vm.InModifica);
                var ricerca = PerId<TextBox>(finestra, "Assoc_Cerca_TextBox");
                ricerca.Text = "Componente-0";
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(10, vm.RigheVisibili.Count);
                var seleziona = PerId<Button>(finestra, "Assoc_SelectVisibleEcu_Button");
                Assert.NotNull(seleziona.Command);
                Assert.True(seleziona.Command!.CanExecute(null));
                seleziona.Command.Execute(null);
                LayoutHeadlessSessione.Assesta(finestra);

                vm.RigheVisibili.Single(r => r.Nome == "Componente-01").Montata = false;

                Assert.All(vm.RigheVisibili.Where(r => r.Nome != "Componente-01"), r => Assert.True(r.Montata));
                Assert.False(vm.RigheVisibili.Single(r => r.Nome == "Componente-01").Montata);
                vm.Ricerca = "";
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(13, vm.RigheVisibili.Count);
                Assert.All(vm.RigheVisibili.Where(r => !r.Nome.StartsWith("Componente-0", StringComparison.Ordinal)), r => Assert.False(r.Montata));
                Assert.Equal(9, vm.RigheVisibili.Count(r => r.Montata));
            }
            finally { finestra.Hide(); }
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    private static T PerId<T>(Visual root, string id) where T : class => root.GetVisualDescendants().OfType<T>()
        .Single(control => control is Control c && AutomationProperties.GetAutomationId(c) == id);
}
