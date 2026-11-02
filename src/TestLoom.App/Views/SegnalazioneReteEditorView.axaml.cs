using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class SegnalazioneReteEditorView : UserControl
{
    public SegnalazioneReteEditorView()
    {
        InitializeComponent();

        // Popup "campi mancanti" posizionato sulla card interessata (feedback utente 2026-09-15), stesso
        // CampiMancantiDialog già usato da Test e Change Request.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not SegnalazioneReteEditorViewModel vm) return;
            vm.RichiestaMostraCampiMancanti += () =>
            {
                if (TopLevel.GetTopLevel(this) is not Window proprietario) return;
                CampiMancantiDialog.Mostra(proprietario, vm.CampiMancanti, VaiAlCampo);
            };
        };
    }

    private void VaiAlCampo(string chiave)
    {
        var sezione = chiave switch
        {
            "DescrizioneSegnalata" => DescrizioneSegnalata,
            "AmbitoSegnalato" => AmbitoSegnalato,
            _ => OrigineSegnalazione
        };
        if (sezione.IsVisible) sezione.BringIntoView();
    }

    private async void OnRecuperaBozza(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SegnalazioneReteEditorViewModel vm || !vm.PuoRecuperareBozzaLocale) return;
        if (vm.ModificheNonSalvate)
        {
            vm.MessaggioErrore = LocalizationService.Instance.Traduci("BozzaSalvareAnnullarePrimaMsg");
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        // Segnalazione di rete mai salvata: bozze senza fotografia iniziale il cui UID non esiste ancora nel repository.
        var nuovoRecord = vm.PuoRecuperareBozzaNuova;
        try
        {
            FailedSaveDraftsWindow dialog;
            if (nuovoRecord)
            {
                var presenti = await Task.Run(vm.UidNcPresenti);
                dialog = new FailedSaveDraftsWindow(vm.WorkspaceRoot, "nonconformita", null,
                    editorSelectionMode: true, nuovoRecordUidGiaPresente: presenti.Contains);
            }
            else
            {
                dialog = new FailedSaveDraftsWindow(vm.WorkspaceRoot,
                    "nonconformita", vm.Nc.Uid, editorSelectionMode: true);
            }
            var selection = await dialog.ShowDialog<FailedSaveDraftsWindow.EditorDraftSelection?>(owner);
            if (selection == null) return;
            if (nuovoRecord)
            {
                var candidato = await Task.Run(() => vm.CaricaBozzaNuovaRecuperabile(selection.Draft));
                vm.ApplicaBozzaNuovaRecuperata(candidato);
                return;
            }
            var candidate = await Task.Run(() => vm.CaricaBozzaRecuperabile(selection.Draft));
            vm.ApplicaBozzaRecuperata(candidate, selection.Fields);
        }
        catch (Exception ex)
        {
            vm.MessaggioErrore = LocalizationService.Instance.Traduci("BozzaNonCaricataPrefisso") + ex.Message;
            CrashLogService.Registra("Recupero bozza locale nell'editor NC", ex);
        }
    }

    private void OnNumeroPraticaKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not SegnalazioneReteEditorViewModel vm) return;
        if (vm.AggiungiNumeroPraticaCommand.CanExecute(null)) vm.AggiungiNumeroPraticaCommand.Execute(null);
    }

    private void OnVinKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not SegnalazioneReteEditorViewModel vm) return;
        if (vm.AggiungiVinCommand.CanExecute(null)) vm.AggiungiVinCommand.Execute(null);
    }

    private void OnLicenzaKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not SegnalazioneReteEditorViewModel vm) return;
        if (vm.AggiungiLicenzaCommand.CanExecute(null)) vm.AggiungiLicenzaCommand.Execute(null);
    }

    private async void OnScegliEcu(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SegnalazioneReteEditorViewModel vm)
            await MultiSelectPickerFlyout.Apri(sender, LocalizationService.Instance.Traduci("ScegliEcuTitolo"), vm.EcuDisponibili, vm.AggiungiEcu);
    }

    private async void OnScegliVeicoli(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SegnalazioneReteEditorViewModel vm)
            await MultiSelectPickerFlyout.Apri(sender, LocalizationService.Instance.Traduci("ScegliVeicoliTitolo"), vm.VeicoliDisponibili, vm.AggiungiVeicoli);
    }

    private async void OnScegliFunzionalita(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SegnalazioneReteEditorViewModel vm)
            await MultiSelectPickerFlyout.Apri(sender, LocalizationService.Instance.Traduci("ScegliFunzionalitaTitolo"), vm.FunzionalitaDisponibili, vm.AggiungiFunzionalita);
    }

    /// <summary>Stesso schema di ChangeRequestEditorView.OnSceglieFileAllegato/TestCaseEditorView.OnSceglieFileAllegato.</summary>
    private async void OnSceglieFileAllegato(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SegnalazioneReteEditorViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            var estensioni = AttachmentService.EstensioniConsentite.Select(ext => "*" + ext).ToList();
            var file = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("SegScegliAllegatiTitolo"),
                AllowMultiple = true,
                FileTypeFilter = new List<FilePickerFileType> { new(LocalizationService.Instance.Traduci("FileConsentitiTitolo")) { Patterns = estensioni } }
            });

            foreach (var f in file)
            {
                var percorso = f.TryGetLocalPath();
                if (!string.IsNullOrEmpty(percorso) && vm.AggiungiAllegatoCommand.CanExecute(percorso))
                    await vm.AggiungiAllegatoCommand.ExecuteAsync(percorso);
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Selezione allegati segnalazione rete", ex); }
    }

    /// <summary>Salva la segnalazione se serve, poi apre l'anteprima di creazione TICKET (browser con il link configurato).</summary>
    private async void OnEsportaPacchetto(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SegnalazioneReteEditorViewModel vm) return;
        var proprietario = TopLevel.GetTopLevel(this) as Window;
        if (proprietario == null) return;
        var stato = vm.StatoApp;
        await UiExceptionGuard.Esegui(async () =>
        {
            var nc = await vm.SalvaPerPreparareEstaAsync();
            if (nc == null || string.IsNullOrWhiteSpace(nc.Id)) return;
            await PolarionCreateTicketDialog.OpenNcAsync(proprietario, stato, nc, null);
        }, "Preparazione ticket TICKET da segnalazione");
    }

    /// <summary>Stesso schema di TestCaseEditorView.OnFunzionalitaOperazioneModificata: la selezione va sulla
    /// singola OperazioneSegnalazione (letta dal DataContext della riga, non dal ViewModel della pagina), e
    /// serve un handler in code-behind — non un semplice binding TwoWay — solo per marcare ModificheNonSalvate.</summary>
    private void OnEcuOperazioneModificata(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string componente, DataContext: OperazioneSegnalazione operazione }) return;
        if (DataContext is not SegnalazioneReteEditorViewModel vm) return;
        if (operazione.Componente.Equals(componente, StringComparison.Ordinal)) return;
        operazione.Componente = componente;
        vm.ModificheNonSalvate = true;
    }

    private void OnFunzionalitaOperazioneModificata(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string funzionalita, DataContext: OperazioneSegnalazione operazione }) return;
        if (DataContext is not SegnalazioneReteEditorViewModel vm) return;
        if (operazione.Funzionalita.Equals(funzionalita, StringComparison.Ordinal)) return;
        operazione.Funzionalita = funzionalita;
        vm.ModificheNonSalvate = true;
    }
}
