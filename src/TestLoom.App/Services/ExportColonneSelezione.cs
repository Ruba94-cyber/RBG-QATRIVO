namespace TestLoom.App.Services;

/// <summary>Scelta delle colonne di un foglio dati. Le scelte esplicite determinano anche
/// l'ordine delle colonne; null o nessuna colonna valida mantiene l'ordine canonico.</summary>
public static class ExportColonneSelezione
{
    public static int[] Indici(IReadOnlyList<string> intestazioni, IReadOnlyList<string>? scelte)
    {
        var tutte = Enumerable.Range(0, intestazioni.Count).ToArray();
        if (scelte == null || scelte.Count == 0) return tutte;
        var indici = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < intestazioni.Count; i++)
            indici.TryAdd(intestazioni[i], i);
        var scelti = new List<int>(scelte.Count);
        var visti = new HashSet<int>();
        foreach (var scelta in scelte)
            if (scelta != null && indici.TryGetValue(scelta, out var indice) && visti.Add(indice))
                scelti.Add(indice);
        return scelti.Count == 0 ? tutte : scelti.ToArray();
    }

    public static List<T> Estrai<T>(IReadOnlyList<T> valori, int[] indici) => indici.Select(i => valori[i]).ToList();
}
