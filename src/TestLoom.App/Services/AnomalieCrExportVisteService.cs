using System.Globalization;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Services;

/// <summary>Identificativi stabili delle sezioni dell'export Anomalie/CR (salvati nelle configurazioni).</summary>
public static class AnomalieSezioniExport
{
    public const string VistaNc = "vistaNc", VistaCr = "vistaCr", DatiNc = "datiNc", DatiCr = "datiCr", Info = "info";
}

/// <summary>Cosa aggiungere ai fogli dati dell'export Anomalie/CR e con quali colonne delle viste (nessuna lista = tutte).</summary>
public sealed record OpzioniVisteAnomalie(bool VistaNc = true, bool VistaCr = true, bool Info = true,
    IReadOnlyList<string>? ColonneVistaNc = null, IReadOnlyList<string>? ColonneVistaCr = null);

/// <summary>
/// Export Anomalie / Change Request per elaborazioni successive: oltre ai fogli dati completi (tutti i campi, UID compresi), aggiunge
/// le tabelle "come visualizzate" nella pagina (stesse colonne, stesso ordine di riga e stessi testi) e un foglio Info con filtri,
/// ordinamento e contenuto del file. Le viste passano per i fogli in testa, i dati restano sotto. Le colonne delle viste si scelgono
/// per identificativo, come nel dialogo di export.
/// </summary>
public static class AnomalieCrExportVisteService
{
    private static string T(string chiave) => LocalizationService.Instance.Traduci(chiave);

    private static readonly (string Id, string Chiave, Func<NcRiga, object?> Valore)[] ColonneNc =
    {
        ("id", "ColId", r => r.Nc.Id), ("test", "ColTestCollegato", r => r.TestCaseIdVisualizzato),
        ("stato", "ColStatoAnomalia", r => r.StatoWorkflowTesto), ("priorita", "ColPriorita", r => r.PrioritaTesto),
        ("gravita", "ColGravita", r => r.GravitaTesto), ("assegnatario", "ColAssegnatario", r => r.AssegnatarioTesto),
        ("apertura", "ColApertura", r => r.DataAperturaTesto), ("chiusura", "ColChiusura", r => r.DataChiusuraTesto),
        ("descbreve", "ColDescrizioneBreve", r => r.DescrizioneBreveVisualizzata), ("descrizione", "ColDescrizioneLunga", r => r.Nc.Descrizione),
        ("ticket", "ColTicketNc", r => r.TicketTesto), ("veicoli", "ColVeicoliAssociati", r => r.VeicoliTesto),
    };

    private static readonly (string Id, string Chiave, Func<ChangeRequest, object?> Valore)[] ColonneCr =
    {
        ("id", "ColId", cr => cr.Id), ("titolo", "CrEdTitoloLabel", cr => cr.Titolo), ("descbreve", "ColDescrizioneBreve", cr => cr.DescrizioneBreve),
        ("descrizione", "ColDescrizioneLunga", cr => cr.Descrizione),
        ("ticket", "ColTicketNc", cr => string.Join(", ", cr.TicketCollegati.Select(t => t.Riferimento).Where(t => !string.IsNullOrWhiteSpace(t)))),
        ("stato", "ColStato", cr => (string)WorkflowLabelConverter.Instance.Convert(cr.Stato, typeof(string), null, CultureInfo.CurrentCulture)!),
        ("veicoli", "ColVeicoliAssociati", cr => Veicoli(cr.VeicoliImpattati)), ("apertura", "ColApertura", cr => Data(cr.DataCreazione)),
        ("implementazione", "ColImplementazione", cr => Data(cr.ImplementataIl)), ("chiusura", "ColChiusura", cr => Data(cr.ChiusaIl)),
    };

    public static IReadOnlyList<ExportColonna> ColonneEsportabiliVistaNc() => ColonneNc.Select(c => new ExportColonna(c.Id, T(c.Chiave))).ToList();
    public static IReadOnlyList<ExportColonna> ColonneEsportabiliVistaCr() => ColonneCr.Select(c => new ExportColonna(c.Id, T(c.Chiave))).ToList();

    private static object? ValorePivotNc(NcRiga r, string id, Func<NcRiga, object?> valore) => id switch
    {
        "veicoli" => r.VeicoliElencoTooltip,
        "apertura" => r.Nc.DataApertura,
        "chiusura" => r.Nc.DataChiusura,
        _ => valore(r),
    };

    public static IEnumerable<IReadOnlyList<object?>> RighePivotNc(IEnumerable<NcRiga> righe) =>
        righe.Select(r => (IReadOnlyList<object?>)ColonneNc.Select(c => ValorePivotNc(r, c.Id, c.Valore)).ToArray());
    public static IEnumerable<IReadOnlyList<object?>> RighePivotNcPerVeicolo(IEnumerable<NcRiga> righe, IReadOnlyCollection<string>? veicoliFiltro = null)
    {
        var filtro = veicoliFiltro == null ? null : new HashSet<string>(veicoliFiltro, StringComparer.OrdinalIgnoreCase);
        foreach (var r in righe)
        {
            var veicoli = AnomalieOrdinamentoConTestComparer.VeicoliAssociati(r.Nc, r.TestCase)
                .Where(v => filtro == null || filtro.Contains(v)).ToList();
            if (veicoli.Count == 0 && filtro == null) veicoli.Add("");
            foreach (var veicolo in veicoli)
                yield return ColonneNc.Select(c => c.Id == "veicoli" ? (object?)veicolo : ValorePivotNc(r, c.Id, c.Valore)).ToArray();
        }
    }

    private static object? ValorePivotCr(ChangeRequest r, string id, Func<ChangeRequest, object?> valore) => id switch
    {
        "veicoli" => string.Join(", ", r.VeicoliImpattati.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase)),
        "apertura" => r.DataCreazione,
        "implementazione" => r.ImplementataIl,
        "chiusura" => r.ChiusaIl,
        _ => valore(r),
    };

    public static IEnumerable<IReadOnlyList<object?>> RighePivotCr(IEnumerable<ChangeRequest> righe) =>
        righe.Select(r => (IReadOnlyList<object?>)ColonneCr.Select(c => ValorePivotCr(r, c.Id, c.Valore)).ToArray());

    public static void Scrivi(XLWorkbook wb, IReadOnlyList<NcRiga> righeNc, IReadOnlyList<ChangeRequest> changeRequests,
        string operatore, DateTime quando, string riepilogoFiltriNc, string riepilogoFiltriCr,
        AnomalieOrdinamento ordinamentoNc, ChangeRequestOrdinamento ordinamentoCr, CancellationToken ct, OpzioniVisteAnomalie? opzioni = null)
    {
        opzioni ??= new OpzioniVisteAnomalie();
        var fogliDati = wb.Worksheets.Select(w => w.Name).ToList();
        IXLWorksheet? vistaNc = null, vistaCr = null;
        if (opzioni.VistaNc) vistaNc = ScriviVistaNc(wb, righeNc, opzioni.ColonneVistaNc, ct);
        if (opzioni.VistaCr) vistaCr = ScriviVistaCr(wb, changeRequests, opzioni.ColonneVistaCr, ct);
        var posizione = 1;
        if (vistaNc != null) vistaNc.Position = posizione++;
        if (vistaCr != null) vistaCr.Position = posizione;
        if (opzioni.Info)
            ScriviInfo(wb, righeNc.Count, changeRequests.Count, operatore, quando, riepilogoFiltriNc, riepilogoFiltriCr,
                ordinamentoNc, ordinamentoCr, vistaNc?.Name, vistaCr?.Name, fogliDati);
    }

    private static string Data(DateTime? d) => d.HasValue ? d.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";

    private static string Veicoli(IEnumerable<string>? v) =>
        (string)VeicoliSommarioConverter.Instance.Convert(v ?? Enumerable.Empty<string>(), typeof(string), null, CultureInfo.CurrentCulture)!;

    private static IXLWorksheet ScriviVistaNc(XLWorkbook wb, IReadOnlyList<NcRiga> righe, IReadOnlyList<string>? scelte, CancellationToken ct)
    {
        var intestazioni = ColonneNc.Select(c => T(c.Chiave)).ToList();
        var indici = ExportColonneSelezione.Indici(ColonneNc.Select(c => c.Id).ToList(), scelte);
        var writer = new ExcelExportTableWriter(wb, T("ExpFoglioVistaNc"), ExportColonneSelezione.Estrai(intestazioni, indici));
        foreach (var r in righe)
        {
            ct.ThrowIfCancellationRequested();
            writer.AggiungiRiga(r.Nc.Id, indici.Select(i => ColonneNc[i].Valore(r)).ToList());
        }
        writer.Completa();
        return wb.Worksheet(writer.NomeFoglioPrincipale);
    }

    private static IXLWorksheet ScriviVistaCr(XLWorkbook wb, IReadOnlyList<ChangeRequest> crs, IReadOnlyList<string>? scelte, CancellationToken ct)
    {
        var intestazioni = ColonneCr.Select(c => T(c.Chiave)).ToList();
        var indici = ExportColonneSelezione.Indici(ColonneCr.Select(c => c.Id).ToList(), scelte);
        var writer = new ExcelExportTableWriter(wb, T("ExpFoglioVistaCr"), ExportColonneSelezione.Estrai(intestazioni, indici));
        foreach (var cr in crs)
        {
            ct.ThrowIfCancellationRequested();
            writer.AggiungiRiga(cr.Id, indici.Select(i => ColonneCr[i].Valore(cr)).ToList());
        }
        writer.Completa();
        return wb.Worksheet(writer.NomeFoglioPrincipale);
    }

    private static void ScriviInfo(XLWorkbook wb, int righeNc, int righeCr, string operatore, DateTime quando, string filtriNc,
        string filtriCr, AnomalieOrdinamento ordNc, ChangeRequestOrdinamento ordCr, string? nomeVistaNc, string? nomeVistaCr,
        IReadOnlyList<string> fogliDati)
    {
        var nome = T("ExpFoglioInfo");
        for (var n = 2; wb.Worksheets.Any(w => string.Equals(w.Name, nome, StringComparison.OrdinalIgnoreCase)); n++) nome = $"{T("ExpFoglioInfo")} ({n})";
        var ws = wb.Worksheets.Add(nome);
        ws.Cell(1, 1).Value = T("ExpInfoTitolo");
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        string Direzione(bool desc) => desc ? "▼" : "▲";
        var voci = new (string Etichetta, string Valore)[]
        {
            (T("ExpInfoGenerato"), quando.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)),
            (T("ExpInfoOperatore"), operatore ?? ""),
            (T("ExpInfoVersione"), typeof(AnomalieCrExportVisteService).Assembly.GetName().Version?.ToString(3) ?? ""),
            (T("ExpInfoRigheNc"), righeNc.ToString(CultureInfo.InvariantCulture)),
            (T("ExpInfoRigheCr"), righeCr.ToString(CultureInfo.InvariantCulture)),
            (T("ExpInfoFiltriNc"), string.IsNullOrWhiteSpace(filtriNc) ? "—" : filtriNc),
            (T("ExpInfoFiltriCr"), string.IsNullOrWhiteSpace(filtriCr) ? "—" : filtriCr),
            (T("ExpInfoOrdineNc"), $"{ordNc.Chiave} {Direzione(ordNc.Decrescente)}"),
            (T("ExpInfoOrdineCr"), $"{ordCr.Chiave} {Direzione(ordCr.Decrescente)}"),
        };
        var riga = 3;
        foreach (var (etichetta, valore) in voci)
        {
            ws.Cell(riga, 1).SetValue(etichetta);
            ws.Cell(riga, 1).Style.Font.SetBold();
            ws.Cell(riga, 2).SetValue(valore);
            riga++;
        }
        riga++;
        ws.Cell(riga, 1).SetValue(T("ExpInfoFogli"));
        ws.Cell(riga, 1).Style.Font.SetBold().Font.FontSize = 12;
        riga++;
        if (nomeVistaNc != null) { ws.Cell(riga, 1).SetValue(nomeVistaNc); ws.Cell(riga, 2).SetValue(T("ExpDescVistaNc")); riga++; }
        if (nomeVistaCr != null) { ws.Cell(riga, 1).SetValue(nomeVistaCr); ws.Cell(riga, 2).SetValue(T("ExpDescVistaCr")); riga++; }
        foreach (var foglio in fogliDati)
        {
            ws.Cell(riga, 1).SetValue(foglio);
            ws.Cell(riga, 2).SetValue(T("ExpDescDatiAnomalie"));
            riga++;
        }
        ws.Column(1).Width = 36;
        ws.Column(2).Width = 100;
    }
}
