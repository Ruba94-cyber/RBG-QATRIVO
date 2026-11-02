using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Layout della Home condiviso dal team (<c>_ditm/ui/home-team-layout.json</c>): precedenza personale, poi team, poi
/// predefinito; pubblicazione solo da Master con controllo di revisione; file corrotto o workspace in sola lettura
/// gestiti senza crash e senza sovrascrivere nulla.
/// </summary>
/// <remarks>[Collection("Localization")]: legge le traduzioni dal dizionario condiviso e cambia ConfirmService.Handler,
/// come le altre suite che toccano la Home e le conferme.</remarks>
[Collection("Localization")]
public sealed class HomeTeamLayoutTests : IDisposable
{
    private const string NomeMaster = "Master Prova";
    private const string NomeStandard = "Operatore Prova";
    private const string NomeViewer = "Viewer Prova";
    private const string Pin = "1234";
    private const string ChiaveLayoutPersonale = "Home.Layout";

    private static readonly string[] OrdinePredefinito = { "riepilogo", "avvisi", "kpi", "grafici", "liste", "recenti" };

    private readonly string _radice = Path.Combine(Path.GetTempPath(), "TestLoom_HomeTeam_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confermaOriginale = ConfirmService.Handler;

    private string Cartella => Path.Combine(_radice, "condiviso");
    private string PercorsoTeam => Path.Combine(Cartella, "_ditm", "ui", "home-team-layout.json");

    public void Dispose()
    {
        ConfirmService.Handler = _confermaOriginale;
        try { Directory.Delete(_radice, recursive: true); } catch { }
    }

    /// <summary>Nuova sessione dell'app su un PC: config.json locale del PC indicato, AppState nuova.</summary>
    private AppState NuovaSessione(string pc) =>
        new(new AppConfigService(Path.Combine(_radice, "pc-" + pc, "config.json")), runBackgroundMaintenance: false);

    /// <summary>Workspace condiviso con un Master, uno Standard e un Viewer (PIN 1234).</summary>
    private void CreaWorkspace()
    {
        Directory.CreateDirectory(Cartella); // Il bootstrap non ricrea una cartella condivisa assente.
        var primo = NuovaSessione("A");
        primo.InizializzaCartella(Cartella);
        primo.Master.Operatori.Save(new List<Operatore>
        {
            TestOperatori.Sigilla(new Operatore { Nome = NomeMaster, Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash(Pin) }),
            TestOperatori.Sigilla(new Operatore { Nome = NomeStandard, Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash(Pin) }),
            TestOperatori.Sigilla(new Operatore { Nome = NomeViewer, Ruolo = RuoloOperatore.Viewer, PinHash = PinService.Hash(Pin) }),
        });
        primo.Dispose();
    }

    /// <summary>Nuova sessione (come un avvio dell'app) sul workspace condiviso, con l'operatore indicato.</summary>
    private AppState Accedi(string nome, string pc = "A")
    {
        var sessione = NuovaSessione(pc);
        sessione.InizializzaCartella(Cartella);
        Assert.True(sessione.ImpostaOperatore(nome, Pin));
        return sessione;
    }

    /// <summary>Layout completo del team: tutte le sezioni nell'ordine storico, con le sole sezioni indicate nascoste.</summary>
    private static IReadOnlyList<(string Id, bool Visibile)> LayoutTeam(params string[] nascoste) =>
        OrdinePredefinito.Select(id => (Id: id, Visibile: !nascoste.Contains(id))).ToList();

    private void ScriviTeamGrezzo(string contenuto)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PercorsoTeam)!);
        File.WriteAllText(PercorsoTeam, contenuto);
    }

    // ------------------------------------------------------------ risoluzione: personale > team > predefinito

    [Fact]
    public void Risoluzione_SenzaTeamENessunPersonale_UsaIlPredefinito()
    {
        CreaWorkspace();

        var home = new HomeViewModel(Accedi(NomeStandard), _ => { });

        Assert.Equal(OrdinePredefinito, home.OrdineSezioni);
        Assert.All(HomeSezioni.Predefinite, id => Assert.True(home.SezioneVisibile(id)));
        Assert.False(home.TeamLayoutDisponibile);
    }

    [Fact]
    public void Risoluzione_SenzaPersonaleUsaIlLayoutDelTeam()
    {
        CreaWorkspace();
        var ordine = new (string Id, bool Visibile)[]
        {
            ("liste", true), ("recenti", true), ("riepilogo", true), ("avvisi", true), ("kpi", false), ("grafici", true),
        };
        HomeTeamLayoutService.Pubblica(Accedi(NomeMaster), ordine, 0);

        // Il PC B non ha una preferenza personale: vede il layout del team.
        var home = new HomeViewModel(Accedi(NomeStandard, pc: "B"), _ => { });

        Assert.Equal(new[] { "liste", "recenti", "riepilogo", "avvisi", "kpi", "grafici" }, home.OrdineSezioni);
        Assert.False(home.SezioneVisibile(HomeSezioni.Kpi));
        Assert.True(home.SezioneVisibile(HomeSezioni.Liste));
        Assert.True(home.TeamLayoutDisponibile);
    }

    [Fact]
    public void Risoluzione_LayoutPersonaleHaPrecedenzaSulTeam()
    {
        CreaWorkspace();
        var master = Accedi(NomeMaster);
        var primo = HomeTeamLayoutService.Pubblica(master, LayoutTeam("kpi"), 0);

        // Il PC ha una scelta personale (grafici nascosto). Il team cambia dopo: riepilogo nascosto, kpi visibile.
        var home = new HomeViewModel(master, _ => { });
        home.ImpostaSezioneVisibile(HomeSezioni.Grafici, false);
        HomeTeamLayoutService.Pubblica(master, LayoutTeam("riepilogo"), primo.Revisione);
        master.Dispose();

        var riavvio = new HomeViewModel(Accedi(NomeMaster), _ => { });
        Assert.True(riavvio.SezioneVisibile(HomeSezioni.Riepilogo));  // personale: il team nuovo lo nasconde
        Assert.False(riavvio.SezioneVisibile(HomeSezioni.Grafici));   // scelta personale
        Assert.False(riavvio.SezioneVisibile(HomeSezioni.Kpi));       // copia del team di allora, salvata sul PC
    }

    [Fact]
    public void Risoluzione_IdSconosciutiIgnorati_SezioniMancantiInFondoVisibili()
    {
        CreaWorkspace();
        JsonIo.WriteAtomic(PercorsoTeam, new HomeTeamLayoutDocumento
        {
            Schema = HomeTeamLayoutService.SchemaCorrente,
            Revisione = 1,
            AggiornatoDa = NomeMaster,
            Sezioni = new List<HomeTeamLayoutSezione>
            {
                new() { Id = "futura", Visibile = true },
                new() { Id = "kpi", Visibile = false },
                new() { Id = "riepilogo", Visibile = true },
            },
        });

        var home = new HomeViewModel(Accedi(NomeStandard), _ => { });

        // "futura" ignorata; le sezioni mancanti si aggiungono in fondo, visibili, nell'ordine predefinito.
        Assert.Equal(new[] { "kpi", "riepilogo", "avvisi", "grafici", "liste", "recenti" }, home.OrdineSezioni);
        Assert.False(home.SezioneVisibile(HomeSezioni.Kpi));
        Assert.True(home.SezioneVisibile(HomeSezioni.Avvisi));
        Assert.True(home.SezioneVisibile(HomeSezioni.Recenti));
    }

    [Fact]
    public void Ripristina_ConTeamPubblicato_TornaAlLayoutDelTeamNonAlPredefinito()
    {
        CreaWorkspace();
        var master = Accedi(NomeMaster);
        HomeTeamLayoutService.Pubblica(master, LayoutTeam("kpi"), 0);
        var home = new HomeViewModel(master, _ => { });
        home.ImpostaSezioneVisibile(HomeSezioni.Riepilogo, false);   // preferenza personale

        home.RipristinaLayoutPredefinitoCommand.Execute(null);

        Assert.False(home.SezioneVisibile(HomeSezioni.Kpi));        // layout del team, non il predefinito di sistema
        Assert.True(home.SezioneVisibile(HomeSezioni.Riepilogo));
    }

    [Fact]
    public void UsaLayoutTeam_RimuoveIlPersonale_EResta_DopoIlRiavvio()
    {
        CreaWorkspace();
        var master = Accedi(NomeMaster);
        HomeTeamLayoutService.Pubblica(master, LayoutTeam("kpi"), 0);
        var home = new HomeViewModel(master, _ => { });
        home.ImpostaSezioneVisibile(HomeSezioni.Riepilogo, false);   // preferenza personale
        Assert.False(home.SezioneVisibile(HomeSezioni.Riepilogo));

        home.UsaLayoutTeamCommand.Execute(null);

        Assert.True(home.SezioneVisibile(HomeSezioni.Riepilogo));    // di nuovo il team
        Assert.False(home.SezioneVisibile(HomeSezioni.Kpi));
        Assert.Empty(ViewStateService.LeggiLista(master, ChiaveLayoutPersonale));
        master.Dispose();

        var riavvio = new HomeViewModel(Accedi(NomeMaster), _ => { });
        Assert.False(riavvio.SezioneVisibile(HomeSezioni.Kpi));      // nessun layout personale: ancora il team
        Assert.True(riavvio.SezioneVisibile(HomeSezioni.Riepilogo));
    }

    // ------------------------------------------------------------ pubblicazione: solo Master, con revisione

    [Fact]
    public void Pubblica_Master_ScriveIlFileConRevisioneEAutore()
    {
        CreaWorkspace();
        var master = Accedi(NomeMaster);

        var documento = HomeTeamLayoutService.Pubblica(master, LayoutTeam("grafici"), 0);

        Assert.Equal(1, documento.Revisione);
        Assert.True(File.Exists(PercorsoTeam));
        Assert.EndsWith(Path.Combine("_ditm", "ui", "home-team-layout.json"), HomeTeamLayoutService.PercorsoFile(master.Paths));
        var letto = JsonIo.TryRead<HomeTeamLayoutDocumento>(PercorsoTeam, out var errore);
        Assert.Null(errore);
        Assert.NotNull(letto);
        Assert.Equal(HomeTeamLayoutService.SchemaCorrente, letto!.Schema);
        Assert.Equal(1, letto.Revisione);
        Assert.Equal(NomeMaster, letto.AggiornatoDa);
        Assert.Equal(OrdinePredefinito, letto.Sezioni.Select(s => s.Id));
        Assert.False(letto.Sezioni.Single(s => s.Id == HomeSezioni.Grafici).Visibile);
    }

    [Theory]
    [InlineData(NomeStandard)]
    [InlineData(NomeViewer)]
    public void Pubblica_NonMaster_RifiutataENonScrive(string nome)
    {
        CreaWorkspace();
        var operatore = Accedi(nome);

        Assert.Throws<UnauthorizedAccessException>(() => HomeTeamLayoutService.Pubblica(operatore, LayoutTeam(), 0));
        Assert.False(File.Exists(PercorsoTeam));

        var home = new HomeViewModel(operatore, _ => { });
        Assert.False(home.PuoPubblicareLayoutTeam);
        Assert.False(home.PubblicaLayoutTeamCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pubblica_RevisioneObsoleta_RifiutataSenzaSovrascrivere_PoiRicaricaERipubblica()
    {
        CreaWorkspace();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var masterA = Accedi(NomeMaster, pc: "A");
        var homeA = new HomeViewModel(masterA, _ => { });       // aperta quando il team non ha ancora un layout (revisione 0)

        // Un altro Master (PC B) pubblica nel frattempo.
        var masterB = Accedi(NomeMaster, pc: "B");
        HomeTeamLayoutService.Pubblica(masterB, LayoutTeam("riepilogo"), 0);

        // La pubblicazione di A e' rifiutata: il file di B non viene sovrascritto.
        Assert.Throws<ConflittoRevisioneException>(() => HomeTeamLayoutService.Pubblica(masterA, LayoutTeam(), 0));
        await homeA.PubblicaLayoutTeamCommand.ExecuteAsync(null);
        Assert.Null(homeA.MessaggioLayoutTeam);
        Assert.True(homeA.HaErroreLayoutTeam);
        var sulDisco = JsonIo.TryRead<HomeTeamLayoutDocumento>(PercorsoTeam, out _);
        Assert.NotNull(sulDisco);
        Assert.Equal(1, sulDisco!.Revisione);
        Assert.Equal(NomeMaster, sulDisco.AggiornatoDa);
        Assert.False(sulDisco.Sezioni.Single(s => s.Id == HomeSezioni.Riepilogo).Visibile);

        // Il Master ricarica il layout del team e ripubblica sulla revisione corrente.
        homeA.UsaLayoutTeamCommand.Execute(null);
        Assert.False(homeA.SezioneVisibile(HomeSezioni.Riepilogo));
        await homeA.PubblicaLayoutTeamCommand.ExecuteAsync(null);
        var ripubblicato = JsonIo.TryRead<HomeTeamLayoutDocumento>(PercorsoTeam, out _);
        Assert.NotNull(ripubblicato);
        Assert.Equal(2, ripubblicato!.Revisione);
        Assert.False(homeA.HaErroreLayoutTeam);
    }

    [Fact]
    public async Task Pubblica_VistaMaster_ConfermaERiapertura_ScriveIlLayoutCorrente()
    {
        CreaWorkspace();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var master = Accedi(NomeMaster, pc: "A");
        var home = new HomeViewModel(master, _ => { });
        home.ImpostaSezioneVisibile(HomeSezioni.Grafici, false);   // il layout che il Master vede su questo PC

        await home.PubblicaLayoutTeamCommand.ExecuteAsync(null);

        Assert.True(home.TeamLayoutDisponibile);
        Assert.True(home.HaMessaggioLayoutTeam);
        Assert.False(home.HaErroreLayoutTeam);
        // Un PC senza layout personale (B) vede il layout pubblicato: grafici nascosto.
        var altroPc = new HomeViewModel(Accedi(NomeStandard, pc: "B"), _ => { });
        Assert.False(altroPc.SezioneVisibile(HomeSezioni.Grafici));
        Assert.True(altroPc.TeamLayoutDisponibile);
    }

    [Fact]
    public async Task Pubblica_ConfermaNegata_NonScrive()
    {
        CreaWorkspace();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);
        var home = new HomeViewModel(Accedi(NomeMaster), _ => { });

        await home.PubblicaLayoutTeamCommand.ExecuteAsync(null);

        Assert.False(File.Exists(PercorsoTeam));
        Assert.False(home.TeamLayoutDisponibile);
    }

    // ------------------------------------------------------------ file corrotto e workspace non scrivibile

    [Fact]
    public void Lettura_FileCorrotto_RicadeSulPredefinitoSenzaCrash()
    {
        CreaWorkspace();
        ScriviTeamGrezzo("{ questo non e' JSON");

        var home = new HomeViewModel(Accedi(NomeStandard), _ => { });

        Assert.Equal(OrdinePredefinito, home.OrdineSezioni);
        Assert.All(HomeSezioni.Predefinite, id => Assert.True(home.SezioneVisibile(id)));
        Assert.False(home.TeamLayoutDisponibile);
        Assert.False(home.UsaLayoutTeamCommand.CanExecute(null));
        Assert.True(home.HaErroreLayoutTeam);
    }

    [Fact]
    public void Pubblica_FileCorrotto_RifiutataSenzaSovrascrivere()
    {
        CreaWorkspace();
        ScriviTeamGrezzo("{ questo non e' JSON");
        var master = Accedi(NomeMaster);

        Assert.Throws<InvalidDataException>(() => HomeTeamLayoutService.Pubblica(master, LayoutTeam(), 0));
        Assert.Equal("{ questo non e' JSON", File.ReadAllText(PercorsoTeam));
    }

    [Fact]
    public void Pubblica_WorkspaceSolaLettura_RifiutataSenzaScrivere()
    {
        CreaWorkspace();
        var master = Accedi(NomeMaster);
        WorkspaceReadOnlyService.SetEnabled(Cartella, true);
        try
        {
            Assert.Throws<IOException>(() => HomeTeamLayoutService.Pubblica(master, LayoutTeam(), 0));
            Assert.False(File.Exists(PercorsoTeam));
        }
        finally
        {
            WorkspaceReadOnlyService.SetEnabled(Cartella, false);
        }
    }

    [Fact]
    public void Pubblica_LockOccupato_RifiutataSenzaScrivere()
    {
        CreaWorkspace();
        var master = Accedi(NomeMaster);
        Directory.CreateDirectory(Path.GetDirectoryName(PercorsoTeam)!);
        // Un altro processo tiene il lock del layout: la pubblicazione attende poco, poi rinuncia senza scrivere.
        using var altroProcesso = DomainRecordCommitLock.Acquire("Test", HomeTeamLayoutService.ElementoId,
            HomeTeamLayoutService.PercorsoFile(master.Paths));

        Assert.Throws<IOException>(() =>
            HomeTeamLayoutService.Pubblica(master, LayoutTeam(), 0, TimeSpan.FromMilliseconds(200)));
        Assert.False(File.Exists(PercorsoTeam));
    }
}
