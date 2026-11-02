using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

public enum StatoAssociazioneDtcRegistro
{
    RigaNonValida,
    RigaStorica,
    RequisitoNonCompatibile
}

public sealed class DtcRegistroAssociazioneException(StatoAssociazioneDtcRegistro stato, string message)
    : InvalidOperationException(message)
{
    public StatoAssociazioneDtcRegistro Stato { get; } = stato;
}

public sealed record EsitoAssociazioneDtcRegistro(string RequisitoId, int NumeroNuovaRevisione, long RevisioneDati);

/// <summary>
/// Associazione retroattiva di una riga guasto corrente a un requisito Extra già presente. Le REV storiche restano
/// immutabili: TestCaseRepository salva la modifica con il normale journal e TestRevisionSafety la registra come
/// nuova REV. Le scelte sono limitate alle tuple che l'Audit può abbinare per tutti i componenti dell'operazione.
/// Dalla 6.8.0 un requisito Extra senza quei guasto si può scegliere comunque: l'associazione aggiunge prima le voci
/// mancanti all'elenco guasto del requisito (una scrittura atomica) e poi collega la riga; l'associazione si può fare
/// anche in blocco e partendo dal requisito.
/// </summary>
public sealed class DtcRegistroAssociazioneService(AppState state)
{
    private readonly AppState _state = state ?? throw new ArgumentNullException(nameof(state));

    public IReadOnlyList<Requisito> RequisitiCompatibili(RigaDtcRegistro riga)
    {
        var (test, condizione) = CaricaRigaCorrente(riga);
        return ElencaCompatibili(test, condizione, _state.Master.Requisiti.Load());
    }

    public EsitoAssociazioneDtcRegistro Associa(RigaDtcRegistro riga, string requisitoId)
    {
        if (!_state.PuoScrivere)
            throw new UnauthorizedAccessException("Workspace in sola lettura o senza un operatore abilitato.");

        var (test, condizione) = CaricaRigaCorrente(riga);
        var id = (requisitoId ?? "").Trim();
        var requisito = ElencaCompatibili(test, condizione, _state.Master.Requisiti.Load())
            .FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (requisito == null)
            throw new DtcRegistroAssociazioneException(StatoAssociazioneDtcRegistro.RequisitoNonCompatibile,
                $"Il requisito {id} non contiene una tupla guasto compatibile con questa operazione.");

        return ApplicaAssociazioni(test, new[] { (condizione, requisito) });
    }

    /// <summary>Un requisito Extra proposto per l'associazione: <see cref="Compatibile"/> se contiene già la tupla guasto/componente/veicolo della riga
    /// (si associa subito), altrimenti l'associazione aggiunge prima il guasto all'elenco del requisito.</summary>
    public sealed record CandidatoRequisito(Requisito Requisito, bool Compatibile, int NumeroDtc);

    /// <summary>Tutti i requisiti Extra non archiviati con il numero di guasto che contengono, per scegliere a quale associare. Prima quelli già
    /// compatibili con la riga, poi quelli senza alcun guasto, poi gli altri, ciascun gruppo in ordine naturale.</summary>
    public IReadOnlyList<CandidatoRequisito> Candidati(RigaDtcRegistro? riga = null)
    {
        HashSet<string> compatibili = new(StringComparer.OrdinalIgnoreCase);
        if (riga != null)
        {
            try { foreach (var r in RequisitiCompatibili(riga)) compatibili.Add(r.Id); }
            catch (DtcRegistroAssociazioneException) { /* riga non più valida: nessun requisito risulta compatibile */ }
        }
        return ElencoRequisitiD4d()
            .Select(r => new CandidatoRequisito(r.Requisito, compatibili.Contains(r.Requisito.Id), r.NumeroDtc))
            .OrderBy(c => c.Compatibile ? 0 : c.NumeroDtc == 0 ? 1 : 2)
            .ThenBy(c => c.Requisito.Id, NaturalStringComparer.Instance)
            .ToList();
    }

    /// <summary>Requisiti Extra non archiviati con il numero dei loro guasto (tutti, anche quelli ancora senza guasto).</summary>
    public IReadOnlyList<(Requisito Requisito, int NumeroDtc)> ElencoRequisitiD4d() =>
        _state.Master.Requisiti.Load()
            .Where(r => !r.Archiviata && RequisitoApplicabilita.ED4D(r))
            .Select(r => (Requisito: r, NumeroDtc: _state.Master.RequisitiDtc.Leggi(r).Voci.Count))
            .OrderBy(x => x.Requisito.Id, NaturalStringComparer.Instance)
            .ToList();

    /// <summary>Voci guasto da avere nel requisito perché la riga sia compatibile: una per ogni componente dell'operazione, col veicolo dell'operazione.</summary>
    public IReadOnlyList<DtcRequisito> TupleNecessarie(RigaDtcRegistro riga)
    {
        var (test, condizione) = CaricaRigaCorrente(riga);
        return TupleNecessarie(test, condizione);
    }

    private static List<DtcRequisito> TupleNecessarie(TestCase test, Condizione condizione)
    {
        var codice = (condizione.Guasto ?? "").Trim();
        var veicolo = DtcAvanzamentoService.VeicoloOperazione(test, condizione);
        return OperationalSemantics.EcuUnits(test, condizione)
            .Where(e => codice.Length > 0 && !string.IsNullOrWhiteSpace(e))
            .Select(e => new DtcRequisito { Codice = codice, Componente = e.Trim(), Veicolo = veicolo.Length == 0 ? null : veicolo })
            .ToList();
    }

    /// <summary>Esito dell'aggiunta di guasto a un requisito: quante voci nuove sono state scritte e quali sono state rifiutate dalle regole del
    /// requisito (veicolo non dichiarato, componente fuori anagrafica, codice non valido), ciascuna col motivo già localizzato.</summary>
    public sealed record RisultatoAggiunta(int Aggiunte, IReadOnlyList<(DtcRequisito Voce, string Errore)> Rifiutate);

    /// <summary>Aggiunge al requisito Extra le voci indicate che non ha già (stessa chiave veicolo/componente/guasto), con una sola scrittura atomica
    /// sull'archivio guasto del requisito (merge a tre vie se un altro PC lo modifica nel frattempo). Valgono le stesse regole dell'editor del
    /// requisito: le voci che non le rispettano non vengono scritte e sono riportate tra le rifiutate.</summary>
    public RisultatoAggiunta AggiungiDtcAlRequisito(Requisito requisito, IEnumerable<DtcRequisito> voci)
    {
        ArgumentNullException.ThrowIfNull(requisito);
        if (!_state.PuoScrivere)
            throw new UnauthorizedAccessException("Workspace in sola lettura o senza un operatore abilitato.");
        if (requisito.Archiviata || !RequisitoApplicabilita.ED4D(requisito))
            throw new InvalidOperationException($"Il requisito {requisito.Id} non è un requisito Extra attivo.");
        var archivio = _state.Master.RequisitiDtc;
        if (!archivio.Disponibile)
            throw new InvalidOperationException("L'archivio guasto dei requisiti non è disponibile in questo workspace.");

        var letto = archivio.Leggi(requisito);
        if (letto.Stato != DtcArchivioStato.Ok)
            throw new InvalidOperationException(letto.Avviso ?? $"L'archivio guasto del requisito {requisito.Id} non è leggibile.");
        if (letto.Embedded)
            throw new InvalidOperationException($"Il requisito {requisito.Id} ha i guasti nel formato precedente: aprirlo e salvarlo dal Master prima di aggiungere guasto.");

        var dichiarati = RequisitoApplicabilita.SottoveicoliDi(requisito);
        var veicoliAnagrafica = _state.Master.Veicoli.Load().Where(v => !v.Archiviata && !string.IsNullOrWhiteSpace(v.Nome)).Select(v => v.Nome).ToList();
        var ecuAnagrafica = _state.Master.Componente.Load().Where(e => !e.Archiviata && !string.IsNullOrWhiteSpace(e.Nome)).Select(e => e.Nome).ToList();
        var chiavi = new HashSet<string>(letto.Voci.Select(DtcRequisito.Chiave), StringComparer.OrdinalIgnoreCase);
        var nuove = new List<DtcRequisito>();
        var rifiutate = new List<(DtcRequisito, string)>();
        foreach (var v in voci)
        {
            var errore = DtcAlberoRegole.ValidaNuovaVoce(v.Veicolo, v.Componente, v.Codice, dichiarati, ecuAnagrafica, chiavi, veicoliAnagrafica);
            if (errore == null)
            {
                // Il veicolo si scrive come dichiarato dal requisito (maiuscole canoniche).
                var copia = v.Clona();
                var canonico = DtcAlberoRegole.Canonico(copia.Veicolo, DtcAlberoRegole.Ammessi(dichiarati, veicoliAnagrafica));
                copia.Veicolo = string.IsNullOrEmpty(canonico) ? null : canonico;
                chiavi.Add(DtcRequisito.Chiave(copia));
                nuove.Add(copia);
            }
            else if (errore != "DtcDuplicato") rifiutate.Add((v, LocalizationService.Instance.Traduci(errore)));
        }
        if (nuove.Count == 0) return new RisultatoAggiunta(0, rifiutate);

        var tutte = letto.Voci.Concat(nuove).ToList();
        archivio.Scrivi(new[] { new DtcScrittura(requisito, tutte, letto.Documento) }, _state.Config.OperatoreAttivo);
        return new RisultatoAggiunta(nuove.Count, rifiutate);
    }

    /// <summary>Esito per riga di un'associazione in blocco: <see cref="Esito"/> valorizzato se associata, altrimenti <see cref="Errore"/>.</summary>
    public sealed record EsitoRiga(RigaDtcRegistro Riga, EsitoAssociazioneDtcRegistro? Esito, string? Errore);

    public sealed record EsitoBlocco(IReadOnlyList<EsitoRiga> Righe, int DtcAggiuntiAlRequisito)
    {
        public int Associate => Righe.Count(r => r.Esito != null);
        public int NonAssociate => Righe.Count(r => r.Esito == null);
    }

    /// <summary>Associa più righe guasto a un requisito Extra. Con <paramref name="aggiungiDtc"/> i guasti mancanti vengono prima aggiunti al requisito
    /// (una sola scrittura per tutto l'insieme); senza, le righe non compatibili restano non associate. Le righe dello stesso Test producono
    /// UNA sola nuova REV. Ogni Test è indipendente: l'errore su una riga non annulla le altre, e nessuna REV storica viene modificata.</summary>
    public EsitoBlocco AssociaInBlocco(IReadOnlyList<RigaDtcRegistro> righe, string requisitoId, bool aggiungiDtc)
    {
        if (!_state.PuoScrivere)
            throw new UnauthorizedAccessException("Workspace in sola lettura o senza un operatore abilitato.");
        var id = (requisitoId ?? "").Trim();
        var requisito = _state.Master.Requisiti.Load()
            .FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && !r.Archiviata && RequisitoApplicabilita.ED4D(r))
            ?? throw new InvalidOperationException($"Il requisito {id} non è un requisito Extra attivo.");

        var esiti = new List<EsitoRiga>();
        var valide = new List<(RigaDtcRegistro Riga, TestCase Test, Condizione Condizione)>();
        foreach (var riga in righe)
        {
            try
            {
                var (test, condizione) = CaricaRigaCorrente(riga);
                valide.Add((riga, test, condizione));
            }
            catch (DtcRegistroAssociazioneException ex) { esiti.Add(new EsitoRiga(riga, null, ex.Message)); }
        }

        var aggiunte = 0;
        var rifiutate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (aggiungiDtc && valide.Count > 0)
        {
            var aggiunta = AggiungiDtcAlRequisito(requisito, valide.SelectMany(v => TupleNecessarie(v.Test, v.Condizione)));
            aggiunte = aggiunta.Aggiunte;
            foreach (var (voce, errore) in aggiunta.Rifiutate) rifiutate[DtcRequisito.Chiave(voce)] = errore;
        }

        // Una nuova REV per Test, con tutte le sue condizioni scelte. Il catalogo si rilegge dopo l'eventuale aggiunta dei guasti.
        var catalogo = _state.Master.Requisiti.Load();
        foreach (var gruppo in valide.GroupBy(v => v.Test.Uid))
        {
            var test = gruppo.First().Test;
            var coppie = new List<(RigaDtcRegistro Riga, Condizione Condizione, Requisito Requisito)>();
            foreach (var v in gruppo)
            {
                var compatibile = ElencaCompatibili(v.Test, v.Condizione, catalogo)
                    .FirstOrDefault(r => r.Id.Equals(requisito.Id, StringComparison.OrdinalIgnoreCase));
                if (compatibile == null)
                    esiti.Add(new EsitoRiga(v.Riga, null, TupleNecessarie(v.Test, v.Condizione).Select(t => rifiutate.GetValueOrDefault(DtcRequisito.Chiave(t))).FirstOrDefault(e => e != null)
                        ?? $"Il requisito {requisito.Id} non contiene una tupla guasto compatibile con questa operazione."));
                else coppie.Add((v.Riga, v.Condizione, compatibile));
            }
            if (coppie.Count == 0) continue;
            try
            {
                var esito = ApplicaAssociazioni(test, coppie.Select(c => (c.Condizione, c.Requisito)));
                foreach (var c in coppie) esiti.Add(new EsitoRiga(c.Riga, esito, null));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
            {
                foreach (var c in coppie) esiti.Add(new EsitoRiga(c.Riga, null, ex.Message));
            }
        }
        return new EsitoBlocco(esiti, aggiunte);
    }

    /// <summary>Copia la REV corrente del Test in una nuova REV e vi collega le condizioni al requisito. La nuova REV mantiene data e operatore
    /// dell'esecuzione originale: è una modifica del collegamento, non una nuova prova. Le REV storiche non cambiano.</summary>
    private EsitoAssociazioneDtcRegistro ApplicaAssociazioni(TestCase test, IEnumerable<(Condizione Condizione, Requisito Requisito)> coppie)
    {
        var elenco = coppie.ToList();
        // È una modifica del collegamento, non una nuova prova. La REV candidata mantiene quindi la data e
        // l'operatore dell'esecuzione originale; UltimaModifica/ModificatoDa registrano l'azione corrente.
        // Aggiungendo esplicitamente la copia, TestRevisionSafety verifica il prefisso storico e il repository
        // la salva con il journal senza riassegnare la data della prova a DateTime.Now.
        var ultima = test.Esecuzioni[^1];
        var nuova = JsonSerializer.Deserialize<Esecuzione>(JsonSerializer.Serialize(ultima, JsonIo.Options), JsonIo.Options)
            ?? throw new InvalidDataException("Impossibile copiare la REV corrente del Test.");
        nuova.Iterazione = test.Esecuzioni.Max(e => e.Iterazione) + 1;
        test.RequisitoId ??= new List<string>();
        foreach (var (condizione, requisito) in elenco)
        {
            var condizioneNuova = nuova.Condizioni.FirstOrDefault(c => c.Uid == condizione.Uid)
                ?? throw RigaNonValida("L'operazione guasto non è presente nella REV copiata.");
            condizioneNuova.DtcRequisitoId = requisito.Id;
            condizioneNuova.DtcRequisitoVuotoScelto = null;
            if (!test.RequisitoId.Contains(requisito.Id, StringComparer.OrdinalIgnoreCase))
                test.RequisitoId.Add(requisito.Id);
        }
        PunteggioConformitaService.SincronizzaPunteggi(nuova, test.RequisitoId);
        test.Esecuzioni.Add(nuova);
        test.UltimaModifica = DateTime.Now;
        test.ModificatoDa = _state.Config.OperatoreAttivo;
        _state.TestCases.Save(test, isNew: false, revisioneAttesa: test.RevisioneDati);

        return new EsitoAssociazioneDtcRegistro(elenco[0].Requisito.Id, nuova.Iterazione, test.RevisioneDati);
    }

    private (TestCase Test, Condizione Condizione) CaricaRigaCorrente(RigaDtcRegistro riga)
    {
        ArgumentNullException.ThrowIfNull(riga);
        if (!Guid.TryParse(riga.TestUid, out var uid))
            throw RigaNonValida("UID Test non valido.");
        var noto = _state.TestCases.GetByUid(uid);
        if (noto == null)
            throw RigaNonValida("Il Test non è più disponibile nell'indice locale.");
        var test = _state.TestCases.LoadCurrent(noto);
        if (test == null || test.Eliminato || test.Uid != uid || !test.Id.Equals(riga.TestId, StringComparison.OrdinalIgnoreCase))
            throw RigaNonValida("La riga del registro non corrisponde più al Test corrente.");

        if (riga.RevisioneIndice < 0 || riga.RevisioneIndice >= test.Esecuzioni.Count)
            throw RigaNonValida("La revisione della riga non esiste più nel Test.");
        if (riga.RevisioneIndice != test.Esecuzioni.Count - 1)
            throw new DtcRegistroAssociazioneException(StatoAssociazioneDtcRegistro.RigaStorica,
                "Le righe di revisioni storiche sono in sola lettura.");

        if (riga.TentativoIndice < 0)
            throw RigaNonValida("La posizione della riga nel Test non è valida.");
        var revisione = test.Esecuzioni[riga.RevisioneIndice];
        var condizione = revisione.Condizioni.ElementAtOrDefault(riga.TentativoIndice);
        if (condizione == null || !Guid.TryParse(riga.CondizioneUid, out var conditionUid) || condizione.Uid != conditionUid
            || !revisione.CondizioniEffettive.Any(c => c.Uid == conditionUid)
            || !condizione.RiguardaDtc
            || !string.IsNullOrWhiteSpace(condizione.DtcRequisitoId)
            || !string.Equals((condizione.Guasto ?? "").Trim(), riga.Guasto.Trim(), StringComparison.OrdinalIgnoreCase)
            || (condizione.DtcPasso ?? PassoDtc.Validazione) != riga.Passo
            || condizione.Esito != riga.Esito
            || !string.Equals(DtcAvanzamentoService.VeicoloOperazione(test, condizione), riga.Veicolo.Trim(), StringComparison.OrdinalIgnoreCase)
            || !OperationalSemantics.EcuUnits(test, condizione).Contains(riga.Componente.Trim(), StringComparer.OrdinalIgnoreCase))
            throw RigaNonValida("La riga guasto è cambiata dopo l'aggiornamento del registro.");

        return (test, condizione);
    }

    private List<Requisito> ElencaCompatibili(TestCase test, Condizione condizione, IReadOnlyList<Requisito> catalogo)
    {
        var codice = (condizione.Guasto ?? "").Trim();
        var veicolo = DtcAvanzamentoService.VeicoloOperazione(test, condizione);
        var componenti = OperationalSemantics.EcuUnits(test, condizione);
        if (codice.Length == 0 || componenti.Count == 0) return new List<Requisito>();

        // I requisiti archiviati non partecipano all'avanzamento Audit. i guasti embedded e quelli del catalogo Phase D
        // vengono entrambi letti dalla stessa API, sempre in sola lettura.
        var requisitiCompatibili = new List<Requisito>();
        foreach (var requisito in catalogo)
        {
            if (requisito.Archiviata || !RequisitoApplicabilita.ED4D(requisito)) continue;
            var voci = _state.Master.RequisitiDtc.Leggi(requisito).Voci;
            if (HaTuplaCompleta(voci, codice, veicolo, componenti)) requisitiCompatibili.Add(requisito);
        }
        return requisitiCompatibili
            .OrderBy(r => r.Id, NaturalStringComparer.Instance)
            .ToList();
    }

    private static bool HaTuplaCompleta(IReadOnlyList<DtcRequisito> voci, string codice, string veicolo, IReadOnlyList<string> componenti)
    {
        foreach (var componente in componenti)
        {
            var abbinate = voci.Where(v => v.Codice.Trim().Equals(codice, StringComparison.OrdinalIgnoreCase)
                    && v.Componente.Trim().Equals(componente, StringComparison.OrdinalIgnoreCase))
                .GroupBy(DtcRequisito.Chiave, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (veicolo.Length == 0)
            {
                // Senza veicolo l'Audit abbina soltanto se (requisito, componente, guasto) identifica una sola cella.
                if (abbinate.Count != 1) return false;
                continue;
            }

            var esatta = abbinate.Where(v => v.VeicoloNorm.Equals(veicolo, StringComparison.OrdinalIgnoreCase)).ToList();
            if (esatta.Count == 1) continue;
            if (esatta.Count > 1) return false;
            var senzaVeicolo = abbinate.Count(v => v.VeicoloNorm.Length == 0);
            if (senzaVeicolo != 1) return false;
        }
        return true;
    }

    private static DtcRegistroAssociazioneException RigaNonValida(string message) =>
        new(StatoAssociazioneDtcRegistro.RigaNonValida, message);
}
