using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Ponte tra la fonte autorevole master e LocalMasterIndexStore. Nei workspace Phase D la fonte e' il mutation
/// journal materializzato causalmente: il vecchio JSON monolitico non viene piu consultato da paging/search.
/// Nei workspace legacy resta il percorso SHA-256 fino al bootstrap Phase D.
///
/// Nel percorso legacy length+LastWriteUtc restano soltanto un throttle temporaneo. In Phase D il reader
/// scalabile espone una revisione locale per catalogo: se l'indice SQLite ha gia' quella revisione, il refresh
/// non enumera alcuna entita; se deve riconciliare, l'enumerazione e' streaming e il LocalMasterIndexStore usa
/// staging SQLite disk-backed. Nessun conteggio o offset totale e' limitato a 32 bit.
/// </summary>
public sealed class MasterDataLocalIndexCoordinator
{
    public const string RequisitiCatalog = "requisiti";
    public const string FunzionalitaCatalog = "funzionalita";
    public const string EcuCatalog = "componente";

    private static readonly TimeSpan SourceStampFastPathWindow = TimeSpan.FromMinutes(5);
    private static readonly object RecoveryRegistryGate = new();
    private static readonly Dictionary<string, WeakReference<LocalIndexRecoveryCoordinator>> RecoveryRegistry =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly DataPaths _paths;
    private readonly object _sourceVersionGate = new();
    private readonly Dictionary<string, VerifiedSourceVersion> _lastVerifiedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly PhaseDMasterScalableReader _phaseDReader;
    public LocalMasterIndexStore Store { get; }
    public LocalIndexRecoveryCoordinator IndexRecovery { get; }

    public MasterDataLocalIndexCoordinator(DataPaths paths, string? localBaseDirectory = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        Store = new LocalMasterIndexStore(paths.Root, localBaseDirectory);
        _phaseDReader = new PhaseDMasterScalableReader(paths.Root, localBaseDirectory);
        lock (RecoveryRegistryGate)
        {
            if (!RecoveryRegistry.TryGetValue(Store.DatabasePath, out var weak) || !weak.TryGetTarget(out var recupero))
            {
                recupero = new LocalIndexRecoveryCoordinator(Store.DatabasePath,
                    RicostruisciTuttiICataloghi, "master-index.db");
                RecoveryRegistry[Store.DatabasePath] = new WeakReference<LocalIndexRecoveryCoordinator>(recupero);
            }
            IndexRecovery = recupero;
        }
        // File bloccato anche per la quarantena: il coordinatore parte degradato e le letture usano la fonte autorevole.
        if (Store.IndisponibileMotivo is { } motivo) IndexRecovery.InvalidaComeObsoleto(motivo);
    }

    private LocalIndexRebuildOutcome RicostruisciTuttiICataloghi()
    {
        if (!Directory.Exists(_paths.Root))
            return LocalIndexRebuildOutcome.Incompleta("Cartella master condivisa non raggiungibile.");
        Store.ReinizializzaDopoQuarantena();
        lock (_sourceVersionGate) _lastVerifiedSources.Clear();
        // Le tre proiezioni condividono un solo DB: dichiararlo sano richiede che TUTTE siano state
        // ricostruite dalla fonte autorevole, non soltanto il catalogo della query che ha fallito.
        RefreshCatalogCore<Requisito>(RequisitiCatalog, _paths.RequisitiFile,
            r => r.Id, r => r.Archiviata, r => $"{r.Id} {r.Testo} {r.ApplicabileBanco} {r.ApplicabileVeicolo}");
        RefreshCatalogCore<Funzionalita>(FunzionalitaCatalog, _paths.FunzionalitaFile,
            f => f.Nome, f => f.Archiviata, f => $"{f.Nome} {string.Join(' ', f.Categorie)} {string.Join(' ', f.ApplicabilePerVeicolo)}");
        RefreshCatalogCore<Componente>(EcuCatalog, _paths.EcuFile,
            e => e.Nome, e => e.Archiviata, e => $"{e.Nome} {string.Join(' ', e.VeicoliAssociati)}");
        return LocalIndexRebuildOutcome.Riuscita(0);
    }

    public MasterCatalogIndexRefresh RefreshRequisiti() => RefreshCatalog<Requisito>(
        RequisitiCatalog,
        _paths.RequisitiFile,
        r => r.Id,
        r => r.Archiviata,
        r => $"{r.Id} {r.Testo} {r.ApplicabileBanco} {r.ApplicabileVeicolo}");

    public MasterCatalogIndexRefresh RefreshFunzionalita() => RefreshCatalog<Funzionalita>(
        FunzionalitaCatalog,
        _paths.FunzionalitaFile,
        f => f.Nome,
        f => f.Archiviata,
        f => $"{f.Nome} {string.Join(' ', f.Categorie)} {string.Join(' ', f.ApplicabilePerVeicolo)}");

    public MasterCatalogIndexRefresh RefreshEcu() => RefreshCatalog<Componente>(
        EcuCatalog,
        _paths.EcuFile,
        e => e.Nome,
        e => e.Archiviata,
        e => $"{e.Nome} {string.Join(' ', e.VeicoliAssociati)}");

    public IReadOnlyList<MasterCatalogIndexRefresh> RefreshHighGrowthCatalogs() =>
        new[] { RefreshRequisiti(), RefreshFunzionalita(), RefreshEcu() };

    public MasterCatalogIndexRefresh RefreshCatalog<T>(
        string catalog,
        string authoritativePath,
        Func<T, string> keyOf,
        Func<T, bool> isArchived,
        Func<T, string> searchTextOf)
        where T : MasterEntity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoritativePath);
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(isArchived);
        ArgumentNullException.ThrowIfNull(searchTextOf);

        // Indice indisponibile per un file bloccato: si ritenta l'apertura (senza quarantena) prima di scegliere la via.
        if (Store.IndisponibileMotivo != null) TentaRiapertura();

        if (IndexRecovery.Salute == LocalIndexHealth.Unavailable)
        {
            // Dopo un guasto non-corruzione (es. fonte temporaneamente irraggiungibile) una validazione
            // forte riuscita puo' riammettere l'indice, senza quarantena di un DB sano.
            // Con il file bloccato (indice indisponibile) la riconciliazione non si puo' scrivere: si verifica la fonte autorevole.
            if (Store.IndisponibileMotivo == null)
            {
                try
                {
                    var riallineato = RefreshCatalogCore(catalog, authoritativePath, keyOf, isArchived, searchTextOf);
                    IndexRecovery.MarcaSano();
                    return riallineato;
                }
                catch (Exception ex) when (LocalIndexFaultClassifier.IndisponibileAmbiente(ex))
                {
                    CrashLogService.Registra($"Master {catalog}: riallineamento dell'indice non riuscito (fonte autorevole)", ex);
                }
            }
            return RefreshCatalogSenzaIndice<T>(catalog, authoritativePath);
        }
        Exception? guastoSorgente = null;
        if (IndexRecovery.TryRead($"refresh master {catalog}", () =>
            {
                try { return RefreshCatalogCore(catalog, authoritativePath, keyOf, isArchived, searchTextOf); }
                catch (Exception ex) when (ex is not SqliteException &&
                                           LocalIndexFaultClassifier.Classify(ex) != LocalIndexFaultKind.Corruption &&
                                           ex is not LocalIndexSchemaIncompatibleException &&
                                           !LocalIndexFaultClassifier.IndisponibileAmbiente(ex))
                {
                    // Un master JSON/Phase D invalido deve conservare il tipo originale di eccezione:
                    // e' un problema della fonte autorevole, non una corruzione del DB usa-e-getta.
                    // Un guasto ambientale dell'indice NON e' un errore della fonte: resta a TryRead, che degrada.
                    guastoSorgente = ex;
                    return default!;
                }
            }, out var esito))
        {
            if (guastoSorgente != null) ExceptionDispatchInfo.Capture(guastoSorgente).Throw();
            return esito;
        }
        // Indice degradato durante la lettura: nessuna eccezione verso il chiamante, il riallineamento si fa sulla fonte autorevole.
        return RefreshCatalogSenzaIndice<T>(catalog, authoritativePath);
    }

    /// <summary>Ritenta l'apertura dello store master reso indisponibile da un file bloccato. Mai quarantena qui: un guasto
    /// persistente lascia il coordinatore degradato e non lancia verso il chiamante.</summary>
    private void TentaRiapertura()
    {
        try
        {
            Store.ReinizializzaDopoQuarantena();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Master: riapertura dell'indice non riuscita (fonte autorevole)", ex);
            IndexRecovery.InvalidaComeObsoleto(ex.Message);
        }
    }

    /// <summary>
    /// Riallineamento senza scrivere nell'indice (file bloccato o indice degradato): fingerprint e conteggio vengono dalla
    /// fonte autorevole, letta come sempre. Nessuna riconciliazione avviene: Added/Updated/Removed restano a zero e
    /// SkippedAsCurrent e' falso. Le letture restano sulla fonte autorevole finche' l'indice non torna sano. Gli errori
    /// della fonte (master assente, JSON non valido) restano quelli di sempre.
    /// </summary>
    private MasterCatalogIndexRefresh RefreshCatalogSenzaIndice<T>(string catalog, string authoritativePath)
        where T : MasterEntity
    {
        if (PhaseDMasterJournalService.IsInitialized(_paths.Root))
        {
            var state = LeggiStatoPhaseD(catalog);
            return new MasterCatalogIndexRefresh(catalog, state.SourceFingerprint, state.ActiveCount,
                new LocalMasterReconcileResult(state.ActiveCount, 0, 0, 0, SkippedAsCurrent: false));
        }
        if (!File.Exists(authoritativePath))
            throw new MasterDataReadException(authoritativePath,
                "Il master autorevole non esiste; l'indice locale precedente non verra interpretato come fonte dati.");
        var bytes = ReadStableBytes(authoritativePath);
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        var items = JsonIo.TryParse<List<T>>(bytes, out var error);
        if (items == null)
            throw new MasterDataReadException(authoritativePath,
                $"Il master autorevole contiene JSON non valido e l'indice locale non verra aggiornato: {error}");
        return new MasterCatalogIndexRefresh(catalog, fingerprint, items.Count,
            new LocalMasterReconcileResult(items.Count, 0, 0, 0, SkippedAsCurrent: false));
    }

    private MasterCatalogIndexRefresh RefreshCatalogCore<T>(
        string catalog, string authoritativePath, Func<T, string> keyOf,
        Func<T, bool> isArchived, Func<T, string> searchTextOf) where T : MasterEntity
    {

        if (PhaseDMasterJournalService.IsInitialized(_paths.Root))
        {
            var state = LeggiStatoPhaseD(catalog);
            if (Store.IsCatalogCurrent(catalog, state.SourceFingerprint))
            {
                return new MasterCatalogIndexRefresh(
                    catalog,
                    state.SourceFingerprint,
                    state.ActiveCount,
                    new LocalMasterReconcileResult(state.ActiveCount, 0, 0, 0, SkippedAsCurrent: true));
            }

            // EnumerateCatalog deserializza un payload alla volta dal materialized head locale. ReconcileCatalog
            // lo riversa in TEMP SQLite su disco: il refresh rimane O(1) in RAM rispetto alla cardinalita totale.
            // EnumeraCatalogoPhaseD avvolge i guasti che nascono durante l'enumerazione (master-v2.db, un DB
            // SQLite separato da master-index.db): senza quell'avvolgimento una SqliteException di master-v2.db
            // arriverebbe qui indistinguibile da una vera corruzione di master-index.db e IndexRecovery (scoped
            // su Store.DatabasePath, "master-index.db") ricostruirebbe la cache SBAGLIATA (punto 0 del piano,
            // revisione di 698ed36 — vedi MasterDataLocalIndexCoordinatorPhaseDMisattributionTests).
            var reconcile = Store.ReconcileCatalog(
                catalog,
                EnumeraCatalogoPhaseD<T>(catalog),
                keyOf,
                isArchived,
                searchTextOf,
                state.SourceFingerprint);
            return new MasterCatalogIndexRefresh(catalog, state.SourceFingerprint, state.ActiveCount, reconcile);
        }

        if (!File.Exists(authoritativePath))
            throw new MasterDataReadException(authoritativePath,
                "Il master autorevole non esiste; l'indice locale precedente non verra interpretato come fonte dati.");

        var sourceStamp = ReadSourceStamp(authoritativePath);
        if (TryFastSkip(catalog, sourceStamp, out var recent))
        {
            return new MasterCatalogIndexRefresh(
                catalog,
                recent.Fingerprint,
                recent.AuthoritativeCount,
                new LocalMasterReconcileResult(recent.AuthoritativeCount, 0, 0, 0, SkippedAsCurrent: true));
        }

        var bytes = ReadStableBytes(authoritativePath);
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        if (Store.IsCatalogCurrent(catalog, fingerprint))
        {
            var count = Store.Count(catalog, includeArchived: true);
            RememberVerified(catalog, sourceStamp, fingerprint, count);
            return new MasterCatalogIndexRefresh(
                catalog,
                fingerprint,
                count,
                new LocalMasterReconcileResult(count, 0, 0, 0, SkippedAsCurrent: true));
        }

        var items = JsonIo.TryParse<List<T>>(bytes, out var error);
        if (items == null)
            throw new MasterDataReadException(authoritativePath,
                $"Il master autorevole contiene JSON non valido e l'indice locale non verra aggiornato: {error}");

        var legacyReconcile = Store.ReconcileCatalog(catalog, items, keyOf, isArchived, searchTextOf, fingerprint);
        RememberVerified(catalog, sourceStamp, fingerprint, items.Count);
        return new MasterCatalogIndexRefresh(catalog, fingerprint, items.Count, legacyReconcile);
    }

    /// <summary>
    /// Avvolge _phaseDReader.GetCatalogState: qualunque guasto (SqliteException su master-v2.db compresa) deve
    /// diventare un problema della fonte autorevole (MasterDataReadException), non finire nella classificazione
    /// di IndexRecovery che governa master-index.db — un DB SQLite completamente diverso. MasterDataReadException
    /// eredita da IOException e ha un case dedicato in LocalIndexFaultClassifier, quindi il filtro in
    /// RefreshCatalog la riconosce come "fonte" e non tocca l'indice locale.
    /// </summary>
    private PhaseDScalableCatalogState LeggiStatoPhaseD(string catalog)
    {
        try
        {
            return _phaseDReader.GetCatalogState(catalog);
        }
        catch (MasterDataReadException) { throw; }
        catch (PhaseDMasterConflictException) { throw; }
        catch (PhaseDMasterBusinessKeyConflictException) { throw; }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new MasterDataReadException("master-v2.db",
                $"Impossibile leggere lo stato del catalogo Phase D locale '{catalog}'.", ex);
        }
    }

    /// <summary>Come LeggiStatoPhaseD, ma per l'enumerazione lazy: EnumerateCatalog apre/tiene la connessione
    /// SQLite a master-v2.db per tutta la durata dell'iterazione (vedi il commento su
    /// PhaseDMasterScalableReader.EnumerateCatalog), quindi il guasto puo' manifestarsi a ogni MoveNext, non solo
    /// alla chiamata iniziale.</summary>
    private IEnumerable<T> EnumeraCatalogoPhaseD<T>(string catalog) where T : MasterEntity
    {
        IEnumerator<T> enumeratore;
        try
        {
            enumeratore = _phaseDReader.EnumerateCatalog<T>(catalog).GetEnumerator();
        }
        catch (PhaseDMasterConflictException) { throw; }
        catch (PhaseDMasterBusinessKeyConflictException) { throw; }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new MasterDataReadException("master-v2.db",
                $"Impossibile avviare l'enumerazione del catalogo Phase D locale '{catalog}'.", ex);
        }
        using (enumeratore)
        {
            while (true)
            {
                T corrente;
                try
                {
                    if (!enumeratore.MoveNext()) yield break;
                    corrente = enumeratore.Current;
                }
                catch (PhaseDMasterConflictException) { throw; }
                catch (PhaseDMasterBusinessKeyConflictException) { throw; }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    throw new MasterDataReadException("master-v2.db",
                        $"Impossibile leggere il catalogo Phase D locale '{catalog}'.", ex);
                }
                yield return corrente;
            }
        }
    }

    private bool TryFastSkip(string catalog, SourceStamp current, out VerifiedSourceVersion version)
    {
        lock (_sourceVersionGate)
        {
            if (_lastVerifiedSources.TryGetValue(catalog, out var candidate) &&
                DateTime.UtcNow - candidate.VerifiedAtUtc <= SourceStampFastPathWindow &&
                candidate.Stamp == current)
            {
                version = candidate;
                return true;
            }
        }
        version = default;
        return false;
    }

    private void RememberVerified(string catalog, SourceStamp stamp, string fingerprint, long authoritativeCount)
    {
        lock (_sourceVersionGate)
            _lastVerifiedSources[catalog] = new VerifiedSourceVersion(stamp, fingerprint, authoritativeCount, DateTime.UtcNow);
    }

    private static SourceStamp ReadSourceStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists) throw new FileNotFoundException("Master data non trovato.", path);
            return new SourceStamp(info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex)
        {
            throw new MasterDataReadException(path, "Impossibile leggere la versione del master autorevole.", ex);
        }
    }

    /// <summary>
    /// Solo compatibilita legacy pre-Phase-D. Il formato monolitico non e' il percorso di scala; il bootstrap
    /// viene migrato separatamente a streaming. Non usare questo metodo per nuovi cataloghi/feature.
    /// </summary>
    private static byte[] ReadStableBytes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
            if (stream.Length > int.MaxValue)
                throw new IOException($"Master legacy monolitico troppo grande per il percorso compatibilita: {stream.Length:N0} byte. Eseguire il bootstrap Phase D streaming.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (MasterDataReadException) { throw; }
        catch (Exception ex)
        {
            throw new MasterDataReadException(path, "Impossibile leggere il master autorevole per aggiornare l'indice locale.", ex);
        }
    }

    private readonly record struct SourceStamp(long Length, long LastWriteUtcTicks);
    private readonly record struct VerifiedSourceVersion(
        SourceStamp Stamp,
        string Fingerprint,
        long AuthoritativeCount,
        DateTime VerifiedAtUtc);
}

public sealed record MasterCatalogIndexRefresh(
    string Catalog,
    string SourceFingerprint,
    long AuthoritativeCount,
    LocalMasterReconcileResult Reconcile);
