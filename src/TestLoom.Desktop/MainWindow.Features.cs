using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Text;
using TestLoom.Core;

namespace TestLoom.Desktop;

/// <summary>
/// The tabbed layout and the features built on top of the basic workspace: language, priorities and tags,
/// template fields, step-by-step runs with evidence, plans, templates, CSV import, matrix export and audit trail.
/// </summary>
public sealed partial class MainWindow
{
    // ----- settings / first run -----
    private readonly ComboBox _languagePicker = new() { ItemsSource = new[] { "English", "Italiano" }, MinWidth = 160 };
    private readonly TextBox _defaultTester = new() { Watermark = "Your name (recorded on test runs)" };
    private readonly Button _saveSettings = new() { Content = "Save settings" };
    private readonly Button _restoreBackup = new() { Content = "Restore workspace from backup (ZIP)" };

    // ----- test metadata -----
    private readonly ComboBox _newPriority = new() { MinWidth = 180 };
    private readonly ComboBox _editPriority = new() { MinWidth = 180 };
    private readonly TextBox _editOwner = new() { Watermark = "Owner (optional)" };
    private readonly TextBox _editTags = new() { Watermark = "Tags, comma separated" };
    private readonly StackPanel _fieldsPanel = new() { Spacing = 6 };
    private readonly TextBlock _fieldsHint = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly List<(string Key, TextBox Box)> _fieldBoxes = new();
    private readonly Button _cloneTest = new() { Content = "Duplicate test" };
    private readonly ComboBox _priorityFilter = new() { MinWidth = 180 };
    private readonly ComboBox _planFilter = new() { MinWidth = 220 };
    private readonly TextBox _tagFilter = new() { Watermark = "Filter by tag" };

    // ----- execution -----
    private readonly TextBlock _runTarget = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _runTester = new() { Watermark = "Tester" };
    private readonly TextBox _runEnvironment = new() { Watermark = "Environment (optional)" };
    private readonly TextBox _runBuild = new() { Watermark = "Version or build (optional)" };
    private readonly TextBox _runMinutes = new() { Watermark = "Duration in minutes (optional)" };
    private readonly ListBox _runStepList = new() { Height = 150 };
    private readonly Button _stepPass = new() { Content = "Step passed" };
    private readonly Button _stepFail = new() { Content = "Step failed" };
    private readonly Button _stepBlock = new() { Content = "Step blocked" };
    private readonly Button _finishStepRun = new() { Content = "Finish step-by-step run" };
    private readonly Button _resetStepRun = new() { Content = "Reset step results" };
    private readonly Button _attachEvidence = new() { Content = "Attach evidence file…" };
    private readonly Button _clearEvidence = new() { Content = "Discard attached files" };
    private readonly ListBox _pendingEvidence = new() { Height = 70 };
    private readonly Dictionary<Guid, WorkspaceStepResult> _stepResults = new();
    private readonly List<WorkspaceEvidence> _pending = new();
    private Guid? _pendingFor;

    // ----- plans -----
    private readonly ListBox _planList = new() { Height = 120 };
    private readonly TextBox _planName = new() { Watermark = "Plan name" };
    private readonly TextBox _planDescription = new() { Watermark = "Description (optional)" };
    private readonly Button _addPlan = new() { Content = "Create plan" };
    private readonly Button _deletePlan = new() { Content = "Delete selected plan" };
    private readonly Button _addToPlan = new() { Content = "Add selected test to plan" };
    private readonly Button _removeFromPlan = new() { Content = "Remove selected test from plan" };
    private readonly TextBlock _planProgress = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _planMembers = new() { Height = 150 };

    // ----- templates -----
    private readonly ListBox _templateList = new() { Height = 110 };
    private readonly TextBox _newTemplateName = new() { Watermark = "New template name" };
    private readonly Button _addTemplate = new() { Content = "Create template" };
    private readonly Button _deleteTemplate = new() { Content = "Delete selected template" };
    private readonly ListBox _templateFieldList = new() { Height = 130 };
    private readonly TextBox _fieldSection = new() { Watermark = "Section" };
    private readonly TextBox _fieldName = new() { Watermark = "Field name" };
    private readonly CheckBox _fieldRequired = new() { Content = "Required before a run can be recorded" };
    private readonly Button _addField = new() { Content = "Add field" };
    private readonly Button _removeField = new() { Content = "Remove selected field" };

    // ----- reports / import / audit -----
    private readonly Button _exportMatrix = new() { Content = "Export traceability matrix (CSV)" };
    private readonly Button _importRequirements = new() { Content = "Import requirements (CSV)" };
    private readonly Button _importTests = new() { Content = "Import tests (CSV)" };
    private readonly ListBox _auditList = new() { Height = 320 };
    private readonly TextBlock _auditSummary = new() { TextWrapping = TextWrapping.Wrap };

    private string _uiLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en";
    private bool _applyingLanguage;
    private bool _firstRunShown;
    private WorkspaceAuditEntry? _lastAudit;
    private readonly List<(Control Control, string Property, string English)> _texts = new();
    private readonly List<Panel> _roots = new();
    private readonly List<TabItem> _tabs = new();

    private string Lang => _workspace?.Language ?? _uiLanguage;
    private string L(string english) => WorkspaceLocalization.T(english, Lang);
    private string StoreDirectory => Path.GetDirectoryName(_store.FilePath)!;

    // =====================================================================================================
    // Layout
    // =====================================================================================================

    private void InitializeFeatures()
    {
        _languagePicker.SelectionChanged += (_, _) => ChangeLanguage();
        _saveSettings.Click += (_, _) => SaveSettings();
        _restoreBackup.Click += async (_, _) => await RestoreBackupAsync();
        _cloneTest.Click += (_, _) => CloneSelectedTest();
        _priorityFilter.SelectionChanged += (_, _) => Refresh();
        _planFilter.SelectionChanged += (_, _) => Refresh();
        _tagFilter.TextChanged += (_, _) => Refresh();

        _runStepList.SelectionChanged += (_, _) => UpdateRunButtons();
        _stepPass.Click += (_, _) => RecordStepResult(ExecutionResult.Passed);
        _stepFail.Click += (_, _) => RecordStepResult(ExecutionResult.Failed);
        _stepBlock.Click += (_, _) => RecordStepResult(ExecutionResult.Blocked);
        _finishStepRun.Click += (_, _) => FinishStepRun();
        _resetStepRun.Click += (_, _) => { _stepResults.Clear(); RenderRunSteps(); };
        _attachEvidence.Click += async (_, _) => await AttachEvidenceAsync();
        _clearEvidence.Click += (_, _) => DiscardPending();

        _planList.SelectionChanged += (_, _) => RenderPlanSelection();
        _addPlan.Click += (_, _) => AddPlan();
        _deletePlan.Click += (_, _) => DeletePlan();
        _addToPlan.Click += (_, _) => ChangePlanMembership(include: true);
        _removeFromPlan.Click += (_, _) => ChangePlanMembership(include: false);

        _templateList.SelectionChanged += (_, _) => RenderTemplateSelection();
        _addTemplate.Click += (_, _) => AddTemplate();
        _deleteTemplate.Click += (_, _) => DeleteTemplate();
        _addField.Click += (_, _) => AddTemplateField();
        _removeField.Click += (_, _) => RemoveTemplateField();
        _templateFieldList.SelectionChanged += (_, _) => _removeField.IsEnabled = _templateFieldList.SelectedItem is not null;

        _exportMatrix.Click += async (_, _) => await ExportFileAsync("Export traceability matrix", "testloom-traceability.csv", "csv",
            "CSV file", () => TraceabilityMatrix.ExportCsv(_workspace!), "Traceability matrix exported.");
        _importRequirements.Click += async (_, _) => await ImportCsvAsync(requirements: true);
        _importTests.Click += async (_, _) => await ImportCsvAsync(requirements: false);
    }

    private Control CreateInterface()
    {
        var tabs = new TabControl();

        // Workspace
        var workspace = NewRoot();
        workspace.Children.Add(new TextBlock { Text = "TestLoom", FontSize = 32, FontWeight = FontWeight.Bold });
        workspace.Children.Add(new TextBlock { Text = "An independent workspace for planning, running and recording tests.", Opacity = 0.8 });
        workspace.Children.Add(SectionHeading("Workspace"));
        workspace.Children.Add(_workspaceSummary);
        workspace.Children.Add(_workspaceName);
        workspace.Children.Add(_presetPicker);
        workspace.Children.Add(_createWorkspace);
        workspace.Children.Add(SectionHeading("Settings"));
        workspace.Children.Add(new TextBlock { Text = "Language" });
        workspace.Children.Add(_languagePicker);
        workspace.Children.Add(_defaultTester);
        workspace.Children.Add(_saveSettings);
        workspace.Children.Add(_restoreBackup);
        workspace.Children.Add(_reloadWorkspace);
        workspace.Children.Add(_recoverWorkspace);
        workspace.Children.Add(_about);
        AddTab(tabs, "Workspace", workspace);

        // Requirements
        var requirementPanel = NewRoot();
        requirementPanel.Children.Add(SectionHeading("Requirements & traceability"));
        requirementPanel.Children.Add(_coverageSummary);
        requirementPanel.Children.Add(_requirementKey);
        requirementPanel.Children.Add(_requirementTitle);
        requirementPanel.Children.Add(_addRequirement);
        requirementPanel.Children.Add(_requirementList);
        requirementPanel.Children.Add(_editRequirementKey);
        requirementPanel.Children.Add(_editRequirementTitle);
        requirementPanel.Children.Add(_saveRequirementEdits);
        AddTab(tabs, "Requirements", requirementPanel);

        // Tests
        var tests = NewRoot();
        tests.Children.Add(SectionHeading("New test case"));
        tests.Children.Add(_testTitle);
        tests.Children.Add(_testDescription);
        tests.Children.Add(_testTemplate);
        tests.Children.Add(new TextBlock { Text = "Priority" });
        tests.Children.Add(_newPriority);
        tests.Children.Add(new TextBlock { Text = "Link to requirement (optional)" });
        tests.Children.Add(_testRequirement);
        tests.Children.Add(_addTest);
        tests.Children.Add(SectionHeading("Tests and execution history"));
        tests.Children.Add(_search);
        tests.Children.Add(_outcomeFilter);
        tests.Children.Add(_priorityFilter);
        tests.Children.Add(_planFilter);
        tests.Children.Add(_tagFilter);
        tests.Children.Add(_showArchived);
        tests.Children.Add(_caseList);
        tests.Children.Add(_caseDetails);
        tests.Children.Add(_editTitle);
        tests.Children.Add(_editDescription);
        tests.Children.Add(_editPriority);
        tests.Children.Add(_editOwner);
        tests.Children.Add(_editTags);
        tests.Children.Add(_fieldsHint);
        tests.Children.Add(_fieldsPanel);
        var editActions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _saveEdits, _cloneTest, _archiveToggle }) { b.Margin = new Thickness(0, 0, 10, 8); editActions.Children.Add(b); }
        tests.Children.Add(editActions);
        tests.Children.Add(SectionHeading("Requirement links for selected test"));
        tests.Children.Add(new TextBlock { Text = "Select a requirement to add or remove its link without changing the test history." });
        tests.Children.Add(_existingTestRequirement);
        var linkButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        linkButtons.Children.Add(_linkRequirement);
        linkButtons.Children.Add(_unlinkRequirement);
        tests.Children.Add(linkButtons);
        tests.Children.Add(SectionHeading("Ordered test steps"));
        tests.Children.Add(_stepList);
        tests.Children.Add(_editStepAction);
        tests.Children.Add(_editStepExpected);
        tests.Children.Add(_saveStep);
        tests.Children.Add(_newStepAction);
        tests.Children.Add(_newStepExpected);
        var stepActions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _addStep, _pasteSteps, _removeStep, _moveStepUp, _moveStepDown }) { b.Margin = new Thickness(0, 0, 10, 8); stepActions.Children.Add(b); }
        tests.Children.Add(stepActions);
        AddTab(tabs, "Tests", tests);

        // Execute
        var run = NewRoot();
        run.Children.Add(SectionHeading("Run the selected test"));
        run.Children.Add(_runTarget);
        run.Children.Add(_runTester);
        run.Children.Add(_runEnvironment);
        run.Children.Add(_runBuild);
        run.Children.Add(_runMinutes);
        run.Children.Add(_runNotes);
        var runButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _pass, _fail, _block }) { b.Margin = new Thickness(0, 0, 10, 8); runButtons.Children.Add(b); }
        run.Children.Add(new TextBlock { Text = "Record one overall result, or go through the steps below." });
        run.Children.Add(runButtons);
        run.Children.Add(SectionHeading("Step-by-step run"));
        run.Children.Add(_runStepList);
        var stepRunButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _stepPass, _stepFail, _stepBlock, _finishStepRun, _resetStepRun }) { b.Margin = new Thickness(0, 0, 10, 8); stepRunButtons.Children.Add(b); }
        run.Children.Add(stepRunButtons);
        run.Children.Add(SectionHeading("Evidence"));
        run.Children.Add(new TextBlock { Text = "Files attached here are saved with the next recorded run (screenshots, logs, measurements…).", TextWrapping = TextWrapping.Wrap });
        run.Children.Add(_pendingEvidence);
        var evidenceButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _attachEvidence, _clearEvidence }) { b.Margin = new Thickness(0, 0, 10, 8); evidenceButtons.Children.Add(b); }
        run.Children.Add(evidenceButtons);
        AddTab(tabs, "Execute", run);

        // Plans
        var plans = NewRoot();
        plans.Children.Add(SectionHeading("Test plans"));
        plans.Children.Add(_planName);
        plans.Children.Add(_planDescription);
        plans.Children.Add(_addPlan);
        plans.Children.Add(_planList);
        plans.Children.Add(_planProgress);
        plans.Children.Add(_planMembers);
        var planButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _addToPlan, _removeFromPlan, _deletePlan }) { b.Margin = new Thickness(0, 0, 10, 8); planButtons.Children.Add(b); }
        plans.Children.Add(planButtons);
        AddTab(tabs, "Plans", plans);

        // Templates
        var templates = NewRoot();
        templates.Children.Add(SectionHeading("Test templates"));
        templates.Children.Add(new TextBlock { Text = "A template defines the fields every test of that kind must describe.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        templates.Children.Add(_templateList);
        templates.Children.Add(_newTemplateName);
        var templateButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _addTemplate, _deleteTemplate }) { b.Margin = new Thickness(0, 0, 10, 8); templateButtons.Children.Add(b); }
        templates.Children.Add(templateButtons);
        templates.Children.Add(new TextBlock { Text = "Fields of the selected template" });
        templates.Children.Add(_templateFieldList);
        templates.Children.Add(_fieldSection);
        templates.Children.Add(_fieldName);
        templates.Children.Add(_fieldRequired);
        var fieldButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _addField, _removeField }) { b.Margin = new Thickness(0, 0, 10, 8); fieldButtons.Children.Add(b); }
        templates.Children.Add(fieldButtons);
        AddTab(tabs, "Templates", templates);

        // Defects
        var defectPanel = NewRoot();
        defectPanel.Children.Add(SectionHeading("Defects & nonconformities"));
        defectPanel.Children.Add(new TextBlock { Text = "Optionally select a test case in the Tests tab to link a new defect." });
        defectPanel.Children.Add(_defectKey);
        defectPanel.Children.Add(_defectTitle);
        defectPanel.Children.Add(_defectDescription);
        defectPanel.Children.Add(new TextBlock { Text = "Severity" });
        defectPanel.Children.Add(_defectSeverity);
        defectPanel.Children.Add(_addDefect);
        defectPanel.Children.Add(_defectList);
        defectPanel.Children.Add(new TextBlock { Text = "Edit selected defect" });
        defectPanel.Children.Add(_selectedDefectTitle);
        defectPanel.Children.Add(_selectedDefectDescription);
        defectPanel.Children.Add(_selectedDefectSeverity);
        defectPanel.Children.Add(_saveDefectDetails);
        defectPanel.Children.Add(new TextBlock { Text = "Selected defect status" });
        defectPanel.Children.Add(_selectedDefectStatus);
        defectPanel.Children.Add(_saveDefectStatus);
        AddTab(tabs, "Defects", defectPanel);

        // Reports
        var reports = NewRoot();
        reports.Children.Add(SectionHeading("Exports"));
        // Full-label actions must remain visible at the minimum supported window width, without clipping.
        var exportButtons = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8 };
        foreach (var b in new Button[] { _exportTests, _exportRequirements, _exportSteps, _exportDefects, _exportMatrix, _exportHtml, _backup })
            exportButtons.Children.Add(b);
        reports.Children.Add(exportButtons);
        reports.Children.Add(SectionHeading("Import"));
        reports.Children.Add(new TextBlock
        {
            Text = "CSV files use the same columns as the exports (comma or semicolon separated). Nothing is imported if any row is invalid.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8
        });
        reports.Children.Add(_importRequirements);
        reports.Children.Add(_importTests);
        AddTab(tabs, "Reports", reports);

        // Audit
        var audit = NewRoot();
        audit.Children.Add(SectionHeading("Audit trail"));
        audit.Children.Add(_auditSummary);
        audit.Children.Add(_auditList);
        AddTab(tabs, "Audit", audit);

        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(18, 6, 18, 10) };
        _status.Margin = new Thickness(0, 4, 0, 0);
        footer.Children.Add(_status);
        footer.Children.Add(new TextBlock { Text = "Local single-user preview · Data stored in a separate TestLoom profile", Opacity = 0.65, FontSize = 12 });
        _roots.Add(footer);

        var layout = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(tabs);
        return layout;
    }

    private StackPanel NewRoot() => new() { Spacing = 10, Margin = new Thickness(22) };

    private void AddTab(TabControl tabs, string header, StackPanel content)
    {
        _roots.Add(content);
        var item = new TabItem
        {
            Header = header,
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = content
            }
        };
        _tabs.Add(item);
        _tabHeaders[item] = header;
        tabs.Items.Add(item);
    }

    // =====================================================================================================
    // Language
    // =====================================================================================================

    private void CaptureTexts()
    {
        var skip = new HashSet<Control> { _status, _archiveToggle, _workspaceSummary, _coverageSummary, _caseDetails,
            _runTarget, _planProgress, _auditSummary, _fieldsHint };
        foreach (var root in _roots)
            foreach (var control in new Control[] { root }.Concat(root.GetLogicalDescendants().OfType<Control>()))
            {
                if (skip.Contains(control)) continue;
                switch (control)
                {
                    case TextBlock { Text.Length: > 0 } block: _texts.Add((block, nameof(TextBlock.Text), block.Text!)); break;
                    case ContentControl { Content: string content } cc: _texts.Add((cc, nameof(ContentControl.Content), content)); break;
                    case TextBox { Watermark.Length: > 0 } box: _texts.Add((box, nameof(TextBox.Watermark), box.Watermark!)); break;
                }
            }
    }

    private void ApplyLanguage()
    {
        _applyingLanguage = true;
        try
        {
            _languagePicker.SelectedIndex = Lang == "it" ? 1 : 0;
            Title = L("TestLoom · Test management");
            foreach (var (control, property, english) in _texts)
            {
                var text = L(english);
                switch (property)
                {
                    case nameof(TextBlock.Text): ((TextBlock)control).Text = text; break;
                    case nameof(ContentControl.Content): ((ContentControl)control).Content = text; break;
                    case nameof(TextBox.Watermark): ((TextBox)control).Watermark = text; break;
                }
            }
            foreach (var tab in _tabs) tab.Header = L(_tabHeaders[tab]);
            FillChoices();
        }
        finally { _applyingLanguage = false; }
        Refresh();
    }

    private readonly Dictionary<TabItem, string> _tabHeaders = new();

    private void FillChoices()
    {
        void Fill(ComboBox box, IEnumerable<string> items, int fallback)
        {
            var keep = box.SelectedIndex;
            box.ItemsSource = items.ToArray();
            box.SelectedIndex = keep >= 0 ? keep : fallback;
        }
        Fill(_outcomeFilter, new[] { "Any outcome", "Not run", "Passed", "Failed", "Blocked" }.Select(L), 0);
        var priorities = Enum.GetNames<WorkspacePriority>().Select(L).ToArray();
        Fill(_newPriority, priorities, (int)WorkspacePriority.Medium);
        Fill(_editPriority, priorities, (int)WorkspacePriority.Medium);
        Fill(_priorityFilter, new[] { "Any priority" }.Select(L).Concat(priorities), 0);
        Fill(_defectSeverity, Enum.GetNames<WorkspaceDefectSeverity>().Select(L), (int)WorkspaceDefectSeverity.Medium);
        Fill(_selectedDefectSeverity, Enum.GetNames<WorkspaceDefectSeverity>().Select(L), -1);
        Fill(_selectedDefectStatus, Enum.GetNames<WorkspaceDefectStatus>().Select(L), -1);
    }

    private void ChangeLanguage()
    {
        if (_applyingLanguage) return;
        var language = _languagePicker.SelectedIndex == 1 ? "it" : "en";
        if (_workspace is null) { _uiLanguage = language; ApplyLanguage(); return; }
        if (_workspace.Language == language) return;
        var previous = _workspace.Language;
        _workspace.Language = language;
        try { _store.Save(_workspace); ApplyLanguage(); SetStatus("Language changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _workspace.Language = previous;
            ApplyLanguage();
            SetStatus("The change was not saved: " + ex.Message);
        }
    }

    private void SaveSettings()
    {
        if (_workspace is null) return;
        var tester = (_defaultTester.Text ?? "").Trim();
        if (tester.Length > 120) { SetStatus("The name is too long."); return; }
        var previous = _workspace.DefaultTester;
        _workspace.DefaultTester = tester;
        try { _store.Save(_workspace); SetStatus("Settings saved."); Refresh(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _workspace.DefaultTester = previous;
            SetStatus("The change was not saved: " + ex.Message);
        }
    }

    private async Task OfferFirstRunAsync()
    {
        if (_workspace is not null || _initialLoadFailed || _firstRunShown) return;
        _firstRunShown = true;
        var choice = await new FirstRunWindow(_uiLanguage).ShowDialog<FirstRunChoice?>(this);
        if (choice is null) return;
        try
        {
            var workspace = WorkspaceStarter.Create(choice.Name, choice.PresetId, choice.Language, choice.IncludeSamples, choice.Tester);
            _store.Save(workspace);
            _workspace = workspace;
            _uiLanguage = workspace.Language;
            ApplyLanguage();
            SetStatus("Workspace created.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus("Cannot create workspace: " + ex.Message);
        }
    }

    private async Task RestoreBackupAsync()
    {
        if (_workspace is not null || _initialLoadFailed) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("Restore workspace from backup (ZIP)"), AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("ZIP backup") { Patterns = new[] { "*.zip" } } }
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            _workspace = WorkspaceBackup.Restore(path, StoreDirectory);
            _store.Load();
            _uiLanguage = _workspace.Language;
            ApplyLanguage();
            SetStatus("Workspace restored from backup.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or System.Text.Json.JsonException or NotSupportedException)
        {
            _workspace = null;
            SetStatus("Backup failed: " + ex.Message);
        }
    }

    // =====================================================================================================
    // Audit helpers
    // =====================================================================================================

    private void Audited(string action, string subject, string detail = "")
    {
        if (_workspace is null) return;
        WorkspaceAudit.Record(_workspace, _workspace.DefaultTester, action, subject, detail);
        _lastAudit = _workspace.Audit.LastOrDefault();
    }

    private void UndoAudit()
    {
        if (_workspace is not null && _lastAudit is not null) _workspace.Audit.Remove(_lastAudit);
        _lastAudit = null;
    }

    private string SummaryText(WorkspaceStatisticsReport s) =>
        $"{s.ActiveTests} {L("tests")} · {s.Passed} {L("passed")} · {s.Failed} {L("failed")} · {s.Blocked} {L("blocked")} · " +
        $"{s.NotRun} {L("not run")} · {L("pass rate")} {s.PassRatePercent:0.#}% · {L("coverage")} {s.RequirementCoveragePercent:0.#}% · " +
        $"{L("open defects")} {s.OpenDefects}";

    // =====================================================================================================
    // Refresh of the feature widgets
    // =====================================================================================================

    private void RefreshFeatures()
    {
        var active = _workspace is not null;
        _languagePicker.IsEnabled = true;
        _defaultTester.IsEnabled = active;
        _saveSettings.IsEnabled = active;
        if (active && !_defaultTester.IsFocused) _defaultTester.Text = _workspace!.DefaultTester;
        _restoreBackup.IsEnabled = !active && !_initialLoadFailed;
        _newPriority.IsEnabled = active;
        _importRequirements.IsEnabled = active;
        _importTests.IsEnabled = active;
        _exportMatrix.IsEnabled = active;
        _addPlan.IsEnabled = active;
        _planName.IsEnabled = active;
        _planDescription.IsEnabled = active;
        _newTemplateName.IsEnabled = active;
        _addTemplate.IsEnabled = active;

        // plan filter
        var plans = _workspace?.Plans.ToArray() ?? Array.Empty<WorkspaceTestPlan>();
        var planKeep = _planFilter.SelectedIndex;
        _applyingLanguage = true;
        _planFilter.ItemsSource = new[] { L("Any plan") }.Concat(plans.Select(p => p.Name)).ToArray();
        _planFilter.SelectedIndex = planKeep >= 0 && planKeep <= plans.Length ? planKeep : 0;
        _applyingLanguage = false;

        var selectedPlan = (_planList.SelectedItem as WorkspaceTestPlan)?.Id;
        _planList.ItemsSource = plans;
        if (selectedPlan.HasValue) _planList.SelectedItem = plans.FirstOrDefault(p => p.Id == selectedPlan.Value);
        RenderPlanSelection();

        var selectedTemplate = (_templateList.SelectedItem as WorkspaceTemplate)?.Id;
        var templates = _workspace?.Templates.ToArray() ?? Array.Empty<WorkspaceTemplate>();
        _templateList.ItemsSource = templates;
        if (selectedTemplate.HasValue) _templateList.SelectedItem = templates.FirstOrDefault(t => t.Id == selectedTemplate.Value);
        RenderTemplateSelection();

        var entries = _workspace?.Audit.AsEnumerable().Reverse().Take(300).Select(e =>
            $"{e.Utc:yyyy-MM-dd HH:mm} UTC · {e.Actor} · {e.Action} · {e.Subject}").ToArray() ?? Array.Empty<string>();
        _auditList.ItemsSource = entries;
        _auditSummary.Text = _workspace is null ? L("Create a workspace first.")
            : $"{_workspace.Audit.Count} {L("recorded changes")} ({L("newest first")})";
    }

    private void RenderFeatureSelection()
    {
        var test = _caseList.SelectedItem as WorkspaceTestCase;
        var editable = test is not null && !test.Archived;

        if (_pendingFor != test?.Id) DiscardPending();
        _stepResults.Clear();

        _editPriority.IsEnabled = editable;
        _editOwner.IsEnabled = editable;
        _editTags.IsEnabled = editable;
        _cloneTest.IsEnabled = test is not null;
        _editPriority.SelectedIndex = test is null ? -1 : (int)test.Priority;
        _editOwner.Text = test?.Owner ?? "";
        _editTags.Text = test is null ? "" : string.Join(", ", test.Tags);

        _fieldBoxes.Clear();
        _fieldsPanel.Children.Clear();
        var template = test is null || _workspace is null ? null : WorkspaceTemplates.Find(_workspace, test.TemplateName);
        _fieldsHint.Text = test is null ? "" : template is null ? L("This test uses no template with fields.")
            : L("Template fields") + ": " + template.Name;
        if (template is not null && test is not null)
            foreach (var (section, field, required, key) in WorkspaceTemplates.Fields(template))
            {
                var box = new TextBox
                {
                    Watermark = $"{section} · {field}{(required ? " *" : "")}",
                    Text = test.FieldValues.GetValueOrDefault(key, ""),
                    IsEnabled = editable, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 90
                };
                _fieldBoxes.Add((key, box));
                _fieldsPanel.Children.Add(box);
            }

        _runTarget.Text = test is null ? L("Select a test in the Tests tab first.")
            : $"{test.Title} · {test.Steps.Count} {L("steps")}";
        if (string.IsNullOrWhiteSpace(_runTester.Text)) _runTester.Text = _workspace?.DefaultTester ?? "";
        foreach (var box in new[] { _runTester, _runEnvironment, _runBuild, _runMinutes }) box.IsEnabled = editable;
        _attachEvidence.IsEnabled = editable;
        _clearEvidence.IsEnabled = editable && _pending.Count > 0;
        RenderRunSteps();
        RenderPending();
    }

    // =====================================================================================================
    // Test details
    // =====================================================================================================

    private void CloneSelectedTest()
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase source) return;
        var copy = TestCaseCloner.Clone(source, L("Copy of "));
        _workspace.TestCases.Add(copy);
        Audited("test.cloned", copy.Title, source.Id.ToString());
        try
        {
            _store.Save(_workspace);
            SetStatus("Test duplicated.");
            Refresh();
            _caseList.SelectedItem = copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _workspace.TestCases.Remove(copy);
            UndoAudit();
            SetStatus("The new test was not saved: " + ex.Message);
        }
    }

    private static List<string> ParseTags(string? text) =>
        (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // =====================================================================================================
    // Execution
    // =====================================================================================================

    private int ParseMinutes(out bool ok)
    {
        ok = true;
        var text = (_runMinutes.Text ?? "").Trim();
        if (text.Length == 0) return 0;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes <= 500_000)
            return minutes * 60;
        ok = false;
        return 0;
    }

    private WorkspaceExecution? NewExecution(WorkspaceTestCase test, ExecutionResult? result, IEnumerable<WorkspaceStepResult>? steps)
    {
        var seconds = ParseMinutes(out var ok);
        if (!ok) { SetStatus("Duration must be a whole number of minutes."); return null; }
        var missing = _workspace is null ? Array.Empty<string>()
            : WorkspaceTemplates.MissingRequired(test, WorkspaceTemplates.Find(_workspace, test.TemplateName));
        if (missing.Count > 0)
        {
            SetStatus(L("Fill the required template fields first: ") + string.Join(", ", missing));
            return null;
        }
        var execution = new WorkspaceExecution
        {
            Result = result ?? ExecutionResult.Passed,
            Notes = _runNotes.Text ?? "",
            Tester = (_runTester.Text ?? "").Trim(),
            Environment = (_runEnvironment.Text ?? "").Trim(),
            BuildVersion = (_runBuild.Text ?? "").Trim(),
            DurationSeconds = seconds,
            Evidence = _pending.ToList()
        };
        if (steps is not null) execution.StepResults = steps.ToList();
        return execution;
    }

    private void RecordStepResult(ExecutionResult result)
    {
        if (_caseList.SelectedItem is not WorkspaceTestCase test || test.Archived ||
            _runStepList.SelectedItem is not RunStepRow row) return;
        _stepResults[row.Step.Id] = new WorkspaceStepResult { StepId = row.Step.Id, Result = result };
        var next = Math.Min(test.Steps.Count - 1, test.Steps.IndexOf(row.Step) + 1);
        RenderRunSteps();
        _runStepList.SelectedIndex = next;
    }

    private void FinishStepRun()
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase test || test.Archived) return;
        if (StepwiseExecution.Overall(test, _stepResults.Values) is not ExecutionResult overall)
        {
            SetStatus("Record a result for every step first.");
            return;
        }
        var execution = NewExecution(test, overall, _stepResults.Values);
        if (execution is null) return;
        CommitExecution(test, execution);
    }

    private void CommitExecution(WorkspaceTestCase test, WorkspaceExecution execution)
    {
        test.Executions.Add(execution);
        Audited("test.run", test.Title, execution.Result.ToString());
        try
        {
            _store.Save(_workspace!);
            _runNotes.Text = "";
            _pending.Clear();
            _pendingFor = null;
            SetStatus("Execution recorded: " + L(execution.Result.ToString()));
            Refresh();
            _caseList.SelectedItem = test;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            test.Executions.Remove(execution);
            UndoAudit();
            SetStatus("The execution was not saved: " + ex.Message);
        }
    }

    private sealed record RunStepRow(int Number, WorkspaceTestStep Step, string Outcome)
    {
        public override string ToString() => $"{Number}. {Step.Action} → {Step.ExpectedResult}{(Outcome.Length == 0 ? "" : "  [" + Outcome + "]")}";
    }

    private void RenderRunSteps()
    {
        var test = _caseList.SelectedItem as WorkspaceTestCase;
        var previous = _runStepList.SelectedIndex;
        _runStepList.ItemsSource = test is null ? Array.Empty<RunStepRow>()
            : test.Steps.Select((s, i) => new RunStepRow(i + 1, s,
                _stepResults.TryGetValue(s.Id, out var r) ? L(r.Result.ToString()) : "")).ToArray();
        if (previous >= 0 && test is not null && previous < test.Steps.Count) _runStepList.SelectedIndex = previous;
        UpdateRunButtons();
    }

    private void UpdateRunButtons()
    {
        var test = _caseList.SelectedItem as WorkspaceTestCase;
        var usable = test is not null && !test.Archived && test.Steps.Count > 0;
        var selected = _runStepList.SelectedItem is RunStepRow;
        _stepPass.IsEnabled = usable && selected;
        _stepFail.IsEnabled = usable && selected;
        _stepBlock.IsEnabled = usable && selected;
        _finishStepRun.IsEnabled = usable;
        _resetStepRun.IsEnabled = usable && _stepResults.Count > 0;
    }

    private async Task AttachEvidenceAsync()
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase test || test.Archived) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("Attach evidence file…"), AllowMultiple = false
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            if (_pending.Count >= 50) { SetStatus("At most 50 files can be attached to one run."); return; }
            var item = new EvidenceStore(StoreDirectory).Add(path);
            _pending.Add(item);
            _pendingFor = test.Id;
            RenderPending();
            SetStatus("File attached to the next run.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            SetStatus("Evidence could not be attached: " + ex.Message);
        }
    }

    private void DiscardPending()
    {
        var store = new EvidenceStore(StoreDirectory);
        foreach (var item in _pending)
            try { store.Remove(item); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _pending.Clear();
        _pendingFor = null;
        RenderPending();
    }

    private void RenderPending()
    {
        _pendingEvidence.ItemsSource = _pending.Select(p => $"{p.FileName} ({p.SizeBytes / 1024.0:0.#} KB)").ToArray();
        _clearEvidence.IsEnabled = _pending.Count > 0;
    }

    private string EvidenceLine(WorkspaceEvidence item)
    {
        var intact = false;
        try { intact = new EvidenceStore(StoreDirectory).Verify(item); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return $"📎 {item.FileName} {(intact ? "✓" : "⚠ " + L("missing or changed"))}";
    }

    // =====================================================================================================
    // Plans
    // =====================================================================================================

    private void AddPlan()
    {
        if (_workspace is null) return;
        var name = (_planName.Text ?? "").Trim();
        if (name.Length == 0 || name.Length > 150) { SetStatus("Enter a plan name."); return; }
        if (_workspace.Plans.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        { SetStatus("A plan with this name already exists."); return; }
        var plan = new WorkspaceTestPlan { Name = name, Description = (_planDescription.Text ?? "").Trim() };
        _workspace.Plans.Add(plan);
        Audited("plan.created", name);
        try
        {
            _store.Save(_workspace);
            _planName.Text = ""; _planDescription.Text = "";
            SetStatus("Plan saved.");
            Refresh();
            _planList.SelectedItem = _workspace.Plans.FirstOrDefault(p => p.Id == plan.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _workspace.Plans.Remove(plan);
            UndoAudit();
            SetStatus("The plan was not saved: " + ex.Message);
        }
    }

    private void DeletePlan()
    {
        if (_workspace is null || _planList.SelectedItem is not WorkspaceTestPlan plan) return;
        var index = _workspace.Plans.IndexOf(plan);
        if (index < 0) return;
        _workspace.Plans.RemoveAt(index);
        Audited("plan.deleted", plan.Name);
        try { _store.Save(_workspace); SetStatus("Plan deleted. Its tests were kept."); Refresh(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _workspace.Plans.Insert(index, plan);
            UndoAudit();
            SetStatus("The plan was not saved: " + ex.Message);
        }
    }

    private void ChangePlanMembership(bool include)
    {
        if (_workspace is null || _planList.SelectedItem is not WorkspaceTestPlan plan ||
            _caseList.SelectedItem is not WorkspaceTestCase test) return;
        var before = plan.TestIds.ToArray();
        try
        {
            if (!TestPlanProgress.SetMembership(_workspace, plan.Id, test.Id, include))
            {
                SetStatus(include ? "The test is already in this plan." : "The test is not in this plan.");
                return;
            }
            Audited(include ? "plan.test-added" : "plan.test-removed", plan.Name, test.Title);
            _store.Save(_workspace);
            SetStatus(include ? "Test added to plan." : "Test removed from plan.");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            plan.TestIds.Clear();
            plan.TestIds.AddRange(before);
            UndoAudit();
            SetStatus("The plan was not saved: " + ex.Message);
        }
    }

    private void RenderPlanSelection()
    {
        var plan = _planList.SelectedItem as WorkspaceTestPlan;
        var test = _caseList.SelectedItem as WorkspaceTestCase;
        _deletePlan.IsEnabled = plan is not null;
        _addToPlan.IsEnabled = plan is not null && test is not null && !test.Archived && !plan.TestIds.Contains(test.Id);
        _removeFromPlan.IsEnabled = plan is not null && test is not null && plan.TestIds.Contains(test.Id);
        if (plan is null || _workspace is null)
        {
            _planProgress.Text = L("Select or create a plan.");
            _planMembers.ItemsSource = Array.Empty<string>();
            return;
        }
        var progress = TestPlanProgress.Calculate(_workspace, plan);
        _planProgress.Text = plan.Name + " · " +
            $"{progress.Total} {L("tests")} · {progress.Passed} {L("passed")} · {progress.Failed} {L("failed")} · " +
            $"{progress.Blocked} {L("blocked")} · {progress.NotRun} {L("not run")} · {progress.CompletionPercent:0.#}% {L("executed")}" +
            (plan.Description.Length > 0 ? Environment.NewLine + plan.Description : "");
        _planMembers.ItemsSource = _workspace.TestCases.Where(t => plan.TestIds.Contains(t.Id)).ToArray();
    }

    // =====================================================================================================
    // Templates
    // =====================================================================================================

    private void AddTemplate()
    {
        if (_workspace is null) return;
        var name = (_newTemplateName.Text ?? "").Trim();
        if (name.Length == 0 || name.Length > 120) { SetStatus("Enter a template name."); return; }
        if (_workspace.Templates.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        { SetStatus("A template with this name already exists."); return; }
        var template = new WorkspaceTemplate { Name = name, Icon = "🧪" };
        _workspace.Templates.Add(template);
        Audited("template.created", name);
        try { _store.Save(_workspace); _newTemplateName.Text = ""; SetStatus("Template saved."); Refresh(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _workspace.Templates.Remove(template);
            UndoAudit();
            SetStatus("The change was not saved: " + ex.Message);
        }
    }

    private void DeleteTemplate()
    {
        if (_workspace is null || _templateList.SelectedItem is not WorkspaceTemplate template) return;
        if (_workspace.TestCases.Any(t => string.Equals(t.TemplateName, template.Name, StringComparison.OrdinalIgnoreCase)))
        { SetStatus("This template is used by tests and cannot be deleted."); return; }
        var index = _workspace.Templates.IndexOf(template);
        _workspace.Templates.RemoveAt(index);
        Audited("template.deleted", template.Name);
        try { _store.Save(_workspace); SetStatus("Template deleted."); Refresh(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _workspace.Templates.Insert(index, template);
            UndoAudit();
            SetStatus("The change was not saved: " + ex.Message);
        }
    }

    private void AddTemplateField()
    {
        if (_workspace is null || _templateList.SelectedItem is not WorkspaceTemplate template) return;
        var section = (_fieldSection.Text ?? "").Trim();
        var name = (_fieldName.Text ?? "").Trim();
        if (section.Length == 0 || name.Length == 0 || section.Length > 120 || name.Length > 120)
        { SetStatus("Enter a section and a field name."); return; }
        if (WorkspaceTemplates.Fields(template).Any(f => string.Equals(f.Key, WorkspaceTemplates.FieldKey(section, name), StringComparison.OrdinalIgnoreCase)))
        { SetStatus("This field already exists."); return; }
        if (template.Sections.Sum(s => s.Fields.Count) >= 100) { SetStatus("A template can have at most 100 fields."); return; }
        var target = template.Sections.FirstOrDefault(s => string.Equals(s.Name, section, StringComparison.OrdinalIgnoreCase));
        var createdSection = target is null;
        if (target is null) { target = new WorkspaceTemplateSection { Name = section }; template.Sections.Add(target); }
        var field = new WorkspaceTemplateField { Name = name, Required = _fieldRequired.IsChecked == true };
        target.Fields.Add(field);
        Audited("template.field-added", template.Name, section + "/" + name);
        try { _store.Save(_workspace); _fieldName.Text = ""; SetStatus("Field added."); Refresh(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            target.Fields.Remove(field);
            if (createdSection) template.Sections.Remove(target);
            UndoAudit();
            SetStatus("The change was not saved: " + ex.Message);
        }
    }

    private void RemoveTemplateField()
    {
        if (_workspace is null || _templateList.SelectedItem is not WorkspaceTemplate template ||
            _templateFieldList.SelectedItem is not TemplateFieldRow row) return;
        var section = template.Sections.FirstOrDefault(s => s.Fields.Contains(row.Field));
        if (section is null) return;
        var index = section.Fields.IndexOf(row.Field);
        var sectionIndex = template.Sections.IndexOf(section);
        section.Fields.RemoveAt(index);
        var removedSection = section.Fields.Count == 0;
        if (removedSection) template.Sections.RemoveAt(sectionIndex);
        Audited("template.field-removed", template.Name, row.Section + "/" + row.Field.Name);
        try { _store.Save(_workspace); SetStatus("Field removed."); Refresh(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (removedSection) template.Sections.Insert(sectionIndex, section);
            section.Fields.Insert(index, row.Field);
            UndoAudit();
            SetStatus("The change was not saved: " + ex.Message);
        }
    }

    private sealed record TemplateFieldRow(string Section, WorkspaceTemplateField Field)
    {
        public override string ToString() => $"{Section} · {Field.Name}{(Field.Required ? " *" : "")}";
    }

    private void RenderTemplateSelection()
    {
        var template = _templateList.SelectedItem as WorkspaceTemplate;
        _deleteTemplate.IsEnabled = template is not null;
        _fieldSection.IsEnabled = template is not null;
        _fieldName.IsEnabled = template is not null;
        _fieldRequired.IsEnabled = template is not null;
        _addField.IsEnabled = template is not null;
        _removeField.IsEnabled = false;
        _templateFieldList.ItemsSource = template is null ? Array.Empty<TemplateFieldRow>()
            : template.Sections.SelectMany(s => s.Fields.Select(f => new TemplateFieldRow(s.Name, f))).ToArray();
    }

    // =====================================================================================================
    // CSV import
    // =====================================================================================================

    private async Task ImportCsvAsync(bool requirements)
    {
        if (_workspace is null) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L(requirements ? "Import requirements (CSV)" : "Import tests (CSV)"), AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("CSV file") { Patterns = new[] { "*.csv" } } }
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            if (new FileInfo(path).Length > WorkspaceCsvImport.MaxChars) { SetStatus("Import failed: the file is too large."); return; }

            var requirementCount = _workspace.Requirements.Count;
            var testCount = _workspace.TestCases.Count;
            var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
            var result = requirements ? WorkspaceCsvImport.ImportRequirements(_workspace, text)
                                      : WorkspaceCsvImport.ImportTests(_workspace, text);
            if (!result.Success)
            {
                SetStatus("Import failed: " + string.Join(" ", result.Errors.Take(3)));
                return;
            }
            if (result.Added == 0) { SetStatus($"{L("Nothing to import")} ({result.Skipped} {L("duplicates skipped")})."); return; }

            Audited(requirements ? "requirements.imported" : "tests.imported", Path.GetFileName(path), $"{result.Added} added, {result.Skipped} skipped");
            try
            {
                _store.Save(_workspace);
                SetStatus($"{result.Added} {L("imported")}, {result.Skipped} {L("duplicates skipped")}.");
                Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _workspace.Requirements.RemoveRange(requirementCount, _workspace.Requirements.Count - requirementCount);
                _workspace.TestCases.RemoveRange(testCount, _workspace.TestCases.Count - testCount);
                UndoAudit();
                SetStatus("Import failed: " + ex.Message);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or DecoderFallbackException)
        {
            SetStatus("Import failed: " + ex.Message);
        }
    }
}
