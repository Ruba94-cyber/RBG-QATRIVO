using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Hash condiviso dei Requisiti per le cache di copertura (LocalTestArchiveQueryService.GetHomeAggiornaBatch e
/// LocalHomeDashboardQueryService.GetRequirementCoverage): un solo punto che entrambe richiamano, cosi' non
/// possono piu' divergere su quali campi del Requisito rendono stantia la cache.
///
/// Il catalogo Requisiti e' JSON e non fa parte del PRAGMA data_version dell'indice SQLite Test: un requisito
/// editato (Tipo Standard/Extra, Sottoveicoli Extra, applicabilita' Banco/Veicolo) senza alcuna scrittura
/// sull'archivio Test non invaliderebbe altrimenti la cache, restituendo la copertura del tipo/ambito
/// precedente. Include quindi ogni campo che GetRequirementCoverageCore legge dal Requisito: Id/Archiviata per
/// il filtro, ApplicabileBanco/Veicolo, e Tipo/Sottoveicoli Extra (RequisitoApplicabilita.ED4D/SottoveicoliDi) per
/// il ramo Extra.
/// </summary>
internal static class RequisitiCoverageCacheKey
{
    public static int ComputeHash(IReadOnlyList<Requisito> requisiti)
    {
        var hash = requisiti.Count;
        foreach (var r in requisiti)
        {
            hash = HashCode.Combine(hash, r.Id, r.Archiviata, r.ApplicabileBanco, r.ApplicabileVeicolo,
                RequisitoApplicabilita.ED4D(r),
                string.Join('\u0001', RequisitoApplicabilita.SottoveicoliDi(r)));
        }
        return hash;
    }
}
