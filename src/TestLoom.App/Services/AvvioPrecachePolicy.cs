namespace TestLoom.App.Services;

/// <summary>
/// Solo criterio di esecuzione della cache di navigazione differita.
/// Non modifica dati, autorizzazioni, sincronizzazioni o stato del workflow.
/// Avviarla su una pagina in uso sottrarrebbe CPU/I/O all'operatore.
/// </summary>
public static class AvvioPrecachePolicy
{
    /// <summary>Il primo NavItem e' gia' caricato dall'app. Restituisce ogni altra
    /// destinazione nel suo ordine configurato, senza presupporre che Home sia prima.
    /// Limitata a Test, WU Status e Report.</summary>
    /// <summary>Solo le pagine che pagano un vero ricalcolo alla prima visita (audit 6.4.0). Scaldare anche le altre
    /// aggiunge CPU/thread UI subito dopo la finestra senza beneficio misurabile.</summary>
    private static readonly string[] PagineCalde = { "wustatus", "report", "test" };

    public static IReadOnlyList<string> CreaCodaPagine(
        IEnumerable<string> ids, string? paginaIniziale) =>
        ids.Where(id => !string.IsNullOrWhiteSpace(id)
            && !string.Equals(id, paginaIniziale, StringComparison.OrdinalIgnoreCase)
            && PagineCalde.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool PuoScaldare(
        bool cartellaValida,
        bool inizializzazioneCompletata,
        bool mostraPrimoAvvio,
        bool mostraOnboarding,
        bool caricamentoPagina,
        bool aggiornamentoInCorso,
        bool editorConModifiche,
        bool paginaVisibile) =>
        cartellaValida && inizializzazioneCompletata &&
        !mostraPrimoAvvio && !mostraOnboarding && !caricamentoPagina && !aggiornamentoInCorso &&
        !editorConModifiche && paginaVisibile;
}
