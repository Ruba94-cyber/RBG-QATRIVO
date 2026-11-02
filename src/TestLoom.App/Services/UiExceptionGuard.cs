namespace TestLoom.App.Services;

/// <summary>Confine di sicurezza per gli handler UI <c>async void</c>. Registra l'eccezione con l'origine (il contesto
/// dell'azione) e la consuma: senza il guard arriverebbe a Dispatcher.UnhandledException, che la registra ma senza
/// contesto e senza alcun messaggio all'utente. <paramref name="mostra"/> (facoltativo) avvisa l'utente dopo la
/// registrazione; un errore di <paramref name="mostra"/> stesso viene registrato, mai propagato.</summary>
public static class UiExceptionGuard
{
    public static async Task Esegui(Func<Task> azione, string origine, Action<string, Exception?>? registra = null, Func<Task>? mostra = null)
    {
        try { await azione(); }
        catch (Exception ex)
        {
            (registra ?? CrashLogService.Registra)(origine, ex);
            if (mostra == null) return;
            try { await mostra(); }
            catch (Exception fallimento) { (registra ?? CrashLogService.Registra)(origine + " (messaggio di errore)", fallimento); }
        }
    }
}
