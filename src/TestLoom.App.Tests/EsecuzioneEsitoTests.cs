using TestLoom.App.Models;

namespace TestLoom.App.Tests;

public sealed class EsecuzioneEsitoTests
{
    [Fact]
    public void NessunaCondizione_EsitoCalcolatoEDaEseguire()
    {
        var esecuzione = new Esecuzione();
        Assert.Equal(Esito.DaEseguire, esecuzione.EsitoCalcolato);
    }

    [Fact]
    public void SoloCondizioniNonEseguite_EsitoCalcolatoEDaEseguire()
    {
        // Decisione utente 6.4.0, punto 1: "Da eseguire" e N.A. non sono operazioni svolte, quindi da sole
        // non possono mai rendere una revisione "Conforme" né alcun altro esito reale.
        var esecuzione = new Esecuzione
        {
            Condizioni =
            {
                new Condizione { Esito = Esito.DaEseguire },
                new Condizione { Esito = Esito.NonApplicabile }
            }
        };
        Assert.Equal(Esito.DaEseguire, esecuzione.EsitoCalcolato);
    }

    [Fact]
    public void CondizioneNonEseguitaMescolataAConforme_NonDominaSulConforme()
    {
        // Prima della 6.4.0 la "gravità" di NonEseguitoNA/DaEseguire era numericamente inferiore a Conforme:
        // qui verifichiamo che il filtro EEseguito() in EsitoCalcolato impedisca a un'operazione mai eseguita
        // di abbassare l'esito quando esiste già un tentativo realmente concluso.
        var esecuzione = new Esecuzione
        {
            Condizioni =
            {
                new Condizione { Esito = Esito.DaEseguire },
                new Condizione { Esito = Esito.Conforme }
            }
        };
        Assert.Equal(Esito.Conforme, esecuzione.EsitoCalcolato);
    }

    [Fact]
    public void UnaSolaCondizioneConforme_EsitoCalcolatoEconforme()
    {
        var esecuzione = new Esecuzione { Condizioni = { new Condizione { Esito = Esito.Conforme } } };
        Assert.Equal(Esito.Conforme, esecuzione.EsitoCalcolato);
    }

    [Fact]
    public void CondizioniMiste_LaPeggioreVince()
    {
        var esecuzione = new Esecuzione
        {
            Condizioni =
            {
                new Condizione { Esito = Esito.Conforme },
                new Condizione { Esito = Esito.NonConforme },
                new Condizione { Esito = Esito.ConformeConDeviazioni }
            }
        };
        Assert.Equal(Esito.NonConforme, esecuzione.EsitoCalcolato);
    }

    [Fact]
    public void ConformeConDeviazioni_VinceSuDaRivedereENonApplicabile()
    {
        var esecuzione = new Esecuzione
        {
            Condizioni =
            {
                new Condizione { Esito = Esito.DaRivedere },
                new Condizione { Esito = Esito.NonApplicabile },
                new Condizione { Esito = Esito.ConformeConDeviazioni }
            }
        };
        Assert.Equal(Esito.ConformeConDeviazioni, esecuzione.EsitoCalcolato);
    }

    [Fact]
    public void SincronizzaEsito_AllineaAllCalcolatoSeNonSovrascritto()
    {
        var esecuzione = new Esecuzione { Esito = Esito.Conforme, Condizioni = { new Condizione { Esito = Esito.NonConforme } } };
        esecuzione.SincronizzaEsito();
        Assert.Equal(Esito.NonConforme, esecuzione.Esito);
    }

    [Fact]
    public void SincronizzaEsito_NonToccaEsitoSeSovrascritto()
    {
        var esecuzione = new Esecuzione
        {
            Esito = Esito.ConformeConDeviazioni,
            EsitoSovrascritto = true,
            MotivazioneOverride = "Deviazione accettata dopo analisi (dato di test).",
            Condizioni = { new Condizione { Esito = Esito.NonConforme } }
        };
        esecuzione.SincronizzaEsito();
        Assert.Equal(Esito.ConformeConDeviazioni, esecuzione.Esito);
        Assert.Equal(Esito.NonConforme, esecuzione.EsitoCalcolato);
    }
}
