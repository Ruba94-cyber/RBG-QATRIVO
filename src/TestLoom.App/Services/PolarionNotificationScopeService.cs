using System.Text.RegularExpressions;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Decides which of an operator's linked TICKET tickets may produce local unread events.
/// Synced tickets and notification subscriptions intentionally have different scopes.
/// </summary>
public static class PolarionNotificationScopeService
{
    public enum Mode { Silent, Full, ReopenOnly, Passive }

    private static readonly Regex IdPattern = new(@"(?<![A-Za-z0-9])TICKET-[0-9]+(?![0-9])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Builds one policy per TICKET ID, even when the same ticket belongs to multiple NC/CR.
    /// An active subscribed association wins over closed associations; unselected and
    /// unassociated records cannot generate notifications unless explicitly subscribed.
    /// </summary>
    public static IReadOnlyDictionary<string, Mode> Build(
        IEnumerable<NonConformita> ncRecords,
        IEnumerable<ChangeRequest> crRecords,
        Func<string, IReadOnlyList<string>> ncTestVehicles,
        PolarionNotificationPreferences preferences,
        IReadOnlyDictionary<string, string> gammaByVehicle)
    {
        ArgumentNullException.ThrowIfNull(ncRecords);
        ArgumentNullException.ThrowIfNull(crRecords);
        ArgumentNullException.ThrowIfNull(ncTestVehicles);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(gammaByVehicle);

        var result = new Dictionary<string, Mode>(StringComparer.OrdinalIgnoreCase);
        foreach (var nc in ncRecords)
        {
            if (nc.CollegamentiPolarion.Count == 0) continue;
            var vehicles = nc.VeicoliSegnalati
                .Concat(!string.IsNullOrWhiteSpace(nc.TestCaseId) ? ncTestVehicles(nc.TestCaseId) : Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var subscribed = preferences.Matches(vehicles, gammaByVehicle);
            foreach (var link in nc.CollegamentiPolarion)
                Add(link.Codice, link.Url, subscribed,
                    nc.StatoWorkflow == StatoAnomalia.Conclusa);
        }

        foreach (var cr in crRecords)
        {
            if (cr.Archiviata || cr.TicketCollegati.Count == 0) continue;
            var subscribed = preferences.Matches(cr.VeicoliImpattati, gammaByVehicle);
            foreach (var link in cr.TicketCollegati)
                Add(link.Riferimento, link.Url, subscribed,
                    cr.Stato is StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata);
        }
        return result;

        void Add(string? reference, string? url, bool subscribed, bool closed)
        {
            var match = IdPattern.Match(reference ?? "");
            if (!match.Success) match = IdPattern.Match(url ?? "");
            if (!match.Success) return;
            var id = match.Value.ToUpperInvariant();
            var mode = !subscribed ? Mode.Silent : closed ? Mode.ReopenOnly : Mode.Full;
            if (!result.TryGetValue(id, out var existing) || Rank(mode) > Rank(existing))
                result[id] = mode;
        }
    }

    private static int Rank(Mode mode) => mode switch
    {
        Mode.Full => 3,
        Mode.ReopenOnly => 2,
        Mode.Passive => 1,
        _ => 0
    };
}
