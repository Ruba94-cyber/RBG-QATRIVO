namespace TestLoom.App.Services;

/// <summary>Una voce mostrabile/ricercabile nel picker generico, indipendente dal tipo reale sottostante
/// (string, TestTemplate, TestCase, Requisito, NonConformita...): il dialog lavora solo su queste, il chiamante
/// fa il mapping da/verso il tipo vero.</summary>
public class VoceSelezionabile
{
    public object Valore { get; init; } = null!;
    public string TestoPrincipale { get; init; } = "";
    public string? TestoSecondario { get; init; }

    /// <summary>Testo su cui filtra la ricerca nel dialog: di norma TestoPrincipale + TestoSecondario, ma
    /// il chiamante può includere altri campi (es. Componente di un TestCase anche se non mostrato in TestoPrincipale).</summary>
    public string TestoRicerca { get; init; } = "";
}

/// <summary>Ponte minimale ViewModel -> finestra di selezione, stesso pattern di <see cref="ConfirmService"/> e
/// <see cref="TextEditService"/>: la View registra l'implementazione reale (una Window Avalonia) all'avvio, i
/// ViewModel chiedono una selezione senza conoscere Avalonia. Sostituisce, per i campi con liste potenzialmente
/// enormi (centinaia di componente, fino a 80 veicoli, tutti i Test/Anomalie), il pattern precedente "TextBox filtro +
/// ListBox sempre visibile" (feedback utente 2026-08-20): quella lista, per quanto affidabile, allungava
/// verticalmente ogni riga del form. Un pulsante che apre un dialog dedicato resta compatto quanto un campo
/// normale, e la finestra si apre già con la lista pronta — nessun rischio di popolamento popup in ritardo,
/// nessuna finestra di codice tra "apri" e "la lista è lì".</summary>
public static class SelezionaDialogService
{
    /// <summary>titolo, voci, multiSelezione -> voci scelte (vuota se annullato). Un solo Handler per i due
    /// casi (singola/multipla): stesso dialog, l'unica differenza è la modalità di selezione della ListBox.</summary>
    public static Func<string, IReadOnlyList<VoceSelezionabile>, bool, Task<IReadOnlyList<VoceSelezionabile>>>? Handler { get; set; }

    public static async Task<VoceSelezionabile?> Scegli(string titolo, IReadOnlyList<VoceSelezionabile> voci)
    {
        if (Handler == null) return null;
        var scelte = await Handler(titolo, voci, false);
        return scelte is { Count: > 0 } ? scelte[0] : null;
    }

    /// <summary>Selezione multipla (feedback utente 2026-08-20): per i campi "Aggiungi a una lista" (Requisiti,
    /// Funzionalità, Test correlati) non ha senso riaprire il dialog una voce alla volta — si scelgono tutte
    /// insieme (Ctrl/Shift+click) e vengono aggiunte in un solo passaggio, senza un secondo click su "Aggiungi"
    /// per ciascuna.</summary>
    public static async Task<IReadOnlyList<VoceSelezionabile>> ScegliMultipli(string titolo, IReadOnlyList<VoceSelezionabile> voci)
    {
        if (Handler == null) return Array.Empty<VoceSelezionabile>();
        // Un Handler che restituisce null (dialogo chiuso senza risultato) equivale a "annullato".
        return await Handler(titolo, voci, true) ?? Array.Empty<VoceSelezionabile>();
    }
}
