using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public class GlobalSearchAndTableLayoutTests
{
    [Fact]
    public void ChiaveLayout_ESeparataPerUtenteRuoloETabella()
    {
        Assert.Equal("Mario|Standard|test-list", TableLayoutPreferences.Chiave("  Mario ", RuoloOperatore.Standard, "test-list"));
        Assert.NotEqual(
            TableLayoutPreferences.Chiave("Mario", RuoloOperatore.Standard, "test-list"),
            TableLayoutPreferences.Chiave("Mario", RuoloOperatore.Master, "test-list"));
    }

    [Fact]
    public void AppConfig_PersisteLayoutCompletoDellaTabella()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_TableLayout", Guid.NewGuid().ToString("N"));
        var service = new AppConfigService(Path.Combine(dir, "config.json"));
        var config = new AppConfig();
        config.LayoutTabelle["Mario|Master|test-list"] = new TableLayoutPreference
        {
            Colonne = [new() { Indice = 2, Ordine = 0, Larghezza = 245, Visibile = false }]
        };

        service.Save(config);
        var letta = service.Load().LayoutTabelle["Mario|Master|test-list"].Colonne.Single();

        Assert.Equal(2, letta.Indice);
        Assert.Equal(245, letta.Larghezza);
        Assert.False(letta.Visibile);
    }

    [Fact]
    public void RicercaGlobale_OperatoreVedeSoloTruckDelleGammeAssegnateENonIMasterData()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "TestLoom_GlobalSearch", Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dataDir);
        state.InizializzaCartella(dataDir);
        state.Master.Veicoli.Save([
            new() { Nome = "Truck Alpha", GammaNome = "Gamma A" },
            new() { Nome = "Truck Beta", GammaNome = "Gamma B" }
        ]);
        state.Master.Componente.Save([new() { Nome = "Componente-SECRET" }]);
        state.Master.Operatori.Save([new()
        {
            Nome = "Operatore A", Ruolo = RuoloOperatore.Standard, GammeAssegnate = ["Gamma A"],
            PinHash = PinService.Hash("1234")
        }]);
        state.TestCases.Save(new TestCase { Id = "TEST-ALPHA", Veicolo = "Truck Alpha", Componente = "Componente-A", Descrizione = "Ricerca motore" }, true);
        state.TestCases.Save(new TestCase { Id = "TEST-BETA", Veicolo = "Truck Beta", Componente = "Componente-B", Descrizione = "Ricerca motore" }, true);
        state.RicaricaTutto();
        Assert.True(state.ImpostaOperatore("Operatore A", "1234"));
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);

        vm.Query = "TEST";
        Assert.Contains(vm.Risultati, x => x.Titolo == "TEST-ALPHA");
        Assert.DoesNotContain(vm.Risultati, x => x.Titolo == "TEST-BETA");

        vm.Query = "SECRET";
        Assert.Empty(vm.Risultati);
    }

    // Addendum: audit prestazioni 2026-08-25 — verifica che il debounce di produzione (default) sia
    // effettivamente attivo, non solo che debounceMs:0 lo bypassi: senza attendere il timer, un carattere
    // appena digitato non deve ancora aver popolato Risultati.
    [Fact]
    public void RicercaGlobale_DebounceDiDefault_NonEseguiceLaRicercaSubito()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "TestLoom_GlobalSearchDebounce", Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dataDir);
        state.InizializzaCartella(dataDir);
        state.TestCases.Save(new TestCase { Id = "TEST-GAMMA", Veicolo = "Truck", Componente = "Componente-X" }, true);
        state.RicaricaTutto();
        var vm = new GlobalSearchViewModel(state, _ => { });

        vm.Query = "TEST";

        Assert.Empty(vm.Risultati);
    }

    [Fact]
    public async Task RicercaGlobale_MasterLegacyCorrottoDopoBootstrapPhaseD_NonHaAlcunEffetto()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "TestLoom_GlobalSearchStale", Guid.NewGuid().ToString("N"));
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(dataDir);
        state.InizializzaCartella(dataDir);
        state.Master.Componente.Save([new Componente { Nome = "Componente-STALE", ModificatoDa = "Seed" }]);
        state.Master.Requisiti.Save([new Requisito
        {
            Id = "REQ-STALE", Testo = "Requisito stale ancora valido", ModificatoDa = "Seed"
        }]);
        state.Master.Funzionalita.Save([new Funzionalita { Nome = "FUNZ-HEALTHY", ModificatoDa = "Seed" }]);
        state.Master.Operatori.Save([TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Search", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Seed"
        })]);
        state.TestCases.Save(new TestCase
        {
            Id = "TEST-STALE", Veicolo = "Truck", Componente = "Componente-X", Descrizione = "Test sano durante errore master"
        }, true);
        state.RicaricaTutto();
        Assert.True(state.ImpostaOperatore("Master Search", "1234"));

        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);
        await vm.AttendiRefreshIndiceMasterAsync();
        vm.Query = "STALE";
        Assert.Contains(vm.Risultati, x => x.Tipo == "Componente" && x.Titolo == "Componente-STALE");
        Assert.Contains(vm.Risultati, x => x.Titolo == "REQ-STALE");
        Assert.Contains(vm.Risultati, x => x.Titolo == "TEST-STALE");

        // MasterDataLocalIndexCoordinator documenta esplicitamente che, in un workspace Phase D, "il vecchio
        // JSON monolitico non viene piu consultato da paging/search": dopo il bootstrap la fonte reale e' il
        // mutation journal materializzato, non piu questo file. Corrompere il mirror legacy qui non deve quindi
        // avere ALCUN effetto sulla ricerca — se in futuro un refactor reintroducesse per errore una lettura
        // diretta di questo file, questo test lo scoprirebbe (il componente sparirebbe/l'indice si romperebbe).
        await vm.AttendiRefreshIndiceMasterAsync();
        File.WriteAllText(state.Paths.EcuFile, "{ json componente volutamente non valido per il test");
        await vm.AttendiRefreshIndiceMasterAsync();

        vm.Query = "RESET";
        vm.Query = "STALE";

        Assert.Contains(vm.Risultati, x => x.Tipo == "Componente" && x.Titolo == "Componente-STALE");
        Assert.Contains(vm.Risultati, x => x.Titolo == "REQ-STALE");
        Assert.Contains(vm.Risultati, x => x.Titolo == "TEST-STALE");
    }

    [Fact]
    public void RicercaGlobale_VeicoliLegacyCorrottoDopoBootstrapPhaseD_NonBloccaNulla()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "TestLoom_GlobalSearchDomainIsolation", Guid.NewGuid().ToString("N"));
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(dataDir);
        state.InizializzaCartella(dataDir);
        state.Master.Veicoli.Save([new Veicolo { Nome = "Truck Alpha", GammaNome = "Gamma A", ModificatoDa = "Seed" }]);
        state.Master.Operatori.Save([TestOperatori.Sigilla(new Operatore
        {
            Nome = "Standard Search", Ruolo = RuoloOperatore.Standard, GammeAssegnate = ["Gamma A"],
            PinHash = PinService.Hash("1234"), ModificatoDa = "Seed"
        })]);
        state.TestCases.Save(new TestCase
        {
            Id = "TEST-HEALTHY", Veicolo = "Truck Alpha", Componente = "Componente-X", Descrizione = "HEALTHY test"
        }, true);
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-HEALTHY", Titolo = "HEALTHY change request", Descrizione = "Dominio indipendente"
        });
        state.RicaricaTutto();
        Assert.True(state.ImpostaOperatore("Standard Search", "1234"));

        // Come per il catalogo componente (vedi RicercaGlobale_MasterLegacyCorrottoDopoBootstrapPhaseD_NonHaAlcunEffetto),
        // Veicoli e' anch'esso un catalogo migrato a Phase D: dopo il bootstrap questo mirror legacy non viene
        // piu' letto ne' dal fallback di ricerca ne' dal filtro Gamma/Standard, che vedono invece i dati reali
        // e ancora validi materializzati dal journal. Corromperlo qui non deve quindi bloccare NULLA: ne' il
        // Test (la cui visibilita' per lo Standard resta correttamente filtrata sui dati Veicoli reali, non
        // su questo file), ne' la Change Request di un dominio indipendente.
        File.WriteAllText(state.Paths.VeicoliFile, "{ json Veicoli volutamente non valido per il test");
        File.SetLastWriteTimeUtc(state.Paths.VeicoliFile, DateTime.UtcNow.AddSeconds(2));
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);

        vm.Query = "HEALTHY";

        Assert.Contains(vm.Risultati, x => x.Titolo == "CR-HEALTHY" && x.Destinazione == "change-request");
        Assert.Contains(vm.Risultati, x => x.Titolo == "TEST-HEALTHY" && x.Destinazione == "test");
    }

    [Fact]
    public void RicercaGlobale_TrovaSegnalazionePerDescrizioneEConservaUidPerAperturaDiretta()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "TestLoom_GlobalSearchSignal", Guid.NewGuid().ToString("N"));
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(dataDir);
        state.InizializzaCartella(dataDir);
        var condizione = new Condizione
        {
            Componente = new EcuSelection(new[] { "BCM" }),
            Funzionalita = "Frenata",
            DescrizioneProblema = "SEGNALAZIONE-TERMINE distintivo",
            Esito = Esito.NonConforme
        };
        var test = new TestCase
        {
            Id = "TEST-SIGNAL-SEARCH", Veicolo = "Truck A", Veicoli = { "Truck A" }, Componente = { "BCM" },
            Esecuzioni = { new Esecuzione { Iterazione = 1, Condizioni = { condizione } } }
        };
        state.TestCases.Save(test, true);
        var segnalazione = new SegnalazioneDaClassificare
        {
            TestId = test.Id, TestUid = test.Uid, EsecuzioneIterazione = 1,
            CondizioneUid = condizione.Uid, Stato = StatoSegnalazione.DaClassificare
        };
        state.Segnalazioni.Save(segnalazione);
        state.RicaricaTutto();
        GlobalSearchResult? opened = null;
        var vm = new GlobalSearchViewModel(state, result => opened = result, debounceMs: 0);

        vm.Query = "SEGNALAZIONE-TERMINE";

        var result = Assert.Single(vm.Risultati.Where(x => x.Destinazione == "segnalazione"));
        Assert.Equal(test.Id, result.ElementoId);
        Assert.Equal(segnalazione.Uid, result.ElementoUid);
        Assert.Contains("Frenata", result.Sottotitolo);
        vm.ApriCommand.Execute(result);
        Assert.Equal(result, opened);
    }

    [Fact]
    public void RicercaGlobale_SegnalazioneNonVisibile_NonVieneEsportataAOperatoreStandard()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "TestLoom_GlobalSearchSignalScope", Guid.NewGuid().ToString("N"));
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(dataDir);
        state.InizializzaCartella(dataDir);
        state.Master.Veicoli.Save([
            new Veicolo { Nome = "Truck visibile", GammaNome = "Gamma A" },
            new Veicolo { Nome = "Truck riservato", GammaNome = "Gamma B" }
        ]);
        state.Master.Operatori.Save([TestOperatori.Sigilla(new Operatore
        {
            Nome = "Standard Search", Ruolo = RuoloOperatore.Standard, GammeAssegnate = ["Gamma A"],
            PinHash = PinService.Hash("1234")
        })]);
        var condizione = new Condizione
        {
            Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Frenata",
            DescrizioneProblema = "SEGRETO-SIGNAL-SEARCH", Esito = Esito.NonConforme
        };
        var test = new TestCase
        {
            Id = "TEST-PRIVATE-SIGNAL", Veicolo = "Truck riservato", Veicoli = { "Truck riservato" },
            Componente = { "BCM" }, Esecuzioni = { new Esecuzione { Iterazione = 1, Condizioni = { condizione } } }
        };
        state.TestCases.Save(test, true);
        state.Segnalazioni.Save(new SegnalazioneDaClassificare
        {
            TestId = test.Id, TestUid = test.Uid, EsecuzioneIterazione = 1,
            CondizioneUid = condizione.Uid, Stato = StatoSegnalazione.DaClassificare
        });
        state.RicaricaTutto();
        Assert.True(state.ImpostaOperatore("Standard Search", "1234"));
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);

        vm.Query = "SEGRETO-SIGNAL-SEARCH";

        Assert.DoesNotContain(vm.Risultati, x => x.Destinazione == "segnalazione");
        Assert.DoesNotContain(vm.Risultati, x => x.Titolo == test.Id);
    }
}
