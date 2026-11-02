using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestLoom.App.Services;

/// <summary>Opzioni di serializzazione condivise da tutto il livello di persistenza.</summary>
public static class JsonIo
{
    private static readonly int[] TransientReadRetryDelaysMs = [75, 225];

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions CompactOptions = new(Options) { WriteIndented = false };

    /// <summary>
    /// Legge un JSON con retry brevi per gli stati transitori OneDrive (handle aperto dal client di sync,
    /// scrittura in corso). Decisione 6.4.2 (docs/PIANO_6.4.2.md §1): un JSON di dominio sintatticamente valido
    /// è SEMPRE leggibile, indipendentemente da eventuali marker _ditmTx/_ditmTxDevice (diagnostica, ignorati in
    /// deserializzazione) — nessun gate cross-file basato su ricevute di commit. Un tombstone
    /// (<see cref="WorkflowTransactionVisibilityService.DeletedProperty"/> = true) resta un'eliminazione
    /// logica e viene sempre riconosciuto come tale (vedi <see cref="TryParse{T}(ReadOnlySpan{byte}, out string?)"/>).
    /// </summary>
    public static T? TryRead<T>(string path, out string? errore) where T : class
    {
        errore = null;
        string? ultimoErrore = null;

        for (var tentativo = 0; tentativo <= TransientReadRetryDelaysMs.Length; tentativo++)
        {
            if (!File.Exists(path))
            {
                errore = ultimoErrore ?? "File non trovato.";
                return null;
            }

            try
            {
                var bytes = ReadAllBytesShared(path);
                var obj = TryParse<T>(bytes, out var parseError);
                if (obj != null)
                {
                    errore = null;
                    return obj;
                }
                if (parseError != null && parseError.StartsWith("Tombstone TestLoom", StringComparison.Ordinal))
                {
                    // Eliminazione logica riconosciuta: non e' un errore transitorio, nessun retry ha senso.
                    errore = parseError;
                    return null;
                }
                ultimoErrore = parseError;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ultimoErrore = ex.Message;
            }
            catch (Exception ex)
            {
                errore = ex.Message;
                return null;
            }

            if (tentativo >= TransientReadRetryDelaysMs.Length) break;
            Thread.Sleep(TransientReadRetryDelaysMs[tentativo]);
        }

        errore = ultimoErrore ?? "File JSON temporaneamente non leggibile.";
        return null;
    }

    public static T? TryParse<T>(string json, out string? errore) where T : class
    {
        errore = null;
        try
        {
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(WorkflowTransactionVisibilityService.DeletedProperty, out var deleted) &&
                    deleted.ValueKind == JsonValueKind.True)
                {
                    errore = "Tombstone TestLoom: il record è stato eliminato logicamente.";
                    return null;
                }
            }

            var obj = JsonSerializer.Deserialize<T>(json, Options);
            if (obj == null) errore = "File vuoto o contenuto 'null'";
            return obj;
        }
        catch (Exception ex)
        {
            errore = ex.Message;
            return null;
        }
    }

    public static T? TryParse<T>(ReadOnlySpan<byte> utf8Json, out string? errore) where T : class
    {
        errore = null;
        try
        {
            if (utf8Json.Length >= 3 && utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF)
                utf8Json = utf8Json[3..];

            // Il tombstone si riconosce dalla sola proprietà _ditmDeleted, indipendentemente da _ditmTx (che sui
            // tombstone resta diagnostica, vedi WorkflowTransactionService.CreateTombstone): stesso criterio
            // dell'overload string-based sopra.
            var reader = new Utf8JsonReader(utf8Json);
            using (var document = JsonDocument.ParseValue(ref reader))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(WorkflowTransactionVisibilityService.DeletedProperty, out var deleted) &&
                    deleted.ValueKind == JsonValueKind.True)
                {
                    errore = "Tombstone TestLoom: il record è stato eliminato logicamente.";
                    return null;
                }
            }

            var obj = JsonSerializer.Deserialize<T>(utf8Json, Options);
            if (obj == null) errore = "File vuoto o contenuto 'null'";
            return obj;
        }
        catch (Exception ex)
        {
            errore = ex.Message;
            return null;
        }
    }

    public static void WriteAtomic<T>(string path, T obj, bool overwrite = true)
    {
        // Catturare l'esistenza PRIMA della serializzazione chiude una finestra reale sui payload grandi:
        // mentre JsonSerializer lavora un altro PC/processo può creare la stessa destinazione. Se il file non
        // esisteva all'ingresso, il rename finale deve restare create-only anche quando il caller usa il default
        // overwrite:true; altrimenti un record nato nel frattempo verrebbe sostituito silenziosamente.
        var destinationExistedAtEntry = File.Exists(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, Options);
        WriteBytesAtomicCore(path, bytes, overwrite, destinationExistedAtEntry);
    }

    public static void WriteBytesAtomic(string path, ReadOnlySpan<byte> bytes, bool overwrite = true)
    {
        // Per chi fornisce byte già serializzati la finestra inizia qui, quindi la fotografia viene presa
        // immediatamente prima di qualunque lavoro transazionale/temp-file.
        WriteBytesAtomicCore(path, bytes, overwrite, File.Exists(path));
    }

    private static void WriteBytesAtomicCore(string path, ReadOnlySpan<byte> bytes, bool overwrite, bool destinationExistedAtEntry)
    {
        byte[] bytesToWrite;
        string? observedBeforeSha;
        try
        {
            WorkflowTransactionService.BeforeWrite(path);
            bytesToWrite = WorkflowTransactionService.PrepareJsonWrite(path, bytes);

            // Il pilot trace è volutamente opt-in: solo durante il pilot paghiamo la lettura/hash della before-image
            // anche fuori da una WorkflowTransaction. Questo permette di ricostruire la finestra A -> local replace
            // -> B senza alterare i benchmark ordinari o l'uso quotidiano.
            observedBeforeSha = destinationExistedAtEntry
                ? PilotConcurrencyTraceService.CaptureBeforeHash(path)
                : null;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            // 6.6.1 (correzione C): anche le operazioni che precedono il file temporaneo (transazione, fotografia, cartella)
            // possono fallire per radice persa: vengono osservate come le scritture successive.
            var sostitutiva = SharedWriteLossMonitor.Osserva(path, ex);
            if (sostitutiva != null) throw sostitutiva;
            throw;
        }
        var tmp = path + ".tmp" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                stream.Write(bytesToWrite);
                stream.Flush(flushToDisk: true);
            }

            // overwrite=false esplicito resta sempre autorevole. Con overwrite=true sovrascriviamo soltanto se
            // la destinazione esisteva già quando l'operazione è iniziata; una creazione concorrente avvenuta
            // durante serializzazione/prepare/temp-write fa fallire File.Move e viene gestita dal repository
            // come conflitto, non trasformata in last-writer-wins.
            // Intenzione con hash atteso registrata prima dello spostamento (review P1.9): un crash dopo File.Move è riconoscibile.
            WorkflowTransactionService.BeforeMove(path, bytesToWrite);
            File.Move(tmp, path, overwrite && destinationExistedAtEntry);
        }
        catch (Exception ex)
        {
            // Fallimento prima dello spostamento: il file di destinazione è invariato e l'errore torna al chiamante.
            PilotConcurrencyTraceService.RecordAtomicWrite(
                path,
                destinationExistedAtEntry,
                overwrite,
                bytesToWrite,
                outcome: "failed",
                observedBeforeSha256: observedBeforeSha,
                exceptionType: ex.GetType().Name);
            // 6.6.1 (d): un errore di cartella/rete/permessi persi su una cartella condivisa porta il workspace in sola
            // lettura. Il file resta invariato e l'errore viene comunque rilanciato al chiamante, che salva la bozza.
            // 6.6.1 (correzione 2): negazione di un solo file con radice che risponde = messaggio chiaro, nessuna sola lettura.
            var sostitutiva = SharedWriteLossMonitor.Osserva(path, ex);
            if (sostitutiva != null) throw sostitutiva;
            throw;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { }
        }

        // Dal File.Move il file contiene i nuovi byte: la scrittura è avvenuta. Nessun errore successivo (registrazione nella
        // transazione, traccia) può farla risultare fallita: il chiamante annullerebbe in memoria una modifica già visibile su
        // disco e gli effetti post-scrittura (es. journal) verrebbero cancellati (review P1.11). La transazione registra
        // l'errore e diventa non committabile; la traccia di successo non dipende dagli esiti precedenti.
        WorkflowTransactionService.AfterWrite(path, bytesToWrite);
        try
        {
            PilotConcurrencyTraceService.RecordAtomicWrite(
                path,
                destinationExistedAtEntry,
                overwrite,
                bytesToWrite,
                outcome: "succeeded",
                observedBeforeSha256: observedBeforeSha);
            PilotTransactionOutcomeTraceService.RegisterForSuccessfulWrite(path, bytesToWrite);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra($"Traccia della scrittura riuscita: {path}", ex);
        }
    }

    /// <summary>
    /// Legge i byte del file consentendo la condivisione in lettura, scrittura ed eliminazione simultanea
    /// (FileShare.ReadWrite | FileShare.Delete), impedendo violazioni di condivisione (sharing violation)
    /// qualora un altro PC, il client OneDrive o un processo di sincronizzazione stia accedendo al file.
    /// </summary>
    public static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var bytes = new byte[stream.Length];
        var totalRead = 0;
        while (totalRead < bytes.Length)
        {
            var read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        if (totalRead != bytes.Length)
        {
            Array.Resize(ref bytes, totalRead);
        }
        return bytes;
    }

    /// <summary>
    /// Enumera le righe di un file di testo consentendo la condivisione in lettura, scrittura ed eliminazione
    /// simultanea (FileShare.ReadWrite | FileShare.Delete), esattamente come <see cref="ReadAllBytesShared"/>.
    /// Serve ai ledger NDJSON condivisi (_ditm/transactions/committed, _ditm/journal): File.ReadLines apre con
    /// FileShare.Read e fallisce con IOException quando il client OneDrive/SharePoint tiene il file aperto in
    /// scrittura, facendo risultare "non trovata" una receipt in realta' gia' presente.
    ///
    /// La semantica rispetto a File.ReadLines non cambia: l'ultima riga senza terminatore viene comunque
    /// restituita, quindi i chiamanti prefix-safe e quelli strict all-or-nothing continuano a vedere una riga
    /// parziale come tale e a scartarla. L'enumerazione e' pigra: le eccezioni di I/O emergono durante il
    /// foreach, dove i chiamanti le gestiscono gia'.
    /// </summary>
    public static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}
