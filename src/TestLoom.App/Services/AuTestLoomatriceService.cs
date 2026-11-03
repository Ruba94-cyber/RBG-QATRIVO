using TestLoom.App.Models;

namespace TestLoom.App.Services;

public enum StatoCellaAudit
{
    DaTestare,
    TestCreatoNonEseguito,
    Valutato,
    Conflitto,

    /// <summary>Piano 6.3.1 §1 ("!"): nessun Test diretto su QUESTO veicolo e nessuna valutazione salvata,
    /// ma esiste un valore per la stessa coppia Componente×Requisito su un altro veicolo della stessa componente da cui
    /// l'operatore potrebbe propagare — è la cella che la vecchia logica riempiva automaticamente. Vedi
    /// CandidatiPropagazione/ValutazioneRequisitoEcuVeicoloService.Propaga.</summary>
    PropagabileNonConfermato,
}

public class CandidatoConflitto
{
    public string TestCaseId { get; set; } = "";
    public PunteggioConformita Punteggio { get; set; }
    public DateTime Data { get; set; }
}

public class CellaMatriceAudit
{
    public string Componente { get; set; } = "";
    public string RequisitoId { get; set; } = "";
    public string Veicolo { get; set; } = "";
    public StatoCellaAudit Stato { get; set; }
    public PunteggioConformita? Punteggio { get; set; }
    public DateTime? DataEsecuzione { get; set; }
    public List<CandidatoConflitto> Candidati { get; set; } = new();
    public bool InserimentoManuale { get; set; }
    public string MotivazioneInserimentoManuale { get; set; } = "";
    public string? DettaglioConflitto { get; set; }

    /// <summary>Origine del valore quando Stato=Valutato: Diretto (segue l'esecuzione del/dei Test su questo
    /// veicolo), Manuale o Propagata (righe ValutazioneRequisitoEcuVeicolo — InserimentoManuale/
    /// MotivazioneInserimentoManuale sopra restano invece l'inserimento storico su RisoluzioneConflittoAudit,
    /// per coppia Componente×Requisito senza dimensione veicolo, invariato da questo piano).</summary>
    public OrigineCellaRequisitoEcuVeicolo? Origine { get; set; }

    /// <summary>Valorizzato quando Origine è Manuale o Propagata (ValutazioneRequisitoEcuVeicoloService.TestoTooltip)
    /// oppure quando Stato=PropagabileNonConfermato (elenco sintetico dei veicoli sorgente disponibili).</summary>
    public string? Tooltip { get; set; }
    public string? Nota { get; set; }
    public string? BaselineSw { get; set; }

    /// <summary>Popolato solo quando Stato=PropagabileNonConfermato: veicoli della stessa componente da cui questa
    /// cella potrebbe copiare un valore, con il candidato trovato per ciascuno (vedi
    /// ValutazioneRequisitoEcuVeicoloService.CandidatiPropagazione).</summary>
    public IReadOnlyDictionary<string, CandidatoPropagazioneRequisito> CandidatiPropagazione { get; set; } =
        new Dictionary<string, CandidatoPropagazioneRequisito>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vedi RisoluzioneCellaRequisitoEcuVeicolo.ConflittoCollisioneCella: valorizzato solo quando
    /// Stato=Valutato e Origine è Manuale/Propagata, true se la cella ha una collisione di chiave business
    /// Phase D non ancora riparata con valori discordanti (fix 6.4.0) — la pagina Audit la mostra come
    /// conflitto da rivedere, non come un valore ordinario.</summary>
    public bool ConflittoCollisioneCella { get; set; }

    /// <summary>Requisito Extra relativo ad altre componente (coppie veicolo-componente, RequisitoApplicabilita.SiApplicaAllaEcu): la cella e'
    /// N.A. (Stato resta DaTestare, solo per compatibilita') e non entra nei KPI. Derivato dai dati, mai persistito.</summary>
    public bool NonApplicabileEcu { get; set; }
}

/// <summary>
/// Matrice completa Requisito x componente per un sottoveicolo. Requisiti Standard sono cross e quindi compaiono
/// per ogni sottoveicolo; un Extra compare soltanto nei sottoveicoli dichiarati e, in quel caso, considera
/// esclusivamente Test che includono davvero quel sottoveicolo. I conflitti di punteggio restano espliciti e
/// risolvibili solo tramite la risoluzione Master esistente.
/// </summary>
public static class AuditMatriceService
{
    public static List<string> EcuDelSottoveicolo(IReadOnlyList<Componente> tutteLeEcu, string veicoloNome) =>
        tutteLeEcu.Where(e => !e.Archiviata && e.VeicoliAssociati.Contains(veicoloNome))
            .Select(e => e.Nome).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Toglie dalle colonne i componenti le cui celle sono tutte N.A. (requisiti Extra relativi ad altre componente). I componenti senza celle
    /// (nessun requisito) restano: nessun cambio rispetto a prima.</summary>
    public static List<string> EcuConAlmenoUnaCellaApplicabile(IReadOnlyList<string> componente, IEnumerable<CellaMatriceAudit> celle)
    {
        var conCelle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applicabili = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in celle)
        {
            conCelle.Add(c.Componente);
            if (!c.NonApplicabileEcu) applicabili.Add(c.Componente);
        }
        return componente.Where(e => !conCelle.Contains(e) || applicabili.Contains(e)).ToList();
    }

    public static Dictionary<(string Componente, string ReqId), List<TestCase>> IndicizzaTestPerEcuERequisito(IReadOnlyList<TestCase> testCases)
    {
        var perCoppia = new Dictionary<(string Componente, string ReqId), List<TestCase>>();
        foreach (var tc in testCases)
        foreach (var componente in tc.Componente)
        foreach (var reqId in tc.RequisitoId)
        {
            var chiave = (componente, reqId);
            if (!perCoppia.TryGetValue(chiave, out var lista)) perCoppia[chiave] = lista = new List<TestCase>();
            lista.Add(tc);
        }
        return perCoppia;
    }

    /// <summary>Candidati diretti (Test collegati a QUESTO veicolo, con l'ultimo punteggio della loro
    /// esecuzione più recente per il requisito) per una coppia Componente×Requisito già indicizzata. Condiviso da
    /// Matrice/CalcolaKpiComplessivi e da ValutazioneRequisitoEcuVeicoloService.RisolviCella per non duplicare
    /// la scansione Esecuzioni/PunteggiRequisiti (hot path, gate 700 ms/100k — un solo posto dove questa
    /// scansione vive, invece di due copie che potrebbero divergere).</summary>
    public static List<CandidatoConflitto> CandidatiDiretti(string requisitoId, IEnumerable<TestCase> testDellaCoppiaEcuRequisito, string veicoloNome)
    {
        var candidati = new List<CandidatoConflitto>();
        foreach (var tc in testDellaCoppiaEcuRequisito)
        {
            if (!tc.VeicoliEffettivi.Contains(veicoloNome, StringComparer.OrdinalIgnoreCase)) continue;

            Esecuzione? ultima = null;
            PunteggioConformita? pVal = null;
            foreach (var e in tc.Esecuzioni)
            {
                foreach (var pr in e.PunteggiRequisiti)
                {
                    if (pr.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ultima == null || e.Data > ultima.Data)
                        {
                            ultima = e;
                            pVal = pr.Valore;
                        }
                        break;
                    }
                }
            }
            if (ultima != null && pVal.HasValue)
            {
                candidati.Add(new CandidatoConflitto
                {
                    TestCaseId = tc.Id,
                    Punteggio = pVal.Value,
                    Data = ultima.Data
                });
            }
        }
        return candidati;
    }

    /// <summary>Candidati di un Test SENZA veicolo (VeicoliEffettivi vuoto — dati storici, es. la cartella
    /// legacy `_SENZA_GAMMA`: un Test creato prima che il campo Veicolo esistesse, o mai valorizzato). Piano
    /// 6.3.1 §1 (seconda consegna, chiarimento semantica): un Test così non è mai "Diretto" per nessun
    /// veicolo specifico (nessuna scansione di <see cref="CandidatiDiretti"/> lo troverebbe comunque, dato che
    /// VeicoliEffettivi è vuoto e Contains non può mai valere), ma il suo punteggio resta un dato REALE — non
    /// va perso silenziosamente. È trattato come "valore componente-generico": propagabile ("!") a QUALSIASI veicolo
    /// di quell'componente, mai diretto — vedi ValutazioneRequisitoEcuVeicoloService.CandidatiPropagazioneCore.</summary>
    public static List<CandidatoConflitto> CandidatiEcuGenerici(string requisitoId, IEnumerable<TestCase> testDellaCoppiaEcuRequisito)
    {
        var candidati = new List<CandidatoConflitto>();
        foreach (var tc in testDellaCoppiaEcuRequisito)
        {
            if (tc.VeicoliEffettivi.Count > 0) continue; // ha un veicolo: non è un caso "Componente-generico"

            Esecuzione? ultima = null;
            PunteggioConformita? pVal = null;
            foreach (var e in tc.Esecuzioni)
            {
                foreach (var pr in e.PunteggiRequisiti)
                {
                    if (pr.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ultima == null || e.Data > ultima.Data) { ultima = e; pVal = pr.Valore; }
                        break;
                    }
                }
            }
            if (ultima != null && pVal.HasValue)
                candidati.Add(new CandidatoConflitto { TestCaseId = tc.Id, Punteggio = pVal.Value, Data = ultima.Data });
        }
        return candidati;
    }

    /// <summary>Come <see cref="CandidatiDiretti"/> ma per TUTTI i veicoli in un solo passaggio su
    /// testDellaCoppiaEcuRequisito (una sola scansione Esecuzioni/PunteggiRequisiti per Test, invece di una
    /// per ciascun veicolo candidato). Da usare quando serve il risultato per più veicoli della stessa coppia
    /// Componente×Requisito nello stesso calcolo — vedi CalcolaKpiComplessivi: senza questa versione precalcolata,
    /// verificare la propagabilità di ogni cella priva di Diretto/valutazione salvata richiamava
    /// CandidatiDiretti una volta per ciascun altro veicolo di Componente.VeicoliAssociati, e lo stesso ricalcolo si
    /// ripeteva identico per OGNI veicolo che condivide quell'componente: costo quadratico nel numero di veicoli
    /// condivisi da un componente (review 2026-09-24, HIGH) invece che lineare nel numero di Test.</summary>
    public static Dictionary<string, List<CandidatoConflitto>> CandidatiDirettiPerVeicolo(string requisitoId, IEnumerable<TestCase> testDellaCoppiaEcuRequisito)
    {
        var perVeicolo = new Dictionary<string, List<CandidatoConflitto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in testDellaCoppiaEcuRequisito)
        {
            Esecuzione? ultima = null;
            PunteggioConformita? pVal = null;
            foreach (var e in tc.Esecuzioni)
            {
                foreach (var pr in e.PunteggiRequisiti)
                {
                    if (pr.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ultima == null || e.Data > ultima.Data)
                        {
                            ultima = e;
                            pVal = pr.Valore;
                        }
                        break;
                    }
                }
            }
            if (ultima == null || !pVal.HasValue) continue;

            var candidato = new CandidatoConflitto { TestCaseId = tc.Id, Punteggio = pVal.Value, Data = ultima.Data };
            foreach (var veicolo in tc.VeicoliEffettivi)
            {
                if (!perVeicolo.TryGetValue(veicolo, out var lista)) perVeicolo[veicolo] = lista = new();
                lista.Add(candidato);
            }
        }
        return perVeicolo;
    }

    /// <summary>Separa i requisiti in Standard e Extra (6.5.0): la pagina Audit e l'export mostrano due liste
    /// distinte, ciascuna con i propri totali - i totali Standard non includono mai i Extra. Passando a
    /// Matrice/CalcolaKpiComplessivi solo una delle due liste si ottengono celle e KPI separati senza altro
    /// cambio di calcolo (il punteggio di conformita dei Extra e calcolato esattamente come per gli Standard).</summary>
    public static (List<Requisito> Standard, List<Requisito> Extra) SeparaPerTipo(IEnumerable<Requisito> requisiti)
    {
        var std = new List<Requisito>();
        var extra = new List<Requisito>();
        foreach (var r in requisiti)
            (RequisitoApplicabilita.ED4D(r) ? extra : std).Add(r);
        return (std, extra);
    }

    public static List<CellaMatriceAudit> Matrice(
        string veicoloNome,
        IReadOnlyList<Componente> tutteLeEcu,
        IReadOnlyList<Requisito> requisiti,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<RisoluzioneConflittoAudit>? risoluzioni = null,
        IReadOnlyDictionary<(string Componente, string ReqId), List<TestCase>>? indiceTestPerCoppia = null,
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo>? valutazioni = null)
    {
        risoluzioni ??= Array.Empty<RisoluzioneConflittoAudit>();
        valutazioni ??= Array.Empty<ValutazioneRequisitoEcuVeicolo>();
        var ecuDelSottoveicolo = EcuDelSottoveicolo(tutteLeEcu, veicoloNome);
        var ecuPerNome = tutteLeEcu.ToDictionary(e => e.Nome, StringComparer.OrdinalIgnoreCase);
        var requisitiAttivi = requisiti
            .Where(r => !r.Archiviata && RequisitoApplicabilita.SiApplicaAlSottoveicolo(r, veicoloNome))
            .OrderBy(r => r.Id, NaturalStringComparer.Instance)
            .ToList();

        var perCoppia = indiceTestPerCoppia ?? IndicizzaTestPerEcuERequisito(testCases);
        var risoluzioniPerCoppia = risoluzioni
            .GroupBy(r => (r.Componente, r.RequisitoId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UltimaModifica).First());
        // RaggruppaPerCoppia (non Indicizza): la matrice deve poter rilevare/mostrare un conflitto di
        // collisione per cella (RisolviCella.ConflittoCollisioneCella), che richiede le righe GREZZE per
        // (Componente,RequisitoId) — Indicizza le riduce già a una per veicolo perdendo l'informazione di collisione.
        // Stesso costo asintotico O(n) di Indicizza; RisolviCella fa poi la propria riduzione per veicolo.
        var valutazioniPerCoppiaGrezze = ValutazioneRequisitoEcuVeicoloService.RaggruppaPerCoppia(valutazioni);

        var risultato = new List<CellaMatriceAudit>();
        foreach (var componente in ecuDelSottoveicolo)
        foreach (var req in requisitiAttivi)
        {
            if (!RequisitoApplicabilita.SiApplicaAllaEcu(req, componente, veicoloNome))
            {
                risultato.Add(new CellaMatriceAudit { Componente = componente, RequisitoId = req.Id, Veicolo = veicoloNome, Stato = StatoCellaAudit.DaTestare, NonApplicabileEcu = true });
                continue;
            }
            var testDellaCoppia = perCoppia.TryGetValue((componente, req.Id), out var lista) ? lista : (IReadOnlyList<TestCase>)Array.Empty<TestCase>();
            var valutazioniDellaCoppia = valutazioniPerCoppiaGrezze.TryGetValue((componente, req.Id), out var righeGrezze)
                ? righeGrezze
                : new List<ValutazioneRequisitoEcuVeicolo>();
            risoluzioniPerCoppia.TryGetValue((componente, req.Id), out var risoluzioneConflittoDiretta);

            var ecuEntita = ecuPerNome.TryGetValue(componente, out var e0) ? e0 : new Componente { Nome = componente };
            var cellaRisolta = ValutazioneRequisitoEcuVeicoloService.RisolviCella(
                req.Id, ecuEntita, veicoloNome, testDellaCoppia, valutazioniDellaCoppia, risoluzioneConflittoDiretta);

            risultato.Add(MappaCella(cellaRisolta));
        }
        return risultato;
    }

    private static CellaMatriceAudit MappaCella(RisoluzioneCellaRequisitoEcuVeicolo r)
    {
        if (r.ConflittoDiretto)
        {
            return new CellaMatriceAudit
            {
                Componente = r.Componente, RequisitoId = r.RequisitoId, Veicolo = r.Veicolo,
                Stato = StatoCellaAudit.Conflitto, Candidati = r.CandidatiConflittoDiretto.ToList(),
                DettaglioConflitto = r.DettaglioConflitto,
            };
        }
        if (r.Diretto)
        {
            return new CellaMatriceAudit
            {
                Componente = r.Componente, RequisitoId = r.RequisitoId, Veicolo = r.Veicolo,
                Stato = StatoCellaAudit.Valutato, Punteggio = r.Punteggio, DataEsecuzione = r.Data,
                Origine = OrigineCellaRequisitoEcuVeicolo.Diretto,
            };
        }
        if (r.InserimentoManuale)
        {
            return new CellaMatriceAudit
            {
                Componente = r.Componente, RequisitoId = r.RequisitoId, Veicolo = r.Veicolo,
                Stato = StatoCellaAudit.Valutato, Punteggio = r.Punteggio, DataEsecuzione = r.Data,
                InserimentoManuale = true, MotivazioneInserimentoManuale = r.Nota ?? "",
                Origine = OrigineCellaRequisitoEcuVeicolo.Manuale,
                Nota = r.Nota,
                Tooltip = $"Valore manuale legacy globale aggiornato il {r.Data:dd/MM/yyyy HH:mm}: {r.Nota}",
            };
        }
        if (r.Origine is OrigineCellaRequisitoEcuVeicolo.Manuale or OrigineCellaRequisitoEcuVeicolo.Propagata)
        {
            return new CellaMatriceAudit
            {
                Componente = r.Componente, RequisitoId = r.RequisitoId, Veicolo = r.Veicolo,
                Stato = StatoCellaAudit.Valutato, Punteggio = r.Punteggio, DataEsecuzione = r.Data,
                Origine = r.Origine, Tooltip = r.Tooltip, Nota = r.Nota, BaselineSw = r.BaselineSw,
                ConflittoCollisioneCella = r.ConflittoCollisioneCella,
            };
        }
        if (r.TestCreatoNonEseguito)
        {
            return new CellaMatriceAudit { Componente = r.Componente, RequisitoId = r.RequisitoId, Veicolo = r.Veicolo, Stato = StatoCellaAudit.TestCreatoNonEseguito };
        }
        if (r.Propagabile)
        {
            return new CellaMatriceAudit
            {
                Componente = r.Componente, RequisitoId = r.RequisitoId, Veicolo = r.Veicolo,
                Stato = StatoCellaAudit.PropagabileNonConfermato,
                CandidatiPropagazione = r.CandidatiPropagazione,
                Tooltip = "Propagabile da: " + string.Join(", ", r.CandidatiPropagazione.Select(kv =>
                    $"{kv.Key} ({(kv.Value.PunteggioSorgente == PunteggioConformita.NonApplicabile ? "N/A" : ((int)kv.Value.PunteggioSorgente).ToString())})")),
            };
        }
        return new CellaMatriceAudit { Componente = r.Componente, RequisitoId = r.RequisitoId, Veicolo = r.Veicolo, Stato = StatoCellaAudit.DaTestare };
    }

    /// <summary>
    /// Calcola i KPI complessivi su tutti i veicoli senza materializzare milioni di oggetti CellaMatriceAudit in memoria.
    /// Le celle DaTestare (la stragrande maggioranza in archivi grandi) sono calcolate per differenza matematica.
    /// </summary>
    public static AuditKpiComplessivi CalcolaKpiComplessivi(
        IEnumerable<Veicolo> veicoli,
        IReadOnlyList<Componente> tutteLeEcu,
        IReadOnlyList<Requisito> requisiti,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<RisoluzioneConflittoAudit>? risoluzioni = null,
        IReadOnlyDictionary<(string Componente, string ReqId), List<TestCase>>? indiceTestPerCoppia = null,
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo>? valutazioni = null)
    {
        risoluzioni ??= Array.Empty<RisoluzioneConflittoAudit>();
        valutazioni ??= Array.Empty<ValutazioneRequisitoEcuVeicolo>();
        var perCoppia = indiceTestPerCoppia ?? IndicizzaTestPerEcuERequisito(testCases);
        var risoluzioniPerCoppia = risoluzioni
            .GroupBy(r => (r.Componente, r.RequisitoId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UltimaModifica).First());
        var valutazioniPerCoppia = ValutazioneRequisitoEcuVeicoloService.Indicizza(valutazioni);
        var valutazioniPerCoppiaGrezze = ValutazioneRequisitoEcuVeicoloService.RaggruppaPerCoppia(valutazioni);
        var ecuEntitaPerNome = tutteLeEcu.ToDictionary(e => e.Nome, StringComparer.OrdinalIgnoreCase);

        var ecuPerVeicolo = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var componente in tutteLeEcu)
        {
            if (componente.Archiviata) continue;
            foreach (var v in componente.VeicoliAssociati)
            {
                if (!ecuPerVeicolo.TryGetValue(v, out var list)) ecuPerVeicolo[v] = list = new();
                list.Add(componente.Nome);
            }
        }

        var reqsConDatiPerEcu = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in perCoppia.Keys)
        {
            if (!reqsConDatiPerEcu.TryGetValue(key.Componente, out var set)) reqsConDatiPerEcu[key.Componente] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(key.ReqId);
        }
        foreach (var key in risoluzioniPerCoppia.Keys)
        {
            if (!reqsConDatiPerEcu.TryGetValue(key.Componente, out var set)) reqsConDatiPerEcu[key.Componente] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(key.RequisitoId);
        }
        foreach (var key in valutazioniPerCoppiaGrezze.Keys)
        {
            if (!reqsConDatiPerEcu.TryGetValue(key.Componente, out var set)) reqsConDatiPerEcu[key.Componente] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(key.ReqId);
        }

        var requisitiMap = requisiti.Where(r => !r.Archiviata).ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var standardReqsCount = requisiti.Count(r => !r.Archiviata && !RequisitoApplicabilita.ED4D(r));
        var d4dReqs = requisiti.Where(r => !r.Archiviata && RequisitoApplicabilita.ED4D(r)).ToList();

        int totaleValutate = 0;
        int totaleTestCreato = 0;
        int totaleConflitto = 0;
        int totalePropagabili = 0;
        long totaleCelle = 0;
        // Cache per (Componente, RequisitoId) dei candidati diretti per veicolo (review 2026-09-24, HIGH): senza,
        // ogni veicolo che condivide un componente rifaceva da zero la scansione di testDellaCoppia per valutare la
        // propagabilità di TUTTI gli altri veicoli della stessa componente — costo quadratico nel numero di veicoli
        // condivisi. Calcolata pigramente alla prima cella (componente,req) che la richiede, poi riusata da ogni
        // veicolo successivo che condivide la stessa coppia.
        var direttiPerVeicoloCache = new Dictionary<(string Componente, string ReqId), Dictionary<string, List<CandidatoConflitto>>>();
        // Stessa idea, per i Test "Componente-generici" (senza veicolo — Piano 6.3.1 §1 seconda consegna): calcolata
        // una sola volta per (Componente,RequisitoId), non per ogni veicolo che valuta la propagabilità.
        var genericiEcuCache = new Dictionary<(string Componente, string ReqId), List<CandidatoConflitto>>();
        double sommaPunti = 0;
        int conteggioPunti = 0;

        foreach (var v in veicoli)
        {
            if (v.Archiviata) continue;
            if (!ecuPerVeicolo.TryGetValue(v.Nome, out var ecuList) || ecuList.Count == 0) continue;

            int reqsVeicoloCount = standardReqsCount;
            long celleD4dRistrette = 0;
            for (int i = 0; i < d4dReqs.Count; i++)
            {
                if (!RequisitoApplicabilita.SiApplicaAlSottoveicolo(d4dReqs[i], v.Nome)) continue;
                if (d4dReqs[i].VeicoliEcuD4D is { Count: > 0 })
                {
                    // Requisito relativo ad alcune componente: le celle verso le altre sono N.A. e non contano nel totale.
                    var req4 = d4dReqs[i];
                    celleD4dRistrette += ecuList.Count(e => RequisitoApplicabilita.SiApplicaAllaEcu(req4, e, v.Nome));
                    continue;
                }
                reqsVeicoloCount++;
            }

            totaleCelle += (long)ecuList.Count * reqsVeicoloCount + celleD4dRistrette;

            foreach (var componente in ecuList)
            {
                if (!reqsConDatiPerEcu.TryGetValue(componente, out var reqIds)) continue;

                foreach (var reqId in reqIds)
                {
                    if (!requisitiMap.TryGetValue(reqId, out var req)) continue;
                    if (RequisitoApplicabilita.ED4D(req) && !RequisitoApplicabilita.SiApplicaAlSottoveicolo(req, v.Nome)) continue;
                    if (!RequisitoApplicabilita.SiApplicaAllaEcu(req, componente, v.Nome)) continue; // N.A.: fuori dai KPI

                    var chiaveCoppiaAudit = (componente, req.Id);
                    var testDellaCoppiaAudit = perCoppia.TryGetValue(chiaveCoppiaAudit, out var testAudit)
                        ? testAudit : (IReadOnlyList<TestCase>)Array.Empty<TestCase>();
                    var valutazioniCoppiaAudit = valutazioniPerCoppiaGrezze.GetValueOrDefault(chiaveCoppiaAudit)
                        ?? new List<ValutazioneRequisitoEcuVeicolo>();
                    risoluzioniPerCoppia.TryGetValue(chiaveCoppiaAudit, out var risoluzioneAudit);
                    var risultatoConPunteggio = ValutazioneRequisitoEcuVeicoloService.RisolviValoreCella(
                        req.Id, ecuEntitaPerNome.GetValueOrDefault(componente) ?? new Componente { Nome = componente }, v.Nome,
                        testDellaCoppiaAudit, valutazioniCoppiaAudit, risoluzioneAudit);
                    if (risultatoConPunteggio != null)
                    {
                        if (risultatoConPunteggio.ConflittoDiretto)
                        {
                            totaleConflitto++;
                            continue;
                        }
                        totaleValutate++;
                        if (risultatoConPunteggio.Punteggio is { } punteggioValutato && punteggioValutato != PunteggioConformita.NonApplicabile)
                        {
                            sommaPunti += (int)punteggioValutato;
                            conteggioPunti++;
                        }
                        continue;
                    }

                    if (testDellaCoppiaAudit.Count == 0 &&
                        !valutazioniPerCoppiaGrezze.ContainsKey(chiaveCoppiaAudit))
                    {
                        continue;
                    }

                    var testDellaCoppia = testDellaCoppiaAudit;

                    PunteggioConformita? primoPunteggio = null;
                    bool haConflittoPunti = false;
                    int numeroCandidati = 0;
                    string? primoTcId = null;
                    List<(string TcId, PunteggioConformita Punteggio)>? listaCandidatiConflitto = null;

                    foreach (var tc in testDellaCoppia)
                    {
                        // Filtro veicolo NON piu' limitato ai soli requisiti Extra (Piano 6.3.1 §1): un Test su
                        // un altro veicolo della stessa componente non vale piu' automaticamente anche per questo —
                        // quella era la propagazione silenziosa che la matrice ora sostituisce con lo stato "!".
                        if (!tc.VeicoliEffettivi.Contains(v.Nome, StringComparer.OrdinalIgnoreCase))
                            continue;

                        Esecuzione? ultima = null;
                        PunteggioConformita? pVal = null;
                        foreach (var e in tc.Esecuzioni)
                        {
                            foreach (var pr in e.PunteggiRequisiti)
                            {
                                if (pr.RequisitoId.Equals(req.Id, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (ultima == null || e.Data > ultima.Data)
                                    {
                                        ultima = e;
                                        pVal = pr.Valore;
                                    }
                                    break;
                                }
                            }
                        }

                        if (ultima == null || !pVal.HasValue) continue;

                        numeroCandidati++;
                        if (numeroCandidati == 1)
                        {
                            primoPunteggio = pVal.Value;
                            primoTcId = tc.Id;
                        }
                        else if (pVal.Value != primoPunteggio)
                        {
                            haConflittoPunti = true;
                            listaCandidatiConflitto ??= new List<(string TcId, PunteggioConformita Punteggio)> { (primoTcId!, primoPunteggio!.Value) };
                            listaCandidatiConflitto.Add((tc.Id, pVal.Value));
                        }
                    }

                    if (numeroCandidati == 0)
                    {
                        bool haTestCreato = testDellaCoppia.Any(tc => tc.VeicoliEffettivi.Contains(v.Nome, StringComparer.OrdinalIgnoreCase));
                        if (haTestCreato) { totaleTestCreato++; continue; }

                        if (valutazioniPerCoppia.TryGetValue((componente, req.Id), out var valMap) && valMap.TryGetValue(v.Nome, out var valSalvata))
                        {
                            totaleValutate++;
                            if (valSalvata.Punteggio != PunteggioConformita.NonApplicabile)
                            {
                                sommaPunti += (int)valSalvata.Punteggio;
                                conteggioPunti++;
                            }
                            continue;
                        }

                        if (ecuEntitaPerNome.TryGetValue(componente, out var ecuEntita))
                        {
                            var ultimaValPerVeicolo = valMap ?? new Dictionary<string, ValutazioneRequisitoEcuVeicolo>(StringComparer.OrdinalIgnoreCase);
                            var chiaveCoppia = (componente, req.Id);
                            if (!direttiPerVeicoloCache.TryGetValue(chiaveCoppia, out var direttiPerVeicolo))
                                direttiPerVeicoloCache[chiaveCoppia] = direttiPerVeicolo = CandidatiDirettiPerVeicolo(req.Id, testDellaCoppia);
                            if (!genericiEcuCache.TryGetValue(chiaveCoppia, out var genericiEcu))
                                genericiEcuCache[chiaveCoppia] = genericiEcu = CandidatiEcuGenerici(req.Id, testDellaCoppia);

                            var propagabile = ValutazioneRequisitoEcuVeicoloService
                                .CandidatiPropagazione(ecuEntita, v.Nome, direttiPerVeicolo, ultimaValPerVeicolo, genericiEcu)
                                .Count > 0;
                            if (propagabile) totalePropagabili++;
                        }
                        continue;
                    }

                    if (!haConflittoPunti)
                    {
                        totaleValutate++;
                        if (primoPunteggio!.Value != PunteggioConformita.NonApplicabile)
                        {
                            sommaPunti += (int)primoPunteggio.Value;
                            conteggioPunti++;
                        }
                        continue;
                    }

                    if (risoluzioniPerCoppia.TryGetValue((componente, req.Id), out var ris) &&
                        listaCandidatiConflitto != null)
                    {
                        var indexScelto = listaCandidatiConflitto.FindIndex(c => c.TcId == ris.TestCaseIdValido);
                        if (indexScelto >= 0)
                        {
                            var scelto = listaCandidatiConflitto[indexScelto];
                            totaleValutate++;
                            if (scelto.Punteggio != PunteggioConformita.NonApplicabile)
                            {
                                sommaPunti += (int)scelto.Punteggio;
                                conteggioPunti++;
                            }
                            continue;
                        }
                    }

                    totaleConflitto++;
                }
            }
        }

        int totaleDaTestare = checked((int)Math.Max(0, totaleCelle - (totaleValutate + totaleTestCreato + totaleConflitto + totalePropagabili)));
        double perc = totaleCelle == 0 ? 0 : 100.0 * totaleValutate / totaleCelle;
        double? media = conteggioPunti == 0 ? null : sommaPunti / conteggioPunti;

        return new AuditKpiComplessivi(totaleValutate, totaleDaTestare, totaleTestCreato, totaleConflitto, perc, media, totalePropagabili);
    }
}

public sealed record AuditKpiComplessivi(
    int TotaleValutate,
    int TotaleDaTestare,
    int TotaleTestCreato,
    int TotaleInConflitto,
    double PercentualeValutato,
    double? PunteggioMedio,
    int TotalePropagabiliNonConfermati = 0);
