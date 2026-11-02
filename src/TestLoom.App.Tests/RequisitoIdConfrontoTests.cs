using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Gli ID requisito si confrontano senza maiuscole/minuscole in copertura, punteggi e Report: un Test e il Master
/// possono riportare la stessa sigla con grafia diversa. Il Report raggruppa gli assegnatari nello stesso modo.</summary>
public class RequisitoIdConfrontoTests
{
    [Fact]
    public void Confronto_IgnoraLeMaiuscoleMaDistingueLeSigle()
    {
        Assert.True(RequisitoIdConfronto.Uguali("req-01", "REQ-01"));
        Assert.False(RequisitoIdConfronto.Uguali("REQ-01", "REQ-02"));
        Assert.False(RequisitoIdConfronto.Uguali(null, "REQ-01"));
        Assert.True(RequisitoIdConfronto.Contiene(new[] { "req-01", "ST12" }, "REQ-01"));
    }

    [Fact]
    public void PunteggioCorrentePerRequisito_TestConIdInMinuscolo_RiportaIlPunteggioDelMaster()
    {
        var tc = new TestCase { Id = "A", RequisitoId = { "r1" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 6, 1), Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.Conforme } } });

        Assert.Equal(PunteggioConformita.Conforme, CoverageService.PunteggioCorrentePerRequisito("R1", new[] { tc }));
    }

    [Fact]
    public void RaggruppaPerAssegnatario_StessaPersonaConGrafiaDiversa_UnSoloGruppo()
    {
        var tc1 = new TestCase { Id = "A", Assegnatario = "Mario" };
        var tc2 = new TestCase { Id = "B", Assegnatario = "mario" };
        var tc3 = new TestCase { Id = "C", Assegnatario = "Luca" };
        var tc4 = new TestCase { Id = "D", Assegnatario = " " };

        var gruppi = ReportService.RaggruppaPerAssegnatario(new[] { tc1, tc2, tc3, tc4 });

        Assert.Equal(2, gruppi.Count);
        Assert.Equal("Luca", gruppi[0].Key);
        Assert.Equal(new[] { "A", "B" }, gruppi[1].Select(t => t.Id));
    }
}
