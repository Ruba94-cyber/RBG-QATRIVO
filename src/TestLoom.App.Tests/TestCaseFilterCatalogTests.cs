using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class TestCaseFilterCatalogTests
{
    [Fact]
    public void Build_PreservesLegacyComparersAndIgnoresBlankValues()
    {
        var testCases = new[]
        {
            new TestCase
            {
                Componente = ["VCM", "vcm", ""],
                Veicoli = ["Daily", "daily"],
                Assegnatario = "Mario",
                CreatoDa = "Paolo",
                RequisitoId = ["REQ-1", "req-1"],
                Funzionalita = ["Diagnosi", "diagnosi"]
            },
            new TestCase
            {
                Componente = ["BCM"],
                Veicoli = ["Eurocargo"],
                Assegnatario = "  ",
                CreatoDa = "Luca",
                RequisitoId = ["REQ-2"],
                Funzionalita = ["Guasto"]
            }
        };
        // Il repository normalizza già componente/veicoli prima di esporre i Test alla lista.
        foreach (var tc in testCases) tc.NormalizzaVeicoli();

        var result = TestCaseFilterCatalog.Build(testCases);

        Assert.Equal(new[] { "BCM", "VCM" }, result.Componente);
        Assert.Equal(new[] { "Daily", "Eurocargo" }, result.Veicoli);
        Assert.Equal(new[] { "Mario" }, result.Assegnatari);
        Assert.Equal(new[] { "Luca", "Paolo" }, result.Creatori);
        Assert.Contains("REQ-1", result.Requisiti);
        Assert.Contains("req-1", result.Requisiti); // Distinct() legacy era case-sensitive.
        Assert.Contains("Diagnosi", result.Funzionalita);
        Assert.Contains("diagnosi", result.Funzionalita);
    }
}
