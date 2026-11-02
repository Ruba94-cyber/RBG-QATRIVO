using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Infrastructure;

/// <summary>
/// Polling con timeout esplicito per sincronizzare i test con la UI reale, invece di un singolo
/// <c>Thread.Sleep</c> a durata fissa (che è sempre o troppo corto — test fragile — o troppo lungo — suite
/// lenta). L'intervallo di polling (75ms) è l'unico "delay fisso" della suite ed è deliberatamente piccolo.
/// </summary>
public static class Wait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(75);

    /// <summary>Attende che <paramref name="probe"/> ritorni un valore non nullo che soddisfa
    /// <paramref name="isReady"/>. Vincolato a tipi reference (AutomationElement, Window, stringhe...): la
    /// sincronizzazione su un semplice bool passa invece da <see cref="ForCondition"/>, che non ha bisogno di
    /// annidare un secondo Nullable su un tipo valore già opzionale.</summary>
    public static T Until<T>(Func<T?> probe, Func<T, bool> isReady, TimeSpan? timeout = null, string? description = null)
        where T : class
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var deadline = DateTime.UtcNow + effectiveTimeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var current = probe();
                if (current != null && isReady(current)) return current;
            }
            catch (Exception ex)
            {
                // Un elemento in fase di ricostruzione (es. durante un refresh della griglia) può lanciare
                // temporaneamente durante la ricerca UIA: si ritenta finché non scade il timeout, invece di
                // far fallire il test al primo tentativo sfortunato.
                lastError = ex;
            }
            Thread.Sleep(PollInterval);
        }
        var reason = description ?? typeof(T).Name;
        throw new TimeoutException(
            $"Timeout ({effectiveTimeout.TotalSeconds:F0}s) in attesa di: {reason}." +
            (lastError != null ? $" Ultima eccezione osservata: {lastError.Message}" : ""));
    }

    public static AutomationElement ForElement(Func<AutomationElement?> find, string description, TimeSpan? timeout = null) =>
        Until(find, _ => true, timeout, description);

    /// <summary>Sincronizzazione su una condizione booleana qualsiasi (elemento chiuso, processo terminato,
    /// proprietà osservabile diventata vera...), con lo stesso identico polling/timeout di <see cref="Until"/>.</summary>
    public static void ForCondition(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var deadline = DateTime.UtcNow + effectiveTimeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return; }
            catch (Exception ex) { lastError = ex; }
            Thread.Sleep(PollInterval);
        }
        throw new TimeoutException(
            $"Timeout ({effectiveTimeout.TotalSeconds:F0}s) in attesa di: {description}." +
            (lastError != null ? $" Ultima eccezione osservata: {lastError.Message}" : ""));
    }

    public static bool TryForCondition(Func<bool> condition, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var deadline = DateTime.UtcNow + effectiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return true; }
            catch { }
            Thread.Sleep(PollInterval);
        }
        return false;
    }
}
