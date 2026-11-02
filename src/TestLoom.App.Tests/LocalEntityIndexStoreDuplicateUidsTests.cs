using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Difetto trovato in revisione (block D, multi-PC 6.4.2), indipendente dal block C: GetDuplicateUids usava
/// GROUP_CONCAT(path,'\n') — in SQLite un literal stringa non ha escape C-style, quindi '\n' e' preso alla
/// lettera (backslash + 'n', due caratteri), MAI un vero newline. Lo Split('\n', ...) lato C# (vero carattere
/// 0x0A) non trovava mai corrispondenze: un gruppo con 2+ path duplicati tornava come UN SOLO path,
/// concatenazione letterale inesistente su disco — "Apri file" su quella riga non avrebbe mai funzionato, e la
/// classificazione content-driven delle copie di conflitto (block C) non trovava mai un path reale da
/// confrontare. Scoperto scrivendo i test di ChangeRequestConflictCopyTests (i path restavano concatenati in
/// un'unica FileConProblemi invece di separati).
/// </summary>
public sealed class LocalEntityIndexStoreDuplicateUidsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_DupUids_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record Item(string Id, Guid Uid);

    [Fact]
    public void GetDuplicateUids_GruppoConTreOPiuPath_RestituisceOgniPathSeparatamente()
    {
        var store = new LocalEntityIndexStore(Path.Combine(_root, "shared"), Path.Combine(_root, "local"));
        var uid = Guid.NewGuid();
        var pathA = Path.Combine(_root, "shared", "cr_CR-0001-PC1.json");
        var pathB = Path.Combine(_root, "shared", "cr_CR-0001-PC2.json");
        var pathC = Path.Combine(_root, "shared", "cr_CR-0001-PC3.json");

        foreach (var path in new[] { pathA, pathB, pathC })
            store.Upsert("test-kind", path, DateTime.UtcNow, 100, new Item("CR-0001", uid), x => x.Id, x => x.Uid);

        var duplicati = store.GetDuplicateUids("test-kind");

        var gruppo = Assert.Single(duplicati);
        Assert.Equal(uid, gruppo.Uid);
        Assert.Equal(3, gruppo.Paths.Count); // prima del fix: 1 solo elemento, concatenazione illeggibile
        Assert.Contains(pathA, gruppo.Paths);
        Assert.Contains(pathB, gruppo.Paths);
        Assert.Contains(pathC, gruppo.Paths);
    }
}
