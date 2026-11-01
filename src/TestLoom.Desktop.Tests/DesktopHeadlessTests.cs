using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using System.Reflection;
using System.Runtime.CompilerServices;
using TestLoom.Core;
using TestLoom.Desktop;

namespace TestLoom.Desktop.Tests;

/// <summary>Same theme as the product but without opening its main window.</summary>
public sealed class TestLoomApplicationForTests : Application
{
    public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
}

/// <summary>
/// One headless Avalonia session for the whole test process. A dedicated thread owns the UI dispatcher and keeps
/// draining it, tests marshal their UI work onto it.
/// </summary>
internal static class UiSession
{
    private static readonly ManualResetEventSlim Ready = new();

    [ModuleInitializer]
    internal static void Pump()
    {
        var thread = new Thread(() =>
        {
            AppBuilder.Configure<TestLoomApplicationForTests>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
            Ready.Set();
            while (true)
            {
                try { Dispatcher.UIThread.RunJobs(); } catch { /* a failing job must not stop the pump */ }
                Thread.Sleep(5);
            }
        }) { IsBackground = true, Name = "TestLoom-Ui-Pump" };
        thread.SetApartmentState(ApartmentState.STA);
        // Do not wait here: a module initializer holds the assembly's load lock, and the pump thread needs it.
        thread.Start();
    }

    public static void Run(Action action) { Ready.Wait(); Dispatcher.UIThread.Invoke(action); }
    public static T Run<T>(Func<T> func) { Ready.Wait(); return Dispatcher.UIThread.Invoke(func); }
    public static Task<T> RunAsync<T>(Func<Task<T>> func) { Ready.Wait(); return Dispatcher.UIThread.InvokeAsync(func); }
}

[CollectionDefinition("ui", DisableParallelization = true)]
public sealed class UiCollection { }

[Collection("ui")]
public sealed class DesktopHeadlessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom-Ui-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private MainWindow OpenWithWorkspace(string language = "en", bool samples = true)
    {
        new WorkspaceStore(_root).Save(WorkspaceStarter.Create("UI test", "software-qa", language, samples, "Ada"));
        var window = new MainWindow(_root);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string[] Headers(Window window) =>
        window.GetLogicalDescendants().OfType<TabControl>().Single().Items.OfType<TabItem>()
            .Select(t => t.Header?.ToString() ?? "").ToArray();

    [Fact]
    public void FirstRunOffersEveryPresetAndNeedsAName() => UiSession.Run(() =>
    {
        var window = new FirstRunWindow("en");
        window.Show();
        var presets = Field<ListBox>(window, "_presets");
        var create = Field<Button>(window, "_create");
        Assert.Equal(TestLoomWorkspacePresets.All.Count, presets.ItemCount);
        Assert.False(create.IsEnabled);
        Field<TextBox>(window, "_name").Text = "Plant audit";
        Assert.True(create.IsEnabled);

        Field<ComboBox>(window, "_language").SelectedIndex = 1;
        Assert.Equal("Qualità software", ((IEnumerable<string>)presets.ItemsSource!).First());
        Assert.Equal("Crea area di lavoro", create.Content);
        window.Close();
    });

    [Fact]
    public async Task FirstRunReturnsTheChoiceItWasGiven()
    {
        var choice = await UiSession.RunAsync(async () =>
        {
            var owner = new Window();
            owner.Show();
            var dialog = new FirstRunWindow("it");
            var pending = dialog.ShowDialog<FirstRunChoice?>(owner);
            Field<TextBox>(dialog, "_name").Text = "Reparto";
            Field<TextBox>(dialog, "_tester").Text = "Ada";
            Field<ListBox>(dialog, "_presets").SelectedIndex = 2;
            Field<CheckBox>(dialog, "_samples").IsChecked = true;
            Click(Field<Button>(dialog, "_create"));
            var result = await pending;
            owner.Close();
            return result;
        });
        Assert.NotNull(choice);
        Assert.Equal(("Reparto", "manufacturing", "it", true, "Ada"),
            (choice!.Name, choice.PresetId, choice.Language, choice.IncludeSamples, choice.Tester));
    }

    [Fact]
    public async Task FirstRunCanBePostponed()
    {
        var choice = await UiSession.RunAsync(async () =>
        {
            var owner = new Window();
            owner.Show();
            var dialog = new FirstRunWindow("en");
            var pending = dialog.ShowDialog<FirstRunChoice?>(owner);
            Click(Field<Button>(dialog, "_cancel"));
            var result = await pending;
            owner.Close();
            return result;
        });
        Assert.Null(choice);
    }

    [Fact]
    public void MainWindowShowsAllTabsAndTranslatesThem() => UiSession.Run(() =>
    {
        var window = OpenWithWorkspace("en");
        Assert.Equal(new[] { "Workspace", "Requirements", "Tests", "Execute", "Plans", "Templates", "Defects", "Reports", "Audit" }, Headers(window));

        Field<ComboBox>(window, "_languagePicker").SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var italian = Headers(window);
        Assert.Equal("Area di lavoro", italian[0]);
        Assert.Equal("Esegui", italian[3]);
        Assert.Equal("it", new WorkspaceStore(_root).Load()!.Language);
        window.Close();
    });

    [Fact]
    public void RecordingARunFromTheUiPersistsItWithTesterAndEnvironment() => UiSession.Run(() =>
    {
        var window = OpenWithWorkspace();
        Field<ListBox>(window, "_caseList").SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Ada", Field<TextBox>(window, "_runTester").Text);
        Field<TextBox>(window, "_runEnvironment").Text = "bench 3";
        Field<TextBox>(window, "_runMinutes").Text = "5";
        Click(Field<Button>(window, "_pass"));
        Dispatcher.UIThread.RunJobs();

        var saved = new WorkspaceStore(_root).Load()!;
        var run = Assert.Single(saved.TestCases[0].Executions);
        Assert.Equal(ExecutionResult.Passed, run.Result);
        Assert.Equal("bench 3", run.Environment);
        Assert.Equal(300, run.DurationSeconds);
        Assert.Contains(saved.Audit, a => a.Action == "test.run");
        window.Close();
    });

    [Fact]
    public void AnInvalidDurationIsRefusedAndNothingIsSaved() => UiSession.Run(() =>
    {
        var window = OpenWithWorkspace();
        Field<ListBox>(window, "_caseList").SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Field<TextBox>(window, "_runMinutes").Text = "abc";
        Click(Field<Button>(window, "_pass"));
        Assert.Empty(new WorkspaceStore(_root).Load()!.TestCases[0].Executions);
        Assert.Contains("minutes", Field<TextBlock>(window, "_status").Text!);
        window.Close();
    });

    [Fact]
    public void StepByStepRunNeedsEveryStepAndThenRecordsTheDerivedResult() => UiSession.Run(() =>
    {
        var window = OpenWithWorkspace();
        Field<ListBox>(window, "_caseList").SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        var steps = Field<ListBox>(window, "_runStepList");
        Assert.Equal(2, steps.ItemCount);

        steps.SelectedIndex = 0;
        Click(Field<Button>(window, "_stepPass"));
        Click(Field<Button>(window, "_finishStepRun"));
        Assert.Empty(new WorkspaceStore(_root).Load()!.TestCases[0].Executions);

        steps.SelectedIndex = 1;
        Click(Field<Button>(window, "_stepFail"));
        Click(Field<Button>(window, "_finishStepRun"));
        var run = Assert.Single(new WorkspaceStore(_root).Load()!.TestCases[0].Executions);
        Assert.Equal(ExecutionResult.Failed, run.Result);
        Assert.Equal(2, run.StepResults.Count);
        window.Close();
    });

    [Fact]
    public void CreatingAPlanAndAddingTheSelectedTestIsSaved() => UiSession.Run(() =>
    {
        var window = OpenWithWorkspace(samples: true);
        Field<TextBox>(window, "_planName").Text = "Release gate";
        Click(Field<Button>(window, "_addPlan"));
        Dispatcher.UIThread.RunJobs();
        var saved = new WorkspaceStore(_root).Load()!;
        Assert.Contains(saved.Plans, p => p.Name == "Release gate");

        Field<ListBox>(window, "_planList").SelectedIndex = saved.Plans.FindIndex(p => p.Name == "Release gate");
        Field<ListBox>(window, "_caseList").SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Click(Field<Button>(window, "_addToPlan"));
        var after = new WorkspaceStore(_root).Load()!;
        Assert.Single(after.Plans.Single(p => p.Name == "Release gate").TestIds);
        window.Close();
    });

    [Fact]
    public void EditingTagsPriorityAndOwnerIsSaved() => UiSession.Run(() =>
    {
        var window = OpenWithWorkspace();
        Field<ListBox>(window, "_caseList").SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Field<ComboBox>(window, "_editPriority").SelectedIndex = (int)WorkspacePriority.Critical;
        Field<TextBox>(window, "_editOwner").Text = "Grace";
        Field<TextBox>(window, "_editTags").Text = "ui, smoke , ui";
        Click(Field<Button>(window, "_saveEdits"));
        var test = new WorkspaceStore(_root).Load()!.TestCases[0];
        Assert.Equal((WorkspacePriority.Critical, "Grace"), (test.Priority, test.Owner));
        Assert.Equal(new[] { "ui", "smoke" }, test.Tags);
        window.Close();
    });

    [Fact]
    public void ADuplicatedTestIsASeparateCopy() => UiSession.Run(() =>
    {
        var window = OpenWithWorkspace();
        Field<ListBox>(window, "_caseList").SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Click(Field<Button>(window, "_cloneTest"));
        var saved = new WorkspaceStore(_root).Load()!;
        Assert.Equal(2, saved.TestCases.Count);
        Assert.StartsWith("Copy of ", saved.TestCases[1].Title);
        Assert.NotEqual(saved.TestCases[0].Steps[0].Id, saved.TestCases[1].Steps[0].Id);
        window.Close();
    });

    [Fact]
    public void AMissingWorkspaceLeavesEveryEditorDisabledUntilCreated() => UiSession.Run(() =>
    {
        var window = new MainWindow(_root);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(Field<Button>(window, "_addTest").IsEnabled);
        Assert.False(Field<Button>(window, "_addPlan").IsEnabled);
        Assert.False(Field<Button>(window, "_importTests").IsEnabled);
        Assert.True(Field<Button>(window, "_restoreBackup").IsEnabled);
        Assert.False(File.Exists(Path.Combine(_root, "workspace.json")));
        window.Close();
    });
}
