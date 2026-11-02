using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Le date della demo ("adesso meno N giorni") possono cadere nell'ora inesistente del passaggio
/// all'ora legale: il round-trip JSON le sposta di un'ora e il Test salvato non coincide più con quello riletto
/// (ConflittoRevisioneException al secondo salvataggio della demo, osservato il 28/09/2026 alle 01:41).</summary>
public sealed class SeedDemoOraLegaleTests
{
    private static DateTime? PrimaOraInesistente()
    {
        var inizio = new DateTime(DateTime.Now.Year, 1, 1, 0, 30, 0, DateTimeKind.Local);
        for (var d = inizio; d < inizio.AddYears(1); d = d.AddHours(1))
            if (TimeZoneInfo.Local.IsInvalidTime(d)) return d;
        return null;
    }

    [Fact]
    public void NormalizzaOrariInesistenti_OraDelCambioOrario_DiventaValidaEResisteAlRoundTripJson()
    {
        var inesistente = PrimaOraInesistente();
        if (inesistente == null) return; // fuso orario senza ora legale: nulla da verificare

        var test = new TestCase
        {
            Id = "TC-ORALEGALE",
            DataCreazione = inesistente.Value,
            UltimaModifica = inesistente.Value,
            Scadenza = inesistente.Value,
            Esecuzioni = new List<Esecuzione> { new() { Iterazione = 1, Data = inesistente.Value } },
            Changelog = new List<ChangelogEntry> { new() { Data = inesistente.Value, Campo = "x" } }
        };

        SeedDemoDataService.NormalizzaOrariInesistenti(test);

        Assert.False(TimeZoneInfo.Local.IsInvalidTime(test.DataCreazione));
        Assert.False(TimeZoneInfo.Local.IsInvalidTime(test.Esecuzioni[0].Data));
        Assert.False(TimeZoneInfo.Local.IsInvalidTime(test.Scadenza!.Value));
        Assert.False(TimeZoneInfo.Local.IsInvalidTime(test.Changelog[0].Data));

        var rilettura = System.Text.Json.JsonSerializer.Deserialize<TestCase>(
            System.Text.Json.JsonSerializer.Serialize(test, JsonIo.Options), JsonIo.Options)!;
        Assert.Equal(TestCaseFingerprint.Compute(test), TestCaseFingerprint.Compute(rilettura));
    }

    [Fact]
    public void NormalizzaOrariInesistenti_NonConformita_ResisteAlRoundTripJson()
    {
        var inesistente = PrimaOraInesistente();
        if (inesistente == null) return;

        var nc = new NonConformita { Id = "NC-ORALEGALE", DataApertura = inesistente.Value, Scadenza = inesistente.Value, DataFix = inesistente.Value };
        SeedDemoDataService.NormalizzaOrariInesistenti(nc);

        Assert.False(TimeZoneInfo.Local.IsInvalidTime(nc.DataApertura));
        var json = System.Text.Json.JsonSerializer.Serialize(nc, JsonIo.Options);
        var rilettura = System.Text.Json.JsonSerializer.Deserialize<NonConformita>(json, JsonIo.Options)!;
        Assert.Equal(json, System.Text.Json.JsonSerializer.Serialize(rilettura, JsonIo.Options));
    }
}
