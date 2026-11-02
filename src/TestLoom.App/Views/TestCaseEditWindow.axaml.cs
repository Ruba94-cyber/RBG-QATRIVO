using Avalonia.Controls;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>
/// Finestra secondaria per un Test pienamente editabile (tipicamente Test di validazione aperto da una CR).
/// È proprietaria del lifecycle del relativo ViewModel: una X con modifiche chiede conferma, una chiusura
/// confermata rilascia sempre lock/timer tramite Dispose e il callback Torna/Salva chiude davvero la finestra.
/// </summary>
public partial class TestCaseEditWindow : Window
{
    private bool _chiusuraAutorizzata;
    private bool _disposed;

    public TestCaseEditWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this, grande: true);
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>Compatibilità per eventuali caller che possiedono già il ViewModel. La finestra garantisce
    /// comunque conferma e Dispose, anche se il callback interno del VM non può essere sostituito.</summary>
    public static void Apri(Window owner, TestCaseEditorViewModel vm, string titolo)
    {
        var w = new TestCaseEditWindow { Title = titolo, DataContext = vm };
        w.Show(owner);
    }

    /// <summary>
    /// Percorso preferito per i Test di validazione CR: costruisce il VM con un callback di ritorno che chiude
    /// questa stessa finestra. Salva/Annulla non possono quindi lasciare a schermo un editor già terminato.
    /// </summary>
    public static void Apri(Window owner, ChangeRequestEditorViewModel parent, string testCaseId)
    {
        TestCaseEditWindow? window = null;
        var vm = parent.CreaEditorTestValidazione(testCaseId, () => window?.CloseFromViewModel());
        window = new TestCaseEditWindow { Title = testCaseId, DataContext = vm };
        window.Show(owner);
    }

    private void CloseFromViewModel()
    {
        _chiusuraAutorizzata = true;
        Close();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_chiusuraAutorizzata) return;
        if (DataContext is not TestCaseEditorViewModel vm) return;
        if (!vm.ModificheNonSalvate)
        {
            _chiusuraAutorizzata = true;
            return;
        }

        e.Cancel = true;
        await UiExceptionGuard.Esegui(async () =>
        {
            if (!await ConfirmService.ConfermaAbbandonoModifiche()) return;
            _chiusuraAutorizzata = true;
            Close();
        }, LocalizationService.Instance.Traduci("ConfermaChiusuraFinestraTestSecondariaTitolo"));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _disposed = true;
        if (DataContext is IDisposable disposable) disposable.Dispose();
        DataContext = null;
    }
}
