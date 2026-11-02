using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Igiene dei DB locali eliminabili/ricostruibili. Non opera mai nella cartella OneDrive. WAL checkpoint e
/// PRAGMA optimize sono economici; VACUUM viene eseguito soltanto quando almeno il 20% delle pagine (e almeno
/// 128 pagine) è libero. La workspace viene ricavata con lo stesso algoritmo di LocalTestIndexStore, senza
/// creare directory nuove: se TestLoom non ha ancora un indice locale non c'è nulla da manutenere.
///
/// Anche le copie `.corrupt-*` prodotte dall'auto-recovery SQLite sono diagnostica LOCALE e ricostruibile dai
/// JSON autorevoli: vengono mantenute in numero limitato e per un periodo limitato, così una macchina con
/// problemi ricorrenti non accumula indefinitamente copie complete dell'indice.
/// </summary>
public static class LocalSqliteMaintenanceService
{
    internal const int MaxCorruptQuarantineFiles = 6;
    internal static readonly TimeSpan CorruptQuarantineRetention = TimeSpan.FromDays(30);

    public static string ResolveWorkspaceDirectory(string sharedRoot)
    {
        if (string.IsNullOrWhiteSpace(sharedRoot)) return "";
        try
        {
            // Stessa chiave di LocalTestIndexStore: la manutenzione deve toccare la cartella che lo store usa davvero.
            var key = WorkspaceLocalPaths.ComputeWorkspaceKey(sharedRoot);
            var baseDir = Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "workspaces");
            return Path.Combine(baseDir, key);
        }
        catch { return ""; }
    }

    public static LocalSqliteMaintenanceResult OptimizeWorkspace(string? workspaceDirectory, bool vacuum) =>
        OptimizeWorkspace(workspaceDirectory, vacuum, ottimizzaStatistiche: true);

    /// <param name="ottimizzaStatistiche">Controlla <c>PRAGMA optimize;</c> E il checkpoint del WAL
    /// (<c>PRAGMA wal_checkpoint(TRUNCATE);</c>) di questo giro. Misurato in
    /// TestLoom.App.Tests.HomeReportCacheBenchmarkTests (2026-09-26): <c>PRAGMA optimize;</c> fa avanzare
    /// <c>PRAGMA data_version</c> anche a contenuto di dominio invariato (prima ANALYZE della sessione:
    /// sqlite_stat1 non esiste ancora, quindi ogni tabella appare "da analizzare"). Il checkpoint NON lo fa se il
    /// WAL è già vuoto — ma lo fa ANCHE LUI, allo stesso modo, quando il WAL ha davvero frame pendenti da una
    /// scrittura recente non ancora svuotata (indice appena ricostruito/aggiornato durante il bootstrap, con
    /// un'altra connessione ancora viva: caso riprodotto dal test
    /// OptimizeWorkspace_ManutenzioneAvvio_WalNonVuoto_FaAvanzareDataVersioneAncheSenzaOptimize). Spostare
    /// pagine dal WAL al file principale non è un'operazione "no-op" per SQLite: fa avanzare data_version per
    /// qualunque altra connessione, anche se il contenuto logico non cambia affatto.
    /// AppState esegue la manutenzione col flag <c>force: true</c> in modo asincrono ad OGNI avvio, in corsa con
    /// la primissima Home già mostrata durante il bootstrap. Le cache di copertura Home
    /// (LocalHomeDashboardQueryService/LocalTestArchiveQueryService) hanno come chiave la revisione dell'indice (P2.6),
    /// che checkpoint e optimize NON avanzano: la manutenzione costa solo I/O e non invalida la prima rivisita. Passare false
    /// SOLO dalla manutenzione avviata in coda all'avvio (AppState, force:true dal costruttore): la manutenzione
    /// periodica (force:false, ogni 12h) e quella esplicita (ForzaManutenzioneStorage) continuano a eseguire
    /// checkpoint+optimize normalmente, ben lontane nel tempo dalla primissima rivisita. Saltare il checkpoint
    /// UNA volta all'avvio è sicuro: non perde alcun dato (il WAL resta semplicemente più a lungo non svuotato,
    /// come farebbe comunque SQLite fra due manutenzioni), e l'auto-checkpoint nativo di SQLite
    /// (wal_autocheckpoint, soglia 1000 pagine, invariato) resta comunque attivo indipendentemente da questo
    /// flag.</param>
    public static LocalSqliteMaintenanceResult OptimizeWorkspace(string? workspaceDirectory, bool vacuum, bool ottimizzaStatistiche)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || !Directory.Exists(workspaceDirectory)) return new(0, 0, 0, 0);
        var optimized = 0;
        var vacuumed = 0;
        var errors = 0;
        long bytesSaved = 0;

        IEnumerable<string> databases;
        try { databases = Directory.EnumerateFiles(workspaceDirectory, "*.db", SearchOption.TopDirectoryOnly).ToList(); }
        catch { return new(0, 0, 1, 0); }

        foreach (var path in databases)
        {
            try
            {
                var before = TotalSqliteBytes(path);
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false
                }.ToString());
                connection.Open();
                Execute(connection, "PRAGMA busy_timeout=2000;");

                // Se il DB è occupato da un'operazione lunga, checkpoint/optimize possono fallire senza rendere
                // la manutenzione un errore funzionale: il prossimo giro riprova.
                //
                // ottimizzaStatistiche gate ANCHE il checkpoint (non solo PRAGMA optimize, vedi doc-comment
                // sopra): il checkpoint costa I/O ma non avanza la revisione dell'indice (P2.6): le cache di copertura Home
                // restano valide anche se la manutenzione corre con la primissima Home del bootstrap.
                if (ottimizzaStatistiche)
                {
                    TryExecute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                    TryExecute(connection, "PRAGMA optimize;");
                }
                optimized++;

                if (vacuum && ShouldVacuum(connection))
                {
                    Execute(connection, "VACUUM;");
                    TryExecute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                    vacuumed++;
                }
                connection.Close();
                var after = TotalSqliteBytes(path);
                if (before > after) bytesSaved += before - after;
            }
            catch (Exception ex)
            {
                errors++;
                CrashLogService.Registra($"Manutenzione SQLite locale: {Path.GetFileName(path)}", ex);
            }
        }

        try { bytesSaved += CleanupCorruptQuarantines(workspaceDirectory, DateTime.UtcNow); }
        catch (Exception ex)
        {
            errors++;
            CrashLogService.Registra("Manutenzione SQLite locale: quarantine corrotti", ex);
        }

        return new LocalSqliteMaintenanceResult(optimized, vacuumed, errors, bytesSaved);
    }

    /// <summary>
    /// Mantiene al massimo le sei copie diagnostiche `.corrupt-*` più recenti e comunque non oltre 30 giorni.
    /// Sono file esclusivamente locali generati da LocalTestIndexStore.QuarantineCorruptDatabase: eliminarli
    /// non può causare perdita di dati di dominio perché l'indice viene sempre ricostruito dai JSON condivisi.
    /// </summary>
    internal static long CleanupCorruptQuarantines(string workspaceDirectory, DateTime nowUtc)
    {
        if (!Directory.Exists(workspaceDirectory)) return 0;
        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateFiles(workspaceDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(p => Path.GetFileName(p).Contains(".corrupt-", StringComparison.OrdinalIgnoreCase))
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => SafeLastWriteUtc(f))
                .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return 0; }

        var cutoff = nowUtc.ToUniversalTime() - CorruptQuarantineRetention;
        long removedBytes = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var old = SafeLastWriteUtc(file) <= cutoff;
            var overCount = i >= MaxCorruptQuarantineFiles;
            if (!old && !overCount) continue;
            try
            {
                var length = file.Exists ? file.Length : 0;
                file.Delete();
                removedBytes += length;
            }
            catch { }
        }
        return removedBytes;
    }

    private static DateTime SafeLastWriteUtc(FileInfo file)
    {
        try { return file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private static bool ShouldVacuum(SqliteConnection connection)
    {
        var pages = ScalarLong(connection, "PRAGMA page_count;");
        var free = ScalarLong(connection, "PRAGMA freelist_count;");
        return pages > 0 && free >= 128 && free * 5 >= pages;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryExecute(SqliteConnection connection, string sql)
    {
        try { Execute(connection, sql); } catch { }
    }

    private static long TotalSqliteBytes(string databasePath)
    {
        long result = 0;
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try { if (File.Exists(path)) result += new FileInfo(path).Length; } catch { }
        }
        return result;
    }
}

public sealed record LocalSqliteMaintenanceResult(int DatabaseOttimizzati, int DatabaseVacuum, int Errori, long ByteRecuperati);
