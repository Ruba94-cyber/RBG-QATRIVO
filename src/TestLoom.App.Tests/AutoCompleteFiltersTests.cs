using TestLoom.App.Models;

namespace TestLoom.App.Tests;

/// <summary>Predicati usati dagli AutoCompleteBox convertiti da ComboBox su componente/Veicolo/Funzionalità/
/// Assegnatario/Tipo di test/Gamma/Categoria (feedback utente 2026-08-19, punto 1 — "scrivendo dovrebbe
/// proporti le voci filtrandole").</summary>
public sealed class AutoCompleteFiltersTests
{
    [Theory]
    [InlineData("bcm", "BCM Motore", true)]
    [InlineData("BCM", "bcm motore", true)]
    [InlineData("xyz", "BCM Motore", false)]
    public void Stringa_FiltraPerSottostringaSenzaDistinguereMaiuscole(string testo, string voce, bool atteso) =>
        Assert.Equal(atteso, AutoCompleteFilters.Stringa(testo, voce));

    [Fact]
    public void Stringa_TestoVuoto_MostraTutto() =>
        Assert.True(AutoCompleteFilters.Stringa("", "qualunque cosa"));

    [Fact]
    public void Stringa_ItemNonStringa_NonCorrisponde() =>
        Assert.False(AutoCompleteFilters.Stringa("bcm", 42));

    [Fact]
    public void TestTemplate_FiltraPerNome()
    {
        var unitlogger = new TestTemplate { Nome = "UnitLogger Performance" };
        var banco = new TestTemplate { Nome = "Validazione Banco" };

        Assert.True(AutoCompleteFilters.TestTemplate("perf", unitlogger));
        Assert.False(AutoCompleteFilters.TestTemplate("perf", banco));
    }

    [Fact]
    public void TestTemplate_TestoVuoto_MostraTutto() =>
        Assert.True(AutoCompleteFilters.TestTemplate("", new TestTemplate { Nome = "UnitLogger" }));

    [Fact]
    public void Requisito_TestoVuoto_MostraTutto() =>
        Assert.True(AutoCompleteFilters.Requisito("", new Requisito { Id = "R1", Testo = "Qualcosa" }));

    [Fact]
    public void TestCorrelato_TestoVuoto_MostraTutto() =>
        Assert.True(AutoCompleteFilters.TestCorrelato("", new TestCase { Id = "TC-1" }));
}
