using TestLoom.App;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata per il punto 12 (feedback utente 2026-08-20): i campi data devono offrire sia
/// il calendario sia l'inserimento libero. DataTestoConverter fa da ponte tra il campo Valore/Testo esistente
/// (string, "gg/mm/aaaa") e CalendarDatePicker.SelectedDate (DateTime?), senza cambiare lo storage esistente.</summary>
public class DataTestoConverterTests
{
    [Fact]
    public void Convert_TestoValido_RitornaDateTime()
    {
        var risultato = DataTestoConverter.Instance.Convert("25/12/2026", typeof(DateTime?), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new DateTime(2026, 12, 25), risultato);
    }

    [Theory]
    [InlineData("")]
    [InlineData("non una data")]
    [InlineData("32/13/2026")]
    [InlineData("2026-12-25")]
    public void Convert_TestoNonValidoOVuoto_RitornaNull(string testo)
    {
        var risultato = DataTestoConverter.Instance.Convert(testo, typeof(DateTime?), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Null(risultato);
    }

    [Fact]
    public void Convert_Null_RitornaNull()
    {
        Assert.Null(DataTestoConverter.Instance.Convert(null, typeof(DateTime?), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_DateTime_RitornaTestoFormatoGgMmAaaa()
    {
        var risultato = DataTestoConverter.Instance.ConvertBack(new DateTime(2026, 3, 7), typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("07/03/2026", risultato);
    }

    [Fact]
    public void ConvertBack_Null_RitornaStringaVuota()
    {
        var risultato = DataTestoConverter.Instance.ConvertBack(null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("", risultato);
    }

    [Fact]
    public void Roundtrip_TestoOriginale_RestaIdenticoDopoAndataERitorno()
    {
        var originale = "01/01/2027";
        var data = DataTestoConverter.Instance.Convert(originale, typeof(DateTime?), null, System.Globalization.CultureInfo.InvariantCulture);
        var testo = DataTestoConverter.Instance.ConvertBack(data, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(originale, testo);
    }
}
