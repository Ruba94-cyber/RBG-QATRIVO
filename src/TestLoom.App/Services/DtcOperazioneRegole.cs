using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Regole PURE (6.5.0, punto 7) dell'operazione guasto: UNICA sorgente di verita = Condizione.Guasto (+DtcRequisitoId +
/// DtcPasso). Il selettore compare se il test e collegato a un requisito Extra con guasto OPPURE usa lo strumento "Guasto Injection"
/// (che ha componente e non ha funzionalita). Il campo "Guasto - Atteso" dello strumento e solo una precompilazione dal guasto
/// dell'operazione, non un secondo dato indipendente.</summary>
public static class DtcOperazioneRegole
{
    public const string StrumentoDtcInjection = "Guasto Injection";
    public const string ChiaveAtteso = "Guasto Injection::guasto - Atteso";

    public static bool UsaDtcInjection(IEnumerable<string> strumenti) =>
        strumenti.Any(s => s.Trim().Equals(StrumentoDtcInjection, StringComparison.OrdinalIgnoreCase));

    /// <summary>Selettore guasto visibile: requisito Extra con guasto collegato OPPURE strumento guasto Injection.</summary>
    public static bool MostraSelettore(bool haRequisitoD4dConDtc, bool usaDtcInjection) => haRequisitoD4dConDtc || usaDtcInjection;

    /// <summary>La Funzionalita dell'operazione NON e obbligatoria quando il selettore guasto e attivo.</summary>
    public static bool FunzionalitaObbligatoria(bool mostraSelettore, int funzionalitaCollegate) => !mostraSelettore && funzionalitaCollegate >= 2;

    /// <summary>Formato del codice guasto digitato: alfanumerico (con . : _ -), 2-32 caratteri, es. P0123, U0100-00, 0x123456.</summary>
    public static bool CodiceValido(string? codice)
    {
        if (string.IsNullOrWhiteSpace(codice)) return false;
        var c = codice.Trim();
        if (c.Length < 2 || c.Length > 32 || !char.IsLetterOrDigit(c[0])) return false;
        return c.All(ch => (ch < 128 && char.IsLetterOrDigit(ch)) || ch is '.' or ':' or '_' or '-');
    }

    /// <summary>Abbina il codice digitato al guasto di un requisito Extra COLLEGATO al test per codice + componente dell'operazione
    /// (componente vuote = qualunque). Ritorna l'id del requisito o null (nessuna corrispondenza: il guasto resta valido e finisce in
    /// Audit (i guasti liberi non entrano in Audit ne sono segnalati); oppure abbinamento ambiguo, vedi <see cref="AbbinaConEsito"/>).</summary>
    public static string? Abbina(IEnumerable<Requisito> requisitiCollegati, string codice, IReadOnlyCollection<string> ecuOperazione,
        Func<Requisito, IReadOnlyList<DtcRequisito>>? voci = null, string? veicolo = null) =>
        AbbinaConEsito(requisitiCollegati, codice, ecuOperazione, voci, veicolo).RequisitoId;

    /// <summary>Esito dell'abbinamento: con componente operazione vuota ("qualunque") e PIU requisiti candidati distinti l'abbinamento e
    /// AMBIGUO e NON si sceglie nessun requisito (niente scelta arbitraria per ordine alfabetico). Con componente specificata, a parita
    /// vince il requisito con Id minore (deterministico). Il veicolo (6.5.1) filtra come il componente: vuoto/null = qualunque
    /// (comportamento invariato per i dati esistenti), altrimenti la voce deve avere ESATTAMENTE quel veicolo
    /// (case-insensitive); una voce senza veicolo non corrisponde a un veicolo specifico.</summary>
    public static (string? RequisitoId, bool Ambiguo) AbbinaConEsito(IEnumerable<Requisito> requisitiCollegati, string codice, IReadOnlyCollection<string> ecuOperazione,
        Func<Requisito, IReadOnlyList<DtcRequisito>>? voci = null, string? veicolo = null)
    {
        voci ??= r => r.DtcVoci; // i guasti archiviati si passano risolti dal chiamante (RequisitoDtcArchivio)
        var c = (codice ?? "").Trim();
        if (c.Length == 0) return (null, false);
        var sv = (veicolo ?? "").Trim();
        var candidati = requisitiCollegati.Where(RequisitoApplicabilita.ED4D)
            .Where(r => voci(r).Any(v => v.Codice.Trim().Equals(c, StringComparison.OrdinalIgnoreCase)
                    && (ecuOperazione.Count == 0 || ecuOperazione.Contains(v.Componente.Trim(), StringComparer.OrdinalIgnoreCase))
                    && (sv.Length == 0 || v.VeicoloNorm.Equals(sv, StringComparison.OrdinalIgnoreCase))))
            .Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
        if (candidati.Count == 0) return (null, false);
        if (candidati.Count > 1 && ecuOperazione.Count == 0) return (null, true);
        return (candidati[0], false);
    }

    /// <summary>Operazione guasto attesa per uno strumento (6.5.1): una per (voce guasto, passo). Valore puro, non persistito.</summary>
    public sealed record OperazioneAttesa(string Strumento, string RequisitoId, string? Veicolo, string Componente, string Codice, PassoDtc Passo);

    /// <summary>I due passi di OGNI guasto, nell'ordine Validazione poi Devalidazione.</summary>
    public static IReadOnlyList<PassoDtc> CoppiaDtc() => new[] { PassoDtc.Validazione, PassoDtc.Devalidazione };

    /// <summary>Operazioni guasto attese per uno strumento in funzione del suo tipo (correzione 6.5.1 fase 3): SOLO i
    /// <see cref="TipoStrumento.Comportamento"/> (es. guasto Injection) hanno la <see cref="CoppiaDtc"/> (Validazione + Devalidazione)
    /// per ogni guasto selezionato. I <see cref="TipoStrumento.DaTestare"/> (UnitLogger, Easy) lavorano per FUNZIONALITA (vedi
    /// <see cref="FunzionalitaAttese"/>) e i <see cref="TipoStrumento.Supporto"/> (ValidationTool, CaptureTool) non generano operazioni.
    /// Ordine deterministico (veicolo, componente, codice, passo), senza duplicati sulla chiave (veicolo, componente, codice) del requisito.</summary>
    public static IReadOnlyList<OperazioneAttesa> OperazioniAttese(TipoStrumento tipo, string strumento, string requisitoId,
        IEnumerable<DtcRequisito> dtcSelezionati)
    {
        if (tipo != TipoStrumento.Comportamento) return Array.Empty<OperazioneAttesa>();
        var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var risultato = new List<OperazioneAttesa>();
        var ordinate = dtcSelezionati
            .Where(v => !string.IsNullOrWhiteSpace(v.Codice))
            .OrderBy(v => v.VeicoloNorm, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Componente.Trim(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Codice.Trim(), StringComparer.OrdinalIgnoreCase);
        foreach (var v in ordinate)
        {
            if (!viste.Add(DtcRequisito.Chiave(v))) continue;
            foreach (var passo in CoppiaDtc())
                risultato.Add(new OperazioneAttesa(strumento, requisitoId, v.VeicoloNorm.Length == 0 ? null : v.VeicoloNorm,
                    v.Componente.Trim(), v.Codice.Trim(), passo));
        }
        return risultato;
    }

    /// <summary>Funzionalita per cui uno strumento <see cref="TipoStrumento.DaTestare"/> ha una propria operazione (una per
    /// funzionalita collegata al test, nell'ordine dato, senza duplicati case-insensitive). Gli altri tipi: nessuna.</summary>
    public static IReadOnlyList<string> FunzionalitaAttese(TipoStrumento tipo, IEnumerable<string> funzionalitaDelTest)
    {
        if (tipo != TipoStrumento.DaTestare) return Array.Empty<string>();
        var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return funzionalitaDelTest.Select(f => (f ?? "").Trim()).Where(f => f.Length > 0 && viste.Add(f)).ToList();
    }

    /// <summary>guasto PROPONIBILI nel blocco "Aggiungi guasto" del Test (6.5.1): le voci dei requisiti Extra collegati, per il componente scelta e per il/i
    /// veicolo/i del test (una voce senza veicolo vale per ogni veicolo; veicoli del test vuoti = nessun filtro). componente non scelta = nessuna
    /// proposta. Voci gia aggiunte (<paramref name="giaPresente"/>) escluse. Ordine deterministico (codice, requisito), senza duplicati.</summary>
    public static List<DtcOpzione> DtcDisponibili(IEnumerable<Requisito> requisitiCollegati, Func<Requisito, IReadOnlyList<DtcRequisito>> voci,
        IReadOnlyCollection<string> veicoliTest, string? componente, Func<DtcOpzione, bool>? giaPresente = null)
    {
        var e = (componente ?? "").Trim();
        var risultato = new List<DtcOpzione>();
        if (e.Length == 0) return risultato;
        var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in requisitiCollegati.Where(RequisitoApplicabilita.ED4D))
            foreach (var v in voci(req))
            {
                if (!v.Componente.Trim().Equals(e, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(v.Codice)) continue;
                if (v.VeicoloNorm.Length > 0 && veicoliTest.Count > 0 && !veicoliTest.Contains(v.VeicoloNorm, StringComparer.OrdinalIgnoreCase)) continue;
                var o = new DtcOpzione(req.Id, v.Codice.Trim(), e, v.VeicoloNorm.Length == 0 ? null : v.VeicoloNorm);
                if (giaPresente?.Invoke(o) == true) continue;
                if (viste.Add(ChiaveDtc(o.RequisitoId, o.Veicolo, o.Componente, o.Codice))) risultato.Add(o);
            }
        return risultato.OrderBy(o => o.Codice, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.RequisitoId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Veicolo ?? "", StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Identita di un guasto aggiunto al Test: (requisito, veicolo, componente, codice), case-insensitive; il requisito puo essere vuoto.</summary>
    public static string ChiaveDtc(string? requisitoId, string? veicolo, string? componente, string? codice) =>
        (requisitoId ?? "").Trim() + "#" + DtcRequisito.Chiave(veicolo, componente, codice);

    /// <summary>Validazione dell'aggiunta di UN guasto al Test. Ritorna null se valida, altrimenti la chiave di localizzazione dell'errore.
    /// <paramref name="ecuAmmesse"/>: componente proponibili (menu); <paramref name="requisitiAssegnabili"/>: requisiti Extra collegati al test.
    /// Il requisito e opzionale (vuoto = guasto libero, fuori da Audit).</summary>
    public static string? ValidaAggiuntaDtc(string? codice, string? componente, IReadOnlyCollection<string> ecuAmmesse, string? requisitoId,
        IReadOnlyCollection<string> requisitiAssegnabili, bool giaPresente)
    {
        if (!CodiceValido(codice)) return "DtcCodiceNonValido";
        var e = (componente ?? "").Trim();
        if (e.Length == 0) return "DtcEcuMancante";
        if (!ecuAmmesse.Contains(e, StringComparer.OrdinalIgnoreCase)) return "DtcEcuNonInAnagrafica";
        var r = (requisitoId ?? "").Trim();
        if (r.Length > 0 && !requisitiAssegnabili.Contains(r, StringComparer.OrdinalIgnoreCase)) return "DtcRequisitoNonCollegato";
        return giaPresente ? "DtcGiaPresente" : null;
    }

    /// <summary>Uno strumento si puo deselezionare (con rimozione delle sue operazioni generate) solo se NESSUNA sua operazione ha un
    /// esito registrato (diverso da "Da eseguire") e non esistono REV registrate che le contengano. Le operazioni passate sono quelle
    /// di proprieta dello strumento (Condizione.Strumento); le operazioni legacy senza strumento non vanno mai passate/cancellate.</summary>
    public static bool PuoDeselezionare(IEnumerable<Condizione> operazioniStrumento, bool haRevRegistrate = false) =>
        !haRevRegistrate && operazioniStrumento.All(c => c.Esito == Esito.DaEseguire);

    /// <summary>Dati inseriti dall'utente su un'operazione ancora "Da eseguire" che andrebbero persi eliminandola: identificatori stabili
    /// (Nota, MotivazioneNA, Deviazione, Problema, Valori:n, Segnalazione). NON conta la precompilazione automatica "Guasto - Atteso" (uguale al
    /// guasto dell'operazione): non e un input dell'utente.</summary>
    public static IReadOnlyList<string> DatiUtente(Condizione c)
    {
        var d = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Nota)) d.Add("Nota");
        if (!string.IsNullOrWhiteSpace(c.MotivazioneNonApplicabile)) d.Add("MotivazioneNA");
        if (!string.IsNullOrWhiteSpace(c.DescrizioneDeviazione)) d.Add("Deviazione");
        if (!string.IsNullOrWhiteSpace(c.DescrizioneProblema)) d.Add("Problema");
        // La durata (TIME) e' una chiave riservata di ValoriStrumenti: non e' un dato di prova inserito dall'utente.
        var valori = c.ValoriStrumenti.Count(kv => !string.IsNullOrWhiteSpace(kv.Value)
            && !EvidenzaOperazione.EChiaveRiservata(kv.Key)
            && !(kv.Key == ChiaveAtteso && kv.Value.Trim().Equals((c.Guasto ?? "").Trim(), StringComparison.OrdinalIgnoreCase)));
        if (valori > 0) d.Add("Valori:" + valori);
        if (c.SegnalazioneUid != null) d.Add("Segnalazione");
        return d;
    }

    public static bool HaDatiUtente(Condizione c) => DatiUtente(c).Count > 0;
}
