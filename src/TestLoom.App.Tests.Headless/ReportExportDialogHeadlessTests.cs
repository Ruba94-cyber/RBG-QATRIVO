using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TestLoom.App.Services;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Verifica il dialogo Avalonia effettivo in memoria: nessun picker nativo e nessun file vengono creati.</summary>
[Collection("LayoutHeadless")]
public sealed class ReportExportDialogHeadlessTests
{
    [Fact]
    public void DialogoSoglia_MostraTreScelte_CliccaOgniEsitoEChiude()
    {
        var opzioni = new[]
        {
            (Valore: 0, Chiave: "ReportExportTroncaBtn", Id: "Report_ExportLarge_Truncate_Button"),
            (Valore: 1, Chiave: "ReportExportCompletoBtn", Id: "Report_ExportLarge_ZipAll_Button"),
            (Valore: 2, Chiave: "ReportExportAnnullaBtn", Id: "Report_ExportLarge_Cancel_Button")
        };

        foreach (var opzione in opzioni)
        {
            Window? owner = null;
            ReportView.DialogoSceltaExport? dialogo = null;
            Task? chiusura = null;
            using var cancellazione = new CancellationTokenSource();
            LayoutHeadlessSessione.Esegui(() =>
            {
                owner = new Window { Width = 800, Height = 600 };
                owner.Show();
                dialogo = ReportView.CreaDialogoSceltaExportGrande(12_345, 234, 10_000, cancellazione);
                Assert.Equal("Report_ExportLarge_Dialog", AutomationProperties.GetAutomationId(dialogo.Finestra));
                Assert.Equal(3, dialogo.Pulsanti.Count);
                Assert.Equal(opzioni.Select(x => x.Id).OrderBy(x => x), dialogo.Pulsanti.Values
                    .Select(AutomationProperties.GetAutomationId).OrderBy(x => x));
                var pulsante = dialogo.Pulsanti[opzione.Valore];
                Assert.Equal(opzione.Id, AutomationProperties.GetAutomationId(pulsante));
                Assert.Equal(LocalizationService.Instance.Traduci(opzione.Chiave), pulsante.Content?.ToString());
                chiusura = dialogo.Finestra.ShowDialog(owner);
                pulsante.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });

            Assert.Equal(opzione.Valore, dialogo!.Risultato.GetAwaiter().GetResult());
            LayoutHeadlessSessione.Esegui(() => LayoutHeadlessSessione.AttendiComando(chiusura!, TimeSpan.FromSeconds(2)));
            LayoutHeadlessSessione.Esegui(() =>
            {
                Assert.False(dialogo.Finestra.IsVisible);
                owner!.Close();
            });
            Assert.Equal(opzione.Valore == 2, cancellazione.IsCancellationRequested);
        }

        using var tokenChiusura = new CancellationTokenSource();
        Window? ownerFinale = null;
        ReportView.DialogoSceltaExport? chiuso = null;
        Task? attesaChiusura = null;
        LayoutHeadlessSessione.Esegui(() =>
        {
            ownerFinale = new Window { Width = 800, Height = 600 };
            ownerFinale.Show();
            chiuso = ReportView.CreaDialogoSceltaExportGrande(1, 1, 1, tokenChiusura);
            attesaChiusura = chiuso.Finestra.ShowDialog(ownerFinale);
            chiuso.Finestra.Close();
        });
        Assert.Null(chiuso!.Risultato.GetAwaiter().GetResult());
        LayoutHeadlessSessione.Esegui(() => LayoutHeadlessSessione.AttendiComando(attesaChiusura!, TimeSpan.FromSeconds(2)));
        LayoutHeadlessSessione.Esegui(() => ownerFinale!.Close());
        Assert.False(tokenChiusura.IsCancellationRequested);
    }
}
