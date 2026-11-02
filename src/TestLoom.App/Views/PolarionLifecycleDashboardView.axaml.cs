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
            var result = await Task.Run(() => PolarionExcelReportService.Generate(rows, DateTimeOffset.Now));
            await File.WriteAllBytesAsync(path, result);
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
            await File.WriteAllTextAsync(path, vm.PolarionCsv(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) { CrashLogService.Registra("Esportazione NC/CR TICKET CSV", ex); }
    }


}
