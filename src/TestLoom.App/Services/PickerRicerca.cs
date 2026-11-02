using System.Globalization;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>Regola di ricerca unica per tutti i selettori con elenchi lunghi (SelezionaDialog, filtri a selezione
/// multipla): sottostringa (non solo prefisso), senza distinzione di maiuscole e di accenti, su testo normalizzato
/// (spazi multipli ridotti a uno, esterni rimossi).</summary>
public static class PickerTesto
{
    public static string Normalizza(string? testo)
    {
        if (string.IsNullOrEmpty(testo)) return string.Empty;
        string decomposto;
        try { decomposto = testo.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { decomposto = testo; } // surrogati non validi: confronto sul testo grezzo
        var sb = new StringBuilder(decomposto.Length);
        var inizioOSpazio = true;
        foreach (var c in decomposto)
        {
            // Il diacritico (es. accento di 'è' dopo la scomposizione NFD) e' un carattere di combinazione: via.
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c))
            {
                if (!inizioOSpazio) { sb.Append(' '); inizioOSpazio = true; }
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
            inizioOSpazio = false;
        }
        if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>Vero se la chiave normalizzata contiene la query normalizzata. Query vuota = tutto corrisponde.</summary>
    public static bool Corrisponde(string chiaveNormalizzata, string queryNormalizzata) =>
        queryNormalizzata.Length == 0 || chiaveNormalizzata.Contains(queryNormalizzata, StringComparison.Ordinal);

    /// <summary>Testo su cui si cerca una voce: il TestoRicerca, con il TestoPrincipale aggiunto solo se il chiamante
    /// non lo include gia' (evita di indicizzare due volte il codice/nome della voce).</summary>
    public static string ChiaveVoce(VoceSelezionabile voce) =>
        string.IsNullOrEmpty(voce.TestoPrincipale) || voce.TestoRicerca.Contains(voce.TestoPrincipale, StringComparison.Ordinal)
            ? voce.TestoRicerca
            : voce.TestoPrincipale + " " + voce.TestoRicerca;
}

/// <summary>Esito di una ricerca: le voci corrispondenti (in ordine sorgente, nessun troncamento) e il numero totale
/// di voci della sorgente, per mostrare "N risultati su M" senza nascondere dati.</summary>
public sealed record PickerRisultato<T>(string Query, IReadOnlyList<T> Voci, int TotaleSorgente);

/// <summary>Motore di ricerca su un elenco in memoria. Le chiavi normalizzate sono calcolate una sola volta (anche in
/// anticipo, fuori dal thread UI) e la ricerca e' un'unica scansione ordinale. Non crea elementi UI: restituisce
/// solo riferimenti ai dati, che il controllo virtualizzato (ListBox) presenta a finestra.</summary>
public sealed class PickerMotore<T> where T : class
{
    private const int IntervalloControlloAnnullamento = 0x0FFF;

    private readonly IReadOnlyList<T> _elementi;
    private readonly Func<T, string> _testoDiRicerca;
    private readonly object _chiaviLock = new();
    private string[]? _chiavi;

    /// <param name="elementi">Sorgente; viene copiata (snapshot) perche' puo' cambiare dopo l'apertura del selettore.</param>
    /// <param name="testoDiRicerca">Testo su cui la query e' confrontata (nome/codice + dettaglio).</param>
    public PickerMotore(IEnumerable<T> elementi, Func<T, string> testoDiRicerca)
    {
        _elementi = elementi.ToList();
        _testoDiRicerca = testoDiRicerca;
    }

    public int Totale => _elementi.Count;

    /// <summary>Costruisce in anticipo le chiavi normalizzate, fuori dal thread UI, cosi' il primo tasto non paga il costo.</summary>
    public Task PreparaAsync(CancellationToken ct = default) => Task.Run(() => Chiavi(ct), ct);

    /// <summary>Ricerca fuori dal thread UI. Lancia OperationCanceledException se <paramref name="ct"/> e' annullato.</summary>
    public Task<PickerRisultato<T>> CercaAsync(string? query, CancellationToken ct = default) =>
        Task.Run(() => Cerca(query, ct), ct);

    public PickerRisultato<T> Cerca(string? query, CancellationToken ct = default)
    {
        var testo = query ?? string.Empty;
        var q = PickerTesto.Normalizza(testo);
        if (q.Length == 0) return new PickerRisultato<T>(testo, _elementi, _elementi.Count);

        var chiavi = Chiavi(ct);
        var voci = new List<T>();
        for (var i = 0; i < chiavi.Length; i++)
        {
            if ((i & IntervalloControlloAnnullamento) == 0) ct.ThrowIfCancellationRequested();
            if (PickerTesto.Corrisponde(chiavi[i], q)) voci.Add(_elementi[i]);
        }
        return new PickerRisultato<T>(testo, voci, _elementi.Count);
    }

    private string[] Chiavi(CancellationToken ct)
    {
        lock (_chiaviLock)
        {
            if (_chiavi != null) return _chiavi;
            var chiavi = new string[_elementi.Count];
            for (var i = 0; i < chiavi.Length; i++)
            {
                if ((i & IntervalloControlloAnnullamento) == 0) ct.ThrowIfCancellationRequested();
                chiavi[i] = PickerTesto.Normalizza(_testoDiRicerca(_elementi[i]));
            }
            _chiavi = chiavi;
            return chiavi;
        }
    }
}
