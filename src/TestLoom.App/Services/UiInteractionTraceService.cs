using System.Collections.Concurrent;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace TestLoom.App.Services;

/// <summary>
/// Local, bounded, daily UI breadcrumb log for reproducing non-crashing defects.
/// Logs control identity and event KIND, NEVER entered text, selections, password/PIN,
/// token, descriptions, field values or source document payloads.
/// Does not replace the durable per-entity audit or DiagnosticTraceService.
/// </summary>
public static class UiInteractionTraceService
{
    private const long MaxFileBytes = 1024 * 1024; // two 1-MiB segments per day
    private static readonly object Gate = new();
    private static string _lastPrunedKey = "";
    private static readonly ConcurrentDictionary<string, DateTime> Recent = new(StringComparer.Ordinal);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Installed = new();
    private static readonly object InstalledValue = new();
    private static string DefaultDirectory => Path.Combine(TestLoomDataRoot.AppDataRoot, "TestLoom");
    internal static string? DirectoryForTest { get; set; }
    private static string DirectoryPath => DirectoryForTest ?? DefaultDirectory;

    /// <summary>Attach once to a Window/UserControl root; handles future dynamic children.</summary>
    public static void Attach(Control root, string area)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (Installed.TryGetValue(root, out _)) return;
        Installed.Add(root, InstalledValue);
        var category = SafeLabel(area);
        root.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is ToggleButton toggle)
                Record(category, toggle.IsChecked switch
                {
                    true => "toggle-on",
                    false => "toggle-off",
                    _ => "toggle-indeterminate"
                }, ControlName(toggle));
            else if (e.Source is Control source)
                Record(category, "click", ControlName(source));
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        // Tunnel observes table headers/rows and custom filter controls even when
        // an inner control handles the pointer event. Only control identity is logged.
        root.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Control source && source is not Button && source is not TextBox)
                Record(category, "pointer", ControlName(source),
                    debounce: TimeSpan.FromMilliseconds(250));
        }, RoutingStrategies.Tunnel);
        // Record ONLY fixed shortcuts, never ordinary keys or entered characters.
        root.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
            if (ctrl && !alt && e.Key is Key.S or Key.F or Key.Z or Key.Y)
                Record(category, "shortcut", "Ctrl-" + e.Key);
            else if (e.Key == Key.Escape)
                Record(category, "shortcut", "Escape", debounce: TimeSpan.FromMilliseconds(250));
        }, RoutingStrategies.Tunnel);
        root.AddHandler(TextBox.TextChangedEvent, (_, e) =>
        {
            if (e.Source is TextBox box && box.IsFocused && box.PasswordChar == default)
                Record(category, "text-edited", ControlName(box), debounce: TimeSpan.FromSeconds(2));
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(SelectingItemsControl.SelectionChangedEvent, (_, e) =>
        {
            if (e.Source is SelectingItemsControl selection && root.IsVisible)
                Record(category, "selection-index-" + selection.SelectedIndex,
                    ControlName(selection), debounce: TimeSpan.FromMilliseconds(450));
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        Record(category, "view-attached", root.GetType().Name);
    }

    public static void Record(string area, string action, string? controlId = null,
        TimeSpan? debounce = null)
    {
        var safeArea = SafeLabel(area);
        var safeAction = SafeLabel(action);
        var safeControl = SafeLabel(controlId);
        if (safeControl.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            safeControl.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            safeControl.Contains("pin", StringComparison.OrdinalIgnoreCase) ||
            safeControl.Contains("secret", StringComparison.OrdinalIgnoreCase))
            safeControl = "sensitive-field";
        var now = DateTime.Now;
        var key = safeArea + "|" + safeAction + "|" + safeControl;
        var cooldown = debounce ?? TimeSpan.Zero;
        if (cooldown > TimeSpan.Zero && Recent.TryGetValue(key, out var previous) && now - previous < cooldown)
            return;
        if (cooldown > TimeSpan.Zero)
        {
            if (Recent.Count > 4096) Recent.Clear(); // bounded UI debounce metadata
            Recent[key] = now;
        }
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                // Keep yesterday + today: at any time includes the most recent 24 hours.
                // Crash, operation and SystemAudit logs have separate, longer policies.
                var today = now.ToString("yyyy-MM-dd");
                // Include the destination path in the pruning key: isolated test
                // workspaces must not reuse another directory's pruning state.
                var pruneKey = DirectoryPath + "|" + today;
                if (_lastPrunedKey != pruneKey)
                {
                    PruneOtherDays(today, now.AddDays(-1).ToString("yyyy-MM-dd"));
                    _lastPrunedKey = pruneKey;
                }
                var file = Path.Combine(DirectoryPath, "ui-" + today + ".log");
                if (File.Exists(file) && new FileInfo(file).Length >= MaxFileBytes)
                {
                    var previousSegment = Path.Combine(DirectoryPath, "ui-" + today + ".1.log");
                    File.Move(file, previousSegment, overwrite: true);
                }
                var entry = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [UI] {safeArea} {safeAction} {safeControl}{Environment.NewLine}";
                File.AppendAllText(file, entry, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Logging must never prevent Test/NC/CR edits or destabilize the UI.
        }
    }

    public static IReadOnlyList<string> FilePresenti()
    {
        lock (Gate)
        {
            if (!Directory.Exists(DirectoryPath)) return Array.Empty<string>();
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
            return Directory.EnumerateFiles(DirectoryPath, "ui-*.log")
                .Where(file => Path.GetFileName(file).StartsWith("ui-" + today, StringComparison.Ordinal) ||
                               Path.GetFileName(file).StartsWith("ui-" + yesterday, StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        }
    }

    private static string ControlName(Control control)
    {
        var id = AutomationProperties.GetAutomationId(control);
        if (!string.IsNullOrWhiteSpace(id)) return id;
        if (!string.IsNullOrWhiteSpace(control.Name)) return control.Name;
        // Never read Content, Text, SelectedItem, DataContext, Tooltip or Tag.
        return control.GetType().Name;
    }

    private static string SafeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var chars = value.Take(96).Select(c =>
            char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray();
        return new string(chars);
    }

    private static void PruneOtherDays(string today, string yesterday)
    {
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "ui-*.log"))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("ui-" + today, StringComparison.Ordinal) ||
                name.StartsWith("ui-" + yesterday, StringComparison.Ordinal)) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
