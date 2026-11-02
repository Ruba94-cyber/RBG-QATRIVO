using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class CondivisioneHealthServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_health_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CartellaLocaleScrivibile_RisultaOperativa()
    {
        Directory.CreateDirectory(_root);
        var esito = CondivisioneHealthService.Verifica(_root);
        Assert.Equal(StatoCondivisione.Operativa, esito.Stato);
    }

    [Fact]
    public void CartellaInesistente_RisultaNonDisponibile()
    {
        var esito = CondivisioneHealthService.Verifica(Path.Combine(_root, "non-esiste"));
        Assert.Equal(StatoCondivisione.NonDisponibile, esito.Stato);
    }

    [Fact]
    public void CartellaVuotaONulla_RisultaNonDisponibile()
    {
        Assert.Equal(StatoCondivisione.NonDisponibile, CondivisioneHealthService.Verifica(null).Stato);
        Assert.Equal(StatoCondivisione.NonDisponibile, CondivisioneHealthService.Verifica("").Stato);
    }

    [Fact]
    public void CartellaRaggiungibileMaProbeNonScrivibile_RisultaSolaLettura()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".testloom-write-probe"));

        var esito = CondivisioneHealthService.Verifica(_root);

        Assert.Equal(StatoCondivisione.SolaLettura, esito.Stato);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void RefreshPeriodico_GiaOperativo_NonEsegueProbeMutante()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        Assert.Equal(StatoCondivisione.Operativa, state.StatoCondivisione.Stato);

        // Un DIRECTORY con lo stesso nome del probe rende impossibile CondivisioneHealthService.Verifica(),
        // ma non influenza il preflight non mutante. Se RileggiDaDisco provasse ancora create/delete ogni
        // minuto, questo refresh degraderebbe lo stato e toccherebbe il path tecnico.
        var probe = Path.Combine(_root, ".testloom-write-probe");
        Directory.CreateDirectory(probe);

        state.RileggiDaDisco();

        Assert.Equal(StatoCondivisione.Operativa, state.StatoCondivisione.Stato);
        Assert.True(Directory.Exists(probe));
    }

    [Fact]
    public void RecoveryDaSolaLettura_RichiedeNuovoProbeFortePrimaDiRiabilitareScritture()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        Assert.Equal(StatoCondivisione.Operativa, state.StatoCondivisione.Stato);

        var manifestPath = state.Paths.VersionManifestFile;
        var originale = JsonIo.TryRead<VersionManifest>(manifestPath, out var errore)
            ?? throw new InvalidDataException(errore ?? "Manifest versione non leggibile nel test health.");
        var futuro = new VersionManifest
        {
            ManifestSchema = originale.ManifestSchema,
            VersioneCorrente = originale.VersioneCorrente,
            VersioneMinima = "999.0.0",
            SchemaDatiMassimo = originale.SchemaDatiMassimo,
            PacchettoRelativo = originale.PacchettoRelativo,
            PubblicatoIl = originale.PubblicatoIl
        };
        JsonIo.WriteAtomic(manifestPath, futuro);

        state.RileggiDaDisco();
        Assert.Equal(StatoCondivisione.SolaLettura, state.StatoCondivisione.Stato);

        JsonIo.WriteAtomic(manifestPath, originale);
        var probe = Path.Combine(_root, ".testloom-write-probe");
        Directory.CreateDirectory(probe); // forza il fallimento della prova reale di scrittura.

        state.RileggiDaDisco();

        Assert.NotEqual(StatoCondivisione.Operativa, state.StatoCondivisione.Stato);
        Assert.True(Directory.Exists(probe));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
