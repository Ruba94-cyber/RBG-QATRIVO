using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class ChangeRequestEditorView : UserControl
{
    // Selettore dei Test sopra soglia: ricerca sul motore gia' preparato dal ViewModel (PickerTestDisponibili), con debounce e
    // scarto dei risultati superati, come gli altri selettori con elenchi lunghi (vedi AutoCompleteRicerca).
    private AutoCompleteRicerca<TestCase>? _ricercaTest;
    private PickerMotore<TestCase>? _motoreTest;

    public ChangeRequestEditorView()
    {
        InitializeComponent();

        CollegaTestAutoCompleteBox.Populating += CollegaTestPopulating;
        CollegaTestAutoCompleteBox.AddHandler(InputElement.KeyDownEvent, CollegaTestTastiera, RoutingStrategies.Tunnel);

        // Popup "campi mancanti" posizionato sulla card interessata (punto 8, feedback utente
        // 2026-08-21): stesso CampiMancantiDialog già usato dal Test.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ChangeRequestEditorViewModel vm) return;
            vm.RichiestaMostraCampiMancanti += () =>
            {
                if (TopLevel.GetTopLevel(this) is not Window proprietario) return;
                // Se l'unico requisito mancante è la motivazione, porta subito l'utente al campo:
                // un popup davanti all'editor impediva di inserirla senza un ulteriore clic sulla riga.
                if (vm.CampiMancanti.Count == 1 && vm.CampiMancanti[0].Chiave == "MotivazioneDuplicato")
                {
                    VaiAlCampo("MotivazioneDuplicato");
                    return;
                }
                CampiMancantiDialog.Mostra(proprietario, vm.CampiMancanti, VaiAlCampo);
            };

            // Il Test di validazione resta in una finestra dedicata, ma ora la finestra costruisce l'editor con
            // un callback di chiusura reale. Salva/Annulla e X condividono quindi lo stesso lifecycle sicuro.
            vm.RichiestaAperturaTestInFinestra += testId =>
            {
                if (TopLevel.GetTopLevel(this) is not Window proprietario) return;
                TestCaseEditWindow.Apri(proprietario, vm, testId);
            };

            InstradaComandoTornaSicuro();
        };
        AttachedToVisualTree += (_, _) => InstradaComandoTornaSicuro();
    }

    /// <summary>
    /// Lo XAML storico lega il pulsante "Torna alla lista" al comando diretto TornaAllaListaCommand. Come per
    /// il Salva del Test, sostituiamo soltanto quel riferimento dopo che i binding sono stati materializzati:
    /// cosi' ogni uscita volontaria dalla CR passa dalla conferma scoped e dalla cleanup allegati fail-safe,
    /// senza duplicare il grande markup XAML solo per cambiare una singola binding expression.
    /// </summary>
    /// <summary>Menu del selettore Test: il motore del ViewModel cerca, il filtro interno di AutoCompleteBox non si usa (FilterMode None).</summary>
    private void CollegaTestPopulating(object? sender, PopulatingEventArgs e)
    {
        e.Cancel = true;
        var ricerca = RicercaTestCorrente();
        if (ricerca == null)
        {
            // Elenco non ancora pronto: menu vuoto finche' il ViewModel non pubblica il motore.
            CollegaTestAutoCompleteBox.ItemsSource = Array.Empty<TestCase>();
            CollegaTestAutoCompleteBox.PopulateComplete();
            return;
        }
        ricerca.Digita(e.Parameter);
    }

    /// <summary>Invio e frecce: la ricerca ancora in attesa del debounce si allinea prima dell'azione, cosi' la scelta non cade su un elenco superato.</summary>
    private void CollegaTestTastiera(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Enter) RicercaTestCorrente()?.Allinea(CollegaTestAutoCompleteBox.Text);
    }

    /// <summary>Ricerca collegata al motore corrente del ViewModel; si ricostruisce solo quando il motore cambia (elenco ricaricato).</summary>
    private AutoCompleteRicerca<TestCase>? RicercaTestCorrente()
    {
        var motore = (DataContext as ChangeRequestEditorViewModel)?.PickerTestDisponibili;
        if (motore == null) return null;
        if (_ricercaTest == null || !ReferenceEquals(motore, _motoreTest))
        {
            _ricercaTest?.Dispose();
            _motoreTest = motore;
            _ricercaTest = new AutoCompleteRicerca<TestCase>(motore, voci =>
            {
                CollegaTestAutoCompleteBox.ItemsSource = voci;
                CollegaTestAutoCompleteBox.PopulateComplete();
            });
        }
        return _ricercaTest;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _ricercaTest?.Dispose();
        _ricercaTest = null;
        _motoreTest = null;
    }

    private void InstradaComandoTornaSicuro()
    {
        if (DataContext is not ChangeRequestEditorViewModel vm) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var button in this.GetVisualDescendants().OfType<Button>())
            {
                if (ReferenceEquals(button.Command, vm.TornaAllaListaCommand))
                    button.Command = vm.TornaAllaListaSicuroCommand;
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void VaiAlCampo(string chiave)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (chiave == "MotivazioneDuplicato")
            {
                DatiCr.BringIntoView();
                MotivazioneDuplicatoTextBox.Focus();
                return;
            }
            var sezione = chiave == "AmbitoImpattato" ? AmbitoImpattato : DatiCr;
            if (sezione.IsVisible) sezione.BringIntoView();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private async void OnRecuperaBozza(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChangeRequestEditorViewModel vm) return;
        if (!vm.PuoRecuperareBozzaLocale) return;
        if (vm.ModificheNonSalvate)
        {
            vm.Errore = LocalizationService.Instance.Traduci("BozzaSalvareAnnullarePrimaMsg");
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        // CR mai salvata: bozze senza fotografia iniziale il cui UID non esiste ancora nel repository.
        var nuovoRecord = vm.PuoRecuperareBozzaNuova;
        try
        {
            FailedSaveDraftsWindow dialog;
            if (nuovoRecord)
            {
                var presenti = await Task.Run(vm.UidCrPresenti);
                dialog = new FailedSaveDraftsWindow(vm.WorkspaceRoot, "change-request", null,
                    editorSelectionMode: true, nuovoRecordUidGiaPresente: presenti.Contains);
            }
            else
            {
                dialog = new FailedSaveDraftsWindow(vm.WorkspaceRoot,
                    "change-request", vm.ChangeRequest.Uid, editorSelectionMode: true);
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
            vm.Errore = LocalizationService.Instance.Traduci("BozzaNonCaricataPrefisso") + ex.Message;
            CrashLogService.Registra("Recupero bozza locale nell'editor CR", ex);
        }
    }

    private static Task ApriPickerMultiplo(object? sender, string titolo, IReadOnlyList<string> disponibili, Action<IReadOnlyList<string>> aggiungi) =>
        MultiSelectPickerFlyout.Apri(sender, titolo, disponibili, aggiungi);

    private async void OnScegliVeicoli(object? sender, RoutedEventArgs e) =>
        await UiExceptionGuard.Esegui(() => GestisciScegliVeicoli(sender, e), "Scelta veicoli (change request)",
            mostra: () => ErroreOperazioneDialogo.MostraAsync(this));

    private async Task GestisciScegliVeicoli(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChangeRequestEditorViewModel vm)
            await ApriPickerMultiplo(sender, LocalizationService.Instance.Traduci("ScegliVeicoliTitolo"), vm.VeicoliDisponibili, vm.AggiungiVeicoli);
    }

    private async void OnScegliEcu(object? sender, RoutedEventArgs e) =>
        await UiExceptionGuard.Esegui(() => GestisciScegliEcu(sender, e), "Scelta componente (change request)",
            mostra: () => ErroreOperazioneDialogo.MostraAsync(this));

    private async Task GestisciScegliEcu(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChangeRequestEditorViewModel vm)
            await ApriPickerMultiplo(sender, LocalizationService.Instance.Traduci("ScegliEcuTitolo"), vm.EcuDisponibili, vm.AggiungiEcu);
    }

    private async void OnScegliStrumenti(object? sender, RoutedEventArgs e) =>
        await UiExceptionGuard.Esegui(() => GestisciScegliStrumenti(sender, e), "Scelta strumenti (change request)",
            mostra: () => ErroreOperazioneDialogo.MostraAsync(this));

    private async Task GestisciScegliStrumenti(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChangeRequestEditorViewModel vm)
            await ApriPickerMultiplo(sender, LocalizationService.Instance.Traduci("ScegliStrumentiTitolo"), vm.StrumentiDisponibili, vm.AggiungiStrumenti);
    }

    private async void OnScegliGamma(object? sender, RoutedEventArgs e) =>
        await UiExceptionGuard.Esegui(() => GestisciScegliGamma(sender, e), "Scelta gamme (change request)",
            mostra: () => ErroreOperazioneDialogo.MostraAsync(this));

    private async Task GestisciScegliGamma(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChangeRequestEditorViewModel vm)
            await ApriPickerMultiplo(sender, LocalizationService.Instance.Traduci("ScegliGammeTitolo"), vm.GammeDisponibili, vm.AggiungiVeicoliDaGamme);
    }

    private async void OnSceglieFileAllegato(object? sender, RoutedEventArgs e) =>
        await UiExceptionGuard.Esegui(() => GestisciSceglieFileAllegato(sender, e), "Scelta allegato (change request)",
            mostra: () => ErroreOperazioneDialogo.MostraAsync(this));

    private async Task GestisciSceglieFileAllegato(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChangeRequestEditorViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            var estensioni = AttachmentService.EstensioniConsentite.Select(ext => "*" + ext).ToList();
            var file = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("CrScegliAllegatiTitolo"),
                AllowMultiple = true,
                FileTypeFilter = new List<FilePickerFileType> { new("Immagini e archivi") { Patterns = estensioni } }
            });

            foreach (var f in file)
            {
                var percorso = f.TryGetLocalPath();
                if (!string.IsNullOrEmpty(percorso) && vm.AggiungiAllegatoCommand.CanExecute(percorso))
                    await vm.AggiungiAllegatoCommand.ExecuteAsync(percorso);
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Selezione allegati CR", ex); }
    }

    /// <summary>Apre direttamente l'anteprima di creazione TICKET (come per le segnalazioni di rete), senza passare dall'export del pacchetto.</summary>
    private async void OnPreparaEsta(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChangeRequestEditorViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window proprietario) return;
        if (vm.ModificheNonSalvate)
        {
            vm.Errore = LocalizationService.Instance.Traduci("CrEdSalvaPrimaEsta");
            return;
        }
        await UiExceptionGuard.Esegui(
            () => PolarionCreateTicketDialog.OpenCrAsync(proprietario, vm.StatoApp, vm.ChangeRequest),
            "Preparazione ticket TICKET da CR");
    }

    private async void OnEsportaPacchetto(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChangeRequestEditorViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        HashSet<string>? sezioni = null;
        var proprietario = TopLevel.GetTopLevel(this) as Window;
        await UiExceptionGuard.Esegui(async () =>
        {
            var dialogo = PolarionSelezioneDialog.Crea(vm.SezioniPacchettoDisponibili(), vm.SezioniPacchettoDiDefault(), vm.EtichettaSezionePacchetto, vm.GeneraAnteprimaPacchetto);
            sezioni = proprietario != null ? await dialogo.ShowDialog<HashSet<string>?>(proprietario) : null;
        }, "Apertura selezione pacchetto CR");
        if (sezioni == null) return;

        try
        {
            var cartelle = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("CrScegliDoveSalvareTitolo"),
                AllowMultiple = false
            });

            var percorso = cartelle.FirstOrDefault()?.TryGetLocalPath();
            var richiesta = !string.IsNullOrEmpty(percorso) ? new RichiestaPacchettoPolarion(percorso, sezioni) : null;
            if (richiesta != null && vm.EsportaPacchettoCommand.CanExecute(richiesta))
                vm.EsportaPacchettoCommand.Execute(richiesta);
        }
        catch (Exception ex) { CrashLogService.Registra("Selezione destinazione pacchetto CR", ex); }
    }
}
