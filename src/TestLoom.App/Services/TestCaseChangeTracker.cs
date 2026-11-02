using System.Collections.Concurrent;

namespace TestLoom.App.Services;

/// <summary>
/// FileSystemWatcher è soltanto un acceleratore per evitare una scansione completa ad ogni minuto.
/// Non è fonte di verità: overflow/errori incrementano una generation e il chiamante torna al full scan.
/// Gli eventi vengono deduplicati per path perché OneDrive può produrre sequenze rumorose.
/// </summary>
public sealed class TestCaseChangeTracker : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private long _failureGeneration;
    private long _handledFailureGeneration;
    private bool _disposed;

    /// <summary>Notifica (dal thread del FileSystemWatcher, MAI il thread UI) che qualcosa è cambiato su
    /// disco: un JSON di dominio/cestino o un errore del watcher. Prima di questa aggiunta il watcher
    /// accumulava soltanto path "sporchi" consumati al giro successivo del timer da 60s
    /// (diagnosi_sync/diagnosi.md, C4): il refresh non era mai guidato dall'evento, solo il suo COSTO era
    /// ridotto. Il chiamante (AppState/MainViewModel) è responsabile del debounce e del marshalling verso la
    /// UI: qui ci si limita a segnalare "è successo qualcosa", senza indicare cosa né quando rileggerlo.</summary>
    public event Action? ModificaRilevata;

    public TestCaseChangeTracker(DataPaths paths)
    {
        AddWatcher(paths.TestCasesDir);
        AddWatcher(paths.CestinoDir);
    }

    /// <summary>Gli handler del FileSystemWatcher girano su thread pool interni dedicati: un'eccezione non
    /// gestita lì può interrompere silenziosamente la consegna di eventi successivi. Un subscriber (test o
    /// produzione) che lancia non deve quindi mai propagare oltre questo punto.</summary>
    private void RaiseModificaRilevata()
    {
        try { ModificaRilevata?.Invoke(); }
        catch (Exception ex) { CrashLogService.Registra("TestCaseChangeTracker.ModificaRilevata", ex); }
    }

    public bool RequiresFullReconciliation =>
        Volatile.Read(ref _failureGeneration) != Volatile.Read(ref _handledFailureGeneration);

    public long CaptureFailureGeneration() => Volatile.Read(ref _failureGeneration);

    /// <summary>Consuma i path cambiati osservati finora. Eventi arrivati durante il drain restano o verranno
    /// re-inseriti dal watcher: RefreshPaths è idempotente, quindi un duplicato è innocuo.</summary>
    public IReadOnlyList<string> Drain()
    {
        var result = new List<string>();
        foreach (var path in _dirty.Keys)
            if (_dirty.TryRemove(path, out _)) result.Add(path);
        return result;
    }

    /// <summary>
    /// Conferma una full reconciliation rispetto alla generation osservata PRIMA di iniziarla. Se un nuovo
    /// overflow/error arriva durante la scansione, _failureGeneration cambia e non viene marcato come gestito:
    /// il giro successivo farà un altro full scan invece di perdere il segnale per una race.
    /// </summary>
    public void MarkFullReconciliationCompleted(long observedGeneration)
    {
        if (Volatile.Read(ref _failureGeneration) == observedGeneration)
            Volatile.Write(ref _handledFailureGeneration, observedGeneration);
    }

    private void AddWatcher(string directory)
    {
        Directory.CreateDirectory(directory);
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            Filter = "*.json",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 32 * 1024,
            EnableRaisingEvents = false
        };
        watcher.Created += (_, e) => Mark(e.FullPath);
        watcher.Changed += (_, e) => Mark(e.FullPath);
        watcher.Deleted += (_, e) => Mark(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            Mark(e.OldFullPath);
            Mark(e.FullPath);
        };
        watcher.Error += (_, _) => { Interlocked.Increment(ref _failureGeneration); RaiseModificaRilevata(); };
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void Mark(string path)
    {
        if (_disposed || !IsTestCaseJson(path)) return;
        _dirty[path] = 0;
        RaiseModificaRilevata();
    }

    private static bool IsTestCaseJson(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("testcase_", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _dirty.Clear();
        ModificaRilevata = null;
    }
}
