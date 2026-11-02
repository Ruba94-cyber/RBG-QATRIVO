using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Parita' (6.6.1) dei filtri Lista Test Ticketing / Stato anomalia / senza componente sul percorso indicizzato:
/// stessi Test, stesso ordine, stesse pagine e stessi conteggi del percorso in memoria (legacy), con combinazioni
/// dei tre filtri e degli altri filtri. Dataset che copre tutti i valori dei tre filtri (NC di ogni stato del
/// workflow, NC con e senza collegamenti Ticketing, Test con ticket, Test senza componente, Test con Componenti duplicati).</summary>
public sealed class TestCaseListFiltriAnomalieIndiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TestListFiltriAnomalieIndice_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState NuovoStato(string configPath) => new(new AppConfigService(configPath), runBackgroundMaintenance: false);

    private string ConfigPath => Path.Combine(_dir, "config", "config.json");

    private AppState CreaStatoConDataset()
    {
        Directory.CreateDirectory(_dir);
        var state = NuovoStato(ConfigPath);
        state.InizializzaCartella(_dir);

        const int nTest = 300;
        var statiLavoro = new[] { StatoLavoroTest.Pianificato, StatoLavoroTest.InCorso, StatoLavoroTest.Concluso };
        for (var i = 0; i < nTest; i++)
        {
            var componente = i % 7 == 0 ? new EcuSelection()
                : i % 11 == 0 ? new EcuSelection(new[] { "Componente-01", "Componente-01" })
                : new EcuSelection(new[] { $"Componente-0{1 + i % 4}" });
            var tc = new TestCase
            {
                Id = $"TC-{i:D4}",
                UltimaModifica = new DateTime(2026, 6, 1).AddHours(i % 40),
                DescrizioneBreve = $"Test {i}",
                Descrizione = $"Verifica {i % 9}",
                Componente = componente,
                StatoLavoro = statiLavoro[i % statiLavoro.Length],
                Assegnatario = i % 3 == 0 ? "Mario" : null,
            };
            if (i % 5 == 0) tc.TicketCollegati.Add(new TicketRif { Riferimento = $"TICKET-{i}" });
            state.TestCases.Save(tc, isNew: true);
        }

        var stati = (StatoAnomalia[])Enum.GetValues(typeof(StatoAnomalia));
        var ncIndice = 0;
        foreach (var tc in state.TestCases.GetAll().OrderBy(t => t.Id, StringComparer.Ordinal).ToList())
        {
            var i = int.Parse(tc.Id.Substring(3), System.Globalization.CultureInfo.InvariantCulture);
            if (i % 3 == 2) continue;
            var quante = i % 4 == 0 ? 2 : 1;
            for (var k = 0; k < quante; k++)
            {
                var nc = new NonConformita
                {
                    Id = $"NC-{ncIndice:D4}",
                    StatoWorkflow = stati[(i + k) % stati.Length],
                    TestCaseId = i % 13 == 0 ? "" : tc.Id,
                    TestUid = i % 13 == 0 ? null : tc.Uid,
                };
                // Prova con Uid valido (associazione per Uid), prova per codice senza condizione (non associata).
                if (i % 13 == 0 || (i + k) % 6 == 0) nc.ProveCollegate.Add(new RiferimentoProva { TestId = tc.Id, TestUid = tc.Uid });
                if (i % 6 == 0) nc.ProveCollegate.Add(new RiferimentoProva { TestId = tc.Id });
                if ((i + k) % 4 == 0) nc.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = $"TICKET-NC-{ncIndice}" });
                state.NonConformita.Save(nc);
                ncIndice++;
            }
        }

        state.TestCases.RefreshIndex();
        Assert.True(state.TestCases.HasPersistentSnapshot);
        return state;
    }

    private sealed record Scenario(string Nome, FiltroPolarioneOpzione Ticketing, string[] Stati, bool SenzaEcu, string[] Componente, string[] StatoLavoro, string? Assegnatario = null);

    private static readonly Scenario[] Scenari =
    {
        new("Ticketing con", FiltroPolarioneOpzione.ConPolarion, Array.Empty<string>(), false, Array.Empty<string>(), Array.Empty<string>()),
        new("Ticketing senza", FiltroPolarioneOpzione.SenzaPolarion, Array.Empty<string>(), false, Array.Empty<string>(), Array.Empty<string>()),
        new("Stato Confermata", FiltroPolarioneOpzione.Tutti, new[] { nameof(StatoAnomalia.Confermata) }, false, Array.Empty<string>(), Array.Empty<string>()),
        new("Stato Confermata+EstaAperto+ProntaPerRetest", FiltroPolarioneOpzione.Tutti,
            new[] { nameof(StatoAnomalia.Confermata), nameof(StatoAnomalia.EstaAperto), nameof(StatoAnomalia.ProntaPerRetest) }, false, Array.Empty<string>(), Array.Empty<string>()),
        new("Stato Conclusa (nessuna aperta)", FiltroPolarioneOpzione.Tutti, new[] { nameof(StatoAnomalia.Conclusa) }, false, Array.Empty<string>(), Array.Empty<string>()),
        new("Senza componente", FiltroPolarioneOpzione.Tutti, Array.Empty<string>(), true, Array.Empty<string>(), Array.Empty<string>()),
        new("Ticketing con + stato InVerifica/EstaAperto", FiltroPolarioneOpzione.ConPolarion,
            new[] { nameof(StatoAnomalia.InVerifica), nameof(StatoAnomalia.EstaAperto) }, false, Array.Empty<string>(), Array.Empty<string>()),
        new("Ticketing senza + senza componente", FiltroPolarioneOpzione.SenzaPolarion, Array.Empty<string>(), true, Array.Empty<string>(), Array.Empty<string>()),
        new("Stato ProntaPerRetest + componente 02/03", FiltroPolarioneOpzione.Tutti, new[] { nameof(StatoAnomalia.ProntaPerRetest) }, false,
            new[] { "Componente-02", "Componente-03" }, Array.Empty<string>()),
        new("Ticketing con + stato Confermata + senza componente", FiltroPolarioneOpzione.ConPolarion, new[] { nameof(StatoAnomalia.Confermata) }, true,
            Array.Empty<string>(), Array.Empty<string>()),
        new("Ticketing con + stato lavoro Pianificato", FiltroPolarioneOpzione.ConPolarion, Array.Empty<string>(), false,
            Array.Empty<string>(), new[] { nameof(StatoLavoroTest.Pianificato) }),
        new("Senza componente + componente-01 (nessun risultato atteso)", FiltroPolarioneOpzione.Tutti, Array.Empty<string>(), true,
            new[] { "Componente-01" }, Array.Empty<string>()),
    };

    private static void Applica(TestCaseListViewModel vm, Scenario s)
    {
        vm.FiltroHaPolarion = s.Ticketing;
        vm.FiltroSoloSenzaEcu = s.SenzaEcu;
        Seleziona(vm.FiltroStatoAnomalia, s.Stati);
        Seleziona(vm.FiltroEcu, s.Componente);
        Seleziona(vm.FiltroStatoLavoro, s.StatoLavoro);
    }

    private static void Seleziona(FiltroMultiSelect filtro, IReadOnlyCollection<string> valori)
    {
        foreach (var voce in filtro.Opzioni)
        {
            var desiderata = valori.Contains(voce.Valore, StringComparer.OrdinalIgnoreCase);
            if (voce.Selezionato != desiderata) voce.Selezionato = desiderata;
        }
    }

    /// <summary>Firma completa della lista: totale, numero di pagine, e per ogni pagina id in ordine con le annotazioni NC
    /// (stati delle anomalie aperte, numero di chiuse, Ticketing) delle righe mostrate.</summary>
    private static string Firma(TestCaseListViewModel vm)
    {
        var parti = new List<string> { $"totale={vm.TotaleRisultati}", $"pagine={vm.PagineTotali}" };
        var pagine = vm.PagineTotali;
        for (var p = 1; p <= pagine; p++)
        {
            parti.Add(string.Join(',', vm.Righe.Select(r =>
                $"{r.Id}:{string.Join('+', r.StatiAnomalieAperteVisual)}:{r.AnomalieChiuseVisual}:{r.HaPolarionVisual}")));
            if (p < pagine) vm.PaginaSuccessivaCommand.Execute(null);
        }
        while (vm.PaginaCorrente > 1) vm.PrimaPaginaCommand.Execute(null);
        return string.Join('|', parti);
    }

    [Fact]
    public void PercorsoIndicizzato_DaStessiTestStessoOrdineStessePagineDelLegacy()
    {
        using var state = CreaStatoConDataset();
        var indicizzato = new TestCaseListViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = true };
        var legacy = new TestCaseListViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = false };
        indicizzato.Aggiorna();
        legacy.Aggiorna();

        var totaliLegacy = new List<long>();
        foreach (var scenario in Scenari)
        {
            Applica(legacy, scenario);
            Applica(indicizzato, scenario);
            var firmaLegacy = Firma(legacy);
            var firmaIndicizzata = Firma(indicizzato);
            Assert.True(firmaLegacy == firmaIndicizzata,
                $"Scenario '{scenario.Nome}': percorso indicizzato diverso dal legacy.\nlegacy:      {firmaLegacy[..Math.Min(300, firmaLegacy.Length)]}\nindicizzato: {firmaIndicizzata[..Math.Min(300, firmaIndicizzata.Length)]}");
            totaliLegacy.Add(legacy.TotaleRisultati);
        }

        // Verifica che il dataset produca risultati non banali (altrimenti la parita' sarebbe vuota).
        Assert.True(totaliLegacy[0] > 0, "Ticketing con: nessun Test nel dataset");
        Assert.True(totaliLegacy[1] > 0, "Ticketing senza: nessun Test nel dataset");
        Assert.True(totaliLegacy[2] > 0, "Stato Confermata: nessun Test nel dataset");
        Assert.True(totaliLegacy[5] > 0, "Senza componente: nessun Test nel dataset");
        Assert.Equal(0, totaliLegacy[4]);   // Conclusa non e' un'anomalia aperta
        Assert.Equal(0, totaliLegacy[^1]);  // Senza componente + componente-01 incompatibili
    }

    [Fact]
    public void FiltriNcIndicizzati_NonLeggonoLArchivioCopiatoDaGetAll()
    {
        using var state = CreaStatoConDataset();
        var vm = new TestCaseListViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = true };
        vm.Aggiorna();

        var prima = PerfContatori.LeggiSnapshot().TestCaseGetAll;
        vm.FiltroHaPolarion = FiltroPolarioneOpzione.ConPolarion;
        Seleziona(vm.FiltroStatoAnomalia, new[] { nameof(StatoAnomalia.Confermata) });
        vm.FiltroSoloSenzaEcu = true;
        var dopo = PerfContatori.LeggiSnapshot().TestCaseGetAll;

        Assert.Equal(prima, dopo);
    }

    [Fact]
    public void MigrazioneV11_ValorizzaEcuGrezzeETicketDallaCacheJson()
    {
        string dbPath;
        long attesiSenzaEcu;
        long attesiTicket;
        using (var state = CreaStatoConDataset())
        {
            dbPath = state.TestCases.LocalIndexDatabasePath!;
            attesiSenzaEcu = state.TestCases.GetAll().Count(t => t.Componente.Count == 0);
            attesiTicket = state.TestCases.GetAll().Count(t => t.TicketCollegati.Count > 0);
        }

        // Riporta l'indice allo schema v11: le due colonne v12 non ci sono piu' e la versione e' 11.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE test_summary DROP COLUMN ecu_grezzi; ALTER TABLE test_summary DROP COLUMN ticket_collegati; " +
                                  "UPDATE meta SET value = '11' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        // Riapertura: la migrazione aggiunge le colonne e le valorizza dal JSON gia' in cache, senza ricostruire dalla cartella.
        using var riaperto = NuovoStato(ConfigPath);
        riaperto.InizializzaCartella(_dir);
        riaperto.TestCases.Count();
        var archivio = new LocalTestArchiveQueryService(dbPath);
        Assert.Equal(attesiSenzaEcu, archivio.QuerySlice(new LocalTestArchiveQuery { SoloSenzaEcu = true }, 0, 1).TotalCount);
        // Con un insieme di Uid NC vuoto, "riferimento Ticketing" si riduce ai soli ticket collegati al Test (ticket_collegati).
        Assert.Equal(attesiTicket, archivio.QuerySlice(new LocalTestArchiveQuery
        {
            Ticketing = new LocalTestPolarionFiltro(ConPolarion: true, UidConNcPolarion: Array.Empty<Guid>())
        }, 0, 1).TotalCount);
    }
}
