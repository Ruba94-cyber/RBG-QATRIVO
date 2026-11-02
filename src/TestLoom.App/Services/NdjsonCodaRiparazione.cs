using System.Security.Cryptography;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>
/// Riparazione della coda di un ledger NDJSON condiviso (current.ndjson) lasciata da un'append interrotta a metà riga
/// (crash, VPN caduta, client di sincronizzazione). Stesso contratto di PhaseDMasterJournalService.TryRiparaCodaTroncata,
/// condiviso da ledger di receipt, notifiche e journal dei test.
///
/// Agisce SOLO se tutte le righe terminate da newline sono valide e l'unica riga non valida è l'ultima, non terminata da
/// newline. In quel caso la coda viene copiata integralmente in current.ndjson.torn-&lt;timestamp&gt; PRIMA del troncamento
/// e poi rimossa dal ledger. Una riga completa e valida senza newline finale non viene mai scartata: viene solo chiusa.
/// Ogni altro caso (riga intermedia non valida) ritorna Irriparabile senza modificare il file.
///
/// Riparazione e append vanno eseguiti sotto <see cref="LockCrossProcesso"/>: senza il mutex un altro processo dello stesso
/// PC potrebbe troncare la riga che sta scrivendo.
/// </summary>
internal static class NdjsonCodaRiparazione
{
    /// <summary>File laterali `current.ndjson.torn-*` conservati per ledger (i più recenti).</summary>
    internal const int TornConservati = 10;

    /// <summary>Attesa massima del mutex cross-processo: oltre, l'operazione fallisce senza modifiche invece di bloccarsi.</summary>
    private static readonly TimeSpan AttesaMutex = TimeSpan.FromSeconds(30);

    internal enum Esito
    {
        /// <summary>Nessuna coda parziale: il file è integro oppure termina con una riga valida senza newline.</summary>
        Integra,
        /// <summary>La coda parziale è stata copiata in .torn-* e rimossa dal ledger.</summary>
        Riparata,
        /// <summary>Riga intermedia non valida o file non attribuibile: nessuna modifica eseguita.</summary>
        Irriparabile
    }

    /// <summary>Nome del mutex nominato per UN file ledger (hash SHA-256 del percorso completo). Local\: il ledger di un device
    /// è scritto da un solo PC, quindi basta la sessione di logon.</summary>
    internal static string NomeMutex(string prefisso, string currentPath)
    {
        var chiave = Path.GetFullPath(currentPath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chiave)));
        return $@"Local\TestLoom-{prefisso}-{hash}";
    }

    /// <summary>Mutex cross-processo del ledger. Si acquisisce SEMPRE dopo il lock in-process (prima il gate, poi il mutex).
    /// AbandonedMutexException (un processo è morto a metà di un append) vale come acquisizione: la coda incompleta viene
    /// poi riparata dal percorso di append.</summary>
    internal sealed class LockCrossProcesso : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly bool _posseduto;

        public LockCrossProcesso(string prefisso, string currentPath)
        {
            _mutex = new Mutex(false, NomeMutex(prefisso, currentPath));
            try
            {
                _posseduto = _mutex.WaitOne(AttesaMutex);
                if (!_posseduto)
                    throw new IOException($"Ledger '{Path.GetFileName(currentPath)}' occupato da un altro processo: operazione annullata senza modifiche.");
            }
            catch (AbandonedMutexException)
            {
                _posseduto = true;
            }
            catch
            {
                _mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try { if (_posseduto) _mutex.ReleaseMutex(); }
            finally { _mutex.Dispose(); }
        }
    }

    /// <summary>
    /// Prepara l'accodamento di una riga su un ledger il cui ultimo byte non è newline. Ritorna il separatore da anteporre alla
    /// prima riga nuova ("" oppure newline). Lancia InvalidDataException senza modificare il file se la coda non è riparabile.
    /// Il chiamante deve tenere il <see cref="LockCrossProcesso"/> per tutta la durata della riparazione e dell'append.
    /// </summary>
    internal static string PreparaAccodamento(string current, Func<string, bool> rigaValida, string etichetta, string messaggioIrriparabile)
    {
        if (!File.Exists(current) || new FileInfo(current).Length == 0) return "";
        if (UltimoByteNewline(current)) return "";

        if (RiparaSeCodaParziale(current, rigaValida, etichetta) == Esito.Irriparabile)
            throw new InvalidDataException(messaggioIrriparabile);

        // Dopo una riparazione il file termina con newline (o è vuoto). Senza riparazione, l'ultima riga è valida ma senza newline.
        return UltimoByteNewline(current) ? "" : Environment.NewLine;
    }

    /// <summary>
    /// Ripara la coda parziale di un ledger. Da chiamare quando la lettura strict fallisce. Non ingoia errori di I/O: un file
    /// illeggibile è un errore del chiamante, non una coda da scartare.
    /// </summary>
    internal static Esito RiparaSeCodaParziale(string current, Func<string, bool> rigaValida, string etichetta)
    {
        var bytes = JsonIo.ReadAllBytesShared(current);
        var prefissoLen = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        var codaLen = bytes.Length - prefissoLen;
        if (codaLen == 0) return Esito.Irriparabile; // termina con newline: l'errore è in una riga intermedia, non si ripara

        var prefisso = Encoding.UTF8.GetString(bytes, 0, prefissoLen);
        foreach (var riga in prefisso.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(riga)) continue;
            if (!rigaValida(riga)) return Esito.Irriparabile;
        }

        var coda = Encoding.UTF8.GetString(bytes, prefissoLen, codaLen);
        if (rigaValida(coda)) return Esito.Integra;

        var laterale = current + ".torn-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        using (var copia = new FileStream(laterale, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.WriteThrough))
        {
            copia.Write(bytes, prefissoLen, codaLen);
            copia.Flush(flushToDisk: true);
        }
        using (var ledger = new FileStream(current, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            ledger.SetLength(prefissoLen);
            ledger.Flush(flushToDisk: true);
        }

        DiagnosticTraceService.Registra($"{etichetta}.CodaRiparata",
            $"ledger={current}; laterale={Path.GetFileName(laterale)}; byte={codaLen}");
        PotaTornObsoleti(current);
        return Esito.Riparata;
    }

    /// <summary>Conserva solo i <see cref="TornConservati"/> file laterali più recenti. Il nome porta un timestamp UTC a larghezza
    /// fissa, quindi l'ordinamento lessicografico coincide con quello cronologico. Best-effort: un errore non annulla la riparazione.</summary>
    internal static void PotaTornObsoleti(string current)
    {
        try
        {
            var dir = Path.GetDirectoryName(current)!;
            var torn = Directory.EnumerateFiles(dir, Path.GetFileName(current) + ".torn-*", SearchOption.TopDirectoryOnly)
                .OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal)
                .ToList();
            var daEliminare = torn.Count - TornConservati;
            if (daEliminare <= 0) return;

            foreach (var file in torn.Take(daEliminare))
            {
                try { File.Delete(file); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            DiagnosticTraceService.Registra("Ledger.TornPotati.Errore", $"ledger={current}; {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static bool UltimoByteNewline(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0) return true;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }
}
