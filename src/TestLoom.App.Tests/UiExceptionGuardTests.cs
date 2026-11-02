using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class UiExceptionGuardTests
{
    [Fact]
    public async Task Esegui_EccezioneAsincrona_LaIntercettaERegistraIlContesto()
    {
        string? origine = null;
        Exception? catturata = null;

        await UiExceptionGuard.Esegui(
            () => Task.FromException(new InvalidOperationException("boom")),
            "apertura editor",
            (o, ex) => { origine = o; catturata = ex; });

        Assert.Equal("apertura editor", origine);
        Assert.IsType<InvalidOperationException>(catturata);
    }

    [Fact]
    public async Task Esegui_AzioneValida_LaCompletaSenzaRegistrareErrori()
    {
        var eseguita = false;
        var registrazioni = 0;

        await UiExceptionGuard.Esegui(() => { eseguita = true; return Task.CompletedTask; }, "azione", (_, _) => registrazioni++);

        Assert.True(eseguita);
        Assert.Equal(0, registrazioni);
    }

    [Fact]
    public async Task Esegui_ConMostra_AvvisaLUtenteDopoLaRegistrazione()
    {
        var ordine = new List<string>();

        await UiExceptionGuard.Esegui(
            () => throw new InvalidOperationException("boom"),
            "apertura editor",
            (o, _) => ordine.Add("registra:" + o),
            () => { ordine.Add("mostra"); return Task.CompletedTask; });

        Assert.Equal(new[] { "registra:apertura editor", "mostra" }, ordine);
    }

    [Fact]
    public async Task Esegui_SeLAvvisoStessoFallisce_RegistraAnchEssoSenzaPropagarlo()
    {
        var ordine = new List<string>();

        await UiExceptionGuard.Esegui(
            () => throw new InvalidOperationException("boom"),
            "apertura editor",
            (o, _) => ordine.Add("registra:" + o),
            () => throw new InvalidOperationException("finestra non disponibile"));

        Assert.Equal(new[] { "registra:apertura editor", "registra:apertura editor (messaggio di errore)" }, ordine);
    }
}
