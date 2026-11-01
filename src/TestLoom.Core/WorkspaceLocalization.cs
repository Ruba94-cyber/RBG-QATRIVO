namespace TestLoom.Core;

/// <summary>
/// English → Italian text table. English is the source language and the fallback, so an untranslated string is
/// shown in English rather than hidden. The UI translates by exact text; the table is checked by tests.
/// </summary>
public static partial class WorkspaceLocalization
{
    public static string T(string english, string? language)
    {
        if (!string.Equals(language, "it", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(english)) return english;
        return Italian.TryGetValue(english, out var it) ? it : english;
    }

    /// <summary>Translates a message that embeds values, e.g. "Paste rejected (…)", by matching its fixed prefix.</summary>
    public static string TMessage(string english, string? language)
    {
        var exact = T(english, language);
        if (!ReferenceEquals(exact, english) && exact != english) return exact;
        if (!string.Equals(language, "it", StringComparison.OrdinalIgnoreCase)) return english;
        foreach (var (prefix, italian) in Prefixes)
            if (english.StartsWith(prefix, StringComparison.Ordinal)) return italian + english[prefix.Length..];
        return english;
    }

    public static IReadOnlyDictionary<string, string> ItalianTable => Italian;

    private static readonly (string English, string Italian)[] Prefixes =
    {
        ("Cannot create workspace: ", "Impossibile creare l'area di lavoro: "),
        ("Backup recovery failed. Original files were not replaced: ", "Ripristino dal backup non riuscito. I file originali non sono stati sostituiti: "),
        ("Could not reload workspace. File unchanged: ", "Impossibile ricaricare l'area di lavoro. File non modificato: "),
        ("Cannot read the existing workspace. The file was left unchanged: ", "Impossibile leggere l'area di lavoro esistente. Il file non è stato modificato: "),
        ("The requirement was not saved: ", "Il requisito non è stato salvato: "),
        ("Requirement changes were not saved: ", "Le modifiche al requisito non sono state salvate: "),
        ("The new test was not saved: ", "Il nuovo test non è stato salvato: "),
        ("Changes were not saved: ", "Le modifiche non sono state salvate: "),
        ("Export failed: ", "Esportazione non riuscita: "),
        ("Backup failed: ", "Backup non riuscito: "),
        ("Execution recorded: ", "Esecuzione registrata: "),
        ("Import failed: ", "Importazione non riuscita: "),
        ("Evidence could not be attached: ", "Impossibile allegare l'evidenza: "),
        ("The execution was not saved: ", "L'esecuzione non è stata salvata: "),
        ("Defect was not saved: ", "Il difetto non è stato salvato: "),
        ("Defect status was not saved: ", "Lo stato del difetto non è stato salvato: "),
        ("Defect details were not saved: ", "I dettagli del difetto non sono stati salvati: "),
        ("Test step was not saved: ", "Il passo di test non è stato salvato: "),
        ("Step changes were not saved: ", "Le modifiche al passo non sono state salvate: "),
        ("The step was not removed: ", "Il passo non è stato rimosso: "),
        ("The step order was not saved: ", "L'ordine dei passi non è stato salvato: "),
        ("Steps could not be saved: ", "Impossibile salvare i passi: "),
        ("Archive status could not be saved: ", "Impossibile salvare lo stato di archiviazione: "),
        ("Requirement links were not changed: ", "I collegamenti ai requisiti non sono stati modificati: "),
        ("The plan was not saved: ", "Il piano non è stato salvato: "),
        ("The change was not saved: ", "La modifica non è stata salvata: "),
    };
}
