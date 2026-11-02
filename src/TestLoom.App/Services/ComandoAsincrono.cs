using Avalonia.Threading;

namespace TestLoom.App.Services;

/// <summary>Esecuzione protetta dei comandi async ([RelayCommand] di CommunityToolkit). Senza questo guard l'eccezione di un
/// comando resta nel Task del comando, che nessuno osserva: l'operazione fallisce senza alcun segnale. Qui l'eccezione viene
/// registrata subito con l'origine e riportata nel messaggio di errore del ViewModel. L'annullamento non è un errore.</summary>
public static class ComandoAsincrono
{
    public static async Task EseguiAsync(Func<Task> azione, string origine, Action<string> mostraErrore)
    {
        try { await azione(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CrashLogService.Registra(origine, ex);
            try
            {
                var messaggio = string.Format(LocalizationService.Instance.Traduci("ComandoNonRiuscitoFmt"), ex.Message);
                if (Avalonia.Application.Current is not null && !Dispatcher.UIThread.CheckAccess())
                    await Dispatcher.UIThread.InvokeAsync(() => mostraErrore(messaggio)).GetTask();
                else
                    mostraErrore(messaggio);
            }
            catch (Exception notificaEx)
            {
                CrashLogService.Registra($"{origine}: segnalazione errore comando non riuscita", notificaEx);
            }
        }
    }
}
