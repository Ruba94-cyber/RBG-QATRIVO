using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class AnomaliaWorkflowServiceTests
{
    [Fact]
    public void TestCorrente_UsaLaProvaConDataPiuRecente_NonLaModificaAmministrativa()
    {
        var a = TestConProva("A", new DateTime(2026, 8, 10), Esito.NonConforme, out var provaA);
        var b = TestConProva("B", new DateTime(2026, 8, 15), Esito.NonConforme, out var provaB);
        a.UltimaModifica = new DateTime(2026, 8, 18);
        var nc = new NonConformita { TestCaseId = a.Id, ProveCollegate = new() { provaA, provaB } };

        Assert.Equal("B", AnomaliaWorkflowService.TestCorrente(nc, new[] { a, b })!.Id);
    }

    /// <summary>Sovraccarico su dizionario già costruito (feedback utente 2026-08-21, punto 9 — perf: la pagina
    /// Anomalie/CR ricostruiva un Dictionary da tutti i test ad ogni riga invece che una sola volta per l'intera
    /// lista): deve dare esattamente lo stesso risultato del sovraccarico IEnumerable, non solo essere più
    /// veloce da chiamare in un ciclo.</summary>
    [Fact]
    public void TestCorrente_SuDizionarioGiaCostruito_DaLoStessoRisultatoDelSovraccaricoIEnumerable()
    {
        var a = TestConProva("A", new DateTime(2026, 8, 10), Esito.NonConforme, out var provaA);
        var b = TestConProva("B", new DateTime(2026, 8, 15), Esito.NonConforme, out var provaB);
        var nc = new NonConformita { TestCaseId = a.Id, ProveCollegate = new() { provaA, provaB } };
        var dizionario = new[] { a, b }.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("B", AnomaliaWorkflowService.TestCorrente(nc, dizionario)!.Id);
    }

    [Fact]
    public void TestCorrente_SenzaProveCollegate_RichiedeIdentitaImmutabile()
    {
        var a = TestConProva("A", new DateTime(2026, 8, 10), Esito.Conforme, out _);
        var nc = new NonConformita { TestCaseId = "A", ProveCollegate = new() };
        var dizionario = new[] { a }.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        Assert.Null(AnomaliaWorkflowService.TestCorrente(nc, dizionario));
        nc.TestUid = a.Uid;
        Assert.Equal("A", AnomaliaWorkflowService.TestCorrente(nc, dizionario)!.Id);
    }

    [Fact]
    public void AnomaliaRestaAperta_FincheOgniTestCollegatoNonEConforme()
    {
        var a = TestConProva("A", new DateTime(2026, 8, 10), Esito.NonConforme, out var provaA);
        var b = TestConProva("B", new DateTime(2026, 8, 15), Esito.Conforme, out var provaB);
        var nc = new NonConformita { TestCaseId = b.Id, ProveCollegate = new() { provaA, provaB } };

        var motivi = AnomaliaWorkflowService.MotiviPerCuiRestaAperta(nc, new[] { a, b });

        Assert.Single(motivi);
        Assert.Contains("A", motivi[0]);
        Assert.False(AnomaliaWorkflowService.PuoChiudersiPerConformita(nc, new[] { a, b }));
    }

    [Fact]
    public void PolarionDelTest_IncludeEstaCollegatoAllaSuaAnomalia()
    {
        var test = new TestCase { Id = "A" };
        var nc = new NonConformita
        {
            TestCaseId = "A", TestUid = test.Uid, CollegamentiPolarion = new() { new CollegamentoPolarion { Codice = "TICKET-1" } }
        };

        Assert.True(AnomaliaWorkflowService.HaPolarion(test, new[] { nc }));
    }

    private static TestCase TestConProva(string id, DateTime data, Esito esito, out RiferimentoProva prova)
    {
        var condizione = new Condizione { Uid = Guid.NewGuid(), Esito = esito };
        var esecuzione = new Esecuzione
        {
            Iterazione = 1, Data = data, Condizioni = new() { condizione }, Esito = esito
        };
        prova = new RiferimentoProva { TestId = id, EsecuzioneIterazione = 1, CondizioneUid = condizione.Uid };
        return new TestCase { Id = id, Esecuzioni = new() { esecuzione }, UltimaModifica = data };
    }
}
