using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TestLoom.App.Services;

/// <summary>
/// Abbassa la priorità I/O/memoria REALE del thread corrente su Windows per tutta la durata dello scope
/// restituito (THREAD_MODE_BACKGROUND_BEGIN/END): <see cref="System.Threading.ThreadPriority.BelowNormal"/> da
/// solo riguarda soltanto lo scheduling CPU, non le priorità di I/O/memoria che sono quelle che davvero contano
/// mentre il backup giornaliero legge/hasha centinaia di migliaia di file in parallelo alle prime navigazioni
/// dell'utente (revisione indipendente su fc7e51b, punto 2). No-op su altri sistemi operativi.
/// </summary>
internal static class WindowsBackgroundThreadMode
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    /// <summary>Entra in modalità background sul thread CHIAMANTE (deve essere invocato dal thread che vuole
    /// abbassare la propria priorità: Windows applica THREAD_MODE_BACKGROUND_* solo al thread corrente).
    /// Restituisce uno scope da racchiudere in <c>using</c>: il Dispose ripristina sempre la priorità normale,
    /// anche in caso di eccezione (finally implicito).</summary>
    public static IDisposable Enter()
    {
        if (!OperatingSystem.IsWindows()) return NoOpScope.Instance;
        try
        {
            // Nessuna eccezione attesa in pratica (nessun handle da chiudere, nessun parametro invalido
            // possibile con una costante nota), ma un fallimento qui non deve mai impedire l'esecuzione del
            // backup: è solo una priorità di I/O più bassa, non una precondizione di correttezza.
            if (!SetThreadPriorityNative(GetCurrentThreadNative(), ThreadModeBackgroundBegin))
                return NoOpScope.Instance;
            return new BackgroundScope();
        }
        catch
        {
            return NoOpScope.Instance;
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "SetThreadPriority", SetLastError = true)]
    private static extern bool SetThreadPriorityNative(IntPtr hThread, int nPriority);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThread")]
    private static extern IntPtr GetCurrentThreadNative();

    private sealed class BackgroundScope : IDisposable
    {
        private bool _disposed;

        [SupportedOSPlatform("windows")]
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { SetThreadPriorityNative(GetCurrentThreadNative(), ThreadModeBackgroundEnd); } catch { }
        }
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();
        public void Dispose() { }
    }
}
