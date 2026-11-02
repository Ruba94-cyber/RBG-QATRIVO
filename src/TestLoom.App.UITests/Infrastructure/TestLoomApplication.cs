using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace TestLoom.App.UITests.Infrastructure;

/// <summary>
/// Avvia TestLoom.exe come processo esterno reale (mai in-process) puntato su un <see cref="TestWorkspace"/>
/// isolato tramite l'argomento <c>--config</c> già previsto da App.axaml.cs proprio per i collaudi end-to-end
/// isolati, e parla con la sua UI reale via UI Automation (UIA3, il default richiesto). Vive per la durata di
/// un singolo test: <see cref="Dispose"/> chiude la finestra e, se non si chiude entro pochi secondi, termina
/// il processo — un test non deve mai lasciare un TestLoom.exe orfano che tenga occupato il mutex single-instance
/// per i test successivi.
/// </summary>
public sealed class TestLoomApplication : IDisposable
{
    internal const string LockContesaBarrierEnvironmentVariable = "TestLoom_UI_TEST_MASTER_DATA_LOCK_BARRIER";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, KeyValuePair<string, string>>
        TestStartupEnvironmentByWorkspace = new(StringComparer.OrdinalIgnoreCase);

    private readonly UIA3Automation _automation;
    private readonly Application _app;

    private TestLoomApplication(Application app, UIA3Automation automation)
    {
        _app = app;
        _automation = automation;
    }

    public int ProcessId => _app.ProcessId;
    public bool IsRunning => !_app.HasExited;

    /// <summary>Registra un override d'ambiente solo per il successivo processo TestLoom avviato con questo workspace.
    /// Il processo testhost non modifica il proprio ambiente.</summary>
    internal static void SetTestStartupEnvironmentVariable(TestWorkspace workspace, string name, string value)
    {
        var workspaceKey = Path.GetFullPath(workspace.ConfigPath);
        if (!TestStartupEnvironmentByWorkspace.TryAdd(workspaceKey, new KeyValuePair<string, string>(name, value)))
            throw new InvalidOperationException($"È già configurato un ambiente di avvio UI test per {workspaceKey}.");
    }

    public static TestLoomApplication Launch(TestWorkspace workspace)
    {
        TestLoomProcessGuard.EnsureNoRunningInstance();
        var exePath = TestLoomExeLocator.Resolve();
        var processStartInfo = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            UseShellExecute = false
        };
        processStartInfo.ArgumentList.Add("--config");
        processStartInfo.ArgumentList.Add(workspace.ConfigPath);
        if (TestStartupEnvironmentByWorkspace.TryRemove(Path.GetFullPath(workspace.ConfigPath), out var overrideValue))
            processStartInfo.Environment[overrideValue.Key] = overrideValue.Value;

        var app = Application.Launch(processStartInfo);
        return new TestLoomApplication(app, new UIA3Automation());
    }

    /// <summary>Attende la finestra principale (Title="TestLoom") e la ritorna. Sotto MostraPrimoAvvio=true questa
    /// stessa finestra ospita FirstRunView — chi chiama sa già, in base a come ha creato il workspace, se si
    /// aspetta la Home o la schermata di login: questo metodo si limita a garantire che la finestra esista ed
    /// esponga un elemento via UI Automation, non decide quale schermata contiene.
    ///
    /// Su un bootstrap lento (indice locale assente/scaduto) può comparire prima una finestra di avvio
    /// transitoria (AvvioWindow.axaml, AutomationId="AvvioWindow", titolo "Apertura di TestLoom…" che NON inizia
    /// per "TestLoom") mostrata da App.axaml.cs mentre AppState viene costruito in background. Va esclusa sia dal
    /// match per titolo sia — soprattutto — dal fallback GetMainWindow(), che seleziona per finestra "main" di
    /// processo a livello Win32 e potrebbe restituire la finestra di avvio se è l'unica presente in quel
    /// momento: senza questa esclusione esplicita la suite UI rischierebbe di agganciarsi alla finestra
    /// sbagliata invece di attendere la finestra principale reale.</summary>
    private const string AvvioWindowAutomationId = "AvvioWindow";

    public Window WaitForMainWindow(TimeSpan? timeout = null) =>
        Wait.Until<Window>(
            () =>
            {
                if (_app.HasExited) return null;
                var windows = _app.GetAllTopLevelWindows(_automation);
                return windows.FirstOrDefault(w =>
                        (w.Title == "TestLoom" || w.Title.StartsWith("TestLoom")) && w.AutomationId != AvvioWindowAutomationId)
                    ?? _app.GetMainWindow(_automation, TimeSpan.FromMilliseconds(500));
            },
            w => w != null && w.AutomationId != "Input Indicator overlay window" && w.AutomationId != AvvioWindowAutomationId,
            timeout ?? TimeSpan.FromSeconds(30),
            "finestra principale TestLoom");

    /// <summary>Versione non eccezionale usata solo dalla diagnostica di fallimento (FailureArtifacts): non
    /// deve mai lanciare, altrimenti un test già in fallimento perderebbe l'eccezione originale.</summary>
    public Window? TryGetMainWindowSnapshot()
    {
        try
        {
            if (_app.HasExited) return null;
            var windows = _app.GetAllTopLevelWindows(_automation);
            return windows.FirstOrDefault(w => w.Title == "TestLoom" || w.Title.StartsWith("TestLoom"))
                ?? _app.GetMainWindow(_automation, TimeSpan.FromMilliseconds(500));
        }
        catch { return null; }
    }

    /// <summary>Attende e ritorna una finestra secondaria (dialog modale: ConfirmDialog, SelezionaDialog...)
    /// identificata da un AutomationId che contiene, non dal titolo: sia ConfirmDialog.Crea sia
    /// SelezionaDialog.Crea impostano Title dinamicamente al messaggio/etichetta passato dal chiamante (mai
    /// una stringa fissa), quindi il titolo cambia da un punto di chiamata all'altro e da IT a EN — cercare per
    /// titolo sarebbe esattamente il locator fragile che questa suite evita altrove.</summary>
    public Window WaitForWindowByContent(string automationId, TimeSpan? timeout = null) =>
        Wait.Until<Window>(
            () => _app.HasExited ? null : _app.GetAllTopLevelWindows(_automation)
                .FirstOrDefault(w => w.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) != null),
            _ => true,
            timeout ?? TimeSpan.FromSeconds(10),
            $"finestra secondaria contenente AutomationId='{automationId}'");

    /// <summary>Attende la prima finestra di primo livello che soddisfa <paramref name="predicato"/> (es. un
    /// ConfirmDialog riconosciuto dal testo del suo messaggio, quando più dialog hanno gli stessi AutomationId).</summary>
    public Window WaitForWindow(Func<Window, bool> predicato, TimeSpan? timeout = null, string? descrizione = null) =>
        Wait.Until<Window>(
            () => _app.HasExited ? null : _app.GetAllTopLevelWindows(_automation).FirstOrDefault(predicato),
            _ => true,
            timeout ?? TimeSpan.FromSeconds(10),
            descrizione ?? "finestra secondaria che soddisfa il predicato");

    /// <summary>Vero se nessuna finestra di primo livello contiene più l'AutomationId indicato — usato per
    /// verificare che un dialog si sia davvero chiuso dopo Annulla/Conferma/Esc.</summary>
    public bool IsWindowClosedByContent(string automationId)
    {
        if (_app.HasExited) return true;
        try { return _app.GetAllTopLevelWindows(_automation).All(w => w.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) == null); }
        catch { return true; }
    }

    /// <summary>Elemento con il focus da tastiera in questo momento, per verificare gli spostamenti di focus
    /// (Tab/Shift+Tab, focus dopo un dialog o dopo un salvataggio) senza dover indovinare quale elemento
    /// abbia ricevuto il focus in altro modo.</summary>
    public FlaUI.Core.AutomationElements.AutomationElement? FocusedElement() =>
        _app.HasExited ? null : _automation.FocusedElement();

    public string? SafeFocusedElementId()
    {
        if (_app.HasExited) return null;
        try
        {
            var el = _automation.FocusedElement();
            return el?.Properties.AutomationId.ValueOrDefault;
        }
        catch { return null; }
    }

    public IReadOnlyList<string> ListTopLevelWindowNames()
    {
        if (_app.HasExited) return Array.Empty<string>();
        return _app.GetAllTopLevelWindows(_automation).Select(w => w.Title).ToList();
    }

    /// <summary>Tutte le finestre di primo livello del processo TestLoom (principale, dialog Avalonia e finestre
    /// native del sistema operativo come i dialog Apri/Salva con nome, che appartengono al processo).</summary>
    public IReadOnlyList<Window> TopLevelWindows()
    {
        if (_app.HasExited) return Array.Empty<Window>();
        return _app.GetAllTopLevelWindows(_automation);
    }

    /// <summary>Chiude le finestre di primo livello diverse dalla principale, cioè i dialog che il test ha lasciato aperti.
    /// Un dialog modale disabilita la finestra principale e la sua chiusura viene ignorata: TestLoom non termina, FlaUI
    /// segnala "Application failed to exit" e il processo viene terminato a forza. Best effort: un dialog che rifiuta
    /// la chiusura resta aperto, e la chiusura della principale viene comunque tentata.</summary>
    private void ChiudiFinestreSecondarie()
    {
        try
        {
            var principale = System.Diagnostics.Process.GetProcessById(_app.ProcessId).MainWindowHandle;
            if (principale == IntPtr.Zero) return;
            // Un dialog modale è una finestra di proprietà della principale (FlaUI le omette da GetAllTopLevelWindows).
            var dialog = FinestreWin32DelProcesso((uint)_app.ProcessId)
                .Where(f => f.Visibile && f.Handle != principale && f.Proprietario != IntPtr.Zero)
                .ToList();
            foreach (var dialogAperto in dialog)
            {
                var inviato = PostMessage(dialogAperto.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (DiagnosticaAttiva) Console.WriteLine($"[TestLoom-DIAG] WM_CLOSE a '{dialogAperto.Titolo}' hwnd={dialogAperto.Handle}: inviato={inviato}");
            }
            // Chiusura asincrona: la principale torna abilitata quando il dialog è davvero chiuso.
            if (dialog.Count > 0)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var abilitata = Wait.TryForCondition(() => IsWindowEnabled(principale), TimeSpan.FromSeconds(3));
                if (DiagnosticaAttiva)
                {
                    var rimasti = FinestreWin32DelProcesso((uint)_app.ProcessId)
                        .Where(f => f.Visibile && f.Proprietario != IntPtr.Zero).Select(f => $"'{f.Titolo}'");
                    Console.WriteLine($"[TestLoom-DIAG] principale abilitata={abilitata} dopo {sw.ElapsedMilliseconds} ms; possedute ancora visibili: {string.Join(", ", rimasti)}");
                }
            }
        }
        catch (Exception ex)
        {
            if (DiagnosticaAttiva) Console.WriteLine($"[TestLoom-DIAG] chiusura dei dialog: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Chiude la finestra principale con il pattern Window di UI Automation (come il pulsante di chiusura). Usato solo
    /// quando la richiesta WM_CLOSE di <see cref="Dispose"/> non ha terminato il processo. Best effort.</summary>
    private void ChiudiFinestraPrincipaleUia()
    {
        try
        {
            var principale = _app.GetMainWindow(_automation, TimeSpan.FromMilliseconds(500));
            principale?.Patterns.Window.Pattern.Close();
        }
        catch (Exception ex)
        {
            if (DiagnosticaAttiva) Console.WriteLine($"[TestLoom-DIAG] chiusura UIA della principale: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Attiva solo con TestLoom_UITEST_CONSERVA_DATI=1 (diagnosi): nessun output nelle esecuzioni normali.</summary>
    private static bool DiagnosticaAttiva => Environment.GetEnvironmentVariable("TestLoom_UITEST_CONSERVA_DATI") == "1";

    /// <summary>Stato delle finestre del processo: handle della finestra principale secondo il sistema, titolo, abilitazione,
    /// e tutte le finestre Win32 del processo (anche quelle non elencate da UI Automation: proprietario, visibilità, classe).</summary>
    private void DiagnosticaFinestre(string momento)
    {
        if (!DiagnosticaAttiva) return;
        try
        {
            var processo = System.Diagnostics.Process.GetProcessById(_app.ProcessId);
            processo.Refresh();
            var finestre = _app.GetAllTopLevelWindows(_automation)
                .Select(w => $"'{w.Title}'[id={w.AutomationId}, abilitata={w.IsEnabled}, hwnd={w.Properties.NativeWindowHandle.ValueOrDefault}]");
            Console.WriteLine($"[TestLoom-DIAG {momento}] MainWindowHandle={processo.MainWindowHandle} titolo='{processo.MainWindowTitle}' UIA: {string.Join("; ", finestre)}");
            var win32 = FinestreWin32DelProcesso((uint)_app.ProcessId)
                .Select(f => $"hwnd={f.Handle} classe='{f.Classe}' titolo='{f.Titolo}' visibile={f.Visibile} abilitata={f.Abilitata} proprietario={f.Proprietario}");
            Console.WriteLine($"[TestLoom-DIAG {momento}] Win32: {string.Join("; ", win32)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestLoom-DIAG {momento}] errore: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;
    private const uint GW_OWNER = 4;

    /// <summary>Finestra Win32 di primo livello di un processo, con proprietario (zero se non ne ha).</summary>
    private readonly record struct FinestraWin32(IntPtr Handle, string Classe, string Titolo, bool Visibile, bool Abilitata, IntPtr Proprietario);

    /// <summary>Tutte le finestre Win32 di primo livello del processo, anche possedute e nascoste.</summary>
    private static List<FinestraWin32> FinestreWin32DelProcesso(uint pid)
    {
        var risultato = new List<FinestraWin32>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var processo);
            if (processo != pid) return true;
            var titolo = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, titolo, titolo.Capacity);
            var classe = new System.Text.StringBuilder(256);
            GetClassName(hWnd, classe, classe.Capacity);
            risultato.Add(new FinestraWin32(hWnd, classe.ToString(), titolo.ToString(), IsWindowVisible(hWnd),
                IsWindowEnabled(hWnd), GetWindow(hWnd, GW_OWNER)));
            return true;
        }, IntPtr.Zero);
        return risultato;
    }

    /// <summary>Diagnostica per il caso in cui TestLoom non termina entro 3 s dalla richiesta di chiusura: titolo, AutomationId e
    /// stato abilitato di ogni finestra aperta, nell'output del test runner (si vede subito quale dialog blocca la chiusura).</summary>
    private void DiagnosticaChiusuraIncompleta()
    {
        try
        {
            var finestre = _app.GetAllTopLevelWindows(_automation)
                .Select(w => $"'{w.Title}' (AutomationId={w.AutomationId}, abilitata={w.IsEnabled})");
            Console.WriteLine($"[TestLoom] Chiusura non completata entro 3 s. Finestre di primo livello: {string.Join("; ", finestre)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestLoom] Chiusura non completata entro 3 s; finestre non leggibili: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_app.HasExited)
            {
                DiagnosticaFinestre("prima della chiusura");
                ChiudiFinestreSecondarie();
                _app.Close();
                if (!Wait.TryForCondition(() => _app.HasExited, TimeSpan.FromSeconds(3)))
                {
                    DiagnosticaChiusuraIncompleta();
                    // Secondo tentativo con la chiusura della finestra via UI Automation (come il pulsante X), prima di terminare.
                    ChiudiFinestraPrincipaleUia();
                    if (Wait.TryForCondition(() => _app.HasExited, TimeSpan.FromSeconds(3))) return;
                    try { _app.Kill(); } catch { }
                    // Il test successivo trova il lock di istanza singola se il processo resta vivo: attesa piu' lunga e
                    // terminazione dell'albero come ultima risorsa.
                    if (!Wait.TryForCondition(() => _app.HasExited, TimeSpan.FromSeconds(10)))
                    {
                        try { System.Diagnostics.Process.GetProcessById(_app.ProcessId).Kill(entireProcessTree: true); } catch { }
                        Wait.TryForCondition(() => _app.HasExited, TimeSpan.FromSeconds(10));
                    }
                }
            }
        }
        catch
        {
            try { if (!_app.HasExited) _app.Kill(); } catch { /* best-effort */ }
        }
        finally
        {
            try { _app.Dispose(); } catch { }
            try { _automation.Dispose(); } catch { }
        }
    }
}
