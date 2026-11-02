using Avalonia.Controls;
using Avalonia.Interactivity;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>
/// Dialogo di consultazione e staging nell'editor: non scrive record condivisi e non esegue merge.
/// Ogni operazione I/O avviene fuori dal thread UI, evitando freeze con molte bozze.
/// </summary>
public partial class FailedSaveDraftsWindow : Window
{
    public sealed record EditorDraftSelection(
        FailedSaveDraftInspector.DraftRow Draft, IReadOnlyList<string> Fields);

    private readonly string _workspaceRoot;
    private readonly string? _domainFilter;
    private readonly Guid? _entityUidFilter;
    private readonly bool _editorSelectionMode;
    private readonly IReadOnlyDictionary<string, string>? _editorFieldCatalog;
    // Modalità "nuovo record": il predicato dice se un UID è già nel repository (record attivi e archiviati).
    private readonly Func<Guid, bool>? _nuovoRecordUidGiaPresente;
    private bool NuovoRecordMode => _nuovoRecordUidGiaPresente != null;
    private IReadOnlyList<FailedSaveDraftInspector.DraftRow> _rows =
        Array.Empty<FailedSaveDraftInspector.DraftRow>();
    private const int PageSize = 30;
    private int _skip;
    private bool _loading;

    public FailedSaveDraftsWindow() : this("") { }

    public FailedSaveDraftsWindow(string workspaceRoot, string? domainFilter = null,
        Guid? entityUidFilter = null, bool editorSelectionMode = false,
        IReadOnlyDictionary<string, string>? editorFieldCatalog = null,
        Func<Guid, bool>? nuovoRecordUidGiaPresente = null)
    {
        _workspaceRoot = workspaceRoot;
        _domainFilter = domainFilter;
        _entityUidFilter = entityUidFilter;
        _editorSelectionMode = editorSelectionMode;
        _editorFieldCatalog = editorFieldCatalog;
        _nuovoRecordUidGiaPresente = nuovoRecordUidGiaPresente;
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
        UseDraftButton.IsVisible = editorSelectionMode;
        FieldSelectionNotice.IsVisible = editorSelectionMode && !NuovoRecordMode;
        FieldSelectionPanel.IsVisible = editorSelectionMode && !NuovoRecordMode;
        if (NuovoRecordMode)
        {
            // Nuovo record: nessun confronto con una base e nessuna scelta per campo; si caricano tutti i campi modificabili.
            CompareButton.IsVisible = false;
            PrepareButton.IsVisible = false;
            UseDraftButton.Content = LocalizationService.Instance.Traduci("FailedDraftCaricaNuovoRecordBtn");
        }
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs args) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        if (_loading || string.IsNullOrWhiteSpace(_workspaceRoot)) return;
        _loading = true;
        PreviousButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        PreviewButton.IsEnabled = false;
        CompareButton.IsEnabled = false;
        PrepareButton.IsEnabled = false;
        UseDraftButton.IsEnabled = false;
        ComparisonText.Text = LocalizationService.Instance.Traduci("FsdConfrontoNonEffettuatoSyncTesto");
        DifferencesText.Text = LocalizationService.Instance.Traduci("FsdDifferenzeNonCalcolateTesto");
        CurrentText.Text = LocalizationService.Instance.Traduci("FsdConfrontoNonEffettuatoTesto");
        PreparedText.Text = LocalizationService.Instance.Traduci("FsdNessunaCopiaPreparataTesto");
        StatusText.Text = LocalizationService.Instance.Traduci("FsdLetturaBozzeTesto");
        try
        {
            var rows = await Task.Run(() => NuovoRecordMode
                ? FailedSaveDraftInspector.ListNewRecordPage(_workspaceRoot, _domainFilter!, _nuovoRecordUidGiaPresente!, _skip, PageSize)
                : FailedSaveDraftInspector.ListPage(
                _workspaceRoot, _skip, PageSize, domain: _domainFilter, entityUid: _entityUidFilter));
            _rows = rows;
            DraftList.ItemsSource = rows.Select(x => x.Label).ToArray();
            DraftList.SelectedIndex = -1;
            DetailText.Text = rows.Count == 0
                ? (_editorSelectionMode ? string.Format(LocalizationService.Instance.Traduci("FsdNessunaBozzaEditorFmt"), EditorEntityLabel) : LocalizationService.Instance.Traduci("FsdNessunaBozzaPaginaTesto"))
                : LocalizationService.Instance.Traduci("FsdSelezionaBozzaTesto");
            PreviewText.Text = LocalizationService.Instance.Traduci("FsdNessunaAnteprimaTesto");
            StatusText.Text = string.Format(LocalizationService.Instance.Traduci("FsdPaginaSoloLetturaFmt"), _skip / PageSize + 1, rows.Count);
            if (_editorSelectionMode)
                StatusText.Text = string.Format(LocalizationService.Instance.Traduci("FsdPaginaBozzeEditorFmt"), _skip / PageSize + 1, EditorEntityLabel);
            PreviousButton.IsEnabled = _skip > 0;
            NextButton.IsEnabled = rows.Count == PageSize;
        }
        catch (Exception ex)
        {
            _rows = Array.Empty<FailedSaveDraftInspector.DraftRow>();
            DraftList.ItemsSource = Array.Empty<string>();
            DetailText.Text = LocalizationService.Instance.Traduci("FsdImpossibileConsultarePrefisso") + ex.Message;
            StatusText.Text = LocalizationService.Instance.Traduci("FsdErroreLetturaTesto");
            CrashLogService.Registra("Elenco bozze di emergenza", ex);
        }
        finally { _loading = false; }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        var index = DraftList.SelectedIndex;
        FieldSelectionPanel.Children.Clear();
        if (index < 0 || index >= _rows.Count)
        {
            PreviewButton.IsEnabled = false;
            CompareButton.IsEnabled = false;
            PrepareButton.IsEnabled = false;
            UseDraftButton.IsEnabled = false;
            return;
        }
        var row = _rows[index];
        DetailText.Text = row.Details;
        PreviewText.Text = row.IsReadable
            ? LocalizationService.Instance.Traduci("FsdAnteprimaNonCaricataTesto")
            : LocalizationService.Instance.Traduci("FsdBozzaNonLeggibileTesto");
        PreviewButton.IsEnabled = row.IsReadable;
        CompareButton.IsEnabled = row.IsReadable && !NuovoRecordMode;
        PrepareButton.IsEnabled = row.IsReadable && !NuovoRecordMode;
        ComparisonText.Text = LocalizationService.Instance.Traduci("FsdConfrontoNonEffettuatoSyncTesto");
        DifferencesText.Text = LocalizationService.Instance.Traduci("FsdDifferenzeNonCalcolateTesto");
        CurrentText.Text = LocalizationService.Instance.Traduci("FsdConfrontoNonEffettuatoTesto");
        PreparedText.Text = LocalizationService.Instance.Traduci("FsdNessunaCopiaPreparataTesto");
        FieldSelectionNotice.Text = LocalizationService.Instance.Traduci("FsdEseguiConfrontoTesto");
        UpdateUseDraftEnabled();
    }

    /// <summary>
    /// Returns a selected CR or NC draft to the editor only after a fresh local comparison and explicit
    /// confirmation. The editor revalidates the draft and exact currently opened canonical snapshot before staging.
    /// </summary>
    private async void OnUseDraftInEditor(object? sender, RoutedEventArgs e)
    {
        var index = DraftList.SelectedIndex;
        if (NuovoRecordMode && _editorSelectionMode && !_loading && index >= 0 && index < _rows.Count)
        {
            await ConfermaNuovoRecordAsync(index, _rows[index]);
            return;
        }
        if (!_editorSelectionMode || _loading || index < 0 || index >= _rows.Count) return;
        var row = _rows[index];
        if (!row.IsReadable || row.Domain is not ("change-request" or "nonconformita" or "segnalazione") ||
            row.Domain == "segnalazione" && _domainFilter != "nonconformita") return;
        UseDraftButton.IsEnabled = false;
        try
        {
            var comparison = await Task.Run(() =>
                FailedSaveDraftComparisonService.Compare(_workspaceRoot, row));
            if (DraftList.SelectedIndex != index || index >= _rows.Count ||
                !string.Equals(_rows[index].FilePath, row.FilePath, StringComparison.Ordinal))
                return;
            ComparisonText.Text = comparison.StateLabel + " — " + comparison.Explanation;
            DifferencesText.Text = (comparison.ThreeWaySummary == null ? "" :
                comparison.ThreeWaySummary + Environment.NewLine + Environment.NewLine) +
                (comparison.Differences?.Details ?? LocalizationService.Instance.Traduci("FsdDifferenzeNonDisponibiliTesto"));
            PreviewText.Text = comparison.DraftPreview;
            CurrentText.Text = comparison.LocalPreview;
            if (comparison.State != FailedSaveDraftComparisonService.ComparisonState.DifferentLocal)
            {
                PreparedText.Text = LocalizationService.Instance.Traduci("FsdBozzaNonCaricataConfrontoTesto");
                return;
            }
            var fieldDomain = SelectionFieldDomain(row);
            var changedFields = EditableChangedFields(comparison.RecoverableFields, fieldDomain,
                FieldCatalog(fieldDomain));
            if (comparison.Differences?.Truncated != false || changedFields.Count == 0)
            {
                PreparedText.Text = LocalizationService.Instance.Traduci("FsdNessunCampoSicuroTesto");
                return;
            }
            var selectedFields = SelectedFieldKeys();
            if (selectedFields.Count == 0 || !selectedFields.All(changedFields.Contains))
            {
                ConfigureFieldSelection(changedFields);
                PreparedText.Text = LocalizationService.Instance.Traduci("FsdBozzaCambiataTesto");
                return;
            }
            if (ConfirmService.Handler == null)
            {
                PreparedText.Text = LocalizationService.Instance.Traduci("FsdImportNonDisponibileTesto");
                return;
            }
            var confirmed = await ConfirmService.Chiedi(
                LocalizationService.Instance.Traduci("FsdCaricaBozzaNellaPrefisso") + EditorEntityLabel,
                LocalizationService.Instance.Traduci("FsdCaricareCampiPrefisso") + string.Join(", ", selectedFields.Select(f => FieldLabel(f, fieldDomain))) +
                LocalizationService.Instance.Traduci("FsdNessunFileModificatoSuffisso") +
                LocalizationService.Instance.Traduci("FsdSalvataggioSuccessivoTesto"),
                LocalizationService.Instance.Traduci("FsdCaricaCampiSceltiBtn"), LocalizationService.Instance.Traduci("Annulla"));
            if (!confirmed)
            {
                PreparedText.Text = LocalizationService.Instance.Traduci("FsdImportAnnullataTesto");
                return;
            }
            if (DraftList.SelectedIndex != index || index >= _rows.Count ||
                !string.Equals(_rows[index].FilePath, row.FilePath, StringComparison.Ordinal))
            {
                PreparedText.Text = LocalizationService.Instance.Traduci("FsdSelezioneCambiataConfrontoTesto");
                return;
            }
            Close(new EditorDraftSelection(row, selectedFields));
        }
        catch (Exception ex)
        {
            PreparedText.Text = LocalizationService.Instance.Traduci("FsdImportInterrottaPrefisso") + ex.Message;
            CrashLogService.Registra("Selezione bozza per recupero editor", ex);
        }
        finally
        {
            UpdateUseDraftEnabled();
        }
    }

    /// <summary>
    /// Bozza di record mai salvato: nessuna scelta per campo. Dopo conferma restituisce la bozza con tutti i campi
    /// modificabili; l'editor chiamante li carica senza salvare nulla.
    /// </summary>
    private async Task ConfermaNuovoRecordAsync(int index, FailedSaveDraftInspector.DraftRow row)
    {
        if (!row.IsReadable)
        {
            PreparedText.Text = LocalizationService.Instance.Traduci("FsdBozzaNonLeggibileTesto");
            return;
        }
        if (ConfirmService.Handler == null)
        {
            PreparedText.Text = LocalizationService.Instance.Traduci("FsdImportNonDisponibileTesto");
            return;
        }
        UseDraftButton.IsEnabled = false;
        try
        {
            var confirmed = await ConfirmService.Chiedi(
                LocalizationService.Instance.Traduci("FsdCaricaBozzaNuovoRecordTitolo"),
                LocalizationService.Instance.Traduci("FsdCaricareTuttiCampiTesto") +
                LocalizationService.Instance.Traduci("FsdSalvataggioNuovoIdTesto"),
                LocalizationService.Instance.Traduci("FsdCaricaBozzaBtn"), LocalizationService.Instance.Traduci("Annulla"));
            if (!confirmed)
            {
                PreparedText.Text = LocalizationService.Instance.Traduci("FsdImportAnnullataTesto");
                return;
            }
            if (DraftList.SelectedIndex != index || index >= _rows.Count ||
                !string.Equals(_rows[index].FilePath, row.FilePath, StringComparison.Ordinal))
            {
                PreparedText.Text = LocalizationService.Instance.Traduci("FsdSelezioneCambiataRiprovareTesto");
                return;
            }
            Close(new EditorDraftSelection(row, Array.Empty<string>()));
        }
        catch (Exception ex)
        {
            PreparedText.Text = LocalizationService.Instance.Traduci("FsdImportInterrottaPrefisso") + ex.Message;
            CrashLogService.Registra("Selezione bozza per nuovo record", ex);
        }
        finally
        {
            UpdateUseDraftEnabled();
        }
    }

    /// <summary>
    /// Confronto SOLO CONSULTIVO con il file canonico locale. Non invoca Save,
    /// RefreshIndex, Restore o API OneDrive. La scelta della versione resta manuale.
    /// </summary>
    private async void OnCompare(object? sender, RoutedEventArgs e)
    {
        var index = DraftList.SelectedIndex;
        if (index < 0 || index >= _rows.Count || _loading) return;
        var row = _rows[index];
        if (!row.IsReadable) return;
        CompareButton.IsEnabled = false;
        PrepareButton.IsEnabled = false;
        UseDraftButton.IsEnabled = false;
        FieldSelectionPanel.Children.Clear();
        ComparisonText.Text = LocalizationService.Instance.Traduci("FsdConfrontoInCorsoTesto");
        try
        {
            var result = await Task.Run(() =>
                FailedSaveDraftComparisonService.Compare(_workspaceRoot, row));
            if (DraftList.SelectedIndex != index || index >= _rows.Count ||
                !string.Equals(_rows[index].FilePath, row.FilePath, StringComparison.Ordinal))
                return;
            ComparisonText.Text = result.StateLabel + " — " + result.Explanation;
            DifferencesText.Text = (result.ThreeWaySummary == null
                    ? ""
                    : result.ThreeWaySummary + Environment.NewLine + Environment.NewLine) +
                (result.Differences?.Details ??
                LocalizationService.Instance.Traduci("FsdDifferenzeNonDispAvvisiTesto"));
            // Il payload letto per il confronto appartiene alla versione selezionata:
            // non usare una precedente anteprima eventualmente ancora in caricamento.
            PreviewText.Text = string.IsNullOrEmpty(result.DraftPreview)
                ? LocalizationService.Instance.Traduci("FsdAnteprimaNonDispConfrontoTesto") : result.DraftPreview;
            CurrentText.Text = string.IsNullOrEmpty(result.LocalPreview)
                ? LocalizationService.Instance.Traduci("FsdCanonicoNonDispTesto")
                : result.LocalPreview;
            if (_editorSelectionMode)
            {
                var fields = result.State == FailedSaveDraftComparisonService.ComparisonState.DifferentLocal &&
                    result.Differences?.Truncated == false
                    ? EditableChangedFields(result.RecoverableFields, SelectionFieldDomain(row),
                        FieldCatalog(SelectionFieldDomain(row))) : Array.Empty<string>();
                ConfigureFieldSelection(fields);
                PreparedText.Text = fields.Count == 0
                    ? LocalizationService.Instance.Traduci("FsdNessunCampoRilevatoTesto")
                    : LocalizationService.Instance.Traduci("FsdSceglieCampiTesto");
            }
        }
        catch (Exception ex)
        {
            if (DraftList.SelectedIndex == index && index < _rows.Count &&
                string.Equals(_rows[index].FilePath, row.FilePath, StringComparison.Ordinal))
                ComparisonText.Text = LocalizationService.Instance.Traduci("FsdConfrontoInterrottoPrefisso") + ex.Message;
            CrashLogService.Registra("Confronto non mutante bozza e file canonico", ex);
        }
        finally
        {
            var selected = DraftList.SelectedIndex;
            CompareButton.IsEnabled = selected >= 0 && selected < _rows.Count &&
                _rows[selected].IsReadable;
        }
    }

    private void ConfigureFieldSelection(IReadOnlyList<string> fields)
    {
        FieldSelectionPanel.Children.Clear();
        if (!_editorSelectionMode) return;
        var index = DraftList.SelectedIndex;
        var domain = index >= 0 && index < _rows.Count
            ? SelectionFieldDomain(_rows[index]) : "";
        foreach (var field in fields)
        {
            var checkBox = new CheckBox
            {
                Content = FieldLabel(field, domain),
                Tag = field,
                IsChecked = false,
                Margin = new Avalonia.Thickness(0, 0, 10, 4)
            };
            checkBox.IsCheckedChanged += OnFieldSelectionChanged;
            FieldSelectionPanel.Children.Add(checkBox);
        }
        FieldSelectionNotice.Text = fields.Count == 0
            ? "Nessun campo modificabile selezionabile. UID, ID, revisioni, changelog e audit restano canonici."
            : "Seleziona i campi da caricare. UID, ID, revisioni, changelog e audit restano canonici.";
        UpdateUseDraftEnabled();
    }

    private void OnFieldSelectionChanged(object? sender, RoutedEventArgs e) => UpdateUseDraftEnabled();

    private void UpdateUseDraftEnabled()
    {
        UseDraftButton.IsEnabled = _editorSelectionMode && DraftList.SelectedIndex >= 0 &&
            DraftList.SelectedIndex < _rows.Count && _rows[DraftList.SelectedIndex].IsReadable &&
            (NuovoRecordMode || SelectedFieldKeys().Count > 0);
    }

    private IReadOnlyList<string> SelectedFieldKeys() => FieldSelectionPanel.Children
        .OfType<CheckBox>().Where(x => x.IsChecked == true && x.Tag is string)
        .Select(x => (string)x.Tag!).Distinct(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> EditableChangedFields(
        IReadOnlyList<string>? candidateFields, string domain,
        IReadOnlyDictionary<string, string> catalog)
    {
        if (candidateFields == null) return Array.Empty<string>();
        var changed = candidateFields.Where(catalog.ContainsKey).Distinct(StringComparer.Ordinal);
        return changed.OrderBy(field => catalog.GetValueOrDefault(field, field),
            StringComparer.CurrentCulture).ToArray();
    }

    private IReadOnlyDictionary<string, string> FieldCatalog(string domain) => _editorFieldCatalog ?? (domain switch
    {
        "change-request" => FailedSaveDraftRecoveryPreparationService.EditableChangeRequestFields,
        "nonconformita" => FailedSaveDraftRecoveryPreparationService.EditableNetworkNcFields,
        _ => new Dictionary<string, string>()
    });

    private string FieldLabel(string field, string domain) =>
        FieldCatalog(domain).GetValueOrDefault(field, field);

    private string EditorEntityLabel => _domainFilter switch
    {
        "change-request" => LocalizationService.Instance.Traduci("EditorEtichettaCrTesto"),
        "nonconformita" => LocalizationService.Instance.Traduci("EditorEtichettaNcTesto"),
        _ => LocalizationService.Instance.Traduci("EditorEtichettaSchedaTesto")
    };

    private string SelectionFieldDomain(FailedSaveDraftInspector.DraftRow row) =>
        row.Domain == "segnalazione" && _domainFilter == "nonconformita"
            ? "nonconformita" : row.Domain;

    /// <summary>
    /// On demand: local-only, create-only preparation after explicit UI consent.
    /// It does NOT restore a record or choose between competing OneDrive versions.
    /// </summary>
    private async void OnPrepare(object? sender, RoutedEventArgs e)
    {
        var index = DraftList.SelectedIndex;
        if (_loading || index < 0 || index >= _rows.Count) return;
        var row = _rows[index];
        if (!row.IsReadable) return;
        // ConfirmService defaults to YES in headless tests; that default must
        // NEVER be treated as a real user's consent for preparing recovery.
        if (ConfirmService.Handler == null)
        {
            PreparedText.Text = LocalizationService.Instance.Traduci("FsdOperazioneNonDispTesto");
            return;
        }
        var confirmed = await ConfirmService.Chiedi(
            LocalizationService.Instance.Traduci("FsdPreparazioneTitolo"),
            LocalizationService.Instance.Traduci("FsdCreareCopiaTesto") +
            LocalizationService.Instance.Traduci("FsdNessunRecordOneDriveTesto"),
            LocalizationService.Instance.Traduci("FsdPreparaCopiaBtn"), LocalizationService.Instance.Traduci("Annulla"));
        if (!confirmed)
        {
            PreparedText.Text = LocalizationService.Instance.Traduci("FsdPreparazioneAnnullataTesto");
            return;
        }
        if (index != DraftList.SelectedIndex || index >= _rows.Count ||
            _rows[index].FilePath != row.FilePath)
        {
            PreparedText.Text = LocalizationService.Instance.Traduci("FsdSelezioneCambiataVerificaTesto");
            return;
        }
        PrepareButton.IsEnabled = false;
        try
        {
            var candidate = await Task.Run(() =>
                FailedSaveDraftRecoveryPreparationService.Prepare(_workspaceRoot, row, true));
            PreparedText.Text = LocalizationService.Instance.Traduci("FsdCopiaLocaleNonApplicataPrefisso") + candidate.LocalCandidatePath +
                LocalizationService.Instance.Traduci("FsdRichiedeVerificaSuffisso");
        }
        catch (Exception ex)
        {
            PreparedText.Text = LocalizationService.Instance.Traduci("FsdPreparazioneNonRiuscitaPrefisso") + ex.Message;
            CrashLogService.Registra("Preparazione locale bozza senza publish", ex);
        }
        finally
        {
            var selected = DraftList.SelectedIndex;
            PrepareButton.IsEnabled = selected >= 0 && selected < _rows.Count &&
                _rows[selected].IsReadable;
        }
    }

    private async void OnPreview(object? sender, RoutedEventArgs e)
    {
        var index = DraftList.SelectedIndex;
        if (index < 0 || index >= _rows.Count || _loading) return;
        var row = _rows[index];
        PreviewButton.IsEnabled = false;
        try
        {
            var preview = await Task.Run(() => FailedSaveDraftInspector.Preview(_workspaceRoot, row));
            // Durante la lettura asincrona l'utente può selezionare un altro file:
            // non mostrare il JSON di una bozza sotto i dettagli di una bozza diversa.
            if (index == DraftList.SelectedIndex && index < _rows.Count &&
                string.Equals(_rows[index].FilePath, row.FilePath, StringComparison.Ordinal))
                PreviewText.Text = preview;
        }
        catch (Exception ex)
        {
            PreviewText.Text = LocalizationService.Instance.Traduci("FsdAnteprimaNonDispPrefisso") + ex.Message;
            CrashLogService.Registra("Anteprima bozza di emergenza", ex);
        }
        finally
        {
            var selected = DraftList.SelectedIndex;
            PreviewButton.IsEnabled = selected >= 0 && selected < _rows.Count &&
                _rows[selected].IsReadable;
        }
    }

    private async void OnPrevious(object? sender, RoutedEventArgs args)
    {
        if (_loading || _skip == 0) return;
        _skip = Math.Max(0, _skip - PageSize);
        await ReloadAsync();
    }

    private async void OnNext(object? sender, RoutedEventArgs args)
    {
        if (_loading || _rows.Count < PageSize) return;
        _skip += PageSize;
        await ReloadAsync();
    }

    private async void OnRefresh(object? sender, RoutedEventArgs args) => await ReloadAsync();
    private void OnClose(object? sender, RoutedEventArgs args) => Close();
}
