using System.Text.Json.Serialization;

namespace TestLoom.App.Services;

/// <summary>Default condiviso (non per-riga come i master data classici, un solo record) delle sezioni
/// preselezionate nel pacchetto Ticketing (Addendum §4, feedback QA): un Master lo imposta con i campi minimi
/// necessari, ogni operatore può comunque aggiungerne altri per un singolo pacchetto senza cambiare il
/// default per tutti.</summary>
public class PolarionExportConfig
{
    /// <summary>
    /// Revisione ottimistica del singleton condiviso. I file legacy senza questa proprietà deserializzano a 0:
    /// il primo salvataggio sicuro li migra a 1 senza richiedere conversioni preventive.
    /// </summary>
    public long RevisioneDati { get; set; }
    public List<string> SezioniDiDefault { get; set; } = PolarionSezioni.Minime.ToList();

    /// <summary>Solo runtime: permette alla UI di restare consultabile anche se il JSON condiviso è corrotto.
    /// Non viene mai serializzato e Save ricontrolla comunque il file autorevole prima di sovrascriverlo.</summary>
    [JsonIgnore]
    public string? ErroreCaricamento { get; set; }

    /// <summary>
    /// Fingerprint semantico della fotografia realmente caricata. Affianca RevisioneDati per intercettare
    /// anche una modifica prodotta da una build legacy che non incrementa ancora la revisione.
    /// </summary>
    [JsonIgnore]
    public string? SourceFingerprint { get; set; }
}

public static class PolarionConfigService
{
    public static PolarionExportConfig Load(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = paths.PolarionConfigFile;
        if (!File.Exists(path)) return new PolarionExportConfig();

        var config = JsonIo.TryRead<PolarionExportConfig>(path, out var error);
        if (config != null)
        {
            config.SourceFingerprint = EntityStateFingerprint.Compute(config);
            return config;
        }

        // La pagina Impostazioni deve restare apribile: mostriamo i default soltanto come fallback runtime,
        // ma marchiamo il risultato come degradato. Il Save sottostante NON potrà sovrascrivere il file finché
        // l'originale esistente resta illeggibile, quindi questo fallback non può trasformarsi in perdita dati.
        var message = $"Configurazione Ticketing condivisa non leggibile: {Path.GetFileName(path)}. " +
                      "I valori mostrati sono solo di fallback; il salvataggio resta bloccato finché il file non viene riparato.";
        CrashLogService.Registra(message, error == null ? null : new InvalidDataException(error));
        return new PolarionExportConfig { ErroreCaricamento = message };
    }

    public static void Save(DataPaths paths, PolarionExportConfig config)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(config);
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Salvataggio configurazione Ticketing");

        var path = paths.PolarionConfigFile;
        var existedAtStart = File.Exists(path);
        if (existedAtStart)
        {
            // Un file ESISTENTE ma illeggibile non equivale a "nessuna configurazione". Anche se la UI sta
            // mostrando fallback runtime, non deve mai poterli scrivere sopra al contenuto originale.
            var current = JsonIo.TryRead<PolarionExportConfig>(path, out var error);
            if (current == null)
                throw new InvalidDataException(
                    "Salvataggio configurazione Ticketing bloccato: il file condiviso esistente non è leggibile e deve essere riparato prima di poter essere sostituito.",
                    error == null ? null : new InvalidDataException(error));

            if (string.IsNullOrWhiteSpace(config.SourceFingerprint))
                throw new IOException("Salvataggio configurazione Ticketing bloccato: fotografia iniziale non disponibile. Ricarica Impostazioni e riprova.");

            var currentFingerprint = EntityStateFingerprint.Compute(current);
            if (current.RevisioneDati != config.RevisioneDati ||
                !currentFingerprint.Equals(config.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new ConflittoRevisioneException("ticketing-config", config.RevisioneDati, current.RevisioneDati,
                    current.RevisioneDati == config.RevisioneDati);
        }
        else if (!string.IsNullOrWhiteSpace(config.SourceFingerprint))
        {
            // La pagina era stata aperta su un file esistente che nel frattempo è scomparso. Non ricrearlo da
            // una fotografia ormai obsoleta: una delete/sync concorrente deve restare visibile come conflitto.
            throw new IOException("La configurazione Ticketing è cambiata o è stata rimossa dopo il caricamento. Ricarica Impostazioni prima di salvare.");
        }

        var oldRevision = config.RevisioneDati;
        var oldFingerprint = config.SourceFingerprint;
        config.RevisioneDati = Math.Max(0, oldRevision) + 1;
        try
        {
            // JsonIo fotografa l'esistenza prima della serializzazione: se il file non esisteva e un'altra
            // replica/processo lo crea durante questa write, il rename finale resta create-only e fallisce.
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
