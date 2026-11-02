using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Fallimenti del rendering asincrono dei grafici (Category "Grafici"): un'eccezione di rendering o un PNG non
/// decodificato non devono essere silenziosi. Il fallimento viene registrato nel crash.log e notificato; nessun risultato
/// viene applicato; la stessa dimensione puo' essere richiesta di nuovo; un render superato non segnala errori.</summary>
[Trait("Category", "Grafici")]
public class RenderGraficoAsincronoFallimentoTests
{
    private static readonly DimensioniGrafico Dimensioni = new(640, 320, 1.25);
    private static readonly TimeSpan Attesa = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EccezioneDiRender_RegistraNelLogENotificaSenzaApplicare()
    {
        var marcatore = Guid.NewGuid().ToString("N");
        var applicati = 0;
        var fallimento = new TaskCompletionSource();
        using var render = new RenderGraficoAsincrono<string>(
            _ => throw new InvalidOperationException("errore di prova " + marcatore),
            png => "decodificato",
            _ => applicati++,
            TimeSpan.Zero,
            () => fallimento.TrySetResult());

        Assert.True(render.Richiedi(Dimensioni));
        await fallimento.Task.WaitAsync(Attesa);

        Assert.Equal(0, applicati);
        Assert.Contains(marcatore, LeggiCrashLog());
        Assert.Contains("Rendering del grafico", LeggiCrashLog());
    }

    [Fact]
    public async Task PngNonDecodificato_NotificaEScriveNelLog()
    {
        var applicati = 0;
        var fallimento = new TaskCompletionSource();
        using var render = new RenderGraficoAsincrono<string>(
            _ => new byte[] { 1, 2, 3 },
            _ => null,
            _ => applicati++,
            TimeSpan.Zero,
            () => fallimento.TrySetResult());

        Assert.True(render.Richiedi(Dimensioni));
        await fallimento.Task.WaitAsync(Attesa);

        Assert.Equal(0, applicati);
        Assert.Contains("PNG del grafico non decodificato", LeggiCrashLog());
    }

    [Fact]
    public async Task Successo_ApplicaSenzaFallimento()
    {
        var applicato = new TaskCompletionSource<string>();
        var fallimenti = 0;
        using var render = new RenderGraficoAsincrono<string>(
            _ => new byte[] { 1 },
            _ => "ok",
            s => applicato.TrySetResult(s),
            TimeSpan.Zero,
            () => fallimenti++);

        render.Richiedi(Dimensioni);

        Assert.Equal("ok", await applicato.Task.WaitAsync(Attesa));
        Assert.Equal(0, fallimenti);
    }

    [Fact]
    public async Task DopoUnFallimento_LaStessaDimensioneSiRiRichiede()
    {
        var tentativi = 0;
        var secondoEsito = new TaskCompletionSource<string>();
        var fallimento = new TaskCompletionSource();
        using var render = new RenderGraficoAsincrono<string>(
            _ =>
            {
                if (Interlocked.Increment(ref tentativi) == 1) throw new InvalidOperationException("primo tentativo");
                return new byte[] { 1 };
            },
            _ => "ok",
            s => secondoEsito.TrySetResult(s),
            TimeSpan.Zero,
            () => fallimento.TrySetResult());

        Assert.True(render.Richiedi(Dimensioni));
        await fallimento.Task.WaitAsync(Attesa);

        // La stessa dimensione non e' piu' considerata "gia' richiesta": un nuovo ridimensionamento ritenta il render.
        Assert.True(render.Richiedi(Dimensioni));
        Assert.Equal("ok", await secondoEsito.Task.WaitAsync(Attesa));
    }

    [Fact]
    public async Task DimensioneGiaRiuscita_NonRiRichiedeIlRender()
    {
        var applicato = new TaskCompletionSource<string>();
        var render = new RenderGraficoAsincrono<string>(_ => new byte[] { 1 }, _ => "ok", s => applicato.TrySetResult(s), TimeSpan.Zero);
        using var _ = render;

        Assert.True(render.Richiedi(Dimensioni));
        await applicato.Task.WaitAsync(Attesa);

        Assert.False(render.Richiedi(Dimensioni));
    }

    [Fact]
    public async Task RenderSuperato_NonSegnalaFallimento()
    {
        var fallimenti = 0;
        var applicati = new TaskCompletionSource<string>();
        using var render = new RenderGraficoAsincrono<string>(
            d => d.Larghezza > 700 ? new byte[] { 1 } : throw new InvalidOperationException("dimensione superata"),
            _ => "ultimo",
            s => applicati.TrySetResult(s),
            TimeSpan.FromMilliseconds(50),
            () => Interlocked.Increment(ref fallimenti));

        render.Richiedi(new DimensioniGrafico(300, 200, 1));
        render.Richiedi(new DimensioniGrafico(800, 200, 1));

        Assert.Equal("ultimo", await applicati.Task.WaitAsync(Attesa));
        Assert.Equal(0, Volatile.Read(ref fallimenti));
    }

    private static string LeggiCrashLog()
    {
        var percorso = Path.Combine(CrashLogService.LogDirectory, "crash.log");
        if (!File.Exists(percorso)) return "";
        using var flusso = new FileStream(percorso, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var lettore = new StreamReader(flusso);
        return lettore.ReadToEnd();
    }
}
