using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressione per il pannello "Unisci" dei cataloghi master ad alta crescita (Requisiti/Funzionalità/Componente),
/// paginati sul read-model SQLite locale (vedi MasterDataPagingSafetyTests per il paging puro). Il picker
/// "Unisci in..." deve elencare TUTTE le voci attive del catalogo, non solo quelle della pagina corrente
/// caricata in Items: bug utente (MasterDataUnioneTests.SelezionareUnaRiga_MostraLeAzioniUnisciRinominaDividi,
/// suite FlaUI) dovuto a Ricarica() che usciva prima di AggiornaSnapshotDaItems (che sola aggiornava
/// Unione.AggiornaVociDisponibili) quando ProvaAttivaVisualizzazionePaginata attivava la vista paginata.
/// </summary>
public sealed class MasterDataUnionePaginataTests : IDisposable
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
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterUnionePaged_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        Directory.CreateDirectory(dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(dir);
        var operatori = state.Master.Operatori.Load();
        operatori.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Unione Paged",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Master Unione Paged", "1234"));
        return state;
    }

    /// <summary>Catalogo di 1000 voci (oltre la soglia dei benchmark richiesti): la prima pagina in memoria ne
    /// contiene solo 200 (DimensionePagina), ma il picker "Unisci in..." deve comunque proporre voci ben oltre
    /// la prima pagina, incluse le ultime del catalogo.</summary>
    [Fact]
    public void Requisiti_PagingAttivo_VociUnioneDisponibiliCopronoTuttoIlCatalogo()
    {
        const int totale = 1000;
        using var state = CreateMasterState();
        state.Master.Requisiti.Save(Enumerable.Range(0, totale)
            .Select(i => new Requisito { Id = $"REQ-{i:D4}", Testo = $"Requisito {i:D4}", ModificatoDa = "Seed" })
            .ToList());

        using var vm = new RequisitiViewModel(state);
        Assert.True(vm.UsaPaginazione);
        Assert.Equal(200, vm.Items.Count); // solo la prima pagina è materializzata in Items

        vm.Unione.ChiaveSelezionata = "REQ-0000";
        var disponibili = vm.Unione.AltreVociPerUnione;

        // Senza il fix, AltreVociPerUnione resta vuoto/limitato alla sola pagina corrente perché
        // Unione.AggiornaVociDisponibili non viene mai chiamato quando la vista paginata si attiva.
        Assert.Equal(totale - 1, disponibili.Count);
        Assert.Contains("REQ-0999", disponibili); // ultima voce, ben oltre la prima pagina da 200
        Assert.Contains("REQ-0500", disponibili); // voce di mezzo, fuori dalla prima pagina
        Assert.DoesNotContain("REQ-0000", disponibili); // la voce selezionata resta esclusa
    }

    /// <summary>L'elenco deve riallinearsi anche su un evento DatiRicaricati (scrittura di un'altra postazione),
    /// non solo alla prima apertura della pagina.</summary>
    [Fact]
    public void Requisiti_PagingAttivo_VociUnioneSiAggiornanoDopoScritturaDiAltraPostazione()
    {
        using var state = CreateMasterState();
        state.Master.Requisiti.Save(Enumerable.Range(0, 300)
            .Select(i => new Requisito { Id = $"REQ-{i:D3}", Testo = $"Requisito {i:D3}", ModificatoDa = "Seed" })
            .ToList());

        using var vm = new RequisitiViewModel(state);
        Assert.True(vm.UsaPaginazione);

        // Un'altra postazione aggiunge una voce che finirebbe fuori dalla prima pagina in memoria.
        var righe = state.Master.Requisiti.Load();
        righe.Add(new Requisito { Id = "REQ-999", Testo = "Aggiunta da altra postazione", ModificatoDa = "Altra postazione" });
        state.Master.Requisiti.Save(righe);

        state.NotificaDatiRicaricati();

        vm.Unione.ChiaveSelezionata = "REQ-000";
        Assert.Contains("REQ-999", vm.Unione.AltreVociPerUnione);
    }
}
