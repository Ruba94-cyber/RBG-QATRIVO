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
    /// Impostazioni e' inclusa: per essa si precarica solo la View, non il VM.</summary>
    public static IReadOnlyList<string> CreaCodaPagine(
        IEnumerable<string> ids, string? paginaIniziale) =>
        ids.Where(id => !string.IsNullOrWhiteSpace(id)
            && !string.Equals(id, paginaIniziale, StringComparison.OrdinalIgnoreCase))
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
