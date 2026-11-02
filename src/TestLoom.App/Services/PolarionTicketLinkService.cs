using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Costruisce il link web di un ticket TICKET a partire dall'host Ticketing configurato dal Master,
/// così digitare solo "TICKET-12345" produce comunque un collegamento apribile.</summary>
public static class PolarionTicketLinkService
{
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
