using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Il cambio lingua dalle Impostazioni deve notificare alla UI le proprietà che mostrano radio e testi
/// localizzati, comprese le etichette calcolate: con una notifica senza nome i binding restano nella lingua precedente.</summary>
[Collection("Localization")]
public sealed class ImpostazioniLinguaNotificaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_LinguaVm_" + Guid.NewGuid().ToString("N"));
    private readonly string _linguaOriginale = LocalizationService.Instance.Lingua;

    public void Dispose()
    {
        LocalizationService.Instance.Lingua = _linguaOriginale;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void CambioLingua_NotificaRadioEEtichettaLocaleAllaUi()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var vm = new ImpostazioniViewModel(state, () => { });
        var notificate = new List<string>();
        vm.PropertyChanged += (_, e) => notificate.Add(e.PropertyName ?? string.Empty);

        vm.EFrancese = true;

        Assert.Contains(nameof(ImpostazioniViewModel.EItaliano), notificate);
        Assert.Contains(nameof(ImpostazioniViewModel.EInglese), notificate);
        Assert.Contains(nameof(ImpostazioniViewModel.EFrancese), notificate);
        Assert.Contains(nameof(ImpostazioniViewModel.EtichettaVerificaLocale), notificate);
        Assert.Equal("Vérifier les données locales", vm.EtichettaVerificaLocale);
    }
}
