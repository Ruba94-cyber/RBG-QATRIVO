using System.Security.Cryptography;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>Local notification interests for one TestLoom operator and workspace on this PC.</summary>
public sealed class PolarionNotificationPreferences
{
    public List<string> Gamme { get; set; } = new();
    public List<string> Veicoli { get; set; } = new();
    public bool IncludiNonAssociati { get; set; }

    /// <summary>No selected ranges/vehicles means no notifications, but all TICKET data is still synchronized.</summary>
    public bool Matches(IEnumerable<string>? affectedVehicles, IReadOnlyDictionary<string, string> gammaByVehicle)
    {
        var vehicles = (affectedVehicles ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (vehicles.Count == 0) return IncludiNonAssociati;
        if (vehicles.Any(v => Veicoli.Contains(v, StringComparer.OrdinalIgnoreCase))) return true;
        return vehicles.Any(v => gammaByVehicle.TryGetValue(v, out var gamma) &&
            Gamme.Contains(gamma, StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class PolarionNotificationPreferencesService
{
    private readonly string _file;

    public PolarionNotificationPreferencesService(DataPaths paths, string operatorId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(operatorId)) throw new ArgumentException("Operator ID required.", nameof(operatorId));
        var operatorHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operatorId.Trim().ToUpperInvariant())))[..24];
        _file = Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "workspaces",
            WorkspaceLocalPaths.ComputeWorkspaceKey(paths.Root), "ticketing-notification-filters",
            operatorHash + ".json");
    }

    public PolarionNotificationPreferences Load()
    {
        if (!File.Exists(_file)) return new PolarionNotificationPreferences();
        return JsonIo.TryRead<PolarionNotificationPreferences>(_file, out var error) ??
            throw new InvalidDataException("Local TICKET notification preferences unreadable; refusing to overwrite.",
                error is null ? null : new InvalidDataException(error));
    }

    public void Save(PolarionNotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        // The values come from TestLoom catalog selections, never external Ticketing field values.
        preferences.Gamme = Normalize(preferences.Gamme);
        preferences.Veicoli = Normalize(preferences.Veicoli);
        if (File.Exists(_file))
            _ = Load(); // fail closed on corrupt file
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        JsonIo.WriteAtomic(_file, preferences);
    }

    private static List<string> Normalize(IEnumerable<string> source) =>
        source.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
}
