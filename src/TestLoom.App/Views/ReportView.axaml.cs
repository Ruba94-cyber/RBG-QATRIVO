using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using System.Globalization;

namespace TestLoom.App.Views;

public partial class ReportView : UserControl
{
    internal sealed record DialogoSceltaExport(Window Finestra, Task<int?> Risultato,
        IReadOnlyDictionary<int, Button> Pulsanti);

    internal const string IdDialogoPreflightExport = "Report_ExportPreflight_Dialog";
    internal const string IdAnnullaPreflightExport = "Report_ExportPreflight_Cancel_Button";
    internal const string IdDialogoExportGrande = "Report_ExportLarge_Dialog";
    internal static IReadOnlyDictionary<int, string> IdScelteExportGrande { get; } = new Dictionary<int, string>
    {
        [0] = "Report_ExportLarge_Truncate_Button",
        [1] = "Report_ExportLarge_ZipAll_Button",
        [2] = "Report_ExportLarge_Cancel_Button"
    };
    internal static bool SceltaExportAnnullata(int? scelta) => scelta is null or 2;
    internal static bool RichiedeSceltaLimiteExport(int totale, int determinanti, int limite) =>
        ReportOrigineExportService.CalcolaConteggiLimitati(determinanti, Math.Max(0, totale - determinanti), limite).RichiedeScelta;
    internal static void AnnullaPreflightExport(CancellationTokenSource cancellation, Action? notificata = null)
    {
        cancellation.Cancel();
        notificata?.Invoke();
    }
    internal static void SegnalaErroreExportReport(ReportViewModel vm, Exception errore) =>
        vm.MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("GenerazioneReportNonRiuscitaFmt"), errore.Message);

    private AutoCompleteRicercaCollegamento? _ricercaEcu;
    private AutoCompleteRicercaCollegamento? _ricercaVeicolo;
    private StatoRidimensionamentoMatrice? _ridimensionamentoMatrice;

    private enum AsseRidimensionamentoMatrice { Colonne, Righe, Viewport }
    private sealed record StatoRidimensionamentoMatrice(IPointer Puntatore, AsseRidimensionamentoMatrice Asse,
        string? Chiave, Avalonia.Point PosizioneIniziale, double ValoreIniziale);

    public ReportView()
    {
        InitializeComponent();
        MatriceViewport.AddHandler(InputElement.PointerWheelChangedEvent, OnMatriceViewportPointerWheelChanged, RoutingStrategies.Tunnel);
        MatriceViewport.AddHandler(InputElement.PointerMovedEvent, OnMatriceResizePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        MatriceViewport.AddHandler(InputElement.PointerReleasedEvent, OnMatriceResizePointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        MatriceViewport.AddHandler(InputElement.PointerCaptureLostEvent, OnMatriceResizePointerCaptureLost, RoutingStrategies.Direct, handledEventsToo: true);
        AttachedToVisualTree += OnCollegaRicercheSelettori;
        DetachedFromVisualTree += OnScollegaRicercheSelettori;
    }

    /// <summary>componente e veicoli possono superare le migliaia di voci: il menu usa il motore di ricerca dei selettori
    /// invece del filtro sincrono di AutoCompleteBox. Il collegamento vive mentre la vista e' nell'albero visuale.</summary>
    private void OnCollegaRicercheSelettori(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _ricercaEcu ??= new AutoCompleteRicercaCollegamento(EcuAutoCompleteBox, () => SorgenteEcu());
        _ricercaVeicolo ??= new AutoCompleteRicercaCollegamento(VeicoliAutoCompleteBox, () => SorgenteVeicoli());
    }

    private void OnScollegaRicercheSelettori(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _ricercaEcu?.Dispose();
        _ricercaEcu = null;
        _ricercaVeicolo?.Dispose();
        _ricercaVeicolo = null;
    }

    private IReadOnlyList<string> SorgenteEcu() =>
        (IReadOnlyList<string>?)(DataContext as ReportViewModel)?.EcuDisponibili ?? Array.Empty<string>();

    private IReadOnlyList<string> SorgenteVeicoli() =>
        (IReadOnlyList<string>?)(DataContext as ReportViewModel)?.VeicoliDisponibili ?? Array.Empty<string>();

    private void OnApriFiltroMultiSelect(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not FiltroMultiSelect filtro) return;
        FiltroMultiSelectFlyoutHelper.Apri(button, filtro);
    }

    private void OnMatriceViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is ReportViewModel vm && sender is Control viewport)
            vm.AggiornaDimensioniViewportMatrice(viewport.Bounds.Width, viewport.Bounds.Height);
    }

    private void OnMatriceResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control superficie || !e.GetCurrentPoint(superficie).Properties.IsLeftButtonPressed
            || DataContext is not ReportViewModel vm) return;
        AsseRidimensionamentoMatrice asse;
        string? chiave = null;
        if (superficie.Tag is string { } tag && tag == "Viewport")
            asse = AsseRidimensionamentoMatrice.Viewport;
        else if (superficie is Border && superficie.Tag is SegmentoIntestazione { Ridimensionabile: true } segmento
            && VicinoAlBordo(e.GetPosition(superficie).X, superficie.Bounds.Width))
        {
            asse = AsseRidimensionamentoMatrice.Colonne;
            chiave = segmento.ChiaveDimensione;
        }
        else if (superficie is Border && superficie.Tag is string chiaveVuota
            && chiaveVuota == ReportViewModel.ChiaveColonnaSenzaCampo
            && VicinoAlBordo(e.GetPosition(superficie).X, superficie.Bounds.Width))
        {
            asse = AsseRidimensionamentoMatrice.Colonne;
            chiave = chiaveVuota;
        }
        else if (superficie is StackPanel { DataContext: RigaMatriceReport riga }
            && superficie.Bounds.Height - e.GetPosition(superficie).Y <= 7)
        {
            asse = AsseRidimensionamentoMatrice.Righe;
            chiave = riga.Riga;
        }
        else return;

        var valore = asse switch
        {
            AsseRidimensionamentoMatrice.Colonne => vm.LarghezzaPerColonnaMatrice(chiave!),
            AsseRidimensionamentoMatrice.Righe => vm.AltezzaPerRigaMatrice(chiave!),
            _ => vm.AltezzaViewportMatrice,
        };
        _ridimensionamentoMatrice = new(e.Pointer, asse, chiave, e.GetPosition(this), valore);
        // Le intestazioni di colonna possono essere ricreate quando cambia la misura. Catturare il puntatore
        // su una superficie stabile evita di perdere il trascinamento a metà ridimensionamento.
        e.Pointer.Capture(MatriceViewport);
        e.Handled = true;
    }

    private void OnMatriceResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_ridimensionamentoMatrice is { } ridimensionamento
            && ReferenceEquals(e.Pointer, ridimensionamento.Puntatore)
            && DataContext is ReportViewModel vm)
        {
            ApplicaTrascinamentoMatrice(vm, ridimensionamento, e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (sender is Control superficie) AggiornaCursoreRidimensionamento(superficie, e.GetPosition(superficie));
    }

    private void OnMatriceResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_ridimensionamentoMatrice is not { } ridimensionamento
            || !ReferenceEquals(e.Pointer, ridimensionamento.Puntatore)
            || DataContext is not ReportViewModel vm) return;
        ApplicaTrascinamentoMatrice(vm, ridimensionamento, e.GetPosition(this));
        _ridimensionamentoMatrice = null;
        vm.SalvaDimensioniMatrice();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnMatriceResizePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_ridimensionamentoMatrice is not { } ridimensionamento
            || !ReferenceEquals(e.Pointer, ridimensionamento.Puntatore)) return;
        _ridimensionamentoMatrice = null;
        if (DataContext is ReportViewModel vm) vm.SalvaDimensioniMatrice();
    }

    private void ApplicaTrascinamentoMatrice(ReportViewModel vm, StatoRidimensionamentoMatrice ridimensionamento, Avalonia.Point posizione)
    {
        var delta = ridimensionamento.Asse == AsseRidimensionamentoMatrice.Colonne
            ? posizione.X - ridimensionamento.PosizioneIniziale.X
            : posizione.Y - ridimensionamento.PosizioneIniziale.Y;
        switch (ridimensionamento.Asse)
        {
            case AsseRidimensionamentoMatrice.Colonne:
                vm.ImpostaLarghezzaColonnaMatrice(ridimensionamento.Chiave!, ridimensionamento.ValoreIniziale + delta);
                break;
            case AsseRidimensionamentoMatrice.Righe:
                vm.ImpostaAltezzaRigaMatrice(ridimensionamento.Chiave!, ridimensionamento.ValoreIniziale + delta);
                break;
            case AsseRidimensionamentoMatrice.Viewport:
                vm.ImpostaDimensioniMatrice(altezzaViewport: ridimensionamento.ValoreIniziale + delta);
                break;
        }
    }

    private static bool VicinoAlBordo(double posizione, double estensione) => posizione <= 7 || estensione - posizione <= 7;

    private void AggiornaCursoreRidimensionamento(Control superficie, Avalonia.Point posizione)
    {
        var attivo = superficie.Tag is string { } tag && tag == "Viewport"
            || superficie is Border && superficie.Tag is SegmentoIntestazione { Ridimensionabile: true } segmento
                && VicinoAlBordo(posizione.X, superficie.Bounds.Width)
            || superficie is Border && superficie.Tag is string chiave
                && chiave == ReportViewModel.ChiaveColonnaSenzaCampo && VicinoAlBordo(posizione.X, superficie.Bounds.Width)
            || superficie is StackPanel { DataContext: RigaMatriceReport }
                && superficie.Bounds.Height - posizione.Y <= 7;
        var verticale = superficie.Tag is string { } asse && asse == "Viewport"
            || superficie is StackPanel;
        superficie.Cursor = attivo
            ? new Cursor(verticale ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast)
            : null;
    }

    private void OnMatriceResizeKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control superficie || !ReferenceEquals(e.Source, superficie) || DataContext is not ReportViewModel vm) return;
        switch (superficie)
        {
            case Border { Tag: SegmentoIntestazione { Ridimensionabile: true, ChiaveDimensione: { } chiave } }:
                if (e.Key is not (Key.Left or Key.Right)) return;
                vm.ImpostaLarghezzaColonnaMatrice(chiave, vm.LarghezzaPerColonnaMatrice(chiave) + (e.Key == Key.Left ? -8 : 8));
                break;
            case Border { Tag: string chiaveVuota } when chiaveVuota == ReportViewModel.ChiaveColonnaSenzaCampo:
                if (e.Key is not (Key.Left or Key.Right)) return;
                vm.ImpostaLarghezzaColonnaMatrice(chiaveVuota, vm.LarghezzaPerColonnaMatrice(chiaveVuota) + (e.Key == Key.Left ? -8 : 8));
                break;
            case StackPanel { DataContext: RigaMatriceReport riga }:
                if (e.Key is not (Key.Up or Key.Down)) return;
                vm.ImpostaAltezzaRigaMatrice(riga.Riga, vm.AltezzaPerRigaMatrice(riga.Riga) + (e.Key == Key.Up ? -8 : 8));
                break;
            case Border { Tag: "Viewport" }:
                if (e.Key is not (Key.Up or Key.Down)) return;
                vm.ImpostaDimensioniMatrice(altezzaViewport: vm.AltezzaViewportMatrice + (e.Key == Key.Up ? -8 : 8));
                break;
            default:
                return;
        }
        vm.SalvaDimensioniMatrice();
        e.Handled = true;
    }

    private void OnMatriceViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not ReportViewModel vm) return;
        e.Handled = vm.ScorriMatriceDaRotella(e.Delta.X, e.Delta.Y,
            spostaSoloColonne: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    private async void OnOrdinaValori(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportViewModel vm || sender is not Button { Tag: string tag }) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var righe = tag == "righe";
        var valori = vm.ValoriOrdinabili(righe);
        if (valori.Count == 0) return;
        try
        {
            var risultato = await OrdinaValoriWindow.Apri(owner, vm.EtichettaDimensioneOrdinabile(righe) ?? "", valori.ToList());
            if (risultato != null) vm.ImpostaOrdineAsse(righe, risultato);
        }
        catch (Exception ex) { CrashLogService.Registra("Ordine assi matrice report", ex); }
    }

    /// <summary>Apre il pannello di correzione dello stato per la cella (solo Master, 6.6.1 fase 2).</summary>
    private async void OnCorreggiStatoCella(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportViewModel vm || sender is not Control { DataContext: CellaMatriceReport cella }) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            var titolo = $"{ReportMatriceService.EtichettaPercorso(cella.Riga)} × {ReportMatriceService.EtichettaPercorso(cella.Colonna)}";
            var finestra = new CorreggiStatoWindow(vm, titolo, vm.RigheCorrezioneCella(cella));
            await finestra.ShowDialog<bool?>(owner);
        }
        catch (Exception ex) { CrashLogService.Registra("Correzione stato matrice report", ex); }
    }

    /// <summary>Rimuove gli inserimenti manuali della cella: tornano a valere i Test (solo Master).</summary>
    private void OnRimuoviInserimentoManuale(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportViewModel vm || sender is not Control { DataContext: CellaMatriceReport cella }) return;
        try
        {
            vm.RimuoviInserimentoManuale(vm.RigheCorrezioneCella(cella).Where(r => r.ManualeVincente || r.ManualeSuperato).ToList(), out _);
        }
        catch (Exception ex) { CrashLogService.Registra("Rimozione inserimento manuale matrice report", ex); }
    }

    private async void OnOrdinaCampo(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportViewModel vm || sender is not Button { Tag: CampoAsseMatrice campo }) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var valori = vm.ValoriOrdinabiliCampo(campo);
        if (valori.Count == 0) return;
        try
        {
            var risultato = await OrdinaValoriWindow.Apri(owner, campo.Dimensione.Etichetta, valori.ToList());
            if (risultato != null) vm.ImpostaOrdineCampo(campo, risultato);
        }
        catch (Exception ex) { CrashLogService.Registra("Ordine campo matrice report", ex); }
    }

    /// <summary>Stesso schema di VeicoliView.OnImportaExcel: picker nativo di salvataggio invece del vecchio
    /// salvataggio automatico a percorso fisso (Addendum: grill-me 2026-08-18, follow-up — l'utente vuole
    /// scegliere sempre dove va a finire il file).</summary>
    private async void OnEsportaExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            // Dialogo unico di export: sezioni, colonne e filtri (che partono da quelli a video e, confermando, restano applicati alla pagina).
            if (topLevel is not Window finestraProprietaria) return;
            var opzioni = await EsportaOpzioniDialogo.Apri(finestraProprietaria, vm.StatoPerExport, vm.CreaDescrittoreExport());
            if (opzioni == null) return;
            vm.SceltaExportCorrente = opzioni;
            await vm.AttendiAggiornamentoAsync(); // i filtri scelti nel dialogo hanno riallineato la pagina
            // La scelta usa i filtri correnti e si presenta solo quando la selezione d'origine supera la soglia.
            using var preflight = new CancellationTokenSource();
            var (filtro, configurazione, origine, decisive) = await PreparaOrigineConAnnullamento(topLevel, vm, preflight);
            var righe = origine.TotaleCompleto;
            var soglia = ReportOrigineExportService.LimiteRigheAggiuntive;
            var includeTutto = false;
            var tagliaAlTetto = false;
            if (RichiedeSceltaLimiteExport(righe, decisive, soglia))
            {
                var scelta = await MostraSceltaExportGrande(topLevel, righe, decisive, soglia, preflight);
                if (SceltaExportAnnullata(scelta)) return;
                includeTutto = scelta == 1;
                tagliaAlTetto = scelta == 0;
            }
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance.Traduci("ReportSalvaExcelTitolo"),
                SuggestedFileName = $"TestLoom_Report_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = includeTutto ? "zip" : "xlsx",
                FileTypeChoices = includeTutto
                    ? new[] { new FilePickerFileType(LocalizationService.Instance.Traduci("ReportExportZipFiltro")) { Patterns = new[] { "*.zip" } } }
                    : new[] { new FilePickerFileType("Excel") { Patterns = new[] { "*.xlsx" } } }
            });

            var percorso = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(percorso))
            {
                if (includeTutto) await vm.EsportaExcelPartizionatoVerso(percorso, filtro, configurazione, origine, preparaCompleto: true);
                else await vm.EsportaExcelPreparatoVerso(percorso, filtro, configurazione, origine, tagliaAlTetto);
            }
        }
        catch (OperationCanceledException)
        {
            vm.Messaggio = LocalizationService.Instance.Traduci("EsportazioneAnnullata");
            vm.MessaggioErrore = null;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Esportazione report Excel", ex);
            SegnalaErroreExportReport(vm, ex);
        }
        finally { vm.SceltaExportCorrente = null; }
    }

    private static async Task<(ReportFilter Filtro, ConfigurazioneMatriceReport Configurazione, ReportOrigineExportService.OriginePreparata Origine, int Decisive)> PreparaOrigineConAnnullamento(
        TopLevel topLevel, ReportViewModel vm, CancellationTokenSource cancellation)
    {
        async Task<(ReportFilter, ConfigurazioneMatriceReport, ReportOrigineExportService.OriginePreparata, int)> PreparaETrovaDecisive()
        {
            var (filtro, configurazione, origine) = await vm.PreparaOrigineExportAsync(cancellation.Token);
            var decisive = await vm.CalcolaConteggioRigheDecisiveAsync(origine, configurazione, cancellation.Token);
            return (filtro, configurazione, origine, decisive);
        }
        var lavoro = PreparaETrovaDecisive();
        if (topLevel is not Window owner) return await lavoro;

        var dialogo = new Window
        {
            Title = LocalizationService.Instance.Traduci("EsportaExcelBtn"), Width = 390,
            SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        AutomationProperties.SetAutomationId(dialogo, IdDialogoPreflightExport);
        var completamento = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var annulla = new Button { Content = LocalizationService.Instance.Traduci("Annulla"), MinWidth = 100,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        AutomationProperties.SetAutomationId(annulla, IdAnnullaPreflightExport);
        annulla.Click += (_, _) => { AnnullaPreflightExport(cancellation, () => completamento.TrySetResult(true)); dialogo.Close(); };
        dialogo.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = LocalizationService.Instance.Traduci("GenerazioneReportInCorso"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new ProgressBar { IsIndeterminate = true, Height = 5 }, annulla
            }
        };
        dialogo.Closed += (_, _) =>
        {
            if (!lavoro.IsCompleted) cancellation.Cancel();
            completamento.TrySetResult(true);
        };
        dialogo.Show(owner);
        var primo = await Task.WhenAny(lavoro, completamento.Task);
        if (primo == completamento.Task)
        {
            try { await lavoro; } catch (OperationCanceledException) { }
            throw new OperationCanceledException(cancellation.Token);
        }
        dialogo.Close();
        return await lavoro;
    }

    private static async Task<int?> MostraSceltaExportGrande(TopLevel topLevel, int righe, int decisive, int soglia, CancellationTokenSource preflight)
    {
        if (topLevel is not Window owner) return null;
        var dialogo = CreaDialogoSceltaExportGrande(righe, decisive, soglia, preflight);
        await dialogo.Finestra.ShowDialog(owner);
        return await dialogo.Risultato;
    }

    /// <summary>Factory interna del dialogo soglia. Mantiene testabile la finestra reale senza aprire picker o scrivere file.</summary>
    internal static DialogoSceltaExport CreaDialogoSceltaExportGrande(int righe, int decisive, int soglia,
        CancellationTokenSource preflight)
    {
        var dialog = new Window
        {
            Title = LocalizationService.Instance.Traduci("ReportExportSogliaTitolo"),
            Width = 540,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        AutomationProperties.SetAutomationId(dialog, IdDialogoExportGrande);
        var t = LocalizationService.Instance.Traduci("ReportExportSogliaTestoFmt");
        var aggiuntive = Math.Max(0, righe - decisive);
        var conteggi = ReportOrigineExportService.CalcolaConteggiLimitati(decisive, aggiuntive, soglia);
        var testo = string.Format(t, righe, decisive, soglia, conteggi.DeterminantiIncluse,
            conteggi.AggiuntiveIncluse, conteggi.DeterminantiEscluse, conteggi.AggiuntiveEscluse);
        var result = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pulsantiScelta = new Dictionary<int, Button>();
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        void Add(string key, int value)
        {
            var button = new Button { Content = LocalizationService.Instance.Traduci(key), MinWidth = 100 };
            AutomationProperties.SetAutomationId(button, IdScelteExportGrande[value]);
            button.Click += (_, _) => { result.TrySetResult(value); dialog.Close(); };
            buttons.Children.Add(button);
            pulsantiScelta.Add(value, button);
        }
        Add("ReportExportTroncaBtn", 0);
        Add("ReportExportCompletoBtn", 1);
        var annulla = new Button { Content = LocalizationService.Instance.Traduci("ReportExportAnnullaBtn"), MinWidth = 100 };
        AutomationProperties.SetAutomationId(annulla, IdScelteExportGrande[2]);
        annulla.Click += (_, _) => { AnnullaPreflightExport(preflight, () => result.TrySetResult(2)); dialog.Close(); };
        buttons.Children.Add(annulla);
        pulsantiScelta.Add(2, annulla);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 18,
            Children = { new TextBlock { Text = testo, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons }
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        return new DialogoSceltaExport(dialog, result.Task, pulsantiScelta);
    }

    private async void OnEsportaPdf(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance.Traduci("ReportSalvaPdfTitolo"),
                SuggestedFileName = $"TestLoom_Report_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "pdf",
                FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } }
            });

            var percorso = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(percorso)) await vm.EsportaPdfVerso(percorso);
        }
        catch (Exception ex) { CrashLogService.Registra("Esportazione report PDF", ex); }
    }
}

/// <summary>Legge la misura per chiave quando cambia la versione delle preferenze locali; così le celle già
/// materializzate si aggiornano senza ricreare la finestra virtualizzata.</summary>
public sealed class ReportDimensioneMatriceConverter : IMultiValueConverter
{
    public static readonly ReportDimensioneMatriceConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 3 || values[0] is not string chiave || values[2] is not ReportViewModel vm) return 0d;
        return parameter?.ToString() == "riga"
            ? vm.AltezzaPerRigaMatrice(chiave)
            : vm.LarghezzaPerColonnaMatrice(chiave);
    }
}

/// <summary>Formatta i nomi accessibili dei bordi di ridimensionamento con la lingua corrente e il nome della riga/colonna.</summary>
public sealed class ReportResizeNomeConverter : IMultiValueConverter
{
    public static readonly ReportResizeNomeConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not string identificatore) return "";
        var chiave = parameter?.ToString() == "riga" ? "ReportResizeRigaNomeFmt" : "ReportResizeColonnaNomeFmt";
        return string.Format(culture, LocalizationService.Instance.Traduci(chiave), identificatore);
    }
}
