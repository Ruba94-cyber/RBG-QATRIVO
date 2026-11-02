using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class PunteggioConformitaServiceTests
{
    [Theory]
    [InlineData(Esito.Conforme, PunteggioConformita.Conforme)]
    [InlineData(Esito.ConformeConDeviazioni, PunteggioConformita.ModeratamenteConforme)]
    [InlineData(Esito.NonConforme, PunteggioConformita.NonConforme)]
    [InlineData(Esito.NonApplicabile, PunteggioConformita.NonApplicabile)]
    public void DaEsito_MappaturaAutomatica(Esito esito, PunteggioConformita atteso)
    {
        Assert.Equal(atteso, PunteggioConformitaService.DaEsito(esito));
    }

    [Theory]
    [InlineData(Esito.DaRivedere)]
    [InlineData(Esito.NonEseguitoNA)]
    public void DaEsito_NessunAutomaticoPerEsitiNonConclusivi(Esito esito)
    {
        Assert.Null(PunteggioConformitaService.DaEsito(esito));
    }

    [Fact]
    public void SincronizzaPunteggi_AggiungeVociMancantiConValoreAutomatico()
    {
        var esecuzione = new Esecuzione { Esito = Esito.Conforme };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1", "R2" });

        Assert.Equal(2, esecuzione.PunteggiRequisiti.Count);
        Assert.All(esecuzione.PunteggiRequisiti, p => Assert.Equal(PunteggioConformita.Conforme, p.Valore));
    }

    [Fact]
    public void SincronizzaPunteggi_RimuoveVociPerRequisitiNonPiuCollegati()
    {
        var esecuzione = new Esecuzione { Esito = Esito.Conforme };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1", "R2" });

        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1" });

        Assert.Single(esecuzione.PunteggiRequisiti);
        Assert.Equal("R1", esecuzione.PunteggiRequisiti[0].RequisitoId);
    }

    [Fact]
    public void SincronizzaPunteggi_NonTocca_VoceSovrascrittaAnchesSeEsitoCambia()
    {
        var esecuzione = new Esecuzione { Esito = Esito.Conforme };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1" });
        esecuzione.PunteggiRequisiti[0].Sovrascritto = true;
        esecuzione.PunteggiRequisiti[0].Valore = PunteggioConformita.ParzialmenteConforme;
        esecuzione.PunteggiRequisiti[0].MotivazioneOverride = "Deviazione minore accettata.";

        esecuzione.Esito = Esito.NonConforme;
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1" });

        Assert.Equal(PunteggioConformita.ParzialmenteConforme, esecuzione.PunteggiRequisiti[0].Valore);
    }

    [Fact]
    public void SincronizzaPunteggi_RiallineaVoceNonSovrascrittaQuandoEsitoCambia()
    {
        var esecuzione = new Esecuzione { Esito = Esito.Conforme };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1" });

        esecuzione.Esito = Esito.NonConforme;
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1" });

        Assert.Equal(PunteggioConformita.NonConforme, esecuzione.PunteggiRequisiti[0].Valore);
    }

    [Theory]
    [InlineData(Esito.DaRivedere)]
    [InlineData(Esito.NonEseguitoNA)]
    public void SincronizzaPunteggi_EsitoTornatoSenzaAutomatico_RimuoveLaVoceAutomaticaNonSovrascritta(Esito esitoSenzaAutomatico)
    {
        var esecuzione = new Esecuzione { Esito = Esito.Conforme };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1" });
        Assert.Equal(PunteggioConformita.Conforme, esecuzione.PunteggiRequisiti[0].Valore);

        // Senza punteggio automatico il Conforme dell'esito precedente non deve restare come voce automatica.
        esecuzione.Esito = esitoSenzaAutomatico;
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1" });

        Assert.Empty(esecuzione.PunteggiRequisiti);
    }

    [Fact]
    public void SincronizzaPunteggi_EsitoTornatoNonEseguito_NonTocaLaVoceSovrascritta()
    {
        var esecuzione = new Esecuzione { Esito = Esito.NonConforme };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1" });
        esecuzione.PunteggiRequisiti[0].Sovrascritto = true;
        esecuzione.PunteggiRequisiti[0].Valore = PunteggioConformita.ParzialmenteConforme;
        esecuzione.PunteggiRequisiti[0].MotivazioneOverride = "Deviazione minore accettata.";

        esecuzione.Esito = Esito.NonEseguitoNA;
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1" });

        Assert.Equal(PunteggioConformita.ParzialmenteConforme, Assert.Single(esecuzione.PunteggiRequisiti).Valore);
    }

    [Fact]
    public void SincronizzaPunteggi_EsitoSenzaAutomatico_NonCreaVoceFincheNonInseritaAMano()
    {
        var esecuzione = new Esecuzione { Esito = Esito.DaRivedere };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, new[] { "R1" });

        Assert.Empty(esecuzione.PunteggiRequisiti);
    }
}
