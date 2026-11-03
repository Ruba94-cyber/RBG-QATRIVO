using ClosedXML.Excel;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Esportazione della matrice Audit completa (Addendum: grill-me 2026-08-18, follow-up, punto 5):
/// un foglio per Sottoveicolo, righe = Requisiti, colonne = componente del sottoveicolo, come nel file d'esempio
/// fornito dall'utente. A differenza della vista in-app (4 stati), l'esportazione mostra solo numeri puri
/// 0-5 o "N/A" — le celle non ancora valutate (Da testare / Test creato non eseguito) restano vuote, per non
/// mescolare "non ancora valutato" con un vero e proprio N/A ("l'esportazione non deve tirare fuori gli stati
/// ma esclusivamente i numeri e i valori").</summary>
public static class AuditReportService
{
    public static IReadOnlyList<ExportColonna> CampiPivot() => ExportPivotService.ConNumerici(ExportPivotService.Campi(
        ("veicolo", "Veicolo", "Vehicle", "Véhicule"), ("componente", "Componente", "Componente", "Componente"),
        ("requisito", "Requisito", "Requirement", "Exigence"), ("stato", "Stato", "Status", "Statut"),
        ("punteggio", "Punteggio (N/A = vuoto)", "Score (N/A = empty)", "Score (N/A = vide)"), ("origine", "Origine", "Origin", "Origine"),
        ("data", "Data esecuzione", "Execution date", "Date exécution"), ("manuale", "Inserimento manuale", "Manual entry", "Saisie manuelle"),
        ("motivazione", "Motivazione", "Reason", "Motif"), ("conflitto", "Dettaglio conflitto", "Conflict details", "Détail conflit"),
        ("collisione", "Conflitto collisione", "Collision conflict", "Conflit collision"), ("nota", "Nota", "Note", "Note"),
        ("baseline", "Baseline software", "Software baseline", "Baseline logiciel"), ("dettaglio", "Dettaglio origine", "Origin details", "Détail origine")), "punteggio");

    /// <summary><paramref name="ct"/> viene controllato tra un sottoveicolo e l'altro e tra le righe requisito: un
    /// annullamento interrompe la generazione senza produrre alcun file (vedi EsportazioneInBackground).</summary>
    public static byte[] GeneraExcel(AppState state, CancellationToken ct = default)
        => GeneraExcelCore(state, sottoveicoloSelezionato: null, tipiRequisito: null, ct, null);

    /// <summary>Esporta l'Audit con lo stesso ambito della matrice corrente. Il veicolo è obbligatorio per
    /// impedire che una selezione mancante allarghi silenziosamente l'esportazione a tutti i veicoli. Un filtro
    /// tipi null mantiene entrambi i tipi; un insieme vuoto produce solo legenda e riepiloghi vuoti.</summary>
    public static byte[] GeneraExcel(AppState state, string sottoveicoloSelezionato,
        IReadOnlyCollection<string>? tipiRequisito, CancellationToken ct = default, AuditExportOpzioni? opzioni = null)
    {
        if (string.IsNullOrWhiteSpace(sottoveicoloSelezionato))
            throw new ArgumentException("Selezionare un veicolo prima di esportare l'Audit.", nameof(sottoveicoloSelezionato));
        return GeneraExcelCore(state, sottoveicoloSelezionato.Trim(), tipiRequisito, ct, opzioni);
    }

    private static byte[] GeneraExcelCore(AppState state, string? sottoveicoloSelezionato,
        IReadOnlyCollection<string>? tipiRequisito, CancellationToken ct, AuditExportOpzioni? opzioni)
    {
        ct.ThrowIfCancellationRequested();
        var veicoli = state.Master.Veicoli.Load()
            .Where(v => !v.Archiviata && (sottoveicoloSelezionato == null || v.Nome.Equals(sottoveicoloSelezionato, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(v => v.Nome, StringComparer.OrdinalIgnoreCase).ToList();
        if (sottoveicoloSelezionato != null && veicoli.Count == 0)
            throw new InvalidOperationException($"Il veicolo selezionato '{sottoveicoloSelezionato}' non è più disponibile.");
        var tutteLeEcu = state.Master.Componente.Load();
        var requisiti = state.Master.Requisiti.Load();
        if (tipiRequisito != null)
        {
            var tipi = new HashSet<string>(tipiRequisito, StringComparer.OrdinalIgnoreCase);
            requisiti = requisiti.Where(r => tipi.Contains(RequisitoApplicabilita.ED4D(r) ? "Extra" : "Standard")).ToList();
        }
        var testCases = state.TestCases.GetAll();
        var risoluzioni = state.Master.RisoluzioniConflittoAudit.Load();
        var valutazioni = state.Master.ValutazioniRequisitoEcuVeicolo.Load();

        using var wb = new XLWorkbook();
        ScriviLegenda(wb);
        var dopoLegenda = wb.Worksheets.Count;
        var conflitti = new Dictionary<(string Veicolo, string Componente, string RequisitoId), CellaMatriceAudit>();
        // 6.5.0: avanzamento guasto dei requisiti Extra nel blocco Extra (colonna "Guasto completi/totale").
        state.Master.RequisitiDtc.Risolvi(requisiti);
        var d4dConDtc = requisiti.Where(r => !r.Archiviata && RequisitoApplicabilita.ED4D(r) && r.DtcTotale > 0).ToList();
        var dtcRisultato = d4dConDtc.Count == 0 ? null : DtcAvanzamentoProvider.Calcola(state, d4dConDtc);
        var dtcPerReq = dtcRisultato?.PerRequisito.ToDictionary(r => r.RequisitoId, StringComparer.OrdinalIgnoreCase);
        foreach (var veicolo in veicoli.ConAnnullamento(ct))
            ScriviFoglioSottoveicolo(wb, veicolo.Nome, tutteLeEcu, requisiti, testCases, risoluzioni, valutazioni, conflitti, dtcPerReq, ct);
        var dopoMatrice = wb.Worksheets.Count;
        var testoDiRequisito = requisiti.ToDictionary(r => r.Id, r => r.Testo, StringComparer.OrdinalIgnoreCase);
        ScriviFoglioConflitti(wb, conflitti.Values.ToList(), testoDiRequisito, ct);
        var dopoConflitti = wb.Worksheets.Count;
        if (dtcRisultato != null)
        {
            var dettaglioDtc = sottoveicoloSelezionato == null
                ? dtcRisultato.PerDtc
                : dtcRisultato.PerDtc.Where(d => d.Veicolo.Equals(sottoveicoloSelezionato, StringComparison.OrdinalIgnoreCase)).ToList();
            ScriviFoglioDettaglioDtc(wb, dettaglioDtc, ct: ct);
        }
        // Sezioni scelte nel dialogo di export (nessuna scelta = tutto): i fogli di una sezione non scelta si tolgono prima di salvare.
        if (opzioni != null)
        {
            var nomi = wb.Worksheets.Select(w => w.Name).ToList();
            void Togli(int da, int a) { foreach (var n in nomi.Skip(da).Take(a - da)) wb.Worksheet(n).Delete(); }
            if (!opzioni.Legenda) Togli(0, dopoLegenda);
            if (!opzioni.Matrice) Togli(dopoLegenda, dopoMatrice);
            if (!opzioni.Conflitti) Togli(dopoMatrice, dopoConflitti);
            if (!opzioni.DettaglioDtc) Togli(dopoConflitti, nomi.Count);
        }

        if (opzioni?.Pivot?.HaSezione("pivot") == true)
        {
            var celle = veicoli.SelectMany(v => AuditMatriceService.Matrice(v.Nome, tutteLeEcu, requisiti, testCases, risoluzioni, valutazioni: valutazioni));
            var righe = celle.Select(c => (IReadOnlyList<object?>)new object?[] { c.Veicolo, c.Componente, c.RequisitoId, c.Stato.ToString(), c.Punteggio.HasValue && c.Punteggio.Value != PunteggioConformita.NonApplicabile ? (object)(int)c.Punteggio.Value : null, c.Origine?.ToString() ?? "", c.DataEsecuzione, c.InserimentoManuale, c.MotivazioneInserimentoManuale, c.DettaglioConflitto, c.ConflittoCollisioneCella, c.Nota, c.BaselineSw, c.Tooltip });
            ExportPivotService.Scrivi(wb, CampiPivot(), righe, opzioni.Pivot, ct);
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Scrive in streaming le righe già filtrate del drill-down guasto Audit; non ricalcola un ambito più ampio.</summary>
    public static void ScriviDettaglioDtcCsv(IEnumerable<DtcDettaglioRiga> righe, string percorso, CancellationToken ct = default)
    {
        using var w = new StreamWriter(percorso, false, new System.Text.UTF8Encoding(true));
        w.WriteLine(DtcDettaglioRiga.IntestazioneCsv);
        foreach (var riga in righe.ConAnnullamento(ct)) w.WriteLine(riga.RigaCsv());
    }

    internal static void ScriviFoglioDettaglioDtc(XLWorkbook wb, IReadOnlyList<DtcAvanzamento> dettaglio,
        int massimoRighePerFoglio = 1_048_575, CancellationToken ct = default)
    {
        if (massimoRighePerFoglio is < 1 or > 1_048_575) throw new ArgumentOutOfRangeException(nameof(massimoRighePerFoglio));
        var intestazioni = DtcDettaglioRiga.IntestazioneCsv.Split(';');
        var writer = new ExcelExportTableWriter(wb, NomeFoglioLibero(wb, "Guasto Extra"), intestazioni, massimoRighePerFoglio + 1);
        var progressivo = 0;
        foreach (var a in dettaglio.ConAnnullamento(ct))
        {
            var d = new DtcDettaglioRiga(a);
            var valori = new[] { d.RequisitoId, d.Guasto, d.Componente, a.Stato.ToString(), d.Conflitto ? "si" : "", d.PassoMancanteTesto, d.UltimoTestId, d.UltimaDataTesto, d.Veicolo, d.NonAllineata ? "non allineata" : "", d.SignificatoStato };
            writer.AggiungiRiga($"{d.RequisitoId} / {d.Guasto} / {d.Componente} / {++progressivo}", valori);
        }
        writer.Completa();
        foreach (var foglio in wb.Worksheets.Where(f => f.Name.StartsWith("Guasto Extra", StringComparison.OrdinalIgnoreCase)))
            foglio.Cell(1, 4).GetComment().AddText(DtcDettaglioRiga.DescrizioniStatiPerNotaExcel());
    }

    private static void ScriviLegenda(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Legenda");
        ws.Cell(1, 1).Value = "RBG - QATRIVO - Audit di conformità";
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 16;
        ws.Cell(2, 1).Value = "Un foglio per veicolo: righe = Requisiti, colonne = Componenti associati. Ogni cella riporta il punteggio 0-5 dell'ultima esecuzione valutata, oppure N/A; le coppie non ancora valutate restano vuote. Le coppie con Test diversi in disaccordo restano vuote qui e compaiono nel foglio \"Conflitti\".";
        ws.Cell(2, 1).Style.Alignment.WrapText = true;
        ws.Row(2).Height = 30;

        var headerRow = 4;
        ws.Cell(headerRow, 1).Value = LocalizationService.Instance.Traduci("AuditXlsPunteggio");
        ws.Cell(headerRow, 2).Value = LocalizationService.Instance.Traduci("AuditXlsSignificato");
        ws.Range(headerRow, 1, headerRow, 2).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);

        var righe = new (string Punteggio, string Significato)[]
        {
            ("0", "Non compliant"),
            ("1", "Poorly compliant"),
            ("2", "Partially compliant"),
            ("3", "Moderately compliant"),
            ("4", "Largely compliant"),
            ("5", "Compliant"),
            ("N/A", "Not applicable"),
        };
        var r = headerRow + 1;
        foreach (var (punteggio, significato) in righe)
        {
            ws.Cell(r, 1).Value = punteggio;
            ws.Cell(r, 2).Value = significato;
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    /// <summary>Nome di un foglio unico nel workbook: stesso criterio del Report (massimo 31 caratteri, suffisso " (n)" se il nome
    /// esiste gia'). Due veicoli con lo stesso prefisso di 31 caratteri non devono far fallire l'export.</summary>
    private static string NomeFoglioLibero(XLWorkbook wb, string nomeProposto) =>
        ReportService.NomeFoglioUnico(new HashSet<string>(wb.Worksheets.Select(w => w.Name), StringComparer.OrdinalIgnoreCase), nomeProposto);

    private static void ScriviFoglioSottoveicolo(XLWorkbook wb, string veicoloNome, IReadOnlyList<Componente> tutteLeEcu, List<Requisito> requisiti, IReadOnlyList<TestCase> testCases,
        IReadOnlyList<RisoluzioneConflittoAudit> risoluzioni, IReadOnlyList<ValutazioneRequisitoEcuVeicolo> valutazioni,
        Dictionary<(string Veicolo, string Componente, string RequisitoId), CellaMatriceAudit> conflitti,
        Dictionary<string, RequisitoDtcAvanzamento>? dtcPerReq = null, CancellationToken ct = default)
    {
        // Nome foglio Excel: niente : \ / ? * [ ] (limite del formato, non di TestLoom). Lunghezza massima e unicita' in NomeFoglioLibero.
        var nomeFoglio = System.Text.RegularExpressions.Regex.Replace(veicoloNome, @"[:\\/?*\[\]]", "-");
        var ws = wb.Worksheets.Add(NomeFoglioLibero(wb, nomeFoglio));

        var ecuColonne = AuditMatriceService.EcuDelSottoveicolo(tutteLeEcu, veicoloNome);
        var matrice = AuditMatriceService.Matrice(veicoloNome, tutteLeEcu, requisiti, testCases, risoluzioni, valutazioni: valutazioni);
        var perCella = matrice.ToDictionary(c => (c.Componente, c.RequisitoId));

        ws.Cell(1, 1).Value = LocalizationService.Instance.Traduci("AuditRequisito");
        ws.Cell(1, 2).Value = LocalizationService.Instance.Traduci("AuditXlsTesto");
        for (var c = 0; c < ecuColonne.Count; c++) ws.Cell(1, c + 3).Value = ecuColonne[c];
        ws.Range(1, 1, 1, ecuColonne.Count + 2).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);

        // Ordinamento naturale, non alfabetico puro (feedback utente 2026-08-26, stessa correzione di
        // AuditMatriceService): l'export Excel deve elencare i requisiti nello stesso ordine mostrato a schermo.
        var attiviOrdinati = requisiti.Where(rq => !rq.Archiviata).OrderBy(rq => rq.Id, NaturalStringComparer.Instance).ToList();
        // 6.5.0: due blocchi SEPARATI nello stesso foglio, Standard e poi Extra (solo quelli applicabili al
        // sottoveicolo), ciascuno con il proprio totale; i totali Standard non includono i Extra.
        var (reqStandard, reqD4d) = AuditMatriceService.SeparaPerTipo(attiviOrdinati);
        reqD4d = reqD4d.Where(rq => RequisitoApplicabilita.SiApplicaAlSottoveicolo(rq, veicoloNome)).ToList();
        var requisitiAttivi = reqStandard.Concat(reqD4d).ToList();
        var rigaCorrente = 2;
        for (var r = 0; r < requisitiAttivi.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var req = requisitiAttivi[r];
            if (r == reqStandard.Count && reqD4d.Count > 0)
            {
                ScriviTotaleBlocco(ws, rigaCorrente++, "Totale Standard", reqStandard, ecuColonne, perCella);
                ws.Cell(rigaCorrente, 1).Value = "Requisiti Extra";
                ws.Range(rigaCorrente, 1, rigaCorrente, ecuColonne.Count + 2).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGreen);
                rigaCorrente++;
            }
            var riga = rigaCorrente++;
            if (r >= reqStandard.Count && dtcPerReq != null && dtcPerReq.TryGetValue(req.Id, out var dtcReq))
            {
                ws.Cell(riga, ecuColonne.Count + 3).Value = $"{dtcReq.Completi}/{dtcReq.Totale}";
                ws.Cell(1, ecuColonne.Count + 3).Value = "Guasto completi (Extra)";
                ws.Cell(1, ecuColonne.Count + 3).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGreen);
            }
            ws.Cell(riga, 1).Value = req.Id;
            ws.Cell(riga, 2).Value = req.Testo;
            for (var c = 0; c < ecuColonne.Count; c++)
            {
                if (!perCella.TryGetValue((ecuColonne[c], req.Id), out var cella)) continue;
                if (cella.Stato == StatoCellaAudit.Conflitto) { conflitti[(veicoloNome, cella.Componente, cella.RequisitoId)] = cella; continue; }
                var colonna = c + 3;

                // Piano 6.3.1 §1: la cella "propagabile ma non confermata" esporta il simbolo "!" invece del
                // numero (l'esportazione mostra solo numeri puri/N/A per un valore reale — "!" NON è un
                // punteggio, è un invito esplicito a decidere in app se propagare).
                if (cella.Stato == StatoCellaAudit.PropagabileNonConfermato)
                {
                    ws.Cell(riga, colonna).Value = "!";
                    if (!string.IsNullOrWhiteSpace(cella.Tooltip)) ws.Cell(riga, colonna).GetComment().AddText(cella.Tooltip);
                    continue;
                }
                if (cella.Stato != StatoCellaAudit.Valutato) continue;

                if (cella.Punteggio == PunteggioConformita.NonApplicabile) ws.Cell(riga, colonna).Value = "N/A";
                else ws.Cell(riga, colonna).Value = (int)cella.Punteggio!.Value;

                // Marcatura Propagato/Manuale con commento (Piano 6.3.1 §1: "ogni valore non diretto porta
                // nota + testo tooltip che dichiara l'origine") — un valore Diretto non porta alcun commento,
                // stesso comportamento di prima di questo piano.
                // Fix 6.4.0 (R4): collisione di chiave business Phase D con valori discordanti tra due
                // postazioni, non ancora rivista da un operatore (vedi CellaMatriceAudit.ConflittoCollisioneCella)
                // — il report resta un export "solo numeri/N/A" (nessun simbolo nella cella), ma il commento
                // segnala esplicitamente che il valore va verificato in app prima di considerarlo definitivo.
                // Combinato nello stesso commento (una sola AddText) con l'etichetta Manuale/Propagato esistente
                // invece di due commenti separati sulla stessa cella.
                var prefissoConflitto = cella.ConflittoCollisioneCella
                    ? "[CONFLITTO] Valori diversi salvati da due postazioni quasi in contemporanea: verificare e reimpostare la cella in TestLoom. "
                    : "";
                if (cella.Origine is OrigineCellaRequisitoEcuVeicolo.Manuale or OrigineCellaRequisitoEcuVeicolo.Propagata)
                {
                    var etichetta = cella.Origine == OrigineCellaRequisitoEcuVeicolo.Propagata ? "[Propagato] " : "[Manuale] ";
                    ws.Cell(riga, colonna).GetComment().AddText(prefissoConflitto + etichetta + (cella.Tooltip ?? ""));
                }
                else if (cella.ConflittoCollisioneCella)
                {
                    ws.Cell(riga, colonna).GetComment().AddText(prefissoConflitto.TrimEnd());
                }
            }
        }

        if (reqD4d.Count > 0) ScriviTotaleBlocco(ws, rigaCorrente++, "Totale Extra", reqD4d, ecuColonne, perCella);
        else if (reqStandard.Count > 0) ScriviTotaleBlocco(ws, rigaCorrente++, "Totale Standard", reqStandard, ecuColonne, perCella);
        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(2);
        if (ecuColonne.Count == 0)
            ws.Cell(2, 1).Value = "Nessuna componente associata a questo veicolo.";
        else if (requisitiAttivi.Count == 0)
            ws.Cell(2, 1).Value = "Nessun requisito attivo.";
        ws.Columns().AdjustToContents();
    }

    /// <summary>Riga di riepilogo di un blocco (Standard o Extra): celle valutate su celle totali del blocco.</summary>
    private static void ScriviTotaleBlocco(IXLWorksheet ws, int riga, string etichetta, IReadOnlyList<Requisito> reqs,
        IReadOnlyList<string> ecuColonne, Dictionary<(string Componente, string RequisitoId), CellaMatriceAudit> perCella)
    {
        var totali = 0; var valutate = 0;
        foreach (var rq in reqs)
            foreach (var componente in ecuColonne)
            {
                if (!perCella.TryGetValue((componente, rq.Id), out var c)) continue;
                totali++;
                if (c.Stato == StatoCellaAudit.Valutato) valutate++;
            }
        ws.Cell(riga, 1).Value = etichetta;
        ws.Cell(riga, 2).Value = $"{valutate}/{totali} celle valutate";
        ws.Range(riga, 1, riga, 2).Style.Font.SetBold();
    }

    /// <summary>Foglio dedicato ai conflitti (Addendum: grill-me 2026-08-18, seconda follow-up — "foglio extra
    /// dedicato ai conflitti", scelto dall'utente rispetto a lasciare in cella l'ultimo voto o una nota
    /// inline): la cella nel foglio del sottoveicolo resta vuota, qui compare il dettaglio di ogni Test
    /// discordante così chi legge capisce perché manca il numero e può risolverlo dall'app.</summary>
    private static void ScriviFoglioConflitti(XLWorkbook wb, List<CellaMatriceAudit> conflitti, IReadOnlyDictionary<string, string> testoDiRequisito, CancellationToken ct = default)
    {
        var ws = wb.Worksheets.Add(NomeFoglioLibero(wb, "Conflitti"));
        ws.Cell(1, 1).Value = "Celle Audit con conflitto tra fonti di pari data; il dettaglio indica origini, valori e contesto.";
        ws.Cell(1, 1).Style.Alignment.WrapText = true;
        ws.Row(1).Height = 30;

        var headerRow = 3;
        string[] headers =
        {
            LocalizationService.Instance.Traduci("ColEcu"),
            LocalizationService.Instance.Traduci("AuditRequisito"),
            LocalizationService.Instance.Traduci("AuditXlsTesto"),
            LocalizationService.Instance.Traduci("AuditXlsTestCoinvolti"),
            LocalizationService.Instance.Traduci("AuditXlsPunteggiDiscordanti"),
            LocalizationService.Instance.Traduci("SottoveicoloLabel"),
            LocalizationService.Instance.Traduci("AuditXlsDettaglioConflitto"),
        };
        for (var i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
        ws.Range(headerRow, 1, headerRow, headers.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);

        var r = headerRow + 1;
        foreach (var cella in conflitti.OrderBy(c => c.Veicolo, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(c => c.Componente, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.RequisitoId, StringComparer.OrdinalIgnoreCase).ConAnnullamento(ct))
        {
            ws.Cell(r, 1).Value = cella.Componente;
            ws.Cell(r, 2).Value = cella.RequisitoId;
            ws.Cell(r, 3).Value = testoDiRequisito.GetValueOrDefault(cella.RequisitoId, "");
            ws.Cell(r, 4).Value = string.Join(", ", cella.Candidati.Select(c => c.TestCaseId));
            ws.Cell(r, 5).Value = string.Join(", ", cella.Candidati.Select(c => $"{c.TestCaseId}: {(c.Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : (int)c.Punteggio)}"));
            ws.Cell(r, 6).Value = cella.Veicolo;
            ws.Cell(r, 7).Value = cella.DettaglioConflitto ?? "";
            r++;
        }
        if (conflitti.Count == 0) ws.Cell(headerRow + 1, 1).Value = "Nessun conflitto al momento dell'esportazione.";

        ws.SheetView.FreezeRows(headerRow);
        ws.Columns().AdjustToContents();
    }
}

/// <summary>Quali fogli dell'export Audit includere (nessuna scelta = tutti).</summary>
public sealed record AuditExportOpzioni(bool Legenda = true, bool Matrice = true, bool Conflitti = true, bool DettaglioDtc = true, ExportConfigurazione? Pivot = null);

/// <summary>Identificativi stabili delle sezioni dell'export Audit.</summary>
public static class AuditSezioniExport
{
    public const string Legenda = "legenda", Matrice = "matrice", Conflitti = "conflitti", DettaglioDtc = "dettaglioDtc";
}
