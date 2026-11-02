using TestLoom.App.Services;

namespace TestLoom.App.Tests;

[Collection("TracciaStatica")]
public sealed class UiInteractionTraceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_UiTrace_" + Guid.NewGuid().ToString("N"));

    public UiInteractionTraceServiceTests()
    {
        Directory.CreateDirectory(_root);
        UiInteractionTraceService.DirectoryForTest = _root;
    }

    [Fact]
    public void StoresOnlyActionKindAndSafeIdentifierInLocalDailyLog()
    {
        UiInteractionTraceService.Record("Test", "text-edited", "Search_Filter");
        var file = Assert.Single(UiInteractionTraceService.FilePresenti());
        Assert.Contains("[UI] Test text-edited Search_Filter", File.ReadAllText(file));
        Assert.Contains("ui-" + DateTime.Now.ToString("yyyy-MM-dd"), Path.GetFileName(file));
    }

    [Fact]
    public void TokenPinPasswordIdentifiersAreRedacted()
    {
        UiInteractionTraceService.Record("Settings", "text-edited", "PolarionApi_Token_TextBox");
        var text = string.Join("", UiInteractionTraceService.FilePresenti().Select(File.ReadAllText));
        Assert.Contains("sensitive-field", text);
        Assert.DoesNotContain("Token_TextBox", text);
    }


    [Fact]
    public void PrunesOldLogsAfterSwitchingToAnotherIsolatedDirectory()
    {
        UiInteractionTraceService.Record("Test", "click", "first");
        var isolated = Path.Combine(_root, "other");
        Directory.CreateDirectory(isolated);
        var obsolete = Path.Combine(isolated, "ui-2000-01-01.log");
        File.WriteAllText(obsolete, "obsolete");
        UiInteractionTraceService.DirectoryForTest = isolated;

        UiInteractionTraceService.Record("Test", "selection-index-3", "Status_Filter");

        Assert.False(File.Exists(obsolete));
        var onlyCurrent = Assert.Single(UiInteractionTraceService.FilePresenti());
        Assert.Contains("selection-index-3 Status_Filter", File.ReadAllText(onlyCurrent));
    }

    [Fact]
    public void DailyUiLogRotatesAtBoundedSizeWithoutLosingCurrentAction()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var current = Path.Combine(_root, "ui-" + today + ".log");
        File.WriteAllText(current, new string('x', 1024 * 1024));

        UiInteractionTraceService.Record("Test", "toggle-on", "Status_Filter");

        var previous = Path.Combine(_root, "ui-" + today + ".1.log");
        Assert.True(File.Exists(previous));
        Assert.True(new FileInfo(current).Length < 1024 * 1024);
        Assert.Contains("[UI] Test toggle-on Status_Filter", File.ReadAllText(current));
        Assert.Contains(previous, UiInteractionTraceService.FilePresenti());
    }

    public void Dispose()
    {
        UiInteractionTraceService.DirectoryForTest = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
