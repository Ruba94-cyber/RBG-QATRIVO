using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class OperatoriView : UserControl
{
    // Sottoscrizione simmetrica a vm.RigaCreata (fix review 2026-09-25, riuso View/ViewLocator): vedi
    // EcuView.axaml.cs per il motivo (la lambda precedente non si disiscriveva mai dal ViewModel precedente).
    private OperatoriViewModel? _vmCorrente;

    public OperatoriView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnGridBeginningEdit(object? sender, DataGridBeginningEditEventArgs e) =>
        MasterDataRenameEditGuard.OnBeginningEdit(sender, e);

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vmCorrente != null) _vmCorrente.RigaCreata -= OnRigaCreata;
        _vmCorrente = DataContext as OperatoriViewModel;
        if (_vmCorrente != null) _vmCorrente.RigaCreata += OnRigaCreata;
    }

    /// <summary>Il PIN in chiaro torna mascherato a ogni cambio di operatore selezionato.</summary>
    private void OnSelezioneOperatoreCambiata(object? sender, SelectionChangedEventArgs e) => MostraPinCheck.IsChecked = false;

    private void OnRigaCreata(Operatore item)
    {
        GridPrincipale.ScrollIntoView(item, null);
        GridPrincipale.SelectedItem = item;
    }

    /// <summary>Sostituisce l'inserimento libero delle gamme assegnate con una selezione a checkbox dalle
    /// gamme realmente censite (Addendum redesign 2026-08): costruito in code-behind perché una DataGrid
    /// TemplateColumn non ha un modo dichiarativo comodo per un multi-select per-riga legato a una lista
    /// libera di stringhe.</summary>
    private void OnScegliGamme(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Operatore operatore) return;
        if (DataContext is not OperatoriViewModel vm) return;

        var panel = new StackPanel { Spacing = 4, MinWidth = 180 };
        if (vm.GammeDisponibili.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Nessuna gamma censita.", Classes = { "Muted" } });
        }

        // Ricerca solo oltre soglia (feedback utente 2026-08-21), stessa soglia di ListaOltreSogliaConverter.
        TextBox? ricerca = null;
        if (vm.GammeDisponibili.Count > 10)
        {
            ricerca = new TextBox { PlaceholderText = LocalizationService.Instance.Traduci("CercaPlaceholder"), Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            panel.Children.Add(ricerca);
        }
        var lista = new StackPanel { Spacing = 2 };
        var checkBoxes = new System.Collections.Generic.Dictionary<string, CheckBox>();
        foreach (var gamma in vm.GammeDisponibili)
        {
            var checkBox = new CheckBox { Content = gamma, IsChecked = operatore.GammeAssegnate.Contains(gamma) };
            checkBox.IsCheckedChanged += (_, _) =>
            {
                var selezionata = checkBox.IsChecked == true;
                if (selezionata && !operatore.GammeAssegnate.Contains(gamma)) operatore.GammeAssegnate.Add(gamma);
                else if (!selezionata) operatore.GammeAssegnate.Remove(gamma);
                button.Content = operatore.GammeAssegnateTesto;
            };
            checkBoxes[gamma] = checkBox;
            lista.Children.Add(checkBox);
        }
        if (ricerca != null)
            ricerca.TextChanged += (_, _) =>
            {
                var q = ricerca.Text ?? "";
                foreach (var (gamma, checkBox) in checkBoxes)
                    checkBox.IsVisible = string.IsNullOrWhiteSpace(q) || gamma.Contains(q, System.StringComparison.OrdinalIgnoreCase);
            };
        panel.Children.Add(new ScrollViewer { Content = lista, MaxHeight = 260 });

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }
}
