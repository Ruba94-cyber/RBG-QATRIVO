using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Text;
using TestLoom.Core;

namespace TestLoom.Desktop;

/// <summary>
/// Independent generic desktop shell. The first milestone intentionally
/// supports only LOCAL workspaces: no private extensions, shared data mounts
/// or migration from another product.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly WorkspaceStore _store;
    private WorkspaceDocument? _workspace;
    private bool _initialLoadFailed;
    private bool _recoveryBlockedBySchema;
    private readonly TextBox _workspaceName = new() { Watermark = "Workspace name" };
    private readonly ComboBox _presetPicker = new() { MinWidth = 250 };
    private readonly Button _createWorkspace = new() { Content = "Create workspace" };
    private readonly Button _about = new() { Content = "About TestLoom" };
    private readonly Button _reloadWorkspace = new() { Content = "Reload workspace from disk" };
    private readonly Button _recoverWorkspace = new() { Content = "Recover damaged workspace from backup" };
    private readonly TextBlock _workspaceSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _requirementKey = new() { Watermark = "Requirement ID (e.g. REQ-001)" };
    private readonly TextBox _requirementTitle = new() { Watermark = "Requirement title" };
    private readonly Button _addRequirement = new() { Content = "Add requirement" };
    private readonly ListBox _requirementList = new() { Height = 110 };
    private readonly TextBox _editRequirementKey = new() { Watermark = "Selected requirement ID" };
    private readonly TextBox _editRequirementTitle = new() { Watermark = "Selected requirement title" };
    private readonly Button _saveRequirementEdits = new() { Content = "Save requirement changes" };
    private readonly TextBlock _coverageSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _testRequirement = new() { MinWidth = 250 };
    private readonly TextBox _testTitle = new() { Watermark = "A clear test objective" };
    private readonly TextBox _testDescription = new() { Watermark = "Steps or acceptance criteria", AcceptsReturn = true, Height = 85, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _testTemplate = new() { MinWidth = 225 };
    private readonly Button _addTest = new() { Content = "Add test case" };
    private readonly ListBox _caseList = new() { Height = 250 };
    private readonly CheckBox _showArchived = new() { Content = "Show archived tests" };
    private readonly TextBox _search = new() { Watermark = "Search tests (title, steps, requirement…)" };
    private readonly ComboBox _outcomeFilter = new() { ItemsSource = new[] { "Any outcome", "Not run", "Passed", "Failed", "Blocked" }, SelectedIndex = 0 };
    private readonly Button _exportHtml = new() { Content = "Export report (HTML)" };
    private readonly Button _backup = new() { Content = "Back up workspace (ZIP)" };
    private readonly TextBox _editTitle = new() { Watermark = "Selected test title" };
    private readonly TextBox _editDescription = new()
    {
        Watermark = "Selected test description", AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, Height = 80
    };
    private readonly Button _saveEdits = new() { Content = "Save test changes" };
    private readonly ComboBox _existingTestRequirement = new() { MinWidth = 270 };
    private readonly Button _linkRequirement = new() { Content = "Link requirement" };
    private readonly Button _unlinkRequirement = new() { Content = "Unlink requirement" };
    private readonly Button _archiveToggle = new() { Content = "Archive test" };
    private readonly TextBlock _caseDetails = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _stepList = new() { Height = 135 };
    private readonly TextBox _newStepAction = new() { Watermark = "Step action" };
    private readonly TextBox _newStepExpected = new() { Watermark = "Expected result" };
    private readonly Button _addStep = new() { Content = "Add step" };
    private readonly Button _pasteSteps = new() { Content = "Paste steps from clipboard" };
    private readonly TextBox _editStepAction = new() { Watermark = "Selected step action" };
    private readonly TextBox _editStepExpected = new() { Watermark = "Selected step expected result" };
    private readonly Button _saveStep = new() { Content = "Save selected step" };
    private readonly Button _removeStep = new() { Content = "Remove selected step" };
    private readonly Button _moveStepUp = new() { Content = "Move step up" };
    private readonly Button _moveStepDown = new() { Content = "Move step down" };
    private readonly TextBox _runNotes = new() { Watermark = "Execution notes (optional)", AcceptsReturn = true, Height = 70 };
    private readonly Button _pass = new() { Content = "Record pass" };
    private readonly Button _fail = new() { Content = "Record fail" };
    private readonly Button _block = new() { Content = "Record blocked" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _exportTests = new() { Content = "Export tests (CSV)" };
    private readonly Button _exportRequirements = new() { Content = "Export requirements (CSV)" };
    private readonly Button _exportSteps = new() { Content = "Export steps (CSV)" };
    private readonly TextBox _defectKey = new() { Watermark = "Defect ID (e.g. BUG-001)" };
    private readonly TextBox _defectTitle = new() { Watermark = "Defect summary" };
    private readonly TextBox _defectDescription = new() { Watermark = "What happened and how to reproduce", AcceptsReturn = true, Height = 70 };
    private readonly ComboBox _defectSeverity = new() { MinWidth = 180 };
    private readonly Button _addDefect = new() { Content = "Create defect" };
    private readonly ListBox _defectList = new() { Height = 125 };
    private readonly ComboBox _selectedDefectStatus = new() { MinWidth = 180 };
    private readonly Button _saveDefectStatus = new() { Content = "Update defect status" };
    private readonly TextBox _selectedDefectTitle = new() { Watermark = "Selected defect title" };
    private readonly TextBox _selectedDefectDescription = new()
    {
        Watermark = "Selected defect description", AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, Height = 70
    };
    private readonly ComboBox _selectedDefectSeverity = new() { MinWidth = 180 };
    private readonly Button _saveDefectDetails = new() { Content = "Save defect details" };
    private readonly Button _exportDefects = new() { Content = "Export defects (CSV)" };

    public MainWindow() : this(null) { }

    public MainWindow(string? profileDirectory)
    {
        Title = "TestLoom · Test management";
        Icon = AboutInfo.LoadIcon();
        Width = 1060;
        Height = 780;
        MinWidth = 660;
        MinHeight = 600;

        // Deliberately independent of any other application's local profile.
        var baseDirectory = profileDirectory ?? EditionStoragePaths.ConfigDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestLoom");
        _store = new WorkspaceStore(baseDirectory);
        try
        {
            _workspace = _store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Text.Json.JsonException or InvalidDataException
                                   or NotSupportedException)
        {
            // An unreadable workspace is NEVER overwritten during first-run.
            _initialLoadFailed = true;
            _recoveryBlockedBySchema = IsUnknownSchema(ex);
            _status.Text = (_recoveryBlockedBySchema
                ? "This workspace uses an unsupported schema. Install a compatible TestLoom version; automatic or manual rollback is disabled. "
                : "Cannot read the existing workspace. The file was left unchanged: ") + ex.Message;
        }

        _presetPicker.ItemsSource = TestLoomWorkspacePresets.All.Select(p => p.DisplayName).ToArray();
        _presetPicker.SelectedIndex = 0;
        _defectSeverity.ItemsSource = Enum.GetNames<WorkspaceDefectSeverity>();
        _defectSeverity.SelectedIndex = (int)WorkspaceDefectSeverity.Medium;
        _selectedDefectStatus.ItemsSource = Enum.GetNames<WorkspaceDefectStatus>();
        _selectedDefectSeverity.ItemsSource = Enum.GetNames<WorkspaceDefectSeverity>();

        _createWorkspace.Click += (_, _) => CreateWorkspace();
        _about.Click += async (_, _) => await AboutInfo.Create().ShowDialog(this);
        _reloadWorkspace.Click += (_, _) => ReloadWorkspace();
        _recoverWorkspace.Click += (_, _) => RecoverWorkspace();
        _addRequirement.Click += (_, _) => AddRequirement();
        _requirementList.SelectionChanged += (_, _) => RenderRequirementSelection();
        _saveRequirementEdits.Click += (_, _) => SaveSelectedRequirement();
        _addTest.Click += (_, _) => AddTestCase();
        _caseList.SelectionChanged += (_, _) => RenderSelection();
        _showArchived.IsCheckedChanged += (_, _) => Refresh();
        _search.TextChanged += (_, _) => Refresh();
        _outcomeFilter.SelectionChanged += (_, _) => Refresh();
        _exportHtml.Click += async (_, _) => await ExportFileAsync("Export report", "testloom-report.html", "html", "HTML report",
            () => WorkspaceHtmlReport.Generate(_workspace!), "Report exported.");
        _backup.Click += async (_, _) => await BackupAsync();
        _saveEdits.Click += (_, _) => SaveSelectedTest();
        _existingTestRequirement.SelectionChanged += (_, _) => RefreshLinkButtons();
        _linkRequirement.Click += (_, _) => ChangeSelectedTestRequirement(link: true);
        _unlinkRequirement.Click += (_, _) => ChangeSelectedTestRequirement(link: false);
        _stepList.SelectionChanged += (_, _) => UpdateStepActions();
        _addStep.Click += (_, _) => AddStep();
        _pasteSteps.Click += async (_, _) => await PasteStepsAsync();
        _saveStep.Click += (_, _) => SaveSelectedStep();
        _removeStep.Click += (_, _) => RemoveSelectedStep();
        _moveStepUp.Click += (_, _) => MoveSelectedStep(-1);
        _moveStepDown.Click += (_, _) => MoveSelectedStep(1);
        _archiveToggle.Click += (_, _) => ToggleArchive();
        _pass.Click += (_, _) => RecordExecution(ExecutionResult.Passed);
        _fail.Click += (_, _) => RecordExecution(ExecutionResult.Failed);
        _block.Click += (_, _) => RecordExecution(ExecutionResult.Blocked);
        _exportTests.Click += async (_, _) => await ExportCsvAsync(requirements: false);
        _exportRequirements.Click += async (_, _) => await ExportCsvAsync(requirements: true);
        _exportSteps.Click += async (_, _) => await ExportCsvAsync(requirements: false, steps: true);
        _addDefect.Click += (_, _) => AddDefect();
        _defectList.SelectionChanged += (_, _) => RenderDefectSelection();
        _saveDefectStatus.Click += (_, _) => SaveDefectStatus();
        _saveDefectDetails.Click += (_, _) => SaveDefectDetails();
        _exportDefects.Click += async (_, _) => await ExportCsvAsync(requirements: false, defects: true);

        InitializeFeatures();
        Content = CreateInterface();
        CaptureTexts();
        Refresh();
        ApplyLanguage();
        Opened += async (_, _) => await OfferFirstRunAsync();
    }

    private static TextBlock SectionHeading(string text) =>
        new() { Text = text, FontSize = 19, FontWeight = FontWeight.SemiBold };

    private void CreateWorkspace()
    {
        if (_workspace is not null || _initialLoadFailed) return;
        var name = _workspaceName.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Enter a workspace name.");
            return;
        }

        try
        {
            var preset = TestLoomWorkspacePresets.All[_presetPicker.SelectedIndex < 0
                ? 0 : _presetPicker.SelectedIndex];
            var workspace = WorkspaceStarter.Create(name, preset.Id, _uiLanguage, includeSamples: false,
                tester: _defaultTester.Text);
            _store.Save(workspace);
            _workspace = workspace;
            SetStatus("Workspace created.");
            ApplyLanguage();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            SetStatus("Cannot create workspace: " + ex.Message);
        }
    }

    private void ReloadWorkspace()
    {
        try
        {
            // Reload must remain available after a failed load: the user may
            // have repaired or upgraded the file outside this application.
            // It never writes data or invokes backup recovery automatically.
            _workspace = _store.Load();
            _initialLoadFailed = false;
            _recoveryBlockedBySchema = false;
            SetStatus(_workspace is null ? "No workspace exists on disk." : "Workspace reloaded.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Text.Json.JsonException or InvalidDataException
                                   or NotSupportedException)
        {
            _workspace = null;
            _initialLoadFailed = true;
            _recoveryBlockedBySchema = IsUnknownSchema(ex);
            SetStatus(_recoveryBlockedBySchema
                ? "A newer workspace schema is not supported. No rollback will be offered."
                : "Could not reload workspace. File unchanged: " + ex.Message);
        }
        Refresh();
    }

    private static bool IsUnknownSchema(Exception ex) =>
        ex is NotSupportedException ||
        (ex is InvalidDataException &&
         ex.Message.Contains("Unsupported workspace schema version", StringComparison.OrdinalIgnoreCase));

    private void RecoverWorkspace()
    {
        if (!_initialLoadFailed || _recoveryBlockedBySchema) return;
        try
        {
            _workspace = _store.RecoverCorruptedFromBackup();
            _initialLoadFailed = false;
            _recoveryBlockedBySchema = false;
            SetStatus("A validated previous workspace was restored. The damaged file was preserved separately.");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidOperationException or System.Text.Json.JsonException
                                   or NotSupportedException)
        {
            if (IsUnknownSchema(ex))
            {
                _recoveryBlockedBySchema = true;
                Refresh();
            }
            SetStatus("Backup recovery failed. Original files were not replaced: " + ex.Message);
        }
    }

    private void AddRequirement()
    {
        if (_workspace is null) return;
        var key = _requirementKey.Text?.Trim();
        var title = _requirementTitle.Text?.Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title))
        {
            SetStatus("Enter both a requirement ID and a title.");
            return;
        }
        if (_workspace.Requirements.Any(x =>
            string.Equals(x.Key.Trim(), key, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("This requirement ID already exists.");
            return;
        }

        var requirement = new WorkspaceRequirement { Key = key, Title = title };
        _workspace.Requirements.Add(requirement);
        try
        {
            _store.Save(_workspace);
            _requirementKey.Text = "";
            _requirementTitle.Text = "";
            SetStatus("Requirement saved.");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            _workspace.Requirements.Remove(requirement);
            SetStatus("The requirement was not saved: " + ex.Message);
        }
    }

    private void SaveSelectedRequirement()
    {
        if (_workspace is null ||
            _requirementList.SelectedItem is not WorkspaceRequirement requirement) return;

        var key = _editRequirementKey.Text?.Trim();
        var title = _editRequirementTitle.Text?.Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title))
        {
            SetStatus("Requirement ID and title cannot be empty.");
            return;
        }

        if (_workspace.Requirements.Any(r => r.Id != requirement.Id &&
            string.Equals(r.Key.Trim(), key, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Another requirement already uses this ID.");
            return;
        }

        var previousKey = requirement.Key;
        var previousTitle = requirement.Title;
        requirement.Key = key;
        requirement.Title = title;
        try
        {
            // Link references remain GUID-based, so renaming a requirement
            // never drops its test coverage or execution history.
            _store.Save(_workspace);
            SetStatus("Requirement changes saved.");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            requirement.Key = previousKey;
            requirement.Title = previousTitle;
            SetStatus("Requirement changes were not saved: " + ex.Message);
        }
    }

    private void RenderRequirementSelection()
    {
        var selected = _requirementList.SelectedItem as WorkspaceRequirement;
        _editRequirementKey.IsEnabled = selected is not null;
        _editRequirementTitle.IsEnabled = selected is not null;
        _saveRequirementEdits.IsEnabled = selected is not null;
        _editRequirementKey.Text = selected?.Key ?? "";
        _editRequirementTitle.Text = selected?.Title ?? "";
    }

    private void AddTestCase()
    {
        if (_workspace is null) return;
        var title = _testTitle.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            SetStatus("Enter a test title.");
            return;
        }
        var templateNames = AvailableTemplates();
        var template = _testTemplate.SelectedIndex >= 0 &&
                       _testTemplate.SelectedIndex < templateNames.Count
            ? templateNames[_testTemplate.SelectedIndex] : L("Custom test");

        var newCase = new WorkspaceTestCase
        {
            Title = title,
            TemplateName = template,
            Description = _testDescription.Text ?? "",
            Priority = (WorkspacePriority)Math.Clamp(_newPriority.SelectedIndex, 0, 3)
        };
        if (_testRequirement.SelectedIndex > 0 &&
            _testRequirement.SelectedIndex <= _workspace.Requirements.Count)
            newCase.RequirementIds.Add(_workspace.Requirements[_testRequirement.SelectedIndex - 1].Id);
        _workspace.TestCases.Add(newCase);
        Audited("test.created", newCase.Title);
        try
        {
            _store.Save(_workspace);
            _testTitle.Text = "";
            _testDescription.Text = "";
            SetStatus("Test case saved.");
            Refresh();
            _caseList.SelectedItem = newCase;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            _workspace.TestCases.Remove(newCase);
            UndoAudit();
            SetStatus("The new test was not saved: " + ex.Message);
        }
    }

    private void SaveSelectedTest()
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase test
            || test.Archived) return;
        var title = _editTitle.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            SetStatus("The test title cannot be empty.");
            return;
        }
        var tags = ParseTags(_editTags.Text);
        if (tags.Count > 50 || tags.Any(t => t.Length > 40))
        {
            SetStatus("Tags can have at most 40 characters each.");
            return;
        }

        var previousTitle = test.Title;
        var previousDescription = test.Description;
        var previousPriority = test.Priority;
        var previousOwner = test.Owner;
        var previousTags = test.Tags;
        var previousFields = new Dictionary<string, string>(test.FieldValues);
        test.Title = title;
        test.Description = _editDescription.Text ?? "";
        test.Priority = (WorkspacePriority)Math.Clamp(_editPriority.SelectedIndex, 0, 3);
        test.Owner = (_editOwner.Text ?? "").Trim();
        test.Tags = tags;
        foreach (var (key, box) in _fieldBoxes) WorkspaceTemplates.SetValue(test, key, box.Text);
        Audited("test.edited", test.Title);
        try
        {
            _store.Save(_workspace);
            SetStatus("Changes saved.");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            test.Title = previousTitle;
            test.Description = previousDescription;
            test.Priority = previousPriority;
            test.Owner = previousOwner;
            test.Tags = previousTags;
            test.FieldValues = previousFields;
            UndoAudit();
            SetStatus("Changes were not saved: " + ex.Message);
        }
    }

    private Guid? PickedRequirementId()
    {
        if (_workspace is null) return null;
        var index = _existingTestRequirement.SelectedIndex - 1;
        return index >= 0 && index < _workspace.Requirements.Count
            ? _workspace.Requirements[index].Id : null;
    }

    private void RefreshLinkButtons()
    {
        var test = _caseList.SelectedItem as WorkspaceTestCase;
        var requirementId = PickedRequirementId();
        var canEdit = test is not null && !test.Archived && requirementId.HasValue;
        var isLinked = canEdit && test!.RequirementIds.Contains(requirementId!.Value);
        _linkRequirement.IsEnabled = canEdit && !isLinked;
        _unlinkRequirement.IsEnabled = canEdit && isLinked;
    }

    private void ChangeSelectedTestRequirement(bool link)
    {
        if (_workspace is null ||
            _caseList.SelectedItem is not WorkspaceTestCase test || test.Archived ||
            PickedRequirementId() is not Guid requirementId)
            return;

        // Keep the exact previous ordering on an I/O or optimistic-lock
        // failure; no in-memory change may masquerade as a saved relationship.
        var originalLinks = test.RequirementIds.ToArray();
        try
        {
            if (!RequirementLinks.SetLinked(_workspace, test.Id, requirementId, link))
                return;

            _store.Save(_workspace);
            SetStatus(link ? "Requirement linked to test." : "Requirement unlinked from test.");
            Refresh();
            _caseList.SelectedItem = test;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException or ArgumentException
                                   or InvalidOperationException)
        {
            test.RequirementIds.Clear();
            test.RequirementIds.AddRange(originalLinks);
            SetStatus("Requirement links were not changed: " + ex.Message);
            RefreshLinkButtons();
        }
    }

    private void ToggleArchive()
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase test) return;
        var old = test.Archived;
        test.Archived = !old;
        Audited(test.Archived ? "test.archived" : "test.restored", test.Title);
        try
        {
            _store.Save(_workspace);
            SetStatus(test.Archived ? "Test archived. Enable Show archived tests to restore it."
                                    : "Test restored.");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            test.Archived = old;
            UndoAudit();
            SetStatus("Archive status could not be saved: " + ex.Message);
        }
    }

    private async Task PasteStepsAsync()
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase test || test.Archived) return;
        var clipboard = GetTopLevel(this)?.Clipboard;
        var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
        var result = StepPasteParser.Parse(text);
        if (!result.Success)
        {
            SetStatus($"{L("Paste rejected")} ({result.Error}, {L("row")} {result.RowNumber}); {L("nothing was added.")}");
            return;
        }
        if (result.Steps.Count == 0)
        {
            SetStatus("The clipboard has no steps. Copy two spreadsheet columns: action and expected result.");
            return;
        }
        var added = result.Steps.Select(x => new WorkspaceTestStep { Action = x.Action, ExpectedResult = x.ExpectedResult }).ToList();
        test.Steps.AddRange(added);
        try
        {
            _store.Save(_workspace);
            SetStatus($"{added.Count} {L("steps pasted and saved.")}");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            foreach (var step in added) test.Steps.Remove(step);
            SetStatus("Steps could not be saved: " + ex.Message);
        }
    }

    private void AddStep()
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase test ||
            test.Archived) return;
        var action = _newStepAction.Text?.Trim();
        var expected = _newStepExpected.Text?.Trim();
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(expected))
        {
            SetStatus("Enter both a step action and its expected result.");
            return;
        }

        var step = new WorkspaceTestStep { Action = action, ExpectedResult = expected };
        test.Steps.Add(step);
        try
        {
            _store.Save(_workspace);
            _newStepAction.Text = "";
            _newStepExpected.Text = "";
            SetStatus("Test step saved.");
            Refresh();
            _stepList.SelectedItem = step;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            test.Steps.Remove(step);
            SetStatus("Test step was not saved: " + ex.Message);
        }
    }

    private void SaveSelectedStep()
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase test ||
            test.Archived || _stepList.SelectedItem is not WorkspaceTestStep step) return;
        var action = _editStepAction.Text?.Trim();
        var expected = _editStepExpected.Text?.Trim();
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(expected))
        {
            SetStatus("The action and expected result cannot be empty.");
            return;
        }

        var previousAction = step.Action;
        var previousExpected = step.ExpectedResult;
        step.Action = action;
        step.ExpectedResult = expected;
        try
        {
            _store.Save(_workspace);
            SetStatus("Step changes saved.");
            Refresh();
            _stepList.SelectedItem = step;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            step.Action = previousAction;
            step.ExpectedResult = previousExpected;
            SetStatus("Step changes were not saved: " + ex.Message);
        }
    }

    private void RemoveSelectedStep()
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase test ||
            test.Archived || _stepList.SelectedItem is not WorkspaceTestStep step) return;
        var index = test.Steps.IndexOf(step);
        if (index < 0) return;
        test.Steps.RemoveAt(index);
        try
        {
            _store.Save(_workspace);
            SetStatus("Step removed.");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            test.Steps.Insert(index, step);
            SetStatus("The step was not removed: " + ex.Message);
        }
    }

    private void MoveSelectedStep(int direction)
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase test ||
            test.Archived || _stepList.SelectedItem is not WorkspaceTestStep step) return;
        var index = test.Steps.IndexOf(step);
        var nextIndex = index + direction;
        if (index < 0 || nextIndex < 0 || nextIndex >= test.Steps.Count) return;
        (test.Steps[index], test.Steps[nextIndex]) = (test.Steps[nextIndex], test.Steps[index]);
        try
        {
            _store.Save(_workspace);
            SetStatus("Step order saved.");
            Refresh();
            _stepList.SelectedItem = step;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            (test.Steps[index], test.Steps[nextIndex]) = (test.Steps[nextIndex], test.Steps[index]);
            SetStatus("The step order was not saved: " + ex.Message);
        }
    }

    private void UpdateStepActions()
    {
        var test = _caseList.SelectedItem as WorkspaceTestCase;
        var selected = _stepList.SelectedItem as WorkspaceTestStep;
        var index = selected is null ? -1 : test?.Steps.IndexOf(selected) ?? -1;
        var enabled = test is not null && !test.Archived && index >= 0;
        _removeStep.IsEnabled = enabled;
        _editStepAction.IsEnabled = enabled;
        _editStepExpected.IsEnabled = enabled;
        _saveStep.IsEnabled = enabled;
        _editStepAction.Text = enabled ? selected!.Action : "";
        _editStepExpected.Text = enabled ? selected!.ExpectedResult : "";
        _moveStepUp.IsEnabled = enabled && index > 0;
        _moveStepDown.IsEnabled = enabled && index < (test?.Steps.Count ?? 0) - 1;
    }

    private void RecordExecution(ExecutionResult result)
    {
        if (_workspace is null || _caseList.SelectedItem is not WorkspaceTestCase test
            || test.Archived) return;
        var execution = NewExecution(test, result, steps: null);
        if (execution is null) return;
        CommitExecution(test, execution);
    }

    private void AddDefect()
    {
        if (_workspace is null) return;
        var key = _defectKey.Text?.Trim();
        var title = _defectTitle.Text?.Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title))
        {
            SetStatus("Enter a defect ID and summary.");
            return;
        }
        if (_workspace.Defects.Any(d => string.Equals(d.Key.Trim(), key, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("A defect with this ID already exists.");
            return;
        }

        var defect = new WorkspaceDefect
        {
            Key = key,
            Title = title,
            Description = _defectDescription.Text ?? "",
            Severity = (WorkspaceDefectSeverity)Math.Clamp(_defectSeverity.SelectedIndex, 0, 3),
            TestCaseId = (_caseList.SelectedItem as WorkspaceTestCase)?.Id
        };
        _workspace.Defects.Add(defect);
        Audited("defect.created", defect.Key, defect.Title);
        try
        {
            _store.Save(_workspace);
            _defectKey.Text = "";
            _defectTitle.Text = "";
            _defectDescription.Text = "";
            SetStatus("Defect saved.");
            Refresh();
            _defectList.SelectedItem = defect;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            _workspace.Defects.Remove(defect);
            UndoAudit();
            SetStatus("Defect was not saved: " + ex.Message);
        }
    }

    private void SaveDefectStatus()
    {
        if (_workspace is null || _defectList.SelectedItem is not WorkspaceDefect defect ||
            _selectedDefectStatus.SelectedIndex < 0) return;
        var oldStatus = defect.Status;
        defect.Status = (WorkspaceDefectStatus)_selectedDefectStatus.SelectedIndex;
        try
        {
            _store.Save(_workspace);
            SetStatus("Defect status saved.");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            defect.Status = oldStatus;
            SetStatus("Defect status was not saved: " + ex.Message);
        }
    }

    private void SaveDefectDetails()
    {
        if (_workspace is null || _defectList.SelectedItem is not WorkspaceDefect defect)
            return;

        var title = _selectedDefectTitle.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title) || _selectedDefectSeverity.SelectedIndex < 0)
        {
            SetStatus("Enter a valid defect title and severity.");
            return;
        }

        var oldTitle = defect.Title;
        var oldDescription = defect.Description;
        var oldSeverity = defect.Severity;
        defect.Title = title;
        defect.Description = _selectedDefectDescription.Text ?? "";
        defect.Severity = (WorkspaceDefectSeverity)_selectedDefectSeverity.SelectedIndex;
        try
        {
            _store.Save(_workspace);
            SetStatus("Defect details saved.");
            Refresh();
            _defectList.SelectedItem = defect;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            defect.Title = oldTitle;
            defect.Description = oldDescription;
            defect.Severity = oldSeverity;
            SetStatus("Defect details were not saved: " + ex.Message);
        }
    }

    private void RenderDefectSelection()
    {
        var selected = _defectList.SelectedItem as WorkspaceDefect;
        var enabled = selected is not null;
        _selectedDefectStatus.IsEnabled = enabled;
        _saveDefectStatus.IsEnabled = enabled;
        _selectedDefectStatus.SelectedIndex = enabled ? (int)selected!.Status : -1;
        _selectedDefectTitle.IsEnabled = enabled;
        _selectedDefectDescription.IsEnabled = enabled;
        _selectedDefectSeverity.IsEnabled = enabled;
        _saveDefectDetails.IsEnabled = enabled;
        _selectedDefectTitle.Text = selected?.Title ?? "";
        _selectedDefectDescription.Text = selected?.Description ?? "";
        _selectedDefectSeverity.SelectedIndex = enabled ? (int)selected!.Severity : -1;
    }

    private async Task ExportCsvAsync(bool requirements, bool steps = false, bool defects = false)
    {
        if (_workspace is null) return;
        try
        {
            var content = defects ? WorkspaceCsvReport.ExportDefects(_workspace)
                : steps
                    ? WorkspaceCsvReport.ExportTestSteps(_workspace,
                        includeArchived: _showArchived.IsChecked == true)
                : requirements
                    ? WorkspaceCsvReport.ExportRequirements(_workspace)
                    : WorkspaceCsvReport.ExportTestCases(_workspace,
                        includeArchived: _showArchived.IsChecked == true);
            if (!StorageProvider.CanSave)
            {
                SetStatus("File saving is unavailable on this desktop.");
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = defects ? "Export defects" : steps ? "Export test steps" :
                    requirements ? "Export requirement coverage" : "Export test cases",
                SuggestedFileName = defects ? "testloom-defects.csv" : steps ? "testloom-steps.csv" :
                    requirements ? "testloom-requirements.csv" : "testloom-tests.csv",
                DefaultExtension = "csv",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("CSV file") { Patterns = new[] { "*.csv" } }
                },
                ShowOverwritePrompt = true
            });
            if (file is null) return;

            await using var writer = new StreamWriter(await file.OpenWriteAsync(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await writer.WriteAsync(content);
            await writer.FlushAsync();
            SetStatus(defects ? "Defects exported." : steps ? "Test steps exported." :
                requirements ? "Requirements exported." : "Test cases exported.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException or NotSupportedException)
        {
            SetStatus("Export failed: " + ex.Message);
        }
    }

    private async Task ExportFileAsync(string title, string fileName, string extension, string typeName,
        Func<string> content, string doneMessage)
    {
        if (_workspace is null) return;
        try
        {
            if (!StorageProvider.CanSave) { SetStatus("File saving is unavailable on this desktop."); return; }
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title, SuggestedFileName = fileName, DefaultExtension = extension,
                FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { "*." + extension } } },
                ShowOverwritePrompt = true
            });
            if (file is null) return;
            await using var writer = new StreamWriter(await file.OpenWriteAsync(), new UTF8Encoding(false));
            await writer.WriteAsync(content());
            await writer.FlushAsync();
            SetStatus(doneMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            SetStatus("Export failed: " + ex.Message);
        }
    }

    private async Task BackupAsync()
    {
        if (_workspace is null) return;
        try
        {
            if (!StorageProvider.CanSave) { SetStatus("File saving is unavailable on this desktop."); return; }
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Back up workspace", SuggestedFileName = "testloom-backup.zip", DefaultExtension = "zip",
                FileTypeChoices = new[] { new FilePickerFileType("ZIP backup") { Patterns = new[] { "*.zip" } } },
                ShowOverwritePrompt = true
            });
            var path = file?.TryGetLocalPath();
            if (path is null) return;
            WorkspaceBackup.Create(_store, path);
            SetStatus("Workspace backed up.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            SetStatus("Backup failed: " + ex.Message);
        }
    }

    private IReadOnlyList<string> AvailableTemplates() =>
        _workspace is null ? Array.Empty<string>() :
        new[] { L("Custom test") }.Concat(_workspace.Templates.Count > 0
            ? _workspace.Templates.Select(t => t.Name)
            : TestLoomTemplateBlueprints.Create(_workspace.PresetId).Select(t => t.Name)).ToArray();

    private bool _refreshing;

    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { RefreshCore(); }
        finally { _refreshing = false; }
    }

    private void RefreshCore()
    {
        var active = _workspace is not null;
        _createWorkspace.IsEnabled = !active && !_initialLoadFailed;
        _workspaceName.IsEnabled = !active && !_initialLoadFailed;
        _presetPicker.IsEnabled = !active && !_initialLoadFailed;
        _reloadWorkspace.IsEnabled = active || _initialLoadFailed || File.Exists(_store.FilePath);
        _recoverWorkspace.IsVisible = _initialLoadFailed && !_recoveryBlockedBySchema;
        _recoverWorkspace.IsEnabled = _initialLoadFailed && !_recoveryBlockedBySchema && File.Exists(_store.FilePath + ".bak");
        _requirementKey.IsEnabled = active;
        _requirementTitle.IsEnabled = active;
        _addRequirement.IsEnabled = active;
        _requirementList.IsEnabled = active;
        _testRequirement.IsEnabled = active;
        _exportTests.IsEnabled = active;
        _exportRequirements.IsEnabled = active;
        _exportSteps.IsEnabled = active;
        _exportDefects.IsEnabled = active;
        _exportHtml.IsEnabled = active;
        _backup.IsEnabled = active;
        _defectKey.IsEnabled = active;
        _defectTitle.IsEnabled = active;
        _defectDescription.IsEnabled = active;
        _defectSeverity.IsEnabled = active;
        _addDefect.IsEnabled = active;
        var selectedDefectId = (_defectList.SelectedItem as WorkspaceDefect)?.Id;
        _defectList.ItemsSource = _workspace?.Defects.ToArray() ?? Array.Empty<WorkspaceDefect>();
        if (selectedDefectId.HasValue)
            _defectList.SelectedItem = _workspace?.Defects.FirstOrDefault(d => d.Id == selectedDefectId.Value);
        RenderDefectSelection();
        _testTitle.IsEnabled = active;
        _testDescription.IsEnabled = active;
        _testTemplate.IsEnabled = active;
        _addTest.IsEnabled = active;

        _workspaceSummary.Text = active
            ? $"{_workspace!.Name} · {TestLoomPresetInfo.Card(_workspace.PresetId, _workspace.Language).DisplayName}{Environment.NewLine}{SummaryText(WorkspaceStatistics.Calculate(_workspace))}"
            : (_recoveryBlockedBySchema
                ? L("This workspace needs a compatible TestLoom version. Previous data is unchanged.")
                : _initialLoadFailed
                    ? L("Workspace cannot be read. Use recovery if a validated backup is available.")
                    : L("Create a new workspace to begin."));
        var selectedRequirementId = (_requirementList.SelectedItem as WorkspaceRequirement)?.Id;
        _requirementList.ItemsSource = _workspace?.Requirements.ToArray()
            ?? Array.Empty<WorkspaceRequirement>();
        if (selectedRequirementId.HasValue)
            _requirementList.SelectedItem = _workspace?.Requirements.FirstOrDefault(
                r => r.Id == selectedRequirementId.Value);
        RenderRequirementSelection();
        _testRequirement.ItemsSource = new[] { L("No linked requirement") }
            .Concat(_workspace?.Requirements.Select(x => x.ToString())
                    ?? Enumerable.Empty<string>()).ToArray();
        _testRequirement.SelectedIndex = 0;
        if (_workspace is not null)
        {
            var coverage = RequirementCoverage.Calculate(_workspace);
            _coverageSummary.Text =
                $"{coverage.TotalRequirements} {L("total")} · {coverage.LinkedRequirements} {L("linked to tests")} · " +
                $"{coverage.RequirementsWithPassingTest} {L("with at least one passing test")}";
        }
        else
            _coverageSummary.Text = L("Create a workspace to manage requirements.");

        var templateOptions = AvailableTemplates();
        _testTemplate.ItemsSource = templateOptions;
        if (templateOptions.Count > 0) _testTemplate.SelectedIndex = 0;

        var selectedId = (_caseList.SelectedItem as WorkspaceTestCase)?.Id;
        _caseList.ItemsSource = null;
        _caseList.ItemsSource = _workspace is null ? Array.Empty<WorkspaceTestCase>()
            : TestCaseFilter.Apply(_workspace, new TestCaseFilter(
                _search.Text, (OutcomeFilter)Math.Max(0, _outcomeFilter.SelectedIndex), _showArchived.IsChecked == true,
                Priority: _priorityFilter.SelectedIndex > 0 ? (WorkspacePriority)(_priorityFilter.SelectedIndex - 1) : null,
                Tag: _tagFilter.Text,
                PlanId: _planFilter.SelectedIndex > 0 && _planFilter.SelectedIndex <= _workspace.Plans.Count
                    ? _workspace.Plans[_planFilter.SelectedIndex - 1].Id : null));
        if (selectedId.HasValue)
            _caseList.SelectedItem = _workspace?.TestCases.FirstOrDefault(t => t.Id == selectedId.Value);
        RenderSelection();
        RefreshFeatures();
    }

    private void RenderSelection()
    {
        var selected = _caseList.SelectedItem as WorkspaceTestCase;
        var enabled = selected is not null;
        _showArchived.IsEnabled = _workspace is not null;
        _editTitle.IsEnabled = enabled && selected?.Archived != true;
        _editDescription.IsEnabled = enabled && selected?.Archived != true;
        _saveEdits.IsEnabled = enabled && selected?.Archived != true;
        _archiveToggle.IsEnabled = enabled;
        _editTitle.Text = selected?.Title ?? "";
        _editDescription.Text = selected?.Description ?? "";
        _archiveToggle.Content = L(selected?.Archived == true ? "Restore test" : "Archive test");
        var canExecute = enabled && selected?.Archived != true;
        _existingTestRequirement.ItemsSource = new[] { L("Select a requirement") }
            .Concat(_workspace?.Requirements.Select(r => r.ToString())
                    ?? Enumerable.Empty<string>()).ToArray();
        _existingTestRequirement.SelectedIndex = 0;
        _existingTestRequirement.IsEnabled = canExecute && _workspace?.Requirements.Count > 0;
        RefreshLinkButtons();
        _runNotes.IsEnabled = canExecute;
        _pass.IsEnabled = canExecute;
        _fail.IsEnabled = canExecute;
        _block.IsEnabled = canExecute;
        var previousStep = (_stepList.SelectedItem as WorkspaceTestStep)?.Id;
        _stepList.ItemsSource = selected?.Steps.ToArray() ?? Array.Empty<WorkspaceTestStep>();
        if (previousStep.HasValue)
            _stepList.SelectedItem = selected?.Steps.FirstOrDefault(s => s.Id == previousStep.Value);
        _newStepAction.IsEnabled = canExecute;
        _newStepExpected.IsEnabled = canExecute;
        _addStep.IsEnabled = canExecute;
        _pasteSteps.IsEnabled = canExecute;
        UpdateStepActions();
        _caseDetails.Text = selected is null
            ? L("Select a test case to review its history or record an execution.")
            : $"{selected.Title}\n{L("Template")}: {selected.TemplateName} · {L("Priority")}: {L(selected.Priority.ToString())}" +
              (selected.Tags.Count > 0 ? $" · {L("Tags")}: {string.Join(", ", selected.Tags)}" : "") + "\n" +
              $"{selected.Description}\n" +
              $"{L("Requirements")}: {string.Join(", ", _workspace?.Requirements
                  .Where(r => selected.RequirementIds.Contains(r.Id))
                  .Select(r => r.Key) ?? Enumerable.Empty<string>())}\n" +
              $"{L("Executions")}: {selected.Executions.Count}\n" +
              (selected.Executions.Count == 0 ? L("No executions recorded.")
                  : string.Join("\n", selected.Executions.TakeLast(5).Select(e =>
                      $"{e.ExecutedUtc:yyyy-MM-dd HH:mm} UTC · {L(e.Result.ToString())}" +
                      (e.Tester.Length > 0 ? " · " + e.Tester : "") +
                      (e.Environment.Length > 0 ? " · " + e.Environment : "") +
                      (e.BuildVersion.Length > 0 ? " · " + e.BuildVersion : "") +
                      (e.DurationSeconds > 0 ? $" · {e.DurationSeconds / 60} min" : "") +
                      (e.Notes.Length > 0 ? " · " + e.Notes : "") +
                      (e.Evidence.Count > 0 ? "\n    " + string.Join("  ", e.Evidence.Select(EvidenceLine)) : ""))));
        RenderFeatureSelection();
        RenderPlanSelection();
    }

    private void SetStatus(string message) => _status.Text = WorkspaceLocalization.TMessage(message, Lang);
}
