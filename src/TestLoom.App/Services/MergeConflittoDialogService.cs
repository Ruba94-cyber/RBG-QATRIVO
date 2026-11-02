namespace TestLoom.App.Services;

/// <summary>Ponte ViewModel/UI per la scelta Master dei soli campi realmente in conflitto.</summary>
public static class MergeConflittoDialogService
{
    public static Func<IReadOnlyList<CampoMergeTestCase>, Task<IReadOnlyDictionary<string, SceltaMerge>?>>? Handler { get; set; }

    public static Task<IReadOnlyDictionary<string, SceltaMerge>?> Risolvi(IReadOnlyList<CampoMergeTestCase> conflitti) =>
        Handler == null
            ? Task.FromResult<IReadOnlyDictionary<string, SceltaMerge>?>(null)
            : Handler(conflitti);
}
