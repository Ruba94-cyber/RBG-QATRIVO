using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Test attivi da passare alla matrice Audit per un insieme di componente, con due percorsi che danno gli stessi Test nello
/// stesso ordine di QueryAll:
/// - filtro selettivo (una piccola frazione dell'archivio): QueryAll sull'indice, che materializza solo i Test abbinati;
/// - filtro non selettivo ("tutti i componenti"): lo snapshot gia' caricato in memoria (GetAll, sola lettura), filtrato e
///   ordinato senza ricreare da SQLite decine di migliaia di oggetti. Sul banco da 50k: 594 MB contro circa 11 MB.
/// L'ordine e' quello di QueryAll (ultima modifica decrescente, id senza distinzione di maiuscole, uid): cosi' anche
/// l'ordine dei candidati delle celle coincide. Gli oracoli contro SQLite sono in AuditSelezioneTestPerEcuTests.
/// </summary>
public static class AuditSelezioneTestPerEcu
{
    /// <summary>Un filtro componente che abbina piu' di questa frazione dell'archivio attivo e' non selettivo.</summary>
    public const double SogliaNonSelettiva = 0.10;

    /// <summary>
    /// Test attivi con almeno una dei componenti indicate. La scelta del percorso usa i conteggi dell'indice (archivio
    /// intero e abbinati), non la dimensione dello snapshot. <paramref name="attivi"/> non viene modificato.
    /// Un errore di lettura dell'indice viene propagato: il chiamante decide il ripiego.
    /// </summary>
    public static List<TestCase> Leggi(
        LocalTestArchiveQueryService archivio,
        IReadOnlyList<TestCase> attivi,
        IReadOnlyCollection<string> componente)
    {
        var totale = archivio.QuerySlice(new LocalTestArchiveQuery(), 0, 1).TotalCount;
        var abbinati = archivio.QuerySlice(new LocalTestArchiveQuery { AllowedEcus = componente }, 0, 1).TotalCount;
        if (abbinati > totale * SogliaNonSelettiva) return DaSnapshot(attivi, componente);
        return archivio.QueryAllAnalisi(new LocalTestArchiveQuery { AllowedEcus = componente });
    }

    /// <summary>
    /// Stessi Test di QueryAll(AllowedEcus = componente), letti dallo snapshot in memoria: stesso abbinamento componente (valori senza
    /// spazi, confronto NOCASE come l'indice) e stesso ordine. Senza componente utili non filtra, come l'indice.
    /// </summary>
    public static List<TestCase> DaSnapshot(IReadOnlyList<TestCase> attivi, IReadOnlyCollection<string> componente)
    {
        var richieste = ValoriEcuRichiesti(componente);
        var chiavi = new List<ChiaveOrdine>(attivi.Count);
        foreach (var tc in attivi)
        {
            if (richieste.Count > 0 && !AbbinaUnaDelleEcu(tc, richieste)) continue;
            chiavi.Add(new ChiaveOrdine(tc.UltimaModifica.ToUniversalTime().Ticks, tc));
        }
        chiavi.Sort(ConfrontaChiavi);
        var risultato = new List<TestCase>(chiavi.Count);
        foreach (var chiave in chiavi) risultato.Add(chiave.Tc);
        return risultato;
    }

    // Ultima modifica: stesso valore scritto nell'indice (UltimaModifica.ToUniversalTime().Ticks). Gli id e gli uid
    // seguono le regole dell'indice: COLLATE NOCASE su id, uid come testo "N" in ordine binario.
    private static int ConfrontaChiavi(ChiaveOrdine a, ChiaveOrdine b)
    {
        var c = b.Ticks.CompareTo(a.Ticks);
        if (c != 0) return c;
        c = SqliteTesto.ConfrontaNoCase(a.Tc.Id ?? "", b.Tc.Id ?? "");
        if (c != 0) return c;
        return string.CompareOrdinal(a.Tc.Uid.ToString("N"), b.Tc.Uid.ToString("N"));
    }

    private static HashSet<string> ValoriEcuRichiesti(IReadOnlyCollection<string> componente)
    {
        var valori = new HashSet<string>(SqliteTesto.ConfrontoNoCase);
        foreach (var v in componente)
            if (!string.IsNullOrWhiteSpace(v)) valori.Add(v.Trim());
        return valori;
    }

    private static bool AbbinaUnaDelleEcu(TestCase tc, HashSet<string> richieste)
    {
        foreach (var e in tc.Componente)
            if (!string.IsNullOrWhiteSpace(e) && richieste.Contains(e.Trim())) return true;
        return false;
    }

    private readonly record struct ChiaveOrdine(long Ticks, TestCase Tc);
}

/// <summary>
/// Confronti con le regole di SQLite usate dall'indice. COLLATE NOCASE piega solo le lettere ASCII; l'ordine dei byte
/// UTF-8 coincide con quello dei code point; un surrogato isolato viene scritto come U+FFFD.
/// </summary>
internal static class SqliteTesto
{
    /// <summary>Uguaglianza NOCASE di SQLite, per insiemi e dizionari (valori componente).</summary>
    public static readonly IEqualityComparer<string> ConfrontoNoCase = new UgualianzaNoCase();

    public static int ConfrontaNoCase(string a, string b)
    {
        var i = 0;
        var j = 0;
        while (i < a.Length && j < b.Length)
        {
            var ca = ProssimoCodePoint(a, ref i);
            var cb = ProssimoCodePoint(b, ref j);
            if (ca != cb) return ca < cb ? -1 : 1;
        }
        if (i < a.Length) return 1;
        if (j < b.Length) return -1;
        return 0;
    }

    private static int ProssimoCodePoint(string s, ref int indice)
    {
        var c = s[indice++];
        int codePoint;
        if (char.IsHighSurrogate(c))
        {
            if (indice < s.Length && char.IsLowSurrogate(s[indice])) codePoint = char.ConvertToUtf32(c, s[indice++]);
            else codePoint = 0xFFFD;
        }
        else if (char.IsLowSurrogate(c)) codePoint = 0xFFFD;
        else codePoint = c;
        return codePoint is >= 65 and <= 90 ? codePoint + 32 : codePoint;
    }

    private sealed class UgualianzaNoCase : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
        {
            if (x is null || y is null) return x is null && y is null;
            return ConfrontaNoCase(x, y) == 0;
        }

        public int GetHashCode(string obj)
        {
            var hash = new HashCode();
            var i = 0;
            while (i < obj.Length) hash.Add(ProssimoCodePoint(obj, ref i));
            return hash.ToHashCode();
        }
    }
}
