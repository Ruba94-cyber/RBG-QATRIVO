using System.Globalization;
using TestLoom.App;

namespace TestLoom.App.Tests;

/// <summary>Copre il feedback utente 2026-08-21 punto 2: sopra 10 opzioni un campo Lista deve mostrare il
/// picker con ricerca invece della ComboBox pura, con la soglia esattamente a 10 (10 compreso resta ComboBox).</summary>
public class ListaSogliaConverterTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(50, false)]
    public void ListaEntroSoglia_VeroFinoA10ElementiCompresi(int numeroOpzioni, bool atteso)
    {
        var lista = Enumerable.Range(0, numeroOpzioni).Select(i => i.ToString()).ToList();
        var risultato = ListaEntroSogliaConverter.Instance.Convert(lista, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(atteso, risultato);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    [InlineData(50, true)]
    public void ListaOltreSoglia_VeroSoloOltre10Elementi(int numeroOpzioni, bool atteso)
    {
        var lista = Enumerable.Range(0, numeroOpzioni).Select(i => i.ToString()).ToList();
        var risultato = ListaOltreSogliaConverter.Instance.Convert(lista, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(atteso, risultato);
    }
}
