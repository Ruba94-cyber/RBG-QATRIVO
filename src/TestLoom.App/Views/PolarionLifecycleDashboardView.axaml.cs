using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Interactive TICKET analytics hosted only from the NC/CR page.</summary>
public partial class PolarionLifecycleDashboardView : UserControl
{
    public PolarionLifecycleDashboardView() => InitializeComponent();

    private void OnChiudiDashboard(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Close();
    }

    private async void OnEsportaReportPolarionExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PolarionLifecycleViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance.Scegli("Esporta report TICKET / Ticketing (Excel)",
                    "Export TICKET / Ticketing report (Excel)", "Exporter le rapport TICKET / Ticketing (Excel)"),
                SuggestedFileName = $"TestLoom_TICKET_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "xlsx",
                FileTypeChoices = new[] { new FilePickerFileType("Excel XLSX") { Patterns = new[] { "*.xlsx" } } }
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            var rows = vm.PolarionRigheFiltratePerExport();
            // Evita MemoryStream + ToArray + WriteAllBytes per report molto grandi.
            // Il workbook completo viene scritto su file temporaneo prima del commit finale.
            await Task.Run(() => PolarionExcelReportService.SaveToFile(rows, DateTimeOffset.Now, path));
        }
        catch (Exception ex) { CrashLogService.Registra("Esportazione NC/CR TICKET Excel", ex); }
    }

    private async void OnEsportaReportPolarionCsv(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PolarionLifecycleViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance.Scegli("Esporta report TICKET / Ticketing (CSV)",
                    "Export TICKET / Ticketing report (CSV)", "Exporter le rapport TICKET / Ticketing (CSV)"),
                SuggestedFileName = $"TestLoom_TICKET_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "csv",
                FileTypeChoices = new[] { new FilePickerFileType("CSV UTF-8") { Patterns = new[] { "*.csv" } } }
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            // Lo snapshot dei filtri si acquisisce sul thread UI; la serializzazione
            // potenzialmente lunga resta su un thread di lavoro (come l'export XLSX).
            var rows = vm.PolarionRigheFiltratePerExport();
            var csv = await Task.Run(() => PolarionLifecycleViewModel.PolarionCsv(rows));
            await File.WriteAllTextAsync(path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) { CrashLogService.Registra("Esportazione NC/CR TICKET CSV", ex); }
    }


}
