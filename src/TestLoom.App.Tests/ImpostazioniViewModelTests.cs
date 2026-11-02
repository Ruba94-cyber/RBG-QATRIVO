using System.Linq;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed class ImpostazioniViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_impostazionivm_" + Guid.NewGuid().ToString("N"));

    private AppState CreaStatoConEtichettaRuoloRinominata(RuoloCampoSistema ruolo, string nuovaEtichetta)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        var pianificazione = state.Master.CampiSistemaPianificazione.Load();
        var voce = pianificazione.SingleOrDefault(v => v.Ruolo == ruolo);
        if (voce != null)
        {
            voce.Etichetta = nuovaEtichetta;
            state.Master.CampiSistemaPianificazione.Save(pianificazione);
            return state;
        }

        var dettagli = state.Master.CampiSistemaDettagli.Load();
        dettagli.Single(v => v.Ruolo == ruolo).Etichetta = nuovaEtichetta;
        state.Master.CampiSistemaDettagli.Save(dettagli);
        return state;
    }

    private AppState CreaStatoMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var operatori = state.Master.Operatori.Load();
        operatori.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Impostazioni",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Master Impostazioni", "1234"));
        return state;
    }

    [Fact]
    public void CopieDiConflitto_CopiaDiConflittoDelTestCase_ÈVisibileEIgnorabileDallaViewModel()
    {
        // Fix revisione release/6.4.2: AppState.TestCases.CopieDiConflitto veniva calcolata ma nessuna
        // ViewModel/XAML la leggeva mai (elenco morto, invisibile all'utente). Verifica che ora arrivi fino a
        // ImpostazioniViewModel e che "Ignora" la tolga dalla vista senza toccare il file su disco.
        var state = CreaStatoMaster();
        var originale = new TestCase { Id = "T-CDC-0001", Componente = "ECM", Veicolo = "Daily", Descrizione = "autentico" };
        state.TestCases.Save(originale, isNew: true);
        var pathOriginale = state.TestCases.PercorsoPer(originale);
        // 6.4.2: una copia IDENTICA all'originale non richiede nulla all'utente e non viene mostrata.
        var pathCopiaIdentica = pathOriginale.Replace(".json", "-PCIDENTICO.json");
        File.Copy(pathOriginale, pathCopiaIdentica);
        File.SetLastWriteTimeUtc(pathCopiaIdentica, DateTime.UtcNow.AddMinutes(-5));
        // Una copia con contenuto DIVERSO (stesso Test, versione mai registrata) va mostrata, ma solo dopo la
        // tolleranza: qui la data del file è già oltre i 2 minuti.
        var pathCopiaConflitto = pathOriginale.Replace(".json", "-ALTROPC.json");
        var copiaDiversa = JsonIo.TryRead<TestCase>(pathOriginale, out _)!;
        copiaDiversa.Descrizione = "modificato su un'altra postazione";
        File.WriteAllText(pathCopiaConflitto, System.Text.Json.JsonSerializer.Serialize(copiaDiversa, JsonIo.Options));
        File.SetLastWriteTimeUtc(pathCopiaConflitto, DateTime.UtcNow.AddMinutes(-5));
        state.TestCases.RefreshIndex();

        var vm = new ImpostazioniViewModel(state, () => { });

        Assert.True(vm.HaCopieDiConflitto);
        var copia = Assert.Single(vm.CopieDiConflitto);
        Assert.Equal(pathCopiaConflitto, copia.Percorso, ignoreCase: true);

        vm.IgnoraCopiaDiConflittoCommand.Execute(copia);

        Assert.False(vm.HaCopieDiConflitto);
        Assert.Empty(vm.CopieDiConflitto);
        Assert.True(File.Exists(pathCopiaConflitto)); // "Ignora" non tocca mai il file fisico
    }

    [Fact]
    public void SottoSezioni_TitoloVeicoli_SeguelEtichettaRinominataDelRuoloVeicolo()
    {
        var state = CreaStatoConEtichettaRuoloRinominata(RuoloCampoSistema.Veicolo, "Automezzi");
        var vm = new ImpostazioniViewModel(state, () => { });

        Assert.Contains(vm.SottoSezioni, s => s.Titolo == "Automezzi");
        Assert.DoesNotContain(vm.SottoSezioni, s => s.Titolo == "Veicoli");
    }

    [Fact]
    public void SottoSezioni_TitoloGenerale_NonCambiaMai()
    {
        var state = CreaStatoConEtichettaRuoloRinominata(RuoloCampoSistema.Veicolo, "Automezzi");
        var vm = new ImpostazioniViewModel(state, () => { });

        Assert.Contains(vm.SottoSezioni, s => s.Titolo == "Generale");
    }

    [Fact]
    public void GammaViewModel_TitoloGamma_SeguelEtichettaRinominata()
    {
        var state = CreaStatoConEtichettaRuoloRinominata(RuoloCampoSistema.Gamma, "Classe veicolare");
        var vm = new GammaViewModel(state);

        Assert.Equal("Classe veicolare", vm.TitoloGamma);
    }

    [Fact]
    public void CategoriaViewModel_TitoloCategoria_SeguelEtichettaRinominata()
    {
        var state = CreaStatoConEtichettaRuoloRinominata(RuoloCampoSistema.Categoria, "Raggruppamento");
        var vm = new CategoriaViewModel(state);

        Assert.Equal("Raggruppamento", vm.TitoloCategoria);
    }

    [Fact]
    public void SottoSezioni_RispettaLOrdineSalvatoInConfig()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        state.Config.OrdineImpostazioni = new List<string> { "tipiditest", "generale" };

        var vm = new ImpostazioniViewModel(state, () => { });

        Assert.Equal(new[] { "tipiditest", "generale" }, vm.SottoSezioni.Take(2).Select(s => s.Id));
    }

    [Fact]
    public void NavigaViaDaGenerale_NonEntraInRicorsioneInfinita()
    {
        // Regressione: PaginaCorrente parte come "this" (pagina "Generale") e questa classe implementa
        // anch'essa IDisposable — SostituisciPaginaCorrente/Dispose devono escludere esplicitamente il
        // riferimento a se stessi, altrimenti disposabile.Dispose() richiama se stesso all'infinito
        // (stack overflow non catturabile, crash immediato dell'app al primo click su una sotto-sezione).
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var vm = new ImpostazioniViewModel(state, () => { });

        vm.SottoSezioneSelezionata = vm.SottoSezioni.Single(s => s.Id == "operatori");

        Assert.IsType<OperatoriViewModel>(vm.PaginaCorrente);

        vm.SottoSezioneSelezionata = vm.SottoSezioni.Single(s => s.Id == "generale");
        Assert.Same(vm, vm.PaginaCorrente);

        vm.Dispose();
    }

    [Fact]
    public void NavPrincipale_QuandoPassato_VieneEsposto()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var nav = new System.Collections.ObjectModel.ObservableCollection<NavItem> { new() { Id = "home", Titolo = "Home" } };

        var vm = new ImpostazioniViewModel(state, () => { }, nav);

        Assert.Same(nav, vm.NavPrincipale);
    }

    [Fact]
    public void Ticketing_ConflittoRemoto_NonPerdeLeSelezioniLocali()
    {
        using var state = CreaStatoMaster();
        var seed = new PolarionExportConfig
        {
            SezioniDiDefault = new List<string> { PolarionSezioni.DatiGenerali }
        };
        PolarionConfigService.Save(state.Paths, seed);

        using var vm = new ImpostazioniViewModel(state, () => { });
        var locale = vm.PolarionSezioniOpzioni.Single(o => o.Valore == PolarionSezioni.Operazioni);
        locale.Selezionato = true;

        // Un altro Master salva dopo l'apertura della pagina Impostazioni.
        var remoto = PolarionConfigService.Load(state.Paths);
        remoto.SezioniDiDefault = new List<string> { PolarionSezioni.Riferimenti };
        PolarionConfigService.Save(state.Paths, remoto);

        vm.SalvaSezioniPolarionCommand.Execute(null);

        Assert.True(locale.Selezionato); // working copy dell'utente preservata per confronto/ripetizione.
        Assert.True(vm.HaMessaggioErrore);
        Assert.Null(vm.Messaggio);
        var autorevole = PolarionConfigService.Load(state.Paths);
        Assert.Equal(new[] { PolarionSezioni.Riferimenti }, autorevole.SezioniDiDefault);
    }

    [Fact]
    public void ApriCartellaAllegati_SeIlBucketNonEsiste_NonLoCreaComeSideEffect()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        if (Directory.Exists(state.Paths.AllegatiRootDir))
            Directory.Delete(state.Paths.AllegatiRootDir, recursive: true);
        Assert.False(Directory.Exists(state.Paths.AllegatiRootDir));

        using var vm = new ImpostazioniViewModel(state, () => { });
        // 6.6.1 (correzione 1): la verifica della cartella avviene fuori dal thread UI: si attende il completamento del comando.
        vm.ApriCartellaAllegatiCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        Assert.False(Directory.Exists(state.Paths.AllegatiRootDir));
        Assert.True(vm.HaMessaggioErrore);
    }

    /// <summary>Review 2026-09-25 (MasterLetturaScope aggiunto attorno a SostituisciPaginaCorrente, perf
    /// rivisita Veicoli/Funzionalità): lo scope deve restare interamente confinato alla costruzione sincrona
    /// della sotto-pagina — non deve ne' restare aperto dopo che la navigazione e' tornata al chiamante, ne'
    /// far si' che un Salva() eseguito subito dopo lavori su una fotografia superata. Una seconda postazione
    /// scrive un nuovo Veicolo SUBITO dopo la navigazione (quindi dopo che lo scope si e' gia' richiuso): il
    /// salvataggio locale che segue deve preservare quella scrittura remota (Phase D, merge per-UID) invece di
    /// sovrascriverla o fallire per un conflitto fittizio basato su una lettura accorpata/obsoleta.</summary>
    [Fact]
    public async Task SostituisciPaginaCorrente_MasterLetturaScope_SalvataggioSubitoDopoNavigazione_VedeStatoAggiornato()
    {
        var state = CreaStatoMaster();
        using var impostazioni = new ImpostazioniViewModel(state, () => { });

        Assert.Null(MasterLetturaScope.IdCorrente);
        var navVeicoli = impostazioni.SottoSezioni.Single(s => s.Id == "veicoli");
        impostazioni.SottoSezioneSelezionata = navVeicoli;

        // Lo scope aperto da SostituisciPaginaCorrente e' richiuso non appena item.Crea() ritorna: non deve
        // restare agganciato al thread di test oltre la navigazione stessa.
        Assert.Null(MasterLetturaScope.IdCorrente);

        var vm = Assert.IsType<VeicoliViewModel>(impostazioni.PaginaCorrente);

        // Seconda postazione: scrive un nuovo Veicolo sulla STESSA cartella condivisa, dopo che la navigazione
        // (e il suo MasterLetturaScope) e' gia' terminata.
        var dirLocale2 = Path.Combine(Path.GetTempPath(), "TestLoom_impostazionivm_postazione2_" + Guid.NewGuid().ToString("N"));
        var configService2 = new AppConfigService(Path.Combine(dirLocale2, "config.json"));
        var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        try
        {
            stato2.InizializzaCartella(_dir);
            var veicoli2 = stato2.Master.Veicoli.Load();
            veicoli2.Add(new Veicolo { Nome = "Veicolo Da Postazione 2" });
            stato2.Master.Veicoli.Save(veicoli2);
        }
        finally
        {
            stato2.Dispose();
            if (Directory.Exists(dirLocale2)) { try { Directory.Delete(dirLocale2, true); } catch { } }
        }

        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "Veicolo Locale Dopo Navigazione";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        var autorevole = state.Master.Veicoli.Load();
        // Il salvataggio locale deve vedere e preservare la scrittura della seconda postazione (Phase D,
        // merge per-UID di due aggiunte disgiunte), non una fotografia scaduta accorpata dallo scope di
        // navigazione.
        Assert.Contains(autorevole, v => v.Nome == "Veicolo Da Postazione 2");
        Assert.Contains(autorevole, v => v.Nome == "Veicolo Locale Dopo Navigazione");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
