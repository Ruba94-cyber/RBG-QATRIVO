namespace TestLoom.App.Services;

/// <summary>Ponte minimale ViewModel -> finestra di dialogo modale. Serve perché una conferma è per natura
/// asincrona e richiede una finestra proprietaria (cosa che un ViewModel non ha e non deve avere): la View
/// registra qui l'implementazione reale all'avvio, i ViewModel chiedono conferma senza conoscere Avalonia.
/// Se nessun handler è registrato (test automatici, headless) la risposta è sempre "sì": i test unitari non
/// devono bloccarsi su una domanda che nessuno può rispondere.</summary>
public static class ConfirmService
{
    private sealed record AbandonmentCleanupRegistration(WeakReference<object> Owner, Action<object> Cleanup);
    private static readonly object AbandonmentCleanupGate = new();
    private static readonly List<AbandonmentCleanupRegistration> AbandonmentCleanups = new();

    public static Func<string, string, string, string, Task<bool>>? Handler { get; set; }

    public static async Task<bool> Chiedi(string titolo, string messaggio, string testoSi, string testoNo) =>
        Handler == null || await Handler(titolo, messaggio, testoSi, testoNo);

    /// <summary>
    /// Registra una cleanup debole da eseguire quando l'utente conferma esplicitamente l'abbandono delle
    /// modifiche. Il servizio non trattiene il ViewModel: editor chiusi/GC vengono rimossi automaticamente.
    /// La callback deve essere statica/non catturare owner; owner viene passato come argomento solo se vivo.
    /// </summary>
    public static void RegisterConfirmedAbandonmentCleanup(object owner, Action<object> cleanup)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(cleanup);
        lock (AbandonmentCleanupGate)
        {
            AbandonmentCleanups.RemoveAll(r => !r.Owner.TryGetTarget(out _));
            if (AbandonmentCleanups.Any(r => r.Owner.TryGetTarget(out var current) && ReferenceEquals(current, owner))) return;
            AbandonmentCleanups.Add(new AbandonmentCleanupRegistration(new WeakReference<object>(owner), cleanup));
        }
    }

    private static void NotifyConfirmedAbandonment(object owner)
    {
        try
        {
            if (owner is IConfirmedAbandonmentParticipant participant)
            {
                participant.PrepareConfirmedAbandonmentCleanup();
                return;
            }

            Action<object>? cleanup = null;
            lock (AbandonmentCleanupGate)
            {
                for (var i = AbandonmentCleanups.Count - 1; i >= 0; i--)
                {
                    var registration = AbandonmentCleanups[i];
                    if (!registration.Owner.TryGetTarget(out var current))
                    {
                        AbandonmentCleanups.RemoveAt(i);
                        continue;
                    }
                    if (ReferenceEquals(current, owner)) cleanup = registration.Cleanup;
                }
            }
            cleanup?.Invoke(owner);
        }
        catch (Exception ex) { CrashLogService.Registra("Cleanup post conferma abbandono modifiche", ex); }
    }

    /// <summary>
    /// Conferma standard senza owner. Mantenuta per compatibilità con editor che non possiedono recovery locale:
    /// non scatena mai cleanup globali, perché una conferma non deve poter cancellare lo stato di un altro editor.
    /// </summary>
    public static Task<bool> ConfermaAbbandonoModifiche() => ConfermaAbbandonoModifiche(owner: null);

    /// <summary>
    /// Variante scoped: solo l'editor che ha richiesto il dialogo riceve la notifica di cleanup dopo un Sì.
    /// È il percorso obbligatorio per editor con bozze/allegati di staging recuperabili.
    /// </summary>
    public static async Task<bool> ConfermaAbbandonoModifiche(object? owner)
    {
        var confermato = await Chiedi(
            LocalizationService.Instance.Traduci("ModificheNonSalvateTitolo"),
            LocalizationService.Instance.Traduci("ModificheNonSalvateBody"),
            LocalizationService.Instance.Traduci("ProcediSenzaSalvare"),
            LocalizationService.Instance.Traduci("Annulla"));
        if (confermato && owner != null) NotifyConfirmedAbandonment(owner);
        return confermato;
    }
}
