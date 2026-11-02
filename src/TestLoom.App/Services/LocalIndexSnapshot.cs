using System.Threading;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Snapshot di sola lettura sull'indice Test (P2.6). Apre una transazione deferred e legge in essa la revisione
/// dell'indice: revisione e dati delle query arrivano dalla stessa versione del file (WAL), quindi la chiave di una cache
/// letta qui non puo' riferirsi a dati diversi da quelli letti dopo nella stessa transazione.
/// </summary>
/// <remarks>
/// La revisione e' un contatore monotono nella tabella meta, incrementato da trigger nella stessa transazione di ogni
/// scrittura sulle tabelle di contenuto (vedi LocalTestIndexStore.InstallaContatoreRevisione). Sostituisce PRAGMA data_version:
/// quel valore e' un contatore PER CONNESSIONE, quindi due connessioni diverse possono riportare lo stesso numero pur con
/// dati cambiati, e la cache serviva un risultato stantio.
/// Le letture che fanno parte dello snapshot devono usare <see cref="Transazione"/> e non aprirne una propria: SQLite non
/// annida le transazioni, quindi un BEGIN interno fallisce.
/// </remarks>
internal sealed class LocalIndexSnapshot : IDisposable
{
    /// <summary>Chiave del contatore di revisione nella tabella meta.</summary>
    internal const string ChiaveRevisione = "revision";

    private static long _tokenSenzaRevisione;
    private readonly SqliteTransaction _transazione;

    public LocalIndexSnapshot(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _transazione = connection.BeginTransaction(deferred: true);
        try
        {
            // Senza la tabella meta (schema incompleto) non c'e' contatore: nessuna cache, mai un errore di lettura. La
            // presenza si verifica prima, perche' SQLite risolve i nomi di tabella alla preparazione della query.
            using var esiste = connection.CreateCommand();
            esiste.Transaction = _transazione;
            esiste.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'meta');";
            var metaPresente = Convert.ToInt64(esiste.ExecuteScalar()) != 0;

            object? valore = null;
            if (metaPresente)
            {
                using var command = connection.CreateCommand();
                command.Transaction = _transazione;
                command.CommandText = $"SELECT CAST(value AS INTEGER) FROM meta WHERE key = '{ChiaveRevisione}' LIMIT 1;";
                valore = command.ExecuteScalar();
            }
            if (valore is long revisione)
            {
                Revisione = revisione;
                Cacheabile = true;
            }
            else
            {
                // Contatore assente (indice non ancora gestito dal writer): nessuna cache. Il token e' unico e negativo,
                // quindi non coincide mai con un token salvato in precedenza ne' con una revisione reale.
                Revisione = -Interlocked.Increment(ref _tokenSenzaRevisione);
                Cacheabile = false;
            }
        }
        catch
        {
            _transazione.Dispose();
            throw;
        }
    }

    /// <summary>Transazione di sola lettura dello snapshot: le letture interne vi si legano invece di aprirne un'altra.</summary>
    public SqliteTransaction Transazione => _transazione;

    /// <summary>Revisione dell'indice nello snapshot, oppure token unico se il contatore manca.</summary>
    public long Revisione { get; }

    /// <summary>False quando il contatore manca: le cache non vanno ne' lette ne' scritte con questo token.</summary>
    public bool Cacheabile { get; }

    /// <summary>Chiude lo snapshot. Nessuna scrittura: il COMMIT rilascia solo lo snapshot di lettura, e Dispose annulla
    /// comunque una transazione rimasta aperta, cosi' la connessione torna al pool senza transazioni pendenti.</summary>
    public void Dispose()
    {
        try
        {
            _transazione.Commit();
        }
        catch (SqliteException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _transazione.Dispose();
        }
    }
}
