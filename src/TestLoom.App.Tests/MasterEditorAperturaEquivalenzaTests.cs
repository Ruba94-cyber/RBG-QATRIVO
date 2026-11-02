using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Equivalenza degli editor master paginati (componente, Requisiti) dopo due caricamenti resi lazy: Veicoli dell'editor componente
/// (EcuViewModel.VeicoliDisponibili, usato solo dall'import Excel) e l'elenco "Unisci in" (MasterUnioneViewModel,
/// letto dal read-model solo quando una riga e' selezionata). Gli oracoli sono letture dirette dello store locale o
/// del catalogo, cioe' la stessa sorgente che usava il percorso eager precedente, non le viste che si stanno testando.
/// </summary>
public sealed class MasterEditorAperturaEquivalenzaTests : IDisposable
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
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterAperturaEq_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        Directory.CreateDirectory(dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(dir);
        var operatori = state.Master.Operatori.Load();
        operatori.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Apertura Eq",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Master Apertura Eq", "1234"));
        return state;
    }

    [Fact]
    public void Componente_VeicoliDisponibili_SiLeggonoAllaPrimaNecessitaNonAllApertura()
    {
        using var state = CreateMasterState();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "VEI-A", ModificatoDa = "Test" } });
        using var vm = new EcuViewModel(state);

        // Il catalogo cambia dopo l'apertura: la lista non e' fissata alla costruzione ma riflette il catalogo alla
        // prima lettura (VeicoliDisponibili serve solo alla validazione dell'import, che ha sempre bisogno del dato corrente).
        var veicoli = state.Master.Veicoli.Load();
        veicoli.Add(new Veicolo { Nome = "VEI-B", ModificatoDa = "Test" });
        state.Master.Veicoli.Save(veicoli);

        Assert.Equal(new[] { "VEI-A", "VEI-B" }, vm.VeicoliDisponibili.OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void Componente_VeicoliDisponibili_UgualiAiVeicoliAttiviDelCatalogo()
    {
        using var state = CreateMasterState();
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = "VEI-A", ModificatoDa = "Test" },
            new() { Nome = "VEI-B", ModificatoDa = "Test", Archiviata = true },
            new() { Nome = "VEI-C", ModificatoDa = "Test" },
        });
        using var vm = new EcuViewModel(state);

        var atteso = state.Master.Veicoli.Load().Where(v => !v.Archiviata).Select(v => v.Nome).ToList();
        Assert.Equal(atteso, vm.VeicoliDisponibili);
        Assert.DoesNotContain("VEI-B", vm.VeicoliDisponibili);
    }

    [Fact]
    public void Requisiti_PagingAttivo_ElencoUnisci_UgualeAlleChiaviAttiveDelloStore()
    {
        using var state = CreateMasterState();
        state.Master.Requisiti.Save(Enumerable.Range(0, 600)
            .Select(i => new Requisito { Id = $"REQ-{i:D4}", Testo = $"Requisito {i:D4}", ModificatoDa = "Seed" })
            .ToList());
        using var vm = new RequisitiViewModel(state);
        Assert.True(vm.UsaPaginazione);

        vm.Unione.ChiaveSelezionata = "REQ-0042";

        var atteso = MasterDataPagedReadSources.Requisiti(state.Paths).ReadAllActiveKeys()
            .Where(k => !k.Equals("REQ-0042", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(atteso, vm.Unione.AltreVociPerUnione);
    }

    [Fact]
    public void Componente_PagingAttivo_ElencoUnisci_UgualeAlleChiaviAttiveDelloStore()
    {
        using var state = CreateMasterState();
        state.Master.Componente.Save(Enumerable.Range(0, 450)
            .Select(i => new Componente { Nome = $"Componente-{i:D4}", ModificatoDa = "Seed" })
            .ToList());
        using var vm = new EcuViewModel(state);
        Assert.True(vm.UsaPaginazione);

        vm.Unione.ChiaveSelezionata = "Componente-0007";

        var atteso = MasterDataPagedReadSources.Componente(state.Paths).ReadAllActiveKeys()
            .Where(k => !k.Equals("Componente-0007", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(atteso, vm.Unione.AltreVociPerUnione);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Componente-00")]
    [InlineData("Componente-0123")]
    public void Componente_PagingAttivo_PrimaPaginaFiltrata_UgualeAllaQueryDelloStore(string filtro)
    {
        using var state = CreateMasterState();
        state.Master.Componente.Save(Enumerable.Range(0, 400)
            .Select(i => new Componente { Nome = $"Componente-{i:D4}", ModificatoDa = "Seed" })
            .ToList());
        using var vm = new EcuViewModel(state);
        Assert.True(vm.UsaPaginazione);

        vm.FiltroTesto = filtro;
        vm.ConfermaRicercaImmediata(); // Invio: la prima pagina filtrata si applica subito (il debounce e' in MasterRicercaDebounceTests)

        var atteso = MasterDataPagedReadSources.Componente(state.Paths).ReadPage(filtro, 1, vm.DimensionePagina).Items
            .Select(e => e.Nome).ToList();
        Assert.Equal(atteso, vm.ItemsAttivi.Select(e => e.Nome).ToList());
    }

    [Theory]
    [InlineData("")]
    [InlineData("REQ-00")]
    [InlineData("REQ-0123")]
    public void Requisiti_PagingAttivo_PrimaPaginaFiltrata_UgualeAllaQueryDelloStore(string filtro)
    {
        using var state = CreateMasterState();
        state.Master.Requisiti.Save(Enumerable.Range(0, 400)
            .Select(i => new Requisito { Id = $"REQ-{i:D4}", Testo = $"Requisito {i:D4}", ModificatoDa = "Seed" })
            .ToList());
        using var vm = new RequisitiViewModel(state);
        Assert.True(vm.UsaPaginazione);

        vm.FiltroTesto = filtro;
        vm.ConfermaRicercaImmediata(); // Invio: la prima pagina filtrata si applica subito (il debounce e' in MasterRicercaDebounceTests)

        var atteso = MasterDataPagedReadSources.Requisiti(state.Paths).ReadPage(filtro, 1, vm.DimensionePagina).Items
            .Select(r => r.Id).ToList();
        Assert.Equal(atteso, vm.ItemsAttivi.Select(r => r.Id).ToList());
    }
}
