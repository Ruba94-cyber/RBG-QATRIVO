using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Elenco compatto delle anomalie collegate a un Test, aperto dalla Lista Test ("Verifica anomalie").
/// Ogni riga ha "Modifica stato" che apre lo STESSO <see cref="AnomaliaDettaglioWindow"/> usato altrove.</summary>
public partial class AnomalieTestElencoWindow : Window
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public AnomalieTestElencoWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
        Closed += (_, _) => _tcs.TrySetResult(DataContext is AnomalieTestElencoViewModel v && v.Modificato);
    }

    public static Task<bool> Apri(Window owner, AnomalieTestElencoViewModel vm)
    {
        var w = new AnomalieTestElencoWindow
        {
            Title = LocalizationService.Instance.Traduci("AnomalieDelTestElencoTitolo") + " · " + vm.TestId,
            DataContext = vm
        };
        w.ShowDialog(owner);
        return w._tcs.Task;
    }

    private async void OnModificaStato(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AnomalieTestElencoViewModel vm || sender is not Button { DataContext: NcRiga riga }) return;
        await UiExceptionGuard.Esegui(async () =>
        {
            if (await AnomaliaDettaglioWindow.Apri(this, vm.CreaDettaglio(riga)))
            {
                vm.Modificato = true;
                vm.Ricarica();
            }
        }, "Modifica stato anomalia dalla Lista Test");
    }

    private void OnChiudi(object? sender, RoutedEventArgs e) => Close();
}
