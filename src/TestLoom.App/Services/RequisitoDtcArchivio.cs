using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

public enum DtcArchivioStato
{
    /// <summary>Letto (o guasto embedded / nessun guasto atteso).</summary>
    Ok = 0,
    /// <summary>Il requisito dichiara guasto ma il documento di archivio non esiste (ancora non consegnato o perso).</summary>
    Assente = 1,
    /// <summary>Il documento esiste ma non e leggibile (corrotto/troncato/scritto da una versione incompatibile).</summary>
    Illeggibile = 2,
}

/// <summary>Esito della lettura dei guasti di un requisito. <see cref="Documento"/> e la baseline da passare alla scrittura
/// (null = nessun documento esistente).</summary>
public sealed class DtcArchivioLetto
{
    public List<DtcRequisito> Voci { get; init; } = new();
    public DtcArchivioStato Stato { get; init; }
    public string? Avviso { get; init; }
    public DtcArchivioRequisito? Documento { get; init; }
    /// <summary>i guasti provengono dal campo embedded del requisito (formato precedente all'archivio).</summary>
    public bool Embedded { get; init; }
}

/// <summary>Scrittura dell'archivio guasto di un requisito: nuove voci + documento letto in precedenza (baseline).</summary>
public sealed record DtcScrittura(Requisito Requisito, IReadOnlyList<DtcRequisito> Voci, DtcArchivioRequisito? Baseline);

/// <summary>
/// Archivio SEPARATO dei guasti dei requisiti Extra (6.5.0): un documento per requisito nel catalogo Phase D
/// "requisiti-guasto", scritto con il motore master di sempre (journal append atomico, version vector per-Uid, tombstone,
/// merge/conflitto per-Uid). Il Requisito porta solo <see cref="Requisito.DtcConteggio"/> (suggerimento). Letture lazy per
/// requisito (nessuna materializzazione dell'intero archivio); nei workspace non ancora Phase D resta il formato
/// embedded (<see cref="Requisito.Guasto"/>). Nessuna eccezione di lettura risale: documento mancante/illeggibile =>
/// requisito senza guasto + avviso.
/// </summary>
public sealed class RequisitoDtcArchivio
{
    public const string Catalogo = "requisiti-guasto";

    private readonly DataPaths _paths;
    private readonly string? _localBase;
    private PhaseDMasterScalableReader? _reader;
    private PhaseDMasterRepositoryEngine<DtcArchivioRequisito>? _engine;
    private readonly object _gate = new();

    public RequisitoDtcArchivio(DataPaths paths, string? localBaseDirectory = null)
    {
        _paths = paths;
        _localBase = localBaseDirectory;
    }

    /// <summary>Vero se il workspace e Phase D (l'archivio usa il journal condiviso).</summary>
    public bool Disponibile => PhaseDMasterJournalService.IsInitialized(_paths.Root);

    public static bool TryUid(Requisito r, out Guid uid) => MasterEntityIdentityService.TryGet(r, out uid) && uid != Guid.Empty;

    private PhaseDMasterScalableReader Reader { get { lock (_gate) return _reader ??= new PhaseDMasterScalableReader(_paths.Root, _localBase); } }
    private PhaseDMasterRepositoryEngine<DtcArchivioRequisito> Engine { get { lock (_gate) return _engine ??= new PhaseDMasterRepositoryEngine<DtcArchivioRequisito>(_paths.Root, Catalogo, null, _localBase); } }

    /// <summary>Legge i guasti del requisito: embedded se presente, altrimenti dal documento di archivio. Non lancia.</summary>
    public DtcArchivioLetto Leggi(Requisito r)
    {
        if (r.Guasto != null)
            return new DtcArchivioLetto { Voci = r.Guasto.Select(Copia).ToList(), Stato = DtcArchivioStato.Ok, Embedded = true };
        if (!Disponibile || !TryUid(r, out var uid)) return new DtcArchivioLetto { Stato = DtcArchivioStato.Ok };
        try
        {
            var attivo = Reader.LoadActiveEntity<DtcArchivioRequisito>(Catalogo, uid);
            if (attivo == null)
                return (r.DtcConteggio ?? 0) > 0
                    ? new DtcArchivioLetto { Stato = DtcArchivioStato.Assente, Avviso = $"Archivio guasto del requisito {r.Id} mancante: il requisito si apre senza guasto." }
                    : new DtcArchivioLetto { Stato = DtcArchivioStato.Ok };
            var doc = attivo.Item;
            doc.Voci ??= new List<DtcRequisito>();
            doc.Voci.RemoveAll(v => v == null);
            return new DtcArchivioLetto { Voci = doc.Voci.Select(Copia).ToList(), Stato = DtcArchivioStato.Ok, Documento = doc };
        }
        catch (Exception ex)
        {
            CrashLogService.Registra($"Archivio guasto requisito {r.Id} illeggibile", ex);
            return new DtcArchivioLetto { Stato = DtcArchivioStato.Illeggibile, Avviso = $"Archivio guasto del requisito {r.Id} non leggibile: il requisito si apre senza guasto (nessun dato e stato modificato)." };
        }
    }

    /// <summary>Solo le voci (per i calcoli); come <see cref="Leggi"/> ma senza metadati.</summary>
    public List<DtcRequisito> Voci(Requisito r) => Leggi(r).Voci;

    private static DtcRequisito Copia(DtcRequisito v) => v.Clona();

    /// <summary>Tentativi di merge a 3 vie per voce quando un altro PC ha modificato lo STESSO requisito dopo la baseline.</summary>
    public const int TentativiMerge = 4;

    private static string Chiave(DtcRequisito v) => DtcRequisito.Chiave(v);

    private sealed class Lavoro
    {
        public required Requisito Requisito { get; init; }
        public required Guid Uid { get; init; }
        public required List<DtcRequisito> Locali { get; init; }
        public DtcArchivioRequisito? Baseline { get; set; }
        public required List<DtcRequisito> BaseVoci { get; init; }
        public DtcArchivioRequisito? Desiderato { get; set; }
        public bool NonScrivere { get; set; }
        /// <summary>Creazione (nessuna baseline): se l'Uid ha una testa tombstone (requisito ripristinato/reimportato con lo stesso Uid) l'archivio si ricrea sopra il tombstone.</summary>
        public bool RicreaSuTombstone { get; init; }
        public DtcArchivioRequisito? Esito { get; set; }
    }

    private static DtcArchivioRequisito NuovoDocumento(Requisito r, Guid uid, IEnumerable<DtcRequisito> voci, string operatore, DateTime ora) => new()
    {
        Id = uid.ToString("N"), RequisitoId = r.Id,
        Voci = voci.Select(Copia).ToList(), UltimaModifica = ora, ModificatoDa = operatore,
        ExtensionData = new Dictionary<string, JsonElement> { [MasterEntityIdentityService.ExtensionKey] = JsonSerializer.SerializeToElement(uid.ToString("N")) }
    };

    /// <summary>Scrive in un'unica operazione atomica (tutto o niente, una sola riga journal) gli archivi dei soli requisiti indicati. Se un altro PC ha modificato lo STESSO requisito dopo la
    /// baseline (MasterDataConflictException del motore) NON si perde la modifica: si rilegge il remoto e si riapplica la
    /// differenza locale rispetto alla baseline (aggiunte e rimozioni per chiave Veicolo|componente|Codice, merge a 3 vie) sul remoto, poi si
    /// salva di nuovo, fino a <see cref="TentativiMerge"/> volte; se il conflitto persiste l'eccezione risale (la copia di lavoro
    /// del chiamante resta intatta). Copre anche la creazione concorrente (baseline null ma documento gia presente). Ritorna i
    /// documenti scritti (nuove baseline, con le voci FUSE) per Uid.</summary>
    public Dictionary<Guid, DtcArchivioRequisito> Scrivi(IReadOnlyList<DtcScrittura> scritture, string operatore, CancellationToken ct = default)
    {
        var risultato = new Dictionary<Guid, DtcArchivioRequisito>();
        if (scritture.Count == 0) return risultato;
        if (!Disponibile) throw new InvalidOperationException("Archivio guasto non disponibile: workspace non Phase D.");
        var ora = DateTime.Now;
        var lavori = new List<Lavoro>();
        foreach (var s in scritture)
        {
            if (!TryUid(s.Requisito, out var uid)) throw new InvalidOperationException($"Requisito {s.Requisito.Id} senza Uid tecnico.");
            if (s.Baseline == null && s.Voci.Count == 0) continue; // niente da creare
            lavori.Add(new Lavoro
            {
                Requisito = s.Requisito, Uid = uid, Locali = s.Voci.Select(Copia).ToList(), Baseline = s.Baseline, RicreaSuTombstone = s.Baseline == null,
                BaseVoci = (s.Baseline?.Voci ?? new List<DtcRequisito>()).Select(Copia).ToList(),
                Desiderato = NuovoDocumento(s.Requisito, uid, s.Voci, operatore, ora)
            });
        }
        // 6.6.0 (import guasto atomico): TUTTO il set va in UNA sola SavePatch, cioe' in UNA sola riga del journal Phase D.
        // Prima i batch da 25 accodavano una riga per batch: un guasto al batch k lasciava scritti i batch 1..k-1, e il
        // journal non ha rollback (non e' nella WorkflowTransaction). Cosi' un guasto o un conflitto prima dell'append non
        // scrive nulla, e il retry e' idempotente: documenti gia' pubblicati = nessuna nuova mutazione (nessun duplicato).
        ct.ThrowIfCancellationRequested();
        for (var tentativo = 1; ; tentativo++)
        {
            try
            {
                var d = lavori.Where(l => !l.NonScrivere).Select(l => l.Desiderato!).ToList();
                var b = lavori.Where(l => !l.NonScrivere && l.Baseline != null).Select(l => l.Baseline!).ToList();
                // allowSilentRemoteMerge: solo le Uid toccate contano (un altro PC su un ALTRO requisito non confligge); la
                // stessa Uid modificata altrove dopo la baseline => MasterDataConflictException, gestita col merge sotto.
                var ricrea = lavori.Where(l => !l.NonScrivere && l.RicreaSuTombstone).Select(l => l.Uid).ToHashSet();
                if (d.Count > 0) Engine.SavePatch(d, b, allowSilentRemoteMerge: true, recreateOverTombstoneUids: ricrea);
                break;
            }
            catch (MasterDataConflictException) when (tentativo < TentativiMerge)
            {
                // Il conflitto e' rilevato PRIMA dell'append: nulla e' stato scritto, si riconcilia l'intero set e si riprova.
                foreach (var l in lavori) RiconciliaConRemoto(l, operatore, ora);
            }
        }
        foreach (var l in lavori) risultato[l.Uid] = l.NonScrivere ? l.Esito! : l.Desiderato!;
        return risultato;
    }

    /// <summary>Merge a 3 vie per voce: risultato = (remoto + aggiunte locali) - rimozioni locali, con aggiunte/rimozioni calcolate
    /// rispetto alla baseline letta all'apertura. Il remoto riletto diventa la nuova baseline.</summary>
    private void RiconciliaConRemoto(Lavoro l, string operatore, DateTime ora)
    {
        DtcArchivioRequisito? remoto;
        try { remoto = Reader.LoadActiveEntity<DtcArchivioRequisito>(Catalogo, l.Uid)?.Item; }
        catch (PhaseDMasterConflictException) { return; } // testa in conflitto di journal: non risolvibile qui, il retry rilancia
        remoto?.Voci?.RemoveAll(v => v == null);
        if (remoto != null) remoto.Voci ??= new List<DtcRequisito>();
        var baseKeys = l.BaseVoci.Select(Chiave).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localKeys = l.Locali.Select(Chiave).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rimosse = baseKeys.Where(k => !localKeys.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aggiunte = l.Locali.Where(v => !baseKeys.Contains(Chiave(v))).ToList();
        var fuse = new List<DtcRequisito>();
        var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in (remoto?.Voci ?? new List<DtcRequisito>()).Concat(aggiunte))
        {
            var k = Chiave(v);
            if (rimosse.Contains(k) || !viste.Add(k)) continue;
            fuse.Add(Copia(v));
        }
        l.Baseline = remoto;
        l.Locali.Clear(); l.Locali.AddRange(fuse);
        l.BaseVoci.Clear(); l.BaseVoci.AddRange((remoto?.Voci ?? new List<DtcRequisito>()).Select(Copia));
        if (remoto != null && fuse.Count == remoto.Voci.Count && viste.SetEquals(remoto.Voci.Select(Chiave)))
        {
            l.NonScrivere = true; l.Esito = remoto; // l'altro PC ha gia pubblicato il nostro risultato
            return;
        }
        if (remoto == null && fuse.Count == 0) { l.NonScrivere = true; l.Esito = NuovoDocumento(l.Requisito, l.Uid, fuse, operatore, ora); return; }
        l.NonScrivere = false;
        l.Desiderato = NuovoDocumento(l.Requisito, l.Uid, fuse, operatore, ora);
    }

    /// <summary>Elimina (tombstone) l'archivio guasto di un requisito rimosso/unito: nessun archivio orfano. Idempotente; non fa nulla
    /// se il documento non esiste. Un conflitto di journal risale (il chiamante decide se e best-effort).</summary>
    public void Rimuovi(Requisito r, string operatore)
    {
        if (!Disponibile || !TryUid(r, out var uid)) return;
        var attivo = Reader.LoadActiveEntity<DtcArchivioRequisito>(Catalogo, uid);
        if (attivo == null) return;
        Engine.SavePatch(Array.Empty<DtcArchivioRequisito>(), new[] { attivo.Item }, allowSilentRemoteMerge: true);
    }

    /// <summary>Unione requisiti Extra: porta nella destinazione i guasti dell'origine (chiave Veicolo|componente|Codice, nessun duplicato) scrivendo
    /// l'archivio della destinazione PRIMA del commit del catalogo (stesso schema atomico di Salva: se l'unione fallisce
    /// l'archivio dell'origine e intatto e quello della destinazione e solo un sovrainsieme, rieseguibile). Allinea in memoria
    /// DtcConteggio/Guasto della destinazione. L'archivio dell'origine va poi tolto con <see cref="Rimuovi"/> a commit riuscito.
    /// Ritorna true se la destinazione e stata modificata.</summary>
    public bool UnisciInDestinazione(Requisito scartato, Requisito corretto, string operatore)
    {
        var a = Leggi(scartato);
        if (a.Voci.Count == 0) return false;
        var b = Leggi(corretto);
        var unione = b.Voci.Select(Copia).ToList();
        var chiavi = unione.Select(Chiave).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in a.Voci) if (chiavi.Add(Chiave(v))) unione.Add(Copia(v));
        if (!Disponibile || !TryUid(corretto, out var uid))
        {
            corretto.Guasto = unione;
            return unione.Count != b.Voci.Count;
        }
        var scritti = Scrivi(new[] { new DtcScrittura(corretto, unione, b.Documento) }, operatore);
        var finale = scritti.TryGetValue(uid, out var doc) ? doc.Voci : unione;
        corretto.Guasto = null;
        corretto.DtcConteggio = finale.Count == 0 ? null : finale.Count;
        return true;
    }

    private Dictionary<Guid, int>? _overlay;
    private string? _overlayFingerprint;

    /// <summary>Conteggi guasto per Uid letti dagli archivi con UNA query SQL che non materializza i payload (json_array_length),
    /// cache in memoria invalidata dall'impronta del catalogo (revisione+conteggio, query economica). Mai autorevole.</summary>
    public IReadOnlyDictionary<Guid, int> ConteggiLeggeri() => ConteggiLeggeri(out _);

    private IReadOnlyDictionary<Guid, int> ConteggiLeggeri(out string fingerprint)
    {
        var fp = fingerprint = Reader.GetCatalogState(Catalogo).SourceFingerprint;
        lock (_gate)
        {
            if (_overlay != null && fp == _overlayFingerprint) return _overlay;
            _overlay = Reader.ContaVociPerEntita(Catalogo);
            _overlayFingerprint = fp;
            return _overlay;
        }
    }

    /// <summary>DtcConteggio null/0 e IGNOTO per i requisiti Extra: l'esistenza/dimensione dell'archivio si ricava dal conteggio leggero
    /// (overlay in memoria <see cref="Requisito.DtcConteggioRisolto"/>, non persistito) cosi selettore guasto, riepilogo Audit e
    /// letture non vengono nascosti da un suggerimento stantio. Non lancia; nessuna scrittura; una sola query SQL economica
    /// e solo se almeno un requisito ne ha bisogno.</summary>
    public void Risolvi(IEnumerable<Requisito> requisiti)
    {
        List<Requisito>? ignoti = null;
        foreach (var r in requisiti)
            if (r.Guasto == null && (r.DtcConteggio ?? 0) <= 0 && TryUid(r, out _) && RequisitoApplicabilita.ED4D(r))
                (ignoti ??= new()).Add(r);
        if (ignoti == null || !Disponibile) return;
        try
        {
            var conteggi = ConteggiLeggeri(out var fp);
            foreach (var r in ignoti)
            {
                if (r.DtcConteggioRisolto != null && r.DtcConteggioRisoltoImpronta == fp) continue; // gia risolto su questa impronta
                r.DtcConteggioRisolto = TryUid(r, out var uid) && conteggi.TryGetValue(uid, out var n) && n > 0 ? n : null;
                r.DtcConteggioRisoltoImpronta = fp;
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Conteggi guasto da archivi non disponibili", ex); }
    }

    /// <summary>Ricostruzione del suggerimento: conteggio guasto per Uid di requisito leggendo gli archivi (mai autorevole).</summary>
    public Dictionary<Guid, int> ConteggiDaArchivi()
    {
        var r = new Dictionary<Guid, int>();
        foreach (var d in Reader.EnumerateCatalog<DtcArchivioRequisito>(Catalogo))
            if (MasterEntityIdentityService.TryGet(d, out var uid)) r[uid] = d.Voci?.Count ?? 0;
        return r;
    }
}
