using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TestLoom.App.Services;

/// <summary>Una REV del Test unito: contenuto, numero finale e provenienza (nessuna REV viene scartata).</summary>
public sealed record RevUnita(JsonObject Rev, int NumeroNuovo, int? NumeroCopiaMia, int? NumeroAltraCopia, DateTime Data, string Operatore)
{
    /// <summary>"comune" (identica nelle due copie), "questo PC" o "l'altro PC".</summary>
    public string Origine => NumeroCopiaMia.HasValue && NumeroAltraCopia.HasValue ? "comune"
        : NumeroCopiaMia.HasValue ? "questo PC" : "l'altro PC";
}

/// <summary>
/// Piano di unione delle REV di due copie dello stesso Test (API per la UI): REV risultanti in ordine, mappa vecchio->nuovo numero
/// per ciascuna copia e, se l'unione automatica non e' possibile, il motivo (la UI chiede allora una scelta semplice).
/// </summary>
public sealed class TestRevisionMergePlan
{
    public bool Unibile { get; init; }
    /// <summary>Motivo per cui serve una decisione manuale (data mancante/non valida...). Null se Unibile.</summary>
    public string? MotivoDecisioneManuale { get; init; }
    public IReadOnlyList<RevUnita> Revisioni { get; init; } = Array.Empty<RevUnita>();
    /// <summary>Numero REV nella copia "mia" (corrente) -> numero nel Test unito.</summary>
    public IReadOnlyDictionary<int, int> MappaCopiaMia { get; init; } = new Dictionary<int, int>();
    /// <summary>Numero REV nell'altra copia -> numero nel Test unito.</summary>
    public IReadOnlyDictionary<int, int> MappaAltraCopia { get; init; } = new Dictionary<int, int>();
    /// <summary>Descrizione leggibile (per cronologia/Audit): REV unite, copia di origine, data, rinumerazioni.</summary>
    public string Nota { get; init; } = "";

    public JsonArray ToJsonArray()
    {
        var a = new JsonArray();
        foreach (var r in Revisioni)
        {
            var c = (JsonObject)r.Rev.DeepClone();
            c["Iterazione"] = r.NumeroNuovo;
            a.Add(c);
        }
        return a;
    }
}

/// <summary>Un punto che richiede una scelta dell'utente: campo, valore di ciascuna copia, autore e data di ciascuna.</summary>
public sealed record DecisioneManuale(string Campo, string Etichetta, string ValoreCopiaMia, string ValoreAltraCopia,
    string AutoreCopiaMia, DateTime? DataCopiaMia, string AutoreAltraCopia, DateTime? DataAltraCopia);

/// <summary>
/// Unione delle REV (Esecuzioni) di due versioni dello stesso Test. Regola: nessuna REV viene persa. Le REV identiche nelle due
/// copie (stessa data, operatore e contenuto) restano una sola; ogni altra REV - presente in una sola copia, oppure con la stessa
/// identita' (data+operatore) ma contenuto diverso - e' tenuta come REV a se', e tutte sono ordinate per data di creazione e
/// rinumerate di seguito. A parita' di data l'ordine e' deterministico e indipendente dalla copia (operatore, poi hash del
/// contenuto). La REV piu' recente diventa quella corrente. Se una data manca o non e' valida l'ordine non e' ricavabile: NON si
/// unisce e resta la scelta esplicita.
/// </summary>
public static class TestRevisionMerge
{
    public static bool TryUnisci(JsonObject mia, JsonObject altra, out JsonArray? unite, out string? nota)
    {
        unite = null;
        nota = null;
        var piano = Pianifica(mia, altra);
        if (piano == null || !piano.Unibile) return false;
        unite = piano.ToJsonArray();
        nota = piano.Nota;
        return true;
    }

    /// <summary>Piano di unione, oppure null se le liste REV non servono unione (assenti o gia' uguali). Un piano con
    /// <see cref="TestRevisionMergePlan.Unibile"/> falso segnala che serve una decisione manuale.</summary>
    public static TestRevisionMergePlan? Pianifica(JsonObject mia, JsonObject altra)
    {
        if (mia["Esecuzioni"] is not JsonArray a || altra["Esecuzioni"] is not JsonArray b) return null;
        if (JsonNode.DeepEquals(a, b)) return null;

        var elA = Elenca(a, out var erroreA);
        var elB = Elenca(b, out var erroreB);
        if (elA == null || elB == null)
            return new TestRevisionMergePlan
            {
                Unibile = false,
                MotivoDecisioneManuale = "Le REV non si possono ordinare automaticamente: " + (erroreA ?? erroreB) + "."
            };

        var usatiB = new bool[elB.Count];
        var voci = new List<(JsonObject O, int? NA, int? NB, DateTime D, string Op, string Hash)>();
        foreach (var x in elA)
        {
            var j = -1;
            for (var i = 0; i < elB.Count; i++)
                if (!usatiB[i] && elB[i].Hash == x.Hash && elB[i].Data == x.Data && elB[i].Op == x.Op) { j = i; break; }
            if (j >= 0) { usatiB[j] = true; voci.Add((x.O, x.Num, elB[j].Num, x.Data, x.Op, x.Hash)); }
            else voci.Add((x.O, x.Num, null, x.Data, x.Op, x.Hash));
        }
        for (var i = 0; i < elB.Count; i++)
            if (!usatiB[i]) voci.Add((elB[i].O, null, elB[i].Num, elB[i].Data, elB[i].Op, elB[i].Hash));

        // Ordine per data di creazione; a parita' operatore e hash (stabile, indipendente da quale copia sia "mia").
        // Se le REV comuni hanno lo stesso numero nelle due copie restano FISSE (nessuna rinumerazione di cio' che e' gia' condiviso
        // e referenziato) e le altre seguono per data; altrimenti ordine per data per tutte.
        var comuniVoci = voci.Where(v => v.NA.HasValue && v.NB.HasValue).ToList();
        var fissi = comuniVoci.All(v => v.NA == v.NB);
        var ordineDate = voci.OrderBy(v => v.D).ThenBy(v => v.Op, StringComparer.Ordinal).ThenBy(v => v.Hash, StringComparer.Ordinal).ToList();
        var ordinate = !fissi ? ordineDate
            : comuniVoci.OrderBy(v => v.NA!.Value).Concat(ordineDate.Where(v => !(v.NA.HasValue && v.NB.HasValue))).ToList();
        var ultimaComune = comuniVoci.Count == 0 ? DateTime.MinValue : comuniVoci.Max(v => v.D);
        var nuoveAnteriori = fissi && voci.Any(v => !(v.NA.HasValue && v.NB.HasValue) && v.D < ultimaComune);
        var revisioni = new List<RevUnita>();
        var mapA = new Dictionary<int, int>();
        var mapB = new Dictionary<int, int>();
        var cambi = new List<string>();
        var n = 0;
        foreach (var v in ordinate)
        {
            n++;
            var r = new RevUnita(v.O, n, v.NA, v.NB, v.D, v.Op);
            revisioni.Add(r);
            if (v.NA.HasValue) mapA[v.NA.Value] = n;
            if (v.NB.HasValue) mapB[v.NB.Value] = n;
            var rinumerata = (v.NA.HasValue && v.NA != n) || (v.NB.HasValue && v.NB != n);
            if (rinumerata || r.Origine != "comune")
                cambi.Add($"REV {(v.NA ?? v.NB)} ({r.Origine}, {v.D:dd/MM/yyyy HH:mm}, {v.Op}) -> REV {n}" +
                          (r.Origine == "comune" && v.NA != v.NB ? $" (nell'altro PC era REV {v.NB})" : ""));
        }
        var soloUna = revisioni.Count(r => r.NumeroCopiaMia.HasValue != r.NumeroAltraCopia.HasValue);
        return new TestRevisionMergePlan
        {
            Unibile = true,
            Revisioni = revisioni,
            MappaCopiaMia = mapA,
            MappaAltraCopia = mapB,
            Nota = $"REV unite: {revisioni.Count - soloUna} comuni, {soloUna} presenti in una sola copia e tenute (totale {revisioni.Count}), ordinate per data" +
                   (cambi.Count > 0 ? "; numerazione: " + string.Join(", ", cambi) : "") +
                   (nuoveAnteriori ? "; alcune REV nuove hanno data anteriore all'ultima REV comune: messe dopo per non rinumerare le REV gia' condivise" : "")
        };
    }

    private sealed record Voce(JsonObject O, int Num, DateTime Data, string Op, string Hash);

    private static List<Voce>? Elenca(JsonArray arr, out string? errore)
    {
        errore = null;
        var res = new List<Voce>();
        foreach (var node in arr)
        {
            if (node is not JsonObject o) { errore = "una REV non e' leggibile"; return null; }
            var d = Data(o);
            if (d == DateTime.MinValue) { errore = $"la REV {Iter(o)} non ha una data di creazione valida"; return null; }
            res.Add(new Voce(o, Iter(o), d, o["Operatore"]?.ToString() ?? "", Hash(Senza(o))));
        }
        return res;
    }

    private static string Hash(JsonNode n) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonico(n)))).ToLowerInvariant();

    private static string Canonico(JsonNode? n) => n switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => "\"" + p.Key + "\":" + Canonico(p.Value))) + "}",
        JsonArray arr => "[" + string.Join(",", arr.Select(Canonico)) + "]",
        null => "null",
        _ => n.ToJsonString()
    };

    /// <summary>
    /// Quando le REV NON si possono unire (data mancante/non valida, dati non interpretabili) spiega all'utente il motivo e quali REV
    /// esistono in una sola copia (scegliendo l'altra lista non verrebbero tenute; la versione non scelta va comunque nel
    /// recovery-bin). Null se le liste sono uguali o unibili.
    /// </summary>
    public static string? DescriviConflitto(JsonObject mia, JsonObject altra)
    {
        if (mia["Esecuzioni"] is not JsonArray a || altra["Esecuzioni"] is not JsonArray b || JsonNode.DeepEquals(a, b)) return null;
        var piano = Pianifica(mia, altra);
        if (piano is { Unibile: true }) return null;
        var motivo = piano?.MotivoDecisioneManuale ?? "Le REV non si possono unire automaticamente.";
        return motivo + " Scegliere la lista di una delle due versioni: l'altra va nel recovery-bin, nulla viene cancellato. " +
               $"Questa versione: {a.Count} REV; l'altra: {b.Count} REV.";
    }

    private static DateTime Data(JsonObject o) =>
        DateTime.TryParse(o["Data"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue;

    private static int Iter(JsonObject o) =>
        int.TryParse(o["Iterazione"]?.ToString(), CultureInfo.InvariantCulture, out var i) ? i : 0;

    /// <summary>Contenuto della REV senza il numero d'ordine (una REV comune puo' avere numeri diversi solo per rinumerazioni).</summary>
    private static JsonNode Senza(JsonObject o)
    {
        var c = (JsonObject)o.DeepClone();
        c.Remove("Iterazione");
        return c;
    }
}
