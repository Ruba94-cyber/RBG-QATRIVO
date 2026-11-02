using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Ogni AppState di test usa un config isolato: la suite non deve mai leggere, scrivere o bloccare
/// il vero %APPDATA% dell'utente e i test paralleli non devono contendersi lo stesso config.json.</summary>
internal static class TestStateFactory
{
    public static AppState Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_TestConfig", Guid.NewGuid().ToString("N"));
        return new AppState(new AppConfigService(Path.Combine(dir, "config.json")), runBackgroundMaintenance: false);
    }
}
