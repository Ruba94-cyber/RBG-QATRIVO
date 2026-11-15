using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>Preferenze per operatore della matrice Audit (larghezze, ordine, componente nascoste, altezze di riga). La matrice non e' un
/// DataGrid (ItemsControl virtualizzata a righe fisse), quindi non passa da TableLayoutPreferences: usa lo stesso storage
/// (AppConfig tramite ViewStateService, chiave "Audit.Matrice.Layout|operatore") e lo stesso modello di menu colonne.
/// Il modello e' puro (nessuna dipendenza da Avalonia) per poter essere provato senza UI.
/// Le colonne sono identificate dal nome componente, non dall'indice: i nomi sconosciuti (componente non piu' presenti nel sottoveicolo
/// scelto) vengono ignorati senza essere persi, cosi' tornano validi se il componente riappare.</summary>
public sealed class PreferenzeMatriceAudit
{
    public const double LarghezzaRequisitoMinima = 120;
    public const double LarghezzaRequisitoMassima = 480;
    public const double LarghezzaEcuMinima = 64;
    public const double LarghezzaEcuMassima = 320;
    public const double AltezzaRigaMinima = 26;
    public const double AltezzaRigaMassima = 240;
    public const string Chiave = "Audit.Matrice.Layout";

    /// <summary>1 = larghezze/ordine/altezze; 2 = aggiunge EcuNascoste (un config v1 si legge con nessuna componente nascosta).</summary>
    public int Versione { get; set; } = 2;
    public double? LarghezzaRequisito { get; set; }
    public Dictionary<string, double> LarghezzeEcu { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> OrdineEcu { get; set; } = new();
    public Dictionary<string, double> AltezzeRighe { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> EcuNascoste { get; set; } = new();

    /// <summary>Legge le preferenze dal JSON salvato; testo vuoto, non valido o null danno le preferenze predefinite.
    /// I valori fuori limite vengono riportati nei limiti, quelli non finiti scartati.</summary>
    public static PreferenzeMatriceAudit Deserializza(string? testo)
    {
        PreferenzeMatriceAudit preferenze;
        if (string.IsNullOrWhiteSpace(testo)) return new PreferenzeMatriceAudit();
        try { preferenze = JsonSerializer.Deserialize<PreferenzeMatriceAudit>(testo) ?? new PreferenzeMatriceAudit(); }
        catch (JsonException) { return new PreferenzeMatriceAudit(); }
        preferenze.Normalizza();
        return preferenze;
    }

    public string Serializza()
    {
        Versione = 2;
        return JsonSerializer.Serialize(this);
    }

    private void Normalizza()
    {
        var larghezze = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (componente, larghezza) in LarghezzeEcu ?? new())
            if (!string.IsNullOrWhiteSpace(componente) && double.IsFinite(larghezza))
                larghezze[componente] = Math.Clamp(larghezza, LarghezzaEcuMinima, LarghezzaEcuMassima);
        LarghezzeEcu = larghezze;

        var altezze = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (requisitoId, altezza) in AltezzeRighe ?? new())
            if (!string.IsNullOrWhiteSpace(requisitoId) && double.IsFinite(altezza))
                altezze[requisitoId] = Math.Clamp(altezza, AltezzaRigaMinima, AltezzaRigaMassima);
        AltezzeRighe = altezze;

        OrdineEcu = (OrdineEcu ?? new()).Where(componente => !string.IsNullOrWhiteSpace(componente)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        EcuNascoste = (EcuNascoste ?? new()).Where(componente => !string.IsNullOrWhiteSpace(componente)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (LarghezzaRequisito is { } requisito)
            LarghezzaRequisito = double.IsFinite(requisito) ? Math.Clamp(requisito, LarghezzaRequisitoMinima, LarghezzaRequisitoMassima) : null;
        Versione = 2;
    }

    /// <summary>Tutti i componenti disponibili nell'ordine scelto dall'utente (nascoste incluse): prima quelle nell'ordine salvato, poi le
    /// nuove nell'ordine naturale. I nomi salvati non piu' presenti sono ignorati.</summary>
    public List<string> OrdineCompleto(IReadOnlyList<string> disponibili)
    {
        var ordine = new List<string>(disponibili.Count);
        foreach (var salvata in OrdineEcu)
        {
            var corrente = disponibili.FirstOrDefault(componente => componente.Equals(salvata, StringComparison.OrdinalIgnoreCase));
            if (corrente != null && !ordine.Contains(corrente, StringComparer.OrdinalIgnoreCase)) ordine.Add(corrente);
        }
        foreach (var componente in disponibili)
            if (!ordine.Contains(componente, StringComparer.OrdinalIgnoreCase)) ordine.Add(componente);
        return ordine;
    }

    public bool EcuNascosta(string componente) => EcuNascoste.Any(n => n.Equals(componente, StringComparison.OrdinalIgnoreCase));

    /// <summary>componente da mostrare, in ordine. Una matrice senza alcuna colonna non e' recuperabile dall'utente: se la preferenza
    /// nasconderebbe tutti i componenti disponibili (es. sottoveicolo diverso da quello in cui e' stata salvata) vengono mostrate tutte.</summary>
    public List<string> OrdineVisibile(IReadOnlyList<string> disponibili)
    {
        var completo = OrdineCompleto(disponibili);
        var visibili = completo.Where(componente => !EcuNascosta(componente)).ToList();
        return visibili.Count == 0 ? completo : visibili;
    }

    /// <summary>Mostra o nasconde un componente. Rifiuta (false) di nascondere l'ultima colonna visibile.</summary>
    public bool ImpostaVisibile(string componente, bool visibile, IReadOnlyList<string> disponibili)
    {
        if (string.IsNullOrWhiteSpace(componente)) return false;
        if (visibile)
        {
            EcuNascoste.RemoveAll(n => n.Equals(componente, StringComparison.OrdinalIgnoreCase));
            return true;
        }
        if (EcuNascosta(componente)) return true;
        if (OrdineVisibile(disponibili).Count(e => !e.Equals(componente, StringComparison.OrdinalIgnoreCase)) == 0) return false;
        EcuNascoste.Add(componente);
        return true;
    }

    /// <summary>Registra il nuovo ordine delle sole colonne mostrate: i componenti nascoste (o escluse da un filtro) mantengono la loro
    /// posizione relativa, cosi' tornano al posto giusto quando vengono rimostrate.</summary>
    public void ImpostaOrdineVisibili(IReadOnlyList<string> disponibili, IReadOnlyList<string> nuovoOrdineVisibili)
    {
        var completo = OrdineCompleto(disponibili);
        var insieme = new HashSet<string>(nuovoOrdineVisibili, StringComparer.OrdinalIgnoreCase);
        var prossimo = 0;
        for (var i = 0; i < completo.Count; i++)
            if (insieme.Contains(completo[i]) && prossimo < nuovoOrdineVisibili.Count) completo[i] = nuovoOrdineVisibili[prossimo++];
        OrdineEcu = completo;
    }

    /// <summary>Torna al layout predefinito: tutti i componenti visibili, ordine naturale, larghezze e altezze di riga standard.</summary>
    public void RipristinaPredefinite()
    {
        LarghezzaRequisito = null;
        LarghezzeEcu = new(StringComparer.OrdinalIgnoreCase);
        OrdineEcu = new();
        AltezzeRighe = new(StringComparer.OrdinalIgnoreCase);
        EcuNascoste = new();
        Versione = 2;
    }
}
