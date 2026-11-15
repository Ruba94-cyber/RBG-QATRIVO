using System.Collections.Concurrent;
using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Fotografia IMMUTABILE e condivisa di un catalogo master Phase D a una data revisione del read-model locale.
///
/// Contratto:
/// <list type="bullet">
/// <item><see cref="Items"/> sono istanze CONDIVISE fra tutti i chiamanti: vanno trattate in sola lettura. Un chiamante che
/// deve modificare le righe (e poi salvarle con <c>Save(List)</c>) usa <see cref="CreaCopiaModificabile"/> o, piu'
/// semplicemente, <c>MasterDataRepository.Load()</c>, che restituisce sempre copie private.</item>
/// <item><see cref="State"/> e' la fotografia causale (baseline per UID, version vector, impronta di catalogo) con cui
/// <c>MasterDataRepository</c> traccia le liste modificabili: e' immutabile, quindi condivisibile.</item>
/// </list>
/// Nessun dato su disco cambia formato: lo snapshot e' una cache in memoria del read-model.
/// </summary>
public sealed class MasterCatalogSnapshot<T> where T : MasterEntity
{
    private readonly string[] _payloads;

    internal MasterCatalogSnapshot(
        long revision,
        string databaseIdentity,
        string sourceFingerprint,
        IReadOnlyList<T> items,
        string[] payloads,
        IReadOnlyList<Guid> conflictedUids,
        PhaseDLoadedListState state)
    {
        Revision = revision;
        DatabaseIdentity = databaseIdentity;
        SourceFingerprint = sourceFingerprint;
        Items = items;
        _payloads = payloads;
        ConflictedUids = conflictedUids;
        State = state;
    }

    /// <summary>Revisione locale del catalogo da cui lo snapshot e' stato letto.</summary>
    public long Revision { get; }
    public string DatabaseIdentity { get; }
    public string SourceFingerprint { get; }

    /// <summary>Righe del catalogo nell'ordine di presentazione (<see cref="MasterCatalogOrdering"/>). SOLA LETTURA.</summary>
    public IReadOnlyList<T> Items { get; }

    public IReadOnlyList<Guid> ConflictedUids { get; }
    public PhaseDLoadedListState State { get; }

    /// <summary>Stima grossolana (byte) della memoria trattenuta: serve al budget della cache.</summary>
    internal long CostoStimato
    {
        get
        {
            long chars = 0;
            foreach (var p in _payloads) chars += p.Length;
            // payload (UTF-16) + grafo di oggetti deserializzato (~ stessa dimensione) + overhead per riga/stato.
            return chars * 4 + _payloads.Length * 512L;
        }
    }

    /// <summary>Copie PRIVATE e modificabili delle righe, nello stesso ordine di <see cref="Items"/>. Ricostruite dal payload
    /// registrato nella testa, cioe' esattamente come le avrebbe prodotte una lettura completa del catalogo.</summary>
    public List<T> CreaCopiaModificabile()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var copia = new List<T>(_payloads.Length);
        foreach (var payload in _payloads)
        {
            copia.Add(JsonSerializer.Deserialize<T>(payload, JsonIo.Options)
                ?? throw new InvalidDataException($"Payload Phase D non deserializzabile ({typeof(T).Name})."));
        }
        PerfContatori.AggiungiCatalogCloneMs(sw.Elapsed.TotalMilliseconds);
        return copia;
    }

    /// <summary>Verifica (per test e diagnostica) che nessun chiamante abbia modificato le righe condivise: ogni riga deve
    /// serializzare come la copia ricostruita dal suo payload originale.</summary>
    public bool VerificaIntegrita()
    {
        if (Items.Count != _payloads.Length) return false;
        for (var i = 0; i < _payloads.Length; i++)
        {
            var atteso = JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(_payloads[i], JsonIo.Options), JsonIo.CompactOptions);
            if (JsonSerializer.Serialize(Items[i], JsonIo.CompactOptions) != atteso) return false;
        }
        return true;
    }
}

/// <summary>
/// Cache di processo degli snapshot dei cataloghi master, condivisa da TUTTI i repository/engine che leggono lo stesso
/// read-model (la chiave e' percorso del DB locale + catalogo + tipo, non l'istanza del repository).
///
/// Segnale di versione (nessuna euristica sul tempo): ogni accesso esegue <c>PhaseDMasterMaterializer.SondaCatalogo</c>,
/// cioe' il Refresh del journal condiviso (che intercetta salvataggi locali, consegne OneDrive/altri PC, conflitti
/// risolti, import e ripristini, perche' tutte queste operazioni passano da mutation nel journal) piu' la lettura della
/// revisione del catalogo e dell'identita' del file DB. Se (revisione, identita') coincidono con quelli dello snapshot in
/// cache lo si restituisce senza leggere teste ne' deserializzare; altrimenti un solo thread ricostruisce (single-flight) e
/// gli altri attendono lo stesso risultato. Nessun lock tenuto durante la lettura: i thread che chiedono una revisione gia'
/// presente non attendono mai chi sta ricostruendo una revisione diversa.
///
/// RAM contenuta: budget globale (<see cref="LimiteBytes"/>) con espulsione LRU; un catalogo che da solo supera il budget non
/// viene trattenuto (resta il comportamento di sempre: lettura integrale a ogni accesso).
/// </summary>
public static class MasterCatalogSnapshotCache
{
    private sealed class Entry
    {
        public required object Snapshot { get; init; }
        public required long Revision { get; init; }
        public required string Identity { get; init; }
        public required long Cost { get; init; }
        public long LastUse;
    }

    private readonly record struct Key(string Db, string Catalog, Type EntityType);
    private readonly record struct FlightKey(Key Key, long Revision, string Identity);

    private static readonly ConcurrentDictionary<Key, Entry> Entries = new();
    private static readonly ConcurrentDictionary<FlightKey, Lazy<object>> InFlight = new();
    private static readonly ConcurrentDictionary<Key, long[]> Statistiche_ = new();
    private static readonly object EvictionGate = new();

    /// <summary>Budget globale stimato (byte) degli snapshot trattenuti. Default 192 MB: i cataloghi master tipici (1.000 voci
    /// ciascuno) ne occupano pochi MB in tutto.</summary>
    public static long LimiteBytes { get; set; } = 192L * 1024 * 1024;

    /// <summary>Letture complete (ricostruzioni) e riusi dello snapshot di un catalogo, per DB e tipo: serve ai test e alla
    /// diagnostica, senza dipendere dai contatori globali condivisi fra test paralleli.</summary>
    public static (long Costruzioni, long Riusi) Statistiche<T>(string databasePath, string catalog) where T : MasterEntity
    {
        if (!Statistiche_.TryGetValue(new Key(NormalizzaDb(databasePath), catalog, typeof(T)), out var s)) return (0, 0);
        return (Interlocked.Read(ref s[0]), Interlocked.Read(ref s[1]));
    }

    public static int NumeroSnapshot => Entries.Count;
    public static long BytesStimati => Entries.Values.Sum(e => e.Cost);

    /// <summary>Svuota la cache (test, diagnostica).</summary>
    public static void Svuota()
    {
        Entries.Clear();
    }

    internal static void InvalidaDatabase(string databasePath)
    {
        foreach (var key in Entries.Keys)
            if (string.Equals(key.Db, NormalizzaDb(databasePath), StringComparison.Ordinal)) Entries.TryRemove(key, out _);
    }

    internal static MasterCatalogSnapshot<T> Ottieni<T>(
        string databasePath,
        string catalog,
        PhaseDMasterCatalogProbe probe,
        Func<MasterCatalogSnapshot<T>> build) where T : MasterEntity
    {
        var key = new Key(NormalizzaDb(databasePath), catalog, typeof(T));
        if (Entries.TryGetValue(key, out var entry) && entry.Revision == probe.Revision &&
            string.Equals(entry.Identity, probe.DatabaseIdentity, StringComparison.Ordinal))
        {
            Volatile.Write(ref entry.LastUse, Environment.TickCount64);
            Interlocked.Increment(ref Statistiche_.GetOrAdd(key, _ => new long[2])[1]);
            PerfContatori.IncrementaCatalogSnapshotRiusato();
            return (MasterCatalogSnapshot<T>)entry.Snapshot;
        }

        var flightKey = new FlightKey(key, probe.Revision, probe.DatabaseIdentity);
        var lazy = InFlight.GetOrAdd(flightKey, _ => new Lazy<object>(() =>
        {
            var costruito = build();
            Interlocked.Increment(ref Statistiche_.GetOrAdd(key, __ => new long[2])[0]);
            return costruito;
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var snapshot = (MasterCatalogSnapshot<T>)lazy.Value;
            Memorizza(key, snapshot);
            return snapshot;
        }
        finally
        {
            InFlight.TryRemove(flightKey, out _);
        }
    }

    private static void Memorizza<T>(Key key, MasterCatalogSnapshot<T> snapshot) where T : MasterEntity
    {
        var cost = snapshot.CostoStimato;
        if (cost > LimiteBytes) return; // troppo grande per il budget: non trattenuto.

        var nuovo = new Entry
        {
            Snapshot = snapshot,
            Revision = snapshot.Revision,
            Identity = snapshot.DatabaseIdentity,
            Cost = cost,
            LastUse = Environment.TickCount64
        };

        lock (EvictionGate)
        {
            // Mai far regredire la cache: se un altro thread ha gia' memorizzato una revisione piu' nuova (stessa
            // identita' DB) la si conserva.
            if (Entries.TryGetValue(key, out var esistente) &&
                string.Equals(esistente.Identity, nuovo.Identity, StringComparison.Ordinal) &&
                esistente.Revision >= nuovo.Revision)
                return;

            Entries[key] = nuovo;

            var totale = 0L;
            foreach (var e in Entries.Values) totale += e.Cost;
            while (totale > LimiteBytes)
            {
                Key? vittima = null;
                var piuVecchio = long.MaxValue;
                foreach (var pair in Entries)
                {
                    if (pair.Key.Equals(key)) continue;
                    var uso = Volatile.Read(ref pair.Value.LastUse);
                    if (uso < piuVecchio) { piuVecchio = uso; vittima = pair.Key; }
                }
                if (vittima is not { } v) break;
                if (Entries.TryRemove(v, out var rimossa)) totale -= rimossa.Cost;
            }
        }
    }

    private static string NormalizzaDb(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
}
