using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Titolo suggerito del ticket TICKET di una NC/CR: "[strumento] [gamma] [veicolo] [componente] [funzionalità] [descrizione breve]".
/// I gruppi senza valore vengono omessi, l'ordine resta fisso. Piu' valori dello stesso gruppo sono uniti con "/".
/// </summary>
public static class PolarionTicketTitle
{
    private const int MaxLength = 512;

    public static string Build(IEnumerable<string> gamma, IEnumerable<string> veicoli,
        IEnumerable<string> componente, IEnumerable<string> funzionalita, string? descrizioneBreve) =>
        Build(Array.Empty<string>(), gamma, veicoli, componente, funzionalita, descrizioneBreve);

    /// <summary>Con lo strumento (es. UnitLogger) come primo gruppo: "[strumento] [gamma] [veicolo] [componente] [funzionalita] [descrizione]".</summary>
    public static string Build(IEnumerable<string> strumenti, IEnumerable<string> gamma, IEnumerable<string> veicoli,
        IEnumerable<string> componente, IEnumerable<string> funzionalita, string? descrizioneBreve)
    {
        var parts = new List<string>();
        foreach (var group in new[] { strumenti, gamma, veicoli, componente.Select(SiglaEcu), funzionalita })
        {
            var value = string.Join("/", group.Select(Clean).Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (value.Length > 0) parts.Add(value);
        }
        var description = Clean(descrizioneBreve);
        var fixedLength = parts.Sum(p => p.Length + 3) + 2; // "[p] " per ciascuna + "[" "]" della descrizione
        if (fixedLength + description.Length > MaxLength)
            description = description[..Math.Max(0, MaxLength - fixedLength)].TrimEnd();
        if (description.Length > 0) parts.Add(description);
        return string.Join(" ", parts.Select(p => "[" + p + "]"));
    }

    /// <summary>Gamma dei veicoli, risolta dal catalogo Veicoli; i veicoli senza gamma sono ignorati.</summary>
    public static IEnumerable<string> GammaDi(AppState state, IEnumerable<string> veicoli)
    {
        var gammaDiVeicolo = state.Master.Veicoli.Load()
            .GroupBy(v => v.Nome, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GammaNome, StringComparer.OrdinalIgnoreCase);
        return veicoli.Select(v => gammaDiVeicolo.GetValueOrDefault(v, ""));
    }

    /// <summary>Il nome componente del catalogo e' "SIGLA descrizione" (es. "BCM - Body Control Module", "MD1-CE101 (Motore)"):
    /// nel titolo va solo la sigla, cioe' il primo gruppo di caratteri prima di uno spazio.</summary>
    public static string SiglaEcu(string? nome)
    {
        var first = (nome ?? "").Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        return first.TrimEnd('-', '–', ':', ',', ';', '(').Trim();
    }

    private static string Clean(string? value) =>
        (value ?? "").Replace('[', '(').Replace(']', ')').Trim();
}
