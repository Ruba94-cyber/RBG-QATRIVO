namespace TestLoom.App.Services;

/// <summary>
/// Serializza la finestra lettura/revisione/scrittura di CR, NC e Segnalazioni fra
/// istanze e processi sullo STESSO filesystem locale. Riusa esattamente il lock
/// usato dai TestCase per lo stesso path; non è un lease distribuito OneDrive.
/// </summary>
public static class DomainRecordCommitLock
{
    public static IDisposable Acquire(string domain, string entityId, string canonicalPath,
        TimeSpan? timeout = null)
    {
        try
        {
            return TestCaseCommitLock.AcquireAll(entityId, new[] { canonicalPath }, timeout);
        }
        catch (TestCaseSaveLockTimeoutException ex)
        {
            throw new IOException(
                $"Operazione bloccata: {domain} {entityId} è in modifica da un'altra istanza. " +
                "Nessun dato è stato scritto; riprova dopo aver ricaricato.", ex);
        }
    }
}
