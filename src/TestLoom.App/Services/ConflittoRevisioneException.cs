namespace TestLoom.App.Services;

/// <summary>
/// Conflitto ottimistico su un'entità condivisa. Oltre alla revisione logica supporta il caso in cui il
/// contenuto sia cambiato lasciando invariata RevisioneDati (es. modifica manuale del JSON o ramo OneDrive
/// divergente ancora alla stessa revisione). Resta la stessa eccezione per non cambiare il flusso UI/merge
/// esistente: il chiamante deve comunque rileggere la versione remota e fare three-way merge/fail-closed.
/// </summary>
public sealed class ConflittoRevisioneException : IOException
{
    public string ElementoId { get; }
    public long RevisioneAttesa { get; }
    public long RevisioneCorrente { get; }
    public bool ContenutoDifferenteAllaStessaRevisione { get; }

    public ConflittoRevisioneException(string elementoId, long attesa, long corrente)
        : this(elementoId, attesa, corrente, contenutoDifferenteAllaStessaRevisione: false)
    {
    }

    public ConflittoRevisioneException(string elementoId, long attesa, long corrente, bool contenutoDifferenteAllaStessaRevisione)
        : base(contenutoDifferenteAllaStessaRevisione
            ? $"{elementoId} ha contenuto diverso da quello aperto, pur risultando ancora alla revisione {corrente}. Le modifiche locali non sono state sovrascritte."
            : $"{elementoId} è stato modificato da un altro operatore (revisione aperta {attesa}, revisione corrente {corrente}). Le modifiche locali non sono state sovrascritte.")
    {
        ElementoId = elementoId;
        RevisioneAttesa = attesa;
        RevisioneCorrente = corrente;
        ContenutoDifferenteAllaStessaRevisione = contenutoDifferenteAllaStessaRevisione;
    }
}
