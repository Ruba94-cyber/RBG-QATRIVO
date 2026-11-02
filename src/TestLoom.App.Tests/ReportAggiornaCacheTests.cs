using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressione mirata dell'audit prestazioni 2026-09-25 (banco 100k Test): a caldo, con la pagina Report gia'
/// aperta e tenuta viva tra un cambio scheda e l'altro (IPaginaAggiornabile), <see cref="ReportViewModel.Aggiorna"/>
/// rifaceva SEMPRE, senza alcun controllo di invarianza, 3+ scansioni complete dell'archivio Test anche quando
/// nulla era cambiato dall'ultima apertura: <see cref="ReportMatriceService.ValoriDisponibiliPerDimensioni"/>,
/// <see cref="ReportMatriceService.Prepara"/> (CostruisciCombinazioni/CreaCandidati, forzata da un
/// <c>_matricePreparata = null</c> incondizionato) e <see cref="CampoSistemaCatalogoService.ValoriDistinti"/> una
/// volta per ciascun filtro campo custom. Stesso modello di WuStatusRicaricaCacheTests: questi test isolano quella
/// regressione con un archivio a scala ridotta (rispetto al banco 100k reale) ma sufficiente a rendere il costo
/// del popolamento completo misurabile in modo affidabile tramite <see cref="PerfContatori"/> (nessun Load() dei
/// cataloghi master coinvolti significa nessuna delle scansioni evitate), e verificano che N rivisite consecutive
/// senza alcuna modifica ai dati non ripaghino quel costo, che il risultato con cache resti identico al ricalcolo
/// pieno, e che una scrittura locale o da una seconda postazione (stessa cartella condivisa) invalidino comunque
/// la cache alla rivisita successiva.
/// </summary>
[Collection("Scale performance")]
public sealed class ReportAggiornaCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ReportCache_" + Guid.NewGuid().ToString("N"));
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ReportAggiornaCacheTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public void AnteprimaRecentiLimitata_MantieneOrdineStabileESelezionaLeDatePiuRecenti()
    {
        var origine = new DateTime(2026, 1, 1);
        var testCases = Enumerable.Range(0, 500).Select(i => new TestCase
        {
            Id = $"TC-{i:D4}",
            UltimaModifica = origine.AddMinutes(i / 3)
        }).ToList();

        var attesi = testCases.OrderByDescending(tc => tc.UltimaModifica).Take(200).Select(tc => tc.Id).ToList();
        var effettivi = ReportViewModel.SelezionaAnteprimaPiuRecenti(testCases, 200).Select(tc => tc.Id).ToList();

        Assert.Equal(attesi, effettivi);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private (AppState State, TestTemplate Template) CreaStatoPhaseD(string dataRoot, string configPath, int testCount, string operatoreAttivo = "Operatore Prova Report")
    {
        var paths = new DataPaths(dataRoot);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);

        // I file Test vanno scritti su disco PRIMA che una qualunque AppState venga aperta su questa cartella
        // (stesso ordine di WuStatusRicaricaCacheTests.CreaStatoPhaseD): la riconciliazione completa
        // dell'archivio Test avviene nel bootstrap della PRIMA AppState aperta e il suo esito (incluso "zero
        // file trovati") resta persistito nell'indice locale — un'AppState aperta dopo, sulla stessa cartella,
        // non ripete da sola una scansione reale se l'indice risulta gia' completo.
        var template = new TestTemplate { Nome = "Diagnostica base" };
        CreaTestCase(paths, testCount, template.Uid);

        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = operatoreAttivo });

        using (var seed = new AppState(configService, runBackgroundMaintenance: false))
        {
            Assert.Equal(testCount, seed.TestCases.Count());
            seed.Master.TestTemplate.Save(new List<TestTemplate> { template });
            seed.Master.Veicoli.Save(Enumerable.Range(0, 20).Select(i => new Veicolo { Nome = $"Veicolo-{i:D3}", GammaNome = $"Gamma-{i % 4}" }).ToList());
            seed.Master.Funzionalita.Save(Enumerable.Range(0, 10).Select(i => new Funzionalita { Nome = $"Funzionalita-{i}" }).ToList());
        }
        SqliteConnection.ClearAllPools();

        var state = new AppState(configService, runBackgroundMaintenance: false);
        Assert.Equal(testCount, state.TestCases.Count());
        return (state, template);
    }

    private static void CreaTestCase(DataPaths paths, int count, Guid templateUid)
    {
        for (var indice = 0; indice < count; indice++)
        {
            var componente = $"Componente-{indice % 30:D3}";
            var dir = paths.TestCaseDir(componente);
            Directory.CreateDirectory(dir);
            var id = $"TC-{indice:D6}";
            var esito = indice % 5 == 0 ? Esito.NonConforme : Esito.Conforme;
            var testCase = new TestCase
            {
                Uid = Guid.NewGuid(),
                RevisioneDati = 1,
                Id = id,
                DescrizioneBreve = $"Test {indice}",
                Descrizione = "Verifica",
                ModificatoDa = "Operatore-01",
                TestTemplateUid = templateUid,
                Componente = { componente },
                Veicolo = $"Veicolo-{indice % 20:D3}",
                Veicoli = { $"Veicolo-{indice % 20:D3}" },
                Funzionalita = { $"Funzionalita-{indice % 10}" },
                DataCreazione = new DateTime(2026, 8, 1).AddMinutes(indice),
                UltimaModifica = new DateTime(2026, 8, 1).AddMinutes(indice),
                StatoLavoro = StatoLavoroTest.Concluso,
                Esecuzioni =
                {
                    new Esecuzione
                    {
                        Iterazione = 1,
                        Data = new DateTime(2026, 8, 1, 12, 0, 0),
                        Operatore = $"Operatore-{indice % 12:D2}",
                        Esito = esito,
                        Condizioni = { new Condizione { Esito = esito, Funzionalita = $"Funzionalita-{indice % 10}" } },
                    },
                },
            };
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(testCase, JsonIo.Options);
            File.WriteAllBytes(Path.Combine(dir, $"testcase_{id}.json"), bytes);
        }
    }

    /// <summary>N rivisite della STESSA pagina in cache (Aggiorna(), lo stesso percorso di
    /// MainViewModel.OttieniPagina quando la scheda Report e' gia' stata aperta in questa sessione) senza alcuna
    /// scrittura nel mezzo: prima del fix ogni chiamata ripeteva ValoriDisponibiliPerDimensioni/ValoriDistinti/
    /// ReportMatriceService.Prepara sull'intero archivio — misurabile qui perche' ciascuna di quelle chiamate
    /// richiede un Load() dei cataloghi master (PerfContatori.MasterLoad): a dati invariati il delta deve essere
    /// zero, non pari al numero di cataloghi (Veicoli+Funzionalita+TestTemplate) moltiplicato per le rivisite.</summary>
    [Fact]
    [Trait("Category", "Scale")]
    public void Aggiorna_RivisiteRipetuteSenzaModifiche_NonRipeteLeScansioniComplete()
    {
        const int testCount = 8_000;
        var (state, _) = CreaStatoPhaseD(Path.Combine(_root, "data"), Path.Combine(_root, "local", "config.json"), testCount);
        using var _dispose = state;

        var swPrimaApertura = Stopwatch.StartNew();
        var vm = new ReportViewModel(state);
        swPrimaApertura.Stop();
        Assert.Equal(testCount, vm.TotaleRisultatiAnteprima);
        Assert.Equal(200, vm.Anteprima.Count);
        Assert.True(vm.AnteprimaLimitata);
        Assert.Contains("200", vm.TestoAvvisoAnteprimaLimitata);
        Assert.Contains(testCount.ToString(), vm.TestoAvvisoAnteprimaLimitata);
        var masterLoadDopoApertura = PerfContatori.LeggiSnapshot().MasterLoad;
        Assert.True(masterLoadDopoApertura > 0, "Setup non valido: la prima apertura dovrebbe leggere almeno un catalogo master.");

        var tempiRivisita = new List<double>();
        var masterLoadPrimaRivisite = masterLoadDopoApertura;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            ((IPaginaAggiornabile)vm).Aggiorna();
            sw.Stop();
            tempiRivisita.Add(sw.Elapsed.TotalMilliseconds);
        }
        var masterLoadDopoRivisite = PerfContatori.LeggiSnapshot().MasterLoad;

        _output.WriteLine($"BENCHMARK|test={testCount}|prima_apertura_ms={swPrimaApertura.Elapsed.TotalMilliseconds:F1}|" +
            $"rivisite_ms=[{string.Join(",", tempiRivisita.Select(t => t.ToString("F1")))}]|" +
            $"master_load_dopo_apertura={masterLoadDopoApertura}|master_load_dopo_rivisite={masterLoadDopoRivisite}");

        Assert.Equal(masterLoadPrimaRivisite, masterLoadDopoRivisite);

        var sogliaRivisita = Math.Max(50.0, swPrimaApertura.Elapsed.TotalMilliseconds * 0.25);
        foreach (var t in tempiRivisita)
            Assert.True(t < sogliaRivisita, $"Rivisita troppo lenta ({t:F1}ms >= soglia {sogliaRivisita:F1}ms): la cache di Aggiorna() non ha evitato il ricalcolo completo.");
        Assert.True(tempiRivisita[^1] < tempiRivisita[0] + sogliaRivisita,
            $"Le rivisite crescono nel tempo (prima={tempiRivisita[0]:F1}ms, ultima={tempiRivisita[^1]:F1}ms).");
    }

    /// <summary>Contenuto invariato dalla cache: la proiezione riusata su rivisita (EcuDisponibili, matrice,
    /// anteprima) deve restare identica a quella del ricalcolo pieno — la cache accelera, non altera il
    /// risultato.</summary>
    [Fact]
    [Trait("Category", "Scale")]
    public void Aggiorna_RivisitaContenutoIdenticoAllaPrimaApertura()
    {
        var (state, _) = CreaStatoPhaseD(Path.Combine(_root, "data2"), Path.Combine(_root, "local2", "config.json"), 300);
        using var _dispose = state;

        var vm = new ReportViewModel(state);
        var ecuPrima = vm.EcuDisponibili.ToList();
        var veicoliPrima = vm.VeicoliDisponibili.ToList();
        var totalePrima = vm.TotaleRisultatiAnteprima;
        // Appiattita in tuple di soli valori primitivi (Assert.Equal su List<(string, List<...>)> annidata
        // confronta la List interna per riferimento tramite l'uguaglianza di default di ValueTuple, non per
        // struttura: qui si confronta invece l'intero contenuto, riga per riga e cella per cella).
        var matricePrima = vm.RigheMatrice.SelectMany(r => r.Celle.Select(c => (r.Riga, c.Colonna, c.Totali, c.Conformi, c.Valutati))).ToList();
        var colonnePrima = vm.ColonneMatrice.ToList();

        ((IPaginaAggiornabile)vm).Aggiorna();
        ((IPaginaAggiornabile)vm).Aggiorna();

        var matriceDopo = vm.RigheMatrice.SelectMany(r => r.Celle.Select(c => (r.Riga, c.Colonna, c.Totali, c.Conformi, c.Valutati))).ToList();

        Assert.Equal(ecuPrima, vm.EcuDisponibili);
        Assert.Equal(veicoliPrima, vm.VeicoliDisponibili);
        Assert.Equal(totalePrima, vm.TotaleRisultatiAnteprima);
        Assert.Equal(colonnePrima, vm.ColonneMatrice);
        Assert.Equal(matricePrima, matriceDopo);
    }

    /// <summary>La cache non deve mai mostrare dati stantii: un nuovo Test salvato localmente (stesso percorso
    /// di TestCaseRepository.Save) deve riflettersi (nuovo componente disponibile, conteggio anteprima aggiornato) alla
    /// rivisita immediatamente successiva, perche' cambia l'identita' per-riferimento di TestCases.GetAll().</summary>
    [Fact]
    [Trait("Category", "Scale")]
    public void Aggiorna_DopoTestSalvatoLocalmente_RiflettePropostoStatoAggiornato()
    {
        var (state, template) = CreaStatoPhaseD(Path.Combine(_root, "data3"), Path.Combine(_root, "local3", "config.json"), 50);
        using var _dispose = state;

        var vm = new ReportViewModel(state);
        var totalePrima = vm.TotaleRisultatiAnteprima;
        Assert.DoesNotContain("Componente-NUOVA", vm.EcuDisponibili);

        state.TestCases.Save(new TestCase
        {
            Id = "T-LOCALE-NUOVO",
            TestTemplateUid = template.Uid,
            Componente = { "Componente-NUOVA" },
            Veicoli = { "Veicolo-000" },
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);

        ((IPaginaAggiornabile)vm).Aggiorna();

        Assert.Equal(totalePrima + 1, vm.TotaleRisultatiAnteprima);
        Assert.Contains("Componente-NUOVA", vm.EcuDisponibili);
    }

    /// <summary>Con un filtro componente attivo il risultato arriva dall'indice (QueryAllAnalisi) e viene riusato dalla cache del filtro
    /// finche' indice e Generazione restano invariati. Un Test salvato localmente con la stessa componente deve comparire alla rivisita
    /// successiva: la cache segue il token dell'indice, non solo la chiave del filtro.</summary>
    [Fact]
    public void Aggiorna_ConFiltroIndicizzato_RiusaSenzaModificheERiflettePoiUnNuovoTest()
    {
        var (state, template) = CreaStatoPhaseD(Path.Combine(_root, "data4"), Path.Combine(_root, "local4", "config.json"), 50);
        using var _dispose = state;

        var vm = new ReportViewModel(state);
        var componente = vm.EcuDisponibili.First();
        vm.FiltroEcu = componente;
        var conFiltro = vm.TotaleRisultatiAnteprima;
        Assert.True(conFiltro > 0);

        ((IPaginaAggiornabile)vm).Aggiorna();
        Assert.Equal(conFiltro, vm.TotaleRisultatiAnteprima);

        state.TestCases.Save(new TestCase
        {
            Id = "T-FILTRO-NUOVO",
            TestTemplateUid = template.Uid,
            Componente = { componente },
            Veicoli = { "Veicolo-000" },
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);

        ((IPaginaAggiornabile)vm).Aggiorna();
        Assert.Equal(conFiltro + 1, vm.TotaleRisultatiAnteprima);
    }

    /// <summary>Stessa garanzia di non-stantio quando cambia un catalogo master usato dalla pagina (TestTemplate):
    /// disarchiviare/archiviare un template deve aggiornare FiltroTemplate alla rivisita successiva.</summary>
    [Fact]
    [Trait("Category", "Scale")]
    public void Aggiorna_DopoModificaCatalogoMasterLocale_AggiornaFiltroTemplate()
    {
        var (state, template) = CreaStatoPhaseD(Path.Combine(_root, "data4"), Path.Combine(_root, "local4", "config.json"), 20);
        using var _dispose = state;

        var altroTemplate = new TestTemplate { Nome = "Altro template" };
        var catalogo = state.Master.TestTemplate.Load();
        var attesa = EntityStateFingerprint.Clone(catalogo);
        catalogo.Add(altroTemplate);
        state.Master.TestTemplate.Save(catalogo, attesa);

        var vm = new ReportViewModel(state);
        Assert.Contains(vm.FiltroTemplate.Opzioni, o => o.Etichetta == altroTemplate.Nome);

        // Archivia il nuovo template dalla stessa postazione: la rivisita successiva deve rimuoverlo dalle
        // opzioni disponibili (stesso filtro "!t.Archiviata" di Aggiorna()).
        var catalogo2 = state.Master.TestTemplate.Load();
        var attesa2 = EntityStateFingerprint.Clone(catalogo2);
        catalogo2.First(t => t.Uid == altroTemplate.Uid).Archiviata = true;
        state.Master.TestTemplate.Save(catalogo2, attesa2);

        ((IPaginaAggiornabile)vm).Aggiorna();

        Assert.DoesNotContain(vm.FiltroTemplate.Opzioni, o => o.Etichetta == altroTemplate.Nome);
    }

    /// <summary>Marcatura N.A. su una cella (Config locale, MatriceNonApplicabileService): non entra nella firma
    /// di invarianza (e' economica, ApplicaMatrice la rilegge sempre), ma deve comunque comparire alla rivisita
    /// successiva anche quando archivio e cataloghi sono altrimenti invariati — la cache non deve "congelare" la
    /// finestra della matrice sull'overlay precedente.</summary>
    [Fact]
    [Trait("Category", "Scale")]
    public void Aggiorna_DopoMarcaturaNonApplicabile_MostraLaMarcaturaAllaRivisita()
    {
        var (state, _) = CreaStatoPhaseD(Path.Combine(_root, "data5"), Path.Combine(_root, "local5", "config.json"), 40);
        using var _dispose = state;

        var vm = new ReportViewModel(state);
        Assert.NotNull(vm.AsseXMatrice);
        Assert.NotNull(vm.AsseYMatrice);
        var cella = vm.RigheMatrice.SelectMany(r => r.Celle).First();
        Assert.False(cella.NonApplicabile);

        var applicata = MatriceNonApplicabileService.Imposta(state.Config, vm.AsseXMatrice!.Id, vm.AsseYMatrice!.Id, cella.Colonna, cella.Riga, "Nota di prova");
        Assert.True(applicata);
        state.ConfigService.Save(state.Config);

        ((IPaginaAggiornabile)vm).Aggiorna();

        var cellaAggiornata = vm.RigheMatrice.SelectMany(r => r.Celle).First(c => c.Colonna == cella.Colonna && c.Riga == cella.Riga);
        Assert.True(cellaAggiornata.NonApplicabile);
    }

    /// <summary>Cambio ruolo operatore senza alcun'altra modifica: incluso per difesa nella firma di invarianza
    /// (stesso principio di WuStatusViewModel.Ricarica) anche se nessun calcolo di questa pagina dipende oggi dal
    /// ruolo. Verifica che il cambio ruolo forzi comunque un ripopolamento pieno (nessun falso "dati invariati"),
    /// cosi' che un futuro filtro/dimensione dipendente dal ruolo non possa mai riusare una proiezione stantia.</summary>
    [Fact]
    [Trait("Category", "Scale")]
    public void Aggiorna_DopoCambioRuoloSenzaAltraModifica_ForzaUnNuovoPopolamento()
    {
        var dataRoot = Path.Combine(_root, "data_ruolo");
        var configPath = Path.Combine(_root, "local_ruolo", "config.json");
        var paths = new DataPaths(dataRoot);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);

        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Master Prova" });
        using (var seed = new AppState(configService, runBackgroundMaintenance: false))
        {
            var operatori = seed.Master.Operatori.Load().ToList();
            operatori.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1111"), ModificatoDa = "Test" }));
            operatori.Add(TestOperatori.Sigilla(new Operatore { Nome = "Standard Prova", Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("2222"), ModificatoDa = "Test" }));
            seed.Master.Operatori.Save(operatori);
        }
        SqliteConnection.ClearAllPools();

        var state = new AppState(configService, runBackgroundMaintenance: false);
        using var _dispose = state;
        Assert.True(state.ImpostaOperatore("Master Prova", "1111"));

        var vm = new ReportViewModel(state);
        var masterLoadDopoApertura = PerfContatori.LeggiSnapshot().MasterLoad;

        // Rivisita a dati/ruolo invariati: nessun nuovo Load() dei cataloghi.
        ((IPaginaAggiornabile)vm).Aggiorna();
        Assert.Equal(masterLoadDopoApertura, PerfContatori.LeggiSnapshot().MasterLoad);

        // Cambio ruolo, nessun'altra modifica: la firma di invarianza include EMaster, quindi la rivisita
        // successiva NON deve essere trattata come "dati invariati" (deve rileggere i cataloghi).
        Assert.True(state.ImpostaOperatore("Standard Prova", "2222"));
        ((IPaginaAggiornabile)vm).Aggiorna();
        Assert.True(PerfContatori.LeggiSnapshot().MasterLoad > masterLoadDopoApertura,
            "Il cambio ruolo senza altre modifiche non ha forzato un nuovo popolamento: la firma di invarianza non include (o ha smesso di includere) EMaster.");
    }

    /// <summary>Richiesta utente (seconda postazione): un nuovo Test e una modifica al catalogo TestTemplate
    /// fatti da un'ALTRA postazione sulla stessa cartella condivisa devono comparire nella pagina della PRIMA
    /// postazione dopo il refresh reale (<see cref="TestCaseRepository.RefreshIndex"/> per l'archivio Test; il
    /// catalogo Master Phase D non richiede un refresh dedicato, Load()/CatturaImpronteCatalogoCorrente leggono
    /// gia' lo stato condiviso piu' recente). "Seconda postazione" = una seconda istanza AppState indipendente
    /// puntata sulla stessa cartella condivisa, stesso schema di WuStatusRicaricaCacheTests.</summary>
    [Fact]
    [Trait("Category", "Scale")]
    public void Aggiorna_DopoModificaEsternaDiSecondaPostazione_RiflettePropostoStatoAggiornato()
    {
        var dataRoot = Path.Combine(_root, "data_seconda_postazione");
        var configPath = Path.Combine(_root, "local_seconda_postazione", "config.json");
        var (stato1, template) = CreaStatoPhaseD(dataRoot, configPath, 30);
        using var _dispose1 = stato1;

        var vm = new ReportViewModel(stato1);
        var totalePrima = vm.TotaleRisultatiAnteprima;
        Assert.DoesNotContain("Componente-SECONDA", vm.EcuDisponibili);
        Assert.DoesNotContain(vm.FiltroTemplate.Opzioni, o => o.Etichetta == "Template seconda postazione");

        // Seconda postazione: stessa cartella condivisa (dataRoot), config e cartella locale separati.
        var configService2 = new AppConfigService(Path.Combine(_root, "local_seconda_postazione_B", "config.json"));
        var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        stato2.InizializzaCartella(dataRoot);

        // (a) nuovo Test dalla seconda postazione.
        stato2.TestCases.Save(new TestCase
        {
            Id = "T-SECONDA-POSTAZIONE",
            TestTemplateUid = template.Uid,
            Componente = { "Componente-SECONDA" },
            Veicoli = { "Veicolo-000" },
            ModificatoDa = "Seconda Postazione",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);

        // (b) nuovo TestTemplate dalla seconda postazione.
        var catalogo2 = stato2.Master.TestTemplate.Load();
        var attesa2 = EntityStateFingerprint.Clone(catalogo2);
        catalogo2.Add(new TestTemplate { Nome = "Template seconda postazione" });
        stato2.Master.TestTemplate.Save(catalogo2, attesa2);
        stato2.Dispose();

        // Meccanismo reale: RefreshIndex() sull'archivio Test della prima postazione (percorso timer/watcher,
        // qui esplicito e deterministico); il catalogo Master (Phase D) non richiede un refresh dedicato.
        stato1.TestCases.RefreshIndex();

        ((IPaginaAggiornabile)vm).Aggiorna();

        Assert.Equal(totalePrima + 1, vm.TotaleRisultatiAnteprima);
        Assert.Contains("Componente-SECONDA", vm.EcuDisponibili);
        Assert.Contains(vm.FiltroTemplate.Opzioni, o => o.Etichetta == "Template seconda postazione");
    }
}
