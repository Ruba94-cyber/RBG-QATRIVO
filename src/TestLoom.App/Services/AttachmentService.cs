using System.Security.Cryptography;
using System.Buffers;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Salva/elenca/elimina gli allegati fuori dai JSON. I nuovi binari vivono nel bucket tecnico del
/// Test/CR, indipendente da componente/ID leggibile. Le rimozioni sono recoverable: prima di togliere il file dal
/// percorso attivo ne viene scritta e flushata una copia nella recovery area del bucket. In questo modo una
/// cancellazione UI seguita da Annulla, crash o Save fallito non distrugge mai l'evidenza.</summary>
public static class AttachmentService
{
    private const string RecoveryDirectoryName = ".testloom-recovery";

    public static readonly string[] EstensioniImmagine = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
    public static readonly string[] EstensioniConsentite =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".pdf", ".txt", ".csv", ".zip",
        ".log", ".trace", ".asc", ".blf", ".mf4", ".mdf", ".pcap", ".pcapng", ".xml", ".json",
        ".dbc", ".ldf", ".a2l", ".arxml", ".dbf", ".cdd", ".odx", ".odx-d", ".sym", ".fibex", ".msg",
    };

    public const long SogliaAvvisoByte = 20 * 1024 * 1024;

    public static bool EstensioneConsentita(string nomeFile) =>
        EstensioniConsentite.Contains(Path.GetExtension(nomeFile).ToLowerInvariant());

    public static bool EImmagine(string nomeFile) =>
        EstensioniImmagine.Contains(Path.GetExtension(nomeFile).ToLowerInvariant());

    public static Allegato Salva(DataPaths paths, TestCase tc, string percorsoSorgente, string operatore)
        => SalvaAsync(paths, tc, percorsoSorgente, operatore).ConfigureAwait(false).GetAwaiter().GetResult();

    public static Task<Allegato> SalvaAsync(DataPaths paths, TestCase tc, string percorsoSorgente, string operatore,
        CancellationToken cancellationToken = default)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Aggiunta allegato Test");
        return SalvaCoreAsync(paths.AllegatiDir(tc), tc.Allegati, percorsoSorgente, operatore, cancellationToken);
    }

    public static Task<Allegato> SalvaAsync(DataPaths paths, ChangeRequest cr, string percorsoSorgente, string operatore,
        CancellationToken cancellationToken = default)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Aggiunta allegato Change Request");
        return SalvaCoreAsync(paths.AllegatiDirChangeRequest(cr), cr.Allegati, percorsoSorgente, operatore, cancellationToken);
    }

    public static Task<Allegato> SalvaAsync(DataPaths paths, NonConformita nc, string percorsoSorgente, string operatore,
        CancellationToken cancellationToken = default)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Aggiunta allegato Non Conformità");
        return SalvaCoreAsync(paths.AllegatiDirNonConformita(nc), nc.Allegati, percorsoSorgente, operatore, cancellationToken);
    }

    /// <summary>Copia e calcola SHA-256 in un solo passaggio asincrono. Il file viene creato in modo esclusivo:
    /// due upload concorrenti non possono sovrascrivere lo stesso nome.</summary>
    private static async Task<Allegato> SalvaCoreAsync(string dir, List<Allegato> allegatiEsistenti,
        string percorsoSorgente, string operatore, CancellationToken cancellationToken)
    {
        var nomeFile = Path.GetFileName(percorsoSorgente);
        if (!EstensioneConsentita(nomeFile))
            throw new InvalidOperationException("Formato non supportato. Usa immagini, PDF, messaggi Outlook (.msg), testo/CSV, archivi ZIP o tracciati diagnostici.");

        Directory.CreateDirectory(dir);

        var destinazione = Path.Combine(dir, nomeFile);
        FileStream? output = null;
        for (var i = 0; output == null; i++)
        {
            destinazione = Path.Combine(dir, i == 0 ? nomeFile : $"{Path.GetFileNameWithoutExtension(nomeFile)}_{i}{Path.GetExtension(nomeFile)}");
            try
            {
                output = new FileStream(destinazione, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 1024 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            }
            catch (IOException) when (File.Exists(destinazione)) { }
        }

        var completato = false;
        try
        {
            using (output)
            await using (var input = new FileStream(percorsoSorgente, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 1024 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                try
                {
                    int letti;
                    while ((letti = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hasher.AppendData(buffer, 0, letti);
                        await output.WriteAsync(buffer.AsMemory(0, letti), cancellationToken).ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                    var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    var duplicato = allegatiEsistenti.FirstOrDefault(a => !a.Archiviata &&
                        !string.IsNullOrWhiteSpace(a.Sha256) && a.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
                    if (duplicato != null)
                        throw new InvalidOperationException($"Il file è già allegato come \"{duplicato.NomeFile}\" (contenuto identico).");

                    completato = true;
                    await Task.Run(() => AllegatiSyncService.ProvaSegnaSoloOnline(destinazione), cancellationToken).ConfigureAwait(false);
                    return new Allegato
                    {
                        NomeFile = Path.GetFileName(destinazione),
                        DataCaricamento = DateTime.Now,
                        CaricatoDa = operatore,
                        DimensioneByte = new FileInfo(destinazione).Length,
                        Sha256 = hash
                    };
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        finally
        {
            output?.Dispose();
            if (!completato)
            {
                try { if (File.Exists(destinazione)) File.Delete(destinazione); }
                catch { /* non mascherare l'errore originale */ }
            }
        }
    }

    /// <summary>
    /// Risolve prima il bucket attivo, poi i layout legacy e infine la recovery area. Il fallback recovery è
    /// fondamentale: se l'utente rimuove un allegato e poi annulla/il Save fallisce, il JSON autorevole continua
    /// a referenziarlo e l'evidenza deve restare apribile anche se era già stata tolta dal percorso attivo.
    /// </summary>
    public static string PercorsoCompleto(DataPaths paths, TestCase tc, Allegato allegato)
    {
        var nuovo = Path.Combine(paths.AllegatiDir(tc), allegato.NomeFile);
        if (File.Exists(nuovo)) return nuovo;

        var legacyCorrente = Path.Combine(paths.AllegatiDirLegacyCurrent(tc), allegato.NomeFile);
        if (File.Exists(legacyCorrente)) return legacyCorrente;

        foreach (var componente in tc.Componente)
        {
            var moltoLegacy = Path.Combine(paths.AllegatiDirLegacy(componente, tc.Id), allegato.NomeFile);
            if (File.Exists(moltoLegacy)) return moltoLegacy;
        }

        var recovery = RecoveryPath(paths.AllegatiDir(tc), allegato);
        return File.Exists(recovery) ? recovery : nuovo;
    }

    public static string PercorsoCompleto(DataPaths paths, ChangeRequest cr, Allegato allegato)
    {
        var nuovo = Path.Combine(paths.AllegatiDirChangeRequest(cr), allegato.NomeFile);
        if (File.Exists(nuovo)) return nuovo;
        var legacy = Path.Combine(paths.AllegatiDirChangeRequestLegacy(cr.Id), allegato.NomeFile);
        if (File.Exists(legacy)) return legacy;
        var recovery = RecoveryPath(paths.AllegatiDirChangeRequest(cr), allegato);
        return File.Exists(recovery) ? recovery : nuovo;
    }

    public static string PercorsoCompleto(DataPaths paths, NonConformita nc, Allegato allegato)
    {
        var nuovo = Path.Combine(paths.AllegatiDirNonConformita(nc), allegato.NomeFile);
        if (File.Exists(nuovo)) return nuovo;
        var recovery = RecoveryPath(paths.AllegatiDirNonConformita(nc), allegato);
        return File.Exists(recovery) ? recovery : nuovo;
    }

    /// <summary>
    /// Rimozione recoverable. Solo dopo avere scritto e flushato una seconda copia viene tentata la delete del
    /// percorso attivo. Se OneDrive rifiuta la delete restano due copie, che è intenzionalmente il failure mode
    /// sicuro: spazio in più è riparabile, un'evidenza persa no.
    /// </summary>
    public static void Elimina(DataPaths paths, TestCase tc, Allegato allegato)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Rimozione allegato Test");
        EliminaRecoverable(PercorsoCompleto(paths, tc, allegato), RecoveryPath(paths.AllegatiDir(tc), allegato));
    }

    public static void Elimina(DataPaths paths, ChangeRequest cr, Allegato allegato)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Rimozione allegato Change Request");
        EliminaRecoverable(PercorsoCompleto(paths, cr, allegato), RecoveryPath(paths.AllegatiDirChangeRequest(cr), allegato));
    }

    public static void Elimina(DataPaths paths, NonConformita nc, Allegato allegato)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Rimozione allegato Non Conformità");
        EliminaRecoverable(PercorsoCompleto(paths, nc, allegato), RecoveryPath(paths.AllegatiDirNonConformita(nc), allegato));
    }

    private static string RecoveryPath(string activeDir, Allegato allegato)
    {
        var ext = Path.GetExtension(allegato.NomeFile);
        return Path.Combine(activeDir, RecoveryDirectoryName, allegato.Uid.ToString("N") + ext);
    }

    private static void EliminaRecoverable(string percorsoAttivo, string recovery)
    {
        if (!File.Exists(percorsoAttivo)) return;
        if (Path.GetFullPath(percorsoAttivo).Equals(Path.GetFullPath(recovery), StringComparison.OrdinalIgnoreCase)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
        var tmp = recovery + ".tmp" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var input = new FileStream(percorsoAttivo, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                input.CopyTo(output, 1024 * 1024);
                output.Flush(flushToDisk: true);
            }
            File.Move(tmp, recovery, overwrite: true);
            try { File.Delete(percorsoAttivo); }
            catch (Exception ex) { CrashLogService.Registra("Allegato: delete percorso attivo rinviata", ex); }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { }
        }
    }

    public static string CalcolaSha256(string percorso)
    {
        using var stream = File.OpenRead(percorso);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
