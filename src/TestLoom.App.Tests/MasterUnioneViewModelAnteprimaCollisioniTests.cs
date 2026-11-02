using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Re-review indipendente 2026-09-24 [MEDIUM]: MasterRiferimentiService.ContaCollisioniValutazioni
/// esisteva ma nessun chiamante di produzione lo usava — il dialog di conferma mostrato PRIMA di Unisci/
/// Rinomina/Dividi (MasterUnioneViewModel.Unisci/Rinomina/Dividi, messaggio MasterUnioneConfermaBodyFmt e
/// affini) non riportava quante valutazioni della matrice Audit sarebbero state consolidate. Collegato tramite
/// MasterUnioneViewModel.AggiungiRigaCollisioniValutazioni: verificato qui a livello di ViewModel, catturando
/// il testo passato a ConfirmService (stesso schema di AnomalieIntegrateNelTestTests) invece di dover aprire
/// davvero la UI.</summary>
public sealed class MasterUnioneViewModelAnteprimaCollisioniTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterUnioneAnteprimaCollisioni_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _handlerOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _handlerOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    [Fact]
    public async Task Unisci_ConCollisioneDiValutazioni_AnteprimaRiportaIlConteggioEBloccaFinoAConferma()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM-OLD" }, new() { Nome = "BCM" } });
        state.Master.ValutazioniRequisitoEcuVeicolo.Save(new List<ValutazioneRequisitoEcuVeicolo>
        {
            new() { RequisitoId = "R1", Componente = "BCM-OLD", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme, Nota = "vecchia", Operatore = "AB" },
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme, Nota = "già su BCM", Operatore = "CD" },
        });
        var vm = new MasterUnioneViewModel<Componente>(state, state.Master.Componente, e => e.Nome);

        string? messaggioCatturato = null;
        ConfirmService.Handler = (_, messaggio, _, _) => { messaggioCatturato = messaggio; return Task.FromResult(false); }; // annulla: verifica solo il testo dell'anteprima

        var eseguita = await vm.Unisci("BCM-OLD", "BCM");

        Assert.False(eseguita); // conferma annullata, nessuna mutazione
        Assert.NotNull(messaggioCatturato);
        Assert.Contains("1", messaggioCatturato); // 1 collisione prevista (R1|BCM|My24)
        Assert.Contains("consolidate automaticamente", messaggioCatturato);
        // Nessuna mutazione avvenuta davvero: il componente non e' stata unita, le due valutazioni originali restano.
        Assert.Equal(2, state.Master.Componente.Load().Count);
        Assert.Equal(2, state.Master.ValutazioniRequisitoEcuVeicolo.Load().Count);
    }

    [Fact]
    public async Task Unisci_SenzaCollisioniDiValutazioni_AnteprimaNonMenzionaConsolidamento()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM-OLD" }, new() { Nome = "BCM" } });
        // Nessuna valutazione salvata per nessuna delle due componente: nessuna collisione possibile.
        var vm = new MasterUnioneViewModel<Componente>(state, state.Master.Componente, e => e.Nome);

        string? messaggioCatturato = null;
        ConfirmService.Handler = (_, messaggio, _, _) => { messaggioCatturato = messaggio; return Task.FromResult(false); };

        await vm.Unisci("BCM-OLD", "BCM");

        Assert.NotNull(messaggioCatturato);
        Assert.DoesNotContain("consolidate automaticamente", messaggioCatturato);
    }

    [Fact]
    public async Task Rinomina_ConCollisioneDiValutazioni_AnteprimaRiportaIlConteggio()
    {
        var state = CreaStato();
        // "My24b" non esiste ancora nel catalogo Veicoli: la Rinomina lo consente (se esistesse già,
        // MasterUnioneViewModel.Rinomina proporrebbe Unisci invece, comportamento invariato e non l'oggetto di
        // questo test).
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "My24" } });
        state.Master.ValutazioniRequisitoEcuVeicolo.Save(new List<ValutazioneRequisitoEcuVeicolo>
        {
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme, Nota = "vecchia", Operatore = "AB" },
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24b", Punteggio = PunteggioConformita.Conforme, Nota = "già presente", Operatore = "CD" },
        });
        var vm = new MasterUnioneViewModel<Veicolo>(state, state.Master.Veicoli, v => v.Nome);

        string? messaggioCatturato = null;
        ConfirmService.Handler = (_, messaggio, _, _) => { messaggioCatturato = messaggio; return Task.FromResult(false); };

        await vm.Rinomina("My24", "My24b");

        Assert.NotNull(messaggioCatturato);
        Assert.Contains("1", messaggioCatturato);
        Assert.Contains("consolidate automaticamente", messaggioCatturato);
    }
}
