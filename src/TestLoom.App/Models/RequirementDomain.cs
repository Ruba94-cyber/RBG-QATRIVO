using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestLoom.App.Models;

/// <summary>
/// Tipo di requisito TestLoom. Standard e' cross-veicolo; Extra e' applicabile esclusivamente ai
/// sottoveicoli esplicitamente dichiarati sul requisito.
/// </summary>
public enum TipoRequisito
{
    Standard,
    Extra
}

/// <summary>
/// Modello esteso disponibile per import/export o evoluzioni future. Il resto dell'app puo continuare a
/// leggere Requisito: grazie a MasterEntity.JsonExtensionData i campi Extra restano preservati anche dai reader
/// legacy. I requisiti JSON esistenti, privi di Tipo, sono Standard per default.
/// </summary>
public class RequisitoConfigurato : Requisito
{
    public TipoRequisito Tipo { get; set; } = TipoRequisito.Standard;
    public List<string> SottoveicoliD4D { get; set; } = new();

    [JsonIgnore]
    public bool ED4D => Tipo == TipoRequisito.Extra;

    [JsonIgnore]
    public string SottoveicoliD4DTesto
    {
        get => string.Join(", ", SottoveicoliD4D);
        set => SottoveicoliD4D = NormalizzaLista((value ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<string> NormalizzaLista(IEnumerable<string> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Punto unico per interpretare e modificare Tipo/Sottoveicoli sia da RequisitoConfigurato sia da un
/// Requisito base deserializzato da MasterDataStore. In questo modo requisiti.json resta un array compatibile:
/// nessun wrapper, sidecar o nuovo file condiviso e nessuna migrazione distruttiva.
/// </summary>
public static class RequisitoApplicabilita
{
    private const string TipoKey = nameof(RequisitoConfigurato.Tipo);
    private const string SottoveicoliKey = nameof(RequisitoConfigurato.SottoveicoliD4D);

    public static TipoRequisito TipoDi(Requisito requisito)
    {
        if (requisito is RequisitoConfigurato configurato) return configurato.Tipo;
        if (requisito.ExtensionData == null || !requisito.ExtensionData.TryGetValue(TipoKey, out var value))
            return TipoRequisito.Standard;

        if (value.ValueKind == JsonValueKind.String && Enum.TryParse<TipoRequisito>(value.GetString(), true, out var parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric) && Enum.IsDefined(typeof(TipoRequisito), numeric))
            return (TipoRequisito)numeric;
        return TipoRequisito.Standard;
    }

    public static IReadOnlyList<string> SottoveicoliDi(Requisito requisito)
    {
        if (requisito is RequisitoConfigurato configurato)
            return Normalizza(configurato.SottoveicoliD4D);
        if (requisito.ExtensionData == null || !requisito.ExtensionData.TryGetValue(SottoveicoliKey, out var value))
            return Array.Empty<string>();

        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                    result.Add(s.Trim());
            return Normalizza(result);
        }
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } legacy)
            return Normalizza(legacy.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return Array.Empty<string>();
    }

    /// <summary>Scrive sul modello base senza richiedere un cambio del tipo generico di MasterDataStore.</summary>
    public static void ImpostaTipo(Requisito requisito, TipoRequisito tipo)
    {
        if (requisito is RequisitoConfigurato configurato)
        {
            configurato.Tipo = tipo;
            return;
        }
        requisito.ExtensionData ??= new Dictionary<string, JsonElement>();
        requisito.ExtensionData[TipoKey] = JsonSerializer.SerializeToElement(tipo.ToString());
    }

    public static void ImpostaSottoveicoli(Requisito requisito, IEnumerable<string> sottoveicoli)
    {
        var normalized = Normalizza(sottoveicoli);
        if (requisito is RequisitoConfigurato configurato)
        {
            configurato.SottoveicoliD4D = normalized.ToList();
            return;
        }
        requisito.ExtensionData ??= new Dictionary<string, JsonElement>();
        requisito.ExtensionData[SottoveicoliKey] = JsonSerializer.SerializeToElement(normalized);
    }

    public static bool RiferisceEcu(Requisito requisito, string componente) =>
        requisito.VeicoliEcuD4D?.Any(a => string.Equals(a.Componente, componente, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Un requisito Extra con coppie veicolo-componente dichiarate riguarda solo quelle componente; senza coppie (e Standard) vale per ogni componente.
    /// Con <paramref name="veicolo"/>, se il requisito ha coppie per quel veicolo contano solo quelle; altrimenti l'unione dei componenti
    /// di tutte le coppie. Solo lettura/visualizzazione: i dati persistiti non cambiano.
    /// </summary>
    public static bool SiApplicaAllaEcu(Requisito requisito, string componente, string? veicolo = null)
    {
        if (!ED4D(requisito) || requisito.VeicoliEcuD4D is not { Count: > 0 } coppie) return true;
        if (veicolo != null)
        {
            var delVeicolo = coppie.Where(c => string.Equals(c.Veicolo, veicolo, StringComparison.OrdinalIgnoreCase)).ToList();
            if (delVeicolo.Count > 0)
                return delVeicolo.Any(c => string.Equals(c.Componente, componente, StringComparison.OrdinalIgnoreCase));
        }
        return coppie.Any(c => string.Equals(c.Componente, componente, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Requisiti non archiviati ristretti ad alcune componente: id requisito -> componente a cui si applica (insieme delle coppie Extra).</summary>
    public static Dictionary<string, HashSet<string>> EcuRistrettePerRequisito(IEnumerable<Requisito> requisiti)
    {
        var mappa = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in requisiti)
        {
            if (r.Archiviata || !ED4D(r) || r.VeicoliEcuD4D is not { Count: > 0 } coppie) continue;
            var componente = coppie.Select(c => c.Componente).Where(e => !string.IsNullOrWhiteSpace(e))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (componente.Count > 0) mappa[r.Id] = componente;
        }
        return mappa;
    }

    public static bool RiferisceVeicolo(Requisito requisito, string veicolo) =>
        SottoveicoliDi(requisito).Contains(veicolo, StringComparer.OrdinalIgnoreCase) ||
        requisito.VeicoliEcuD4D?.Any(a => string.Equals(a.Veicolo, veicolo, StringComparison.OrdinalIgnoreCase)) == true;

    public static IReadOnlyList<string> VeicoliEcuEsclusi(Requisito requisito, IEnumerable<string> selezionati)
    {
        var ammessi = selezionati.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (requisito.VeicoliEcuD4D ?? new()).Select(a => a.Veicolo)
            .Where(v => !ammessi.Contains(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool ED4D(Requisito requisito) => TipoDi(requisito) == TipoRequisito.Extra;

    /// <summary>Standard vale per qualunque sottoveicolo; Extra soltanto per quelli dichiarati.</summary>
    public static bool SiApplicaAlSottoveicolo(Requisito requisito, string sottoveicolo)
    {
        if (!ED4D(requisito)) return true;
        var target = SottoveicoliDi(requisito);
        // 6.5.0: i sottoveicoli sono facoltativi. Extra senza sottoveicoli = applicabile a tutti.
        return target.Count == 0 || target.Contains(sottoveicolo, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Un Test soddisfa il perimetro di un Extra se copre almeno uno dei sottoveicoli del requisito.
    /// Standard resta cross e non viene filtrato per veicolo.
    /// </summary>
    public static bool SiApplicaAlTest(Requisito requisito, TestCase testCase)
    {
        if (!ED4D(requisito)) return true;
        var target = SottoveicoliDi(requisito);
        if (target.Count == 0) return true;
        return testCase.VeicoliEffettivi.Any(v => target.Contains(v, StringComparer.OrdinalIgnoreCase));
    }

    public static string DescrizioneAmbito(Requisito requisito)
    {
        if (!ED4D(requisito)) return "Standard · cross-veicolo";
        var targets = SottoveicoliDi(requisito);
        return targets.Count == 0 ? "Extra · tutti i veicoli" : $"Extra · {string.Join(", ", targets)}";
    }

    private static IReadOnlyList<string> Normalizza(IEnumerable<string> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
