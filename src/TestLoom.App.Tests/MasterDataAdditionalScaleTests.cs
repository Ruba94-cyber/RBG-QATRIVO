using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Estende il gate 100k oltre Funzionalita: Requisiti ed componente sono cataloghi di prima classe e devono restare
/// utilizzabili allo stesso ordine di grandezza. In consultazione il requisito ora è anche strutturale: non
/// devono più essere materializzate 100k righe nella DataGrid; la fotografia completa viene caricata soltanto
/// dopo il lock quando si entra davvero in modifica.
/// </summary>
[Collection("Scale performance")]
public sealed class MasterDataAdditionalScaleTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterScaleMore_" + Guid.NewGuid().ToString("N"));

    public MasterDataAdditionalScaleTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;
        try { Directory.Delete(_dir, true); }
        catch { }
    }

    // Con Phase D attivo (sempre, dopo InizializzaCartella) il monolite JSON legacy resta congelato allo stato
    // del bootstrap invece di essere riscritto a ogni Save: per un catalogo mai salvato prima dell'attivazione
    // (come questi, appena creati nel test) il file può non esistere affatto. Le dimensioni qui sono solo
    // telemetria diagnostica per l'output del test, non un'asserzione di correttezza.
    private static long SafeLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private AppState CreateState(RuoloOperatore ruolo)
    {
        var state = TestStateFactory.Create();
        // Il preflight di startup e' rigorosamente non mutante (CondivisioneHealthService) e non crea mai la
        // cartella condivisa da solo: su un path non ancora esistente InizializzaCartella si ferma con
        // StatoCondivisione.NonDisponibile senza lanciare, lasciando Master null.
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var operatori = state.Master.Operatori.Load();
        operatori.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Op Scale Additional",
            Ruolo = ruolo,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Op Scale Additional", "1234"));
        return state;
    }

    [Fact]
    [Trait("Category", "Scale")]
    public async Task Requisiti100k_Master_AperturaFiltroESalvataggioRestanoLineari()
    {
        using var state = CreateState(RuoloOperatore.Master);
        const int count = 100_000;
        state.Master.Requisiti.Save(Enumerable.Range(0, count)
            .Select(i => new Requisito
            {
                Id = $"REQ-{i:D6}",
                Testo = i == count - 1 ? "Testo requisito target 099999" : $"Requisito prestazioni {i:D6}",
                ApplicabileBanco = (i & 1) == 0,
                ApplicabileVeicolo = true,
                ModificatoDa = "Seed"
            })
            .ToList());

        var fileBytesPrima = SafeLength(state.Paths.RequisitiFile);
        var watch = Stopwatch.StartNew();
        using var vm = new RequisitiViewModel(state);
        var apertura = watch.Elapsed;
        Assert.True(vm.UsaPaginazione);
        Assert.Equal(vm.DimensionePagina, vm.Items.Count);
        Assert.Equal(vm.DimensionePagina, vm.ItemsAttivi.Count);
        Assert.True(vm.PaginaSuccessivaDisponibile);

        watch.Restart();
        vm.FiltroTesto = "target 099999";
        vm.ConfermaRicercaImmediata(); // Invio: la misura include la query, senza attendere il debounce
        var filtro = watch.Elapsed;
        var filtrato = Assert.Single(vm.ItemsAttivi);
        Assert.Equal("REQ-099999", filtrato.Id);

        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        Assert.True(vm.UsaPaginazione);
        // Dal refactor "Edit Phase D master pages without full-catalog promotion" (91fc21f) l'editing di un
        // catalogo a 100k righe resta O(page-size) in RAM invece di promuovere all'intera fotografia: la
        // finestra caricata rispetta ancora il filtro attivo, quindi qui contiene solo la riga cercata.
        vm.Items.Single(r => r.Id == "REQ-099999").Testo = "Testo requisito target 099999 modificato";

        watch.Restart();
        await vm.SalvaCommand.ExecuteAsync(null);
        var salvataggio = watch.Elapsed;

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.False(vm.InModifica);
        Assert.True(vm.UsaPaginazione);
        Assert.True(vm.Items.Count <= vm.DimensionePagina);
        Assert.Equal(count, state.Master.Requisiti.Load().Count);
        Assert.Equal("Testo requisito target 099999 modificato",
            state.Master.Requisiti.Load().Single(r => r.Id == "REQ-099999").Testo);

        var fileBytesDopo = SafeLength(state.Paths.RequisitiFile);
        var previousBytes = SafeLength(state.Paths.RequisitiFile + ".prev");
        var ramMiB = Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0;
        _output.WriteLine($"MASTER_SCALE|type=Requisiti|count={count}|open_ms={apertura.TotalMilliseconds:F0}|filter_ms={filtro.TotalMilliseconds:F0}|save_ms={salvataggio.TotalMilliseconds:F0}|file_before_bytes={fileBytesPrima}|file_after_bytes={fileBytesDopo}|prev_bytes={previousBytes}|one_row_write_bytes={fileBytesDopo + previousBytes}|ram_mib={ramMiB:F1}");

        Assert.True(apertura < TimeSpan.FromSeconds(60), $"Apertura 100k Requisiti troppo lenta: {apertura}");
        Assert.True(filtro < TimeSpan.FromSeconds(15), $"Filtro 100k Requisiti troppo lento: {filtro}");
        Assert.True(salvataggio < TimeSpan.FromSeconds(120), $"Salvataggio 100k Requisiti troppo lento: {salvataggio}");
    }

    [Fact]
    [Trait("Category", "Scale")]
    public async Task Componente100k_Standard_AperturaFiltroEAggiuntaRestanoLineari()
    {
        using var state = CreateState(RuoloOperatore.Standard);
        const int count = 100_000;
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = "Veicolo-Scale", GammaNome = "Scale", ModificatoDa = "Seed" }
        });
        state.Master.Componente.Save(Enumerable.Range(0, count)
            .Select(i => new Componente
            {
                Nome = i == count - 1 ? "Componente-TARGET-099999" : $"Componente-{i:D6}",
                VeicoliAssociati = new List<string> { "Veicolo-Scale" },
                ModificatoDa = "Seed"
            })
            .ToList());

        var fileBytesPrima = SafeLength(state.Paths.EcuFile);
        var watch = Stopwatch.StartNew();
        using var vm = new EcuViewModel(state);
        var apertura = watch.Elapsed;
        Assert.True(vm.UsaPaginazione);
        Assert.Equal(vm.DimensionePagina, vm.Items.Count);
        Assert.Equal(vm.DimensionePagina, vm.ItemsAttivi.Count);
        Assert.True(vm.PuoCreare);
        Assert.False(vm.PuoModificare);

        watch.Restart();
        vm.FiltroTesto = "Componente-TARGET-099999";
        vm.ConfermaRicercaImmediata(); // Invio: la ricerca paginata si applica subito, senza attendere il debounce
        var filtro = watch.Elapsed;
        Assert.Equal("Componente-TARGET-099999", Assert.Single(vm.ItemsAttivi).Nome);

        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        // Dal refactor "Edit Phase D master pages without full-catalog promotion" (91fc21f) l'editing di un
        // catalogo a 100k righe resta O(page-size) in RAM: la finestra caricata rispetta ancora il filtro
        // attivo, quindi qui contiene solo la riga cercata prima dell'aggiunta sotto.
        vm.NuovoCommand.Execute(null);
        var nuova = vm.Items[^1];
        nuova.Nome = "Componente-100000";
        nuova.VeicoliAssociati = new List<string> { "Veicolo-Scale" };

        watch.Restart();
        await vm.SalvaCommand.ExecuteAsync(null);
        var salvataggio = watch.Elapsed;

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.False(vm.InModifica);
        Assert.True(vm.UsaPaginazione);
        Assert.True(vm.Items.Count <= vm.DimensionePagina);
        Assert.Equal(count + 1, state.Master.Componente.Load().Count);
        Assert.Contains(state.Master.Componente.Load(), e => e.Nome == "Componente-100000");

        var fileBytesDopo = SafeLength(state.Paths.EcuFile);
        var previousBytes = SafeLength(state.Paths.EcuFile + ".prev");
        var ramMiB = Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0;
        _output.WriteLine($"MASTER_SCALE|type=componente|count={count}|open_ms={apertura.TotalMilliseconds:F0}|filter_ms={filtro.TotalMilliseconds:F0}|save_ms={salvataggio.TotalMilliseconds:F0}|file_before_bytes={fileBytesPrima}|file_after_bytes={fileBytesDopo}|prev_bytes={previousBytes}|one_row_write_bytes={fileBytesDopo + previousBytes}|ram_mib={ramMiB:F1}");

        Assert.True(apertura < TimeSpan.FromSeconds(60), $"Apertura 100k componente troppo lenta: {apertura}");
        Assert.True(filtro < TimeSpan.FromSeconds(15), $"Filtro 100k componente troppo lento: {filtro}");
        Assert.True(salvataggio < TimeSpan.FromSeconds(120), $"Salvataggio 100k componente troppo lento: {salvataggio}");
    }
}
