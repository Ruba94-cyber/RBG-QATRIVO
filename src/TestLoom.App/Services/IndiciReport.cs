using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Indici dell'export Excel del Report, costruiti una volta per foglio. Sostituiscono le scansioni complete di tutti i
/// Test e di tutte le non conformita ripetute per ogni requisito (costo requisiti x test e requisiti x non conformita). Ogni
/// risultato conserva l'ordine di origine: il contenuto del workbook resta quello di prima.</summary>
internal static class IndiciReport
{
    /// <summary>Test per ID requisito, senza distinzione di maiuscole (come <see cref="RequisitoIdConfronto"/>), nell'ordine di
    /// <paramref name="testCases"/>. Un Test compare una sola volta per requisito.</summary>
    public static Dictionary<string, List<TestCase>> TestPerRequisito(IEnumerable<TestCase> testCases)
    {
        var indice = new Dictionary<string, List<TestCase>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in testCases)
        {
            foreach (var requisitoId in tc.RequisitoId.Where(id => id != null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!indice.TryGetValue(requisitoId, out var lista)) indice[requisitoId] = lista = new List<TestCase>();
                lista.Add(tc);
            }
        }
        return indice;
    }

    /// <summary>Test che referenziano il requisito, nell'ordine di origine (vuoto se nessuno).</summary>
    public static IReadOnlyList<TestCase> TestDelRequisito(Dictionary<string, List<TestCase>> testPerRequisito, string requisitoId) =>
        testPerRequisito.TryGetValue(requisitoId, out var lista) ? lista : Array.Empty<TestCase>();

    /// <summary>Non conformita collegate a ciascun Test, come indici in <paramref name="nc"/> (ordine di origine). I candidati
    /// si cercano per UID e per codice della prova; ogni candidato passa il criterio di
    /// <see cref="AnomaliaWorkflowService.AppartieneAlTest"/>, lo stesso della scansione completa. L'indice evita solo di
    /// provare ogni coppia Test x non conformita: un Test che non e' candidato non puo' appartenere alla non conformita.</summary>
    public static Dictionary<TestCase, List<int>> NcPerTest(IReadOnlyList<NonConformita> nc, IEnumerable<TestCase> testCases)
    {
        var perUid = new Dictionary<Guid, List<TestCase>>();
        var perCodice = new Dictionary<string, List<TestCase>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in testCases)
        {
            if (!perUid.TryGetValue(tc.Uid, out var daUid)) perUid[tc.Uid] = daUid = new List<TestCase>();
            daUid.Add(tc);
            if (!perCodice.TryGetValue(tc.Id, out var daCodice)) perCodice[tc.Id] = daCodice = new List<TestCase>();
            daCodice.Add(tc);
        }

        var indice = new Dictionary<TestCase, List<int>>(ReferenceEqualityComparer.Instance);
        var candidati = new HashSet<TestCase>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < nc.Count; i++)
        {
            var anomalia = nc[i];
            candidati.Clear();
            if (anomalia.TestUid is { } uidAnomalia && perUid.TryGetValue(uidAnomalia, out var daUidAnomalia))
                candidati.UnionWith(daUidAnomalia);
            foreach (var prova in anomalia.ProveCollegate)
            {
                if (prova.TestUid is { } uidProva)
                {
                    if (perUid.TryGetValue(uidProva, out var daUidProva)) candidati.UnionWith(daUidProva);
                }
                else if (perCodice.TryGetValue(prova.TestId, out var daCodiceProva))
                {
                    candidati.UnionWith(daCodiceProva);
                }
            }
            foreach (var tc in candidati)
            {
                if (!AnomaliaWorkflowService.AppartieneAlTest(anomalia, tc.Id, tc)) continue;
                if (!indice.TryGetValue(tc, out var lista)) indice[tc] = lista = new List<int>();
                lista.Add(i);
            }
        }
        return indice;
    }

    /// <summary>ID delle non conformita aperte collegate ai Test dati, nell'ordine di <paramref name="nc"/>, senza ripetizioni.</summary>
    public static List<string> NcAperteCollegate(IEnumerable<TestCase> testDelRequisito, Dictionary<TestCase, List<int>> ncPerTest,
        IReadOnlyList<NonConformita> nc)
    {
        var indici = new SortedSet<int>();
        foreach (var tc in testDelRequisito)
            if (ncPerTest.TryGetValue(tc, out var lista)) indici.UnionWith(lista);
        return indici.Select(i => nc[i]).Where(n => n.Stato == StatoNonConformita.Aperta).Select(n => n.Id).ToList();
    }
}
