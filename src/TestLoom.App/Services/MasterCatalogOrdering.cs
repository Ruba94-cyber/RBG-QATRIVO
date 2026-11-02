using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Ordine di presentazione stabile dei cataloghi master letti da Phase D (6.6.0).
///
/// Il materializer legge le teste <c>ORDER BY entity_uid</c>: gli UID sono casuali, quindi template, componente, veicoli e
/// funzionalita comparivano in ordine casuale. Questa proiezione ordina SOLO in lettura, senza cambiare alcun dato
/// persistito e senza usare l'ordine locale delle righe del database (che dipende dal PC):
/// <list type="number">
/// <item>ordine esplicito <c>Ordine</c>, quando la riga ne ha uno (template di test, strumenti diagnostici);</item>
/// <item>chiave business (nome, o Id per i requisiti) in ordine ordinale senza distinzione di maiuscole;</item>
/// <item>UID come ultimo spareggio, cosi' l'ordine e' totale e identico su ogni riavvio e su ogni postazione.</item>
/// </list>
/// Il legacy JSON non conserva un indice persistente nel formato Phase D (il bootstrap importa le righe con UID e
/// timestamp deterministici e senza posizione), quindi l'ordine originario del file non e' ricostruibile: il fallback
/// alfabetico e' l'unico ordine che ogni postazione puo' calcolare uguale dal solo contenuto condiviso.
/// </summary>
public static class MasterCatalogOrdering
{
    public static List<T> Ordina<T>(IEnumerable<T> righe) where T : MasterEntity
    {
        ArgumentNullException.ThrowIfNull(righe);
        return righe
            .OrderBy(r => OrdineEsplicito(r) ?? 0)
            .ThenBy(r => PhaseDMasterBusinessKeyService.Get(r), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => MasterEntityIdentityService.GetRequired(r))
            .ToList();
    }

    private static int? OrdineEsplicito(MasterEntity riga) => riga switch
    {
        TestTemplate template => template.Ordine,
        StrumentoDiagnostico strumento => strumento.Ordine,
        _ => null
    };
}
