using TestLoom.App.Models;

namespace TestLoom.App.Tests;

/// <summary>Regola guasto: la devalidazione e' applicabile solo se la validazione dello stesso guasto e' Conforme
/// (o Conforme con deviazioni); una devalidazione gia' eseguita non viene mai nascosta.</summary>
public class DtcDevalidazioneRegolaTests
{
    private static Condizione Op(PassoDtc passo, Esito esito, string guasto = "P0001", string componente = "ECM") => new()
    {
        Guasto = guasto, DtcPasso = passo, Esito = esito, DtcRequisitoId = "REQ-1", Componente = new EcuSelection(new[] { componente }),
    };

    [Theory]
    [InlineData(Esito.DaEseguire, true)]
    [InlineData(Esito.NonConforme, true)]
    [InlineData(Esito.NonApplicabile, true)]
    [InlineData(Esito.Conforme, false)]
    [InlineData(Esito.ConformeConDeviazioni, false)]
    public void DevalidazioneNascostaSoloSeValidazioneNonConforme(Esito validazione, bool nascostaAttesa)
    {
        var val = Op(PassoDtc.Validazione, validazione);
        var dev = Op(PassoDtc.Devalidazione, Esito.DaEseguire);
        var rev = new Esecuzione { Condizioni = new() { val, dev } };
        rev.AggiornaDevalidazioniNascoste();
        Assert.Equal(nascostaAttesa, dev.DevalidazioneNascosta);
        Assert.False(val.DevalidazioneNascosta);
    }

    [Fact]
    public void DevalidazioneGiaEseguitaNonVieneMaiNascosta()
    {
        var val = Op(PassoDtc.Validazione, Esito.NonConforme);
        var dev = Op(PassoDtc.Devalidazione, Esito.NonConforme);
        var rev = new Esecuzione { Condizioni = new() { val, dev } };
        rev.AggiornaDevalidazioniNascoste();
        Assert.False(dev.DevalidazioneNascosta);
    }

    [Fact]
    public void LaRegolaValePerSingoloDtc()
    {
        var valA = Op(PassoDtc.Validazione, Esito.Conforme, "P0001");
        var devA = Op(PassoDtc.Devalidazione, Esito.DaEseguire, "P0001");
        var valB = Op(PassoDtc.Validazione, Esito.NonConforme, "P0002");
        var devB = Op(PassoDtc.Devalidazione, Esito.DaEseguire, "P0002");
        var rev = new Esecuzione { Condizioni = new() { valA, devA, valB, devB } };
        rev.AggiornaDevalidazioniNascoste();
        Assert.False(devA.DevalidazioneNascosta);
        Assert.True(devB.DevalidazioneNascosta);
    }

    [Fact]
    public void RiapparireQuandoLaValidazioneTornaConforme()
    {
        var val = Op(PassoDtc.Validazione, Esito.NonConforme);
        var dev = Op(PassoDtc.Devalidazione, Esito.DaEseguire);
        var rev = new Esecuzione { Condizioni = new() { val, dev } };
        rev.AggiornaDevalidazioniNascoste();
        Assert.True(dev.DevalidazioneNascosta);
        val.Esito = Esito.Conforme;
        rev.AggiornaDevalidazioniNascoste();
        Assert.False(dev.DevalidazioneNascosta);
    }

    [Fact]
    public void DevalidazioneFacoltativaInseribileMaMaiRichiesta()
    {
        var val = Op(PassoDtc.Validazione, Esito.NonConforme);
        var dev = Op(PassoDtc.Devalidazione, Esito.DaEseguire);
        var rev = new Esecuzione { Condizioni = new() { val, dev } };
        rev.AggiornaDevalidazioniNascoste();
        Assert.Same(dev, val.DevalidazioneInseribile);
        dev.DevalidazioneForzata = true;
        rev.AggiornaDevalidazioniNascoste();
        Assert.False(dev.DevalidazioneNascosta);
        Assert.True(dev.DevalidazioneNonRichiesta);   // non blocca "Concluso"
        Assert.Null(val.DevalidazioneInseribile);
    }
}
