using Avalonia.Controls;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Avviso all'utente per un'operazione fallita in un gestore di evento della vista (P4 3): il dettaglio tecnico è
/// già nel crash.log, qui si dice dove trovarlo. Con una finestra proprietaria visibile il dialogo è modale; altrimenti
/// si apre come finestra autonoma, così l'errore non resta invisibile.</summary>
internal static class ErroreOperazioneDialogo
{
    public static async Task MostraAsync(Control? origine)
    {
        var loc = LocalizationService.Instance;
        var percorso = Path.Combine(CrashLogService.LogDirectory, "crash.log");
        var dialogo = ConfirmDialog.Crea(
            loc.Traduci("ErroreOperazioneTitolo"),
            string.Format(loc.Traduci("ErroreOperazioneMessaggioFmt"), percorso),
            loc.Traduci("OkBtn"), "");
        var proprietario = origine == null ? null : TopLevel.GetTopLevel(origine) as Window;
        if (proprietario is { IsVisible: true }) await dialogo.ShowDialog<bool>(proprietario);
        else dialogo.Show();
    }
}
