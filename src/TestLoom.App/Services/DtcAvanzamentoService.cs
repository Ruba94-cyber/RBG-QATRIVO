using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Stato di avanzamento di un guasto (cella guasto x componente), 6.5.0 punto 7.</summary>
public enum StatoDtc
{
    /// <summary>Nessun passo ancora concluso con esito conforme e nessun esito non conforme.</summary>
    DaFare = 0,
    /// <summary>Validazione conforme, devalidazione non ancora conforme.</summary>
    Validato = 1,
    /// <summary>Validazione E devalidazione, ciascuna con ULTIMO esito (per data) Conforme o Conforme con deviazioni.</summary>
    Completo = 2,
    /// <summary>Almeno un passo ha come ultimo esito NonConforme.</summary>
    NonConforme = 3,
}

/// <summary>Un esito di operazione guasto estratto dai Test (una Condizione eseguita con Guasto valorizzato, una riga
/// per componente coinvolta). <see cref="Ordine"/> serve solo a rendere deterministico lo spareggio a pari data.</summary>
/// <remarks>6.5.1: <see cref="Veicolo"/> e il veicolo dell'operazione (Condizione.DtcVeicolo, o l'unico veicolo del test se
/// vuoto; "" = nessuno): la cella e (Requisito, Veicolo, componente, guasto), lo stesso codice in due veicoli sono celle distinte.</remarks>
public sealed record EsitoDtcRilevato(string RequisitoId, string Guasto, string Componente, PassoDtc Passo, Esito Esito, DateTime Data, string TestId, int Ordine, string Veicolo = "");

/// <summary>Avanzamento di una cella guasto x componente: passi, ultimo esito per passo e stato complessivo.</summary>
public sealed record DtcAvanzamento(
    string RequisitoId,
    string Guasto,
    string Componente,
    StatoDtc Stato,
    Esito? EsitoValidazione,
    Esito? EsitoDevalidazione,
    DateTime? DataValidazione,
    DateTime? DataDevalidazione,
    string TestIdValidazione,
    string TestIdDevalidazione,
    bool Conflitto,
    string Veicolo = "",
    bool NonAllineata = false)
{
    /// <summary>Chiave stabile della cella (usata anche come AutomationId della riga nella vista).</summary>
    public string Chiave => DtcAvanzamentoService.ChiaveCella(RequisitoId, Guasto, Componente, Veicolo);
}

/// <summary>Copertura di un Requisito: coperto = tutti i suoi guasto completi (e almeno uno).</summary>
public sealed record RequisitoDtcAvanzamento(string RequisitoId, int Totale, int Completi, int Validati, int NonConformi, int DaFare)
{
    public bool Coperto => Totale > 0 && Completi == Totale;
}

public sealed record DtcRiepilogo(int Totale, int Completi, int Validati, int NonConformi, int DaFare, int RequisitiTotali, int RequisitiCoperti)
{
    public string Testo => $"{Completi}/{Totale}";
}

public sealed record DtcAvanzamentoRisultato(
    IReadOnlyList<DtcAvanzamento> PerDtc,
    IReadOnlyList<RequisitoDtcAvanzamento> PerRequisito,
    DtcRiepilogo Totale,
    int CelleSenzaAnagrafica)
{
    public static readonly DtcAvanzamentoRisultato Vuoto = new(
        Array.Empty<DtcAvanzamento>(), Array.Empty<RequisitoDtcAvanzamento>(), new DtcRiepilogo(0, 0, 0, 0, 0, 0, 0), 0);
}

/// <summary>
/// Calcolo PURO (nessun I/O, nessuno stato) dell'avanzamento dei test Banco basati su requisito/componente/guasto
/// (6.5.0, punto 7). Cella = (Requisito, componente, guasto) — i guasti vivono dentro il Requisito (Requisito.Guasto); per ogni cella due passi, validazione e devalidazione.
///
/// REGOLE (decisioni utente):
///  - ULTIMO esito per data di ciascun passo: un Non Conforme seguito da un Conforme torna Conforme e viceversa.
///  - Completo = validazione E devalidazione con ultimo esito Conforme. Validato = solo validazione Conforme.
///    Non conforme = almeno un passo con ultimo esito non conforme. Altrimenti Da fare.
///  - Un test puo coprire piu guasto ma non tutti: l'insieme dei guasti e quello dei Requisiti, gli esiti arrivano da
///    qualunque numero di test.
///  - Requisito coperto = tutti i suoi guasto (celle) completi. Lo stesso codice guasto in due Requisiti sono due celle
///    distinte (l'operazione referenzia Requisito+guasto); il totale N/M conta tutte le celle.
///  - 6.5.1: la cella e (Requisito, Veicolo, componente, guasto). Lo stesso codice in due veicoli = due celle. Un esito si abbina
///    alla cella del suo veicolo; se non esiste, a quella SENZA veicolo (voce dei dati 6.5.0, vale per ogni veicolo);
///    un esito senza veicolo si abbina all'unica cella (Requisito, componente, guasto) esistente (se ce ne sono piu, e ambiguo e non
///    si abbina: finisce tra le celle senza anagrafica). Una voce il cui veicolo non e piu tra quelli dichiarati dal requisito
///    e NON ALLINEATA: segnalata, ma conta normalmente.
///
/// DECISIONI UTENTE: ConformeConDeviazioni vale conforme (un guasto con passi Conformi o Conformi con
/// deviazioni e Completo; la riga mostra comunque l'esito reale del passo); a pari data/ora con classi diverse sullo
/// stesso passo il conflitto e segnalato (Conflitto=true) e prevale il non conforme (esito cautelativo); N.A./Da
/// eseguire non sono esiti (come OperationalSemantics: solo Conforme/NonConforme/ConformeConDeviazioni contano).
/// I Requisiti archiviati non entrano nei totali.
/// </summary>
public static class DtcAvanzamentoService
{
    /// <summary>Chiave della cella. Senza veicolo la chiave e identica a quella 6.5.0 (AutomationId stabili per i dati esistenti).</summary>
    public static string ChiaveCella(string requisitoId, string guasto, string componente, string? veicolo = null)
    {
        var k = (requisitoId ?? "").Trim().ToUpperInvariant() + "|" + (componente ?? "").Trim().ToUpperInvariant() + "|" + (guasto ?? "").Trim().ToUpperInvariant();
        var sv = (veicolo ?? "").Trim();
        return sv.Length == 0 ? k : k + "|" + sv.ToUpperInvariant();
    }

    /// <summary>Veicolo di un'operazione guasto: quello scelto sull'operazione (Condizione.DtcVeicolo); se vuoto, l'UNICO veicolo
    /// del test (con piu veicoli o nessuno non si indovina: "" = qualunque). Sorgente unica per RaccogliEsiti e per l'indice SQLite.</summary>
    public static string VeicoloOperazione(TestCase tc, Condizione c)
    {
        var sv = (c.DtcVeicolo ?? "").Trim();
        if (sv.Length > 0) return sv;
        var veicoli = tc.VeicoliEffettivi;
        return veicoli.Count == 1 ? (veicoli[0] ?? "").Trim() : "";
    }

    /// <summary>Estrae gli esiti guasto dai Test: Condizioni EFFETTIVE (ultimo tentativo di ogni operazione di una REV)
    /// con Guasto valorizzato e realmente eseguite, una voce per componente (OperationalSemantics.EcuUnits). I Test eliminati
    /// sono ignorati. Costo lineare, nessuna allocazione per i Test senza operazioni guasto.</summary>
    public static List<EsitoDtcRilevato> RaccogliEsiti(IEnumerable<TestCase> testCases)
    {
        var esiti = new List<EsitoDtcRilevato>();
        var ordine = 0;
        foreach (var tc in testCases)
        {
            if (tc.Eliminato) continue;
            foreach (var rev in tc.Esecuzioni)
            {
                var haDtc = false;
                foreach (var c in rev.Condizioni) if (c.RiguardaDtc) { haDtc = true; break; }
                if (!haDtc) continue;
                foreach (var c in rev.CondizioniEffettive)
                {
                    if (!c.RiguardaDtc || !c.Esito.EEseguito()) continue;
                    var guasto = c.Guasto!.Trim();
                    var req = (c.DtcRequisitoId ?? "").Trim();
                    var passo = c.DtcPasso ?? PassoDtc.Validazione;
                    var componenti = OperationalSemantics.EcuUnits(tc, c);
                    var sv = VeicoloOperazione(tc, c);
                    if (componenti.Count == 0)
                        esiti.Add(new EsitoDtcRilevato(req, guasto, "", passo, c.Esito, rev.Data, tc.Id, ordine++, sv));
                    else
                        foreach (var componente in componenti)
                            esiti.Add(new EsitoDtcRilevato(req, guasto, componente, passo, c.Esito, rev.Data, tc.Id, ordine++, sv));
                }
            }
        }
        return esiti;
    }

    private enum Classe { Conforme, NonConforme }

    // Decisione utente 6.5.0: Conforme E ConformeConDeviazioni valgono "conforme" per completare un passo;
    // solo NonConforme (e gli esiti non eseguiti, che non sono mai estratti) no.
    private static Classe Classifica(Esito e) => e is Esito.Conforme or Esito.ConformeConDeviazioni ? Classe.Conforme : Classe.NonConforme;

    private sealed class Passo
    {
        public DateTime Data;
        public Esito Esito;
        public string TestId = "";
        public bool Conflitto;
        public bool Presente;
    }

    private static void Applica(Passo p, EsitoDtcRilevato e)
    {
        if (!p.Presente || e.Data > p.Data)
        {
            p.Presente = true; p.Data = e.Data; p.Esito = e.Esito; p.TestId = e.TestId; p.Conflitto = false;
            return;
        }
        if (e.Data < p.Data) return;
        // Stessa data/ora: classi uguali -> nessun conflitto (resta il primo); classi diverse -> conflitto e
        // prevale il non conforme (cautelativo). Con entrambi non conformi resta il primo incontrato.
        if (Classifica(e.Esito) == Classifica(p.Esito)) return;
        p.Conflitto = true;
        if (Classifica(e.Esito) == Classe.NonConforme) { p.Esito = e.Esito; p.TestId = e.TestId; }
    }

    /// <summary>Calcola stato per guasto e per requisito e il totale. Voci guasto con codice o componente vuoti sono ignorate;
    /// i Requisiti archiviati non entrano nei totali. Gli esiti su celle assenti dai Requisiti non alterano nessun
    /// totale e sono solo contati in <see cref="DtcAvanzamentoRisultato.CelleSenzaAnagrafica"/>.</summary>
    public static DtcAvanzamentoRisultato Calcola(IEnumerable<Requisito> requisiti, IEnumerable<EsitoDtcRilevato> esiti)
    {
        var celle = new Dictionary<string, (string Req, string Guasto, string Componente, string Sv, bool NonAllineata)>(StringComparer.Ordinal);
        var perTripla = new Dictionary<string, List<string>>(StringComparer.Ordinal); // (Req,componente,guasto) -> chiavi cella (una per veicolo)
        foreach (var r in requisiti)
        {
            if (r.Archiviata) continue;
            var req = (r.Id ?? "").Trim();
            if (req.Length == 0) continue;
            IReadOnlyList<string>? dichiarati = null;
            foreach (var v in r.DtcVoci)
            {
                var guasto = (v.Codice ?? "").Trim();
                var componente = (v.Componente ?? "").Trim();
                if (guasto.Length == 0 || componente.Length == 0) continue;
                var sv = v.VeicoloNorm;
                var chiave = ChiaveCella(req, guasto, componente, sv);
                var nonAllineata = false;
                if (sv.Length > 0)
                {
                    dichiarati ??= RequisitoApplicabilita.SottoveicoliDi(r);
                    nonAllineata = !dichiarati.Contains(sv, StringComparer.OrdinalIgnoreCase);
                }
                if (!celle.TryAdd(chiave, (req, guasto, componente, sv, nonAllineata))) continue;
                var tripla = ChiaveCella(req, guasto, componente);
                if (!perTripla.TryGetValue(tripla, out var lista)) perTripla[tripla] = lista = new List<string>(1);
                lista.Add(chiave);
            }
        }

        // Abbinamento esito -> cella: veicolo esatto; poi cella senza veicolo; per esiti senza veicolo, l'unica cella della tripla.
        string? TrovaCella(EsitoDtcRilevato e)
        {
            var sv = (e.Veicolo ?? "").Trim();
            var esatta = ChiaveCella(e.RequisitoId, e.Guasto, e.Componente, sv);
            if (celle.ContainsKey(esatta)) return esatta;
            if (sv.Length > 0)
            {
                var senza = ChiaveCella(e.RequisitoId, e.Guasto, e.Componente);
                return celle.ContainsKey(senza) ? senza : null;
            }
            return perTripla.TryGetValue(esatta, out var l) && l.Count == 1 ? l[0] : null;
        }

        var validazioni = new Dictionary<string, Passo>(StringComparer.Ordinal);
        var devalidazioni = new Dictionary<string, Passo>(StringComparer.Ordinal);
        var senzaAnagrafica = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in esiti)
        {
            var k = TrovaCella(e);
            if (k == null) { senzaAnagrafica.Add(ChiaveCella(e.RequisitoId, e.Guasto, e.Componente, e.Veicolo)); continue; }
            var mappa = e.Passo == PassoDtc.Devalidazione ? devalidazioni : validazioni;
            if (!mappa.TryGetValue(k, out var p)) mappa[k] = p = new Passo();
            Applica(p, e);
        }

        var perDtc = new List<DtcAvanzamento>(celle.Count);
        foreach (var (k, cella) in celle)
        {
            validazioni.TryGetValue(k, out var v);
            devalidazioni.TryGetValue(k, out var d);
            var cv = v is { Presente: true } ? Classifica(v.Esito) : (Classe?)null;
            var cd = d is { Presente: true } ? Classifica(d.Esito) : (Classe?)null;
            var stato = cv == Classe.NonConforme || cd == Classe.NonConforme ? StatoDtc.NonConforme
                : cv == Classe.Conforme && cd == Classe.Conforme ? StatoDtc.Completo
                : cv == Classe.Conforme ? StatoDtc.Validato
                : StatoDtc.DaFare;
            perDtc.Add(new DtcAvanzamento(
                cella.Req, cella.Guasto, cella.Componente, stato,
                v is { Presente: true } ? v.Esito : null, d is { Presente: true } ? d.Esito : null,
                v is { Presente: true } ? v.Data : null, d is { Presente: true } ? d.Data : null,
                v?.TestId ?? "", d?.TestId ?? "",
                (v?.Conflitto ?? false) || (d?.Conflitto ?? false), cella.Sv, cella.NonAllineata));
        }
        perDtc.Sort((a, b) =>
        {
            var c = NaturalStringComparer.Instance.Compare(a.RequisitoId, b.RequisitoId);
            if (c != 0) return c;
            c = NaturalStringComparer.Instance.Compare(a.Veicolo, b.Veicolo);
            if (c != 0) return c;
            c = NaturalStringComparer.Instance.Compare(a.Componente, b.Componente);
            return c != 0 ? c : NaturalStringComparer.Instance.Compare(a.Guasto, b.Guasto);
        });

        var perReq = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase); // [tot, completi, validati, nonConf, daFare]
        foreach (var x in perDtc)
        {
            if (!perReq.TryGetValue(x.RequisitoId, out var a)) perReq[x.RequisitoId] = a = new int[5];
            a[0]++;
            a[x.Stato switch { StatoDtc.Completo => 1, StatoDtc.Validato => 2, StatoDtc.NonConforme => 3, _ => 4 }]++;
        }
        var perRequisito = perReq
            .Select(kv => new RequisitoDtcAvanzamento(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3], kv.Value[4]))
            .OrderBy(r => r.RequisitoId, NaturalStringComparer.Instance)
            .ToList();

        var tot = new DtcRiepilogo(
            perDtc.Count,
            perDtc.Count(x => x.Stato == StatoDtc.Completo),
            perDtc.Count(x => x.Stato == StatoDtc.Validato),
            perDtc.Count(x => x.Stato == StatoDtc.NonConforme),
            perDtc.Count(x => x.Stato == StatoDtc.DaFare),
            perRequisito.Count,
            perRequisito.Count(r => r.Coperto));
        return new DtcAvanzamentoRisultato(perDtc, perRequisito, tot, senzaAnagrafica.Count);
    }

    /// <summary>Scorciatoia: estrae gli esiti dai Test e calcola.</summary>
    public static DtcAvanzamentoRisultato Calcola(IEnumerable<Requisito> requisiti, IEnumerable<TestCase> testCases) =>
        Calcola(requisiti, RaccogliEsiti(testCases));
}
