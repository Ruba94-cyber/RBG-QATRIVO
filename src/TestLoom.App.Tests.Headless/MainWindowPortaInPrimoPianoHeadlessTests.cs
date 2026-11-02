using Avalonia.Controls;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>P4 (6): riportare in primo piano (menu della tray, seconda istanza) non deve cambiare lo stato di una finestra
/// massimizzata. Il ripristino a Normale vale solo per una finestra minimizzata.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class MainWindowPortaInPrimoPianoHeadlessTests
{
    [Fact]
    public void MostraDaTray_UnaFinestraMassimizzataRestaMassimizzata() =>
        LayoutHeadlessSessione.Esegui(() => EseguiConFinestra(finestra =>
        {
            finestra.WindowState = WindowState.Maximized;
            finestra.MostraDaTray();
            Assert.Equal(WindowState.Maximized, finestra.WindowState);
        }));

    [Fact]
    public void MostraDaTray_UnaFinestraMinimizzataTornaNormale() =>
        LayoutHeadlessSessione.Esegui(() => EseguiConFinestra(finestra =>
        {
            finestra.WindowState = WindowState.Minimized;
            finestra.MostraDaTray();
            Assert.Equal(WindowState.Normal, finestra.WindowState);
        }));

    private static void EseguiConFinestra(Action<MainWindow> verifica)
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            LayoutHeadlessSessione.Mostra(finestra);
            verifica(finestra);
        }
        finally
        {
            MasterHeadless.Pulisci(dir);
        }
    }
}
