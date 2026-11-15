using System.Text;
using System.Text.RegularExpressions;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Costruisce il link web di un ticket TICKET a partire dall'host Ticketing configurato dal Master,
/// così digitare solo "TICKET-12345" produce comunque un collegamento apribile.</summary>
public static class PolarionTicketLinkService
{
    private static readonly Regex CodiceCanonico = new("^TICKET-[A-Z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // "TICKET-123", "TICKET 123", "TICKET123", "TICKET_123" ovunque nel testo (incollato da titolo, mail, URL, elenco).
    private static readonly Regex CodiceNelTesto = new(@"(?<![A-Z0-9])TICKET(?:[\s_-]*(\d+)|-([A-Z0-9]+(?:[_-][A-Z0-9]+)*))(?![A-Z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Normalizza ciò che l'utente ha scritto/incollato in un codice canonico "TICKET-xxx". Tollera
    /// maiuscole/minuscole, spazi (anche non separabili e zero-width), trattini tipografici (en/em dash, minus),
    /// caratteri full-width, "TICKET 123"/"TICKET123", parentesi/cancelletto/punteggiatura attorno, testo extra dopo
    /// il codice (titolo incollato) e URL Ticketing (<c>...workitem?id=TICKET-123</c>). Altri prefissi (es. "XYZ-12")
    /// restano rifiutati.</summary>
    public static bool TryNormalizzaCodice(string? input, out string codice)
    {
        codice = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input.Normalize(NormalizationForm.FormKC))
        {
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u00AD') continue;
            sb.Append(ch is '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' ? '-' : ch);
        }
        var testo = sb.ToString().Trim().ToUpperInvariant();
        if (CodiceCanonico.IsMatch(testo)) { codice = testo; return true; }
        var decodificato = Uri.UnescapeDataString(testo.Replace('+', ' '));
        var m = CodiceNelTesto.Match(decodificato);
        var daId = Regex.Match(decodificato, @"[?&]ID=", RegexOptions.CultureInvariant);
        if (daId.Success) { var mi = CodiceNelTesto.Match(decodificato, daId.Index); if (mi.Success) m = mi; }
        if (!m.Success) return false;
        var cand = "TICKET-" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        if (!CodiceCanonico.IsMatch(cand)) return false;
        codice = cand;
        return true;
    }

    /// <summary>URL da aprire per un ticket: costruito SEMPRE dal numero del ticket (codice) con le impostazioni Ticketing,
    /// così un URL salvato vuoto, vecchio o incollato male non impedisce il link. L'URL salvato è solo un ripiego quando
    /// l'host Ticketing non è configurato; stringa vuota se nessuno dei due è disponibile.</summary>
    public static string RisolviUrl(DataPaths? paths, string? codice, string? urlSalvato)
    {
        var derivato = DerivaUrl(paths, codice);
        if (derivato.Length > 0) return derivato;
        return Uri.TryCreate(urlSalvato?.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : "";
    }

    /// <summary>Stessa regola di <see cref="RisolviUrl(DataPaths?, string?, string?)"/> con impostazioni già caricate (testabile).</summary>
    internal static string RisolviUrl(PolarionApiSettings settings, string? codice, string? urlSalvato)
    {
        var derivato = string.IsNullOrWhiteSpace(codice) ? "" : DerivaUrl(settings, codice);
        if (derivato.Length > 0) return derivato;
        return Uri.TryCreate(urlSalvato?.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : "";
    }

    /// <summary>Link redirect documentato da Ticketing per un work item; stringa vuota se l'host non è configurato.</summary>
    public static string DerivaUrl(DataPaths? paths, string? codice)
    {
        if (paths is null || string.IsNullOrWhiteSpace(codice)) return "";
        try { return DerivaUrl(PolarionApiSettingsService.Load(paths), codice); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    internal static string DerivaUrl(PolarionApiSettings settings, string codice)
    {
        if (!Uri.TryCreate(settings.ApiRoot, UriKind.Absolute, out var host) || host.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(settings.ProjectId) || string.IsNullOrWhiteSpace(codice))
            return "";
        return new Uri(host, "/ticketing/redirect/project/" + Uri.EscapeDataString(settings.ProjectId) +
            "/workitem?id=" + Uri.EscapeDataString(codice.Trim())).AbsoluteUri;
    }
}
