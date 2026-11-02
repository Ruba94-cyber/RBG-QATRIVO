using TestLoom.App.Models;

namespace TestLoom.App.Tests;

/// <summary>
/// Normalizzazione di componente e veicoli eseguita a ogni lettura dell'archivio. Gli attesi sono calcolati a mano con la
/// semantica originale (Where non vuoto, Select Trim, Distinct OrdinalIgnoreCase, prima occorrenza conservata), cosi'
/// il confronto non dipende dal codice che verifica.
/// </summary>
public sealed class TestCaseNormalizzazioneTests
{
    [Fact]
    public void EcuSelection_Normalizza_MantienePrimaOccorrenzaSenzaVuotiNeDuplicatiPerMaiuscole()
    {
        var componente = new EcuSelection();
        componente.AddRange(new[] { "  BCM ", "bcm", "", "  ", "Componente-2", "componente-2 ", null! });

        componente.Normalizza();

        Assert.Equal(new[] { "BCM", "Componente-2" }, componente);
    }

    [Fact]
    public void TestCase_NormalizzaVeicoli_DeduplicaSenzaTrimEConservaLOrdine()
    {
        var tc = new TestCase { Veicoli = new List<string> { "Fiat ", "fiat", "", "Lancia", "LANCIA", null! } };

        tc.NormalizzaVeicoli();

        Assert.Equal(new[] { "Fiat ", "fiat", "Lancia" }, tc.Veicoli);
        Assert.Equal("Fiat ", tc.Veicolo);
    }
}
