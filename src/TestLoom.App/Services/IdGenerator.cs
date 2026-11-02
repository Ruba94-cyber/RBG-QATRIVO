using System.Text.RegularExpressions;

namespace TestLoom.App.Services;

/// <summary>Genera ID brevi e leggibili "T-0001" progressivi (feedback utente 2026-08-20, punto 19: "l'id
/// deve essere ridotto essendo puramente un identificativo utile" — prima incorporava componente-data-iniziali,
/// ridondante con le colonne già in tabella/i filtri e con l'organizzazione su disco già per componente/Id
/// (TestCaseRepository.PathFor), quindi non serviva davvero a localizzare il file). Decentralizzato come
/// prima: ogni istanza calcola il proprio progressivo dagli ID esistenti passati, senza contatore centrale;
/// TestCaseRepository.Save riverifica comunque l'unicità al momento della scrittura effettiva (Addendum
/// §16.1) rigenerando in caso di collisione dell'ultimo istante.
///
/// Le iniziali dell'operatore sono tornate nell'ID come prefisso "T-OPA-0001" (feedback utente 2026-08-24):
/// il progressivo resta globale (calcolato su tutti gli ID esistenti, in entrambi i formati), le iniziali
/// servono solo a distinguere a colpo d'occhio due test creati "in parallelo" da operatori diversi prima che
/// una cartella condivisa disallineata si risincronizzi. Gli ID già esistenti nel vecchio formato "T-0001"
/// non vengono rinominati: ProgressivoRegex riconosce entrambi i formati per continuare a trovare il
/// progressivo massimo su tutto lo storico.</summary>
public static partial class IdGenerator
{
    [GeneratedRegex(@"^T-(?:(?<ini>[A-Za-z]+)-)?(?<prog>\d+)$")]
    private static partial Regex ProgressivoRegex();

    public static string Genera(IEnumerable<string> idEsistenti, string iniziali)
    {
        var maxProg = 0;
        foreach (var id in idEsistenti)
        {
            var m = ProgressivoRegex().Match(id);
            if (m.Success && int.TryParse(m.Groups["prog"].Value, out var p))
                maxProg = Math.Max(maxProg, p);
        }
        return $"T-{iniziali}-{(maxProg + 1):D4}";
    }

    public static string RegeneraProgressivo(string id)
    {
        var m = ProgressivoRegex().Match(id);
        if (!m.Success) return "T-0002";
        var prog = int.Parse(m.Groups["prog"].Value) + 1;
        return m.Groups["ini"].Success ? $"T-{m.Groups["ini"].Value}-{prog:D4}" : $"T-{prog:D4}";
    }

    /// <summary>Progressivo numerico di un ID ("T-AB-0042" o "T-0042"); false se l'ID non ha questo formato.</summary>
    public static bool TryProgressivo(string? id, out int progressivo)
    {
        progressivo = 0;
        var m = ProgressivoRegex().Match(id ?? string.Empty);
        return m.Success && int.TryParse(m.Groups["prog"].Value, out progressivo);
    }

    public static string IniziliDa(string nomeCompleto)
    {
        var parti = nomeCompleto.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parti.Select(p => char.ToUpperInvariant(p[0])));
    }
}
