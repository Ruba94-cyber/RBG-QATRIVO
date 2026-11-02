using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>P4 (5): la copia negli appunti riporta il proprio esito. Senza gestore (finestra non pronta) o con un errore del
/// sistema la copia NON e' riuscita, e il chiamante deve dirlo invece di confermare una copia mai avvenuta.</summary>
[Collection("Localization")]
public sealed class ClipboardServiceTests : IDisposable
{
    private readonly Func<string, Task<bool>>? _originale = ClipboardService.Handler;

    public void Dispose() => ClipboardService.Handler = _originale;

    [Fact]
    public async Task SenzaGestore_LaCopiaNonRiesce()
    {
        ClipboardService.Handler = null;

        Assert.False(await ClipboardService.CopiaTestoAsync("report"));
    }

    [Fact]
    public async Task GestoreRiuscito_RestituisceVeroEPassaIlTesto()
    {
        string? ricevuto = null;
        ClipboardService.Handler = testo => { ricevuto = testo; return Task.FromResult(true); };

        Assert.True(await ClipboardService.CopiaTestoAsync("report"));
        Assert.Equal("report", ricevuto);
    }

    [Fact]
    public async Task GestoreFallito_RestituisceFalso()
    {
        ClipboardService.Handler = _ => Task.FromResult(false);

        Assert.False(await ClipboardService.CopiaTestoAsync("report"));
    }

    [Fact]
    public async Task GestoreCheLanciaUnErrore_RestituisceFalsoSenzaPropagarlo()
    {
        ClipboardService.Handler = _ => throw new InvalidOperationException("appunti occupati");

        Assert.False(await ClipboardService.CopiaTestoAsync("report"));
    }
}
