using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Un TICKET collegato a una NC: proprio della NC oppure ereditato dal Test a cui la NC appartiene.</summary>
public sealed record EstaLinkEffettivo(string Codice, string Url, bool DalTest)
{
    public string Tooltip => DalTest ? LocalizationService.Instance.Traduci("EstaLinkDalTestTooltip") : "";

    /// <summary>Apribile dalla tabella: senza URL salvato l'indirizzo si ricava dal codice; con un URL salvato deve essere web (http/https).</summary>
    public bool Apribile => string.IsNullOrWhiteSpace(Url) || UrlWebValidoConverter.IsWebUrl(Url);
}

/// <summary>
/// Regola unica per gli TICKET mostrati/esportati/ordinati per una NC: l'unione dei link propri della NC e dei ticket del Test
/// collegato, senza duplicati (confronto sul codice, senza distinguere maiuscole), prima i propri. Prima la regola era "o l'uno o
/// l'altro" e copiata in più punti: un TICKET presente solo nel Test spariva appena la NC ne aveva uno proprio.
/// Nessun dato viene scritto: i link restano dove sono (NC.CollegamentiPolarion, TestCase.TicketCollegati).
/// </summary>
public static class NcEstaLinks
{
    public static IReadOnlyList<EstaLinkEffettivo> Effettivi(NonConformita nc, TestCase? test)
    {
        var risultato = new List<EstaLinkEffettivo>();
        var visti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in nc.CollegamentiPolarion)
        {
            var codice = (l.Codice ?? "").Trim();
            if (codice.Length > 0 && visti.Add(codice)) risultato.Add(new EstaLinkEffettivo(codice, l.Url ?? "", DalTest: false));
        }
        if (test != null)
            foreach (var t in test.TicketCollegati)
            {
                var codice = (t.Riferimento ?? "").Trim();
                if (codice.Length > 0 && visti.Add(codice)) risultato.Add(new EstaLinkEffettivo(codice, t.Url ?? "", DalTest: true));
            }
        return risultato;
    }

    public static string Testo(NonConformita nc, TestCase? test, string vuoto = "—")
    {
        var link = Effettivi(nc, test);
        return link.Count == 0 ? vuoto : string.Join(", ", link.Select(l => l.Codice));
    }
}
