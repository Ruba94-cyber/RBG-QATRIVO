using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Validazione visibile dei filtri TIME (Home e Report): un testo non valido ("1,5", "-3", "abc") segnala l'errore sul campo,
/// ma il filtro resta "nessun filtro" come prima; un testo valido o vuoto non segnala nulla.</summary>
[Collection("Localization")]
public sealed class TempoFiltroValidazioneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TempoValidazione_" + Guid.NewGuid().ToString("N"));
    private readonly AppState _state;

    public TempoFiltroValidazioneTests()
    {
        Directory.CreateDirectory(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("0", false)]
    [InlineData("15", false)]
    [InlineData("1,5", true)]
    [InlineData("-3", true)]
    [InlineData("abc", true)]
    [InlineData("99999999999", true)]
    public void TestoValido_SegnalaSoloIlTestoNonIntero(string testo, bool haErrore)
    {
        Assert.Equal(haErrore, !FiltroDurataMinuti.TestoValido(testo));
        if (haErrore) Assert.Null(FiltroDurataMinuti.DaTesto(testo, 0));
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("-3")]
    [InlineData("abc")]
    public void Home_TestoNonValido_SegnalaErroreESenzaFiltro(string testo)
    {
        var vm = new HomeViewModel(_state, _ => { });
        var notificati = new List<string?>();
        vm.PropertyChanged += (_, e) => notificati.Add(e.PropertyName);

        vm.TestoFiltroTempoOperazione = testo;
        vm.TestoFiltroTempoTotaleTest = testo;

        Assert.True(vm.FiltroTempoOperazioneHaErrore);
        Assert.True(vm.FiltroTempoTotaleTestHaErrore);
        Assert.Contains(nameof(HomeViewModel.FiltroTempoOperazioneHaErrore), notificati);
        Assert.Contains(nameof(HomeViewModel.FiltroTempoTotaleTestHaErrore), notificati);
        Assert.Null(vm.FiltroTempoOperazioneAttivo);
        Assert.Null(vm.FiltroTempoTotaleTestAttivo);

        vm.TestoFiltroTempoOperazione = "15";
        Assert.False(vm.FiltroTempoOperazioneHaErrore);
        Assert.NotNull(vm.FiltroTempoOperazioneAttivo);
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("-3")]
    [InlineData("abc")]
    public void Report_TestoNonValido_SegnalaErroreESenzaFiltro(string testo)
    {
        var vm = new ReportViewModel(_state);
        var notificati = new List<string?>();
        vm.PropertyChanged += (_, e) => notificati.Add(e.PropertyName);

        vm.TestoFiltroTempoOperazione = testo;
        vm.TestoFiltroTempoTotaleTest = testo;

        Assert.True(vm.FiltroTempoOperazioneHaErrore);
        Assert.True(vm.FiltroTempoTotaleTestHaErrore);
        Assert.Contains(nameof(ReportViewModel.FiltroTempoOperazioneHaErrore), notificati);
        Assert.Contains(nameof(ReportViewModel.FiltroTempoTotaleTestHaErrore), notificati);
        Assert.Null(vm.FiltroTempoOperazioneAttivo);
        Assert.Null(vm.FiltroTempoTotaleTestAttivo);

        vm.TestoFiltroTempoTotaleTest = "";
        Assert.False(vm.FiltroTempoTotaleTestHaErrore);
    }

    [Fact]
    public void Vuoto_NonSegnalaErroreNeHomeNeReport()
    {
        var home = new HomeViewModel(_state, _ => { });
        Assert.False(home.FiltroTempoOperazioneHaErrore);
        Assert.False(home.FiltroTempoTotaleTestHaErrore);

        var report = new ReportViewModel(_state);
        Assert.False(report.FiltroTempoOperazioneHaErrore);
        Assert.False(report.FiltroTempoTotaleTestHaErrore);
    }

    [Fact]
    public void MessaggioNonValido_EsisteLocalizzato()
    {
        var messaggio = LocalizationService.Instance.Traduci("FiltroTempoMinutiNonValidoMsg");
        Assert.NotEqual("FiltroTempoMinutiNonValidoMsg", messaggio);
        Assert.False(string.IsNullOrWhiteSpace(messaggio));
    }
}
