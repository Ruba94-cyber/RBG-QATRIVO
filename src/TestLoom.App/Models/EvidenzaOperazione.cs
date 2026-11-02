using System.Globalization;

namespace TestLoom.App.Models;

/// <summary>Collegamento tra un'operazione (Condizione) di una REV e gli allegati del Test che ne sono l'evidenza.
/// Il file resta in <see cref="TestCase.Allegati"/> (stesso storage, stessi limiti, stessa rimozione differita): qui vive solo
/// l'elenco degli <see cref="Allegato.Uid"/> collegati, in <see cref="Condizione.ValoriStrumenti"/> sotto una chiave riservata,
/// come la durata (<see cref="DurataOperazione"/>). Nessun cambio di schema: un client precedente ritrasmette la chiave
/// senza interpretarla, quindi non perde il collegamento. Il collegamento appartiene alla singola REV: una nuova
/// iterazione o una ripetizione non lo ereditano. Un Uid che non corrisponde piu' ad alcun allegato e' ignorato in lettura
/// (l'allegato eliminato non lascia orfani visibili), mai riscritto in silenzio.</summary>
public static class EvidenzaOperazione
{
    /// <summary>Chiave riservata: "EVIDENZE::" non coincide con alcuno strumento del catalogo ("Strumento::Campo").</summary>
    public const string ChiaveValoriStrumenti = "EVIDENZE::AllegatiUid";

    /// <summary>Limite di sanita' per operazione.</summary>
    public const int MassimoPerOperazione = 50;

    private const char Separatore = ';';

    /// <summary>Vero per le chiavi riservate (durata ed evidenze): non sono campi strumento da mostrare, copiare o esportare.</summary>
    public static bool EChiaveRiservata(string? chiave) =>
        chiave == ChiaveValoriStrumenti || chiave == DurataOperazione.ChiaveValoriStrumenti ||
        chiave == VistaCampiOperazione.ChiaveValoriStrumentiTest;

    /// <summary>Uid collegati, in ordine di inserimento e senza duplicati. Token non validi sono ignorati.</summary>
    public static IReadOnlyList<Guid> Leggi(IReadOnlyDictionary<string, string>? valori)
    {
        if (valori == null || !valori.TryGetValue(ChiaveValoriStrumenti, out var testo) || string.IsNullOrWhiteSpace(testo))
            return Array.Empty<Guid>();
        var risultato = new List<Guid>();
        foreach (var token in testo.Split(Separatore, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Guid.TryParse(token, out var uid) && !risultato.Contains(uid)) risultato.Add(uid);
        return risultato;
    }

    /// <summary>Aggiunge il collegamento (idempotente). Falso se il limite per operazione e' raggiunto.</summary>
    public static bool Aggiungi(Dictionary<string, string> valori, Guid allegatoUid)
    {
        ArgumentNullException.ThrowIfNull(valori);
        var attuali = Leggi(valori).ToList();
        if (attuali.Contains(allegatoUid)) return true;
        if (attuali.Count >= MassimoPerOperazione) return false;
        attuali.Add(allegatoUid);
        Scrivi(valori, attuali);
        return true;
    }

    /// <summary>Rimuove il collegamento (idempotente). Non tocca l'allegato.</summary>
    public static bool Rimuovi(Dictionary<string, string> valori, Guid allegatoUid)
    {
        ArgumentNullException.ThrowIfNull(valori);
        var attuali = Leggi(valori).ToList();
        if (!attuali.Remove(allegatoUid)) return false;
        Scrivi(valori, attuali);
        return true;
    }

    /// <summary>Allegati del Test collegati a una operazione (esclusi archiviati e Uid non piu' presenti).</summary>
    public static IReadOnlyList<Allegato> Evidenze(TestCase test, Condizione operazione) =>
        Leggi(operazione.ValoriStrumenti)
            .Select(u => test.Allegati.FirstOrDefault(a => a.Uid == u && !a.Archiviata))
            .OfType<Allegato>().ToList();

    /// <summary>Etichetta leggibile dell'operazione per report ed esportazioni (stessa regola del testo ticket).</summary>
    public static string EtichettaOperazione(Condizione operazione, int indice) =>
        string.IsNullOrWhiteSpace(operazione.Funzionalita) ? $"Operazione #{indice + 1}" : operazione.Funzionalita;

    /// <summary>Per ogni allegato collegato: "REV n · operazione" (piu' voci separate da "; " se riusato). Vuoto = evidenza del solo Test.</summary>
    public static Dictionary<Guid, string> EtichettePerAllegato(TestCase test)
    {
        var risultato = new Dictionary<Guid, List<string>>();
        foreach (var esecuzione in test.Esecuzioni)
            for (var i = 0; i < esecuzione.Condizioni.Count; i++)
                foreach (var allegato in Evidenze(test, esecuzione.Condizioni[i]))
                {
                    if (!risultato.TryGetValue(allegato.Uid, out var voci)) risultato[allegato.Uid] = voci = new List<string>();
                    voci.Add($"REV {esecuzione.Iterazione} · {EtichettaOperazione(esecuzione.Condizioni[i], i)}");
                }
        return risultato.ToDictionary(kv => kv.Key, kv => string.Join("; ", kv.Value));
    }

    /// <summary>Elimina tutti i collegamenti della REV (usato quando si clona una REV in una nuova).</summary>
    public static void Svuota(Dictionary<string, string> valori) => valori?.Remove(ChiaveValoriStrumenti);

    private static void Scrivi(Dictionary<string, string> valori, List<Guid> uid)
    {
        if (uid.Count == 0) valori.Remove(ChiaveValoriStrumenti);
        else valori[ChiaveValoriStrumenti] = string.Join(Separatore, uid.Select(u => u.ToString("D", CultureInfo.InvariantCulture)));
    }
}
