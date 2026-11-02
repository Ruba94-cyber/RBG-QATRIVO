using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class TestCasePathHierarchyTests
{
    [Fact]
    public void PercorsoGerarchico_UsaGammaGruppoSottoveicoliEGruppoEcu()
    {
        var paths = new DataPaths(Path.Combine(Path.GetTempPath(), "TestLoom_Path_Test"));
        var tc = new TestCase { Id = "T-1", Veicoli = ["Daily B", "Daily A"], Componente = new EcuSelection(["EDC", "BCM"]) };
        var veicoli = new List<Veicolo>
        {
            new() { Nome = "Daily A", GammaNome = "LCV" },
            new() { Nome = "Daily B", GammaNome = "LCV" }
        };

        var path = paths.TestCaseFileGerarchico(tc, veicoli);

        Assert.EndsWith(Path.Combine("testcases", "LCV", "Daily A + Daily B", "BCM + EDC", "testcase_T-1.json"), path);
    }
}
