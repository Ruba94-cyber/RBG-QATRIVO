using System.Text.Json.Serialization;

namespace TestLoom.App.Services;

/// <summary>Modalità di accesso: impostazione UNICA per l'intera cartella dati condivisa (piano 6.4.2, §4b,
/// decisione utente 2026-09-28) — "Accesso con PIN" (default, storico) oppure "Accesso senza PIN" (selezione
/// operatore + Login, nessun PIN richiesto). Vale per tutti i ruoli, Master compresi. Stesso schema di
/// <see cref="PolarionExportConfig"/>/<see cref="PolarionConfigService"/> (un solo record condiviso, revisione
/// ottimistica): scelto invece di un campo dentro un file master esistente perché non è un master-data a righe
/// (nessun editor a griglia, nessun Changelog per riga) né un file di compatibilità (version-manifest.json,
/// che descrive lo schema del client, non una preferenza dell'utente) — un file dedicato, piccolo, con lo
/// stesso pattern già consolidato in questo codebase per un singleton condiviso.</summary>
public class AccessModeConfig
{
    public long RevisioneDati { get; set; }

    /// <summary>true = "Accesso senza PIN". Leggere sempre tramite RuoloSigilloService.SenzaPinEffettivo:
    /// questo campo grezzo NON tiene conto del sigillo (un file manomesso a mano deve ricadere su "con PIN").</summary>
    public bool SenzaPin { get; set; }

    /// <summary>Sigillo HMAC (RuoloSigilloService.SigillaModalitaAccesso), stessa protezione di
    /// Operatore.SigilloRuolo: un file modificato fuori dall'app (SenzaPin cambiato senza ricalcolare questo
    /// campo) fa ricadere l'impostazione su "con PIN", mai il contrario.</summary>
    public string? Sigillo { get; set; }

    [JsonIgnore]
    public string? ErroreCaricamento { get; set; }

    [JsonIgnore]
    public string? SourceFingerprint { get; set; }
}

public static class AccessModeConfigService
{
    public static AccessModeConfig Load(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = paths.AccessoConfigFile;
        if (!File.Exists(path)) return new AccessModeConfig();

        var config = JsonIo.TryRead<AccessModeConfig>(path, out var error);
        if (config != null)
        {
            config.SourceFingerprint = EntityStateFingerprint.Compute(config);
            return config;
        }

        var message = $"Configurazione modalità di accesso non leggibile: {Path.GetFileName(path)}. " +
                      "I valori mostrati sono solo di fallback (con PIN); il salvataggio resta bloccato finché il file non viene riparato.";
        CrashLogService.Registra(message, error == null ? null : new InvalidDataException(error));
        return new AccessModeConfig { ErroreCaricamento = message };
    }

    /// <summary>Salva la modalità di accesso. <paramref name="operatoreEMasterEffettivo"/> è la difesa in
    /// profondità voluta dalla revisione del piano 6.4.2, §4b, punto 4: ImpostazioniViewModel.SalvaModalitaAccesso
    /// già blocca il comando se l'operatore non è Master, ma quel controllo vive nella UI — un futuro chiamante
    /// (un altro ViewModel, uno script, un bug che rimuove il gate) potrebbe richiamare questo metodo senza
    /// ripetere il controllo. Il parametro è OBBLIGATORIO (nessun default) apposta: costringe ogni chiamante
    /// presente e futuro a passare esplicitamente lo stato calcolato con RuoloEffettivo (mai col Ruolo grezzo),
    /// non un "true" comodo per bypassare la verifica.</summary>
    public static void Save(DataPaths paths, AccessModeConfig config, bool operatoreEMasterEffettivo)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(config);
        if (!operatoreEMasterEffettivo)
            throw new UnauthorizedAccessException(
                "Salvataggio modalità di accesso bloccato: solo un Master con ruolo effettivamente sigillato può cambiarla.");
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Salvataggio modalità di accesso");

        var path = paths.AccessoConfigFile;
        var existedAtStart = File.Exists(path);
        if (existedAtStart)
        {
            var current = JsonIo.TryRead<AccessModeConfig>(path, out var error);
            if (current == null)
                throw new InvalidDataException(
                    "Salvataggio modalità di accesso bloccato: il file condiviso esistente non è leggibile e deve essere riparato prima di poter essere sostituito.",
                    error == null ? null : new InvalidDataException(error));

            if (string.IsNullOrWhiteSpace(config.SourceFingerprint))
                throw new IOException("Salvataggio modalità di accesso bloccato: fotografia iniziale non disponibile. Ricarica Impostazioni e riprova.");

            var currentFingerprint = EntityStateFingerprint.Compute(current);
            if (current.RevisioneDati != config.RevisioneDati ||
                !currentFingerprint.Equals(config.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new ConflittoRevisioneException("accesso-config", config.RevisioneDati, current.RevisioneDati,
                    current.RevisioneDati == config.RevisioneDati);
        }
        else if (!string.IsNullOrWhiteSpace(config.SourceFingerprint))
        {
            throw new IOException("La configurazione della modalità di accesso è cambiata o è stata rimossa dopo il caricamento. Ricarica Impostazioni prima di salvare.");
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
