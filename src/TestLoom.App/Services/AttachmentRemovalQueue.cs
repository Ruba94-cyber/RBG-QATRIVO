using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Rimozioni fisiche differite degli allegati staccati da un record in editing (Test, CR, Segnalazione/NC).
/// La UI toglie subito l'allegato dalla lista, ma il binario resta nel percorso attivo finche' il record persistito
/// non lo referenzia piu'. Annulla, crash o un Salva fallito lasciano quindi JSON e file coerenti tra loro: nessun
/// record puo' restare a puntare a un binario gia' spostato in recovery.
///
/// Invariante di sicurezza: un binario viene rimosso solo dopo una persistenza riuscita in cui il record non lo
/// referenzia piu'. Dentro una Unit of Work la rimozione e' agganciata al commit: un rollback riporta il record al
/// JSON precedente, che lo referenzia ancora, e la lista pendente resta invariata.
///
/// Gli allegati aggiunti, invece, vengono copiati subito (invariato rispetto al Test): un abbandono esplicito li
/// ripulisce tramite DraftAttachmentCleanupService, un crash lascia un orfano accettato (nessun record lo referenzia).
/// </summary>
public sealed class AttachmentRemovalQueue
{
    private readonly List<Allegato> _pendenti = new();
    private HashSet<Guid> _referenziatiDopoPersistenza = new();
    private Action<Allegato>? _eliminaDopoPersistenza;

    public bool HaPendenti => _pendenti.Count > 0;

    /// <summary>Stacca l'allegato dal record in editing. Il binario non viene toccato qui.</summary>
    public void Accoda(Allegato allegato)
    {
        ArgumentNullException.ThrowIfNull(allegato);
        if (_pendenti.All(p => p.Uid != allegato.Uid)) _pendenti.Add(allegato);
    }

    /// <summary>
    /// Da chiamare subito dopo che il record e' stato persistito con gli allegati indicati. Elimina fisicamente i
    /// pendenti che il record persistito non referenzia piu'. Dentro una Unit of Work l'esecuzione slitta al commit;
    /// un errore di rimozione viene solo registrato: il record e' gia' coerente e al peggio resta un orfano.
    /// </summary>
    public void ApplicaDopoPersistenza(IEnumerable<Allegato> allegatiPersistiti, Action<Allegato> elimina)
    {
        ArgumentNullException.ThrowIfNull(elimina);
        _referenziatiDopoPersistenza = allegatiPersistiti.Select(a => a.Uid).ToHashSet();
        _eliminaDopoPersistenza = elimina;
        if (!HaPendenti) return;
        if (!WorkflowTransactionService.DeferAfterCommit(EseguiRimozioni)) EseguiRimozioni();
    }

    /// <summary>
    /// Abbandono esplicito: la lista viene dimenticata senza toccare alcun binario. Restituisce gli allegati staccati
    /// perche' il cleanup dell'abbandono li consideri nella copia di lavoro: il reconcile rimuove solo quelli mai
    /// autorevoli e lascia intatti quelli ancora referenziati dal record persistito.
    /// </summary>
    public IReadOnlyList<Allegato> Abbandona()
    {
        var staccati = _pendenti.ToList();
        _pendenti.Clear();
        return staccati;
    }

    /// <summary>Aggiunge alla copia di lavoro gli allegati staccati che non vi compaiono gia'.</summary>
    public static void UnisciAllaCopiaDiLavoro(List<Allegato> copia, IEnumerable<Allegato> staccati)
    {
        foreach (var staccato in staccati.ToList())
            if (copia.All(a => a.Uid != staccato.Uid)) copia.Add(staccato);
    }

    private void EseguiRimozioni()
    {
        var elimina = _eliminaDopoPersistenza;
        foreach (var pendente in _pendenti.ToList())
        {
            if (_referenziatiDopoPersistenza.Contains(pendente.Uid)) continue; // mai rimuovere un binario ancora referenziato
            try { elimina?.Invoke(pendente); }
            catch (Exception ex) { CrashLogService.Registra($"Rimozione allegato dopo salvataggio {pendente.Uid:N}", ex); }
        }
        _pendenti.Clear();
    }
}
