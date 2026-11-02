using System.Diagnostics;
using System.Globalization;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Misura A/B apertura/ritorno Home a 100k Test + 1k cataloghi su un dataset persistente condiviso fra
/// binari diversi (baseline e branch). Attivo solo con la variabile d'ambiente TestLoom_BENCH_ROOT (cartella di lavoro,
/// creata/popolata alla prima esecuzione); TestLoom_BENCH_OUT = file CSV in cui accodare "label;open_ms;return_ms".</summary>
[Collection("Scale performance")]
public class HomeOpenAbBenchTests
{
    [Fact]
    [Trait("Category", "Scale")]
    public void Home_AperturaERitorno_SuDatasetCondiviso()
    {
        var root = Environment.GetEnvironmentVariable("TestLoom_BENCH_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        var label = Environment.GetEnvironmentVariable("TestLoom_BENCH_LABEL") ?? "?";
        var outFile = Environment.GetEnvironmentVariable("TestLoom_BENCH_OUT");
        var dataRoot = Path.Combine(root, "data");
        var marker = Path.Combine(root, "ready.marker");
        var configPath = Path.Combine(root, "local", "config.json");
        var configService = new AppConfigService(configPath);

        if (!File.Exists(marker))
        {
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            const int testCount = 100_000, catalogSize = 1_000;
            for (var indice = 0; indice < testCount; indice++)
            {
                var ecuN = indice % catalogSize;
                var dir = paths.TestCaseDir($"Componente-{ecuN:D4}");
                Directory.CreateDirectory(dir);
                var id = $"TC-{indice:D6}";
                var json = $$"""
                {"SchemaVersion":4,"Uid":"{{Guid.NewGuid()}}","RevisioneDati":1,"Id":"{{id}}","DescrizioneBreve":"D {{indice % 137}}","Descrizione":"V","DataCreazione":"2026-08-01T08:00:00","UltimaModifica":"2026-08-01T12:00:00","ModificatoDa":"Operatore-01","Componente":["Componente-{{ecuN:D4}}"],"Veicolo":"Veicolo-{{indice % catalogSize:D4}}","VeicoliEffettivi":["Veicolo-{{indice % catalogSize:D4}}"],"StatoLavoro":"Concluso","Funzionalita":["Funzione-{{indice % catalogSize:D4}}"],"RequisitoId":["REQ-{{indice % catalogSize:D5}}"],"CampiCustom":{},"Esecuzioni":[],"Allegati":[],"Changelog":[]}
                """;
                File.WriteAllText(Path.Combine(dir, $"testcase_{id}.json"), json, Encoding.UTF8);
            }
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Operatore Prova Home" });
            using (var s = new AppState(configService, runBackgroundMaintenance: false))
            {
                s.Master.Gamma.Save(Enumerable.Range(0, 50).Select(i => new Gamma { Nome = $"Gamma-{i:D2}" }).ToList());
                s.Master.Veicoli.Save(Enumerable.Range(0, catalogSize).Select(i => new Veicolo { Nome = $"Veicolo-{i:D4}", GammaNome = $"Gamma-{i % 50:D2}" }).ToList());
                s.Master.Componente.Save(Enumerable.Range(0, catalogSize).Select(i => new Componente { Nome = $"Componente-{i:D4}", VeicoliAssociati = { $"Veicolo-{i % catalogSize:D4}" } }).ToList());
                s.Master.Funzionalita.Save(Enumerable.Range(0, catalogSize).Select(i => new Funzionalita { Nome = $"Funzione-{i:D4}", Categorie = new() { $"Categoria-{i % 50:D2}" } }).ToList());
                s.Master.Requisiti.Save(Enumerable.Range(0, catalogSize).Select(i => new Requisito { Id = $"REQ-{i:D5}", Testo = $"Requisito {i}" }).ToList());
            }
            SqliteConnection.ClearAllPools();
            File.WriteAllText(marker, "ok");
        }

        using var state = new AppState(configService, runBackgroundMaintenance: false);
        var sw = Stopwatch.StartNew();
        var vm = new HomeViewModel(state, _ => { });
        var apertura = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var vm2 = new HomeViewModel(state, _ => { });
        var ritorno = sw.Elapsed.TotalMilliseconds;
        SqliteConnection.ClearAllPools();
        if (outFile != null)
            File.AppendAllText(outFile, string.Create(CultureInfo.InvariantCulture, $"{label};{apertura:F0};{ritorno:F0}\n"));
    }
}
