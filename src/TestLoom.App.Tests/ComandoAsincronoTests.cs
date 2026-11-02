using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>P4 (2): un comando async che fallisce viene registrato subito e riportato nel messaggio di errore del ViewModel,
/// senza propagare l'eccezione. L'annullamento non è un errore e non produce alcun messaggio.</summary>
public sealed class ComandoAsincronoTests
{
    [Fact]
    public async Task Eccezione_VieneRegistrataERiportataSenzaPropagarla()
    {
        var marcatore = "salvataggio di prova " + Guid.NewGuid().ToString("N");
        string? messaggio = null;

        await ComandoAsincrono.EseguiAsync(() => throw new InvalidOperationException(marcatore), "Salva (test)", m => messaggio = m);

        Assert.NotNull(messaggio);
        Assert.Contains(marcatore, messaggio);
        var testoLog = File.ReadAllText(Path.Combine(CrashLogService.LogDirectory, "crash.log"));
        Assert.Contains("Salva (test)", testoLog);
        Assert.Contains(marcatore, testoLog);
    }

    [Fact]
    public async Task Successo_EsegueLAzioneSenzaMessaggiDiErrore()
    {
        var eseguita = false;
        string? messaggio = null;

        await ComandoAsincrono.EseguiAsync(() => { eseguita = true; return Task.CompletedTask; }, "Salva (test)", m => messaggio = m);

        Assert.True(eseguita);
        Assert.Null(messaggio);
    }

    [Fact]
    public async Task Annullamento_NonEUnErroreESenzaMessaggio()
    {
        string? messaggio = null;

        await ComandoAsincrono.EseguiAsync(() => throw new OperationCanceledException(), "Salva (test)", m => messaggio = m);

        Assert.Null(messaggio);
    }

    [Fact]
    public async Task ErroreNelNotificatore_NonPropagaEIlComandoTermina()
    {
        var origine = "Notifier failure " + Guid.NewGuid().ToString("N");
        var erroreAzione = "azione " + Guid.NewGuid().ToString("N");
        var erroreNotifica = "notifica " + Guid.NewGuid().ToString("N");
        var command = new AsyncRelayCommand(() => ComandoAsincrono.EseguiAsync(
            () => throw new InvalidOperationException(erroreAzione),
            origine,
            _ => throw new InvalidOperationException(erroreNotifica)));

        var eccezione = await Record.ExceptionAsync(() => command.ExecuteAsync(null));

        Assert.Null(eccezione);
        Assert.False(command.IsRunning);
        var testoLog = File.ReadAllText(Path.Combine(CrashLogService.LogDirectory, "crash.log"));
        Assert.Contains(origine, testoLog);
        Assert.Contains(erroreAzione, testoLog);
        Assert.Contains(erroreNotifica, testoLog);
    }
}
