using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Ordine personalizzato dei valori degli assi della matrice Report (es. "Diag. base", "Diag. avanzata",
/// "Programming" in un ordine scelto invece che alfabetico). Decisioni utente: l'ordine e' PERSONALE per
/// operatore e ruolo, salvato solo in AppConfig (mai nella cartella dati condivisa) e vale per DIMENSIONE,
/// non per layout di report: la stessa Categoria ha lo stesso ordine sia sulle righe sia sulle colonne.
/// Chiave: operatore|ruolo|dimensione (stesso schema di TableLayoutPreferences.Chiave).
/// </summary>
public static class OrdineAssiReportService
{
    public static string Chiave(string operatore, RuoloOperatore ruolo, string dimensione) =>
        $"{(operatore ?? "").Trim()}|{ruolo}|{(dimensione ?? "").Trim()}";

    public static IReadOnlyList<string>? Leggi(AppConfig config, RuoloOperatore ruolo, string dimensione) =>
        config.OrdineAssiReport.TryGetValue(Chiave(config.OperatoreAttivo, ruolo, dimensione), out var ordine) && ordine.Count > 0
            ? ordine : null;

    /// <summary>Imposta l'ordine (null o vuoto = ripristino dell'ordine alfabetico: la chiave viene rimossa).
    /// Restituisce true se la configurazione e' cambiata (il chiamante salva solo in quel caso).</summary>
    public static bool Imposta(AppConfig config, RuoloOperatore ruolo, string dimensione, IReadOnlyList<string>? ordine)
    {
        var chiave = Chiave(config.OperatoreAttivo, ruolo, dimensione);
        if (ordine == null || ordine.Count == 0)
            return config.OrdineAssiReport.Remove(chiave);
        var nuovo = ordine.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (config.OrdineAssiReport.TryGetValue(chiave, out var esistente) && esistente.SequenceEqual(nuovo, StringComparer.Ordinal))
            return false;
        config.OrdineAssiReport[chiave] = nuovo;
        return true;
    }

    /// <summary>Ordine da salvare dopo una modifica fatta su un sottoinsieme di valori (la matrice filtrata
    /// mostra solo alcuni valori): i valori scelti nel dialogo in testa, poi i valori salvati in precedenza
    /// che il dialogo non conteneva (restano "dormienti" e rivivono se il valore ricompare).</summary>
    public static List<string> Unisci(IReadOnlyList<string> nuovoOrdine, IReadOnlyList<string>? ordinePrecedente)
    {
        var risultato = new List<string>(nuovoOrdine);
        if (ordinePrecedente == null) return risultato;
        var presenti = new HashSet<string>(nuovoOrdine, StringComparer.OrdinalIgnoreCase);
        foreach (var v in ordinePrecedente) if (presenti.Add(v)) risultato.Add(v);
        return risultato;
    }

    /// <summary>Ordina i valori di un asse: prima quelli presenti nell'ordine salvato (nella posizione
    /// salvata), poi i nuovi in ordine alfabetico; i valori salvati non piu' presenti sono ignorati.
    /// O(n log n) sui soli valori dell'asse: una lookup per valore, nessun accesso alle celle.</summary>
    public static List<string> Ordina(IEnumerable<string> valori, IReadOnlyList<string>? ordine)
    {
        if (ordine == null || ordine.Count == 0)
            return valori.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var posizione = new Dictionary<string, int>(ordine.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ordine.Count; i++) posizione.TryAdd(ordine[i], i);
        var lista = valori.ToList();
        lista.Sort((a, b) =>
        {
            var pa = posizione.TryGetValue(a, out var ia);
            var pb = posizione.TryGetValue(b, out var ib);
            if (pa && pb) return ia.CompareTo(ib);
            if (pa) return -1;
            if (pb) return 1;
            return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        });
        return lista;
    }

    /// <summary>Sposta di una posizione (delta -1 = su, +1 = giu') l'elemento all'indice dato; restituisce
    /// il nuovo indice (invariato se fuori limite).</summary>
    public static int Sposta<T>(IList<T> elenco, int indice, int delta)
    {
        var destinazione = indice + delta;
        if (indice < 0 || indice >= elenco.Count || destinazione < 0 || destinazione >= elenco.Count) return indice;
        (elenco[indice], elenco[destinazione]) = (elenco[destinazione], elenco[indice]);
        return destinazione;
    }
}
