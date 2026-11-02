namespace TestLoom.App.Services;

/// <summary>Copia di testo negli appunti di sistema (es. "Copia report" degli import). Il ViewModel non dipende da Avalonia:
/// la finestra principale registra <see cref="Handler"/> all'avvio; senza handler (test headless) la copia non riesce.</summary>
public static class ClipboardService
{
    /// <summary>Esito della copia: true se gli appunti hanno accettato il testo.</summary>
    public static Func<string, Task<bool>>? Handler { get; set; }

    /// <summary>Copia il testo e restituisce l'esito. False senza gestore o con un errore degli appunti (registrato): il
    /// chiamante deve dirlo all'utente invece di confermare una copia mai avvenuta (P4 5).</summary>
    public static async Task<bool> CopiaTestoAsync(string testo)
    {
        var gestore = Handler;
        if (gestore == null) return false;
        try { return await gestore(testo); }
        catch (Exception ex)
        {
            // Gli appunti possono essere occupati da un'altra applicazione: l'errore si registra, non si propaga.
            CrashLogService.Registra("Copia negli appunti", ex);
            return false;
        }
    }
}
