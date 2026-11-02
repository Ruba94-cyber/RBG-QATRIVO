using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata per il fix del freeze da I/O bloccante (feedback utente 2026-08-20, punto 5):
/// LockService e CondivisioneHealthService facevano I/O sincrono senza limiti sul thread UI. Qui si verifica il
/// meccanismo di base con timeout brevi e controllati, senza dover aspettare i 3 secondi reali usati in
/// produzione.</summary>
public class IoTimeoutGuardTests
{
    [Fact]
    public void Esegui_OperazioneVeloce_RitornaIlRisultatoVero()
    {
        var risultato = IoTimeoutGuard.Esegui(() => 42, TimeSpan.FromSeconds(5), -1);
        Assert.Equal(42, risultato);
    }

    [Fact]
    public void Esegui_OperazioneCheImpiegaTroppo_RitornaFallback_SenzaBloccareOltreIlTimeout()
    {
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        var risultato = IoTimeoutGuard.Esegui(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5)); // simula una cartella di rete/OneDrive in stallo
            return "mai raggiunto entro il timeout";
        }, TimeSpan.FromMilliseconds(100), "fallback");

        cronometro.Stop();

        Assert.Equal("fallback", risultato);
        Assert.True(cronometro.ElapsedMilliseconds < 2000, $"Il chiamante è rimasto bloccato {cronometro.ElapsedMilliseconds}ms, ben oltre il timeout di 100ms — il freeze non è risolto.");
    }

    [Fact]
    public void Esegui_OperazioneCheLanciaEccezione_RitornaFallback_NonPropaga()
    {
        var risultato = IoTimeoutGuard.Esegui<string?>(() => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5), "fallback");
        Assert.Equal("fallback", risultato);
    }

    [Fact]
    public void Esegui_SenzaFallbackEsplicito_RitornaDefaultDelTipo()
    {
        var risultato = IoTimeoutGuard.Esegui(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return "x";
        }, TimeSpan.FromMilliseconds(50));

        Assert.Null(risultato);
    }

    // P2.2: Esegui riduce completamento, scadenza ed errore a un unico fallback. Il chiamante che deve decidere
    // (per esempio se un lock è libero o soltanto illeggibile) deve poter distinguere i tre casi.
    [Fact]
    public void EseguiConEsito_OperazioneVeloce_RestituisceOkConIlValore()
    {
        var risultato = IoTimeoutGuard.EseguiConEsito(() => 42, TimeSpan.FromSeconds(5));

        Assert.Equal(EsitoIo.Ok, risultato.Esito);
        Assert.Equal(42, risultato.Valore);
        Assert.Null(risultato.Eccezione);
    }

    [Fact]
    public void EseguiConEsito_OperazioneCheImpiegaTroppo_RestituisceTimeoutNonErrore()
    {
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        var risultato = IoTimeoutGuard.EseguiConEsito(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return "x";
        }, TimeSpan.FromMilliseconds(100));

        cronometro.Stop();
        Assert.Equal(EsitoIo.Timeout, risultato.Esito);
        Assert.Null(risultato.Valore);
        Assert.Null(risultato.Eccezione);
        Assert.True(cronometro.ElapsedMilliseconds < 2000, $"Il chiamante è rimasto bloccato {cronometro.ElapsedMilliseconds}ms oltre il tetto di 100ms.");
    }

    [Fact]
    public void EseguiConEsito_OperazioneInErrore_RestituisceErroreConLaCausaOriginale()
    {
        var risultato = IoTimeoutGuard.EseguiConEsito<string?>(() => throw new IOException("violazione di condivisione"), TimeSpan.FromSeconds(5));

        Assert.Equal(EsitoIo.Errore, risultato.Esito);
        Assert.Null(risultato.Valore);
        var causa = Assert.IsType<IOException>(risultato.Eccezione);
        Assert.Equal("violazione di condivisione", causa.Message);
    }
}
