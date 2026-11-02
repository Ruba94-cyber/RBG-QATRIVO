using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Gruppo di operazioni dello stesso strumento: il contatore e SOLO un riepilogo (l'esito del test resta calcolato per
/// operazione, nessun nuovo aggregato). <see cref="Strumento"/> null = operazioni legacy/manuali senza strumento.</summary>
public sealed record GruppoOperazioniStrumento(string? Strumento, IReadOnlyList<Condizione> Operazioni, int Totale, int Ok)
{
    /// <summary>Esempio "UnitLogger 2/3 OK" (testo della lingua corrente).</summary>
    public string Contatore => string.Format(LocalizationService.Instance.Traduci("OperazioniStrumentoContatoreFmt"), Strumento, Ok, Totale);
}

/// <summary>Regole PURE (6.5.1, fase 3) delle operazioni di PROPRIETA di uno strumento (Condizione.Strumento): quali mancano, come
/// si raggruppano per strumento e con quale contatore. Non hanno stato: l'editor le applica alla REV corrente. Le operazioni
/// legacy (Strumento == null) non appartengono a nessuno strumento e non sono mai oggetto di creazione/rimozione automatica.</summary>
public static class OperazioniStrumentoRegole
{
    /// <summary>Operazione "OK" = eseguita e non Non Conforme (Conforme o Conforme con deviazioni).</summary>
    public static bool EOk(Esito e) => e is Esito.Conforme or Esito.ConformeConDeviazioni;

    /// <summary>Raggruppa per strumento proprietario (ordine di prima comparsa; il gruppo legacy, se presente, e escluso: non e uno
    /// strumento). Il conteggio usa le operazioni EFFETTIVE (ultimo tentativo di ogni catena di ripetizioni); le operazioni del
    /// gruppo sono tutte quelle dello strumento, in ordine di lista.</summary>
    public static IReadOnlyList<GruppoOperazioniStrumento> Raggruppa(IEnumerable<Condizione> tutte, IEnumerable<Condizione> effettive)
    {
        var effSet = effettive.ToHashSet();
        var risultato = new List<GruppoOperazioniStrumento>();
        var visti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var elenco = tutte.ToList();
        foreach (var nome in elenco.Select(c => c.Strumento).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()))
        {
            if (!visti.Add(nome)) continue;
            var ops = elenco.Where(c => Appartiene(c, nome)).ToList();
            var eff = ops.Where(effSet.Contains).ToList();
            risultato.Add(new GruppoOperazioniStrumento(nome, ops, eff.Count, eff.Count(c => EOk(c.Esito))));
        }
        return risultato;
    }

    /// <summary>6.5.2: la combo Funzionalita di riga compare solo per le operazioni il cui Strumento PROPRIETARIO e un DaTestare; per le
    /// operazioni di altri strumenti (guasto Injection, ValidationTool...) mai. Le legacy (Strumento null) restano editabili salvo nei test
    /// "solo guasto"; le operazioni guasto legacy senza proprietario conservano il comportamento precedente.</summary>
    public static bool FunzionalitaRigaVisibile(string? strumentoProprietario, bool soloOperazioniDtc, IEnumerable<string>? strumentiDaTestare)
    {
        if (string.IsNullOrWhiteSpace(strumentoProprietario)) return !soloOperazioniDtc;
        var nome = strumentoProprietario.Trim();
        return strumentiDaTestare != null && strumentiDaTestare.Any(n => string.Equals((n ?? "").Trim(), nome, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Appartiene(Condizione c, string strumento) =>
        !string.IsNullOrWhiteSpace(c.Strumento) && c.Strumento.Trim().Equals(strumento.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Funzionalita del test senza ancora un'operazione dello strumento (solo tipo DaTestare).</summary>
    public static IReadOnlyList<string> FunzionalitaMancanti(TipoStrumento tipo, string strumento, IEnumerable<Condizione> operazioni,
        IEnumerable<string> funzionalitaDelTest)
    {
        var coperte = operazioni.Where(c => Appartiene(c, strumento)).Select(c => (c.Funzionalita ?? "").Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DtcOperazioneRegole.FunzionalitaAttese(tipo, funzionalitaDelTest).Where(f => !coperte.Contains(f)).ToList();
    }

    /// <summary>Operazione guasto da creare e l'operazione esistente da cui deriva (componente e funzionalita da copiare).</summary>
    public sealed record DtcDaCreare(DtcOperazioneRegole.OperazioneAttesa Attesa, Condizione Sorgente);

    /// <summary>Per un <see cref="TipoStrumento.Comportamento"/>: le coppie (Validazione + Devalidazione) mancanti per i guasti GIA presenti
    /// nelle operazioni (di qualunque proprietario: un passo gia coperto, anche da un'operazione legacy, non si duplica). La creazione di
    /// nuovi guasto e un'altra fase. Riusa <see cref="DtcOperazioneRegole.OperazioniAttese"/> per ordine e dedup.</summary>
    public static IReadOnlyList<DtcDaCreare> CoppieDtcMancanti(TipoStrumento tipo, string strumento, IEnumerable<Condizione> operazioni)
    {
        if (tipo != TipoStrumento.Comportamento) return Array.Empty<DtcDaCreare>();
        var dtcOps = operazioni.Where(c => c.RiguardaDtc).ToList();
        var coperti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in dtcOps)
            if (c.DtcPasso is { } p) coperti.Add(ChiavePasso(c.DtcRequisitoId, c.DtcVeicolo, EcuDi(c), c.Guasto, p));

        var risultato = new List<DtcDaCreare>();
        foreach (var perRequisito in dtcOps.GroupBy(c => c.DtcRequisitoId ?? "", StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sorgentePerChiave = new Dictionary<string, Condizione>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in perRequisito) sorgentePerChiave.TryAdd(DtcRequisito.Chiave(c.DtcVeicolo, EcuDi(c), c.Guasto), c);
            var voci = sorgentePerChiave.Values.Select(c => new DtcRequisito { Codice = c.Guasto!, Componente = EcuDi(c), Veicolo = c.DtcVeicolo });
            foreach (var att in DtcOperazioneRegole.OperazioniAttese(TipoStrumento.Comportamento, strumento, perRequisito.Key, voci))
            {
                if (coperti.Contains(ChiavePasso(perRequisito.Key, att.Veicolo, att.Componente, att.Codice, att.Passo))) continue;
                risultato.Add(new DtcDaCreare(att, sorgentePerChiave[DtcRequisito.Chiave(att.Veicolo, att.Componente, att.Codice)]));
            }
        }
        return risultato;
    }

    private static string EcuDi(Condizione c) => string.Join(",", c.Componente.Select(e => e.Trim()).Where(e => e.Length > 0));

    private static string ChiavePasso(string? requisito, string? veicolo, string? componente, string? codice, PassoDtc passo) =>
        $"{(requisito ?? "").Trim()}|{DtcRequisito.Chiave(veicolo, componente, codice)}|{passo}";
}
