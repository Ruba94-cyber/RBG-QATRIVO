using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class KpiServiceTests
{
    private static TestCase ConEsiti(params Esito[] esiti)
    {
        var tc = new TestCase { Id = Guid.NewGuid().ToString() };
        var i = 1;
        foreach (var e in esiti)
            tc.Esecuzioni.Add(new Esecuzione { Iterazione = i++, Esito = e, Matricola = "Matricola", Condizioni = [new Condizione { Esito = e }] });
        return tc;
    }

    [Fact]
    public void IterazioniPerConforme_RitornaPosizioneDellaPrimaConforme()
    {
        var tc = ConEsiti(Esito.NonConforme, Esito.ConformeConDeviazioni, Esito.Conforme);
        Assert.Equal(3, KpiService.IterazioniPerConforme(tc));
    }

    [Fact]
    public void IterazioniPerConforme_NullSeNonRaggiuntaMai()
    {
        var tc = ConEsiti(Esito.NonConforme, Esito.NonEseguitoNA);
        Assert.Null(KpiService.IterazioniPerConforme(tc));
    }

    [Fact]
    public void IterazioniPerConforme_NullSeNessunaEsecuzione()
    {
        var tc = new TestCase { Id = "X" };
        Assert.Null(KpiService.IterazioniPerConforme(tc));
    }

    [Fact]
    public void CalcolaAggregato_EscludeIMaiRaggiuntiDallaMedia()
    {
        // due casi raggiungono Conforme in 1 e 3 iterazioni (media 2), un terzo non lo raggiunge mai
        var casi = new[]
        {
            ConEsiti(Esito.Conforme),
            ConEsiti(Esito.NonConforme, Esito.ConformeConDeviazioni, Esito.Conforme),
            ConEsiti(Esito.NonConforme, Esito.NonConforme),
        };

        var kpi = KpiService.CalcolaAggregato(casi);

        Assert.Equal(2.0, kpi.MediaIterazioni);
        Assert.Equal(2, kpi.CasiRaggiuntiConforme);
        Assert.Equal(1, kpi.CasiMaiRaggiuntiConforme);
    }

    [Fact]
    public void CalcolaAggregato_IgnoraTestCaseSenzaEsecuzioni()
    {
        var casi = new[] { new TestCase { Id = "SENZA-ESECUZIONI" } };
        var kpi = KpiService.CalcolaAggregato(casi);

        Assert.Equal(0, kpi.CasiRaggiuntiConforme);
        Assert.Equal(0, kpi.CasiMaiRaggiuntiConforme);
        Assert.Equal(0, kpi.MediaIterazioni);
    }

    [Fact]
    public void PercentualeMaiRaggiunti_CalcolataSulTotaleConcluso()
    {
        var kpi = new KpiIterazioniConforme { CasiRaggiuntiConforme = 3, CasiMaiRaggiuntiConforme = 1 };
        Assert.Equal(25.0, kpi.PercentualeMaiRaggiunti);
    }

    [Fact]
    public void PercentualeMaiRaggiunti_ZeroSeNessunCasoConcluso()
    {
        var kpi = new KpiIterazioniConforme();
        Assert.Equal(0, kpi.PercentualeMaiRaggiunti);
    }
}
