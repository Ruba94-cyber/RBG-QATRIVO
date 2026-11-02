namespace TestLoom.App.Services;

/// <summary>Scelta delle colonne di un foglio dati: le intestazioni sono l'identificativo della colonna, l'ordine e' sempre quello canonico
/// del foglio. Nessuna scelta (null o senza colonne valide) = tutte le colonne.</summary>
public static class ExportColonneSelezione
{
    public static int[] Indici(IReadOnlyList<string> intestazioni, IReadOnlyList<string>? scelte)
    {
        var tutte = Enumerable.Range(0, intestazioni.Count).ToArray();
        if (scelte == null || scelte.Count == 0) return tutte;
        var scelti = tutte.Where(i => scelte.Contains(intestazioni[i], StringComparer.OrdinalIgnoreCase)).ToArray();
        return scelti.Length == 0 ? tutte : scelti;
    }

    public static List<T> Estrai<T>(IReadOnlyList<T> valori, int[] indici) => indici.Select(i => valori[i]).ToList();
}
