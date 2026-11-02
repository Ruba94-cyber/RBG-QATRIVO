using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Filtri sui campi custom di sistema (Home e Report): il valore "Phoenix" trova anche "phoenix" in ogni percorso (SQL della Home,
/// indice del Report, filtro in memoria), con lo stesso conteggio; l'elenco dei valori distinti non duplica le varianti di maiuscole.</summary>
public sealed class FiltriCampiSistemaMaiuscoleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_FiltriMaiuscole_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Giorno = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static TestCase Test(string id, string progetto)
    {
        var tc = new TestCase
        {
            Id = id, Uid = Guid.NewGuid(), DataCreazione = Giorno, UltimaModifica = Giorno,
            Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily"], Veicolo = "Daily", Funzionalita = ["F1"],
            CampiCustom = new() { ["progetto-demo"] = progetto },
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = Giorno, Operatore = "Mario", Esito = Esito.Conforme,
            Condizioni = [new Condizione { Funzionalita = "F1", Esito = Esito.Conforme }],
        });
        return tc;
    }

    private static readonly Dictionary<string, List<string>> FiltroPhoenix = new() { ["progetto-demo"] = ["Phoenix"] };

    [Fact]
    public void Valori_DistinzioneSoloPerMaiuscoleEPrimaGrafia()
    {
        var test = new[] { Test("A", "Phoenix"), Test("B", "phoenix"), Test("C", "Orion") };
        var valori = CampoSistemaCatalogoService.ValoriDistinti(test, "progetto-demo");
        Assert.Equal(2, valori.Count);
        Assert.Contains("Phoenix", valori);
        Assert.DoesNotContain("phoenix", valori);
    }

    [Fact]
    public void FiltroInMemoria_ReportEDashboard_TrovanoPhoenixEPhoenixConLoStessoConteggio()
    {
        var test = new[] { Test("A", "Phoenix"), Test("B", "phoenix"), Test("C", "Orion") };

        var report = new ReportFilter { CampiCustom = new(FiltroPhoenix) };
        Assert.Equal(new[] { "A", "B" }, test.Where(report.Corrisponde).Select(t => t.Id).ToArray());

        var dashboard = new DashboardFilter { CampiCustom = new(FiltroPhoenix) };
        Assert.Equal(2, dashboard.Applica(test, _ => null).Count);
    }

    [Fact]
    public void IndiciSql_HomeEReport_TrovanoPhoenixEPhoenixConLoStessoConteggio()
    {
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        foreach (var tc in new[] { Test("A", "Phoenix"), Test("B", "phoenix"), Test("C", "Orion") })
            store.Upsert(Path.Combine(_root, tc.Id + ".json"), Giorno, 100, tc);

        var report = new LocalTestArchiveQueryService(store.DatabasePath).QueryAll(new LocalTestArchiveQuery
        {
            CustomFieldSelections = FiltroPhoenix.ToDictionary(x => x.Key, x => (IReadOnlyCollection<string>)x.Value),
            MatchStringsOrdinal = true,
        });
        Assert.Equal(2, report.Count());

        var home = new LocalHomeDashboardQueryService(store.DatabasePath)
            .GetSnapshot(new DashboardFilter { CampiCustom = new(FiltroPhoenix) }, null, null);
        Assert.Equal(2, home.OutcomeCounts.Values.Sum());
    }
}
