using Avalonia.Threading;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>P3.10: il conteggio notifiche autorevole può essere ricalcolato da un thread non UI (un DatiRicaricati sollevato
/// in background). L'avviso alla shell (tooltip e lampeggio dell'icona tray) deve però arrivare sul thread UI, altrimenti
/// l'aggiornamento della tray lancia eccezioni e l'avviso Windows non parte. Serve l'Application (Category=LayoutHeadless):
/// senza app la notifica resta sincrona sul thread chiamante, per i test unitari.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class MainViewModelNotificheThreadHeadlessTests : IDisposable
{
    private const string Operatore = "Operatore Prova Thread";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NotificheThread_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Sigilla(new() { Nome = Operatore, Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234") }) });
        Assert.True(state.ImpostaOperatore(Operatore, "1234"));
        OnboardingService.SegnaCompletato(state.Config, Operatore, RuoloOperatore.Standard);
        return state;
    }

    private static NotificaApp Nota(string tipo) =>
        new() { Destinatario = Operatore, Tipo = tipo, Titolo = "Titolo", Messaggio = "Messaggio" };

    [Fact]
    public async Task ConteggioRicalcolatoDaThreadNonUi_AvvisoArrivaSulThreadUi()
    {
        LayoutHeadlessSessione.Esegui(() => { });
        var state = CreaStato();
        var main = LayoutHeadlessSessione.Esegui(() => new MainViewModel(state));
        var threadAvviso = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        main.ConteggioNotificheRilevantiCambiato += (_, _) => threadAvviso.TrySetResult(Dispatcher.UIThread.CheckAccess());

        // Il DatiRicaricati arriva da un thread di pool (non dal thread UI): il ricalcolo del badge non deve eseguire
        // l'avviso alla shell su quel thread.
        await Task.Run(() =>
        {
            state.Notifiche.Add(Nota("ChangeRequest"));
            state.NotificaDatiRicaricati();
        });

        Assert.True(await threadAvviso.Task.WaitAsync(TimeSpan.FromSeconds(10)), "L'avviso alla shell e' stato eseguito fuori dal thread UI.");
        Assert.Equal(1, LayoutHeadlessSessione.Esegui(() => main.NotificheNonLette));
    }
}
