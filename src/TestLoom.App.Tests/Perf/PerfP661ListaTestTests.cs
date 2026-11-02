using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Xunit.Abstractions;

namespace TestLoom.App.Tests.Perf;

/// <summary>
/// Banco ciclo prestazioni 6.6.1, Lista Test: tempo dei filtri che oggi ricadono sul percorso legacy in memoria
/// (Ticketing, Stato anomalia, senza componente) e tempo bloccante sul thread chiamante (il setter del filtro esegue
/// Applica() in modo sincrono, quindi il tempo misurato e' quello in cui la UI resta ferma).
///
/// Dataset deterministico (stessi Uid/id/date a ogni giro, cosi' i conteggi sono confrontabili prima/dopo):
/// un Test ogni 10 senza componente, un Test ogni 50 con TicketCollegati, un NC ogni 3 Test con tutti gli stati
/// del workflow, collegamenti Ticketing su un NC ogni 7 e prove con TestUid su un NC ogni 4.
///
/// Attivo solo con TestLoom_PERF_P661=1 (non parte della suite standard). TestLoom_PERF_MEDIA_SCALE limita la scala,
/// TestLoom_PERF_P661_LABEL etichetta l'output. Output: .ai/artifacts/perf/p661-lista-{etichetta}-{N}k.tsv nella
/// radice del repository (cartella non versionata) e riga di riepilogo nell'output del test.
/// Il workspace sintetico vive sotto .ai/tmp/perf-p661 e viene rimosso a fine test.
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfP661ListaTestTests
{
    private const int CampioniMisurati = 7;
    private readonly ITestOutputHelper _output;

    public PerfP661ListaTestTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public void MisuraFiltriListaTestAnomalieAScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_P661"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;
        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_P661_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radice = TrovaRadiceRepo() ?? Path.GetTempPath();
        var root = Path.Combine(radice, ".ai", "tmp", "perf-p661", $"{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var configPath = Path.Combine(root, "local", "config.json");
        var righe = new List<string>();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            var sw = Stopwatch.StartNew();
            DatasetP661.Genera(paths, scala);
            _output.WriteLine($"Generazione dataset {scala} Test + NC: {sw.ElapsedMilliseconds} ms");

            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "" });
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            sw.Restart();
            state.TestCases.RefreshIndex();
            _output.WriteLine($"Indicizzazione completa: {sw.ElapsedMilliseconds} ms; indice persistente: {state.TestCases.HasPersistentSnapshot}");

            // A/B nello stesso processo: stesso stato, stesse operazioni, campioni alternati fra percorso legacy in memoria
            // (PercorsoIndicizzatoAttivo = false) e percorso indicizzato (true). Il confronto di totale e prima pagina
            // verifica la parita' a ogni misura.
            // Campo di sistema Lista del catalogo filtri (come nel Master): i filtri Lista Test lo offrono solo se e' nel catalogo.
            state.Master.CampiSistemaDettagli.Save(new List<CampoSistemaDefinizione> { DatasetP661.CatalogoCampoVci() });
            var vmLegacy = new TestCaseListViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = false };
            var vmIndicizzato = new TestCaseListViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = true };
            vmLegacy.Aggiorna();
            vmIndicizzato.Aggiorna();

            righe.AddRange(MisuraAB("Ticketing con (solo filtro)", scala, vmLegacy, vmIndicizzato,
                prepara: vm => vm.FiltroHaPolarion = FiltroPolarioneOpzione.Tutti,
                misura: vm => vm.FiltroHaPolarion = FiltroPolarioneOpzione.ConPolarion));
            righe.AddRange(MisuraAB("Ticketing senza (solo filtro)", scala, vmLegacy, vmIndicizzato,
                prepara: vm => vm.FiltroHaPolarion = FiltroPolarioneOpzione.Tutti,
                misura: vm => vm.FiltroHaPolarion = FiltroPolarioneOpzione.SenzaPolarion));
            righe.AddRange(MisuraAB("Stato anomalia Confermata (solo filtro)", scala, vmLegacy, vmIndicizzato,
                prepara: vm => ImpostaVoce(vm.FiltroStatoAnomalia, nameof(StatoAnomalia.Confermata), false),
                misura: vm => ImpostaVoce(vm.FiltroStatoAnomalia, nameof(StatoAnomalia.Confermata), true)));
            righe.AddRange(MisuraAB("Senza componente (solo filtro)", scala, vmLegacy, vmIndicizzato,
                prepara: vm => vm.FiltroSoloSenzaEcu = false,
                misura: vm => vm.FiltroSoloSenzaEcu = true));
            righe.AddRange(MisuraAB("Filtro componente componente-03 (riferimento indicizzato)", scala, vmLegacy, vmIndicizzato,
                prepara: vm => ImpostaVoce(vm.FiltroEcu, "Componente-03", false),
                misura: vm => ImpostaVoce(vm.FiltroEcu, "Componente-03", true)));
            righe.AddRange(MisuraAB("Campo di sistema VCI USB (solo filtro)", scala, vmLegacy, vmIndicizzato,
                prepara: vm => ImpostaVoceCampoSistema(vm, DatasetP661.CampoVci, "VCI USB", false),
                misura: vm => ImpostaVoceCampoSistema(vm, DatasetP661.CampoVci, "VCI USB", true)));
            righe.AddRange(MisuraRevisitaConCampoSistema("Revisita Lista Test (Aggiorna) con/senza campo di sistema", scala, state, vmIndicizzato));
            righe.AddRange(MisuraAB("Combinato componente + Stato + Ticketing con", scala, vmLegacy, vmIndicizzato,
                prepara: vm =>
                {
                    ImpostaVoce(vm.FiltroEcu, "Componente-03", true);
                    ImpostaVoce(vm.FiltroStatoAnomalia, nameof(StatoAnomalia.Confermata), true);
                },
                misura: vm => vm.FiltroHaPolarion = FiltroPolarioneOpzione.ConPolarion));
            ResetFiltri(vmLegacy);
            ResetFiltri(vmIndicizzato);

            var tuttiTestRiga = state.TestCases.GetAll();
            var tsv = new StringBuilder();
            tsv.AppendLine($"# Test in memoria: {tuttiTestRiga.Count}; senza componente in memoria: {tuttiTestRiga.Count(t => t.Componente.Count == 0)}; " +
                $"Ticketing con ticket: {tuttiTestRiga.Count(t => t.TicketCollegati.Count > 0)}");
            tsv.AppendLine("scenario	p50_ms	p95_ms	min_ms	max_ms	totale_risultati	righe_pagina	parita");
            foreach (var r in righe) tsv.AppendLine(r);
            var cartella = Path.Combine(radice, ".ai", "artifacts", "perf");
            Directory.CreateDirectory(cartella);
            File.WriteAllText(Path.Combine(cartella, $"p661-lista-{etichetta}-{scala / 1000}k.tsv"), tsv.ToString());
            _output.WriteLine(tsv.ToString());
            Assert.DoesNotContain(righe, r => r.EndsWith("	KO", StringComparison.Ordinal));


        }
        finally
        {
            if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        }
    }

    /// <summary>Campiona lo stesso scenario sui due percorsi, alternando l'ordine a ogni giro. Ogni campione parte da
    /// filtri azzerati e dalla preparazione (non cronometrate); il tempo e' solo quello dell'impostazione del filtro,
    /// cioe' una Applica() sincrona, quindi il tempo che la UI resta bloccata. Parita': totale, righe e id della pagina
    /// uguali fra i due percorsi a ogni campione.</summary>
    private List<string> MisuraAB(string scenario, int scala, TestCaseListViewModel legacy, TestCaseListViewModel indicizzato,
        Action<TestCaseListViewModel> prepara, Action<TestCaseListViewModel> misura)
    {
        var tempi = new Dictionary<string, List<double>> { ["legacy"] = new(), ["indicizzato"] = new() };
        var ultima = new Dictionary<string, string>();
        var paritaOk = true;
        for (var k = 0; k <= CampioniMisurati; k++)
        {
            var ordine = k % 2 == 0 ? new[] { "legacy", "indicizzato" } : new[] { "indicizzato", "legacy" };
            var esiti = new Dictionary<string, string>();
            foreach (var nome in ordine)
            {
                var vm = nome == "legacy" ? legacy : indicizzato;
                ResetFiltri(vm);
                prepara(vm);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var sw = Stopwatch.StartNew();
                misura(vm);
                sw.Stop();
                if (k == 0) continue; // warm-up non conteggiato
                tempi[nome].Add(sw.Elapsed.TotalMilliseconds);
                esiti[nome] = $"{vm.TotaleRisultati}|{vm.Righe.Count}|{string.Join(',', vm.Righe.Select(r => r.Id))}";
            }
            if (k == 0) continue;
            if (esiti["legacy"] != esiti["indicizzato"]) paritaOk = false;
            foreach (var kv in esiti) ultima[kv.Key] = kv.Value;
        }

        var righeTsv = new List<string>();
        foreach (var nome in new[] { "legacy", "indicizzato" })
        {
            var ordinati = tempi[nome].OrderBy(x => x).ToArray();
            var p50 = ordinati[(ordinati.Length - 1) / 2];
            var p95 = ordinati[Math.Min(ordinati.Length - 1, (int)Math.Ceiling(0.95 * ordinati.Length) - 1)];
            var parti = ultima[nome].Split('|');
            var parita = paritaOk ? "OK" : "KO";
            _output.WriteLine($"{scenario} [{nome}] ({scala} Test): p50 {p50:F1} ms, p95 {p95:F1} ms, totale {parti[0]}, righe pagina {parti[1]}, parita {parita}");
            righeTsv.Add(string.Join('	', $"{scenario} [{nome}]", p50.ToString("F2", CultureInfo.InvariantCulture),
                p95.ToString("F2", CultureInfo.InvariantCulture), ordinati[0].ToString("F2", CultureInfo.InvariantCulture),
                ordinati[^1].ToString("F2", CultureInfo.InvariantCulture), parti[0], parti[1], parita));
        }
        return righeTsv;
    }

    /// <summary>Costo della revisita della pagina (Aggiorna(): filtri, conciliazione dei campi di sistema, query) con il campo di sistema nel
    /// catalogo e senza. Stesso stato e stesso dataset, campioni alternati: la differenza fra le due righe e' il costo del campo di sistema.
    /// Non ha parita' di risultati (il catalogo cambia): la colonna parita' resta n/a.</summary>
    private List<string> MisuraRevisitaConCampoSistema(string scenario, int scala, AppState state, TestCaseListViewModel vm)
    {
        var catalogoConCampo = new List<CampoSistemaDefinizione> { DatasetP661.CatalogoCampoVci() };
        var tempi = new Dictionary<string, List<double>> { ["con campo"] = new(), ["senza campo"] = new() };
        for (var k = 0; k <= CampioniMisurati; k++)
        {
            var ordine = k % 2 == 0 ? new[] { true, false } : new[] { false, true };
            foreach (var conCampo in ordine)
            {
                state.Master.CampiSistemaDettagli.Save(conCampo ? catalogoConCampo : new List<CampoSistemaDefinizione>());
                ResetFiltri(vm); // nessun filtro residuo dalle misure precedenti: entrambe le righe confrontano la stessa revisita
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var sw = Stopwatch.StartNew();
                vm.Aggiorna();
                sw.Stop();
                if (k == 0) continue; // warm-up non conteggiato
                tempi[conCampo ? "con campo" : "senza campo"].Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        var righeTsv = new List<string>();
        foreach (var nome in new[] { "senza campo", "con campo" })
        {
            var ordinati = tempi[nome].OrderBy(x => x).ToArray();
            var p50 = ordinati[(ordinati.Length - 1) / 2];
            var p95 = ordinati[Math.Min(ordinati.Length - 1, (int)Math.Ceiling(0.95 * ordinati.Length) - 1)];
            _output.WriteLine($"{scenario} [{nome}] ({scala} Test): p50 {p50:F1} ms, p95 {p95:F1} ms");
            righeTsv.Add(string.Join('	', $"{scenario} [{nome}]", p50.ToString("F2", CultureInfo.InvariantCulture),
                p95.ToString("F2", CultureInfo.InvariantCulture), ordinati[0].ToString("F2", CultureInfo.InvariantCulture),
                ordinati[^1].ToString("F2", CultureInfo.InvariantCulture), "-", "-", "n/a"));
        }
        // Lascia il catalogo come il resto del banco (con il campo), per le misure successive.
        state.Master.CampiSistemaDettagli.Save(catalogoConCampo);
        vm.Aggiorna();
        return righeTsv;
    }

    private static void ResetFiltri(TestCaseListViewModel vm)
    {
        vm.FiltroHaPolarion = FiltroPolarioneOpzione.Tutti;
        vm.FiltroSoloSenzaEcu = false;
        foreach (var voce in vm.FiltroStatoAnomalia.Opzioni.Where(o => o.Selezionato).ToList()) voce.Selezionato = false;
        foreach (var voce in vm.FiltroEcu.Opzioni.Where(o => o.Selezionato).ToList()) voce.Selezionato = false;
        foreach (var campo in vm.FiltriCampiCustomLista) campo.Filtro.DeselezionaTutto();
    }

    private static void ImpostaVoce(FiltroMultiSelect filtro, string valore, bool selezionato)
    {
        var voce = filtro.Opzioni.FirstOrDefault(o => string.Equals(o.Valore, valore, StringComparison.OrdinalIgnoreCase));
        if (voce != null && voce.Selezionato != selezionato) voce.Selezionato = selezionato;
    }

    private static void ImpostaVoceCampoSistema(TestCaseListViewModel vm, string chiave, string valore, bool selezionato)
    {
        var campo = vm.FiltriCampiCustomLista.SingleOrDefault(f => f.Chiave == chiave);
        if (campo != null) ImpostaVoce(campo.Filtro, valore, selezionato);
    }

    private static string? TrovaRadiceRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
        return null;
    }

    /// <summary>Dataset deterministico: Uid e id fissi, nessun valore casuale, cosi' conteggi e ordine sono riproducibili.</summary>
    internal static class DatasetP661
    {
        private static readonly string[] Componente = Enumerable.Range(0, 20).Select(i => $"Componente-{i:D2}").ToArray();
        private static readonly StatoLavoroTest[] StatiLavoro =
        {
            StatoLavoroTest.Pianificato, StatoLavoroTest.InCorso, StatoLavoroTest.Concluso,
            StatoLavoroTest.DaRivedere, StatoLavoroTest.InAttesa,
        };
        private static readonly StatoAnomalia[] Workflow =
        {
            StatoAnomalia.InVerifica, StatoAnomalia.Confermata, StatoAnomalia.EstaAperto,
            StatoAnomalia.ProntaPerRetest, StatoAnomalia.Conclusa,
        };

        internal const string CampoVci = "vci-connection";
        private static readonly string[] VciValori = { "VCI USB", "VCI LAN", "vci usb", "VCI Bluetooth" };

        /// <summary>Campo di sistema Lista "VCI Connection" usabile come filtro, come nel catalogo del Master.</summary>
        /// <summary>Uid fisso: salvataggi ripetuti dello stesso campo non devono creare una collisione di chiave business nel catalogo.</summary>
        internal static CampoSistemaDefinizione CatalogoCampoVci() => new()
        {
            Uid = Guid.Parse("00000000-0000-4000-8000-00000000c001"),
            Chiave = CampoVci, Etichetta = "VCI Connection", Gruppo = GruppoCampoSistema.Dettagli, Ruolo = RuoloCampoSistema.Nessuno,
            UsabileComeFiltro = true, Tipo = TipoCampoStrumento.Lista, OpzioniLista = new List<string> { "VCI USB", "VCI LAN", "VCI Wi-Fi", "VCI Bluetooth" },
        };

        internal static Guid UidTest(int i) => Guid.Parse($"{i + 1:x8}-0000-4000-8000-00000000a001");
        internal static Guid UidNc(int k) => Guid.Parse($"{k + 1:x8}-0000-4000-9000-00000000b001");

        internal static void Genera(DataPaths paths, int nTest, bool conEsecuzioni = false)
        {
            var opzioni = JsonIo.Options;
            for (var i = 0; i < nTest; i++)
            {
                var id = $"TC-{i:D6}";
                var senzaEcu = i % 10 == 0;
                var componente = Componente[i % Componente.Length];
                var tc = new TestCase
                {
                    Id = id,
                    Uid = UidTest(i),
                    RevisioneDati = 1,
                    DataCreazione = new DateTime(2026, 1, 1).AddDays(i % 300),
                    UltimaModifica = new DateTime(2026, 6, 1).AddMinutes(i * 7 % 100_000),
                    ModificatoDa = $"Operatore-Bench-{i % 20:D2}",
                    CreatoDa = $"Operatore-Bench-{i % 20:D2}",
                    Componente = senzaEcu ? new EcuSelection() : new EcuSelection(new[] { componente }),
                    StatoLavoro = StatiLavoro[i % StatiLavoro.Length],
                    DescrizioneBreve = $"Diagnostica banco scenario {i % 500}",
                    Descrizione = $"Verifica di scala su {componente}.",
                };
                if (i % 50 == 0) tc.TicketCollegati.Add(new TicketRif { Riferimento = $"TICKET-T-{i}" });
                // Campo di sistema usabile come filtro (Lista, "VCI Connection"): un Test ogni tre, con grafie diverse da quelle del catalogo.
                if (i % 3 == 0) tc.CampiCustom[CampoVci] = VciValori[i % VciValori.Length];
                // Esecuzioni con note e deviazioni (solo banco Home): un Test ogni 4, deviazioni su un Test ogni 8.
                if (conEsecuzioni && i % 4 == 1)
                    tc.Esecuzioni.Add(new Esecuzione
                    {
                        Iterazione = 1,
                        Data = tc.UltimaModifica,
                        Operatore = tc.ModificatoDa,
                        Note = $"nota banco {i % 37}",
                        Deviazioni = i % 8 == 1 ? $"deviazione {i % 5}" : "",
                    });
                var dir = paths.TestCaseDir(senzaEcu ? "Componente-SENZA" : componente);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"testcase_{id}.json"), JsonSerializer.SerializeToUtf8Bytes(tc, opzioni));

                if (i % 3 != 0) continue;
                var k = i / 3;
                var nc = new NonConformita
                {
                    Uid = UidNc(k),
                    RevisioneDati = 1,
                    Id = $"NC-{k:D6}",
                    TestCaseId = k % 11 == 0 ? "" : id,
                    TestUid = k % 11 == 0 ? null : UidTest(i),
                    DataApertura = new DateTime(2026, 1, 1).AddDays(k % 300),
                    DataChiusura = Workflow[k % Workflow.Length] == StatoAnomalia.Conclusa ? new DateTime(2026, 6, 1).AddDays(k % 150) : null,
                    Owner = $"Operatore-Bench-{k % 20:D2}",
                    StatoWorkflow = Workflow[k % Workflow.Length],
                    Severita = (SeveritaAnomalia)(1 + k % 5),
                    Descrizione = $"Anomalia riscontrata su scenario banco {k % 500}",
                    Azione = "In lavorazione",
                };
                if (k % 11 == 0 || k % 4 == 0) nc.ProveCollegate.Add(new RiferimentoProva { TestId = id, TestUid = UidTest(i) });
                if (k % 7 == 0) nc.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = $"TICKET-{k}" });
                File.WriteAllBytes(paths.NonConformitaFile(nc.Id), JsonSerializer.SerializeToUtf8Bytes(nc, opzioni));
            }
        }
    }
}
