using System.Diagnostics;

namespace TestLoom.App.Services;

/// <summary>
/// Cronologia della chiusura dell'app nel trace diagnostico (categoria "Chiusura"). Il tempo parte dal primo evento di
/// chiusura della finestra principale e ogni fase registra i millisecondi trascorsi: serve a vedere quale passo tiene TestLoom
/// in vita dopo la chiusura (dispose dei ViewModel, attesa della manutenzione in background, dialogo di conferma, uscita del
/// processo). Solo tracciamento: non cambia la logica di chiusura.
/// </summary>
public static class ChiusuraDiagnostica
{
    private static readonly object Gate = new();
    private static Stopwatch? _orologio;

    /// <summary>Avvia il cronometro alla prima chiamata; le chiamate successive non lo riavviano.</summary>
    public static void Inizia(string origine)
    {
        lock (Gate)
        {
            if (_orologio != null) return;
            _orologio = Stopwatch.StartNew();
        }
        Registra($"{origine}: inizio");
    }

    /// <summary>Registra una fase con il tempo trascorso dall'inizio della chiusura. Senza chiusura in corso non scrive nulla.</summary>
    public static void Registra(string fase)
    {
        var orologio = _orologio;
        if (orologio == null) return;
        DiagnosticTraceService.Registra("Chiusura", $"{fase} t={orologio.ElapsedMilliseconds}ms");
    }
}
