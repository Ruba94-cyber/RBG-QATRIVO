using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Adattatore tra gli editor master ad alta crescita e il read-model SQLite locale. Letture e query sono sempre
/// bounded. Quando Phase D e' attivo espone anche SavePatch, che committa esclusivamente la finestra/delta per
/// UID; nei workspace legacy il writer bounded e' disabilitato e resta il percorso di compatibilita fino al
/// bootstrap. Page size limita solo la finestra UI, mai la cardinalita totale del catalogo.
/// </summary>
public sealed class MasterDataPagedReadSource<T> where T : MasterEntity
{
    private readonly LocalMasterIndexStore _store;
    private readonly LocalMasterArchiveQueryService _archive;
    private readonly string _catalog;
    private readonly Action _refresh;
    private readonly Func<bool>? _canSavePatch;
    private readonly Action<IReadOnlyList<T>, IReadOnlyList<T>, bool>? _savePatch;
    private readonly LocalIndexRecoveryCoordinator? _indexRecovery;

    public MasterDataPagedReadSource(
        LocalMasterIndexStore store,
        string catalog,
        Action refresh,
        Func<bool>? canSavePatch = null,
        Action<IReadOnlyList<T>, IReadOnlyList<T>, bool>? savePatch = null,
        LocalIndexRecoveryCoordinator? indexRecovery = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        if ((canSavePatch == null) != (savePatch == null))
            throw new ArgumentException("Capability e writer SavePatch devono essere specificati insieme.");
        _catalog = catalog;
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _canSavePatch = canSavePatch;
        _savePatch = savePatch;
        _indexRecovery = indexRecovery;
        _archive = new LocalMasterArchiveQueryService(store.DatabasePath);
    }

    /// <summary>Solo per il catalogo requisiti (6.5.0): limita la lettura paginata a un Tipo (Standard/Extra),
    /// lato SQL. Null = tutti (comportamento storico).</summary>
    public TipoRequisito? FiltroTipoRequisito { get; set; }

    public bool SupportsBoundedWrite => _canSavePatch?.Invoke() == true;

    /// <summary>Riallinea l'indice alla fonte autorevole verificata. Se la fonte non è leggibile l'eccezione
    /// risale: una fotografia SQLite precedente non deve essere promossa implicitamente a source of truth.</summary>
    public void Refresh() => _refresh();

    /// <summary>Riga singola per chiave esatta, archiviata o no (vedi LocalMasterIndexStore.TryGetByKey): usata
    /// dal percorso archivio/ripristino, dove ReadPage/ReadArchivedPage da sole non bastano a ritrovare la voce
    /// esatta su cui agire.</summary>
    public T? TryGetByKey(string key) => LeggiIndice("master per chiave",
        () => _store.TryGetByKey<T>(_catalog, key));

    /// <summary>Tutte le chiavi attive del catalogo (nessuna deserializzazione JSON riga per riga): usata dal
    /// picker "Unisci in..." in vista paginata, dove Items contiene solo la pagina corrente e non l'intero
    /// catalogo. Se l'indice locale non è leggibile l'eccezione risale al chiamante, che ripiega sulle voci
    /// già in memoria (degrado di completezza, mai un crash).</summary>
    public IReadOnlyList<string> ReadAllActiveKeys() => LeggiIndice("chiavi attive",
        () => _store.ReadActiveKeys(_catalog));

    private TResult LeggiIndice<TResult>(string operazione, Func<TResult> lettura)
    {
        if (_indexRecovery == null) return lettura();
        if (_indexRecovery.TryRead($"{operazione} {_catalog}", lettura, out var risultato)) return risultato;
        throw new IOException($"Indice master {_catalog} indisponibile; usare la fonte autorevole.");
    }

    public void SavePatch(IReadOnlyList<T> desiredWindow, IReadOnlyList<T> baselineWindow, bool allowSilentRemoteMerge = true)
    {
        // Come MasterDataRepository.Save: il percorso di scrittura non deve MAI vedere una fotografia
        // accorpata da uno scope di lettura aperto (MasterLetturaScope), ma lo stato condiviso piu' aggiornato,
        // prima di decidere merge o conflitto.
        using var scritturaFuoriDaLetturaAccorpata = MasterLetturaScope.Sospendi();
        if (!SupportsBoundedWrite || _savePatch == null)
            throw new InvalidOperationException($"Writer Phase D bounded non disponibile per {_catalog}.");
        _savePatch(desiredWindow, baselineWindow, allowSilentRemoteMerge);
    }

    /// <summary>
    /// Legge una pagina. In sola consultazione le righe archiviate restano escluse (comportamento storico: la
    /// finestra di browsing mostra solo righe attive). Il percorso "modifica"/archivio chiede invece
    /// includeArchived:true, perché deve poter distinguere una riga archiviata da una realmente assente
    /// (esattamente come fa il caricamento legacy completo via Items.Where(!EArchiviata)) — filtrare qui invece
    /// che lato caller perderebbe le righe archiviate della finestra corrente. Chiede una riga in più per
    /// sapere se esiste una pagina successiva senza eseguire un COUNT FTS separato. Il limite 1000 e'
    /// esclusivamente per la singola query/collection UI.
    /// </summary>
    public MasterDataPage<T> ReadPage(string? query, long pageNumber, int pageSize, bool includeArchived = false)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (pageSize is < 1 or >= 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));

        var offset = checked((pageNumber - 1L) * pageSize);
        var hits = LeggiIndice("pagina", () => _store.Query<T>(_catalog, query, includeArchived, offset, pageSize + 1, FiltroTipoRequisito));
        var hasNext = hits.Count > pageSize;
        var items = hits.Take(pageSize).Select(h => h.Item).ToList();
        return new MasterDataPage<T>(items, hasNext);
    }

    /// <summary>
    /// Pagina archived-only per l'Archivio unificato. Il refresh forte avviene una sola volta prima di COUNT
    /// e SELECT: il totale e la pagina provengono quindi dalla stessa fotografia locale appena riconciliata.
    /// L'indice resta ricostruibile; qualunque errore viene gestito dal caller con fallback autorevole.
    /// refresh:false salta la riconciliazione quando il chiamante sa gia' di avere una fotografia fresca (es.
    /// l'Archivio unificato, che ha appena costruito/riallineato questa stessa sorgente un istante prima e
    /// altrimenti pagherebbe la stessa riconciliazione dell'intero catalogo piu' volte per ogni apertura,
    /// feedback utente: "l'archivio pur essendo vuoto sembra abbastanza lento e poco fluido nell'aprirsi").
    /// </summary>
    public MasterDataArchivePage<T> ReadArchivedPage(long offset, int pageSize, bool refresh = true)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));

        if (refresh) _refresh();
        var total = LeggiIndice("archivio conteggio", () => _archive.CountArchived(_catalog));
        if (offset >= total) return new MasterDataArchivePage<T>(Array.Empty<T>(), total);
        return new MasterDataArchivePage<T>(
            LeggiIndice("archivio pagina", () => _archive.QueryArchived<T>(_catalog, offset, pageSize)), total);
    }
}

public sealed record MasterDataPage<T>(IReadOnlyList<T> Items, bool HasNext) where T : MasterEntity;
public sealed record MasterDataArchivePage<T>(IReadOnlyList<T> Items, long Total) where T : MasterEntity;

/// <summary>Factory tipizzata: ogni editor condivide il DB locale del workspace ma mantiene una sorgente
/// piccola e indipendente. Il coordinator continua a validare la fonte autorevole e i fingerprint forti.</summary>
public static class MasterDataPagedReadSources
{
    /// <summary>Auto-selezione centralizzata: i cataloghi ad alta crescita gia coperti dal read-model locale
    /// sono sempre paginati. La soglia dei benchmark (100k, 1M, ...) non compare nella logica applicativa.</summary>
    public static MasterDataPagedReadSource<T>? TryCreate<T>(DataPaths paths) where T : MasterEntity
    {
        if (typeof(T) == typeof(Requisito))
            return (MasterDataPagedReadSource<T>)(object)Requisiti(paths);
        if (typeof(T) == typeof(Funzionalita))
            return (MasterDataPagedReadSource<T>)(object)Funzionalita(paths);
        if (typeof(T) == typeof(Componente))
            return (MasterDataPagedReadSource<T>)(object)Componente(paths);
        return null;
    }

    public static MasterDataPagedReadSource<Requisito> Requisiti(DataPaths paths) =>
        Create<Requisito>(paths, MasterDataLocalIndexCoordinator.RequisitiCatalog,
            c => c.RefreshRequisiti());

    public static MasterDataPagedReadSource<Funzionalita> Funzionalita(DataPaths paths) =>
        Create<Funzionalita>(paths, MasterDataLocalIndexCoordinator.FunzionalitaCatalog,
            c => c.RefreshFunzionalita());

    public static MasterDataPagedReadSource<Componente> Componente(DataPaths paths) =>
        Create<Componente>(paths, MasterDataLocalIndexCoordinator.EcuCatalog,
            c => c.RefreshEcu());

    private static MasterDataPagedReadSource<T> Create<T>(
        DataPaths paths,
        string catalog,
        Func<MasterDataLocalIndexCoordinator, MasterCatalogIndexRefresh> refresh)
        where T : MasterEntity
    {
        var coordinator = new MasterDataLocalIndexCoordinator(paths);
        var engine = new Lazy<PhaseDMasterRepositoryEngine<T>>(
            () => new PhaseDMasterRepositoryEngine<T>(paths.Root, catalog),
            LazyThreadSafetyMode.ExecutionAndPublication);
        return new MasterDataPagedReadSource<T>(
            coordinator.Store,
            catalog,
            () => refresh(coordinator),
            () => PhaseDMasterJournalService.IsInitialized(paths.Root),
            (desired, baseline, allowSilentRemoteMerge) => engine.Value.SavePatch(desired, baseline, allowSilentRemoteMerge),
            coordinator.IndexRecovery);
    }
}
