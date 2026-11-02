using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class FirstRunView : UserControl
{
    // Popup di avanzamento (stesso AvvioWindow del bootstrap in App.axaml.cs) mostrato mentre
    // FirstRunViewModel.Conferma inizializza una cartella nuova/grande sul thread di background. Vive nel
    // code-behind e non nel ViewModel perché una Window è un dettaglio della view, non dello stato applicativo
    // (lo stesso ViewModel è coperto da test unit che non toccano Avalonia).
    private AvvioWindow? _popupAccesso;
    private CancellationTokenSource? _popupDelayCts;

    // Stessa soglia di App.axaml.cs (SogliaComparsaAvvio): un login di ritorno sulla stessa cartella già
    // inizializzata ritorna quasi subito da ImpostaCartella (guard su CartellaValida) e non deve far
    // sfarfallare una finestra che si apre e chiude in pochi millisecondi.
    private static readonly TimeSpan SogliaComparsaPopupAccesso = TimeSpan.FromMilliseconds(500);

    public FirstRunView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FirstRunViewModel vm) vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FirstRunViewModel.AccessoInCorso)) return;
        if (sender is not FirstRunViewModel vm) return;
        if (vm.AccessoInCorso) SchedulaPopupAccesso(vm);
        else
        {
            _popupDelayCts?.Cancel();
            ChiudiPopupAccesso();
        }
    }

    private async void SchedulaPopupAccesso(FirstRunViewModel vm)
    {
        _popupDelayCts?.Cancel();
        var cts = new CancellationTokenSource();
        _popupDelayCts = cts;
        try { await Task.Delay(SogliaComparsaPopupAccesso, cts.Token); }
        catch (TaskCanceledException) { return; }
        if (cts.IsCancellationRequested || !vm.AccessoInCorso) return;
        MostraPopupAccesso(vm);
    }

    private void MostraPopupAccesso(FirstRunViewModel vm)
    {
        if (_popupAccesso != null) return;
        _popupAccesso = new AvvioWindow { DataContext = vm.AvvioProgress };
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null) _popupAccesso.Show(owner);
        else _popupAccesso.Show();
    }

    private void ChiudiPopupAccesso()
    {
        if (_popupAccesso == null) return;
        _popupAccesso.ConsentiChiusura();
        _popupAccesso.Close();
        _popupAccesso = null;
    }

    private async void OnScegliCartella(object? sender, RoutedEventArgs e)
    {
        await UiExceptionGuard.Esegui(async () =>
        {
            if (DataContext is not FirstRunViewModel vm) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is not { CanPickFolder: true } storage) return;
            var cartelle = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("ScegliCartellaTitolo"),
                AllowMultiple = false
            });
            var cartella = cartelle.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(cartella)) return;
            vm.Cartella = cartella;
            vm.RicaricaDopoSceltaCommand.Execute(null);
        }, LocalizationService.Instance.Traduci("FirstRunSelezioneCartellaTitolo"));
    }
}
