using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// TICKET creation from a Test must be anchored in an existing NC or CR. External
/// Ticketing requirements are deliberately NOT treated as TICKET work items.
/// Does not mutate Test/NC/CR or create duplicate associations.
/// </summary>
public static class PolarionTestSourceResolver
{
    public sealed record Source(string Label, NonConformita? Nc, ChangeRequest? Cr)
    {
        public bool IsCr => Cr is not null;
        public string Id => Nc?.Id ?? Cr?.Id ?? "";
        public override string ToString() => Label;
    }

    public static IReadOnlyList<Source> Find(
        TestCase test, IEnumerable<NonConformita> nonConformita,
        IEnumerable<ChangeRequest> changeRequests)
    {
        ArgumentNullException.ThrowIfNull(test);
        var result = new List<Source>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nc in nonConformita)
        {
            var matches = nc.TestUid.HasValue && nc.TestUid.Value != Guid.Empty
                ? nc.TestUid.Value == test.Uid
                : !string.IsNullOrWhiteSpace(test.Id) &&
                    string.Equals(nc.TestCaseId, test.Id, StringComparison.OrdinalIgnoreCase);
            if (!matches || string.IsNullOrWhiteSpace(nc.Id) || !seen.Add("NC:" + nc.Uid.ToString("N"))) continue;
            result.Add(new Source("NC " + nc.Id + " · " + nc.StatoWorkflow, nc, null));
        }
        foreach (var cr in changeRequests)
        {
            if (cr.Archiviata || string.IsNullOrWhiteSpace(test.Id) ||
                !cr.TestValidazione.Any(link => string.Equals(link.TestId, test.Id, StringComparison.OrdinalIgnoreCase)) ||
                !seen.Add("CR:" + cr.Uid.ToString("N"))) continue;
            result.Add(new Source("CR " + cr.Id + " · " + cr.Stato, null, cr));
        }
        return result.OrderBy(x => x.IsCr).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
