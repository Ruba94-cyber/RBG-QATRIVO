using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class CampoSistemaDefinizioneView : UserControl
{
    // Sottoscrizione simmetrica a vm.RigaCreata (fix review 2026-09-25, riuso View/ViewLocator): vedi
    // EcuView.axaml.cs per il motivo (la lambda precedente non si disiscriveva mai dal ViewModel precedente).
    private CampoSistemaDefinizioneViewModel? _vmCorrente;

    public CampoSistemaDefinizioneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vmCorrente != null)
        {
            _vmCorrente.RigaCreata -= OnRigaCreata;
            _vmCorrente.PropertyChanged -= OnVmPropertyChanged;
        }
        _vmCorrente = DataContext as CampoSistemaDefinizioneViewModel;
        if (_vmCorrente != null)
        {
            _vmCorrente.RigaCreata += OnRigaCreata;
            _vmCorrente.PropertyChanged += OnVmPropertyChanged;
        }
    }

    /// <summary>Dopo il salvataggio (uscita dalla modifica) l'Etichetta e la Chiave della riga nuova sono definitive: si
    /// riallineano Id e Name delle righe già realizzate, che al caricamento della riga erano ancora vuoti.</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CampoSistemaDefinizioneViewModel.InModifica) || _vmCorrente?.InModifica != false) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(AggiornaIdRighe, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void AggiornaIdRighe()
    {
        foreach (var riga in GridPrincipale.GetVisualDescendants().OfType<DataGridRow>())
            if (riga.DataContext is CampoSistemaDefinizione campo) ImpostaIdRiga(riga, campo);
    }

    private static void ImpostaIdRiga(DataGridRow riga, CampoSistemaDefinizione campo)
    {
        Avalonia.Automation.AutomationProperties.SetAutomationId(riga, $"CampoSist_Riga_{campo.Chiave}");
        Avalonia.Automation.AutomationProperties.SetName(riga, campo.Etichetta);
    }

    /// <summary>La riga nuova (da "+ Nuovo") va selezionata, portata in vista sulla colonna Etichetta e messa in modifica:
    /// su uno schermo piccolo, in fondo a un catalogo lungo, non si vedrebbe altrimenti. La selezione arriva già dal
    /// ViewModel (ElementoSelezionato, legato a SelectedItem); qui si completano scroll, fuoco e modifica della cella.</summary>
    private void OnRigaCreata(CampoSistemaDefinizione item)
    {
        GridPrincipale.SelectedItem = item;
        var colonnaEtichetta = GridPrincipale.Columns.FirstOrDefault();
        if (colonnaEtichetta == null) return;
        GridPrincipale.ScrollIntoView(item, colonnaEtichetta);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(GridPrincipale.SelectedItem, item)) return;
            GridPrincipale.CurrentColumn = colonnaEtichetta;
            GridPrincipale.BeginEdit();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Opzioni di un campo di sistema di tipo Lista: stesso editor a una voce per riga dei campi strumento
    /// (ListaOpzioniEditorWindow). Il risultato passa dal ViewModel, che lo registra come modifica da salvare;
    /// l'annullamento non cambia nulla.</summary>
    private async void OnModificaOpzioniCampo(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CampoSistemaDefinizione campo }) return;
        if (_vmCorrente is not { InModifica: true } vm) return;
        await ModificaOpzioni(vm, campo);
    }

    /// <summary>Tipo cambiato nella griglia: passando a Lista un campo senza opzioni, l'editor delle opzioni si apre subito, come negli
    /// altri editor di definizione. Solo con focus tastiera o menu aperto: la griglia ricicla le righe mentre si scorre, e una riga
    /// riciclata non deve aprire una finestra.</summary>
    private async void OnTipoCambiato(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: CampoSistemaDefinizione campo } combo ||
            (!combo.IsKeyboardFocusWithin && !combo.IsDropDownOpen)) return;
        var precedente = e.RemovedItems.Count > 0 ? e.RemovedItems[0] as TipoCampoStrumento? : null;
        var nuovo = e.AddedItems.Count > 0 && e.AddedItems[0] is TipoCampoStrumento t ? t : campo.Tipo;
        if (!ListaOpzioniEditorWindow.DeveProporre(precedente, nuovo, campo.OpzioniLista.Count)) return;
        if (_vmCorrente is not { InModifica: true } vm) return;
        await ModificaOpzioni(vm, campo);
    }

    private async Task ModificaOpzioni(CampoSistemaDefinizioneViewModel vm, CampoSistemaDefinizione campo)
    {
        if (TopLevel.GetTopLevel(this) is not Window proprietario) return;
        var risultato = await ListaOpzioniEditorWindow.Apri(proprietario, campo.OpzioniLista);
        if (risultato == null) return;
        vm.ImpostaOpzioniLista(campo, risultato);
    }

    /// <summary>Id stabile per la riga, usato dai test FlaUI: CampoSist_Riga_{Chiave}; Name = Etichetta. Impostato al
    /// caricamento della riga, quindi riflette la chiave dopo il salvataggio quando la riga viene ricreata.</summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not CampoSistemaDefinizione campo) return;
        ImpostaIdRiga(e.Row, campo);
    }
}
