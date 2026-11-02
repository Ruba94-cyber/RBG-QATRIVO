using TestLoom.App.Models;

namespace TestLoom.App.Services;

public class TestCaseCandidato
{
    public TestCase TestCase { get; set; } = null!;
    public bool BaselineDiversa { get; set; }

    /// <summary>Include componente e Descrizione breve (feedback utente 2026-08-20), non solo l'Id: senza contesto
    /// l'operatore doveva aprire ogni candidato al buio per capire se è davvero lo stesso test.</summary>
    public string Etichetta
    {
        get
        {
            var etichetta = TestCase.Id;
            if (!string.IsNullOrWhiteSpace(TestCase.DescrizioneBreve)) etichetta += $"  —  {TestCase.DescrizioneBreve}";
            if (BaselineDiversa) etichetta += $"  —  baseline diversa ({UltimaBaseline(TestCase)})";
            return etichetta;
        }
    }

    private static string UltimaBaseline(TestCase tc) => tc.Esecuzioni.Count > 0 ? tc.Esecuzioni[^1].BaselineSw : "";
}

/// <summary>Rilevamento duplicati/iterazioni (§6.2) e proposta di test correlati (§6.3). Include la baseline
/// SW/HW (dell'ultima revisione) nel confronto ma non la usa per bloccare: se la baseline differisce il
/// candidato resta proposto, con l'indicazione esplicita della differenza (Addendum §16.4), lasciando la
/// decisione all'operatore.</summary>
public static class DuplicateDetectionService
{
    public static List<TestCaseCandidato> TrovaCandidatiIterazione(
        IReadOnlyList<TestCase> archivio, string componente, IEnumerable<string> requisitoId, IEnumerable<string> funzionalita,
        Guid testTemplateUid, string baselineSw, string baselineHw, string? escludiId = null)
        => TrovaCandidatiIterazione(archivio, new[] { componente }, requisitoId, funzionalita, testTemplateUid, baselineSw, baselineHw, escludiId);

    public static List<TestCaseCandidato> TrovaCandidatiIterazione(
        IReadOnlyList<TestCase> archivio, IEnumerable<string> componente, IEnumerable<string> requisitoId, IEnumerable<string> funzionalita,
        Guid testTemplateUid, string baselineSw, string baselineHw, string? escludiId = null)
    {
        var ecuSet = componente.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reqSet = requisitoId.ToHashSet();
        var funzSet = funzionalita.ToHashSet();
        if (reqSet.Count == 0 && funzSet.Count == 0) return new List<TestCaseCandidato>();

        return archivio
            .Where(tc => !tc.Eliminato && tc.Id != escludiId && tc.Componente.Any(ecuSet.Contains) && tc.TestTemplateUid == testTemplateUid)
            .Where(tc => tc.RequisitoId.Any(reqSet.Contains) || tc.Funzionalita.Any(funzSet.Contains))
            .Select(tc =>
            {
                var ultima = tc.Esecuzioni.Count > 0 ? tc.Esecuzioni[^1] : null;
                return new TestCaseCandidato
                {
                    TestCase = tc,
                    BaselineDiversa = ultima != null && (ultima.BaselineSw != baselineSw || ultima.BaselineHw != baselineHw)
                };
            })
            .ToList();
    }

    /// <summary>Propone come testCorrelati eventuali test su altro template (es. banco -> veicolo) che coprono
    /// lo stesso requisito/funzionalità (§6.3).</summary>
    public static List<TestCase> TrovaCandidatiCorrelati(
        IReadOnlyList<TestCase> archivio, string componente, IEnumerable<string> requisitoId, IEnumerable<string> funzionalita,
        Guid testTemplateUid, string? escludiId = null)
        => TrovaCandidatiCorrelati(archivio, new[] { componente }, requisitoId, funzionalita, testTemplateUid, escludiId);

    public static List<TestCase> TrovaCandidatiCorrelati(
        IReadOnlyList<TestCase> archivio, IEnumerable<string> componente, IEnumerable<string> requisitoId, IEnumerable<string> funzionalita,
        Guid testTemplateUid, string? escludiId = null)
    {
        var ecuSet = componente.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reqSet = requisitoId.ToHashSet();
        var funzSet = funzionalita.ToHashSet();
        if (reqSet.Count == 0 && funzSet.Count == 0) return new List<TestCase>();

        return archivio
            .Where(tc => !tc.Eliminato && tc.Id != escludiId && tc.Componente.Any(ecuSet.Contains) && tc.TestTemplateUid != testTemplateUid)
            .Where(tc => tc.RequisitoId.Any(reqSet.Contains) || tc.Funzionalita.Any(funzSet.Contains))
            .ToList();
    }

    /// <summary>Rileva ID duplicati tra più file — tipicamente una copia di conflitto SharePoint/OneDrive
    /// creata quando due operatori hanno scritto lo stesso test case in una finestra di sync troppo
    /// ravvicinata: entrambi i file sono JSON validi (non intercettati da "file con problemi"), ma risolvono
    /// allo stesso TestCase.Id, rendendo ambiguo qualunque riferimento incrociato finché non vengono
    /// riconciliati manualmente.</summary>
    public static List<TestCaseDuplicato> TrovaIdDuplicati(IEnumerable<(string Percorso, string Id)> file) =>
        file.GroupBy(f => f.Id)
            .Where(g => g.Count() > 1)
            .Select(g => new TestCaseDuplicato { Id = g.Key, Percorsi = g.Select(f => f.Percorso).OrderBy(p => p).ToList() })
            .OrderBy(d => d.Id)
            .ToList();
}
