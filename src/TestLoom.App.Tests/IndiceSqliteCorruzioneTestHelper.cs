using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Preparazione comune dei test che corrompono l'intestazione del file SQLite dell'indice locale.
/// In WAL SQLite legge la pagina 1 dal file -wal finche' il WAL non e' stato svuotato da un checkpoint: con frame
/// ancora validi la corruzione scritta nel file principale e' invisibile e nessuna lettura fallisce (esito
/// non deterministico, dipendente da quando l'ultimo writer ha lasciato frame nel WAL). Il checkpoint TRUNCATE
/// porta tutte le pagine nel file principale, cosi' la corruzione simulata e' quella che il lettore incontra davvero.
/// </summary>
internal static class IndiceSqliteCorruzioneTestHelper
{
    public static void CheckpointWalEChiudiPool(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            // Riga di risultato: busy, log, checkpointed. busy = 1 significa checkpoint NON completato (lettori/scrittori attivi):
            // in quel caso la corruzione simulata non arriverebbe nel file principale e il test sarebbe non deterministico.
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read(), "PRAGMA wal_checkpoint(TRUNCATE) deve restituire una riga di esito.");
            Assert.Equal(0, reader.GetInt32(0));
        }
        SqliteConnection.ClearAllPools();
    }
}
