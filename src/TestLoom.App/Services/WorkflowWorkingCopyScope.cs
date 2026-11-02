using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Overlay temporaneo delle working-copy possedute dall'editor durante un singolo salvataggio workflow.
///
/// I repository di NC/Segnalazioni espongono deliberatamente snapshot clonati: nessun ViewModel deve poter
/// mutare la cache globale prima del Save. Il salvataggio storico del Test, però, rilegge alcune entità dal
/// repository mentre applica i side-effect del workflow. Questo scope permette a QUEL SOLO flusso async di
/// vedere le copie effettivamente modificate nel form, senza reintrodurre riferimenti condivisi nella cache.
///
/// Le copie registrate qui non vengono mai restituite direttamente: Apply/Get clonano nuovamente l'overlay,
/// così anche le mutazioni preparatorie fatte dal commit non alterano il form. In caso di rollback l'utente
/// conserva esattamente la propria working-copy precedente al tentativo.
/// </summary>
public static class WorkflowWorkingCopyScope
{
    private static readonly AsyncLocal<State?> Current = new();

    /// <summary>True se nel flusso corrente è aperto uno scope di working-copy (es. dentro SalvaSicuro, anche
    /// durante il ripristino dopo un rollback). In quel caso PeekNonConformita restituisce le working-copy
    /// interne dello scope senza clonarle: chi trattiene i risultati oltre la chiamata (es. liste mostrate
    /// dalla UI) deve usare il percorso che clona (ApplyNonConformita / GetAll).</summary>
    public static bool Attivo => Current.Value != null;

    public static IDisposable Begin(
        IEnumerable<NonConformita>? nonConformita = null,
        IEnumerable<SegnalazioneDaClassificare>? segnalazioni = null)
    {
        if (Current.Value != null)
            throw new InvalidOperationException("Esiste gia uno scope di working-copy workflow attivo nello stesso flusso.");

        var state = new State();
        if (nonConformita != null)
        {
            foreach (var item in nonConformita.Where(x => x.Uid != Guid.Empty).DistinctBy(x => x.Uid))
                state.NonConformita[item.Uid] = item.Clona();
        }
        if (segnalazioni != null)
        {
            foreach (var item in segnalazioni.Where(x => x.Uid != Guid.Empty).DistinctBy(x => x.Uid))
                state.Segnalazioni[item.Uid] = EntityStateFingerprint.Clone(item);
        }

        Current.Value = state;
        return new Scope(state);
    }

    internal static long CountNonConformitaByState(IEnumerable<NonConformita> authoritative, StatoNonConformita stato)
    {
        var state = Current.Value;
        return authoritative.LongCount(item =>
            (state != null && state.NonConformita.TryGetValue(item.Uid, out var working) ? working : item).Stato == stato);
    }
    /// <summary>
    /// Come ApplyNonConformita, ma SENZA clonare: ogni elemento ritornato è l'istanza effettiva in cache (o la
    /// working-copy attiva), non una copia. Il chiamante deve trattarle come strettamente di sola lettura e non
    /// trattenerle oltre l'ambito sincrono della chiamata — nessuna garanzia di isolamento dalla cache globale.
    /// Pensata per i pochi punti caldi verificati che devono solo leggere/ordinare/contare l'intero archivio NC
    /// (Addendum prestazioni 2026-09-20): ApplyNonConformita (che clona sempre) resta la scelta di default per
    /// qualunque oggetto che possa finire esposto a mutazione o salvataggio.
    /// </summary>
    public static IReadOnlyList<NonConformita> PeekNonConformita(IReadOnlyList<NonConformita> authoritative)
    {
        var state = Current.Value;
        if (state == null || state.NonConformita.Count == 0) return authoritative;

        var result = new List<NonConformita>(authoritative);
        for (var i = 0; i < result.Count; i++)
        {
            if (state.NonConformita.TryGetValue(result[i].Uid, out var working))
                result[i] = working;
        }
        return result;
    }

    public static IReadOnlyList<NonConformita> ApplyNonConformita(IEnumerable<NonConformita> authoritative)
    {
        var state = Current.Value;
        // Clona() invece del giro JSON generico (feedback utente 2026-09-17, volumi 100k): stesso isolamento,
        // ordini di grandezza più rapido su un intero archivio NC.
        var result = authoritative.Select(x => x.Clona()).ToList();
        if (state == null || state.NonConformita.Count == 0) return result;

        for (var i = 0; i < result.Count; i++)
        {
            if (state.NonConformita.TryGetValue(result[i].Uid, out var working))
                result[i] = working.Clona();
        }
        return result;
    }

    public static NonConformita? ApplyNonConformita(NonConformita? authoritative)
    {
        if (authoritative == null) return null;
        var state = Current.Value;
        return state != null && state.NonConformita.TryGetValue(authoritative.Uid, out var working)
            ? working.Clona()
            : authoritative.Clona();
    }

    /// <summary>Come CountNonConformitaByState, per le Segnalazioni: conteggio scalare sullo stesso overlay di
    /// GetAll/ApplySegnalazioni, senza clonare l'intero archivio (Addendum prestazioni 2026-09-25, Home a 100k:
    /// DaClassificare non deve materializzare ogni Segnalazione solo per contarle).</summary>
    internal static long CountSegnalazioniByState(IEnumerable<SegnalazioneDaClassificare> authoritative, StatoSegnalazione stato)
    {
        var state = Current.Value;
        return authoritative.LongCount(item =>
            (state != null && state.Segnalazioni.TryGetValue(item.Uid, out var working) ? working : item).Stato == stato);
    }

    public static IReadOnlyList<SegnalazioneDaClassificare> ApplySegnalazioni(IEnumerable<SegnalazioneDaClassificare> authoritative)
    {
        var state = Current.Value;
        var result = authoritative.Select(EntityStateFingerprint.Clone).ToList();
        if (state == null || state.Segnalazioni.Count == 0) return result;

        for (var i = 0; i < result.Count; i++)
        {
            if (state.Segnalazioni.TryGetValue(result[i].Uid, out var working))
                result[i] = EntityStateFingerprint.Clone(working);
        }
        return result;
    }

    /// <summary>Come ApplySegnalazioni, ma SENZA clonare (stessa regola di PeekNonConformita): elementi di cache o working-copy
    /// attiva, da trattare come sola lettura entro la chiamata. Usata dalla ricerca globale, che proietta solo stringhe.</summary>
    public static IReadOnlyList<SegnalazioneDaClassificare> PeekSegnalazioni(IReadOnlyList<SegnalazioneDaClassificare> authoritative)
    {
        var state = Current.Value;
        if (state == null || state.Segnalazioni.Count == 0) return authoritative;

        var result = new List<SegnalazioneDaClassificare>(authoritative);
        for (var i = 0; i < result.Count; i++)
        {
            if (state.Segnalazioni.TryGetValue(result[i].Uid, out var working))
                result[i] = working;
        }
        return result;
    }

    public static SegnalazioneDaClassificare? ApplySegnalazione(SegnalazioneDaClassificare? authoritative)
    {
        if (authoritative == null) return null;
        var state = Current.Value;
        return state != null && state.Segnalazioni.TryGetValue(authoritative.Uid, out var working)
            ? EntityStateFingerprint.Clone(working)
            : EntityStateFingerprint.Clone(authoritative);
    }

    private sealed class State
    {
        public Dictionary<Guid, NonConformita> NonConformita { get; } = new();
        public Dictionary<Guid, SegnalazioneDaClassificare> Segnalazioni { get; } = new();
    }

    private sealed class Scope : IDisposable
    {
        private State? _state;
        public Scope(State state) => _state = state;

        public void Dispose()
        {
            if (_state == null) return;
            if (ReferenceEquals(Current.Value, _state)) Current.Value = null;
            _state = null;
        }
    }
}

