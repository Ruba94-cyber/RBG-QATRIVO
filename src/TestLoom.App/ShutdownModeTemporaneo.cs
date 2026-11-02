using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace TestLoom.App;

/// <summary>
/// Forza temporaneamente <see cref="IClassicDesktopStyleApplicationLifetime.ShutdownMode"/> a un valore
/// (tipicamente <see cref="ShutdownMode.OnExplicitShutdown"/>) e ripristina il valore effettivo precedente
/// quando viene chiamato <see cref="Dispose"/> — mai un valore assunto (es. sempre <c>OnLastWindowClose</c>),
/// cosi' un eventuale ShutdownMode diverso impostato altrove resta intatto dopo il ripristino.
///
/// Nato dalla revisione indipendente sul bug critico (piano 6.4.0, punto 2/4): sul percorso di avvio veloce
/// (bootstrap sotto la soglia di comparsa dell'AvvioWindow, vedi App.axaml.cs.RunStartupAsync) l'AvvioWindow
/// non viene mai creata — la prima Window a esistere e' quella invisibile fuori schermo usata per scaldare la
/// View "Test"/Editor (App.axaml.cs.ScaldaViewFuoriSchermoAsync), PRIMA che MainWindow esista. Con lo
/// ShutdownMode di default di Avalonia (OnLastWindowClose) il suo Close() la renderebbe l'ULTIMA finestra a
/// chiudersi e terminerebbe l'intero processo prima di mostrare MainWindow. Questo tipo forza
/// OnExplicitShutdown per tutta la finestra temporale in cui possono esistere solo finestre non definitive.
///
/// Disaccoppiato da <see cref="IClassicDesktopStyleApplicationLifetime"/> tramite lettura/scrittura passate
/// come delegati (non tramite l'interfaccia stessa): Avalonia marca quell'interfaccia e le sue basi come non
/// implementabili da codice utente (errore CS0535 "This interface or abstract class is -not- implementable by
/// user code"), quindi un test non può costruire un finto lifetime per verificare questo comportamento contro
/// l'interfaccia reale — puo' invece verificarlo contro due semplici delegati, come fa
/// ShutdownModeTemporaneoTests.
/// </summary>
internal sealed class ShutdownModeTemporaneo : IDisposable
{
    private readonly Action<ShutdownMode> _scrivi;
    private readonly ShutdownMode _valoreOriginale;
    private bool _ripristinato;

    public ShutdownModeTemporaneo(Func<ShutdownMode> leggi, Action<ShutdownMode> scrivi, ShutdownMode temporaneo)
    {
        _scrivi = scrivi;
        _valoreOriginale = leggi();
        scrivi(temporaneo);
    }

    /// <summary>Valore letto alla costruzione, PRIMA di forzare quello temporaneo: e' quello che Dispose
    /// ripristina. Esposto solo per i test (verificare che non sia mai stato perso).</summary>
    public ShutdownMode ValoreOriginale => _valoreOriginale;

    /// <summary>Idempotente: una seconda chiamata (es. da un secondo punto di uscita) non ripristina due volte
    /// né sovrascrive un valore impostato nel frattempo da altro codice dopo il primo ripristino.</summary>
    public void Dispose()
    {
        if (_ripristinato) return;
        _ripristinato = true;
        _scrivi(_valoreOriginale);
    }
}
