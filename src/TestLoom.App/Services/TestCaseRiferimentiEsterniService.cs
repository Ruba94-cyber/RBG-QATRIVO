using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Insieme degli Id Test referenziati da almeno una NC/CR/Segnalazione esistente — punto unico di verità
/// condiviso dall'harness di correzione della cartella dati (tools/data-fix-6.4.2: elimina i Test con
/// Origine=ImportStatoUdt, ma MAI se referenziati altrove, fail-closed) e da qualunque altro punto dell'app
/// che debba fare la stessa domanda in futuro. Stesso principio di Models.TestCaseOrigine.EImportStatoUdt:
/// una funzione pura, condivisa, invece di farla reimplementare separatamente a ogni chiamante (divergenza
/// silenziosa sui campi controllati).
/// </summary>
public static class TestCaseRiferimentiEsterniService
{
    public static HashSet<string> TestIdReferenziati(IEnumerable<NonConformita> nonConformita,
        IEnumerable<ChangeRequest> changeRequest, IEnumerable<SegnalazioneDaClassificare> segnalazioni)
    {
        var referenziati = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nonConformita)
        {
            if (!string.IsNullOrWhiteSpace(n.TestCaseId)) referenziati.Add(n.TestCaseId);
            foreach (var p in n.ProveCollegate) if (!string.IsNullOrWhiteSpace(p.TestId)) referenziati.Add(p.TestId);
        }
        foreach (var c in changeRequest)
            foreach (var v in c.TestValidazione) if (!string.IsNullOrWhiteSpace(v.TestId)) referenziati.Add(v.TestId);
        foreach (var s in segnalazioni) if (!string.IsNullOrWhiteSpace(s.TestId)) referenziati.Add(s.TestId);
        return referenziati;
    }

    /// <summary>Sottoinsieme di <paramref name="testCases"/> il cui Id compare in
    /// <paramref name="idReferenziati"/> — un solo Test in questo elenco basta per rendere fail-closed
    /// un'operazione che vorrebbe eliminarli tutti.</summary>
    public static List<TestCase> Referenziati(IReadOnlyList<TestCase> testCases, IReadOnlySet<string> idReferenziati) =>
        testCases.Where(t => idReferenziati.Contains(t.Id)).ToList();
}
