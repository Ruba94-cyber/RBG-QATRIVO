using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class DuplicateDetectionServiceTests
{
    private static readonly Guid TemplateBanco = Guid.NewGuid();
    private static readonly Guid TemplateVeicolo = Guid.NewGuid();

    [Fact]
    public void TrovaIdDuplicati_RilevaDuePercorsiConLoStessoId()
    {
        var file = new[]
        {
            ("C:/dati/testcase_componente-TEST-BCM-20260813-OPA-01.json", "Componente-TEST-BCM-20260813-OPA-01"),
            ("C:/dati/testcase_componente-TEST-BCM-20260813-OPA-01-PC2.json", "Componente-TEST-BCM-20260813-OPA-01"),
            ("C:/dati/testcase_componente-TEST-BCM-20260813-OPA-02.json", "Componente-TEST-BCM-20260813-OPA-02"),
        };

        var duplicati = DuplicateDetectionService.TrovaIdDuplicati(file);

        var duplicato = Assert.Single(duplicati);
        Assert.Equal("Componente-TEST-BCM-20260813-OPA-01", duplicato.Id);
        Assert.Equal(2, duplicato.Percorsi.Count);
    }

    [Fact]
    public void TrovaIdDuplicati_NessunDuplicato_RitornaListaVuota()
    {
        var file = new[]
        {
            ("a.json", "ID-1"),
            ("b.json", "ID-2"),
        };

        Assert.Empty(DuplicateDetectionService.TrovaIdDuplicati(file));
    }

    [Fact]
    public void TrovaIdDuplicati_ListaVuota_NonSolleva()
    {
        Assert.Empty(DuplicateDetectionService.TrovaIdDuplicati(Array.Empty<(string, string)>()));
    }

    private static TestCase Test(string id, string componente, Guid template, params string[] requisiti) =>
        new() { Id = id, Componente = componente, TestTemplateUid = template, RequisitoId = requisiti.ToList() };

    [Fact]
    public void TrovaCandidatiIterazione_StessoRequisitoStessaEcuStessoTipo_EProposto()
    {
        var archivio = new[] { Test("TC-1", "Componente-A", TemplateBanco, "R1") };

        var candidati = DuplicateDetectionService.TrovaCandidatiIterazione(
            archivio, "Componente-A", new[] { "R1" }, Array.Empty<string>(), TemplateBanco, "SW1", "HW1");

        Assert.Single(candidati);
    }

    [Fact]
    public void TrovaCandidatiIterazione_BaselineDiversa_RestaPropostoMaSegnalato()
    {
        var esistente = Test("TC-1", "Componente-A", TemplateBanco, "R1");
        esistente.Esecuzioni.Add(new Esecuzione { BaselineSw = "1.0", BaselineHw = "H1", Matricola = "V" });

        var candidati = DuplicateDetectionService.TrovaCandidatiIterazione(
            new[] { esistente }, "Componente-A", new[] { "R1" }, Array.Empty<string>(), TemplateBanco, "2.0", "H1");

        var candidato = Assert.Single(candidati);
        Assert.True(candidato.BaselineDiversa);
    }

    [Fact]
    public void TrovaCandidatiIterazione_SenzaRequisitoNeFunzionalita_NonPropongeNulla()
    {
        var archivio = new[] { Test("TC-1", "Componente-A", TemplateBanco, "R1") };
        var candidati = DuplicateDetectionService.TrovaCandidatiIterazione(
            archivio, "Componente-A", Array.Empty<string>(), Array.Empty<string>(), TemplateBanco, "SW", "HW");
        Assert.Empty(candidati);
    }

    [Fact]
    public void TrovaCandidatiCorrelati_SoloTipoTestDiverso()
    {
        var archivio = new[]
        {
            Test("TC-BANCO", "Componente-A", TemplateBanco, "R1"),
            Test("TC-VEICOLO", "Componente-A", TemplateVeicolo, "R1"),
        };

        var correlati = DuplicateDetectionService.TrovaCandidatiCorrelati(archivio, "Componente-A", new[] { "R1" }, Array.Empty<string>(), TemplateBanco);

        var c = Assert.Single(correlati);
        Assert.Equal("TC-VEICOLO", c.Id);
    }
}
