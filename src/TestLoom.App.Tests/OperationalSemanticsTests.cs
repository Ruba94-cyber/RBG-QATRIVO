using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// OperationalSemantics non ha ancora un consumer applicativo (nessuna Vista/ViewModel/report lo richiama
/// oggi), ma la regola di conteggio che implementa è già definita e deve restare corretta prima che qualcosa
/// la usi. Blinda qui il contratto: un'operazione mai eseguita vale 0, un'operazione ambigua (nessuna componente
/// dichiarata su un Test multi-componente) vale 0 e non inventa un tentativo, e il contatore cresce di una unità per
/// ogni componente realmente coinvolta a ogni revisione che contiene un'esecuzione o una ripetizione reale.
/// </summary>
public sealed class OperationalSemanticsTests
{
    [Fact]
    public void NessunaCondizione_OperazioneNonEseguita_ContaZero()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM" });
        testCase.Esecuzioni.Add(new Esecuzione { Iterazione = 1 });

        Assert.Equal(0, OperationalSemantics.CountAttemptUnits(testCase));
        Assert.Equal(0, OperationalSemantics.CountEffectiveUnits(testCase));
    }

    [Fact]
    public void NessunaEsecuzione_ContaZero()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM" });

        Assert.Equal(0, OperationalSemantics.CountAttemptUnits(testCase));
        Assert.Equal(0, OperationalSemantics.CountEffectiveUnits(testCase));
    }

    [Fact]
    public void EcuAmbigua_TestMultiEcuSenzaTagSullaCondizione_NonInventaUnTentativo()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM", "ECM" });
        testCase.Esecuzioni.Add(EsecuzioneCon(CondizioneSenzaEcu()));

        Assert.Equal(0, OperationalSemantics.CountAttemptUnits(testCase));
        Assert.Equal(0, OperationalSemantics.CountEffectiveUnits(testCase));
    }

    [Fact]
    public void EcuInferita_TestConUnaSolaEcu_ContaUno()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM" });
        testCase.Esecuzioni.Add(EsecuzioneCon(CondizioneSenzaEcu()));

        Assert.Equal(1, OperationalSemantics.CountAttemptUnits(testCase));
        Assert.Equal(1, OperationalSemantics.CountEffectiveUnits(testCase));
    }

    [Fact]
    public void CondizioneSuDueEcu_ContaDueUnita()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM", "ECM" });
        testCase.Esecuzioni.Add(EsecuzioneCon(CondizioneCon(new[] { "BCM", "ECM" })));

        Assert.Equal(2, OperationalSemantics.CountAttemptUnits(testCase));
        Assert.Equal(2, OperationalSemantics.CountEffectiveUnits(testCase));
    }

    [Fact]
    public void DueRevisioni_OgnunaConUnaOperazioneReale_IlContatoreSomma()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM" });
        testCase.Esecuzioni.Add(EsecuzioneCon(CondizioneCon(new[] { "BCM" })));
        testCase.Esecuzioni.Add(EsecuzioneCon(CondizioneCon(new[] { "BCM" })));

        Assert.Equal(2, OperationalSemantics.CountAttemptUnits(testCase));
        Assert.Equal(2, OperationalSemantics.CountEffectiveUnits(testCase));
    }

    [Fact]
    public void RipetizioneNellaStessaRevisione_TentativiContaEntrambiEffettivoContaSoloLUltimo()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM" });
        var primoTentativo = CondizioneCon(new[] { "BCM" });
        var ripetizione = CondizioneCon(new[] { "BCM" });
        ripetizione.OperazioneOrigineUid = primoTentativo.Uid;
        testCase.Esecuzioni.Add(EsecuzioneCon(primoTentativo, ripetizione));

        // Due tentativi distinti della stessa operazione: il conteggio "tentativi" li vede entrambi...
        Assert.Equal(2, OperationalSemantics.CountAttemptUnits(testCase));
        // ...ma solo l'ultimo (CondizioniEffettive) determina il lavoro "effettivo" per quell'operazione.
        Assert.Equal(1, OperationalSemantics.CountEffectiveUnits(testCase));
    }

    [Fact]
    public void OperazioneDaEseguire_NonContaAncheConEcuValorizzata()
    {
        // Decisione utente 6.4.0, punto 1: un'operazione conta solo se realmente eseguita
        // (Esito.EEseguito()) — non basta avere un componente valorizzata.
        var testCase = TestCaseCon(componente: new[] { "BCM" });
        testCase.Esecuzioni.Add(EsecuzioneCon(new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Diagnosi", Esito = Esito.DaEseguire }));

        Assert.Equal(0, OperationalSemantics.CountAttemptUnits(testCase));
        Assert.Equal(0, OperationalSemantics.CountEffectiveUnits(testCase));
    }

    [Fact]
    public void OperazioneNonApplicabile_NonConta()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM" });
        testCase.Esecuzioni.Add(EsecuzioneCon(new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Diagnosi", Esito = Esito.NonApplicabile }));

        Assert.Equal(0, OperationalSemantics.CountAttemptUnits(testCase));
        Assert.Equal(0, OperationalSemantics.CountEffectiveUnits(testCase));
    }

    [Fact]
    public void MixDiOperazioniEseguiteENonEseguite_ContaSoloQuelleEseguite()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM", "ECM" });
        var eseguita = CondizioneCon(new[] { "BCM" });
        var nonEseguita = new Condizione { Componente = new EcuSelection(new[] { "ECM" }), Funzionalita = "Diagnosi", Esito = Esito.DaEseguire };
        testCase.Esecuzioni.Add(EsecuzioneCon(eseguita, nonEseguita));

        Assert.Equal(1, OperationalSemantics.CountAttemptUnits(testCase));
        Assert.Equal(1, OperationalSemantics.CountEffectiveUnits(testCase));
    }

    [Fact]
    public void EnumerateAttemptUnits_SaltaLeOperazioniNonEseguite()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM" });
        var eseguita = CondizioneCon(new[] { "BCM" });
        var nonEseguita = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Diagnosi", Esito = Esito.DaEseguire };
        testCase.Esecuzioni.Add(EsecuzioneCon(eseguita, nonEseguita));

        var unita = OperationalSemantics.EnumerateAttemptUnits(testCase).ToList();

        var solo = Assert.Single(unita);
        Assert.Equal(eseguita.Uid, solo.ConditionUid);
    }

    [Fact]
    public void EnumerateAttemptUnits_MarcaEffettivoSoloLUltimoTentativoDellaCatena()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM" });
        var primoTentativo = CondizioneCon(new[] { "BCM" });
        var ripetizione = CondizioneCon(new[] { "BCM" });
        ripetizione.OperazioneOrigineUid = primoTentativo.Uid;
        testCase.Esecuzioni.Add(EsecuzioneCon(primoTentativo, ripetizione));

        var unita = OperationalSemantics.EnumerateAttemptUnits(testCase).ToList();

        Assert.Equal(2, unita.Count);
        Assert.False(unita[0].IsEffective);
        Assert.True(unita[1].IsEffective);
        Assert.All(unita, u => Assert.Equal(primoTentativo.Uid, u.OperationRootUid));
    }

    private static TestCase TestCaseCon(string[] componente) => new()
    {
        Id = "TC-OPSEM",
        Componente = new EcuSelection(componente),
        Veicolo = "Daily",
        Funzionalita = new List<string> { "Diagnosi" },
        Descrizione = "Base",
        Esecuzioni = new List<Esecuzione>()
    };

    private static Esecuzione EsecuzioneCon(params Condizione[] condizioni) => new()
    {
        Iterazione = 1,
        Operatore = "Tester",
        Data = new DateTime(2026, 9, 1),
        Condizioni = condizioni.ToList()
    };

    private static Condizione CondizioneCon(string[] componente) =>
        new() { Componente = new EcuSelection(componente), Funzionalita = "Diagnosi", Esito = Esito.Conforme };

    [Fact]
    public void EcuUnits_TrimmaDeduplicaSenzaDistinzioneMaiuscoleEMantieneLOrdine()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM" });
        var condizione = new Condizione
        {
            Componente = new EcuSelection(new[] { " ESP ", "bcm", "", "ESP", "  ", "BCM", "Gateway" }),
            Funzionalita = "Diagnosi",
            Esito = Esito.Conforme,
        };

        Assert.Equal(new[] { "ESP", "bcm", "Gateway" }, OperationalSemantics.EcuUnits(testCase, condizione));
    }

    [Fact]
    public void EcuUnits_SenzaEcuSullaCondizione_UsaLaUnicaEcuDelTest()
    {
        var testCase = TestCaseCon(componente: new[] { " EDC " });

        Assert.Equal(new[] { "EDC" }, OperationalSemantics.EcuUnits(testCase, CondizioneSenzaEcu()));
    }

    [Fact]
    public void EcuUnits_SenzaEcuEMultiEcuDelTest_NonInventaNulla()
    {
        var testCase = TestCaseCon(componente: new[] { "BCM", "EDC" });

        Assert.Empty(OperationalSemantics.EcuUnits(testCase, CondizioneSenzaEcu()));
    }

    private static Condizione CondizioneSenzaEcu() =>
        new() { Componente = new EcuSelection(Array.Empty<string>()), Funzionalita = "Diagnosi", Esito = Esito.Conforme };
}
