using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// La pagina Anomalie/CR usa la proiezione di elenco: totali e badge esatti, una sola pagina materializzata (nessuna riga visuale per
/// l'intero archivio), nessun dato stantio dopo un salvataggio, annullamento delle richieste superate nella digitazione rapida.
/// </summary>
[Collection("Localization")]
public sealed class AnomalieCrElencoViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AnomalieCrVm_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato(int cr)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var r = new Random(21);
        for (var n = 1; n <= cr; n++)
        {
            var c = ChangeRequestElencoTests.Genera(r, n);
            c.Archiviata = false;
            state.ChangeRequests.Save(c);
        }
        return state;
    }

    [Fact]
    public void PaginaMaterializzaSoloUnaPaginaETotaliSonoEsatti()
    {
        using var state = CreaStato(620);
        var vm = new NonConformitaViewModel(state, _ => { });
        Assert.Equal(620, vm.TotaleCr);
        Assert.Equal(250, vm.ChangeRequests.Count); // una pagina, non 620 righe
        Assert.True(vm.Righe.Count <= 250);
        Assert.Equal(3, vm.PagineTotaliCr);

        vm.FiltroStatoCr = StatoChangeRequest.Chiusa;
        var attesi = state.ChangeRequests.PeekAllReadOnly().Count(c => c.Stato == StatoChangeRequest.Chiusa);
        Assert.Equal(attesi, vm.TotaleCr);
        Assert.All(vm.ChangeRequests, c => Assert.Equal(StatoChangeRequest.Chiusa, c.Stato));

        vm.FiltroStatoCr = null;
        vm.FiltroTesto = "CR-2026-00";
        var attesiTesto = NonConformitaViewModel.CalcolaPaginaChangeRequest(state.ChangeRequests.PeekAllReadOnly(),
            new NonConformitaViewModel.FiltroChangeRequest(null, "CR-2026-00", null, null, null, null, false, null, null, null, null, null, null, 1), null);
        Assert.Equal(attesiTesto.Totale, vm.TotaleCr);
        Assert.Equal(attesiTesto.Righe.Select(c => c.Id), vm.ChangeRequests.Select(c => c.Id));
    }

    [Fact]
    public void DopoUnSalvataggioLaPaginaNonMostraDatiStantii()
    {
        using var state = CreaStato(80);
        var vm = new NonConformitaViewModel(state, _ => { });
        var totale = vm.TotaleCr;
        var nuova = ChangeRequestElencoTests.Genera(new Random(5), 9001);
        nuova.Archiviata = false; nuova.Titolo = "Appena inserita"; nuova.DataCreazione = DateTime.Now.AddYears(1);
        state.ChangeRequests.Save(nuova);

        vm.Aggiorna();
        Assert.Equal(totale + 1, vm.TotaleCr);
        Assert.Equal("Appena inserita", vm.ChangeRequests.First().Titolo); // ordinamento predefinito: apertura decrescente

        var daArchiviare = state.ChangeRequests.GetById(nuova.Id)!;
        state.ChangeRequests.Archivia(daArchiviare, "Test");
        vm.Aggiorna();
        Assert.Equal(totale, vm.TotaleCr);
        Assert.DoesNotContain(vm.ChangeRequests, c => c.Id == nuova.Id);
    }

    [Fact]
    public void OpzioniFiltroDalleCrCoincidonoConLaLetturaStorica()
    {
        using var state = CreaStato(120);
        var vm = new NonConformitaViewModel(state, _ => { });
        var cr = state.ChangeRequests.PeekAllReadOnly();
        var veicoliAttesi = cr.SelectMany(c => c.VeicoliImpattati).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // le opzioni includono i veicoli delle CR (anche se assenti dal catalogo master)
        var opzioni = vm.FiltroVeicolo.Opzioni.Select(o => o.Valore).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(veicoliAttesi.IsSubsetOf(opzioni));
        var creatoriAttesi = cr.Select(c => c.CreatoDa).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(creatoriAttesi.IsSubsetOf(vm.FiltroCreatoreCr.Opzioni.Select(o => o.Valore).ToHashSet(StringComparer.OrdinalIgnoreCase)));
    }
}
