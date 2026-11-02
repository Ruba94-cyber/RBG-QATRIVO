using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Natura di un guasto sui DB SQLite LOCALI (cache read-model). Serve a distinguere i pochissimi casi in cui
/// il file e' realmente illeggibile come database — e quindi va messo in quarantena e ricostruito dai JSON
/// condivisi — da tutti gli altri, in cui il database e' SANO e metterlo in quarantena sarebbe un danno
/// gratuito (perdita della fotografia calda, ore di ricostruzione a 100k Test) oltre che inutile: un lock
/// temporaneo, un disco pieno o una ACL negata non si risolvono cancellando la cache.
/// </summary>
public enum LocalIndexFaultKind
{
    /// <summary>Nessun guasto SQLite riconosciuto (tipicamente un bug applicativo o un errore di dominio).</summary>
    None,

    /// <summary>SQLITE_CORRUPT (11) o SQLITE_NOTADB (26): il file non e' piu' un database valido.</summary>
    Corruption,

    /// <summary>SQLITE_BUSY (5) / SQLITE_LOCKED (6): contesa transitoria, si ritenta piu' tardi.</summary>
    Busy,

    /// <summary>Permessi/ACL/sola lettura/impossibile aprire: problema d'ambiente, non del contenuto.</summary>
    AccessDenied,

    /// <summary>SQLITE_FULL (13) o ERROR_DISK_FULL: spazio esaurito, il contenuto resta valido.</summary>
    DiskFull,

    /// <summary>SQLITE_IOERR (10) e varianti estese: I/O instabile (rete, OneDrive, disco).</summary>
    Io,

    /// <summary>Schema dell'indice locale non interpretabile da questa versione dell'app.</summary>
    SchemaIncompatible,

    /// <summary>Errore SQLite riconosciuto ma non riconducibile a nessuna delle politiche sopra.</summary>
    Other
}

/// <summary>
/// Lo schema dell'indice locale non e' gestibile da questa build. NON e' corruzione: il file e' un database
/// perfettamente valido, scritto da un'altra versione di TestLoom. Ha un tipo dedicato proprio per non finire,
/// per collisione di codice d'errore, nel percorso di recupero per corruzione.
/// </summary>
public sealed class LocalIndexSchemaIncompatibleException : Exception
{
    public LocalIndexSchemaIncompatibleException(string message) : base(message) { }
}

/// <summary>
/// Classificazione dei guasti sui database locali basata ESCLUSIVAMENTE sui codici d'errore primari/estesi di
/// SqliteException. Il testo del messaggio non viene mai ispezionato: e' localizzato, cambia fra versioni di
/// SQLite e confondere "database disk image is malformed" con una frase che contiene "corrupt" ha come
/// conseguenza la distruzione di una cache sana.
/// </summary>
public static class LocalIndexFaultClassifier
{
    // Codici primari SQLite (https://www.sqlite.org/rescode.html). Il codice esteso ha nel byte basso il
    // codice primario: SQLITE_CORRUPT_VTAB (267) & 0xFF == 11.
    public const int SqliteError = 1;
    public const int SqlitePerm = 3;
    public const int SqliteBusy = 5;
    public const int SqliteLocked = 6;
    public const int SqliteReadOnly = 8;
    public const int SqliteIoErr = 10;
    public const int SqliteCorrupt = 11;
    public const int SqliteFull = 13;
    public const int SqliteCantOpen = 14;
    public const int SqliteAuth = 23;
    public const int SqliteNotADb = 26;

    private const int MaxInnerDepth = 8;

    // HRESULT Win32 per disco pieno: ERROR_DISK_FULL (0x70) e ERROR_HANDLE_DISK_FULL (0x27).
    private const int HResultDiskFull = unchecked((int)0x80070070);
    private const int HResultHandleDiskFull = unchecked((int)0x80070027);

    /// <summary>Soltanto questi due codici primari autorizzano quarantena e ricostruzione.</summary>
    public static bool IsCorruption(Exception? ex) => Classify(ex) == LocalIndexFaultKind.Corruption;

    /// <summary>
    /// Guasto AMBIENTALE che rende l'indice inutilizzabile in questa sessione (file bloccato, disco pieno, ACL, contesa):
    /// lo store lo registra come indisponibile invece di lasciarlo propagare dal costruttore. Non comprende la corruzione
    /// (gestita dalla quarantena) ne' le IOException del condiviso (cartella non raggiungibile), che restano dove sono.
    /// </summary>
    public static bool IndisponibileAmbiente(Exception? ex) =>
        ex is LocalIndexUnavailableException ||
        (ex is SqliteException && Classify(ex) is LocalIndexFaultKind.AccessDenied or LocalIndexFaultKind.Busy
            or LocalIndexFaultKind.DiskFull or LocalIndexFaultKind.Io);

    /// <summary>
    /// Errore di istruzione SQL (SQLITE_ERROR: sintassi, ESCAPE non valido, colonna o tabella inesistente). Non dipende
    /// dall'ambiente (disco, lock, rete, permessi) e non dice nulla sullo stato del file: e' un bug del programma.
    /// La lettura lo registra ad alta voce e ripiega sul percorso lento SENZA degradare l'indice; la scrittura
    /// degrada comunque, perche' un read model non aggiornato non va piu' servito come fresco.
    /// </summary>
    public static bool IsErroreSqlProgramma(Exception? ex) =>
        Classify(ex) == LocalIndexFaultKind.Other && TrovaSqlite(ex) is { } sqlite && PrimaryCode(sqlite) == SqliteError;

    private static SqliteException? TrovaSqlite(Exception? ex)
    {
        for (var depth = 0; ex != null && depth < MaxInnerDepth; depth++)
        {
            if (ex is SqliteException sqlite) return sqlite;
            ex = ex.InnerException;
        }
        return null;
    }

    public static LocalIndexFaultKind Classify(Exception? ex)
    {
        for (var depth = 0; ex != null && depth < MaxInnerDepth; depth++)
        {
            switch (ex)
            {
                case AggregateException aggregate:
                    {
                        var kind = LocalIndexFaultKind.None;
                        foreach (var inner in aggregate.Flatten().InnerExceptions)
                        {
                            var innerKind = Classify(inner);
                            // La corruzione vince su qualunque altro guasto concorrente: e' l'unica che
                            // richiede un'azione distruttiva e non deve essere mascherata da un BUSY.
                            if (innerKind == LocalIndexFaultKind.Corruption) return innerKind;
                            if (kind == LocalIndexFaultKind.None) kind = innerKind;
                        }
                        return kind;
                    }

                case LocalIndexSchemaIncompatibleException:
                    return LocalIndexFaultKind.SchemaIncompatible;

                case SqliteException sqlite:
                    return FromSqliteCode(PrimaryCode(sqlite));

                case UnauthorizedAccessException:
                    return LocalIndexFaultKind.AccessDenied;

                case IOException io when io.HResult == HResultDiskFull || io.HResult == HResultHandleDiskFull:
                    return LocalIndexFaultKind.DiskFull;

                case IOException:
                    return LocalIndexFaultKind.Io;
            }

            ex = ex.InnerException;
        }

        return LocalIndexFaultKind.None;
    }

    /// <summary>
    /// Codice primario effettivo: se disponibile si usa il byte basso del codice esteso, cosi' le varianti
    /// (SQLITE_CORRUPT_INDEX, SQLITE_IOERR_SHORT_READ, SQLITE_READONLY_DBMOVED...) ricadono nella politica
    /// della loro famiglia senza elencarle una per una.
    /// </summary>
    private static int PrimaryCode(SqliteException sqlite)
    {
        var extended = sqlite.SqliteExtendedErrorCode;
        return extended != 0 ? extended & 0xFF : sqlite.SqliteErrorCode;
    }

    private static LocalIndexFaultKind FromSqliteCode(int primary) => primary switch
    {
        SqliteCorrupt or SqliteNotADb => LocalIndexFaultKind.Corruption,
        SqliteBusy or SqliteLocked => LocalIndexFaultKind.Busy,
        SqlitePerm or SqliteAuth or SqliteReadOnly or SqliteCantOpen => LocalIndexFaultKind.AccessDenied,
        SqliteFull => LocalIndexFaultKind.DiskFull,
        SqliteIoErr => LocalIndexFaultKind.Io,
        _ => LocalIndexFaultKind.Other
    };

    /// <summary>Etichetta breve per crash log e avviso non modale, senza esporre percorsi locali.</summary>
    public static string Descrivi(LocalIndexFaultKind kind) => kind switch
    {
        LocalIndexFaultKind.Corruption => "indice locale corrotto",
        LocalIndexFaultKind.Busy => "indice locale occupato",
        LocalIndexFaultKind.AccessDenied => "accesso all'indice locale negato",
        LocalIndexFaultKind.DiskFull => "spazio su disco esaurito",
        LocalIndexFaultKind.Io => "errore di I/O sull'indice locale",
        LocalIndexFaultKind.SchemaIncompatible => "schema dell'indice locale non compatibile",
        LocalIndexFaultKind.Other => "errore SQLite sull'indice locale",
        _ => "errore non SQLite"
    };
}
