using Avalonia.Controls;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Revisione 6.4.6: decisioni pure di cattura/applicazione delle larghezze colonna.</summary>
public class TableLayoutLarghezzeDecisioniTests
{
    [Fact]
    public void ColonnaNascosta_ConservaLaLarghezzaPrecedente_NonIlMoncone()
    {
        Assert.Equal(180, TableLayoutPreferences.LarghezzaPerSalvataggio(false, false, 0, 40, 180));
        Assert.Equal(0, TableLayoutPreferences.LarghezzaPerSalvataggio(false, false, 0, 40, 0));
        Assert.Equal(0, TableLayoutPreferences.LarghezzaPerSalvataggio(false, false, 0, 40, 12));
    }

    [Fact]
    public void ColonnaVisibile_UsaActualWidthConMinimi()
    {
        Assert.Equal(220, TableLayoutPreferences.LarghezzaPerSalvataggio(true, false, 220, 40, 100));
        Assert.Equal(160, TableLayoutPreferences.LarghezzaPerSalvataggio(true, false, 90, 160, 0));
        Assert.Equal(40, TableLayoutPreferences.LarghezzaPerSalvataggio(true, false, 10, 0, 0));
    }

    [Fact]
    public void ColonnaAutoInXaml_NonSalvaNeApplicaLarghezza()
    {
        Assert.Equal(0, TableLayoutPreferences.LarghezzaPerSalvataggio(true, true, 90, 40, 90));
        Assert.False(TableLayoutPreferences.DevoApplicareLarghezza(true, true, true, 90));
    }

    // Colonna Auto in XAML ridimensionata dall'utente (Width non piu' Auto): il chiamante passa
    // autoOriginale=false, quindi la larghezza reale viene salvata e poi riapplicata come pixel.
    [Fact]
    public void ColonnaAutoRidimensionata_SalvaLaLarghezzaReale_EPoiLaRiapplica()
    {
        var salvata = TableLayoutPreferences.LarghezzaPerSalvataggio(true, false, 140, 40, 0);
        Assert.Equal(140, salvata);
        Assert.True(TableLayoutPreferences.DevoApplicareLarghezza(true, true, false, salvata));
        // Auto mai toccata: salva 0 e non viene applicata.
        Assert.Equal(0, TableLayoutPreferences.LarghezzaPerSalvataggio(true, true, 140, 40, 0));
        Assert.False(TableLayoutPreferences.DevoApplicareLarghezza(true, true, false, 0));
    }

    [Theory]
    [InlineData(true, true, false, 180, true)]
    [InlineData(true, false, false, 180, false)]   // salvata nascosta: tiene la larghezza XAML
    [InlineData(false, true, false, 180, false)]   // versione vecchia
    [InlineData(true, true, false, 39, false)]
    public void DevoApplicareLarghezza_Regole(bool applicabili, bool visibile, bool auto, double larghezza, bool atteso) =>
        Assert.Equal(atteso, TableLayoutPreferences.DevoApplicareLarghezza(applicabili, visibile, auto, larghezza));

    [Fact]
    public void LarghezzeCambiate_RilevaSoloVariazioniReali()
    {
        var a = new[] { new DataGridLength(100), new DataGridLength(200) };
        var uguali = new[] { new DataGridLength(100.2), new DataGridLength(200) };
        var diverse = new[] { new DataGridLength(100), new DataGridLength(260) };
        Assert.False(TableLayoutPreferences.LarghezzeCambiate(a, uguali));
        Assert.True(TableLayoutPreferences.LarghezzeCambiate(a, diverse));
        Assert.True(TableLayoutPreferences.LarghezzeCambiate(a, a.Take(1).ToArray()));
    }
}
