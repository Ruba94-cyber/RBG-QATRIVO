using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class ReportFilterTests
{
    private static TestCase CasoBase() => new() { Id = "TC-1", Componente = "ECM", Veicolo = "Daily MY24", DataCreazione = new DateTime(2026, 6, 1) };

    [Fact]
    public void FiltroVuoto_CorrispondeATutto()
    {
        Assert.True(new ReportFilter().Corrisponde(CasoBase()));
    }

    [Fact]
    public void DataA_IncludeIlGiornoScelto_AncheSeIlTestHaUnOrario()
    {
        // Il selettore di data dà la mezzanotte: il test creato alle 15:30 del giorno scelto deve restare nel filtro.
        var tc = CasoBase();
        tc.DataCreazione = new DateTime(2026, 6, 1, 15, 30, 0);

        Assert.True(new ReportFilter { DataA = new DateTime(2026, 6, 1) }.Corrisponde(tc));
        Assert.False(new ReportFilter { DataA = new DateTime(2026, 5, 31) }.Corrisponde(tc));
    }

    [Fact]
    public void FiltroCampoCustom_CorrispondeSoloAiTestConValoreAmmesso()
    {
        var conValore = CasoBase();
        conValore.CampiCustom["custom-1"] = "Rosso";
        var senzaValore = CasoBase();
        senzaValore.Id = "TC-2";

        var filtro = new ReportFilter { CampiCustom = { ["custom-1"] = new List<string> { "Rosso" } } };

        Assert.True(filtro.Corrisponde(conValore));
        Assert.False(filtro.Corrisponde(senzaValore));
    }

    [Fact]
    public void FiltroCampoCustom_ListaVuota_NonFiltraSuQuellaChiave()
    {
        var filtro = new ReportFilter { CampiCustom = { ["custom-1"] = new List<string>() } };
        Assert.True(filtro.Corrisponde(CasoBase()));
    }

    [Fact]
    public void TestCaseEliminato_NonCorrispondeMai()
    {
        var tc = CasoBase();
        tc.Eliminato = true;
        Assert.False(new ReportFilter().Corrisponde(tc));
    }
}
