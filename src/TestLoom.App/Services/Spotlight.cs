using Avalonia;
using Avalonia.Controls;

namespace TestLoom.App.Services;

/// <summary>Collega un identificativo stabile a un controllo reale. Il tour attiva una classe visuale sul
/// controllo corrispondente anche quando la pagina viene ricreata durante il cambio di passo.</summary>
public static class Spotlight
{
    private static readonly List<WeakReference<Control>> Controlli = new();
    // Il registro è statico: nell'app esiste un solo thread UI, ma controlli creati su un altro thread (es. test
    // che istanziano View su thread diversi) non vanno toccati da qui — Avalonia lancerebbe "The calling thread
    // cannot access this object". Lock perché Registra e Aggiorna possono girare su thread diversi.
    private static readonly object Gate = new();
    private static string? _targetId;

    public static readonly AttachedProperty<string?> TargetIdProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, string?>("TargetId");

    static Spotlight()
    {
        TargetIdProperty.Changed.AddClassHandler<Control>((controllo, _) => Registra(controllo));
    }

    public static void SetTargetId(Control control, string? value) => control.SetValue(TargetIdProperty, value);
    public static string? GetTargetId(Control control) => control.GetValue(TargetIdProperty);

    public static string? TargetId
    {
        get => _targetId;
        set { _targetId = value; Aggiorna(); }
    }

    private static void Registra(Control controllo)
    {
        lock (Gate)
        {
            if (!Controlli.Any(x => x.TryGetTarget(out var c) && ReferenceEquals(c, controllo)))
                Controlli.Add(new(controllo));
        }
        Applica(controllo);
    }

    private static void Aggiorna()
    {
        List<Control> vivi;
        lock (Gate)
        {
            vivi = new List<Control>(Controlli.Count);
            for (var i = Controlli.Count - 1; i >= 0; i--)
            {
                if (!Controlli[i].TryGetTarget(out var controllo)) { Controlli.RemoveAt(i); continue; }
                vivi.Add(controllo);
            }
        }
        foreach (var controllo in vivi)
            if (controllo.CheckAccess()) Applica(controllo);
    }

    private static void Applica(Control controllo)
    {
        var attivo = !string.IsNullOrWhiteSpace(_targetId) &&
                     string.Equals(GetTargetId(controllo), _targetId, StringComparison.Ordinal);
        if (attivo && !controllo.Classes.Contains("SpotlightTarget")) controllo.Classes.Add("SpotlightTarget");
        if (!attivo) controllo.Classes.Remove("SpotlightTarget");
    }
}
