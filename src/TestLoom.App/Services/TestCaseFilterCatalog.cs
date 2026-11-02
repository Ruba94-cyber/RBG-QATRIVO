using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Cataloghi distinti usati dai filtri della Lista Test. Prima ogni catalogo eseguiva una propria scansione
/// dell'intero archivio (componente, veicoli, assegnatari, creatori, requisiti, funzionalità): con 100k Test erano
/// sei passate complete prima ancora di applicare il filtro corrente. Questa aggregazione conserva la stessa
/// semantica ma visita ogni Test una sola volta.
/// </summary>
public sealed record TestCaseFilterCatalogSnapshot(
    IReadOnlyList<string> Componente,
    IReadOnlyList<string> Veicoli,
    IReadOnlyList<string> Assegnatari,
    IReadOnlyList<string> Creatori,
    IReadOnlyList<string> Requisiti,
    IReadOnlyList<string> Funzionalita);

public static class TestCaseFilterCatalog
{
    public static TestCaseFilterCatalogSnapshot Build(IEnumerable<TestCase> testCases)
    {
        ArgumentNullException.ThrowIfNull(testCases);

        // Mantiene esattamente i comparer del vecchio LINQ:
        // componente usava Distinct(StringComparer.OrdinalIgnoreCase), tutti gli altri Distinct() standard.
        var componente = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var veicoli = new HashSet<string>();
        var assegnatari = new HashSet<string>();
        var creatori = new HashSet<string>();
        var requisiti = new HashSet<string>();
        var funzionalita = new HashSet<string>();

        foreach (var tc in testCases)
        {
            foreach (var value in tc.Componente)
                AddNonBlank(componente, value);
            foreach (var value in tc.VeicoliEffettivi)
                AddNonBlank(veicoli, value);
            AddNonBlank(assegnatari, tc.Assegnatario);
            AddNonBlank(creatori, tc.CreatoDa);
            foreach (var value in tc.RequisitoId)
                AddNonBlank(requisiti, value);
            foreach (var value in tc.Funzionalita)
                AddNonBlank(funzionalita, value);
        }

        // OrderBy(x => x) conserva il comparer/cultura preesistente della UI.
        return new TestCaseFilterCatalogSnapshot(
            Sort(componente), Sort(veicoli), Sort(assegnatari), Sort(creatori), Sort(requisiti), Sort(funzionalita));
    }

    private static void AddNonBlank(HashSet<string> target, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target.Add(value);
    }

    private static IReadOnlyList<string> Sort(HashSet<string> values) => values.OrderBy(x => x).ToList();
}
