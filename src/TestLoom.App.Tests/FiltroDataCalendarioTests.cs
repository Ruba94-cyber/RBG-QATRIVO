using System.Globalization;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Filtri di data con CalendarDatePicker (Anomalie, Lista Test): conversione testo/data, data digitata non valida
/// segnalata senza applicare una data precedente, persistenza compatibile con i valori gia' salvati in gg/MM/aaaa o aaaa-MM-gg.</summary>
public sealed class FiltroDataCalendarioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_FiltroDataCal_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState Stato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        return state;
    }

    private static DateTimeOffset Giorno(int anno, int mese, int giorno) => new(new DateTime(anno, mese, giorno));

    private static object? Converti(string? testo) =>
        DataFiltroTestoConverter.Instance.ConvertBack(testo, typeof(DateTime?), null, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("01/03/2026", 2026, 3, 1)]
    [InlineData("1/3/2026", 2026, 3, 1)]
    [InlineData("2026-03-01", 2026, 3, 1)]
    [InlineData("  01/03/2026  ", 2026, 3, 1)]
    public void ConvertBack_AccettaGgMmAaaaGMAaaaaAaaaaMmGgESpazi(string testo, int anno, int mese, int giorno) =>
        Assert.Equal(new DateTime(anno, mese, giorno), Converti(testo));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ConvertBack_VuotoOSoloSpazi_NessunaData(string? testo) => Assert.Null(Converti(testo));

    [Theory]
    [InlineData("14/03/20")]    // anno incompleto: non e' una data, non applica alcun vincolo
    [InlineData("31/02/2026")]  // giorno inesistente
    [InlineData("32/01/2026")]
    [InlineData("01-03-2026")]
    [InlineData("abc")]
    public void ConvertBack_TestoNonValido_LanciaFormatException(string testo) =>
        Assert.Throws<FormatException>(() => Converti(testo));

    [Fact]
    public void Convert_DataInFormatoItaliano_EVuotoSenzaData()
    {
        Assert.Equal("01/03/2026", DataFiltroTestoConverter.Instance.Convert(new DateTime(2026, 3, 1), typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("", DataFiltroTestoConverter.Instance.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Nc_StatoSalvatoInFormatoVecchioEIso_VieneLetto()
    {
        var s = Stato();
        ViewStateService.Scrivi(s, "AnomalieCr.DataAperturaDa", "01/03/2026");   // formato testo delle versioni precedenti
        ViewStateService.Scrivi(s, "AnomalieCr.DataChiusuraA", "2026-06-10");    // formato ISO salvato dalla versione attuale

        var vm = new NonConformitaViewModel(s, _ => { });

        Assert.Equal(Giorno(2026, 3, 1), vm.FiltroDataAperturaDa);
        Assert.Equal(new DateTime(2026, 3, 1), vm.DataAperturaDa);
        Assert.Equal(new DateTime(2026, 6, 10), vm.DataChiusuraA);
    }

    [Fact]
    public void Nc_DataValidaDopoSegnalazione_RimuoveLaSegnalazioneEPersisteIso()
    {
        var s = Stato();
        var vm = new NonConformitaViewModel(s, _ => { });

        vm.SegnalaDataNonValida(nameof(NonConformitaViewModel.DataAperturaA));
        Assert.True(vm.HaDataNonValida);

        vm.FiltroDataAperturaA = Giorno(2026, 3, 14);

        Assert.False(vm.DataAperturaANonValida);
        Assert.False(vm.HaDataNonValida);
        Assert.Equal("2026-03-14", ViewStateService.Leggi(s, "AnomalieCr.DataAperturaA"));
    }

    [Fact]
    public void Nc_CampoSconosciuto_LanciaErrore()
    {
        var vm = new NonConformitaViewModel(Stato(), _ => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.SegnalaDataNonValida("DataInesistente"));
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.AzzeraDataNonValida("DataInesistente"));
    }

    [Fact]
    public void ListaTest_DataDigitataNonValida_NonApplicaLaDataPrecedente()
    {
        var vm = new TestCaseListViewModel(Stato(), _ => { });
        vm.FiltroDataDa = Giorno(2026, 3, 1);

        vm.SegnalaDataNonValida(nameof(TestCaseListViewModel.FiltroDataDa));

        Assert.Null(vm.FiltroDataDa);
        Assert.True(vm.FiltroDataDaNonValida);
        Assert.True(vm.HaDataNonValida);

        vm.FiltroDataDa = Giorno(2026, 3, 1);
        Assert.False(vm.FiltroDataDaNonValida);
        Assert.False(vm.HaDataNonValida);
    }

    [Fact]
    public void ListaTest_ReimpostaFiltri_AzzeraLeDateEAncheLaSegnalazione()
    {
        var vm = new TestCaseListViewModel(Stato(), _ => { });
        vm.FiltroDataA = Giorno(2026, 12, 31);
        vm.SegnalaDataNonValida(nameof(TestCaseListViewModel.FiltroDataDa));

        vm.ReimpostaFiltriCommand.Execute(null);

        Assert.Null(vm.FiltroDataDa);
        Assert.Null(vm.FiltroDataA);
        Assert.False(vm.HaDataNonValida);
    }
}
