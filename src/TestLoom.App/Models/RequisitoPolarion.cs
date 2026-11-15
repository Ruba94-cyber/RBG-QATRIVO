using System.Text.RegularExpressions;
using TestLoom.App.Services;

namespace TestLoom.App.Models;

/// <summary>Collegamento di un Requisito a un work item Ticketing: progetto + ID (es. "REQ_PROJ" / "REQ-123").</summary>
public readonly record struct RequisitoPolarionLink(string Progetto, string WorkItemId)
{
    /// <summary>Forma testuale di interscambio (Excel, campo di testo): "PROGETTO/ID".</summary>
    public override string ToString() => Progetto + RequisitoPolarionRegole.Separatore + WorkItemId;
}

/// <summary>
/// Regole uniche per il collegamento Requisito -> Ticketing: validazione (stessi pattern di
/// <see cref="PolarionReadClient"/>), parsing tollerante di quello che l'utente incolla e URL del browser.
/// Puro e senza I/O: usato da editor, import/export Excel, impostazioni e sincronizzazione.
/// </summary>
public static class RequisitoPolarionRegole
{
    public const char Separatore = '/';

    // Esempio: https://host/ticketing/#/project/REQ_PROJ/workitem?id=REQ-123
    // oppure   https://host/ticketing/redirect/project/REQ_PROJ/workitem?id=REQ-123
    private static readonly Regex UrlPattern = new(
        @"/project/(?<p>[A-Za-z][A-Za-z0-9_]*)/workitem\?(?:[^#]*&)?id=(?<i>[A-Za-z][A-Za-z0-9_]*-[0-9]+)(?![0-9A-Za-z_])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool ProgettoValido(string? progetto) => PolarionReadClient.IsValidProjectId(progetto?.Trim());

    public static bool WorkItemValido(string? id) => PolarionReadClient.IsValidWorkItemId(id?.Trim());

    /// <summary>Collegamento valido e completo di un requisito, oppure null (nessun collegamento o dati incompleti/non validi).</summary>
    public static RequisitoPolarionLink? Di(Requisito? requisito)
    {
        if (requisito is null) return null;
        var p = requisito.PolarionProgetto?.Trim();
        var i = requisito.PolarionWorkItemId?.Trim();
        return ProgettoValido(p) && WorkItemValido(i) ? new RequisitoPolarionLink(p!, i!.ToUpperInvariant()) : null;
    }

    /// <summary>
    /// Interpreta il testo inserito dall'utente. Formati accettati: "PROGETTO/ID", un URL Ticketing con
    /// <c>/project/PROGETTO/workitem?id=ID</c>, oppure il solo "ID" se è indicato un progetto predefinito.
    /// Il testo vuoto non è un errore: significa "nessun collegamento" e restituisce (true, null).
    /// </summary>
    public static bool TryParse(string? testo, string? progettoPredefinito, out RequisitoPolarionLink? link, out string? errore)
    {
        link = null; errore = null;
        var t = (testo ?? "").Trim();
        if (t.Length == 0) return true;

        var url = UrlPattern.Match(t);
        if (url.Success)
        {
            link = new RequisitoPolarionLink(url.Groups["p"].Value, url.Groups["i"].Value.ToUpperInvariant());
            return true;
        }

        var parti = t.Split(Separatore, StringSplitOptions.TrimEntries);
        string progetto, id;
        if (parti.Length == 2) { progetto = parti[0]; id = parti[1]; }
        else if (parti.Length == 1 && !string.IsNullOrWhiteSpace(progettoPredefinito)) { progetto = progettoPredefinito.Trim(); id = parti[0]; }
        else { errore = "formato"; return false; }

        if (!ProgettoValido(progetto)) { errore = "progetto"; return false; }
        if (!WorkItemValido(id)) { errore = "id"; return false; }
        link = new RequisitoPolarionLink(progetto, id.ToUpperInvariant());
        return true;
    }

    /// <summary>Applica (o rimuove, con null) il collegamento su un requisito, normalizzando i campi vuoti a null.</summary>
    public static void Imposta(Requisito requisito, RequisitoPolarionLink? link)
    {
        ArgumentNullException.ThrowIfNull(requisito);
        requisito.PolarionProgetto = link?.Progetto;
        requisito.PolarionWorkItemId = link?.WorkItemId;
    }

    /// <summary>URL del work item nel browser (sul host dell'API root), o null se l'API root non è HTTPS valida.</summary>
    public static Uri? UrlBrowser(string? apiRoot, RequisitoPolarionLink link)
    {
        if (!Uri.TryCreate(apiRoot, UriKind.Absolute, out var api) || api.Scheme != Uri.UriSchemeHttps) return null;
        if (!ProgettoValido(link.Progetto) || !WorkItemValido(link.WorkItemId)) return null;
        return new Uri(new Uri(api.GetLeftPart(UriPartial.Authority)),
            "/ticketing/redirect/project/" + Uri.EscapeDataString(link.Progetto) +
            "/workitem?id=" + Uri.EscapeDataString(link.WorkItemId));
    }

    /// <summary>Elenco (senza duplicati) dei collegamenti validi dei requisiti NON archiviati, ordinato in modo stabile.</summary>
    public static IReadOnlyList<RequisitoPolarionLink> CollegamentiAttivi(IEnumerable<Requisito> requisiti)
    {
        ArgumentNullException.ThrowIfNull(requisiti);
        var set = new HashSet<RequisitoPolarionLink>(ConfrontoSenzaMaiuscole.Instance);
        foreach (var r in requisiti)
        {
            if (r.Archiviata) continue;
            if (Di(r) is { } l) set.Add(l);
        }
        return set.OrderBy(l => l.Progetto, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.WorkItemId, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Uguaglianza dei collegamenti senza distinguere le maiuscole (Ticketing e il file system le trattano allo stesso modo).</summary>
public sealed class ConfrontoSenzaMaiuscole : IEqualityComparer<RequisitoPolarionLink>
{
    public static readonly ConfrontoSenzaMaiuscole Instance = new();
    public bool Equals(RequisitoPolarionLink x, RequisitoPolarionLink y) =>
        string.Equals(x.Progetto, y.Progetto, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.WorkItemId, y.WorkItemId, StringComparison.OrdinalIgnoreCase);
    public int GetHashCode(RequisitoPolarionLink l) => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(l.Progetto), StringComparer.OrdinalIgnoreCase.GetHashCode(l.WorkItemId));
}

/// <summary>Famiglia di stato Ticketing usata per colore e filtro (indipendente dal vocabolario esatto del progetto).</summary>
public enum PolarionStatoClasse
{
    /// <summary>Il requisito non ha un collegamento Ticketing.</summary>
    NonCollegato = 0,
    /// <summary>Collegato ma nessuno stato letto (non ancora sincronizzato, offline, progetto non configurato).</summary>
    NonDisponibile = 1,
    Aperto = 2,
    InLavorazione = 3,
    Chiuso = 4
}

/// <summary>Classificazione degli stati Ticketing (stessa logica del colore dello stato TICKET nelle tabelle NC/CR).</summary>
public static class PolarionStatoClassificatore
{
    public static PolarionStatoClasse Classifica(string? status)
    {
        var t = (status ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return PolarionStatoClasse.NonDisponibile;
        if (t.Contains("clos") || t.Contains("chius") || t.Contains("resol") || t.Contains("verif") || t.Contains("done") || t.Contains("rejected") || t.Contains("rifiut")
            || t.Contains("approv") || t.Contains("accept") || t.Contains("complet") || t.Contains("releas") || t.Contains("obsolet"))
            return PolarionStatoClasse.Chiuso;
        if (t.Contains("test") || t.Contains("progress") || t.Contains("lavoraz") || t.Contains("review") || t.Contains("analy") || t.Contains("implement"))
            return PolarionStatoClasse.InLavorazione;
        return PolarionStatoClasse.Aperto;
    }
}
