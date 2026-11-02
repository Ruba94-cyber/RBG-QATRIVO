using System.Linq;
using System.Reflection;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura perf (protocollo AGENTS.md, profilo rivisita Veicoli/Funzionalità 2026-09-25):
/// GammaViewModel/CategoriaViewModel annidati non vengono più costruiti eagerly dal ctor di
/// VeicoliViewModel/FunzionalitaViewModel, solo alla prima apertura reale dell'Expander (VeicoliView.Gamme /
/// FunzionalitaView.Categorie) o al primo accesso esplicito alla proprietà. Questa suite verifica che la
/// pigrizia non cambi alcun comportamento osservabile: HaModificheNonSalvate/Dispose restano corretti sia col
/// nested mai creato sia con quello creato e modificato, e le viste leggere (GammeDisponibili/
/// CategorieDisponibili) restano aggiornate anche senza costruire l'editor completo.</summary>
public sealed class MasterDataNestedLazyTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private AppState CreaStatoInizializzato()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_nestedlazy_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        Directory.CreateDirectory(dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore("Master Test", "1234"));
        return state;
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    /// <summary>Numero di sottoscrittori correnti di AppState.DatiRicaricati (evento field-like, backing field
    /// riflesso col nome esatto della proprietà, stesso schema di AuditViewReuseSubscriptionTests): usato per
    /// dimostrare che il nested MAI aperto non lascia nessuna sottoscrizione, e che Dispose() la rimuove quando
    /// invece è stato creato.</summary>
    private static int NumeroSottoscrittoriDatiRicaricati(AppState state)
    {
        var campo = typeof(AppState).GetField("DatiRicaricati", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(campo);
        var delegato = campo!.GetValue(state) as Action;
        return delegato?.GetInvocationList().Length ?? 0;
    }

    // --- Veicoli/Gamma ------------------------------------------------------------------------------------

    [Fact]
    public void Veicoli_HaModificheNonSalvate_FalseConGammaMaiAperta()
    {
        var state = CreaStatoInizializzato();
        var vm = new VeicoliViewModel(state);

        Assert.False(vm.HaModificheNonSalvate());
    }

    [Fact]
    public void Veicoli_HaModificheNonSalvate_VeroSeSoloGammaApertaEModificata()
    {
        var state = CreaStatoInizializzato();
        var vm = new VeicoliViewModel(state);

        // Accedere a Gamme e' esattamente l'apertura dell'Expander (VeicoliView.axaml.cs.CollegaGamme):
        // costruisce il nested solo ora.
        vm.Gamme.IniziaModificaCommand.AttendiModifica();
        vm.Gamme.NuovoCommand.Execute(null);
        vm.Gamme.Items.Last().Nome = "Gamma Nuova";

        Assert.True(vm.HaModificheNonSalvate());
    }

    [Fact]
    public void Veicoli_Dispose_SenzaGammaMaiAperta_NonLasciaSottoscrizioniDatiRicaricati()
    {
        var state = CreaStatoInizializzato();
        var primaDiCreare = NumeroSottoscrittoriDatiRicaricati(state);

        var vm = new VeicoliViewModel(state);
        // Solo VeicoliViewModel si e' agganciato: Gamme non e' mai stata toccata.
        Assert.Equal(primaDiCreare + 1, NumeroSottoscrittoriDatiRicaricati(state));

        vm.Dispose();
        Assert.Equal(primaDiCreare, NumeroSottoscrittoriDatiRicaricati(state));
    }

    [Fact]
    public void Veicoli_Dispose_DopoAperturaGamma_RilasciaEntrambeLeSottoscrizioni()
    {
        var state = CreaStatoInizializzato();
        var primaDiCreare = NumeroSottoscrittoriDatiRicaricati(state);

        var vm = new VeicoliViewModel(state);
        _ = vm.Gamme; // apre l'Expander: ora ci sono due sottoscrizioni (Veicoli + Gamma annidata)
        Assert.Equal(primaDiCreare + 2, NumeroSottoscrittoriDatiRicaricati(state));

        vm.Dispose();
        Assert.Equal(primaDiCreare, NumeroSottoscrittoriDatiRicaricati(state));
    }

    [Fact]
    public void GammeDisponibili_SenzaNestedCreato_VedeModificaDaSecondaPostazione()
    {
        var state = CreaStatoInizializzato();
        var dirCondivisa = _dirs.Last();

        var vm = new VeicoliViewModel(state);
        Assert.DoesNotContain("Gamma Da Postazione 2", vm.GammeDisponibili);

        ScriviGammaDaSecondaPostazione(dirCondivisa, "Gamma Da Postazione 2");

        state.RicaricaTutto();

        // GammeDisponibili resta la vista leggera (nested Gamme MAI acceduto in questo test): deve comunque
        // riflettere la scrittura della seconda postazione dopo il refresh.
        Assert.Contains("Gamma Da Postazione 2", vm.GammeDisponibili);
    }

    /// <summary>Misura headless 2026-09-25: la colonna "Gamma" legge GammeDisponibili più volte per ogni riga; con
    /// un Load() a ogni lettura Master.Veicoli passava da ~460 a ~1490 ms. Letture ripetute senza refresh devono
    /// restituire la stessa lista (nessuna rilettura del catalogo); dopo un refresh la lista va ricalcolata.</summary>
    [Fact]
    public void GammeECategorieDisponibili_SenzaNested_NonRileggonoIlCatalogoAOgniLettura()
    {
        var state = CreaStatoInizializzato();
        var dirCondivisa = _dirs.Last();
        var veicoli = new VeicoliViewModel(state);
        var funzionalita = new FunzionalitaViewModel(state);

        var gamme = veicoli.GammeDisponibili;
        var categorie = funzionalita.CategorieDisponibili;
        for (var i = 0; i < 20; i++)
        {
            Assert.Same(gamme, veicoli.GammeDisponibili);
            Assert.Same(categorie, funzionalita.CategorieDisponibili);
        }

        ScriviGammaDaSecondaPostazione(dirCondivisa, "Gamma Ricalcolata");
        state.RicaricaTutto();
        Assert.NotSame(gamme, veicoli.GammeDisponibili);
        Assert.Contains("Gamma Ricalcolata", veicoli.GammeDisponibili);
        Assert.NotSame(categorie, funzionalita.CategorieDisponibili);
    }

    private void ScriviGammaDaSecondaPostazione(string dirCondivisa, string nomeGamma)
    {
        var dirLocale2 = Path.Combine(Path.GetTempPath(), "TestLoom_nestedlazy_postazione2_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dirLocale2);
        var configService2 = new AppConfigService(Path.Combine(dirLocale2, "config.json"));
        var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        try
        {
            stato2.InizializzaCartella(dirCondivisa);
            var gamme = stato2.Master.Gamma.Load();
            gamme.Add(new Gamma { Nome = nomeGamma });
            stato2.Master.Gamma.Save(gamme);
        }
        finally { stato2.Dispose(); }
    }

    private void RinominaEtichettaRuoloDaSecondaPostazione(string dirCondivisa, RuoloCampoSistema ruolo, string nuovaEtichetta)
    {
        var dirLocale2 = Path.Combine(Path.GetTempPath(), "TestLoom_nestedlazy_postazione2_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dirLocale2);
        var configService2 = new AppConfigService(Path.Combine(dirLocale2, "config.json"));
        var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        try
        {
            stato2.InizializzaCartella(dirCondivisa);
            var pianificazione = stato2.Master.CampiSistemaPianificazione.Load();
            var voce = pianificazione.SingleOrDefault(v => v.Ruolo == ruolo);
            if (voce != null)
            {
                voce.Etichetta = nuovaEtichetta;
                stato2.Master.CampiSistemaPianificazione.Save(pianificazione);
                return;
            }
            var dettagli = stato2.Master.CampiSistemaDettagli.Load();
            dettagli.Single(v => v.Ruolo == ruolo).Etichetta = nuovaEtichetta;
            stato2.Master.CampiSistemaDettagli.Save(dettagli);
        }
        finally { stato2.Dispose(); }
    }

    /// <summary>FIX review 2026-09-25 (finding Major): prima della correzione, GammeDisponibili restava la
    /// vista leggera ma NON notificava mai PropertyChanged dopo un refresh (solo il CollectionChanged del
    /// nested — mai creato in questo test — lo faceva). Verifica la NOTIFICA, non solo il valore finale: una
    /// UI in binding non aggiorna la colonna "Gamma" della griglia senza un PropertyChanged reale.</summary>
    [Fact]
    public void GammeDisponibili_SenzaNestedCreato_NotificaPropertyChangedDopoModificaDaSecondaPostazione()
    {
        var state = CreaStatoInizializzato();
        var dirCondivisa = _dirs.Last();
        var vm = new VeicoliViewModel(state);

        var notificato = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VeicoliViewModel.GammeDisponibili)) notificato = true; };

        ScriviGammaDaSecondaPostazione(dirCondivisa, "Gamma Notifica");
        state.RicaricaTutto();

        Assert.True(notificato);
        Assert.Contains("Gamma Notifica", vm.GammeDisponibili);
    }

    /// <summary>Stesso finding Major, per TitoloGamma: una rinomina dell'etichetta del ruolo di sistema "Gamma"
    /// fatta da un'altra postazione deve notificare PropertyChanged (e invalidare _titoloGammaCache), non solo
    /// aggiornare il valore restituito alla prossima lettura.</summary>
    [Fact]
    public void TitoloGamma_SenzaNestedCreato_NotificaPropertyChangedDopoRinominaRuoloDaSecondaPostazione()
    {
        var state = CreaStatoInizializzato();
        var dirCondivisa = _dirs.Last();
        var vm = new VeicoliViewModel(state);
        _ = vm.TitoloGamma; // valorizza _titoloGammaCache, che deve essere invalidata dal refresh

        var notificato = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VeicoliViewModel.TitoloGamma)) notificato = true; };

        RinominaEtichettaRuoloDaSecondaPostazione(dirCondivisa, RuoloCampoSistema.Gamma, "Classe Veicolare");
        state.RicaricaTutto();

        Assert.True(notificato);
        Assert.Equal("Classe Veicolare", vm.TitoloGamma);
    }

    /// <summary>"Verifica anche dopo il salvataggio locale di Veicoli" (review 2026-09-25): un Salva() sulla
    /// pagina Veicoli stessa (che non tocca affatto il catalogo Gamma) non deve rompere il refresh successivo di
    /// GammeDisponibili da una seconda postazione — la lettura leggera e la sua notifica restano indipendenti
    /// dal ciclo di vita di Items/snapshot di Veicoli.</summary>
    [Fact]
    public async Task GammeDisponibili_DopoSalvataggioLocaleDiVeicoli_ContinuaANotificareModificheDaSecondaPostazione()
    {
        var state = CreaStatoInizializzato();
        var dirCondivisa = _dirs.Last();
        var vm = new VeicoliViewModel(state);

        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "Veicolo Locale";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);

        var notificato = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VeicoliViewModel.GammeDisponibili)) notificato = true; };

        ScriviGammaDaSecondaPostazione(dirCondivisa, "Gamma Dopo Salvataggio Locale");
        state.RicaricaTutto();

        Assert.True(notificato);
        Assert.Contains("Gamma Dopo Salvataggio Locale", vm.GammeDisponibili);
    }

    // --- Funzionalità/Categoria ----------------------------------------------------------------------------

    [Fact]
    public void Funzionalita_HaModificheNonSalvate_FalseConCategorieMaiAperta()
    {
        var state = CreaStatoInizializzato();
        var vm = new FunzionalitaViewModel(state);

        Assert.False(vm.HaModificheNonSalvate());
    }

    [Fact]
    public void Funzionalita_HaModificheNonSalvate_VeroSeSoloCategorieApertaEModificata()
    {
        var state = CreaStatoInizializzato();
        var vm = new FunzionalitaViewModel(state);

        vm.Categorie.IniziaModificaCommand.AttendiModifica();
        vm.Categorie.NuovoCommand.Execute(null);
        vm.Categorie.Items.Last().Nome = "Categoria Nuova";

        Assert.True(vm.HaModificheNonSalvate());
    }

    [Fact]
    public void Funzionalita_Dispose_SenzaCategorieMaiAperta_NonLasciaSottoscrizioniDatiRicaricati()
    {
        var state = CreaStatoInizializzato();
        var primaDiCreare = NumeroSottoscrittoriDatiRicaricati(state);

        var vm = new FunzionalitaViewModel(state);
        Assert.Equal(primaDiCreare + 1, NumeroSottoscrittoriDatiRicaricati(state));

        vm.Dispose();
        Assert.Equal(primaDiCreare, NumeroSottoscrittoriDatiRicaricati(state));
    }

    [Fact]
    public void Funzionalita_Dispose_DopoAperturaCategorie_RilasciaEntrambeLeSottoscrizioni()
    {
        var state = CreaStatoInizializzato();
        var primaDiCreare = NumeroSottoscrittoriDatiRicaricati(state);

        var vm = new FunzionalitaViewModel(state);
        _ = vm.Categorie;
        Assert.Equal(primaDiCreare + 2, NumeroSottoscrittoriDatiRicaricati(state));

        vm.Dispose();
        Assert.Equal(primaDiCreare, NumeroSottoscrittoriDatiRicaricati(state));
    }

    [Fact]
    public void CategorieDisponibili_SenzaNestedCreato_VedeModificaDaSecondaPostazione()
    {
        var state = CreaStatoInizializzato();
        var dirCondivisa = _dirs.Last();

        var vm = new FunzionalitaViewModel(state);
        Assert.DoesNotContain("Categoria Da Postazione 2", vm.CategorieDisponibili);

        ScriviCategoriaDaSecondaPostazione(dirCondivisa, "Categoria Da Postazione 2");

        state.RicaricaTutto();

        Assert.Contains("Categoria Da Postazione 2", vm.CategorieDisponibili);
    }

    private void ScriviCategoriaDaSecondaPostazione(string dirCondivisa, string nomeCategoria)
    {
        var dirLocale2 = Path.Combine(Path.GetTempPath(), "TestLoom_nestedlazy_postazione2_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dirLocale2);
        var configService2 = new AppConfigService(Path.Combine(dirLocale2, "config.json"));
        var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        try
        {
            stato2.InizializzaCartella(dirCondivisa);
            var categorie = stato2.Master.Categorie.Load();
            categorie.Add(new Categoria { Nome = nomeCategoria });
            stato2.Master.Categorie.Save(categorie);
        }
        finally { stato2.Dispose(); }
    }

    /// <summary>FIX review 2026-09-25 (finding Major), lato Funzionalità/Categoria: verifica la NOTIFICA, non
    /// solo il valore finale.</summary>
    [Fact]
    public void CategorieDisponibili_SenzaNestedCreato_NotificaPropertyChangedDopoModificaDaSecondaPostazione()
    {
        var state = CreaStatoInizializzato();
        var dirCondivisa = _dirs.Last();
        var vm = new FunzionalitaViewModel(state);

        var notificato = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FunzionalitaViewModel.CategorieDisponibili)) notificato = true; };

        ScriviCategoriaDaSecondaPostazione(dirCondivisa, "Categoria Notifica");
        state.RicaricaTutto();

        Assert.True(notificato);
        Assert.Contains("Categoria Notifica", vm.CategorieDisponibili);
    }

    /// <summary>Stesso finding Major, per TitoloCategoria.</summary>
    [Fact]
    public void TitoloCategoria_SenzaNestedCreato_NotificaPropertyChangedDopoRinominaRuoloDaSecondaPostazione()
    {
        var state = CreaStatoInizializzato();
        var dirCondivisa = _dirs.Last();
        var vm = new FunzionalitaViewModel(state);
        _ = vm.TitoloCategoria; // valorizza _titoloCategoriaCache, che deve essere invalidata dal refresh

        var notificato = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FunzionalitaViewModel.TitoloCategoria)) notificato = true; };

        RinominaEtichettaRuoloDaSecondaPostazione(dirCondivisa, RuoloCampoSistema.Categoria, "Raggruppamento");
        state.RicaricaTutto();

        Assert.True(notificato);
        Assert.Equal("Raggruppamento", vm.TitoloCategoria);
    }

    /// <summary>"Verifica anche dopo il salvataggio locale di Funzionalità" (review 2026-09-25): analogo al
    /// caso Veicoli/Gamma sopra.</summary>
    [Fact]
    public async Task CategorieDisponibili_DopoSalvataggioLocaleDiFunzionalita_ContinuaANotificareModificheDaSecondaPostazione()
    {
        var state = CreaStatoInizializzato();
        var dirCondivisa = _dirs.Last();
        var vm = new FunzionalitaViewModel(state);

        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "Funzionalita Locale";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);

        var notificato = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FunzionalitaViewModel.CategorieDisponibili)) notificato = true; };

        ScriviCategoriaDaSecondaPostazione(dirCondivisa, "Categoria Dopo Salvataggio Locale");
        state.RicaricaTutto();

        Assert.True(notificato);
        Assert.Contains("Categoria Dopo Salvataggio Locale", vm.CategorieDisponibili);
    }

    // --- TitoloGamma/TitoloCategoria senza costruire il nested ----------------------------------------------

    [Fact]
    public void TitoloGamma_SenzaNestedCreato_UgualeATitoloDelNestedUnaVoltaCreato()
    {
        var state = CreaStatoInizializzato();
        var vm = new VeicoliViewModel(state);

        var titoloPrimaDiAprire = vm.TitoloGamma;
        var titoloNested = vm.Gamme.TitoloGamma;

        Assert.Equal(titoloNested, titoloPrimaDiAprire);
    }

    [Fact]
    public void TitoloCategoria_SenzaNestedCreato_UgualeATitoloDelNestedUnaVoltaCreato()
    {
        var state = CreaStatoInizializzato();
        var vm = new FunzionalitaViewModel(state);

        var titoloPrimaDiAprire = vm.TitoloCategoria;
        var titoloNested = vm.Categorie.TitoloCategoria;

        Assert.Equal(titoloNested, titoloPrimaDiAprire);
    }
}

/// <summary>Regressione (bug critico Categorie/Gamma vuote, 2026-09-30): il contenuto lazy dell'Expander riceve il
/// DataContext dal code-behind, ma con x:DataType della UserControl le compiled binding restavano tipizzate sul VM
/// esterno e fallivano in silenzio. Il contenuto deve dichiarare il proprio x:DataType.</summary>
public sealed class MasterDataNestedLazyXamlTests
{
    private static string LeggiVista(string nome)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "src", "TestLoom.App", "Views", nome);
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(nome);
    }

    [Theory]
    [InlineData("FunzionalitaView.axaml", "CategorieContenuto", "vm:CategoriaViewModel")]
    [InlineData("VeicoliView.axaml", "GammeContenuto", "vm:GammaViewModel")]
    public void ContenutoLazy_DichiaraXDataTypeDelNested(string vista, string nome, string tipo)
    {
        var xaml = LeggiVista(vista);
        var m = System.Text.RegularExpressions.Regex.Match(xaml, "<StackPanel x:Name=\"" + nome + "\"[^>]*>");
        Assert.True(m.Success);
        Assert.Contains("x:DataType=\"" + tipo + "\"", m.Value);
    }
}
