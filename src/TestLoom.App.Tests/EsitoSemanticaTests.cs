using TestLoom.App.Models;

namespace TestLoom.App.Tests;

/// <summary>Blinda l'unica definizione di "operazione eseguita" (decisione utente 6.4.0, punto 1), da cui
/// dipendono tutti i conteggi operativi/copertura/esito aggregato: solo Conforme/NonConforme/ConformeConDeviazioni.
/// N.A., "Da eseguire" e i legacy DaRivedere/NonEseguitoNA restano fuori.</summary>
public sealed class EsitoSemanticaTests
{
    [Theory]
    [InlineData(Esito.Conforme, true)]
    [InlineData(Esito.NonConforme, true)]
    [InlineData(Esito.ConformeConDeviazioni, true)]
    [InlineData(Esito.DaEseguire, false)]
    [InlineData(Esito.NonApplicabile, false)]
    [InlineData(Esito.DaRivedere, false)]
    [InlineData(Esito.NonEseguitoNA, false)]
    public void EEseguito_RiflettePropriaSemanticaUnica(Esito esito, bool atteso)
    {
        Assert.Equal(atteso, esito.EEseguito());
    }

    [Fact]
    public void DaEseguire_EIlDefaultDellEnum()
    {
        // Una Condizione appena creata (nessun Esito impostato) deve nascere "Da eseguire" senza bisogno di
        // Esito nullable in giro per il codice: default(Esito) == Esito.DaEseguire è la garanzia strutturale
        // su cui si basa TestCaseEditorViewModel e tutto il resto.
        Assert.Equal(Esito.DaEseguire, default(Esito));
        Assert.Equal(Esito.DaEseguire, new Condizione().Esito);
    }

    [Fact]
    public void Label_DaEseguire_NonEVuotaEDiversaDaiLegacy()
    {
        var label = Esito.DaEseguire.Label();
        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.NotEqual(Esito.NonEseguitoNA.Label(), label);
    }
}
