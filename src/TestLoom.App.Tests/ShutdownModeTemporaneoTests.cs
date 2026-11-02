using Avalonia.Controls;
using TestLoom.App;

namespace TestLoom.App.Tests;

/// <summary>Copertura del FAIL critico della review indipendente sul ramo perf/6.4.0-freddo-residuo:
/// App.axaml.cs.ScaldaViewFuoriSchermoAsync apre e chiude una Window invisibile DENTRO PreparaAvvioAsync,
/// PRIMA che MainWindow esista. Sul percorso di avvio veloce (bootstrap sotto SogliaComparsaAvvio) l'AvvioWindow
/// non viene mai creata, quindi quella Window di scaldamento e' l'UNICA finestra aperta in quel momento: con lo
/// ShutdownMode di default di Avalonia (OnLastWindowClose, non impostato altrove nel repo) il suo Close()
/// chiuderebbe l'"ultima finestra" e terminerebbe l'intero processo prima di mostrare MainWindow.
///
/// ShutdownModeTemporaneo (App.axaml.cs.RunStartupAsync) forza OnExplicitShutdown per l'intera finestra
/// temporale a rischio e ripristina il valore effettivo precedente solo dopo mainWindow.Show(). Non e'
/// possibile costruire un finto IClassicDesktopStyleApplicationLifetime per testare questo comportamento
/// contro l'interfaccia reale: Avalonia la marca "not implementable by user code" (CS0535 con quel messaggio
/// esplicito, verificato in questa sessione). ShutdownModeTemporaneo e' quindi disaccoppiato dall'interfaccia
/// tramite due semplici delegati lettura/scrittura, verificabili qui con una semplice variabile locale al posto
/// del vero lifetime — stesso contratto, nessuna dipendenza da Avalonia headless/finestre reali.</summary>
public sealed class ShutdownModeTemporaneoTests
{
    [Fact]
    public void Costruzione_ForzaSubitoIlValoreTemporaneo_EMemorizzaQuelloEffettivoPrecedente()
    {
        var valoreCorrente = ShutdownMode.OnLastWindowClose; // default reale di Avalonia, mai impostato nel repo
        var scope = new ShutdownModeTemporaneo(
            () => valoreCorrente, v => valoreCorrente = v, ShutdownMode.OnExplicitShutdown);

        Assert.Equal(ShutdownMode.OnExplicitShutdown, valoreCorrente); // gia' forzato
        Assert.Equal(ShutdownMode.OnLastWindowClose, scope.ValoreOriginale); // ma quello vero non e' perso
    }

    [Fact]
    public void Dispose_RipristinaEsattamenteIlValoreEffettivoLettoAllaCostruzione_NonUnDefaultAssunto()
    {
        // Un valore diverso dal default (es. impostato altrove nel repo in futuro) deve tornare esattamente
        // quello, non OnLastWindowClose per assunzione.
        var valoreCorrente = ShutdownMode.OnMainWindowClose;
        var scope = new ShutdownModeTemporaneo(
            () => valoreCorrente, v => valoreCorrente = v, ShutdownMode.OnExplicitShutdown);

        scope.Dispose();

        Assert.Equal(ShutdownMode.OnMainWindowClose, valoreCorrente);
    }

    [Fact]
    public void Dispose_EIdempotente_UnaSecondaChiamataNonSovrascriveUnValoreImpostatoDopoIlPrimoRipristino()
    {
        var valoreCorrente = ShutdownMode.OnLastWindowClose;
        var scope = new ShutdownModeTemporaneo(
            () => valoreCorrente, v => valoreCorrente = v, ShutdownMode.OnExplicitShutdown);

        scope.Dispose();
        Assert.Equal(ShutdownMode.OnLastWindowClose, valoreCorrente);

        // Qualcos'altro (es. codice successivo nel metodo reale) cambia di nuovo il valore dopo il ripristino.
        valoreCorrente = ShutdownMode.OnMainWindowClose;

        // Un secondo Dispose (es. dal ramo catch difensivo di RunStartupAsync dopo che il ramo try l'ha gia'
        // chiamato) non deve rimettere OnLastWindowClose sopra al valore appena impostato da altri.
        scope.Dispose();
        Assert.Equal(ShutdownMode.OnMainWindowClose, valoreCorrente);
    }

    [Fact]
    public void Dispose_VieneChiamatoDaUnFinally_AncheSeLAzioneProtettaLanciaUnEccezione()
    {
        // Riproduce lo schema reale: try { mainWindow.Show(); } finally { shutdownModeTemporaneo.Dispose(); }.
        // Un fallimento nell'azione protetta non deve lasciare l'app bloccata su OnExplicitShutdown.
        var valoreCorrente = ShutdownMode.OnLastWindowClose;
        var scope = new ShutdownModeTemporaneo(
            () => valoreCorrente, v => valoreCorrente = v, ShutdownMode.OnExplicitShutdown);

        try
        {
            try { throw new InvalidOperationException("mainWindow.Show() fallita"); }
            finally { scope.Dispose(); }
        }
        catch (InvalidOperationException)
        {
            // atteso: qui simuliamo il catch esterno di RunStartupAsync, che non deve trovare il valore
            // ancora forzato.
        }

        Assert.Equal(ShutdownMode.OnLastWindowClose, valoreCorrente);
    }
}
