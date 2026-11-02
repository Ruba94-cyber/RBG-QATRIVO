using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Ricerca master con FTS5 disattivato (P3.7): la ricerca LIKE deve funzionare anche con caratteri speciali (%, _),
/// senza errori SQL. Il ramo non-FTS usa ESCAPE '\' e in una stringa raw C# il backslash va scritto una sola volta.
/// </summary>
public sealed class LocalMasterIndexRicercaSenzaFtsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_MasterNoFts_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void RicercaConFtsDisattivato_TrovaIValoriLetteralmente_ENonDegradaLIndice()
    {
        var shared = Path.Combine(_root, "shared");
        Directory.CreateDirectory(shared);
        var store = new LocalMasterIndexStore(shared, Path.Combine(_root, "local"));
        var items = new List<Componente>
        {
            new() { Nome = "BCM_100%", VeicoliAssociati = new() { "Daily" }, ModificatoDa = "Seed" },
            new() { Nome = "Modulo 1000", VeicoliAssociati = new() { "Daily" }, ModificatoDa = "Seed" },
            new() { Nome = "Gateway", VeicoliAssociati = new() { "Daily" }, ModificatoDa = "Seed" }
        };
        store.ReconcileCatalog("componente", items, e => e.Nome, e => e.Archiviata,
            e => e.Nome + " " + string.Join(' ', e.VeicoliAssociati), sourceFingerprint: "v1");
        DisattivaFts(store.DatabasePath);

        Assert.False(store.FtsEnabled);
        // "100%" e' letterale: non deve trovare "Modulo 1000" come farebbe un % non escapato.
        var trovati = store.Query<Componente>("componente", "100%", false, 0, 20);
        Assert.Equal("BCM_100%", Assert.Single(trovati).Key);
        Assert.Equal(3, store.Query<Componente>("componente", "Daily", false, 0, 20).Count);
    }

    /// <summary>Simula un indice creato da un PC senza FTS5: la ricerca deve ripiegare sul LIKE senza degradare nulla.</summary>
    private static void DisattivaFts(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE meta SET value = '0' WHERE key = 'fts_enabled';";
        command.ExecuteNonQuery();
    }
}
