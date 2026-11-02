using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionNotificationPreferencesTests
{
    [Fact]
    public void OnlySubscribedVehicleOrRangeReceivesEvents()
    {
        var settings = new PolarionNotificationPreferences
        {
            Gamme = new() { "Daily" }, Veicoli = new() { "Alpha MY26" }
        };
        var catalogue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Daily MY24"] = "Daily", ["Daily MY26"] = "Daily",
            ["Alpha MY26"] = "Truck", ["Streetway"] = "Bus"
        };
        Assert.True(settings.Matches(new[] { "Daily MY24" }, catalogue));
        Assert.True(settings.Matches(new[] { "Alpha MY26" }, catalogue));
        Assert.False(settings.Matches(new[] { "Streetway" }, catalogue));
        Assert.False(settings.Matches(Array.Empty<string>(), catalogue));
        settings.IncludiNonAssociati = true;
        Assert.True(settings.Matches(Array.Empty<string>(), catalogue));
    }

    [Fact]
    public void DefaultsToNoNotificationsAndNoUnassociatedEvents()
    {
        var preferences = new PolarionNotificationPreferences();
        Assert.Empty(preferences.Gamme);
        Assert.Empty(preferences.Veicoli);
        Assert.False(preferences.IncludiNonAssociati);
        Assert.False(preferences.Matches(new[] { "Daily MY24" }, new Dictionary<string, string>()));
    }
}
