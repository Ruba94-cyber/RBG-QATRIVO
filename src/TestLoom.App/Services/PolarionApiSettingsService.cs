using System.Text;
using System.Text.Json.Serialization;

namespace TestLoom.App.Services;

/// <summary>
/// Master-managed shared REST settings, deliberately separate from legacy Ticketing package defaults.
/// Token obfuscation is NOT encryption or an access boundary: SharePoint readers can recover the PAT.
/// </summary>
/// <summary>Progetto Ticketing selezionabile per i collegamenti dei requisiti (id tecnico + nome leggibile).</summary>
public sealed class PolarionProgettoConfig
{
    public string Id { get; set; } = "";
    public string Nome { get; set; } = "";
}

public sealed class PolarionApiSettings
{
    public const int MaxProgetti = 50;
    public const int MaxNomeProgetto = 80;

    public long RevisioneDati { get; set; }
    public string ApiRoot { get; set; } = "";
    public string ProjectId { get; set; } = "TICKET_Tickets";
    /// <summary>Progetti Ticketing aggiuntivi (es. quelli dei requisiti). Additivo: null/assente = solo il progetto TICKET.
    /// L'TICKET (<see cref="ProjectId"/>) resta sempre il progetto predefinito di Test/NC/CR e non va elencato qui.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PolarionProgettoConfig>? Progetti { get; set; }
    public bool LetturaAbilitata { get; set; }
    /// <summary>Legacy JSON compatibility: old true values are ignored and writing is disabled.</summary>
    public bool ScritturaAbilitata { get => false; set { } }
    public bool AutomazioniSospese { get; set; } = true;
    public int IntervalloMinuti { get; set; } = 15;
    public string TokenOffuscato { get; set; } = "";
    /// <summary>Ticketing user IDs, independent from internal TestLoom operators.</summary>
    public List<string> AssegnatariEsta { get; set; } = new();
    /// <summary>Optional Master-verified browser create link (the standard REST API offers no UI prefill contract).</summary>
    public string BrowserCreateUrl { get; set; } = "";

    [JsonIgnore] public string? ErroreCaricamento { get; set; }
    [JsonIgnore] public string? SourceFingerprint { get; set; }

    public bool HaToken => !string.IsNullOrWhiteSpace(TokenOffuscato);

    /// <summary>Progetti selezionabili: prima l'TICKET predefinito (nome dall'elenco se presente, altrimenti il suo ID), poi gli
    /// altri senza duplicati (confronto senza maiuscole, prima occorrenza), solo con ID valido. Mai null, mai vuoto.</summary>
    public IReadOnlyList<PolarionProgettoConfig> ProgettiEffettivi()
    {
        var result = new List<PolarionProgettoConfig>();
        var visti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ticket = string.IsNullOrWhiteSpace(ProjectId) ? "TICKET_Tickets" : ProjectId.Trim();
        var nomeEsta = Progetti?.FirstOrDefault(p => string.Equals(p?.Id?.Trim(), ticket, StringComparison.OrdinalIgnoreCase))?.Nome;
        result.Add(new PolarionProgettoConfig { Id = ticket, Nome = string.IsNullOrWhiteSpace(nomeEsta) ? ticket : nomeEsta.Trim() });
        visti.Add(ticket);
        foreach (var p in Progetti ?? new List<PolarionProgettoConfig>())
        {
            var id = p?.Id?.Trim() ?? "";
            if (!PolarionReadClient.IsValidProjectId(id) || !visti.Add(id)) continue;
            result.Add(new PolarionProgettoConfig { Id = id, Nome = string.IsNullOrWhiteSpace(p!.Nome) ? id : p.Nome.Trim() });
        }
        return result;
    }

    /// <summary>True se <paramref name="projectId"/> e uno dei progetti selezionabili (TICKET compreso).</summary>
    public bool ProgettoConfigurato(string? projectId) =>
        !string.IsNullOrWhiteSpace(projectId) &&
        ProgettiEffettivi().Any(p => string.Equals(p.Id, projectId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Master settings editor calls this with a newly entered token; never expose it in UI bindings.</summary>
    public void ImpostaToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n'))
            throw new ArgumentException("Invalid Ticketing token.", nameof(token));
        // Deliberately weak reversible encoding requested for team-shared, non-public SharePoint.
        var source = Encoding.UTF8.GetBytes(token);
        var mask = Encoding.UTF8.GetBytes("TestLoom-ticketing-local-obfuscation-v1");
        for (var i = 0; i < source.Length; i++) source[i] ^= mask[i % mask.Length];
        TokenOffuscato = "v1:" + Convert.ToBase64String(source);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(source);
    }

    public string LeggiToken()
    {
        if (!TokenOffuscato.StartsWith("v1:", StringComparison.Ordinal))
            throw new InvalidDataException("Unknown Ticketing token encoding.");
        try
        {
            var bytes = Convert.FromBase64String(TokenOffuscato[3..]);
            var mask = Encoding.UTF8.GetBytes("TestLoom-ticketing-local-obfuscation-v1");
            for (var i = 0; i < bytes.Length; i++) bytes[i] ^= mask[i % mask.Length];
            var token = Encoding.UTF8.GetString(bytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("Empty Ticketing token.");
            return token;
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("Invalid Ticketing token encoding.", e);
        }
    }
}

public static class PolarionApiSettingsService
{
    /// <summary>Valida e normalizza l'elenco dei progetti: ID con lo stesso pattern di <see cref="PolarionReadClient"/>, nome
    /// non vuoto (default = ID) e al massimo 80 caratteri, nessun duplicato (senza maiuscole), massimo 50 voci. L'TICKET
    /// predefinito e implicito: una voce con il suo ID resta solo se porta un nome diverso dall'ID. Lista vuota -> null
    /// (nessun campo nel JSON). Errori con <see cref="InvalidOperationException"/> senza includere input esterno.</summary>
    public static List<PolarionProgettoConfig>? NormalizzaProgetti(IEnumerable<PolarionProgettoConfig>? progetti, string? progettoEsta)
    {
        if (progetti is null) return null;
        var ticket = (progettoEsta ?? "").Trim();
        var result = new List<PolarionProgettoConfig>();
        var visti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in progetti)
        {
            var id = p?.Id?.Trim() ?? "";
            var nome = p?.Nome?.Trim() ?? "";
            if (id.Length == 0 && nome.Length == 0) continue; // riga vuota dell'editor
            if (!PolarionReadClient.IsValidProjectId(id))
                throw new InvalidOperationException("Invalid Ticketing project ID in the project list.");
            if (nome.Length > PolarionApiSettings.MaxNomeProgetto)
                throw new InvalidOperationException("Ticketing project name is too long.");
            if (!visti.Add(id))
                throw new InvalidOperationException("Duplicate Ticketing project ID in the project list.");
            if (string.Equals(id, ticket, StringComparison.OrdinalIgnoreCase) && (nome.Length == 0 || nome == id)) continue;
            result.Add(new PolarionProgettoConfig { Id = id, Nome = nome.Length == 0 ? id : nome });
        }
        if (result.Count > PolarionApiSettings.MaxProgetti)
            throw new InvalidOperationException("Too many Ticketing projects.");
        return result.Count == 0 ? null : result;
    }

    public static PolarionApiSettings Load(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = paths.PolarionApiConfigFile;
        if (!File.Exists(path)) return new PolarionApiSettings();
        var settings = JsonIo.TryRead<PolarionApiSettings>(path, out var error);
        if (settings != null)
        {
            settings.SourceFingerprint = EntityStateFingerprint.Compute(settings);
            return settings;
        }
        return new PolarionApiSettings
        {
            ErroreCaricamento = "Ticketing API settings file is unreadable; changes and sync are disabled. " + error
        };
    }

    public static void Save(DataPaths paths, PolarionApiSettings settings, bool isMaster)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        if (!isMaster) throw new UnauthorizedAccessException("Only a TestLoom Master may change Ticketing API settings.");
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Ticketing API settings");
        if (settings.IntervalloMinuti < 1 || settings.IntervalloMinuti > 1440)
            throw new ArgumentOutOfRangeException(nameof(settings), "Refresh interval must be between 1 and 1440 minutes.");
        if (settings.LetturaAbilitata)
        {
            if (!Uri.TryCreate(settings.ApiRoot, UriKind.Absolute, out var root) ||
                root.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(root.UserInfo) ||
                !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment) ||
                !settings.HaToken)
                throw new InvalidOperationException("Ticketing read access needs a valid HTTPS API root and a configured token.");
            _ = settings.LeggiToken();
        }

        settings.Progetti = NormalizzaProgetti(settings.Progetti, settings.ProjectId);

        var validUsers = settings.AssegnatariEsta.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (validUsers.Count > 2 || validUsers.Any(x =>
                !System.Text.RegularExpressions.Regex.IsMatch(x, @"^[A-Za-z0-9_.-]{1,80}$")))
            throw new InvalidOperationException("Up to two valid Ticketing user IDs may be configured.");
        settings.AssegnatariEsta = validUsers;
        if (!string.IsNullOrWhiteSpace(settings.BrowserCreateUrl))
        {
            if (!Uri.TryCreate(settings.ApiRoot, UriKind.Absolute, out var api) ||
                !Uri.TryCreate(settings.BrowserCreateUrl, UriKind.Absolute, out var browser) ||
                browser.Scheme != Uri.UriSchemeHttps || browser.Host != api.Host ||
                browser.Port != api.Port || !string.IsNullOrWhiteSpace(browser.UserInfo) ||
                !browser.AbsolutePath.StartsWith("/ticketing/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Browser create URL must be on the configured HTTPS Ticketing host.");
        }

        var path = paths.PolarionApiConfigFile;
        if (File.Exists(path))
        {
            var current = JsonIo.TryRead<PolarionApiSettings>(path, out var error);
            if (current is null)
                throw new InvalidDataException("Existing Ticketing API settings are unreadable; refusing to overwrite.", error is null ? null : new InvalidDataException(error));
            if (string.IsNullOrWhiteSpace(settings.SourceFingerprint))
                throw new IOException("Settings snapshot missing; reload before saving.");
            if (current.RevisioneDati != settings.RevisioneDati ||
                !EntityStateFingerprint.Compute(current).Equals(settings.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new ConflittoRevisioneException("ticketing-api-config", settings.RevisioneDati, current.RevisioneDati,
                    current.RevisioneDati == settings.RevisioneDati);
        }
        else if (!string.IsNullOrWhiteSpace(settings.SourceFingerprint))
        {
            throw new IOException("Ticketing API settings were deleted after loading; reload before saving.");
        }

        var previous = settings.RevisioneDati;
        var previousFingerprint = settings.SourceFingerprint;
        settings.RevisioneDati = Math.Max(0, previous) + 1;
        try
        {
            JsonIo.WriteAtomic(path, settings);
            settings.SourceFingerprint = EntityStateFingerprint.Compute(settings);
        }
        catch
        {
            settings.RevisioneDati = previous;
            settings.SourceFingerprint = previousFingerprint;
            throw;
        }
    }
}
