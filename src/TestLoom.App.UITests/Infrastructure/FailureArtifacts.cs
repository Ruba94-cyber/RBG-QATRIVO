using System.Drawing.Imaging;
using System.Text;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Infrastructure;

/// <summary>
/// Diagnostica di un test UI fallito (Fase 10): screenshot, nome test, timestamp, finestra attiva, step
/// corrente, elemento cercato (AutomationId/Name/ControlType), PID TestLoom, eccezione completa, albero UI
/// automation e percorso dell'artifact — tutto sotto TestResults/UIFailures/&lt;test&gt;/&lt;timestamp&gt;/,
/// così ogni fallimento resta ispezionabile anche molto dopo che il processo TestLoom è già stato chiuso.
/// </summary>
public static class FailureArtifacts
{
    public static string Capture(
        TestLoomApplication? app,
        string testName,
        Exception exception,
        string? step = null,
        string? searchedAutomationId = null,
        string? searchedName = null,
        string? searchedControlType = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var dir = Path.Combine(AppContext.BaseDirectory, "TestResults", "UIFailures", Sanitize(testName), stamp);
        Directory.CreateDirectory(dir);

        var screenshotPath = Path.Combine(dir, "screenshot.png");
        try
        {
            var window = app?.TryGetMainWindowSnapshot();
            if (window != null)
            {
                using var bitmap = window.Capture();
                bitmap.Save(screenshotPath, ImageFormat.Png);
            }
            // Lo schermo intero viene salvato SEMPRE: la cattura della sola finestra puo' risultare ritagliata
            // (DPI/finestra oltre il monitor) e nasconderebbe dialoghi e popup fuori dalla finestra principale.
            using var image = FlaUI.Core.Capturing.Capture.Screen();
            image.ToFile(window != null ? Path.Combine(dir, "screenshot_fullscreen.png") : screenshotPath);
        }
        catch (Exception captureEx)
        {
            File.WriteAllText(Path.Combine(dir, "screenshot_failed.txt"), captureEx.ToString());
        }

        var info = new StringBuilder();
        info.AppendLine($"Test: {testName}");
        info.AppendLine($"Timestamp: {DateTime.Now:O}");
        info.AppendLine($"Step: {step ?? "(non specificato)"}");
        info.AppendLine($"TestLoom PID: {(app?.ProcessId.ToString() ?? "(processo non disponibile)")}");
        info.AppendLine($"TestLoom in esecuzione: {(app?.IsRunning.ToString() ?? "sconosciuto")}");
        info.AppendLine($"Elemento cercato — AutomationId: {searchedAutomationId ?? "(n/a)"}");
        info.AppendLine($"Elemento cercato — Name: {searchedName ?? "(n/a)"}");
        info.AppendLine($"Elemento cercato — ControlType: {searchedControlType ?? "(n/a)"}");
        info.AppendLine();
        info.AppendLine("Finestre visibili al momento del fallimento:");
        try
        {
            foreach (var w in app?.ListTopLevelWindowNames() ?? Array.Empty<string>())
                info.AppendLine($"  - {w}");
        }
        catch (Exception ex) { info.AppendLine($"  (non enumerabili: {ex.Message})"); }
        info.AppendLine();
        info.AppendLine("Eccezione completa:");
        info.AppendLine(exception.ToString());
        File.WriteAllText(Path.Combine(dir, "failure.txt"), info.ToString());

        try
        {
            var window = app?.TryGetMainWindowSnapshot();
            if (window != null) File.WriteAllText(Path.Combine(dir, "ui-tree.txt"), DumpTree(window, 0, maxDepth: 6));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "ui-tree_failed.txt"), ex.ToString()); }

        return dir;
    }

    private static string DumpTree(AutomationElement element, int depth, int maxDepth)
    {
        var sb = new StringBuilder();
        AppendNode(sb, element, depth, maxDepth);
        return sb.ToString();
    }

    private static void AppendNode(StringBuilder sb, AutomationElement element, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        string automationId, name, controlType;
        try { automationId = element.AutomationId; } catch { automationId = "?"; }
        try { name = element.Name; } catch { name = "?"; }
        try { controlType = element.ControlType.ToString(); } catch { controlType = "?"; }
        sb.AppendLine($"{new string(' ', depth * 2)}[{controlType}] Id='{automationId}' Name='{name}'");

        AutomationElement[] children;
        try { children = element.FindAllChildren(); }
        catch { return; }
        foreach (var child in children) AppendNode(sb, child, depth + 1, maxDepth);
    }

    private static string Sanitize(string testName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = testName.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
