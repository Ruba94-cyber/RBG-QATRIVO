using TestLoom.App.Models;
using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>
/// Guardrail dello stamp riusato dall'indice Test (<see cref="TestCaseFingerprint.StampDaIndice"/>): lo hash memorizzato
/// nella riga si riusa come stamp solo con marcatore dell'algoritmo attuale e forma SHA-256 valida. In ogni altro caso
/// lo stamp si ricalcola. Il valore "finto" e' ben formato ma diverso dal calcolo reale: se il riuso avviene, lo stamp
/// assegnato e' esattamente quello memorizzato; se il guardrail scatta, e' il ricalcolo.
/// </summary>
public sealed class TestCaseFingerprintRiusoIndiceTests
{
    private const string HashMemorizzatoFinto = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void MarcatoreAttualeEFormaValida_RiusaLoHashMemorizzato()
    {
        var tc = NuovoTest();

        var stamp = TestCaseFingerprint.StampDaIndice(tc, HashMemorizzatoFinto, TestCaseFingerprint.VersioneAlgoritmo);

        Assert.Equal(HashMemorizzatoFinto, stamp);
        Assert.Equal(HashMemorizzatoFinto, TestCaseFingerprint.GetStored(tc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256-v0")]
    public void MarcatoreAssenteOAltroAlgoritmo_RicalcolaLoStamp(string? marcatore)
    {
        var tc = NuovoTest();

        var stamp = TestCaseFingerprint.StampDaIndice(tc, HashMemorizzatoFinto, marcatore);

        Assert.NotEqual(HashMemorizzatoFinto, stamp);
        Assert.Equal(TestCaseFingerprint.Compute(tc), stamp);
        Assert.Equal(stamp, TestCaseFingerprint.GetStored(tc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NON-ESADECIMALE")]
    [InlineData("ABCDEF")]
    public void HashMancanteOFormaNonValida_RicalcolaLoStamp(string? hash)
    {
        var tc = NuovoTest();

        var stamp = TestCaseFingerprint.StampDaIndice(tc, hash, TestCaseFingerprint.VersioneAlgoritmo);

        Assert.Equal(TestCaseFingerprint.Compute(tc), stamp);
        Assert.Equal(stamp, TestCaseFingerprint.GetStored(tc));
    }

    private static TestCase NuovoTest() => new()
    {
        Uid = Guid.Parse("6f1d2a4b-3c5e-4f70-8a91-b2c3d4e5f607"),
        Id = "TC-STAMP-RIUSO",
        RevisioneDati = 1,
        Componente = new EcuSelection { "Componente-STAMP" },
        Veicoli = ["Daily"],
        Veicolo = "Daily",
        DescrizioneBreve = "stamp riuso indice",
    };
}
