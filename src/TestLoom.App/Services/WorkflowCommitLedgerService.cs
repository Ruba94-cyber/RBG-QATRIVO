using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>
/// Esito della verifica autorevole della ricevuta di commit di una transazione. È la sola base per decidere il ripristino.
/// Committed: la ricevuta è nel ledger, il commit è avvenuto e nulla va ripristinato.
/// AssenteCertificata: tutte le fonti pertinenti sono state lette senza errori e la ricevuta non c'è: ripristino consentito.
/// Sconosciuto: una fonte non è leggibile o incoerente: l'esito è indeterminato, nessun ripristino, scritture bloccate.
/// </summary>
public enum EsitoRicevutaCommit
{
    Committed,
    AssenteCertificata,
    Sconosciuto
}

/// <summary>
/// Ledger condiviso bounded delle receipt delle WorkflowTransaction.
/// Una receipt per file produrrebbe migliaia di piccoli JSON su SharePoint/OneDrive: ogni device mantiene
/// current.ndjson + pochi segmenti + checkpoint gzip. I reader usano una cache invalidata da mtime+length,
/// così archivi molto grandi non rileggono/decomprimono lo stesso ledger per ogni singolo Test.
/// </summary>
public static class WorkflowCommitLedgerService
{
    private const long RotateAfterBytes = 512 * 1024;
    private const int CompletedSegmentsToKeep = 2;
    private const int SchemaVersion = 1;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonIo.Options) { WriteIndented = false };
    private static readonly ConcurrentDictionary<string, CheckpointCacheEntry> CheckpointCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, NdjsonCacheEntry> NdjsonCache = new(StringComparer.OrdinalIgnoreCase);

    public static void Publish(DataPaths paths, WorkflowTransactionCommitReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(receipt);
        if (!IsStructurallyValidReceipt(receipt))
            throw new InvalidDataException("Receipt workflow incompleta: pubblicazione bloccata.");

        // La receipt non è un side-effect best-effort: è ciò che rende verificabile il commit sui client che
        // ricevono i file OneDrive fuori ordine. Se il workspace è diventato non scrivibile, il chiamante deve
        // ricevere un errore e WorkflowTransactionService eseguirà rollback anziché dichiarare il commit.
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Pubblicazione receipt workflow");

        lock (Gate)
        {
            // Doppio gate: una transaction straniera o un cambio di compatibilità può comparire mentre si
            // attende il lock statico del ledger. La transaction corrente è esclusa tramite ActiveId.
            WorkspaceWritePolicy.EnsureCanWrite(paths, "Pubblicazione receipt workflow");

            // Verifica strict prima dell'append: un errore di lettura NON equivale a "receipt assente" e non deve far accodare alla cieca.
            // Il ledger è quello del device che scrive, quindi una coda parziale è una scrittura locale interrotta (vedi riparazione).
            var verifica = CercaNelDispositivo(paths, receipt.DeviceId, receipt.Id, locale: true);
            if (!verifica.Certa)
            {
                // Errore di I/O (lettura impossibile) e irregolarità strutturale (riga intermedia non valida) restano distinti.
                var messaggio = $"Commit ledger non integralmente leggibile: pubblicazione bloccata senza modificare il ledger. {verifica.Dettaglio}";
                throw verifica.Errore is IOException or UnauthorizedAccessException
                    ? new IOException(messaggio, verifica.Errore)
                    : new InvalidDataException(messaggio, verifica.Errore);
            }
            if (verifica.Trovata)
            {
                if (TryGetReceipt(paths, receipt.DeviceId, receipt.Id, out var existing, out _) && existing != null &&
                    !ReceiptsEquivalent(existing, receipt))
                    throw new InvalidDataException($"Il TransactionId {receipt.Id:N} esiste già nel commit ledger con contenuto differente.");
                return;
            }

            var dir = DeviceDirectory(paths.Root, receipt.DeviceId);
            Directory.CreateDirectory(dir);
            var current = Path.Combine(dir, "current.ndjson");
            // Riparazione della coda e append sotto lo stesso mutex cross-processo: un altro processo dello stesso PC non
            // può troncare o accodare mentre scriviamo (stesso contratto di PhaseDMasterJournalService).
            using var lockCrossProcesso = new NdjsonCodaRiparazione.LockCrossProcesso("CommitLedger", current);
            var nonVuoto = File.Exists(current) && new FileInfo(current).Length > 0;
            if (nonVuoto && !TryReadNdjsonStrict(current, out _))
            {
                // Solo un'ultima riga parziale (crash a metà append) è riparabile: viene spostata in current.ndjson.torn-*.
                // Qualunque altra irregolarità, o una divergenza ancora presente dopo la riparazione, blocca la pubblicazione:
                // un append sopra una riga intermedia non valida renderebbe ambiguo anche il nuovo evento. La receipt è parte
                // del commit atomico: meglio fallire e lasciare la decisione al WorkflowTransactionService (esito tri-state).
                var esito = NdjsonCodaRiparazione.RiparaSeCodaParziale(current, RigaReceiptValida, "CommitLedger");
                if (esito != NdjsonCodaRiparazione.Esito.Riparata || !TryReadNdjsonStrict(current, out _))
                    throw new InvalidDataException("Commit ledger current.ndjson non integralmente leggibile: pubblicazione bloccata senza modificare il file.");
            }

            // Coda valida ma senza newline finale: va chiusa prima di accodare, altrimenti due receipt finiscono sulla stessa riga.
            var chiusuraRiga = nonVuoto && !NdjsonCodaRiparazione.UltimoByteNewline(current) ? Environment.NewLine : "";
            var line = chiusuraRiga + JsonSerializer.Serialize(receipt, CompactJsonOptions) + Environment.NewLine;
            using (var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read,
                       64 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            NdjsonCache.TryRemove(Path.GetFullPath(current), out _);
            TryRotateAndCompact(paths, receipt.DeviceId);
        }
    }

    public static bool TryGetReceipt(
        DataPaths paths,
        string deviceId,
        Guid transactionId,
        out WorkflowTransactionCommitReceipt? receipt,
        out string? error)
    {
        receipt = null;
        error = null;
        if (transactionId == Guid.Empty || string.IsNullOrWhiteSpace(deviceId)) return false;

        try
        {
            var dir = DeviceDirectory(paths.Root, deviceId);
            if (!Directory.Exists(dir)) return false;

            var checkpointPath = Path.Combine(dir, "checkpoint.json.gz");
            var checkpointWasCached = IsCheckpointCacheCurrent(checkpointPath);
            var checkpoint = ReadCheckpointCached(checkpointPath);
            if (checkpoint?.Receipts.TryGetValue(transactionId.ToString("N"), out receipt) == true && receipt != null)
                return true;

            // mtime+length sono un ottimo fast-path, ma non una prova crittografica di identità. Alcuni provider
            // di sync possono sostituire i byte preservando entrambi. Se il risultato negativo proveniva DAVVERO
            // dalla cache, rileggerlo una volta evita che una receipt appena sincronizzata resti Pending per sempre.
            if (checkpointWasCached)
            {
                checkpoint = ReadCheckpointCached(checkpointPath, forceRefresh: true);
                if (checkpoint?.Receipts.TryGetValue(transactionId.ToString("N"), out receipt) == true && receipt != null)
                    return true;
            }

            var files = SafeFiles(dir, "segment-*.ndjson")
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .Prepend(Path.Combine(dir, "current.ndjson"));
            foreach (var file in files)
            {
                if (!File.Exists(file)) continue;
                var mapWasCached = IsNdjsonCacheCurrent(file);
                var map = ReadNdjsonCached(file);
                if (map != null && map.TryGetValue(transactionId, out receipt) && receipt != null)
                    return true;

                // Stesso principio del checkpoint: i cache hit positivi restano O(1); solo un cache miss che sta
                // per decidere la visibilità di una transazione forza una seconda lettura fisica di conferma.
                if (!mapWasCached) continue;
                map = ReadNdjsonCached(file, forceRefresh: true);
                if (map != null && map.TryGetValue(transactionId, out receipt) && receipt != null)
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Verifica la ricevuta di una transazione senza mai trasformare un errore di lettura in "assente". Una ricevuta trovata
    /// è prova positiva e vince su qualunque errore altrove. In assenza di prova positiva l'assenza è certificata solo se
    /// tutte le fonti pertinenti sono state lette senza errori; altrimenti l'esito è Sconosciuto.
    /// Il dispositivo locale è l'unico che scrive il proprio ledger: una coda parziale della sua ultima riga è una scrittura
    /// interrotta (commit non completato). Per un dispositivo remoto la stessa coda può essere una sincronizzazione in corso.
    /// </summary>
    public static EsitoRicevutaCommit VerificaRicevuta(DataPaths paths, string deviceId, Guid transactionId, out string? dettaglio)
    {
        ArgumentNullException.ThrowIfNull(paths);
        dettaglio = null;
        if (transactionId == Guid.Empty)
        {
            dettaglio = Motivo("TxLedgerIncoerente");
            return EsitoRicevutaCommit.Sconosciuto;
        }

        var dispositivo = string.IsNullOrWhiteSpace(deviceId)
            ? EsitoLettura.Assente
            : CercaNelDispositivo(paths, deviceId, transactionId, DispositivoLocale(deviceId));
        if (dispositivo.Trovata) return EsitoRicevutaCommit.Committed;

        var legacy = CercaRicevutaLegacy(paths, transactionId);
        if (legacy.Trovata) return EsitoRicevutaCommit.Committed;

        if (dispositivo.Certa && legacy.Certa) return EsitoRicevutaCommit.AssenteCertificata;

        var errore = dispositivo.Errore ?? legacy.Errore;
        dettaglio = errore != null
            ? Motivo("TxLedgerIllegibile")
            : dispositivo.Dettaglio ?? legacy.Dettaglio ?? Motivo("TxLedgerIncoerente");
        if (errore != null)
            CrashLogService.Registra($"Verifica ricevuta commit {transactionId:N} non conclusiva", errore);
        return EsitoRicevutaCommit.Sconosciuto;
    }

    /// <summary>Motivo mostrato a chi salva: testo localizzato, senza dettagli tecnici (il testo dell'eccezione resta nel log diagnostico).</summary>
    private static string Motivo(string chiave) => LocalizationService.Instance.Traduci(chiave);

    private static bool DispositivoLocale(string deviceId)
    {
        try { return deviceId.Equals(DeviceIdentityService.GetOrCreate(), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Esito della lettura di una singola fonte. Trovata è prova positiva; Certa=false significa "non so".</summary>
    private readonly record struct EsitoLettura(bool Trovata, bool Certa, string? Dettaglio, Exception? Errore)
    {
        public static EsitoLettura Assente => new(false, true, null, null);
        public static EsitoLettura Presente => new(true, true, null, null);
        public static EsitoLettura Incerta(string dettaglio, Exception? errore = null) => new(false, false, dettaglio, errore);
    }

    /// <summary>Ordine di lettura: current (prima della rotazione che lo sposta in un segmento), segmenti, checkpoint per ultimo
    /// (la compattazione scrive il checkpoint prima di eliminare i segmenti). Le fonti incerte non fermano la ricerca.</summary>
    private static EsitoLettura CercaNelDispositivo(DataPaths paths, string deviceId, Guid transactionId, bool locale)
    {
        string dir;
        try { dir = DeviceDirectory(paths.Root, deviceId); }
        catch (InvalidDataException ex) { return EsitoLettura.Incerta(Motivo("TxLedgerIncoerente"), ex); }

        if (!Directory.Exists(dir))
            return locale ? EsitoLettura.Assente : EsitoLettura.Incerta(Motivo("TxLedgerNonVisibile"));

        try
        {
            var certa = true;
            string? dettaglio = null;
            Exception? errore = null;
            void Registra(EsitoLettura esito)
            {
                if (esito.Certa) return;
                certa = false;
                dettaglio ??= esito.Dettaglio;
                errore ??= esito.Errore;
            }

            var corrente = CercaInNdjson(Path.Combine(dir, "current.ndjson"), transactionId, locale);
            if (corrente.Trovata) return corrente;
            Registra(corrente);

            var segmenti = Directory.EnumerateFiles(dir, "segment-*.ndjson", SearchOption.TopDirectoryOnly)
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var segmento in segmenti)
            {
                var esito = CercaInNdjson(segmento, transactionId, locale);
                if (esito.Trovata) return esito;
                Registra(esito);
            }

            var checkpoint = CercaNelCheckpoint(Path.Combine(dir, "checkpoint.json.gz"), transactionId);
            if (checkpoint.Trovata) return checkpoint;
            Registra(checkpoint);

            return certa ? EsitoLettura.Assente : EsitoLettura.Incerta(dettaglio ?? Motivo("TxLedgerIncoerente"), errore);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EsitoLettura.Incerta(Motivo("TxLedgerIllegibile"), ex);
        }
    }

    private static EsitoLettura CercaInNdjson(string file, Guid transactionId, bool locale)
    {
        if (!File.Exists(file)) return EsitoLettura.Assente;

        // Lettura integrale in memoria: i ledger sono limitati da RotateAfterBytes e l'esito deve riferirsi a un solo snapshot.
        // Un file eliminato tra enumerazione e lettura (rotazione o compattazione concorrente) non è un errore: il suo contenuto
        // è finito in un segmento più recente o nel checkpoint, che vengono letti dopo nello stesso ordine di lettura.
        byte[] bytes;
        try { bytes = JsonIo.ReadAllBytesShared(file); }
        catch (FileNotFoundException) { return EsitoLettura.Assente; }
        var testo = Encoding.UTF8.GetString(bytes);
        var righe = testo.Split('\n');
        var ultimaSenzaNewline = testo.Length > 0 && testo[^1] != '\n';
        var certa = true;
        string? dettaglio = null;
        for (var i = 0; i < righe.Length; i++)
        {
            var riga = righe[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(riga)) continue;

            WorkflowTransactionCommitReceipt? item = null;
            try { item = JsonSerializer.Deserialize<WorkflowTransactionCommitReceipt>(riga, CompactJsonOptions); }
            catch (JsonException) { }
            if (item != null && IsStructurallyValidReceipt(item))
            {
                if (item.Id == transactionId) return EsitoLettura.Presente;
                continue;
            }

            var ultimaParziale = ultimaSenzaNewline && i == righe.Length - 1;
            if (ultimaParziale && locale) continue; // scrittura locale interrotta: il commit non è mai stato completato
            certa = false;
            dettaglio ??= Motivo("TxLedgerIncoerente");
        }
        return certa ? EsitoLettura.Assente : EsitoLettura.Incerta(dettaglio ?? Motivo("TxLedgerIncoerente"));
    }

    private static EsitoLettura CercaNelCheckpoint(string path, Guid transactionId)
    {
        if (!File.Exists(path)) return EsitoLettura.Assente;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var checkpoint = JsonSerializer.Deserialize<WorkflowCommitLedgerCheckpoint>(gzip, CompactJsonOptions);
            if (checkpoint == null || !IsStructurallyValidCheckpoint(checkpoint))
                return EsitoLettura.Incerta(Motivo("TxLedgerIncoerente"));
            return checkpoint.Receipts.ContainsKey(transactionId.ToString("N")) ? EsitoLettura.Presente : EsitoLettura.Assente;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return EsitoLettura.Incerta(Motivo("TxLedgerIllegibile"), ex);
        }
    }

    private static EsitoLettura CercaRicevutaLegacy(DataPaths paths, Guid transactionId)
    {
        var path = LegacyReceiptPath(paths.Root, transactionId);
        try
        {
            if (!File.Exists(path)) return EsitoLettura.Assente;
            var receipt = JsonSerializer.Deserialize<WorkflowTransactionCommitReceipt>(JsonIo.ReadAllBytesShared(path), JsonIo.Options);
            return receipt != null && receipt.Id == transactionId && IsStructurallyValidReceipt(receipt, requireDeviceId: false)
                ? EsitoLettura.Presente
                : EsitoLettura.Incerta(Motivo("TxLedgerIncoerente"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return EsitoLettura.Incerta(Motivo("TxLedgerIllegibile"), ex);
        }
    }

    public static void Compact(DataPaths paths, string deviceId)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Compattazione commit ledger workflow");
        lock (Gate)
        {
            WorkspaceWritePolicy.EnsureCanWrite(paths, "Compattazione commit ledger workflow");
            TryRotateAndCompact(paths, deviceId, force: true);
        }
    }

    /// <summary>
    /// Lookup prefix-safe: una receipt completa già appendata rimane verificabile anche se OneDrive ci mostra
    /// una riga finale parziale. Il file incompleto NON viene mai messo in cache, così il completamento successivo
    /// viene riletto. La compattazione usa invece TryReadNdjsonStrict e richiede l'intero file valido.
    /// </summary>
    private static IReadOnlyDictionary<Guid, WorkflowTransactionCommitReceipt>? ReadNdjsonCached(
        string path,
        bool forceRefresh = false)
    {
        var full = Path.GetFullPath(path);
        if (!TryFileStamp(full, out var length, out var mtimeTicks))
        {
            NdjsonCache.TryRemove(full, out _);
            return null;
        }

        if (!forceRefresh && NdjsonCache.TryGetValue(full, out var cached) &&
            cached.Length == length && cached.LastWriteUtcTicks == mtimeTicks)
            return cached.Receipts;

        var parsed = new Dictionary<Guid, WorkflowTransactionCommitReceipt>();
        var poisonedIds = new HashSet<Guid>();
        var complete = true;
        try
        {
            // ReadLinesShared, non File.ReadLines: con FileShare.Read un handle del client di sincronizzazione
            // sul current.ndjson faceva risultare "non presente" una receipt gia' scritta -> commit Pending.
            foreach (var line in JsonIo.ReadLinesShared(full))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                WorkflowTransactionCommitReceipt? item;
                try { item = JsonSerializer.Deserialize<WorkflowTransactionCommitReceipt>(line, CompactJsonOptions); }
                catch (JsonException)
                {
                    complete = false;
                    break;
                }

                if (item == null || !IsStructurallyValidReceipt(item))
                {
                    complete = false;
                    break;
                }

                if (poisonedIds.Contains(item.Id)) continue;
                if (parsed.TryGetValue(item.Id, out var duplicate) && !ReceiptsEquivalent(duplicate, item))
                {
                    // Il solo TransactionId ambiguo non deve invalidare receipt complete e indipendenti che lo
                    // precedono. Quel TransactionId resta però non verificabile e non verrà scelto last-writer-wins.
                    parsed.Remove(item.Id);
                    poisonedIds.Add(item.Id);
                    complete = false;
                    continue;
                }
                parsed[item.Id] = item;
            }
        }
        catch (IOException) { NdjsonCache.TryRemove(full, out _); return null; }
        catch (UnauthorizedAccessException) { NdjsonCache.TryRemove(full, out _); return null; }

        if (!TryFileStamp(full, out var finalLength, out var finalMtimeTicks) ||
            finalLength != length || finalMtimeTicks != mtimeTicks)
        {
            NdjsonCache.TryRemove(full, out _);
            return null;
        }

        if (complete)
        {
            var entry = new NdjsonCacheEntry(length, mtimeTicks, parsed);
            NdjsonCache[full] = entry;
            return entry.Receipts;
        }

        NdjsonCache.TryRemove(full, out _);
        return parsed;
    }

    /// <summary>
    /// Lettura fisica all-or-nothing usata prima di append distruttivi/compattazione. A differenza del lookup,
    /// una sola riga parziale, receipt malformata o TransactionId divergente rende l'intero file non compattabile.
    /// </summary>
    private static bool TryReadNdjsonStrict(
        string path,
        out IReadOnlyDictionary<Guid, WorkflowTransactionCommitReceipt> receipts)
    {
        receipts = new Dictionary<Guid, WorkflowTransactionCommitReceipt>();
        var full = Path.GetFullPath(path);
        if (!TryFileStamp(full, out var length, out var mtimeTicks)) return false;

        var parsed = new Dictionary<Guid, WorkflowTransactionCommitReceipt>();
        try
        {
            // Solo la modalita' di apertura cambia: la semantica resta all-or-nothing (una riga non valida
            // o una divergenza di stamp fanno ancora fallire l'intera lettura, e il file-stamp viene
            // riverificato dopo l'enumerazione).
            foreach (var line in JsonIo.ReadLinesShared(full))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                WorkflowTransactionCommitReceipt? item;
                try { item = JsonSerializer.Deserialize<WorkflowTransactionCommitReceipt>(line, CompactJsonOptions); }
                catch (JsonException) { return false; }
                if (item == null || !IsStructurallyValidReceipt(item)) return false;
                if (parsed.TryGetValue(item.Id, out var duplicate) && !ReceiptsEquivalent(duplicate, item)) return false;
                parsed[item.Id] = item;
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        if (!TryFileStamp(full, out var finalLength, out var finalMtimeTicks) ||
            finalLength != length || finalMtimeTicks != mtimeTicks)
            return false;

        receipts = parsed;
        return true;
    }

    private static WorkflowCommitLedgerCheckpoint? ReadCheckpointCached(string path, bool forceRefresh = false)
    {
        var full = Path.GetFullPath(path);
        if (!TryFileStamp(full, out var length, out var mtimeTicks))
        {
            CheckpointCache.TryRemove(full, out _);
            return null;
        }

        if (!forceRefresh && CheckpointCache.TryGetValue(full, out var cached) &&
            cached.Length == length && cached.LastWriteUtcTicks == mtimeTicks)
            return cached.Checkpoint;

        var checkpoint = ReadCheckpoint(full);
        if (checkpoint == null)
        {
            CheckpointCache.TryRemove(full, out _);
            return null;
        }
        if (!TryFileStamp(full, out var finalLength, out var finalMtimeTicks) ||
            finalLength != length || finalMtimeTicks != mtimeTicks)
        {
            CheckpointCache.TryRemove(full, out _);
            return null;
        }

        CheckpointCache[full] = new CheckpointCacheEntry(length, mtimeTicks, checkpoint);
        return checkpoint;
    }

    private static bool IsNdjsonCacheCurrent(string path)
    {
        var full = Path.GetFullPath(path);
        return TryFileStamp(full, out var length, out var mtimeTicks) &&
               NdjsonCache.TryGetValue(full, out var cached) &&
               cached.Length == length && cached.LastWriteUtcTicks == mtimeTicks;
    }

    private static bool IsCheckpointCacheCurrent(string path)
    {
        var full = Path.GetFullPath(path);
        return TryFileStamp(full, out var length, out var mtimeTicks) &&
               CheckpointCache.TryGetValue(full, out var cached) &&
               cached.Length == length && cached.LastWriteUtcTicks == mtimeTicks;
    }

    private static void TryRotateAndCompact(DataPaths paths, string deviceId, bool force = false)
    {
        try
        {
            var dir = DeviceDirectory(paths.Root, deviceId);
            Directory.CreateDirectory(dir);
            var current = Path.Combine(dir, "current.ndjson");
            if (File.Exists(current) && new FileInfo(current).Length > 0 &&
                (force || new FileInfo(current).Length >= RotateAfterBytes))
            {
                var segment = Path.Combine(dir,
                    $"segment-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.ndjson");
                File.Move(current, segment, overwrite: false);
                NdjsonCache.TryRemove(Path.GetFullPath(current), out _);
            }

            var segments = SafeFiles(dir, "segment-*.ndjson")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            if (segments.Count <= CompletedSegmentsToKeep) return;

            var compactThese = segments.Take(segments.Count - CompletedSegmentsToKeep).ToList();
            var parsedByFile = new List<(string File, IReadOnlyDictionary<Guid, WorkflowTransactionCommitReceipt> Receipts)>(compactThese.Count);
            foreach (var file in compactThese)
            {
                // La compattazione è distruttiva sui segmenti sorgente: qui la cache metadata non basta e la
                // lettura prefix-safe del lookup non è sufficiente. Ogni byte deve essere interpretato.
                if (!TryReadNdjsonStrict(file, out var receipts)) return;
                parsedByFile.Add((file, receipts));
            }

            var checkpointPath = Path.Combine(dir, "checkpoint.json.gz");
            var checkpoint = ReadCheckpointCached(checkpointPath, forceRefresh: true);
            if (File.Exists(checkpointPath) && checkpoint == null)
            {
                // File presente ma corrotto/parziale/di schema futuro: non sostituirlo con un checkpoint vuoto.
                return;
            }
            checkpoint ??= new WorkflowCommitLedgerCheckpoint
            {
                SchemaVersion = SchemaVersion,
                DeviceId = deviceId
            };

            foreach (var (_, receipts) in parsedByFile)
            {
                foreach (var receipt in receipts.Values)
                {
                    var key = receipt.Id.ToString("N");
                    if (checkpoint.Receipts.TryGetValue(key, out var existing) && !ReceiptsEquivalent(existing, receipt))
                        return; // conflitto TransactionId: preserva sorgenti e checkpoint senza scegliere un vincitore.
                    checkpoint.Receipts[key] = receipt;
                }
            }
            checkpoint.DeviceId = deviceId;
            checkpoint.SchemaVersion = SchemaVersion;
            checkpoint.UpdatedUtc = DateTime.UtcNow;
            WriteCheckpointAtomic(checkpointPath, checkpoint);

            foreach (var file in compactThese)
            {
                try { File.Delete(file); } catch { }
                NdjsonCache.TryRemove(Path.GetFullPath(file), out _);
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Compattazione commit ledger workflow", ex);
        }
    }

    private static WorkflowCommitLedgerCheckpoint? ReadCheckpoint(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var checkpoint = JsonSerializer.Deserialize<WorkflowCommitLedgerCheckpoint>(gzip, CompactJsonOptions);
            return IsStructurallyValidCheckpoint(checkpoint) ? checkpoint : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            CrashLogService.Registra($"Checkpoint commit ledger non leggibile: {path}", ex);
            return null;
        }
    }

    private static void WriteCheckpointAtomic(string path, WorkflowCommitLedgerCheckpoint checkpoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                    JsonSerializer.Serialize(gzip, checkpoint, CompactJsonOptions);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            var full = Path.GetFullPath(path);
            if (TryFileStamp(full, out var length, out var mtimeTicks))
                CheckpointCache[full] = new CheckpointCacheEntry(length, mtimeTicks, checkpoint);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static bool IsStructurallyValidCheckpoint(WorkflowCommitLedgerCheckpoint? checkpoint)
    {
        if (checkpoint == null || checkpoint.SchemaVersion != SchemaVersion ||
            string.IsNullOrWhiteSpace(checkpoint.DeviceId) || checkpoint.Receipts == null)
            return false;

        foreach (var pair in checkpoint.Receipts)
        {
            var receipt = pair.Value;
            if (receipt == null || !IsStructurallyValidReceipt(receipt) ||
                !pair.Key.Equals(receipt.Id.ToString("N"), StringComparison.OrdinalIgnoreCase) ||
                !receipt.DeviceId.Equals(checkpoint.DeviceId, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>Riga del ledger valida come receipt completa: usata solo dalla riparazione della coda parziale.</summary>
    private static bool RigaReceiptValida(string riga)
    {
        try
        {
            var item = JsonSerializer.Deserialize<WorkflowTransactionCommitReceipt>(riga, CompactJsonOptions);
            return item != null && IsStructurallyValidReceipt(item);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsStructurallyValidReceipt(WorkflowTransactionCommitReceipt receipt, bool requireDeviceId = true)
    {
        if (receipt.Id == Guid.Empty ||
            (requireDeviceId && string.IsNullOrWhiteSpace(receipt.DeviceId)) ||
            receipt.Entries is not { Count: > 0 } entries)
            return false;
        return entries.All(entry => entry != null &&
            !string.IsNullOrWhiteSpace(entry.RelativePath) && !string.IsNullOrWhiteSpace(entry.FinalSha256));
    }

    private static bool ReceiptsEquivalent(WorkflowTransactionCommitReceipt a, WorkflowTransactionCommitReceipt b)
    {
        if (a.Id != b.Id || !string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase)) return false;
        var left = a.Entries.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var right = b.Entries.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].RelativePath.Equals(right[i].RelativePath, StringComparison.OrdinalIgnoreCase) ||
                !left[i].FinalSha256.Equals(right[i].FinalSha256, StringComparison.OrdinalIgnoreCase) ||
                left[i].LogicallyDeleted != right[i].LogicallyDeleted) return false;
        }
        return true;
    }

    private static bool TryFileStamp(string path, out long length, out long mtimeTicks)
    {
        length = 0;
        mtimeTicks = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            length = info.Length;
            mtimeTicks = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch { return false; }
    }

    private static string DeviceDirectory(string root, string deviceId)
    {
        var safe = string.Concat(deviceId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        if (string.IsNullOrWhiteSpace(safe)) throw new InvalidDataException("DeviceId commit ledger non valido.");
        return Path.Combine(root, "_ditm", "transactions", "committed", safe);
    }

    private static string LegacyReceiptPath(string root, Guid transactionId) =>
        Path.Combine(root, "_ditm", "transactions", "committed", $"{transactionId:N}.committed.json");

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private sealed record CheckpointCacheEntry(long Length, long LastWriteUtcTicks, WorkflowCommitLedgerCheckpoint Checkpoint);
    private sealed record NdjsonCacheEntry(long Length, long LastWriteUtcTicks, IReadOnlyDictionary<Guid, WorkflowTransactionCommitReceipt> Receipts);
}

public sealed class WorkflowCommitLedgerCheckpoint
{
    public int SchemaVersion { get; set; } = 1;
    public string DeviceId { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public Dictionary<string, WorkflowTransactionCommitReceipt> Receipts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
