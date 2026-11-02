using System.Globalization;

namespace TestLoom.App.Models;

/// <summary>Durata manuale di un'operazione (Condizione), in minuti interi (TIME, 6.6.1). Vale per OGNI tipo di Test
/// (Validazione, UnitLogger, UnitLogger Performance, RAS) e non dipende dallo strumento diagnostico scelto.
/// Il valore vive in <see cref="Condizione.ValoriStrumenti"/> sotto una chiave riservata: i Test JSON restano additivi
/// (schema condiviso 10 invariato) e un client precedente (6.6.0) lo ritrasmette senza interpretarlo.
/// Semantica: vuoto (null) = durata non nota, mai inventata; 0 = zero noto. Negativi e testo non numerico non sono validi.</summary>
public static class DurataOperazione
{
    /// <summary>Chiave riservata. Il prefisso "TEMPO::" non coincide con il nome di nessuno strumento del catalogo: i campi
    /// reali sono "Strumento::Campo". Ogni lettura che conta i valori degli strumenti deve escluderla.</summary>
    public const string ChiaveValoriStrumenti = "TEMPO::DurataMinuti";

    /// <summary>Passo dei pulsanti +/- dell'editor.</summary>
    public const int PassoRapido = 5;

    /// <summary>Limite di sanita' (circa 694 giorni di lavoro su un'unica operazione): valori oltre sono rifiutati.</summary>
    public const int MassimoMinuti = 1_000_000;

    /// <summary>Interpreta il testo digitato. Vuoto = sconosciuto (<paramref name="minuti"/> null, valido). Intero 0..Massimo = valido.
    /// Ritorna false per tutto il resto (negativi, decimali, separatori, testo).</summary>
    public static bool TryParse(string? testo, out int? minuti)
    {
        minuti = null;
        var pulito = testo?.Trim();
        if (string.IsNullOrEmpty(pulito)) return true;
        if (!int.TryParse(pulito, NumberStyles.None, CultureInfo.InvariantCulture, out var valore)) return false;
        if (valore < 0 || valore > MassimoMinuti) return false;
        minuti = valore;
        return true;
    }

    /// <summary>Legge la durata dal dizionario. Chiave assente, vuota o non valida = null: un valore corrotto non diventa mai un numero.</summary>
    public static int? Leggi(IReadOnlyDictionary<string, string>? valori)
    {
        if (valori == null || !valori.TryGetValue(ChiaveValoriStrumenti, out var testo)) return null;
        return TryParse(testo, out var minuti) ? minuti : null;
    }

    /// <summary>Scrive la durata nel dizionario. Null rimuove la chiave; un valore negativo o oltre il limite e' rifiutato.
    /// Le altre chiavi del dizionario non vengono toccate.</summary>
    public static void Scrivi(IDictionary<string, string> valori, int? minuti)
    {
        ArgumentNullException.ThrowIfNull(valori);
        if (minuti is null)
        {
            valori.Remove(ChiaveValoriStrumenti);
            return;
        }
        if (minuti.Value < 0 || minuti.Value > MassimoMinuti)
            throw new ArgumentOutOfRangeException(nameof(minuti), minuti.Value,
                $"La durata deve essere compresa tra 0 e {MassimoMinuti} minuti.");
        valori[ChiaveValoriStrumenti] = minuti.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Copia dei soli valori strumento: la durata non e' un campo da riportare su un'altra operazione (copia di campi,
    /// nuova iterazione, anomalia, ticket). La copia della durata passa solo dalla regola di copia (vedi TestCaseEditorViewModel).</summary>
    public static Dictionary<string, string> CopiaSenzaDurata(IReadOnlyDictionary<string, string>? sorgente)
    {
        var copia = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sorgente == null) return copia;
        foreach (var (chiave, valore) in sorgente)
            if (!EvidenzaOperazione.EChiaveRiservata(chiave)) copia[chiave] = valore;
        return copia;
    }

    /// <summary>Vero se il dizionario contiene valori degli strumenti, escludendo la durata: una durata da sola non rende un'operazione
    /// "piena" (ne' la fa considerare gia' compilata nei suggerimenti o nelle copie degli strumenti).</summary>
    public static bool HaValoriStrumenti(IReadOnlyDictionary<string, string>? valori) =>
        valori != null && valori.Keys.Any(chiave => !EvidenzaOperazione.EChiaveRiservata(chiave));

    /// <summary>Applica un passo (+5 / -5) al valore corrente. Il risultato non e' mai negativo (satura a 0).
    /// Da vuoto: il piu' porta a <see cref="PassoRapido"/>, il meno lascia il valore vuoto (non inventa uno zero).</summary>
    public static int? Passo(int? corrente, int passo)
    {
        if (corrente is null) return passo > 0 ? Math.Min(passo, MassimoMinuti) : null;
        var nuovo = (long)corrente.Value + passo;
        return (int)Math.Clamp(nuovo, 0L, MassimoMinuti);
    }

    /// <summary>Un valore di durata gia' registrato su un'operazione precedente, con l'ordine del Report (data, Id del Test
    /// in ordine ordinale, indice di revisione, indice di tentativo): serve alla regola di copia.</summary>
    public readonly record struct OsservazioneDurata(int Minuti, string Veicolo, DateTime Data, string TestId, int Revisione, int Tentativo);

    /// <summary>Regola di copia (decisione utente TIME): la durata piu' recente della stessa componente x Funzionalita x veicolo; se
    /// non c'e', la piu' recente della stessa componente x Funzionalita su un altro veicolo; altrimenti null (vuoto).
    /// Il candidato va filtrato a monte su componente e Funzionalita. Uno zero noto e' un valore valido da copiare.</summary>
    public static int? SceltaCopia(IEnumerable<OsservazioneDurata> osservazioni, string veicoloCorrente)
    {
        ArgumentNullException.ThrowIfNull(osservazioni);
        OsservazioneDurata? stessoVeicolo = null;
        OsservazioneDurata? altroVeicolo = null;
        foreach (var osservazione in osservazioni)
        {
            if (string.Equals(osservazione.Veicolo, veicoloCorrente, StringComparison.OrdinalIgnoreCase))
            {
                if (stessoVeicolo is null || Successiva(osservazione, stessoVeicolo.Value)) stessoVeicolo = osservazione;
            }
            else if (altroVeicolo is null || Successiva(osservazione, altroVeicolo.Value))
            {
                altroVeicolo = osservazione;
            }
        }
        return (stessoVeicolo ?? altroVeicolo)?.Minuti;
    }

    private static bool Successiva(OsservazioneDurata a, OsservazioneDurata b)
    {
        if (a.Data != b.Data) return a.Data > b.Data;
        var confronto = string.CompareOrdinal(a.TestId, b.TestId);
        if (confronto != 0) return confronto > 0;
        if (a.Revisione != b.Revisione) return a.Revisione > b.Revisione;
        return a.Tentativo > b.Tentativo;
    }
}
