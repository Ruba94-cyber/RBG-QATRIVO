using System.Text;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Riga di dettaglio guasto (Audit, sezione Extra, e relativi export): stato, conflitto, passo mancante, ultimo test e
/// data (+ in coda, dalla 6.5.1: veicolo e allineamento: le colonne precedenti non cambiano posizione). I testi sono calcolati lazy (lista virtualizzata a 100k guasto: solo le righe a schermo li formattano).</summary>
public sealed class DtcDettaglioRiga
{
    public DtcDettaglioRiga(DtcAvanzamento a) { Origine = a; }
    public DtcAvanzamento Origine { get; }
    public string Chiave => Origine.Chiave;
    public string RequisitoId => Origine.RequisitoId;
    public string Guasto => Origine.Guasto;
    public string Componente => Origine.Componente;
    /// <summary>Veicolo della cella; "" = voce senza veicolo.</summary>
    public string Veicolo => Origine.Veicolo;
    public string VeicoloTesto => Origine.Veicolo.Length == 0 ? DtcRequisito.SenzaVeicolo : Origine.Veicolo;
    /// <summary>Il veicolo della voce non e piu tra quelli dichiarati dal requisito (rimosso/rinominato): da riallineare.</summary>
    public bool NonAllineata => Origine.NonAllineata;
    /// <summary>Veicolo con il segnale di non allineamento (colonna dell'elenco in Audit).</summary>
    public string VeicoloConAvviso => VeicoloTesto + (NonAllineata ? " ⚠" : "");
    /// <summary>AutomationId della riga: invariato (Audit_Guasto_Voce_{guasto}) senza veicolo, altrimenti distinto per veicolo ed componente.</summary>
    public string AutomationId => Origine.Veicolo.Length == 0 ? $"Audit_Guasto_Voce_{Guasto}"
        : $"Audit_Guasto_Voce_{Guasto}_{Origine.Veicolo.Replace(' ', '_')}_{Componente.Replace(' ', '_')}";
    public string AllineamentoTesto => Origine.NonAllineata ? LocalizationService.Instance.Traduci("DtcNonAllineata") : "";
    public StatoDtc Stato => Origine.Stato;
    public bool Conflitto => Origine.Conflitto;
    public string StatoTesto => DtcAvanzamentoProvider.StatoEtichetta(Origine);
    public string PassoMancanteTesto => DtcAvanzamentoProvider.PassoMancante(Origine);
    public string SignificatoStato => DescrizioneStato(Stato);

    public static string DescrizioneStato(StatoDtc stato) => LocalizationService.Instance.Traduci(stato switch
    {
        StatoDtc.Validato => "DtcStatoDescrizioneValidato",
        StatoDtc.Completo => "DtcStatoDescrizioneCompleto",
        StatoDtc.NonConforme => "DtcStatoDescrizioneNonConforme",
        _ => "DtcStatoDescrizioneDaFare",
    });

    public static string DescrizioniStatiPerNotaExcel() => string.Join(Environment.NewLine, Enum.GetValues<StatoDtc>()
        .Select(stato => $"{stato}: {DescrizioneStato(stato)}"));

    /// <summary>Test dell'ultimo esito registrato (il piu recente tra validazione e devalidazione); vuoto se nessuno.</summary>
    public string UltimoTestId => UltimoPasso().TestId;
    public DateTime? UltimaData => UltimoPasso().Data;
    public string UltimaDataTesto => UltimaData is { } d ? d.ToString("dd/MM/yy HH:mm") : "";
    public bool HaTest => UltimoTestId.Length > 0;

    private (string TestId, DateTime? Data) UltimoPasso()
    {
        var a = Origine;
        if (a.DataValidazione is { } v && (a.DataDevalidazione is not { } d || v >= d)) return (a.TestIdValidazione, v);
        if (a.DataDevalidazione is { } dd) return (a.TestIdDevalidazione, dd);
        return ("", null);
    }

    private static string Csv(string v) => v.Contains('"') || v.Contains(';') || v.Contains('\n') || v.Contains('\r')
        ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    public const string IntestazioneCsv = "Requisito;guasto;componente;Stato;Conflitto;Passo mancante;Ultimo test;Ultima data;Veicolo;Allineamento;Significato stato";

    public string RigaCsv() => string.Join(';', Csv(RequisitoId), Csv(Guasto), Csv(Componente), Csv(Origine.Stato.ToString()),
        Conflitto ? "si" : "", Csv(PassoMancanteTesto), Csv(UltimoTestId), UltimaDataTesto, Csv(Veicolo), NonAllineata ? "non allineata" : "", Csv(SignificatoStato));

    /// <summary>Esporta il dettaglio guasto come CSV in STREAMING (una riga per volta, nessuna struttura intermedia).</summary>
    public static void ScriviCsv(TextWriter w, IEnumerable<DtcAvanzamento> righe, CancellationToken ct = default)
    {
        w.WriteLine(IntestazioneCsv);
        foreach (var a in righe)
        {
            ct.ThrowIfCancellationRequested();
            w.WriteLine(new DtcDettaglioRiga(a).RigaCsv());
        }
    }
}
