using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Dialogo UNICO di dettaglio anomalia (pagina Anomalie e pagina Test). Non ridimensionabile e mai piu
/// alto dello schermo (stesso pattern di CreaTestCollegatoWindow). Il ViewModel decide quando chiudersi
/// (<see cref="AnomaliaDettaglioViewModel.ChiusuraRichiesta"/>); questo code-behind non contiene logica.</summary>
public partial class AnomaliaDettaglioWindow : Window
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public AnomaliaDettaglioWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
        // I campi del modello Nc sono legati direttamente a TextBox/ComboBox: il binding scrive il modello senza notificare
        // il ViewModel, quindi ogni modifica dell'utente ricalcola l'indicatore "Modifiche non salvate". Differito di una
        // passata di dispatcher, cosi il valore e gia nel modello quando il ViewModel lo legge.
        AddHandler(TextBox.TextChangedEvent, OnCampoModificato, Avalonia.Interactivity.RoutingStrategies.Bubble);
        AddHandler(Avalonia.Controls.Primitives.SelectingItemsControl.SelectionChangedEvent, OnCampoModificato, Avalonia.Interactivity.RoutingStrategies.Bubble);
        Closed += (_, _) =>
        {
            if (DataContext is AnomaliaDettaglioViewModel vm) vm.ChiusuraRichiesta -= Chiudi;
            _tcs.TrySetResult(DataContext is AnomaliaDettaglioViewModel v && v.Modificato);
        };
    }

    private void Chiudi() => Close();

    private void OnEntraInModifica(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AnomaliaDettaglioViewModel vm) vm.EntraInModifica();
    }

    private void OnCampoModificato(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AnomaliaDettaglioViewModel vm)
            Avalonia.Threading.Dispatcher.UIThread.Post(vm.SegnalaModificaCampo, Avalonia.Threading.DispatcherPriority.Background);
    }

    private async void OnRecuperaBozza(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AnomaliaDettaglioViewModel vm || !vm.PuoRecuperareBozza) return;
        if (vm.HaModificheNonSalvate)
        {
            vm.Errore = LocalizationService.Instance.Traduci("BozzaSalvareAnnullarePrimaMsg");
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new FailedSaveDraftsWindow(vm.WorkspaceRoot,
            "nonconformita", vm.Nc.Uid, editorSelectionMode: true,
            editorFieldCatalog: vm.CampiBozzaRecuperabili);
        try
        {
            var selection = await dialog.ShowDialog<FailedSaveDraftsWindow.EditorDraftSelection?>(owner);
            if (selection == null) return;
            var candidate = await Task.Run(() => vm.CaricaBozzaRecuperabile(selection.Draft));
            vm.ApplicaBozzaRecuperata(candidate, selection.Fields);
        }
        catch (Exception ex)
        {
            vm.Errore = LocalizationService.Instance.Traduci("BozzaNonCaricataPrefisso") + ex.Message;
            CrashLogService.Registra("Recupero bozza locale nel dettaglio NC", ex);
        }
    }

    /// <summary>Apre il dialogo modale; ritorna true se qualcosa e stato scritto (Salva/Chiudi/Riapri/Elimina) e il
    /// chiamante deve quindi ricaricare la propria vista.</summary>
    public static Task<bool> Apri(Window owner, AnomaliaDettaglioViewModel vm)
    {
        var w = new AnomaliaDettaglioWindow
        {
            Title = LocalizationService.Instance.Traduci("AnomaliaDettaglioTitolo") + " · " + vm.Nc.Id,
            DataContext = vm
        };
        vm.ChiusuraRichiesta += w.Chiudi;
        w.ShowDialog(owner);
        return w._tcs.Task;
    }
}
