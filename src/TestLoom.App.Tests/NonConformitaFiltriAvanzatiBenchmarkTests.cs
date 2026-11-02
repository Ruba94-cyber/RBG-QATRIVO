using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Benchmark riproducibile dei filtri avanzati della pagina Anomalie a 100.000 NC (e 5.000 Test):
/// confronta, sugli STESSI scenari e sullo stesso archivio, il percorso "memoria" (predicati in LINQ sulla
/// cache, equivalente all'algoritmo storico della pagina) con il percorso "indice" (filtro + ordinamento +
/// paginazione delegati all'indice SQLite locale schema v4). Dataset deterministico (seed fisso).
///
/// Esecuzione:  dotnet test src/TestLoom.App.Tests -c Release --filter "Category=Scale&amp;FullyQualifiedName~FiltriAvanzati"
///              --logger "console;verbosity=detailed"   (le righe utili iniziano con BENCH|)
/// Variabile opzionale TestLoom_BENCH_NC_COUNT (default 100000). Non scrive nulla fuori dalla cartella temporanea.
/// Lo scenario "uiThread" misura anche quanto resta bloccato il thread chiamante: i filtri girano su un
/// thread di lavoro (vedi NonConformitaViewModel), quindi il blocco UI e' solo l'applicazione del risultato.
/// </summary>
[Collection("Scale performance")]
public class NonConformitaFiltriAvanzatiBenchmarkTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public NonConformitaFiltriAvanzatiBenchmarkTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private const int Ripetizioni = 7;

    [Fact]
    [Trait("Category", "Scale")]
    public void FiltriAvanzati_100kNc_IndiceVsMemoria()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("TestLoom_BENCH_NC_COUNT"), out var c) ? c : 100_000;
        var persistente = Environment.GetEnvironmentVariable("TestLoom_BENCH_NC_DIR");
        var root = persistente ?? Path.Combine(Path.GetTempPath(), "TestLoom_NcFiltriBench", Guid.NewGuid().ToString("N"));
        try
        {
            var dataRoot = Path.Combine(root, "data");
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            const int testCount = 5000;
            var sw = Stopwatch.StartNew();
            var giaPresente = persistente != null && Directory.Exists(paths.NonConformitaDir) && Directory.GetFiles(paths.NonConformitaDir, "nc_*.json").Length == count;
            if (!giaPresente) { CreaTest(paths, testCount);
            CreaNc(paths, count, testCount); }
            _output.WriteLine($"BENCH|setup_dataset_ms={sw.ElapsedMilliseconds}|nc={count}|test={testCount}");

            var configService = new AppConfigService(Path.Combine(root, "local", "config.json"));
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Bench" });
            sw.Restart();
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            _output.WriteLine($"BENCH|apertura_appstate_ms={sw.ElapsedMilliseconds}|nc_in_cache={state.NonConformita.CountAll}");
            Assert.Equal(count, state.NonConformita.CountAll);

            // Il refresh iniziale (full reconcile) allinea l'indice: lo si attende prima di misurare.
            var atteso = Stopwatch.StartNew();
            while (!state.NonConformita.IndiceFiltriUtilizzabile && atteso.Elapsed < TimeSpan.FromMinutes(5))
            {
                state.NonConformita.RefreshIndex();
                if (!state.NonConformita.IndiceFiltriUtilizzabile) Thread.Sleep(200);
            }
            Assert.True(state.NonConformita.IndiceFiltriUtilizzabile, "indice non allineato dopo il refresh");
            _output.WriteLine($"BENCH|attesa_allineamento_indice_ms={atteso.ElapsedMilliseconds}");

            var gammaDiVeicolo = Enumerable.Range(0, 50).ToDictionary(i => $"Veicolo-{i:D3}", i => $"Gamma-{i % 10:D2}");
            TestCase? Trova(string id) => state.TestCases.GetById(id);
            AnomalieFiltroAmbito Ambito(string[]? veicoli = null, string[]? componente = null, string[]? gamma = null, string[]? funz = null) => new()
            {
                Veicoli = veicoli == null ? null : new(veicoli, StringComparer.OrdinalIgnoreCase),
                Componente = componente == null ? null : new(componente, StringComparer.OrdinalIgnoreCase),
                Gamma = gamma == null ? null : new(gamma, StringComparer.OrdinalIgnoreCase),
                Funzionalita = funz == null ? null : new(funz, StringComparer.OrdinalIgnoreCase),
                GammaDiVeicolo = gammaDiVeicolo
            };
            var nessunAmbito = Ambito();
            var apertura = AnomalieOrdinamento.Predefinito;

            var scenari = new List<(string Nome, AnomalieQueryRichiesta Richiesta)>
            {
                ("01_stato_aperta_ord_apertura", new(new() { Stato = StatoNonConformita.Aperta }, nessunAmbito, apertura, 1, 250)),
                ("02_nessun_filtro_pagina_200", new(new(), nessunAmbito, apertura, 200, 250)),
                ("03_priorita2_gravita2_assegnatari3", new(new()
                {
                    Priorita = [PrioritaOperativa.Major, PrioritaOperativa.Critical],
                    Gravita = [SeveritaAnomalia.High, SeveritaAnomalia.Highest],
                    Assegnatari = ["Operatore-01", "Operatore-02", "Operatore-03"]
                }, nessunAmbito, apertura, 1, 250)),
                ("04_come_03_piu_date_apertura_chiusura", new(new()
                {
                    Priorita = [PrioritaOperativa.Major, PrioritaOperativa.Critical],
                    Gravita = [SeveritaAnomalia.High, SeveritaAnomalia.Highest],
                    Assegnatari = ["Operatore-01", "Operatore-02", "Operatore-03"],
                    AperturaDa = new DateTime(2026, 2, 1), AperturaA = new DateTime(2026, 5, 31),
                    ChiusuraDa = new DateTime(2026, 6, 1), ChiusuraA = new DateTime(2026, 8, 31)
                }, nessunAmbito, apertura, 1, 250)),
                ("05_testo_scenario_42", new(new() { Testo = "scenario 42" }, nessunAmbito, apertura, 1, 250)),
                ("06_ord_descrizione_asc_tutte", new(new(), nessunAmbito, new(AnomalieOrdinamentoChiave.Descrizione, false), 1, 250)),
                ("07_ord_priorita_desc_pagina_100", new(new(), nessunAmbito, new(AnomalieOrdinamentoChiave.Priorita, true), 100, 250)),
                ("08_ord_assegnatario_asc_stato_chiusa", new(new() { Stato = StatoNonConformita.Chiusa }, nessunAmbito, new(AnomalieOrdinamentoChiave.Assegnatario, false), 1, 250)),
                ("09_ambito_sottoveicolo_ecu", new(new(), Ambito(veicoli: ["Veicolo-003"], componente: ["Componente-003"]), apertura, 1, 250)),
                ("10_ambito_gamma_piu_priorita", new(new() { Priorita = [PrioritaOperativa.Critical] }, Ambito(gamma: ["Gamma-03"]), apertura, 1, 250)),
                ("11_ambito_funzionalita_piu_stato_date", new(new()
                {
                    Stato = StatoNonConformita.Aperta, AperturaDa = new DateTime(2026, 1, 1), AperturaA = new DateTime(2026, 4, 30)
                }, Ambito(funz: ["Funzionalita-007"]), apertura, 1, 250)),
                ("12_tutto_combinato", new(new()
                {
                    Priorita = [PrioritaOperativa.Major, PrioritaOperativa.Critical],
                    Gravita = [SeveritaAnomalia.High],
                    Assegnatari = ["Operatore-01", "Operatore-02"],
                    AperturaDa = new DateTime(2026, 1, 1), AperturaA = new DateTime(2026, 6, 30),
                    Testo = "anomalia"
                }, Ambito(veicoli: ["Veicolo-003", "Veicolo-004"], componente: ["Componente-003", "Componente-004"]),
                    new(AnomalieOrdinamentoChiave.Gravita, true), 1, 250)),
                ("13_ticket_esta_collegato_direttamente_alla_nc", new(new() { HaTicketEsta = true }, nessunAmbito, apertura, 1, 250)),
            };

            _output.WriteLine("BENCH|scenario|memoria_min_ms|memoria_mediana_ms|app_min_ms|app_mediana_ms|totale_righe|stesso_risultato");
            foreach (var (nome, richiesta) in scenari)
            {
                var memoria = Misura(state, Trova, richiesta, consentiIndice: false, out var risMem);
                var indice = Misura(state, Trova, richiesta, consentiIndice: true, out var risIdx);

                var uguale = risMem.Totale == risIdx.Totale && risMem.PaginaEffettiva == risIdx.PaginaEffettiva &&
                    risMem.Righe.Select(r => r.Nc.Id).SequenceEqual(risIdx.Righe.Select(r => r.Nc.Id));
                _output.WriteLine($"BENCH|{nome}|{memoria.Min:F1}|{memoria.Mediana:F1}|{indice.Min:F1}|{indice.Mediana:F1}|{risIdx.Totale}|{uguale}|percorso_scelto={(risIdx.DaIndice ? "indice" : "memoria")}");
                Assert.True(uguale, $"{nome}: indice e memoria devono dare lo stesso risultato (totale {risMem.Totale} vs {risIdx.Totale})");
            }

            var ramMiB = Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0;
            var dbPath = state.NonConformita.IndexRecovery!.DatabasePath;
            _output.WriteLine($"BENCH|ram_mib={ramMiB:F1}|nc_index_db_mib={new FileInfo(dbPath).Length / 1024.0 / 1024.0:F1}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (persistente == null) { try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { } }
        }
    }

    /// <summary>Isola il solo livello SQLite (senza repository/AppState): costo di apertura connessione, COUNT e
    /// pagina ordinata sullo schema v4 a 100k righe. Serve a distinguere il costo della query dal resto.</summary>
    [Fact]
    [Trait("Category", "Scale")]
    public void FiltriAvanzati_100kNc_SoloStoreSqlite()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_NcFiltriStore", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "shared"));
            var store = new LocalNonConformitaIndexStore(Path.Combine(root, "shared"), Path.Combine(root, "local"));
            const int n = 100_000;
            var rnd = new Random(1);
            var owners = Enumerable.Range(0, 12).Select(i => $"Operatore-{i:D2}").ToArray();
            var entries = Enumerable.Range(0, n).Select(i =>
            {
                var chiusa = rnd.Next(3) == 0;
                var ap = new DateTime(2026, 1, 1).AddDays(rnd.Next(240)).AddMinutes(rnd.Next(1440));
                return new LocalIndexWriteEntryNc(Path.Combine(root, "shared", $"nc_{i}.json"), DateTime.UtcNow, 1, new NonConformita
                {
                    Uid = Guid.NewGuid(), Id = $"NC-{i:D6}", Owner = owners[rnd.Next(12)], DataApertura = ap,
                    DataChiusura = chiusa ? ap.AddDays(rnd.Next(90)) : null,
                    StatoWorkflow = chiusa ? StatoAnomalia.Conclusa : (StatoAnomalia)rnd.Next(0, 5),
                    Severita = (SeveritaAnomalia)rnd.Next(6), Priorita = (PrioritaOperativa)rnd.Next(6),
                    Descrizione = $"Anomalia comunicazione CAN su scenario {rnd.Next(137)}"
                });
            }).ToList();
            var sw = Stopwatch.StartNew();
            store.ReconcileFull(entries, DateTime.UtcNow);
            _output.WriteLine($"BENCH|store|reconcile_full_primo_ms={sw.ElapsedMilliseconds}");
            sw.Restart();
            store.ReconcileFull(entries, DateTime.UtcNow);
            _output.WriteLine($"BENCH|store|reconcile_full_secondo_invariato_ms={sw.ElapsedMilliseconds}");

            var filtro = new AnomalieFiltroNc { Priorita = [PrioritaOperativa.Major, PrioritaOperativa.Critical], Gravita = [SeveritaAnomalia.High, SeveritaAnomalia.Highest] };
            var ord = AnomalieOrdinamento.Predefinito;
            for (var giro = 0; giro < 3; giro++)
            {
                sw.Restart(); store.QueryPagina(new AnomalieFiltroNc(), ord, 0, 0); var tCountTutti = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); store.QueryPagina(filtro, ord, 0, 0); var tCountFiltro = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); store.QueryPagina(filtro, ord, 0, 250); var tPagina = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); store.QueryPagina(new AnomalieFiltroNc(), ord, 0, 250); var tPaginaTutti = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); store.QueryPagina(new AnomalieFiltroNc(), ord, 0, -1); var tTutti = sw.Elapsed.TotalMilliseconds;
                _output.WriteLine($"BENCH|store|giro={giro}|count_tutti={tCountTutti:F1}|count_filtro={tCountFiltro:F1}|pagina_filtro={tPagina:F1}|pagina_tutti={tPaginaTutti:F1}|tutti_i_percorsi={tTutti:F1}");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static (double Min, double Mediana) Misura(AppState state, Func<string, TestCase?> trova, AnomalieQueryRichiesta r,
        bool consentiIndice, out AnomalieQueryRisultato ultimo)
    {
        // Un giro di riscaldamento (JIT, page cache SQLite) escluso dalle statistiche.
        ultimo = AnomalieQueryService.Esegui(state.NonConformita, trova, r, consentiIndice);
        var tempi = new List<double>();
        for (var i = 0; i < Ripetizioni; i++)
        {
            var sw = Stopwatch.StartNew();
            ultimo = AnomalieQueryService.Esegui(state.NonConformita, trova, r, consentiIndice);
            tempi.Add(sw.Elapsed.TotalMilliseconds);
        }
        tempi.Sort();
        return (tempi[0], tempi[tempi.Count / 2]);
    }

    private static void CreaTest(DataPaths paths, int count)
    {
        for (var indice = 0; indice < count; indice++)
        {
            var componente = indice % 50;
            var dir = paths.TestCaseDir($"Componente-{componente:D3}");
            Directory.CreateDirectory(dir);
            var id = $"TC-{indice:D6}";
            var json = $$"""
            {"SchemaVersion":4,"Uid":"{{new Guid(indice, 0, 0, new byte[8])}}","RevisioneDati":1,"Id":"{{id}}","DescrizioneBreve":"Diagnostica scenario {{indice % 137}}","Descrizione":"Verifica realistica","DataCreazione":"2026-08-01T08:00:00","UltimaModifica":"2026-08-01T12:00:00","ModificatoDa":"Operatore-01","Componente":["Componente-{{componente:D3}}"],"Veicolo":"Veicolo-{{indice % 50:D3}}","VeicoliEffettivi":["Veicolo-{{indice % 50:D3}}"],"StatoLavoro":"Concluso","Funzionalita":["Funzionalita-{{indice % 40:D3}}"],"RequisitoId":[],"CampiCustom":{},"Esecuzioni":[],"Allegati":[],"Changelog":[]}
            """;
            File.WriteAllText(Path.Combine(dir, $"testcase_{id}.json"), json, Encoding.UTF8);
        }
    }

    private static void CreaNc(DataPaths paths, int count, int testCount)
    {
        Directory.CreateDirectory(paths.NonConformitaDir);
        var parole = new[] { "comunicazione CAN", "perdita segnale", "schermo nero", "errore guasto", "timeout diagnosi", "reset inatteso" };
        Parallel.For(0, count, indice =>
        {
            var r = new Random(unchecked(20260507 + indice * 31));
            var id = $"NC-{indice:D6}";
            var chiusa = r.Next(3) == 0;
            var apertura = new DateTime(2026, 1, 1).AddDays(r.Next(240)).AddMinutes(r.Next(1440));
            var nc = new NonConformita
            {
                Uid = new Guid(indice + 1, 1, 1, new byte[8]),
                RevisioneDati = 1,
                Id = id,
                TestCaseId = r.Next(5) == 0 ? $"TC-{r.Next(testCount):D6}" : "",
                DataApertura = apertura,
                DataChiusura = chiusa ? apertura.AddDays(r.Next(90)) : null,
                Owner = $"Operatore-{r.Next(12):D2}",
                StatoWorkflow = chiusa ? StatoAnomalia.Conclusa : (StatoAnomalia)r.Next(0, 5),
                Severita = (SeveritaAnomalia)r.Next(6),
                Priorita = (PrioritaOperativa)r.Next(6),
                Descrizione = $"Anomalia {parole[r.Next(parole.Length)]} su scenario {r.Next(137)}",
                Azione = "In lavorazione",
            };
            if (r.Next(5) == 0)
                nc.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = $"TICKET-{indice:D6}", Url = $"https://ticketing.example/TICKET-{indice:D6}" });
            var bytes = JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options);
            File.WriteAllBytes(paths.NonConformitaFile(id), bytes);
        });
    }
}
