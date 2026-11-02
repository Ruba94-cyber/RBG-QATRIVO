using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TestLoom.App.Services;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class SelezionaDialogMultiSelezioneHeadlessTests
{
    [Fact]
    public void SelezioneMultipla_AzioniMassiveRispettanoRicercaEScelteNascoste()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var voci = new[] { "Alpha 1", "Alpha 2", "Beta" }
                .Select(nome => new VoceSelezionabile { Valore = nome, TestoPrincipale = nome, TestoRicerca = nome })
                .ToList();
            var dialog = SelezionaDialog.Crea("Seleziona", voci, multiSelezione: true);
            try
            {
                LayoutHeadlessSessione.Mostra(dialog);
                var ricerca = PerId<TextBox>(dialog, "SelectDialog_Filter_TextBox");
                var lista = PerId<ListBox>(dialog, "SelectDialog_Items_ListBox");
                var seleziona = PerId<Button>(dialog, "SelectDialog_SelectVisible_Button");
                var deseleziona = PerId<Button>(dialog, "SelectDialog_DeselectVisible_Button");

                ricerca.Text = "Beta";
                AttendiLista(dialog, lista, 1);
                lista.SelectedItems!.Add(voci[2]);

                ricerca.Text = "Alpha";
                AttendiLista(dialog, lista, 2);
                seleziona.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                LayoutHeadlessSessione.Assesta(dialog);
                Assert.Equal(2, lista.SelectedItems!.Count);

                lista.SelectedItems.Remove(voci[0]);
                ricerca.Text = "";
                AttendiLista(dialog, lista, 3);
                Assert.Contains(voci[1], lista.SelectedItems!.Cast<VoceSelezionabile>());
                Assert.Contains(voci[2], lista.SelectedItems!.Cast<VoceSelezionabile>());
                Assert.DoesNotContain(voci[0], lista.SelectedItems!.Cast<VoceSelezionabile>());

                ricerca.Text = "Alpha";
                AttendiLista(dialog, lista, 2);
                deseleziona.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                LayoutHeadlessSessione.Assesta(dialog);
                ricerca.Text = "";
                AttendiLista(dialog, lista, 3);

                Assert.Equal(new[] { "Beta" }, lista.SelectedItems!.Cast<VoceSelezionabile>().Select(v => (string)v.Valore));
            }
            finally
            {
                dialog.Hide();
            }
        });
    }

    [Fact]
    public void SelezioneMultipla_AzioniImmediateAllineanoRicercaEConservanoScelteNascoste()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var voci = new[] { "Alpha 1", "Alpha 2", "Beta" }
                .Select(nome => new VoceSelezionabile { Valore = nome, TestoPrincipale = nome, TestoRicerca = nome })
                .ToList();
            var dialog = SelezionaDialog.Crea("Seleziona", voci, multiSelezione: true);
            try
            {
                LayoutHeadlessSessione.Mostra(dialog);
                var ricerca = PerId<TextBox>(dialog, "SelectDialog_Filter_TextBox");
                var lista = PerId<ListBox>(dialog, "SelectDialog_Items_ListBox");
                var seleziona = PerId<Button>(dialog, "SelectDialog_SelectVisible_Button");
                var deseleziona = PerId<Button>(dialog, "SelectDialog_DeselectVisible_Button");

                ricerca.Text = "Alpha";
                AttendiLista(dialog, lista, 2);
                seleziona.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(new[] { voci[0], voci[1] }, lista.SelectedItems!.Cast<VoceSelezionabile>());

                ricerca.Text = "Beta";
                AttendiLista(dialog, lista, 1);
                lista.SelectedItems!.Add(voci[2]);

                // Il debounce di 200 ms non e' scaduto: il bulk deve applicare la query digitata, non i risultati precedenti.
                ricerca.Text = "Alpha 1";
                seleziona.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AttendiLista(dialog, lista, 1);
                Assert.Same(voci[0], Assert.Single(lista.SelectedItems!.Cast<VoceSelezionabile>()));

                // Anche Deseleziona agisce sulla query corrente e conserva le selezioni Alpha nascoste.
                ricerca.Text = "Beta";
                deseleziona.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AttendiLista(dialog, lista, 1);
                Assert.Empty(lista.SelectedItems!.Cast<VoceSelezionabile>());

                ricerca.Text = "";
                AttendiLista(dialog, lista, 3);
                Assert.Equal(new[] { voci[0], voci[1] }, lista.SelectedItems!.Cast<VoceSelezionabile>());
            }
            finally
            {
                dialog.Hide();
            }
        });
    }

    private static T PerId<T>(Visual root, string id) where T : class => root.GetVisualDescendants().OfType<T>()
        .Single(control => control is Control c && AutomationProperties.GetAutomationId(c) == id);

    private static void AttendiLista(Window finestra, ListBox lista, int numeroVoci)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < limite && lista.ItemCount != numeroVoci)
        {
            Dispatcher.UIThread.RunJobs();
            LayoutHeadlessSessione.Assesta(finestra);
            Thread.Sleep(5);
        }
        Assert.Equal(numeroVoci, lista.ItemCount);
    }
}
