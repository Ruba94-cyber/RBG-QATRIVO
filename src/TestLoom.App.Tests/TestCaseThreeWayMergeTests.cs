using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class TestCaseThreeWayMergeTests
{
    [Fact]
    public void Prepara_UnisceAutomaticamenteCampiModificatiDaUnaSolaParte()
    {
        var baseVersion = Caso("base", "descrizione base");
        var mia = Caso("mia", "descrizione base");
        var loro = Caso("base", "descrizione loro");

        var merge = TestCaseThreeWayMerge.Prepara(baseVersion, mia, loro);
        var risolto = merge.CreaVersioneRisolta(new Dictionary<string, SceltaMerge>(), "Master");

        Assert.Empty(merge.Conflitti);
        Assert.Equal("mia", risolto.DescrizioneBreve);
        Assert.Equal("descrizione loro", risolto.Descrizione);
        Assert.Contains(risolto.Changelog, c => c.Campo == "merge automatico: DescrizioneBreve");
    }

    [Fact]
    public void Prepara_RichiedeSceltaMasterSoloPerStessoCampoModificatoDiversamente()
    {
        var baseVersion = Caso("base", "descrizione");
        var mia = Caso("mia", "descrizione");
        var loro = Caso("loro", "descrizione");
        var merge = TestCaseThreeWayMerge.Prepara(baseVersion, mia, loro);

        var conflitto = Assert.Single(merge.Conflitti);
        Assert.Equal("DescrizioneBreve", conflitto.Nome);
        var risolto = merge.CreaVersioneRisolta(new Dictionary<string, SceltaMerge> { [conflitto.Nome] = SceltaMerge.Loro }, "Master");

        Assert.Equal("loro", risolto.DescrizioneBreve);
        Assert.Contains(risolto.Changelog, c => c.Campo == "merge Master: DescrizioneBreve" && c.Operatore == "Master");
    }

    [Fact]
    public void CreaVersioneRisolta_RifiutaConflittoSenzaDecisione()
    {
        var baseVersion = Caso("base", "descrizione");
        var merge = TestCaseThreeWayMerge.Prepara(baseVersion, Caso("mia", "descrizione"), Caso("loro", "descrizione"));

        Assert.Throws<InvalidOperationException>(() => merge.CreaVersioneRisolta(new Dictionary<string, SceltaMerge>(), "Master"));
    }

    /// <summary>P1.10: il merge automatico di un fork non deve perdere lo storico di nessuno dei due rami.</summary>
    [Fact]
    public void CreaVersioneRisolta_UnisceIChangelogDeiDueRamiSenzaDuplicati()
    {
        var comune = new ChangelogEntry { Data = new DateTime(2026, 10, 1, 9, 0, 0), Operatore = "Master", Campo = "creazione", ValoreNuovo = "creato" };
        var baseVersion = Caso("base", "descrizione");
        baseVersion.Changelog.Add(comune);
        var mia = Caso("mia", "descrizione");
        mia.Changelog = new List<ChangelogEntry>(baseVersion.Changelog)
        {
            new() { Data = new DateTime(2026, 10, 2, 9, 0, 0), Operatore = "Operatore A", Campo = "mia modifica", ValoreNuovo = "a" }
        };
        var loro = Caso("base", "descrizione loro");
        loro.Changelog = new List<ChangelogEntry>(baseVersion.Changelog)
        {
            new() { Data = new DateTime(2026, 10, 3, 9, 0, 0), Operatore = "Operatore B", Campo = "modifica loro", ValoreNuovo = "b" }
        };

        var merge = TestCaseThreeWayMerge.Prepara(baseVersion, mia, loro);
        var risolto = merge.CreaVersioneRisolta(new Dictionary<string, SceltaMerge>(), "Master");

        Assert.Single(risolto.Changelog, c => c.Campo == "creazione");
        Assert.Single(risolto.Changelog, c => c.Campo == "mia modifica");
        Assert.Single(risolto.Changelog, c => c.Campo == "modifica loro");
    }

    private static TestCase Caso(string descrizioneBreve, string descrizione) => new()
    {
        Id = "TC-1", Uid = Guid.Parse("11111111-1111-1111-1111-111111111111"), RevisioneDati = 4,
        Componente = "BCM", DescrizioneBreve = descrizioneBreve, Descrizione = descrizione
    };
}
