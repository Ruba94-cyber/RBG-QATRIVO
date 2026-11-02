namespace TestLoom.App.Models;

/// <summary>Quali campi strumento PER OPERAZIONE vengono mostrati in un Test. Solo visibilita': i valori restano nel modello,
/// nei report e nelle esportazioni, e un campo che ha gia' un valore non viene mai nascosto.
/// Due livelli: il default del tipo di test (<see cref="TestTemplate.CampiOperazioneNascosti"/>, deciso dal Master) e un
/// override del singolo Test, salvato in <see cref="TestCase.ValoriStrumentiTest"/> sotto una chiave riservata (cosi' i client
/// precedenti lo ritrasmettono senza interpretarlo). Chiave campo: "Strumento::Campo".</summary>
public static class VistaCampiOperazione
{
    public const string ChiaveValoriStrumentiTest = "VISTA::CampiOperazione";

    /// <summary>Override del Test: true = mostra, false = nascondi. Assente = vale il default del tipo di test.</summary>
    public static Dictionary<string, bool> LeggiOverride(IReadOnlyDictionary<string, string>? valoriTest)
    {
        var risultato = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (valoriTest == null || !valoriTest.TryGetValue(ChiaveValoriStrumentiTest, out var testo) || string.IsNullOrWhiteSpace(testo))
            return risultato;
        foreach (var riga in testo.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (riga.Length < 4 || riga[0] is not ('+' or '-') || !riga.Contains("::")) continue;
            risultato[riga[1..]] = riga[0] == '+';
        }
        return risultato;
    }

    public static void ScriviOverride(Dictionary<string, string> valoriTest, IReadOnlyDictionary<string, bool> overrides)
    {
        if (overrides.Count == 0) { valoriTest.Remove(ChiaveValoriStrumentiTest); return; }
        valoriTest[ChiaveValoriStrumentiTest] = string.Join('\n',
            overrides.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Value ? "+" : "-") + kv.Key));
    }

    /// <summary>Vero se il campo va nascosto: scelta del Test, altrimenti default del tipo di test. Un campo con valore resta visibile.</summary>
    public static bool Nascosto(string chiave, IReadOnlyCollection<string>? nascostiDelTemplate,
        IReadOnlyDictionary<string, bool> overrides, bool haValore)
    {
        if (haValore) return false;
        if (overrides.TryGetValue(chiave, out var mostra)) return !mostra;
        return nascostiDelTemplate?.Contains(chiave, StringComparer.OrdinalIgnoreCase) == true;
    }

    /// <summary>Imposta la visibilita' voluta di un campo. Se coincide col default del tipo di test l'override viene tolto.</summary>
    public static void Imposta(Dictionary<string, string> valoriTest, string chiave, bool visibile,
        IReadOnlyCollection<string>? nascostiDelTemplate)
    {
        var overrides = LeggiOverride(valoriTest);
        var defaultVisibile = nascostiDelTemplate?.Contains(chiave, StringComparer.OrdinalIgnoreCase) != true;
        if (visibile == defaultVisibile) overrides.Remove(chiave);
        else overrides[chiave] = visibile;
        ScriviOverride(valoriTest, overrides);
    }
}
