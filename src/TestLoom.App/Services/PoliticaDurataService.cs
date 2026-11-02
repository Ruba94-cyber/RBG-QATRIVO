using System.Text.Json.Serialization;

namespace TestLoom.App.Services;

/// <summary>Politica condivisa della durata operazioni (TIME, 6.6.1). Un solo record per l'intera cartella dati: se
/// <see cref="DurataObbligatoria"/> e' vero, il salvataggio di un'operazione eseguita e attiva senza durata e' bloccato
/// (vedi PoliticaDurataService.RichiedeDurata). Default: facoltativa. File assente = facoltativa. Non e' in AppConfig
/// (per PC) e non ha sigillo HMAC: non e' una protezione di accesso.
/// Limite noto: i client 6.6.0 ignorano questa politica. Prima di attivarla occorre aggiornare tutti i PC Master.
/// Lo stesso schema (revisione + fingerprint sorgente) di <see cref="AccessModeConfig"/>.</summary>
public class PoliticaDurataConfig
{
    public long RevisioneDati { get; set; }

    /// <summary>true = durata obbligatoria per le operazioni eseguite e attive. I valori storici mancanti non vengono toccati.</summary>
    public bool DurataObbligatoria { get; set; }

    [JsonIgnore]
    public string? ErroreCaricamento { get; set; }

    [JsonIgnore]
    public string? SourceFingerprint { get; set; }
}

public static class PoliticaDurataService
{
    /// <summary>Carica la politica. File assente = facoltativa (nessun errore). File illeggibile = fallback facoltativo con
    /// <see cref="PoliticaDurataConfig.ErroreCaricamento"/> valorizzato: il salvataggio resta bloccato finche' il file non e' riparato.</summary>
    public static PoliticaDurataConfig Load(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = paths.PoliticaDurataFile;
        if (!File.Exists(path)) return new PoliticaDurataConfig();

        var config = JsonIo.TryRead<PoliticaDurataConfig>(path, out var error);
        if (config != null)
        {
            config.SourceFingerprint = EntityStateFingerprint.Compute(config);
            return config;
        }

        var message = $"Politica della durata operazioni non leggibile: {Path.GetFileName(path)}. " +
                      "I valori mostrati sono solo di fallback (durata facoltativa); il salvataggio resta bloccato finche' il file non viene riparato.";
        CrashLogService.Registra(message, error == null ? null : new InvalidDataException(error));
        return new PoliticaDurataConfig { ErroreCaricamento = message };
    }

    /// <summary>Vero quando la durata va richiesta: politica obbligatoria e file leggibile (un file illegibile non blocca il lavoro: resta facoltativo).</summary>
    public static bool RichiedeDurata(PoliticaDurataConfig politica)
        => politica is { DurataObbligatoria: true, ErroreCaricamento: null };

    /// <summary>Salva la politica. <paramref name="operatoreEMasterEffettivo"/> e' obbligatorio (nessun default): il servizio
    /// rifiuta comunque chi non e' Master effettivo, anche se il gate della UI fosse stato rimosso. Rifiuta una revisione o
    /// una fotografia sorgente diverse (scrittura concorrente): non sovrascrive mai un valore piu' recente.</summary>
    public static void Save(DataPaths paths, PoliticaDurataConfig config, bool operatoreEMasterEffettivo)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(config);
        if (!operatoreEMasterEffettivo)
            throw new UnauthorizedAccessException(
                "Salvataggio della politica di durata bloccato: solo un Master effettivo puo' modificarla.");
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Salvataggio politica di durata");

        var path = paths.PoliticaDurataFile;
        // Blocco di commit condiviso, stesso schema di Change Request e Non Conformita: controllo di revisione e fotografia e
        // scrittura avvengono nella stessa sezione, cosi' due Master che salvano dalla stessa revisione non si sovrascrivono.
        using var bloccoCommit = DomainRecordCommitLock.Acquire("Politica durata", "politica-durata", path);
        if (File.Exists(path))
        {
            var current = JsonIo.TryRead<PoliticaDurataConfig>(path, out var error);
            if (current == null)
                throw new InvalidDataException(
                    "Salvataggio della politica di durata bloccato: il file condiviso esistente non e' leggibile e va riparato prima di poterlo sostituire.",
                    error == null ? null : new InvalidDataException(error));

            if (string.IsNullOrWhiteSpace(config.SourceFingerprint))
                throw new IOException("Salvataggio della politica di durata bloccato: fotografia iniziale non disponibile. Ricarica Impostazioni e riprova.");

            var currentFingerprint = EntityStateFingerprint.Compute(current);
            if (current.RevisioneDati != config.RevisioneDati ||
                !currentFingerprint.Equals(config.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new ConflittoRevisioneException("politica-durata", config.RevisioneDati, current.RevisioneDati,
                    current.RevisioneDati == config.RevisioneDati);
        }
        else if (!string.IsNullOrWhiteSpace(config.SourceFingerprint))
        {
            throw new IOException("La politica della durata operazioni e' stata rimossa dopo il caricamento. Ricarica Impostazioni prima di salvare.");
        }

        var oldRevision = config.RevisioneDati;
        var oldFingerprint = config.SourceFingerprint;
        config.RevisioneDati = Math.Max(0, oldRevision) + 1;
        try
        {
            JsonIo.WriteAtomic(path, config);
            config.SourceFingerprint = EntityStateFingerprint.Compute(config);
        }
        catch
        {
            config.RevisioneDati = oldRevision;
            config.SourceFingerprint = oldFingerprint;
            throw;
        }
    }
}
