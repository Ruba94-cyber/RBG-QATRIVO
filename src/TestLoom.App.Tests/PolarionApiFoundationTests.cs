using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionApiFoundationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PolarionApi_" + Guid.NewGuid().ToString("N"));
    private DataPaths Paths()
    {
        Directory.CreateDirectory(_root);
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        return paths;
    }

    [Fact]
    public void SharedTokenIsObfuscatedAndMasterOnly()
    {
        var paths = Paths();
        var config = new PolarionApiSettings();
        config.ImpostaToken("test-token-do-not-use");
        config.ApiRoot = "https://example.invalid/ticketing/rest/v1";
        config.LetturaAbilitata = true;
        Assert.Throws<UnauthorizedAccessException>(() => PolarionApiSettingsService.Save(paths, config, false));
        Assert.False(File.Exists(paths.PolarionApiConfigFile));
        PolarionApiSettingsService.Save(paths, config, true);
        var data = File.ReadAllText(paths.PolarionApiConfigFile);
        Assert.DoesNotContain("test-token-do-not-use", data);
        var reread = PolarionApiSettingsService.Load(paths);
        Assert.Equal("test-token-do-not-use", reread.LeggiToken());
        Assert.True(reread.AutomazioniSospese);
        Assert.Equal(15, reread.IntervalloMinuti);
    }

    [Fact]
    public void LegacyWriteFlagCanNeverAuthorizeApiWrites()
    {
        var paths = Paths();
        var config = new PolarionApiSettings();
        Assert.False(config.ScritturaAbilitata);
        config.ScritturaAbilitata = true;
        Assert.False(config.ScritturaAbilitata);
        Assert.Throws<UnauthorizedAccessException>(() => PolarionApiSettingsService.Save(paths, config, false));
        Assert.False(File.Exists(paths.PolarionApiConfigFile));
        config.ImpostaToken("synthetic-token");
        config.ApiRoot = "https://example.invalid/ticketing/rest/v1";
        config.LetturaAbilitata = true;
        PolarionApiSettingsService.Save(paths, config, true);
        Assert.False(PolarionApiSettingsService.Load(paths).ScritturaAbilitata);
        // Parse and reserialize an actual legacy configuration with the old write flag enabled.
        var oldJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.PolarionApiConfigFile))!.AsObject();
        oldJson["ScritturaAbilitata"] = true;
        File.WriteAllText(paths.PolarionApiConfigFile, oldJson.ToJsonString());
        Assert.True(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.PolarionApiConfigFile))!["ScritturaAbilitata"]!.GetValue<bool>());
        Assert.False(PolarionApiSettingsService.Load(paths).ScritturaAbilitata);
    }

    [Fact]
    public void MasterMayConfigureTwoExternalAssigneesAndSameHostBrowserLink()
    {
        var paths = Paths();
        var cfg = new PolarionApiSettings
        {
            ApiRoot = "https://example.invalid/ticketing/rest/v1",
            BrowserCreateUrl = "https://example.invalid/ticketing/redirect/project/TICKET_Tickets/workitems",
            AssegnatariEsta = ["user.one", "user.two"]
        };
        PolarionApiSettingsService.Save(paths, cfg, true);
        Assert.Equal(2, PolarionApiSettingsService.Load(paths).AssegnatariEsta.Count);
    }

    [Fact]
    public void BrowserCreateUrlCannotPointOutsidePolarionHost()
    {
        var paths = Paths();
        var cfg = new PolarionApiSettings
        {
            ApiRoot = "https://example.invalid/ticketing/rest/v1",
            BrowserCreateUrl = "https://evil.invalid/ticketing/"
        };
        Assert.Throws<InvalidOperationException>(() => PolarionApiSettingsService.Save(paths, cfg, true));
    }

    [Fact]
    public void TwoMastersCannotOverwriteSettingsFromStaleBaseline()
    {
        var paths = Paths();
        PolarionApiSettingsService.Save(paths, new PolarionApiSettings(), true);
        var a = PolarionApiSettingsService.Load(paths);
        var b = PolarionApiSettingsService.Load(paths);
        b.IntervalloMinuti = 20;
        PolarionApiSettingsService.Save(paths, b, true);
        a.IntervalloMinuti = 10;
        Assert.Throws<ConflittoRevisioneException>(() => PolarionApiSettingsService.Save(paths, a, true));
        Assert.Equal(20, PolarionApiSettingsService.Load(paths).IntervalloMinuti);
    }

    [Fact]
    public void InvalidExistingConfigCannotBeOverwritten()
    {
        var paths = Paths();
        File.WriteAllText(paths.PolarionApiConfigFile, "{ not valid");
        var fallback = PolarionApiSettingsService.Load(paths);
        Assert.NotNull(fallback.ErroreCaricamento);
        Assert.Throws<InvalidDataException>(() => PolarionApiSettingsService.Save(paths, fallback, true));
    }

    [Fact]
    public void CacheIsSharedByTicketAndDoesNotOverwriteNewerSnapshots()
    {
        var paths = Paths();
        var cache = new PolarionSnapshotStore(paths);
        var first = new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "assigned",
            Updated = DateTimeOffset.Parse("2026-10-29T12:00:00Z"),
            RetrievedAtUtc = DateTimeOffset.Parse("2026-10-29T12:01:00Z"),
            Comments = new() { new PolarionCommentSnapshot { Id = "TICKET_Tickets/TICKET-123/1", Text = "Sample" } }
        };
        Assert.True(cache.SaveIfNewer(first));
        Assert.Equal("assigned", cache.Load("TICKET_Tickets", "TICKET-123")!.Status);
        var stale = new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "new",
            Updated = DateTimeOffset.Parse("2026-10-29T11:00:00Z"),
            RetrievedAtUtc = DateTimeOffset.Parse("2026-10-29T12:02:00Z")
        };
        Assert.False(cache.SaveIfNewer(stale));
        Assert.Equal("Sample", Assert.Single(cache.Load("TICKET_Tickets", "TICKET-123")!.Comments).Text);
        Assert.Throws<ArgumentException>(() => cache.Load("TICKET_Tickets", "../TICKET-123"));
    }

    [Fact]
    public void ReadOnlySnapshotStoreCannotWriteSharedCache()
    {
        var paths = Paths();
        var snapshot = new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123",
            RetrievedAtUtc = DateTimeOffset.UtcNow
        };
        Assert.False(new PolarionSnapshotStore(paths, sharedWriteAllowed: false).SaveIfNewer(snapshot));
        Assert.Null(new PolarionSnapshotStore(paths).Load("TICKET_Tickets", "TICKET-123"));
    }

    [Fact]
    public async Task ParallelCacheWritesWithinOneProcessPreserveMostRecentVerifiedRevision()
    {
        var paths = Paths();
        var cache = new PolarionSnapshotStore(paths);
        var snapshots = Enumerable.Range(1, 24).Select(i => new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "testing",
            Updated = DateTimeOffset.Parse("2026-10-29T12:00:00Z").AddMinutes(i),
            RetrievedAtUtc = DateTimeOffset.Parse("2026-10-29T13:00:00Z").AddMinutes(i)
        }).ToArray();
        await Task.WhenAll(snapshots.Reverse().Select(x => Task.Run(() => cache.SaveIfNewer(x))));
        var saved = cache.Load("TICKET_Tickets", "TICKET-123");
        Assert.Equal(snapshots[^1].Updated, saved!.Updated);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
