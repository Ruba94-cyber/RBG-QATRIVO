using System.Globalization;
using System.Runtime.InteropServices;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Base dei tempi (TIME, 6.6.1), scelta solo nelle modalità Tempo totale e Tempo medio. "Tutte le esecuzioni" somma
/// ogni tentativo realmente eseguito, anche ripetuto o non effettivo. "Ultimo stato" usa la durata dell'operazione che
/// determina lo stato corrente di ogni combinazione (stessa regola di vincita del Report).</summary>
public enum BaseTempoMatrice { TutteLeEsecuzioni = 0, UltimoStato = 1 }

/// <summary>Operatore strettamente maggiore o minore di un valore in minuti (filtri TIME).</summary>
public enum OperatoreDurata { Maggiore = 0, Minore = 1 }

/// <summary>Filtro TIME su minuti: strettamente maggiore (&gt;) o minore (&lt;). Un valore non noto non corrisponde mai.</summary>
public sealed record FiltroDurataMinuti(OperatoreDurata Operatore, int Minuti)
{
    public bool Corrisponde(int? minuti) => minuti is { } valore
        && (Operatore == OperatoreDurata.Maggiore ? valore > Minuti : valore < Minuti);

    /// <summary>Filtro dal testo digitato e dall'operatore (0 = maggiore, 1 = minore), come nei filtri TIME di Report e Home. Testo vuoto,
    /// negativo o non intero = nessun filtro.</summary>
    public static FiltroDurataMinuti? DaTesto(string? testo, int operatore)
    {
        if (!int.TryParse(testo?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minuti) || minuti < 0)
            return null;
        return new FiltroDurataMinuti(operatore == 1 ? OperatoreDurata.Minore : OperatoreDurata.Maggiore, minuti);
    }

    /// <summary>Vero se il testo e' vuoto (nessun filtro) o un intero non negativo. Falso per "1,5", "-3", "abc" e i valori fuori intervallo:
    /// il filtro resta ignorato come prima (<see cref="DaTesto"/> ritorna null), ma la vista deve segnalare l'errore di digitazione.</summary>
    public static bool TestoValido(string? testo) => string.IsNullOrWhiteSpace(testo) || DaTesto(testo, 0) != null;

    public string Etichetta => (Operatore == OperatoreDurata.Maggiore ? ">" : "<") + " " + Minuti.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Valore temporale aggregato. <see cref="SommaMinuti"/> somma solo i minuti noti; <see cref="Conosciuti"/> conta le
/// osservazioni con minuti noti; <see cref="Eleggibili"/> conta tutte le osservazioni eseguite (completezza = noti / eleggibili).
/// La media e' somma / noti: mai media di medie. Senza valori noti la media e' vuota, distinta da uno zero noto.</summary>
public readonly record struct ValoreTempo(long SommaMinuti, int Conosciuti, int Eleggibili)
{
    public bool HaValore => Conosciuti > 0;
    public double? Media => Conosciuti > 0 ? (double)SommaMinuti / Conosciuti : null;
    public bool Completo => Eleggibili > 0 && Conosciuti == Eleggibili;
}

internal sealed class AccumulatoreTempo
{
    private long _somma;
    private int _conosciuti;
    private int _eleggibili;

    public void Aggiungi(int? minuti)
    {
        _eleggibili++;
        if (minuti is not { } valore) return;
        _somma += valore;
        _conosciuti++;
    }

    public ValoreTempo Valore() => new(_somma, _conosciuti, _eleggibili);
}

/// <summary>Risultato temporale di una matrice: stesse chiavi di riga e colonna della matrice di stato (celle elementari,
/// aggregate e totali), cosi' assi, filtri e ordine restano identici cambiando modalita' o base.</summary>
internal sealed class RisultatoTempoMatrice
{
    public RisultatoTempoMatrice(BaseTempoMatrice baseTempo, Dictionary<(string Y, string X), ValoreTempo> celle)
    {
        Base = baseTempo;
        Celle = celle;
    }

    public BaseTempoMatrice Base { get; }
    public Dictionary<(string Y, string X), ValoreTempo> Celle { get; }

    /// <summary>Valore di una coppia (riga, colonna) con le stesse chiavi della matrice; coppia assente = nessuna osservazione.</summary>
    public ValoreTempo Valore(string riga, string colonna) => Celle.TryGetValue((riga, colonna), out var valore) ? valore : default;
}

public static partial class ReportMatriceService
{
    /// <summary>Riepilogo compatto Home: come la base TIME "Tutte le esecuzioni" del Report.
    /// Un tentativo identificato da TestUid + Condizione.Uid e' contato una sola volta
    /// (ultima revisione); le ripetizioni con UID distinto e le guasto sono incluse.
    /// Il tempo totale considera ogni esito eseguito; in modo Tempo medio vengono contate
    /// solo operazioni con Esito.Conforme (anche il denominatore di completezza).
    /// Per la media il denominatore comprende solo durate note, mai la media delle medie.</summary>
    public static ValoreTempo RiepilogoTempoHome(IEnumerable<TestCase> testCases, bool soloConformi = false)
    {
        var accumulatore = new AccumulatoreTempo();
        foreach (var tc in testCases)
        {
            // Parita' con le query SQLite (eliminato = 0) e la matrice Report:
            // le righe synthetic/manuali non rappresentano tempo di lavoro eseguito.
            if (tc.Eliminato || tc.Origine == OrigineTest.ImportStatoUdt ||
                tc.Id.StartsWith(ManualePrefisso, StringComparison.Ordinal)) continue;
            foreach (var occorrenza in UltimeOccorrenze(tc).Values)
            {
                var condizione = occorrenza.Condizione;
                if (condizione.Esito.EEseguito() && (!soloConformi || condizione.Esito == Esito.Conforme))
                    accumulatore.Aggiungi(condizione.DurataMinuti);
            }
        }
        return accumulatore.Valore();
    }

    /// <summary>Vero per le due modalità temporali (Tempo totale, Tempo medio).</summary>
    public static bool ModalitaTempo(ModalitaCellaMatrice modalita) =>
        modalita is ModalitaCellaMatrice.TempoTotale or ModalitaCellaMatrice.TempoMedio;

    /// <summary>Testo di una cella temporale, stesso formato per UI ed export: "45 min · 2/3" (noti/eleggibili). Totale o media
    /// secondo la modalita'. Senza valori noti: "—" (con i noti a zero sul totale eleggibile, se ci sono osservazioni).
    /// Una cella marcata N.A. resta "N.A.".</summary>
    public static string TestoTempo(ValoreTempo valore, ModalitaCellaMatrice modalita, bool nonApplicabile = false)
    {
        if (nonApplicabile) return "N.A.";
        if (valore.Eleggibili == 0) return "—";
        var completezza = $"{valore.Conosciuti}/{valore.Eleggibili}";
        if (!valore.HaValore) return $"— · {completezza}";
        var numero = modalita == ModalitaCellaMatrice.TempoMedio
            ? valore.Media!.Value.ToString("0.#", CultureInfo.CurrentCulture)
            : valore.SommaMinuti.ToString(CultureInfo.CurrentCulture);
        return $"{numero} min · {completezza}";
    }

    /// <summary>Operazioni correnti effettive di un Test (TIME, base Test): per ogni operazione (Condizione.Uid) si considera solo
    /// l'ULTIMA revisione in cui compare, e solo se in quella revisione l'operazione e' ancora effettiva (CondizioniEffettive, cioe'
    /// is_effective della proiezione) ed eseguita. Le operazioni guasto contano (decisione utente), la matrice per funzionalita' no.</summary>
    internal static List<Condizione> OperazioniCorrentiEffettive(TestCase tc)
    {
        var risultato = new List<Condizione>();
        var effettivePerRevisione = new Dictionary<int, HashSet<Guid>>();
        foreach (var (revisione, condizione) in UltimeOccorrenze(tc).Values)
        {
            if (!condizione.Esito.EEseguito()) continue;
            if (!effettivePerRevisione.TryGetValue(revisione, out var uidEffettivi))
            {
                uidEffettivi = tc.Esecuzioni[revisione].CondizioniEffettive.Select(c => c.Uid).ToHashSet();
                effettivePerRevisione[revisione] = uidEffettivi;
            }
            if (uidEffettivi.Contains(condizione.Uid)) risultato.Add(condizione);
        }
        return risultato;
    }

    /// <summary>Vero se il Test ha almeno una operazione corrente effettiva (guasto inclusi, vedi <see cref="OperazioniCorrentiEffettive"/>)
    /// la cui durata soddisfa il filtro. Un'operazione senza durata non corrisponde mai; un Test importato da UnitLogger non corrisponde.</summary>
    internal static bool HaOperazioneConDurata(TestCase tc, FiltroDurataMinuti filtro) =>
        tc.Origine != OrigineTest.ImportStatoUdt
        && OperazioniCorrentiEffettive(tc).Any(condizione => filtro.Corrisponde(condizione.DurataMinuti));

    /// <summary>Un tentativo e' una Condizione (Uid). Le copie salvate con lo stesso Uid in revisioni successive sono LO STESSO
    /// tentativo: il suo valore e' quello dell'ULTIMA revisione in cui compare (un tentativo non si conta due volte, le ripetizioni
    /// con Uid nuovo restano tentativi distinti). Ritorna Uid -> (indice della revisione, condizione).</summary>
    internal static Dictionary<Guid, (int Revisione, Condizione Condizione)> UltimeOccorrenze(TestCase tc)
    {
        var risultato = new Dictionary<Guid, (int Revisione, Condizione Condizione)>();
        for (var revisione = 0; revisione < tc.Esecuzioni.Count; revisione++)
            foreach (var condizione in tc.Esecuzioni[revisione].Condizioni)
                risultato[condizione.Uid] = (revisione, condizione);
        return risultato;
    }

    /// <summary>TIME (6.6.1), Stato e Stato %: tiene solo le combinazioni la cui operazione vincente (stato corrente) ha una durata che
    /// corrisponde al filtro. Una combinazione senza operazione vincente con durata (non testata, valore manuale del Master, esito non
    /// eseguito) non corrisponde mai. Dopo la riduzione allo stato corrente, mai prima: vedi Prepara.</summary>
    private static Dictionary<string, CombinazioneAccumulatore> FiltraVincentePerDurata(
        Dictionary<string, CombinazioneAccumulatore> combinazioni, FiltroDurataMinuti filtro)
    {
        var risultato = new Dictionary<string, CombinazioneAccumulatore>(combinazioni.Comparer);
        foreach (var (chiave, acc) in combinazioni)
        {
            if (!acc.Eseguita || acc.ManualeVincente || acc.Vincente is not { } vincente) continue;
            if (filtro.Corrisponde(vincente.DurataMinuti)) risultato[chiave] = acc;
        }
        return risultato;
    }

    /// <summary>Combinazioni della base "Ultimo stato": il core dei soli tentativi dell'ULTIMA revisione in cui compare (raccolto nello
    /// stesso passaggio dello Stato, vedi CostruisciCombinazioni), poi la stessa proiezione dello Stato sugli assi. Una copia superata
    /// non entra mai, e non si ripete la scansione dei Test.</summary>
    private static Dictionary<string, CombinazioneAccumulatore> CombinazioniUltimoStatoTempo(
        Dictionary<string, CombinazioneAccumulatore> coreUltimo,
        ConfigurazioneMatriceReport config, IReadOnlyDictionary<string, string> gammaDi,
        IReadOnlyDictionary<string, Funzionalita> funzioneDi, IReadOnlyDictionary<Guid, TestTemplate> templateDi)
    {
        var (proiettate, _) = ProiettaStatoCorrente(new Dictionary<string, CombinazioneAccumulatore>(StringComparer.OrdinalIgnoreCase),
            coreUltimo, config, gammaDi, funzioneDi, templateDi);
        return proiettate;
    }

    /// <summary>Totale dei minuti noti del Test (TIME, livello Test, uguale in ogni modalita e base). Decisione utente: somma di TUTTI
    /// i tentativi realmente eseguiti del Test, ripetizioni incluse (20 minuti piu una ripetizione da 10 danno 30), ciascuno una volta:
    /// identita TestUid + Condizione.Uid, con il valore dell'ultima revisione in cui compare. Le operazioni guasto contano; un Test
    /// importato da UnitLogger non ha totale. Null = nessuna durata nota. Le operazioni guasto non entrano nelle celle della matrice per funzionalita.</summary>
    internal static int? TotaleMinutiTest(TestCase tc)
    {
        if (tc.Origine == OrigineTest.ImportStatoUdt) return null;
        long somma = 0;
        var conosciuti = 0;
        foreach (var occorrenza in UltimeOccorrenze(tc).Values)
        {
            var condizione = occorrenza.Condizione;
            if (!condizione.Esito.EEseguito()) continue;
            if (condizione.DurataMinuti is not { } minuti) continue;
            somma += minuti;
            conosciuti++;
        }
        return conosciuti == 0 ? null : (int)Math.Min(somma, int.MaxValue);
    }

    /// <summary>Calcolo temporale della base "Ultimo stato": un'operazione per combinazione, la vincitrice dello stato corrente
    /// (Report: data, Id del Test, revisione, tentativo). Un'operazione vincente su piu' componente o veicoli conta una sola volta per riga,
    /// colonna e totale. I valori manuali del Master e i Test importati da UnitLogger non hanno durata di lavoro e restano fuori.</summary>
    private static RisultatoTempoMatrice CalcolaTempoUltimoStato(Dictionary<string, CombinazioneAccumulatore> combinazioni,
        ConfigurazioneMatriceReport config, IReadOnlyDictionary<string, string> gammaDi)
    {
        var osservazioni = new Dictionary<(Guid TestUid, Guid CondizioneUid), (int? Minuti, List<(string Y, string X)> Coppie)>();
        foreach (var acc in combinazioni.Values)
        {
            if (!acc.Eseguita || acc.ManualeVincente || acc.Vincente is not { } vincente) continue;
            if (vincente.Test.Origine == OrigineTest.ImportStatoUdt) continue;
            // Tempo medio: la vincitrice dello stato deve essere CONFORME.
            // Non recuperare un precedente conforme se l'ultimo stato e' NOK.
            if (config.ModalitaCella == ModalitaCellaMatrice.TempoMedio && vincente.Esito != Esito.Conforme) continue;
            // Filtro sulla durata per combinazione corrente (decisione utente): vale per il tentativo che determina lo stato.
            if (config.FiltroTempoOperazione is { } filtroCorrente && !filtroCorrente.Corrisponde(vincente.DurataMinuti)) continue;
            var chiave = (vincente.Test.Uid, vincente.CondizioneUid);
            if (!osservazioni.TryGetValue(chiave, out var osservazione))
            {
                osservazione = (vincente.DurataMinuti, new List<(string Y, string X)>(1));
                osservazioni[chiave] = osservazione;
            }
            osservazione.Coppie.Add((ChiaveAsse(acc, config.CampiY, gammaDi), ChiaveAsse(acc, config.CampiX, gammaDi)));
        }

        var aggregatore = new AggregatoreTempoMatrice(config.CampiY.Count, config.CampiX.Count);
        foreach (var osservazione in osservazioni.Values) aggregatore.Osservazione(osservazione.Minuti, osservazione.Coppie);
        return aggregatore.Risultato(BaseTempoMatrice.UltimoStato);
    }

    /// <summary>Un'unica espansione vuota condivisa (nessun asse extra): il dizionario non viene mai modificato.</summary>
    private static readonly Dictionary<string, string> NessunValoreExtra = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Dictionary<string, string>> UnaEspansioneSenzaExtra = [NessunValoreExtra];

    /// <summary>Calcolo temporale della base "Tutte le esecuzioni": ogni tentativo realmente eseguito (repetizioni e tentativi non
    /// effettivi inclusi), espanso su funzionalita', componente e veicoli come la matrice. Filtri, esito e assi seguono la stessa logica
    /// della matrice di stato: un tentativo non viene mai contato piu' di una volta per cella o per totale.</summary>
    private static RisultatoTempoMatrice CalcolaTempoTutteLeEsecuzioni(IEnumerable<TestCase> testCases,
        ConfigurazioneMatriceReport config, IReadOnlyDictionary<string, string> gammaDi,
        IReadOnlyDictionary<string, Funzionalita> funzioneDi, IReadOnlyDictionary<Guid, TestTemplate> templateDi)
    {
        var estra = DimensioniAggiuntive(config);
        var filtri = config.Filtri
            .Where(f => f.Key != DimensioniMatrice.Esito)
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        var esiti = FiltroEsitoStatoCorrente(config);
        var aggregatore = new AggregatoreTempoMatrice(config.CampiY.Count, config.CampiX.Count);
        var coppie = new List<(string Y, string X)>();
        var ultimaRevisione = new Dictionary<Guid, int>();   // riusata fra i Test: nessuna allocazione per Test

        foreach (var tc in testCases)
        {
            if (tc.Eliminato || tc.Origine == OrigineTest.ImportStatoUdt) continue;
            if (tc.Id.StartsWith(ManualePrefisso, StringComparison.Ordinal)) continue;
            if (config.FiltroTempoTotaleTest is { } filtroTotale && !filtroTotale.Corrisponde(TotaleMinutiTest(tc))) continue;

            var veicoli = tc.VeicoliEffettivi.Count > 0 ? (IEnumerable<string>)tc.VeicoliEffettivi : new[] { "" };
            // Stessa regola di UltimeOccorrenze (l'ultima revisione in cui compare l'Uid vince), senza un dizionario per Test.
            ultimaRevisione.Clear();
            for (var r = 0; r < tc.Esecuzioni.Count; r++)
                foreach (var condizioneUltima in tc.Esecuzioni[r].Condizioni) ultimaRevisione[condizioneUltima.Uid] = r;
            for (var revisioneIndice = 0; revisioneIndice < tc.Esecuzioni.Count; revisioneIndice++)
            {
                var revisione = tc.Esecuzioni[revisioneIndice];
                for (var tentativoIndice = 0; tentativoIndice < revisione.Condizioni.Count; tentativoIndice++)
                {
                    var condizione = revisione.Condizioni[tentativoIndice];
                    // Un tentativo conta una sola volta, dall'ultima revisione in cui compare (vedi UltimeOccorrenze).
                    if (!ultimaRevisione.TryGetValue(condizione.Uid, out var revisioneUltima) || revisioneUltima != revisioneIndice) continue;
                    if (!condizione.Esito.EEseguito() || condizione.RiguardaDtc) continue;
                    // Tempo medio: solo operazioni con esito Conforme. In Tempo totale
                    // restano comprese NOK e deviazioni, come da storico TIME.
                    if (config.ModalitaCella == ModalitaCellaMatrice.TempoMedio && condizione.Esito != Esito.Conforme) continue;
                    var minuti = condizione.DurataMinuti;
                    if (config.FiltroTempoOperazione is { } filtroOperazione && !filtroOperazione.Corrisponde(minuti)) continue;

                    var funzioni = string.IsNullOrWhiteSpace(condizione.Funzionalita)
                        ? tc.Funzionalita.DefaultIfEmpty(null!) : new[] { condizione.Funzionalita };
                    var ecuUnita = OperationalSemantics.EcuUnits(tc, condizione);
                    var componenti = ecuUnita.Count > 0 ? (IEnumerable<string>)ecuUnita : new[] { "" };
                    var deviazioneAccettata = condizione.Esito == Esito.ConformeConDeviazioni
                        && revisione.EsitoSovrascritto && revisione.Esito == Esito.Conforme;

                    coppie.Clear();
                    foreach (var f in funzioni)
                    foreach (var componente in componenti)
                    foreach (var v in veicoli)
                    {
                        var candidato = new CandidatoCombinazione(f, componente, v, condizione.Esito, deviazioneAccettata,
                            tc, revisione.Data, revisioneIndice, tentativoIndice)
                        { DurataMinuti = minuti, CondizioneUid = condizione.Uid };
                        if (!PassaFiltri(candidato, filtri, gammaDi, funzioneDi, templateDi)) continue;

                        var valoriExtra = estra.Count == 0
                            ? UnaEspansioneSenzaExtra
                            : EspandiEstra(candidato, estra, gammaDi, funzioneDi, templateDi);
                        foreach (var valori in valoriExtra)
                        {
                            var acc = new CombinazioneAccumulatore(candidato, valori);
                            if (esiti != null && !PassaFiltroEsitoCorrente(acc, esiti)) continue;
                            coppie.Add((ChiaveAsse(acc, config.CampiY, gammaDi), ChiaveAsse(acc, config.CampiX, gammaDi)));
                        }
                    }
                    if (coppie.Count == 0) continue;
                    aggregatore.Osservazione(minuti, coppie);
                }
            }
        }
        return aggregatore.Risultato(BaseTempoMatrice.TutteLeEsecuzioni);
    }

    /// <summary>Ripartisce ogni osservazione (un tentativo) su tutte le chiavi di riga/colonna che raggiunge, prefissi dei
    /// percorsi inclusi (aggregati e totali). Una chiave riceve la durata intera una sola volta per osservazione.</summary>
    private sealed class AggregatoreTempoMatrice
    {
        private readonly int _livelliY;
        private readonly int _livelliX;
        private readonly Dictionary<(string Y, string X), AccumulatoreTempo> _celle = new(ConfrontoCoppiaMatrice.Istanza);
        private readonly HashSet<(string Y, string X)> _chiaviViste = new(ConfrontoCoppiaMatrice.Istanza);
        // Buffer dei prefissi riusati per ogni coppia: stessa sequenza di PrefissiPercorso, senza allocare un array per coppia.
        private readonly string[] _prefissiY;
        private readonly string[] _prefissiX;

        public AggregatoreTempoMatrice(int livelliY, int livelliX)
        {
            _livelliY = livelliY;
            _livelliX = livelliX;
            _prefissiY = new string[livelliY + 1];
            _prefissiX = new string[livelliX + 1];
        }

        public void Osservazione(int? minuti, IReadOnlyList<(string Y, string X)> coppie)
        {
            _chiaviViste.Clear();
            foreach (var (y, x) in coppie)
            {
                RiempiPrefissi(y, _livelliY, _prefissiY);
                RiempiPrefissi(x, _livelliX, _prefissiX);
                foreach (var py in _prefissiY)
                foreach (var px in _prefissiX)
                {
                    if (!_chiaviViste.Add((py, px))) continue;
                    ref var acc = ref CollectionsMarshal.GetValueRefOrAddDefault(_celle, (py, px), out _);
                    acc ??= new AccumulatoreTempo();
                    acc.Aggiungi(minuti);
                }
            }
        }

        /// <summary>Stessi prefissi di PrefissiPercorso (radice "" inclusa), riempiti nel buffer dato. Per un solo livello il
        /// primo valore si legge senza spezzare la chiave in un array.</summary>
        private static void RiempiPrefissi(string chiave, int livelli, string[] prefissi)
        {
            if (livelli == 1)
            {
                prefissi[0] = "";
                var fine = chiave.IndexOf(SepLivello);
                prefissi[1] = fine < 0 ? chiave : chiave[..fine];
                return;
            }
            Array.Copy(PrefissiPercorso(chiave, livelli), prefissi, livelli + 1);
        }

        public RisultatoTempoMatrice Risultato(BaseTempoMatrice baseTempo)
        {
            var celle = new Dictionary<(string Y, string X), ValoreTempo>(ConfrontoCoppiaMatrice.Istanza);
            foreach (var (chiave, acc) in _celle) celle[chiave] = acc.Valore();
            return new RisultatoTempoMatrice(baseTempo, celle);
        }
    }
}
