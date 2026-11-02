using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Debounce della ricerca negli editor master paginati (componente, Requisiti): la digitazione rapida produce una sola query
/// dopo la pausa; Invio applica subito il testo corrente; svuotare il campo applica subito; una ricerca superata da una
/// digitazione successiva non si mostra; a parita' di testo i risultati e il loro ordine per rilevanza sono quelli della
/// query diretta sullo store. I cataloghi non paginati restano sul percorso in memoria, immediato.
/// </summary>
public sealed class MasterRicercaDebounceTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }
    }

    private AppState CreateMasterState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterRicercaDebounce_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        Directory.CreateDirectory(dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(dir);
        var operatori = state.Master.Operatori.Load();
        operatori.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Ricerca Debounce",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Master Ricerca Debounce", "1234"));
        return state;
    }

    private static void SeedEcu(AppState state, int n) =>
        state.Master.Componente.Save(Enumerable.Range(0, n)
            .Select(i => new Componente { Nome = $"Componente-{i:D4}", ModificatoDa = "Seed" })
            .ToList());

    /// <summary>Prima pagina attesa: la query diretta sullo store, con lo stesso testo.</summary>
    private static List<string> Atteso(AppState state, string testo) =>
        MasterDataPagedReadSources.Componente(state.Paths).ReadPage(testo, 1, 200).Items.Select(e => e.Nome).ToList();

    private static List<string> Chiavi(MasterDataEditorViewModelBase<Componente> vm) =>
        vm.ItemsAttivi.Select(e => e.Nome).ToList();

    private static void Digita(EcuViewModel vm, string testo)
    {
        var corrente = string.Empty;
        foreach (var c in testo)
        {
            corrente += c;
            vm.FiltroTesto = corrente;
        }
    }

    [Fact]
    public async Task DigitazioneRapida_UnaSolaQueryDopoLaPausa()
    {
        using var state = CreateMasterState();
        SeedEcu(state, 400);
        using var vm = new EcuViewModel(state) { DebounceRicerca = TimeSpan.FromMilliseconds(80) };
        Assert.True(vm.UsaPaginazione);
        var query = 0;
        vm.ItemsAttivi.CollectionChanged += (_, _) => query++;

        Digita(vm, "Componente-0123");
        Assert.Equal(0, query); // durante la digitazione nessuna query

        await vm.RicercaPendente;
        Assert.Equal(1, query);
        Assert.Equal(Atteso(state, "Componente-0123"), Chiavi(vm));
    }

    [Fact]
    public async Task Invio_PrimaDelDebounce_ApplicaIlTestoCompleto()
    {
        using var state = CreateMasterState();
        SeedEcu(state, 400);
        using var vm = new EcuViewModel(state) { DebounceRicerca = TimeSpan.FromSeconds(30) };
        var query = 0;
        vm.ItemsAttivi.CollectionChanged += (_, _) => query++;

        Digita(vm, "Componente-0123");
        vm.ConfermaRicercaImmediata();

        Assert.Equal(1, query); // applicato subito, senza attendere la pausa
        Assert.Equal(Atteso(state, "Componente-0123"), Chiavi(vm));

        // La ricerca in attesa e' stata annullata: completa senza applicare nulla di nuovo.
        await vm.RicercaPendente;
        Assert.Equal(1, query);
        Assert.Equal(Atteso(state, "Componente-0123"), Chiavi(vm));
    }

    [Fact]
    public void SvuotareIlCampo_ApplicaSubito()
    {
        using var state = CreateMasterState();
        SeedEcu(state, 400);
        using var vm = new EcuViewModel(state) { DebounceRicerca = TimeSpan.FromSeconds(30) };
        Digita(vm, "Componente-0123");
        vm.ConfermaRicercaImmediata();
        Assert.Equal(Atteso(state, "Componente-0123"), Chiavi(vm));

        var query = 0;
        vm.ItemsAttivi.CollectionChanged += (_, _) => query++;

        vm.FiltroTesto = string.Empty;

        Assert.Equal(1, query); // svuotato: la prima pagina senza filtro e' gia' visibile
        Assert.Equal(Atteso(state, string.Empty), Chiavi(vm));
    }

    [Fact]
    public async Task RicercaSuperataDaUnaDigitazioneSuccessiva_NonSiMostra()
    {
        using var state = CreateMasterState();
        SeedEcu(state, 400);
        using var vm = new EcuViewModel(state) { DebounceRicerca = TimeSpan.FromMilliseconds(60) };
        var query = 0;
        vm.ItemsAttivi.CollectionChanged += (_, _) => query++;

        vm.FiltroTesto = "Componente-01";
        var superata = vm.RicercaPendente;
        vm.FiltroTesto = "Componente-0123";
        var attuale = vm.RicercaPendente;

        await superata;  // annullata: non applica il risultato di "Componente-01"
        await attuale;
        Assert.Equal(1, query);
        Assert.Equal(Atteso(state, "Componente-0123"), Chiavi(vm));
    }

    [Theory]
    [InlineData("Componente-00")]
    [InlineData("Componente-0123")]
    [InlineData("0123")]
    public async Task DopoIlDebounce_RisultatiEOrdineUgualiAllaQueryDiretta(string testo)
    {
        using var state = CreateMasterState();
        SeedEcu(state, 400);
        using var vm = new EcuViewModel(state) { DebounceRicerca = TimeSpan.FromMilliseconds(40) };

        Digita(vm, testo);
        await vm.RicercaPendente;

        Assert.Equal(Atteso(state, testo), Chiavi(vm));
    }

    [Fact]
    public void CatalogoNonPaginato_FiltroInMemoriaResitaImmediato()
    {
        using var state = CreateMasterState();
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = "VEI-A", ModificatoDa = "Test" },
            new() { Nome = "VEI-B", ModificatoDa = "Test" },
            new() { Nome = "VEI-C", ModificatoDa = "Test" },
        });
        using var vm = new VeicoliViewModel(state);
        Assert.False(vm.UsaPaginazione);

        vm.FiltroTesto = "VEI-B";

        Assert.Equal(new[] { "VEI-B" }, vm.ItemsAttivi.Select(v => v.Nome).ToArray());
    }
}
