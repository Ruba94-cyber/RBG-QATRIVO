using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class ReportMatricePercentualiConformitaTests
{
    private static readonly ConfigurazioneMatriceReport Configurazione = new()
    {
        CampiX = [DimensioniMatrice.Componente],
        CampiY = [DimensioniMatrice.Funzionalita],
    };

    private static TestCase CreaTest(string id, string veicolo, Esito esito, OrigineTest origine = OrigineTest.TestLoom,
        params Esito[] revisioni)
    {
        var test = new TestCase
        {
            Id = id,
            Componente = new EcuSelection(["BCM"]),
            Veicoli = [veicolo],
            Funzionalita = ["F"],
            Origine = origine,
        };
        var outcomes = revisioni.Length == 0 ? [esito] : revisioni;
        for (var i = 0; i < outcomes.Length; i++)
        {
            test.Esecuzioni.Add(new Esecuzione
            {
                Data = new DateTime(2026, 1, 1).AddDays(i),
                Esito = outcomes[i],
                Condizioni = [new Condizione
                {
                    Funzionalita = "F",
                    Esito = outcomes[i],
                    Componente = new EcuSelection(["BCM"]),
                }],
            });
        }
        return test;
    }

    [Fact]
    public void StatoPercentuale_ManualiInclusiCatalogoNonTestatoSulTotale()
    {
        var testRecente = CreaTest("T-1", "V1", Esito.Conforme);
        var testImportato = CreaTest("U-1", "V2", Esito.Conforme, OrigineTest.ImportStatoUdt);
        var manuali = new[]
        {
            new StatoFunzionalitaEcuVeicolo
            {
                Funzionalita = "F", Componente = "BCM", Veicolo = "V1", Esito = Esito.NonConforme,
                Operatore = "Master", Nota = "manuale su F",
                DataDecisione = new DateTime(2026, 1, 5),
            },
            new StatoFunzionalitaEcuVeicolo
            {
                Funzionalita = "F", Componente = "BCM", Veicolo = "V3", Esito = Esito.NonConforme,
                Operatore = "Master", Nota = "solo manuale",
                DataDecisione = new DateTime(2026, 1, 5),
            },
        };
        var veicoli = Enumerable.Range(1, 4).Select(i => new Veicolo { Nome = $"V{i}" }).ToArray();
        var funzionalita = new[] { new Funzionalita { Nome = "F", TutteLeEcu = true } };
        var componente = new[]
        {
            new Componente { Nome = "BCM", VeicoliAssociati = ["V1", "V2", "V3"] },
            new Componente { Nome = "TCU", VeicoliAssociati = ["V4"] },
        };

        var report = ReportMatriceService.Genera([testRecente, testImportato], Configurazione,
            veicoli, funzionalita, [], ecuCatalogo: componente, manuali: manuali);
        var riga = Assert.Single(report.Righe);
        var cella = Assert.Single(riga.Celle, c => c.Colonna == "BCM");
        var totale = report.TotaleGenerale.StatiPercentuale;

        Assert.Equal(3, cella.StatiPercentuale.Totale);
        Assert.Equal(1, cella.StatiPercentuale.Ok);
        Assert.Equal(2, cella.StatiPercentuale.NonConforme);
        Assert.Equal(new StatiMatrice(1, 2, 0, 0, 1, 0), totale);
        Assert.Contains("25%", report.TotaleGenerale.Testo);
        Assert.Contains("50%", report.TotaleGenerale.Testo);
    }

    [Fact]
    public void TestPercentuale_ContaTestCaseCorrenteUnaVolta_EscludeImportatiEManuali()
    {
        var testConRevisioni = CreaTest("T-1", "V1", Esito.Conforme, revisioni: [Esito.NonConforme, Esito.Conforme]);
        var testNok = CreaTest("T-2", "V1", Esito.NonConforme);
        var snapshotUdt = CreaTest("U-1", "V1", Esito.Conforme, OrigineTest.ImportStatoUdt);
        var manuali = new[]
        {
            new StatoFunzionalitaEcuVeicolo
            {
                Funzionalita = "F", Componente = "BCM", Veicolo = "V1", Esito = Esito.Conforme,
                Operatore = "Master", Nota = "manuale", DataDecisione = new DateTime(2026, 1, 5),
            },
            new StatoFunzionalitaEcuVeicolo
            {
                Funzionalita = "F", Componente = "BCM", Veicolo = "V2", Esito = Esito.Conforme,
                Operatore = "Master", Nota = "solo manuale", DataDecisione = new DateTime(2026, 1, 5),
            },
        };
        var configurazione = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.Funzionalita],
            ModalitaCella = ModalitaCellaMatrice.TestPercentuale,
        };

        var report = ReportMatriceService.Genera([testConRevisioni, testNok, snapshotUdt],
            configurazione, [], [], [], manuali: manuali);
        var cella = Assert.Single(report.Righe).Celle.Single();

        Assert.Equal(2, cella.Totali);
        Assert.Equal(1, cella.StatiPercentuale.Ok);
        Assert.Equal(1, cella.StatiPercentuale.NonConforme);
        Assert.Equal(0, cella.StatiPercentuale.NonTestato);
        Assert.Contains("(2 test)", cella.Testo);
        Assert.Equal(2, report.TotaleGenerale.StatiPercentuale.Totale);
        Assert.Equal(1, report.TotaleGenerale.StatiPercentuale.Ok);
        Assert.Equal(1, report.TotaleGenerale.StatiPercentuale.NonConforme);
        Assert.Contains("(2 test)", report.TotaleGenerale.Testo);
    }
}
