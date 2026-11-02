using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Navigazione di MainViewModel nei test. Il cambio scheda e' sincrono solo senza un'applicazione Avalonia nel
/// processo: con Application.Current valorizzata (la avviano i test headless di layout e grafici) MostraPaginaDopoFrame
/// cede un frame al dispatcher, quindi SelectedNavItem torna subito ma CurrentPage si aggiorna dopo. Chi legge
/// CurrentPage deve attendere la fine del caricamento (CaricamentoPagina torna falso); senza app l'attesa non dura nulla.</summary>
internal static class NavigazioneTestHelper
{
    /// <summary>Seleziona la scheda e attende la fine del caricamento.</summary>
    public static async Task NavigaAsync(MainViewModel main, string navId)
    {
        main.SelectedNavItem = main.NavItems.First(n => n.Id == navId);
        await AttendiCaricamentoAsync(main);
    }

    /// <summary>Attende la fine di una navigazione gia' avviata (da SelectedNavItem o da un comando). Senza navigazione in
    /// corso ritorna subito. Il limite evita che un test resti bloccato se la navigazione non termina.</summary>
    public static async Task AttendiCaricamentoAsync(MainViewModel main)
    {
        var scadenza = DateTime.UtcNow.AddSeconds(15);
        while (main.CaricamentoPagina && DateTime.UtcNow < scadenza) await Task.Delay(5);
    }
}
