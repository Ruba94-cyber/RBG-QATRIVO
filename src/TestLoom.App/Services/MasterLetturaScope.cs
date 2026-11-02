namespace TestLoom.App.Services;

/// <summary>
/// Delimita una LETTURA LOGICA dei master: un refresh di pagina che interroga sei cataloghi e' una sola
/// lettura, non sei.
///
/// Perche' serve (feedback utente 2026-09-18, "le prestazioni generali sono molto pessime"): nei workspace
/// Phase D ogni <c>MasterDataRepository.Load()</c> passa da <c>PhaseDMasterMaterializer.Refresh()</c>, che
/// enumera TUTTE le sorgenti journal sulla cartella condivisa (SharePoint/OneDrive, quindi I/O di rete), apre
/// SQLite e avvia una transazione di SCRITTURA che scrive comunque la meta refreshed_utc anche quando non e'
/// cambiato nulla (schema_version invece non viene piu' riscritto ad ogni Refresh dall'ottimizzazione
/// 2026-09-25, che ha portato un Refresh no-op da ~90-190ms a ~1,5ms sul banco a piena scala). Home e Report
/// leggono sei o piu' cataloghi per ogni aggiornamento: anche a costo per-Refresh ridotto, restano sei
/// scansioni della cartella condivisa e sei transazioni di scrittura per una pagina sola, lavoro ripetuto senza
/// informazione nuova.
///
/// Oltre che piu' veloce, accorpare e' anche piu' CORRETTO: se una consegna remota arriva fra la terza e la
/// quarta Load(), oggi la pagina si compone con tre cataloghi vecchi e tre nuovi. Dentro uno scope tutti i
/// cataloghi vedono la stessa fotografia.
///
/// Semantica volutamente conservativa: lo scope e' per-thread e dura quanto una singola operazione sincrona di
/// pagina. FUORI dallo scope non cambia assolutamente nulla, quindi non esiste un modo per cui questo
/// meccanismo renda visibili dati piu' vecchi di quanto lo fossero prima. Non va mai aperto attorno a un
/// salvataggio: il percorso di scrittura deve sempre vedere lo stato condiviso piu' aggiornato.
/// </summary>
public static class MasterLetturaScope
{
    [ThreadStatic] private static int _profondita;
    [ThreadStatic] private static long _idCorrente;
    private static long _prossimoId;

    /// <summary>Identificativo della lettura logica in corso su QUESTO thread, o null se non ce n'e' una.
    /// Un thread di background non vede mai lo scope aperto dal thread UI.</summary>
    public static long? IdCorrente => _profondita > 0 ? _idCorrente : null;

    /// <summary>Apre (o annida) uno scope. L'annidamento e' consentito perche' un refresh di pagina puo'
    /// chiamare sotto-procedure che aprono a loro volta il proprio scope: vale quello piu' esterno.
    ///
    /// ATTENZIONE: essendo [ThreadStatic], Apri() e il Dispose() del suo IDisposable DEVONO eseguire sullo
    /// stesso thread OS — quindi entro un metodo interamente SINCRONO, senza alcun "await" fra i due (un
    /// "await" puo' far riprendere la continuazione su un thread diverso del pool, e il Dispose() sul thread
    /// sbagliato lascerebbe lo scope aperto per sempre su quello originale). Gli unici chiamanti attuali,
    /// HomeViewModel.Aggiorna/AggiornaDashboardCore e ReportViewModel.Aggiorna/EseguiRigenerazioniPendenti,
    /// sono e devono restare metodi sincroni per questo motivo.</summary>
    public static IDisposable Apri()
    {
        if (_profondita == 0) _idCorrente = Interlocked.Increment(ref _prossimoId);
        _profondita++;
        return new Chiusura(Environment.CurrentManagedThreadId);
    }

    /// <summary>Sospende temporaneamente la lettura logica in corso su questo thread. Serve al percorso di
    /// SCRITTURA: un Save deve sempre partire dalla fotografia piu' aggiornata dello stato condiviso, mai da
    /// un refresh accorpato. Non e' teoria — senza questa sospensione un salvataggio eseguito durante un
    /// refresh di pagina falliva con PhaseDMasterConflictException ("teste concorrenti non risolte"), perche'
    /// il Save non rivedeva le sorgenti journal prima di scrivere.
    ///
    /// Alla ripresa la lettura riceve un id NUOVO: dopo una scrittura, tutto cio' che viene letto nel resto
    /// dell'operazione deve poter vedere quello che e' appena stato salvato.</summary>
    public static IDisposable Sospendi()
    {
        var profondita = _profondita;
        _profondita = 0;
        return new Ripresa(Environment.CurrentManagedThreadId, profondita);
    }

    private sealed class Ripresa : IDisposable
    {
        private readonly int _thread;
        private readonly int _profonditaPrecedente;
        private bool _ripreso;

        public Ripresa(int thread, int profonditaPrecedente)
        {
            _thread = thread;
            _profonditaPrecedente = profonditaPrecedente;
        }

        public void Dispose()
        {
            if (_ripreso || _thread != Environment.CurrentManagedThreadId) return;
            _ripreso = true;
            _profondita = _profonditaPrecedente;
            if (_profondita > 0) _idCorrente = Interlocked.Increment(ref _prossimoId);
        }
    }

    private sealed class Chiusura : IDisposable
    {
        private readonly int _thread;
        private bool _chiuso;

        public Chiusura(int thread) => _thread = thread;

        public void Dispose()
        {
            // Una chiusura che arrivasse da un thread diverso decrementerebbe il contatore di QUEL thread,
            // lasciando aperto per sempre lo scope di questo: meglio ignorarla che corrompere il conteggio.
            if (_chiuso || _thread != Environment.CurrentManagedThreadId) return;
            _chiuso = true;
            if (_profondita > 0) _profondita--;
        }
    }
}
