using Avalonia.Controls;
using Avalonia.Interactivity;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>
/// Viewer puramente diagnostico. Non modifica indici, attributi, journal,
/// backup o file operativi; la scansione metadata avviene fuori dal thread UI.
/// </summary>
public partial class WorkspaceDiagnosticsWindow : Window
{
    private readonly string _sharedRoot;
    private readonly string? _backupEsterno;
    private bool _busy;

    public WorkspaceDiagnosticsWindow() : this("", null) { }

    public WorkspaceDiagnosticsWindow(string sharedRoot, string? backupEsterno)
    {
        _sharedRoot = sharedRoot;
        _backupEsterno = backupEsterno;
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = LocalizationService.Instance.Traduci("WsDiagScansioneInCorsoTesto");
        string prefisso = "";
        try
        {
            // 6.7.1: cartella dati in uso e modalità condivisa, stesse fonti del file di esportazione. Fuori dal thread UI.
            var intestazione = await Task.Run(() => WorkspaceDiagnosticSnapshotService.IntestazioneCartella(_sharedRoot));
            prefisso = string.Join(Environment.NewLine, intestazione) + Environment.NewLine + Environment.NewLine;
            var snapshot = await Task.Run(() =>
                WorkspaceDiagnosticSnapshotService.Collect(_sharedRoot, _backupEsterno));
            DetailsText.Text = prefisso + snapshot.Format();
            StatusText.Text = snapshot.Available
                ? string.Format(LocalizationService.Instance.Traduci("WsDiagFotografiaCompletataFmt"), snapshot.Warnings.Count)
                : LocalizationService.Instance.Traduci("WsDiagCartellaNonDisponibileTesto");
        }
        catch (Exception ex)
        {
            DetailsText.Text = prefisso + LocalizationService.Instance.Traduci("WsDiagNonCompletataPrefisso") + ex.Message +
                Environment.NewLine + LocalizationService.Instance.Traduci("WsDiagNessunDatoModificatoTesto");
            StatusText.Text = LocalizationService.Instance.Traduci("WsDiagNonDisponibileTesto");
            CrashLogService.Registra("Centro integrità locale", ex);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _busy = false;
        }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
