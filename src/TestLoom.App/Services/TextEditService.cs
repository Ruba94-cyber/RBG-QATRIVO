namespace TestLoom.App.Services;

/// <summary>Ponte minimale ViewModel -> finestra dedicata alla scrittura di un campo testuale lungo, sullo
/// stesso schema di ConfirmService: la View registra l'implementazione reale all'avvio, i ViewModel la usano
/// senza conoscere Avalonia. Restituisce null se l'utente annulla (il chiamante non deve toccare il valore
/// originale in quel caso), altrimenti il testo modificato anche se vuoto.</summary>
public static class TextEditService
{
    public static Func<string, string, Task<string?>>? Handler { get; set; }

    public static Task<string?> Modifica(string titolo, string valoreCorrente) =>
        Handler == null ? Task.FromResult<string?>(null) : Handler(titolo, valoreCorrente);
}
