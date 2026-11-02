using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TestLoom.App.Services;

/// <summary>
/// Decisione utente 6.4.2: gli snapshot ZIP di backup sono SOLO locali al PC, sotto la stessa cartella
/// per-workspace di <see cref="LocalTestIndexStore"/> (%LOCALAPPDATA%\TestLoom\workspaces\&lt;hash&gt;\backup, via
/// <see cref="WorkspaceLocalPaths"/>). La cartella condivisa &lt;Root&gt;/backup NON viene più scritta: OneDrive/
/// SharePoint ha già la propria cronologia versioni sui JSON condivisi, e duplicare lì gli snapshot serviva solo
/// a moltiplicare I/O di sincronizzazione. Gli zip condivisi già esistenti da versioni precedenti NON vengono
/// toccati (né letti né cancellati) da questa classe.
/// </summary>
public static class BackupService
{
    private static readonly TimeSpan TempRetention = TimeSpan.FromDays(1);

    /// <summary>Solo gli ultimi 3 snapshot locali vengono conservati (decisione utente 6.4.2): a differenza
    /// della vecchia retention 14/8/12 pensata per una cartella condivisa senza cronologia propria, qui basta
    /// una manciata di ripristini rapidi recenti perché OneDrive/SharePoint copre già la cronologia a lungo
    /// termine dei JSON.</summary>
    internal const int MaxSnapshotsLocali = 3;

    /// <summary>Nome del file che registra l'impronta (fingerprint) dell'ultimo snapshot pubblicato, nella
    /// stessa cartella degli zip. Usato SOLO dal giornaliero automatico per decidere se i dati sono cambiati
    /// dall'ultimo snapshot; il manuale ("Crea ora") lo aggiorna ma non lo consulta mai, perché deve creare
    /// sempre su richiesta esplicita dell'utente.</summary>
    internal const string FingerprintFileName = "last-snapshot.fingerprint";

    // Un .tmp di QUESTO PC non può appartenere a una scrittura ancora davvero in corso più a lungo di questa
    // grazia (WriteGate garantisce un solo backup per PROCESSO alla volta e ogni entry scritta nello ZIP
    // aggiorna LastWriteTime): oltre la grazia è sempre un orfano da una chiusura brusca precedente, rimovibile
    // subito invece di aspettare TempRetention. Un .tmp di un ALTRO PC può invece essere legittimamente in
    // scrittura in questo momento (revisione review: cartella CONDIVISA fra PC) e va toccato solo se più vecchio
    // di TempRetention.
    private static readonly TimeSpan OwnDeviceOrphanGrace = TimeSpan.FromMinutes(5);

    // Serializza le SCRITTURE di backup (giornaliero automatico vs manuale "Crea ora"): entrambi condividono lo
    // stesso BackupDir e lo stesso file .tmp/.zip per prefisso; senza questo gate potrebbero sovrapporsi.
    // Il giornaliero (in background, priorità bassa) ATTENDE la propria fetta con un WaitAsync/Wait cancellabile;
    // il manuale invece è un click esplicito dell'utente e deve fallire subito con un messaggio chiaro invece di
    // restare fermo dietro al giornaliero (criterio d'accettazione 5).
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    /// <summary>Punto di iniezione SOLO per test (assembly interno, mai usato in produzione): permette di
    /// simulare un backup lento/bloccante senza dover creare davvero centinaia di migliaia di file. Chiamato una
    /// sola volta per esecuzione di <see cref="Crea"/>, subito dopo la fotografia "before", col token di
    /// cancellazione della stessa esecuzione. Nessun default diverso da null non altera in alcun modo il
    /// comportamento a produzione invariata.</summary>
    internal static Func<CancellationToken, Task>? TestSlowdownHook;

    /// <summary>Punto di iniezione SOLO per test: chiamato dopo ogni blocco da 128 KB letto durante l'hashing
    /// SHA-256 di CaptureSourceSnapshot/AddVerifiedSource (ComputeSha256Blocked) — il punto specifico che prima
    /// chiamava <c>SHA256.HashData(stream)</c> sull'intero file senza controllare mai il token. Permette di
    /// simulare una lettura realmente lenta/bloccante su un file di dimensione normale, senza dover generare
    /// davvero centinaia di migliaia di file grandi. Nullo in produzione, nessun comportamento diverso.</summary>
    internal static Func<CancellationToken, Task>? TestPerBlockHook;

    /// <summary>Come <see cref="TestPerBlockHook"/> ma per la copia nello ZIP (CopyToZipEntryAndHash): distinto
    /// perché quella fase, a differenza dell'hashing dello snapshot, gira DOPO che il file .tmp è già stato
    /// creato su disco — serve ai test che devono verificare la pulizia di un .tmp realmente orfano, non solo
    /// la cancellazione prima che il .tmp esista.</summary>
    internal static Func<CancellationToken, Task>? TestCopyBlockHook;

    public static string? CreaGiornalieroSeNecessario(DataPaths paths, DateTime now) =>
        CreaGiornalieroSeNecessario(paths, now, CancellationToken.None);

    /// <summary>Overload interno con CancellationToken, usato dallo scheduler in background (AppState): il
    /// giornaliero attende il proprio turno sul gate di scrittura (mai un throw immediato come il manuale) ma
    /// resta comunque annullabile rapidamente se l'app chiude o la cartella cambia mentre è in coda.
    /// <paramref name="localBaseDirectory"/> esiste solo per test (sovrascrive %LOCALAPPDATA%), mai in
    /// produzione — stesso meccanismo di LocalTestIndexStore.</summary>
    internal static string? CreaGiornalieroSeNecessario(DataPaths paths, DateTime now, CancellationToken ct, string? localBaseDirectory = null)
    {
        EnsureSnapshotCanStart(paths, "Backup giornaliero");
        var backupDir = WorkspaceLocalPaths.GetBackupDirectory(paths.Root, localBaseDirectory);
        Directory.CreateDirectory(backupDir);
        ApplicaRetention(backupDir, now);
        var prefix = $"TestLoom_snapshot_{now:yyyyMMdd}";
        if (Directory.EnumerateFiles(backupDir, prefix + "*.zip").Any()) return null;

        WriteGate.Wait(ct);
        try { return CreaSeCambiato(paths, now, prefix, ct, backupDir); }
        finally { WriteGate.Release(); }
    }

    public static string CreaOra(DataPaths paths, DateTime now, string? localBaseDirectory = null)
    {
        EnsureSnapshotCanStart(paths, "Backup manuale");
        if (!WriteGate.Wait(0))
            throw new IOException("Backup manuale non eseguito: è in corso il backup giornaliero automatico. Riprova tra qualche minuto.");
        try
        {
            var backupDir = WorkspaceLocalPaths.GetBackupDirectory(paths.Root, localBaseDirectory);
            // Richiesta esplicita dell'utente ("Crea backup adesso" in Impostazioni): crea SEMPRE, indipendentemente
            // dal fingerprint dell'ultimo snapshot pubblicato — a differenza del giornaliero automatico.
            return Crea(paths, now, $"TestLoom_snapshot_{now:yyyyMMdd_HHmmss}", CancellationToken.None, backupDir);
        }
        finally { WriteGate.Release(); }
    }

    /// <summary>Solo per il giornaliero automatico: salta la creazione dello zip se il fingerprint dei dati
    /// (fotografia SHA-256 per-file, senza mtime) coincide con quello dell'ultimo snapshot pubblicato in questa
    /// cartella. Il manuale ("Crea ora") non passa mai da qui: crea sempre su richiesta esplicita.</summary>
    private static string? CreaSeCambiato(DataPaths paths, DateTime now, string name, CancellationToken ct, string backupDir)
    {
        EnsureSnapshotCanStart(paths, "Backup giornaliero");
        ct.ThrowIfCancellationRequested();
        var before = CaptureSourceSnapshot(paths, ct);
        var fingerprint = ComputeFingerprint(paths, before);

        var fingerprintFile = Path.Combine(backupDir, FingerprintFileName);
        if (File.Exists(fingerprintFile))
        {
            string? precedente;
            try { precedente = File.ReadAllText(fingerprintFile).Trim(); }
            catch { precedente = null; } // file fingerprint illeggibile: meglio un backup di troppo che uno saltato per errore
            if (precedente != null && precedente.Equals(fingerprint, StringComparison.Ordinal))
                return null; // nulla è cambiato dall'ultimo snapshot: il giornaliero non produce un nuovo zip
        }

        return Crea(paths, now, name, ct, backupDir, precomputedBefore: before, precomputedFingerprint: fingerprint);
    }

    private static string Crea(DataPaths paths, DateTime now, string name, CancellationToken ct, string backupDir,
        List<BackupSourceStamp>? precomputedBefore = null, string? precomputedFingerprint = null)
    {
        EnsureSnapshotCanStart(paths, "Creazione backup");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(backupDir);
        // Il DeviceId nel nome del temporary (mai nel nome finale .zip, invariato) è storico da quando questa
        // cartella era condivisa fra PC: dalla 6.4.2 la cartella è SOLO locale a questo PC (nessun altro
        // processo su un altro PC può scriverci), ma il suffisso resta comunque utile per distinguere un .tmp di
        // QUESTO processo da un eventuale secondo processo TestLoom avviato sullo stesso PC.
        var temp = Path.Combine(backupDir, $"{name}.{DeviceSuffix()}.tmp");
        var final = Path.Combine(backupDir, name + ".zip");
        var before = precomputedBefore ?? CaptureSourceSnapshot(paths, ct);

        var slowdown = TestSlowdownHook;
        if (slowdown != null) slowdown(ct).GetAwaiter().GetResult();

        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var source in before)
                {
                    ct.ThrowIfCancellationRequested();
                    // La copia viene hashata mentre entra nello ZIP e deve coincidere con la fotografia iniziale.
                    // Così una versione arrivata dopo CaptureSourceSnapshot non può essere inglobata nello
                    // snapshot e poi "sparire" prima del controllo finale facendo risultare before==after.
                    var entry = zip.CreateEntry(Path.GetRelativePath(paths.Root, source.Path), CompressionLevel.Optimal);
                    var copiedHash = CopyToZipEntryAndHash(source.Path, entry, ct);
                    if (!copiedHash.Equals(source.Sha256, StringComparison.Ordinal))
                        throw new IOException($"Backup non pubblicato: '{Path.GetRelativePath(paths.Root, source.Path)}' è cambiato durante la copia nello snapshot.");
                }
                var manifest = zip.CreateEntry("snapshot-manifest.txt");
                using var writer = new StreamWriter(manifest.Open());
                writer.WriteLine($"TestLoom snapshot {now:O}");
                writer.WriteLine("Contiene storage TestLoom riconosciuto: JSON di dominio/config e source of truth master Phase D; allegati esclusi e protetti dalla cronologia SharePoint.");
                writer.WriteLine($"File TestLoom verificati: {before.Count}");
                writer.WriteLine("Consistenza: file-set, dimensioni, LastWriteTimeUTC e SHA-256 verificati; anche i byte copiati nello ZIP devono coincidere con la fotografia iniziale.");
            }

            ct.ThrowIfCancellationRequested();

            // Una transazione o una mutation può iniziare dopo il primo preflight. Prima del rename finale
            // verifichiamo sia la barriera sia la stessa fotografia completa: qualunque append/rotazione/
            // checkpoint Phase D cambia file-set o SHA e impedisce di pubblicare uno snapshot misto.
            if (!SharedWorkflowTransactionGuard.CanReadConsistently(paths, out var barrierMessage))
                throw new IOException($"Backup non pubblicato: {barrierMessage}");

            var after = CaptureSourceSnapshot(paths, ct);
            if (!SameSnapshot(before, after))
                throw new IOException("Backup non pubblicato: i dati TestLoom sono cambiati durante la creazione dello snapshot. Verrà ritentato da uno stato stabile.");

            File.Move(temp, final, false);
            // Fingerprint aggiornato DOPO la pubblicazione riuscita, ancora dentro lo stesso WriteGate: sia il
            // giornaliero (che lo confronta al giro successivo) sia il manuale (che lo aggiorna ma non lo legge
            // mai) mantengono così un'unica "ultima fotografia pubblicata" coerente con lo zip appena scritto.
            WriteFingerprintAtomic(backupDir, precomputedFingerprint ?? ComputeFingerprint(paths, after));
            ApplicaRetention(backupDir, now);
            return final;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Impronta del contenuto dello snapshot: SHA-256 della concatenazione ordinata (per percorso
    /// relativo, come già ordina <see cref="CaptureSourceSnapshot"/>) di "relpath|length|sha256" per ogni file
    /// catturato. Deliberatamente SENZA mtime: OneDrive/SharePoint può toccare le date dei file sincronizzati
    /// senza cambiarne il contenuto, e un fingerprint sensibile a mtime farebbe scattare un giornaliero inutile
    /// ad ogni risincronizzazione.</summary>
    private static string ComputeFingerprint(DataPaths paths, IReadOnlyList<BackupSourceStamp> stamps)
    {
        var sb = new StringBuilder();
        foreach (var stamp in stamps)
        {
            sb.Append(Path.GetRelativePath(paths.Root, stamp.Path).Replace('\\', '/'))
              .Append('|').Append(stamp.Length).Append('|').Append(stamp.Sha256).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Scrittura atomica (temp + rename) del fingerprint accanto agli zip, come per lo zip stesso:
    /// un crash a metà scrittura non deve mai lasciare un fingerprint troncato che poi confonderebbe il
    /// confronto del giornaliero successivo.</summary>
    private static void WriteFingerprintAtomic(string backupDir, string fingerprint)
    {
        var final = Path.Combine(backupDir, FingerprintFileName);
        var temp = final + "." + DeviceSuffix() + ".tmp";
        try
        {
            File.WriteAllText(temp, fingerprint);
            File.Move(temp, final, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static string DeviceSuffix()
    {
        // Difensivo: se l'identità device non è leggibile per qualunque motivo, il backup non deve fallire per
        // questo soltanto — il tmp risultante viene comunque ripulito dal fallback "più vecchio di 24h" in
        // PulisciTemporanei, semplicemente senza la pulizia anticipata riservata al proprio dispositivo.
        try { return DeviceIdentityService.GetOrCreate(); }
        catch { return "unknown-device"; }
    }

    private static void EnsureSnapshotCanStart(DataPaths paths, string operation)
    {
        if (WorkflowTransactionService.IsActive)
            throw new IOException($"{operation} bloccato: è in corso una Unit of Work TestLoom locale.");
        WorkspaceWritePolicy.EnsureCanWrite(paths, operation);
    }

    private static List<BackupSourceStamp> CaptureSourceSnapshot(DataPaths paths, CancellationToken ct)
    {
        var root = Path.GetFullPath(paths.Root);
        var backupRoot = Path.GetFullPath(paths.BackupDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        var attachmentsRoot = Path.GetFullPath(paths.AllegatiRootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
        var technicalRoot = Path.GetFullPath(Path.Combine(paths.Root, "_ditm")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var result = new List<BackupSourceStamp>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void AddVerifiedSource(string file)
        {
            var full = Path.GetFullPath(file);
            if (!seen.Add(full)) return;

            // Length+mtime non bastano con una replica sincronizzata: il fingerprint di backup si lega ai byte
            // effettivi e la seconda fotografia rileva anche sostituzioni cross-replica same-stamp.
            var info = new FileInfo(full);
            var sha256 = ComputeSha256Blocked(full, ct);

            info.Refresh();
            result.Add(new BackupSourceStamp(full, info.Length, info.LastWriteTimeUtc.Ticks, sha256));
        }

        // Enumerare solo documenti operativi: non attraversare neppure l'albero
        // degli allegati Files On-Demand. Phase D resta gestito separatamente.
        foreach (var file in WorkspaceOperationalFileEnumerator.EnumerateManagedJson(root, ct))
            AddVerifiedSource(file);

        // Dopo il cutover i master autorevoli vivono sotto _ditm/master-v2 e non sono soltanto JSON: current e
        // segmenti sono NDJSON, gli shard sono gzip. Non includiamo altri namespace tecnici _ditm.
        var phaseDRoot = PhaseDMasterJournalService.PhaseDRoot(root);
        if (Directory.Exists(phaseDRoot))
        {
            if (!PhaseDMasterBootstrapService.IsReady(root))
                throw new IOException("Backup bloccato: storage master Phase D presente ma bootstrap non completato/ready.");

            foreach (var file in Directory.EnumerateFiles(phaseDRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (TestLoomStorageOwnership.IsPhaseDAuthoritativePath(root, file))
                    AddVerifiedSource(file);
            }
        }

        return result.OrderBy(x => x.Path, PathComparer).ToList();
    }

    /// <summary>Hash SHA-256 a blocchi (mai <c>SHA256.HashData(stream)</c> sull'intero file in un colpo solo):
    /// su un file grande quella singola chiamata non è interrompibile, e un thread di background bloccato lì
    /// dentro non risponderebbe alla cancellazione entro il tempo limitato che la chiusura dell'app può
    /// permettersi di attendere (revisione indipendente su fc7e51b).</summary>
    private static string ComputeSha256Blocked(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            var perBlock = TestPerBlockHook;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                hash.AppendData(buffer, 0, read);
                if (perBlock != null) perBlock(ct).GetAwaiter().GetResult();
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string CopyToZipEntryAndHash(string sourcePath, ZipArchiveEntry entry, CancellationToken ct)
    {
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        using var output = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            var perBlock = TestCopyBlockHook;
            while (true)
            {
                // Controllato ad ogni blocco (128 KB) e non solo per-file: un singolo file grande non deve
                // impedire un annullamento rapido (criterio d'accettazione 4, chiusura/cambio cartella durante
                // il backup giornaliero).
                ct.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                if (perBlock != null) perBlock(ct).GetAwaiter().GetResult();
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool SameSnapshot(IReadOnlyList<BackupSourceStamp> a, IReadOnlyList<BackupSourceStamp> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!PathComparer.Equals(a[i].Path, b[i].Path) || a[i].Length != b[i].Length ||
                a[i].LastWriteUtcTicks != b[i].LastWriteUtcTicks ||
                !a[i].Sha256.Equals(b[i].Sha256, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// Retention 6.4.2 (decisione utente): conserva solo gli ULTIMI <see cref="MaxSnapshotsLocali"/> snapshot
    /// in questa cartella LOCALE, per data desunta dal nome file. La vecchia gradazione 14 giornalieri/8
    /// settimanali/12 mensili era pensata per l'unica cronologia disponibile su una cartella condivisa senza
    /// versioning proprio; qui OneDrive/SharePoint copre già la cronologia a lungo termine dei JSON e la
    /// cartella locale serve solo per un ripristino rapido dei giorni più recenti. Il numero restituito
    /// riguarda esclusivamente i temporary TestLoom rimossi (telemetria di manutenzione).
    /// </summary>
    internal static int ApplicaRetention(string backupDir, DateTime now)
    {
        if (!Directory.Exists(backupDir)) return 0;
        var tempRemoved = PulisciTemporanei(backupDir, now);

        var files = Directory.EnumerateFiles(backupDir, "TestLoom_snapshot_*.zip")
            .Select(f => new FileInfo(f))
            .OrderByDescending(DataSnapshot)
            .ToList();

        foreach (var f in files.Skip(MaxSnapshotsLocali))
        {
            try { f.Delete(); } catch { }
        }
        return tempRemoved;
    }

    internal static int PulisciTemporanei(string backupDir, DateTime now)
    {
        if (!Directory.Exists(backupDir)) return 0;
        var count = 0;
        // Suffisso ".{DeviceId}.tmp" scritto da Crea(): un .tmp che lo porta è SEMPRE nostro (nessun altro PC
        // può generare lo stesso DeviceId), quindi qui non può mai essere una scrittura di un ALTRO processo su
        // un ALTRO PC sulla stessa cartella condivisa. Può però essere una scrittura ANCORA in corso di un
        // secondo processo TestLoom su QUESTO PC (WriteGate è per-processo, non per-macchina): la grazia protegge
        // quel caso, dato che ogni entry scritta nello ZIP aggiorna LastWriteTime.
        var miaFirma = "." + DeviceSuffix() + ".tmp";
        // Un backup di QUESTO processo in corso tiene il WriteGate: il suo .tmp può restare senza scritture per più
        // della grazia mentre rilegge e riverifica tutti i file prima del rename (cartelle grandi). In quel caso
        // la pulizia anticipata dei propri .tmp è sospesa; resta solo la retention generale (TempRetention).
        var backupInCorsoInQuestoProcesso = WriteGate.CurrentCount == 0;
        foreach (var path in Directory.EnumerateFiles(backupDir, "TestLoom_snapshot_*.tmp", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(path);
                var eta = now - info.LastWriteTime;
                var nostroECertamenteOrfano = !backupInCorsoInQuestoProcesso &&
                                              path.EndsWith(miaFirma, StringComparison.OrdinalIgnoreCase) && eta > OwnDeviceOrphanGrace;
                if (!nostroECertamenteOrfano && eta <= TempRetention) continue;
                info.Delete();
                count++;
            }
            catch { }
        }
        return count;
    }

    private static DateTime DataSnapshot(FileInfo file)
    {
        var name = Path.GetFileNameWithoutExtension(file.Name);
        var raw = name.StartsWith("TestLoom_snapshot_", StringComparison.OrdinalIgnoreCase)
            ? name["TestLoom_snapshot_".Length..]
            : "";
        foreach (var format in new[] { "yyyyMMdd_HHmmss", "yyyyMMdd" })
            if (DateTime.TryParseExact(raw, format, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed)) return parsed;
        return file.LastWriteTime;
    }

    private sealed record BackupSourceStamp(string Path, long Length, long LastWriteUtcTicks, string Sha256);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
