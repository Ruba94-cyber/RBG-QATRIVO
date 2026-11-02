using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Navigazione nell'applicazione reale (Category=LayoutHeadless, Application.Current valorizzata): SelectedNavItem
/// torna subito, la pagina arriva dopo un frame (MostraPaginaDopoFrame). Un comando che agisce sulla pagina appena aperta deve
/// attendere la navigazione. Nei test senza app la navigazione e' sincrona e il ritardo non si vede: il caso reale va quindi
/// riprodotto qui, nel processo headless (la guardia ProgettoTestPrincipaleSenzaHeadlessTests vieta l'Application nel progetto
/// principale).</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class NavigazioneAsincronaHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NavAsync_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;
    private volatile bool _confermaChiesta;

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState StatoMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Nav", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        state.Master.Operatori.Save(new List<Operatore> { op });
        state.ImpostaOperatore("Master Nav", "1234");
        return state;
    }

    /// <summary>Attende sul thread di test che la condizione diventi vera: la navigazione gira sul thread UI della pompa.</summary>
    private static async Task AttendiAsync(Func<bool> condizione, string descrizione)
    {
        var scadenza = DateTime.UtcNow.AddSeconds(5);
        while (!condizione() && DateTime.UtcNow < scadenza) await Task.Delay(5);
        Assert.True(condizione(), $"entro 5 s non si verifica: {descrizione}");
    }

    [Fact]
    public async Task VaiAImpostazioniGenerale_InAppReale_ApplicaGenerale_AncheConOrdineSottoSezioniPersonalizzato()
    {
        var state = StatoMaster();
        // L'utente ha spostato "operatori" davanti a "generale": Generale non e' la prima sotto-sezione.
        state.Config.OrdineImpostazioni = new List<string> { "operatori", "generale", "archivio" };
        var main = LayoutHeadlessSessione.Esegui(() => new MainViewModel(state) { MostraPrimoAvvio = false });

        LayoutHeadlessSessione.Esegui(() => main.VaiAImpostazioniGeneraleCommand.Execute(null));

        await AttendiAsync(() => main.CurrentPage is ImpostazioniViewModel p && p.SottoSezioneSelezionata?.Id == "generale",
            "Impostazioni aperte sulla sotto-sezione Generale");
        var pagina = Assert.IsType<ImpostazioniViewModel>(main.CurrentPage);
        Assert.True(pagina.EGenerale);
    }

    [Fact]
    public async Task VaiAImpostazioniGenerale_InAppReale_ConEditorConModificheAnnullato_NonCambiaLaPagina()
    {
        var state = StatoMaster();
        var main = LayoutHeadlessSessione.Esegui(() => new MainViewModel(state) { MostraPrimoAvvio = false });
        // La navigazione iniziale del costruttore finisce prima: altrimenti sovrascriverebbe l'editor impostato dal test.
        await AttendiAsync(() => !main.CaricamentoPagina, "navigazione iniziale completata");
        var editor = LayoutHeadlessSessione.Esegui(() =>
        {
            var e = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
            e.SceglieTemplateCommand.Execute(e.TemplatesDisponibili[0]);
            e.ModificheNonSalvate = true; // modifiche non ancora salvate, come le rileva HaModificheNonSalvate()
            main.CurrentPage = e;
            return e;
        });
        ConfirmService.Handler = (_, _, _, _) => { _confermaChiesta = true; return Task.FromResult(false); }; // l'utente annulla

        LayoutHeadlessSessione.Esegui(() => main.VaiAImpostazioniGeneraleCommand.Execute(null));

        await AttendiAsync(() => _confermaChiesta, "conferma di abbandono richiesta");
        LayoutHeadlessSessione.Esegui(() => { }); // svuota i messaggi ancora in coda sul dispatcher
        Assert.Same(editor, main.CurrentPage);
    }
}
