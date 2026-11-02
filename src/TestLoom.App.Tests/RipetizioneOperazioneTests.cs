using TestLoom.App.Models;

namespace TestLoom.App.Tests;

public class RipetizioneOperazioneTests
{
    [Fact]
    public void UltimoTentativoConforme_SuperaIlPrecedenteNonConforme()
    {
        var origine = new Condizione { Uid = Guid.NewGuid(), Esito = Esito.NonConforme };
        var ripetizione = new Condizione
        {
            Uid = Guid.NewGuid(), OperazioneOrigineUid = origine.Uid, Esito = Esito.Conforme
        };
        var esecuzione = new Esecuzione { Condizioni = new List<Condizione> { origine, ripetizione } };

        Assert.Equal(Esito.Conforme, esecuzione.EsitoCalcolato);
        Assert.Equal(ripetizione.Uid, Assert.Single(esecuzione.CondizioniEffettive).Uid);
    }

    [Fact]
    public void NuovaOperazioneNegativa_RestaIndipendenteDaUnaRipetizioneConforme()
    {
        var origine = new Condizione { Uid = Guid.NewGuid(), Esito = Esito.NonConforme };
        var ripetizione = new Condizione
        {
            Uid = Guid.NewGuid(), OperazioneOrigineUid = origine.Uid, Esito = Esito.Conforme
        };
        var altra = new Condizione { Uid = Guid.NewGuid(), Esito = Esito.NonConforme };
        var esecuzione = new Esecuzione { Condizioni = new List<Condizione> { origine, ripetizione, altra } };

        Assert.Equal(Esito.NonConforme, esecuzione.EsitoCalcolato);
        Assert.Equal(2, esecuzione.CondizioniEffettive.Count);
    }
}
