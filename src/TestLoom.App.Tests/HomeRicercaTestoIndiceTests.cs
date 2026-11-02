using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Ricerca testuale della Home (6.6.1): Da fare / Fatti con testo sul percorso indicizzato devono contenere
/// gli stessi Test del percorso in memoria (HomeViewModel.Corrisponde), con paginazione, e il testo deve applicarsi
/// dopo il ritardo di digitazione. Dataset con componente, veicoli, requisiti, funzionalita', template (anche sconosciuto),
/// note e deviazioni delle esecuzioni.</summary>
public sealed class HomeRicercaTestoIndiceTests : IDisposable
{
    private static readonly Guid TemplateFreni = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid TemplateMotore = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid TemplateSconosciuto = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeRicercaTesto_" + Guid.NewGuid().ToString("N"));

    /// <summary>Ricerche della parita': campi diversi di Corrisponde, maiuscole/minuscole, caratteri non ASCII e il caso "?".</summary>
    private static readonly string[] Ricerche =
    {
        "TC-00", "freni", "MODELLO motore", "Modello freni", "Componente-02", "componente-03", "VEI-1", "REQ-2", "Funzione 1",
        "nota 3", "NOTA 6", "deviazione", "Verifica 4", "Breve 1", "?", "modello sconosciuto", "perché", "non esiste",
    };

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private string ConfigPath => Path.Combine(_dir, "config", "config.json");

    private AppState NuovoStato() => new(new AppConfigService(ConfigPath), runBackgroundMaintenance: false);

    private AppState CreaStato(int nTest)
    {
        Directory.CreateDirectory(_dir);
        var state = NuovoStato();
        state.InizializzaCartella(_dir);
        state.Master.TestTemplate.Save(new List<TestTemplate>
        {
            new() { Uid = TemplateFreni, Nome = "Modello freni" },
            new() { Uid = TemplateMotore, Nome = "Modello motore" },
        });

        var stati = new[]
        {
            StatoLavoroTest.Pianificato, StatoLavoroTest.InCorso, StatoLavoroTest.Concluso,
            StatoLavoroTest.DaRivedere, StatoLavoroTest.InAttesa, StatoLavoroTest.DaRitestare,
        };
        for (var i = 0; i < nTest; i++)
        {
            var veicolo = $"VEI-{i % 3}";
            var tc = new TestCase
            {
                Id = $"TC-{i:D4}",
                UltimaModifica = new DateTime(2026, 6, 1).AddHours(i % 40),
                Descrizione = i % 6 == 0 ? "Verifica freni ABS" : $"Verifica {i % 9}",
                DescrizioneBreve = $"Breve {i}",
                Componente = i % 5 == 0 ? new EcuSelection() : new EcuSelection(new[] { $"Componente-0{i % 4}" }),
                Veicolo = veicolo,
                Veicoli = new List<string> { veicolo },
                RequisitoId = i % 2 == 0 ? new List<string> { $"REQ-{i % 4}" } : new List<string>(),
                Funzionalita = i % 3 == 0 ? new List<string> { $"Funzione {i % 3}" } : new List<string>(),
                TestTemplateUid = i % 7 == 0 ? TemplateSconosciuto : i % 2 == 0 ? TemplateFreni : TemplateMotore,
                StatoLavoro = stati[i % stati.Length],
                Assegnatario = i % 4 == 0 ? "Mario" : null,
            };
            if (i % 4 == 1)
                tc.Esecuzioni.Add(new Esecuzione
                {
                    Iterazione = 1,
                    Data = tc.UltimaModifica,
                    Operatore = "Mario",
                    Note = $"nota {i % 7}",
                    Deviazioni = i % 8 == 1 ? "deviazione x" : "",
                });
            state.TestCases.Save(tc, isNew: true);
        }

        state.RicaricaTutto();
        Assert.True(state.TestCases.HasPersistentSnapshot);
        return state;
    }

    private static async Task ImpostaTesto(HomeViewModel vm, string testo)
    {
        vm.TestoRicerca = testo;
        await Task.Delay(HomeViewModel.DebounceTestoRicerca + TimeSpan.FromMilliseconds(150));
        await AttendiQuieto(vm);
    }

    private static async Task AttendiQuieto(HomeViewModel vm)
    {
        for (var i = 0; i < 2000 && vm.CaricamentoInCorso; i++) await Task.Delay(10);
    }

    private static string Firma(IEnumerable<TestCase> righe) =>
        string.Join(',', righe.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal));

    [Fact]
    public async Task RicercaTestoIndicizzata_DaStessiTestDelPercorsoInMemoria_PerOgniCampoDiCorrisponde()
    {
        using var state = CreaStato(90);
        var indicizzato = new HomeViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = true };
        var memoria = new HomeViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = false };
        await AttendiQuieto(indicizzato);
        await AttendiQuieto(memoria);

        var risultatiNonBanali = 0;
        foreach (var ricerca in Ricerche)
        {
            await ImpostaTesto(indicizzato, ricerca);
            await ImpostaTesto(memoria, ricerca);
            indicizzato.AggiornaListeSincrono();
            memoria.AggiornaListeSincrono();

            Assert.True(Firma(memoria.DaFare) == Firma(indicizzato.DaFare),
                $"Da fare, ricerca '{ricerca}': percorso indicizzato diverso dal percorso in memoria.");
            Assert.True(Firma(memoria.Fatti) == Firma(indicizzato.Fatti),
                $"Fatti, ricerca '{ricerca}': percorso indicizzato diverso dal percorso in memoria.");
            risultatiNonBanali += memoria.DaFare.Count + memoria.Fatti.Count;
        }

        // Il dataset deve produrre corrispondenze reali (altrimenti la parita' sarebbe vuota).
        Assert.True(risultatiNonBanali > 0);
        await ImpostaTesto(indicizzato, "");
        await ImpostaTesto(memoria, "");
    }

    [Fact]
    public async Task RicercaTestoIndicizzata_RestaPaginata_EContaTuttiICorrispondenti()
    {
        using var state = CreaStato(260);
        var indicizzato = new HomeViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = true };
        var memoria = new HomeViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = false };
        await ImpostaTesto(indicizzato, "Verifica");
        await ImpostaTesto(memoria, "Verifica");

        indicizzato.AggiornaListeSincrono();
        memoria.AggiornaListeSincrono();

        Assert.True(memoria.DaFare.Count > 100, "Il dataset deve superare una pagina di Da fare.");
        Assert.Equal(100, indicizzato.DaFare.Count);
        Assert.Equal(memoria.DaFare.Count, indicizzato.TotaleDaFare);
        var primaPagina = indicizzato.DaFare.Select(t => t.Id).ToHashSet();
        Assert.Subset(memoria.DaFare.Select(t => t.Id).ToHashSet(), primaPagina);

        indicizzato.PaginaDaFareSuccessivaCommand.Execute(null);
        Assert.Equal(Math.Min(100, memoria.DaFare.Count - 100), indicizzato.DaFare.Count);
        Assert.DoesNotContain(indicizzato.DaFare, t => primaPagina.Contains(t.Id));
    }

    [Fact]
    public async Task RicercaTestoIndicizzata_NonLeggeLArchivioCopiatoDaGetAll()
    {
        using var state = CreaStato(60);
        var indicizzato = new HomeViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = true };
        await ImpostaTesto(indicizzato, "freni");

        var prima = PerfContatori.LeggiSnapshot().TestCaseGetAll;
        indicizzato.AggiornaListeSincrono();
        Assert.Equal(prima, PerfContatori.LeggiSnapshot().TestCaseGetAll);
    }

    /// <summary>Invio nella casella di ricerca (KeyBinding Enter della Home): il testo corrente si applica subito, senza attendere
    /// il ritardo di digitazione.</summary>
    [Fact]
    public async Task TestoRicerca_InvioApplicaSubitoSenzaAttenderIlDebounce()
    {
        using var state = CreaStato(20);
        var vm = new HomeViewModel(state, _ => { });
        await AttendiQuieto(vm);

        vm.TestoRicerca = "freni";
        vm.ConfermaRicercaTestoImmediata();

        Assert.Equal("freni", vm.TestoRicercaApplicato);
        await AttendiQuieto(vm);
    }

    [Fact]
    public async Task TestoRicerca_SiApplicaDopoIlDebounce_ESvuotamentoEImmediato()
    {
        using var state = CreaStato(20);
        var vm = new HomeViewModel(state, _ => { });
        await AttendiQuieto(vm);

        vm.TestoRicerca = "fr";
        vm.TestoRicerca = "fre";
        vm.TestoRicerca = "freni";
        Assert.Equal("", vm.TestoRicercaApplicato);

        await Task.Delay(HomeViewModel.DebounceTestoRicerca + TimeSpan.FromMilliseconds(300));
        Assert.Equal("freni", vm.TestoRicercaApplicato);

        vm.TestoRicerca = "";
        Assert.Equal("", vm.TestoRicercaApplicato);
        await AttendiQuieto(vm);
    }

    [Fact]
    public void MigrazioneV12_ValorizzaNoteEDeviazioniDallaCacheJson()
    {
        string dbPath;
        long attesiNote;
        long attesiDeviazioni;
        using (var state = CreaStato(60))
        {
            dbPath = state.TestCases.LocalIndexDatabasePath!;
            attesiNote = state.TestCases.GetAll().Count(t => t.Esecuzioni.Any(e => e.Note.Contains("nota 3", StringComparison.OrdinalIgnoreCase)));
            attesiDeviazioni = state.TestCases.GetAll().Count(t => t.Esecuzioni.Any(e => e.Deviazioni.Contains("deviazione", StringComparison.OrdinalIgnoreCase)));
        }

        // Riporta l'indice allo schema v12: le colonne v13 non ci sono piu' e la versione e' 12.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE test_revision DROP COLUMN note; ALTER TABLE test_revision DROP COLUMN deviazioni; " +
                                  "UPDATE meta SET value = '12' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        using var riaperto = NuovoStato();
        riaperto.InizializzaCartella(_dir);
        riaperto.TestCases.Count();
        var archivio = new LocalTestArchiveQueryService(dbPath);
        Assert.Equal(attesiNote, archivio.QuerySlice(new LocalTestArchiveQuery { TestoHome = "nota 3" }, 0, 1).TotalCount);
        Assert.Equal(attesiDeviazioni, archivio.QuerySlice(new LocalTestArchiveQuery { TestoHome = "deviazione" }, 0, 1).TotalCount);
    }
}
