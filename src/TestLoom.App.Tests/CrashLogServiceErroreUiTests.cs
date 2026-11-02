using System.Text.RegularExpressions;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>P4 (1): un errore UI non gestito viene sempre registrato, ma l'avviso alla shell compare al massimo una volta
/// per intervallo: una rottura che si ripete non deve coprire la schermata.</summary>
[Collection("CrashLogUi")]
public sealed class CrashLogServiceErroreUiTests : IDisposable
{
    private readonly List<string> _segnalazioni = new();
    private readonly Action<string> _ascolta;

    public CrashLogServiceErroreUiTests()
    {
        CrashLogService.AzzeraLimiteAvvisoErroreUi();
        _ascolta = _segnalazioni.Add;
        CrashLogService.ErroreUiSegnalato += _ascolta;
    }

    public void Dispose()
    {
        CrashLogService.ErroreUiSegnalato -= _ascolta;
        CrashLogService.AzzeraLimiteAvvisoErroreUi();
    }

    [Fact]
    public void GestisciErroreUi_RegistraOgniVoltaESegnalaAlPiuUnaVoltaPerIntervallo()
    {
        var marcatore = "errore UI di prova " + Guid.NewGuid().ToString("N");
        var t0 = DateTime.UtcNow;
        var ex = new InvalidOperationException(marcatore);

        CrashLogService.GestisciErroreUi(ex, t0);
        CrashLogService.GestisciErroreUi(ex, t0.AddSeconds(5));
        Assert.Single(_segnalazioni);

        CrashLogService.GestisciErroreUi(ex, t0.Add(CrashLogService.IntervalloAvvisoErroreUi).AddSeconds(1));
        Assert.Equal(2, _segnalazioni.Count);

        // Ogni errore resta nel crash.log, anche quando l'avviso non viene ripetuto.
        var testoLog = File.ReadAllText(Path.Combine(CrashLogService.LogDirectory, "crash.log"));
        Assert.Equal(3, Regex.Count(testoLog, Regex.Escape(marcatore)));
        Assert.Contains("Avalonia.Dispatcher.UnhandledException", testoLog);
    }
}
