using Avalonia.Controls;
using Avalonia.Interactivity;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class TestTemplateView : UserControl
{
    // Sottoscrizione simmetrica a vm.RigaCreata (fix review 2026-09-25, riuso View/ViewLocator): vedi
    // EcuView.axaml.cs per il motivo (la lambda precedente non si disiscriveva mai dal ViewModel precedente).
    private TestTemplateViewModel? _vmCorrente;

    public TestTemplateView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vmCorrente != null) _vmCorrente.RigaCreata -= OnRigaCreata;
        _vmCorrente = DataContext as TestTemplateViewModel;
        if (_vmCorrente != null) _vmCorrente.RigaCreata += OnRigaCreata;
    }

    private void OnRigaCreata(TestTemplate item)
    {
        GridPrincipale.ScrollIntoView(item, null);
        GridPrincipale.SelectedItem = item;
    }

    /// <summary>Editor annidato del contenuto del template (sezioni/campi, strumenti, azioni, vincolo, fogli
    /// report): stesso schema di StrumentiView.OnGestisciCampi, ma con una finestra dedicata invece di un
    /// flyout, vista la quantità di sotto-sezioni da mostrare (TestTemplateEditorWindow).</summary>
    private async void OnGestisciTemplate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestTemplateViewModel { InModifica: true } vm
            || sender is not Button button || button.Tag is not TestTemplate template) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;

        await UiExceptionGuard.Esegui(
            () => TestTemplateEditorWindow.Apri(owner, vm.Stato, template, vm.Items, vm.StrumentiDisponibili),
            "Apertura editor template");
    }
}
