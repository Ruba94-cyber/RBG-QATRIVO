namespace TestLoom.App.Services;

/// <summary>
/// Punto di estensione PER CATALOGO Phase D per la policy di collisione di chiave business (stessa chiave,
/// UID diverse — vedi <see cref="PhaseDMasterBusinessKeyConflictException"/>). Per la stragrande maggioranza
/// dei cataloghi Phase D una simile collisione indica una vera corruzione/bug di unicità e DEVE restare
/// fail-closed (comportamento invariato: <see cref="PhaseDMasterMaterializer.LoadCatalog{T}"/>,
/// <see cref="PhaseDMasterRepositoryEngine{T}"/>, <see cref="PhaseDMasterScalableReader.LoadPatchState"/>
/// continuano a lanciare).
///
/// Il catalogo <c>audit-valutazioni-requisito-componente-veicolo</c> è l'unica eccezione (bug reale osservato in
/// 6.3.1, verificato 6/6): <c>ValutazioneRequisitoEcuVeicoloService.Propaga/ImpostaManuale/PropagaMassiva</c>
/// fanno sempre RemoveAll+Add di una riga NUOVA con UID casuale, mai un update in-place. Due postazioni che
/// scrivono quasi in contemporanea la STESSA cella nuova (stessa chiave business RequisitoId|Componente|Veicolo)
/// possono quindi produrre due Upsert con UID diverse che superano entrambe la validazione locale e finiscono
/// nel journal condiviso append-only PRIMA che l'altra sia visibile: da quel momento in poi, senza questa
/// tolleranza, qualunque Load() (anche da una terza postazione mai coinvolta) renderebbe l'intero catalogo
/// permanentemente illeggibile, senza alcun percorso di recupero dall'UI.
///
/// La collisione per QUESTO catalogo è repairabile deterministicamente (vedi
/// <see cref="ValutazioneRequisitoEcuVeicoloService.ConsolidaCollisioni"/>: vince l'UltimaModifica più
/// recente, a parità l'UID ordinale minore) e la riparazione avviene lato scrittura al primo Save successivo
/// (<see cref="ValutazioneRequisitoEcuVeicoloService.Propaga"/>/<c>ImpostaManuale</c>/<c>PropagaMassiva</c>).
/// Tollerare la collisione qui è quindi sicuro: il catalogo resta sempre leggibile e la riparazione converge,
/// mai una perdita silenziosa di dati (la riga perdente confluisce in Nota/Changelog della vincente).
/// </summary>
public static class PhaseDMasterKeyCollisionTolerance
{
    private static readonly HashSet<string> TolerantCatalogs = new(StringComparer.OrdinalIgnoreCase)
    {
        "audit-valutazioni-requisito-componente-veicolo",
        "report-stato-funzionalita-componente-veicolo",
    };

    /// <summary>Vero se una collisione di chiave business su questo catalogo NON deve bloccare
    /// lettura/scrittura (vedi commento di classe). Per ogni altro catalogo resta fail-closed.</summary>
    public static bool IsTolerant(string catalog) => TolerantCatalogs.Contains(catalog);
}
