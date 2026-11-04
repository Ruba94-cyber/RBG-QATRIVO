using ClosedXML.Excel;

namespace TestLoom.App.Services;

/// <summary>Standalone, filter-matched TICKET workbook. No changes to the established NC workbook.</summary>
public static class PolarionExcelReportService
{
    /// <summary>API in-memory usata dai test e dalle integrazioni esistenti.</summary>
    public static byte[] Generate(IReadOnlyList<PolarionReportService.Row> rows,
        DateTimeOffset generatedAt, CancellationToken ct = default)
    {
        using var workbook = BuildWorkbook(rows, generatedAt, ct);
        ct.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    /// <summary>Export desktop a basso picco memoria: ClosedXML salva direttamente
    /// su un file temporaneo nella stessa directory di destinazione. Il file
    /// preesistente non viene toccato in caso di eccezione o annullamento.</summary>
    public static void SaveToFile(IReadOnlyList<PolarionReportService.Row> rows,
        DateTimeOffset generatedAt, string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("Excel output directory not available.", nameof(path));
        var temp = Path.Combine(dir, "." + Path.GetFileName(fullPath) +
            "." + Guid.NewGuid().ToString("N") + ".tmp.xlsx");
        try
        {
            using (var workbook = BuildWorkbook(rows, generatedAt, ct))
            {
                ct.ThrowIfCancellationRequested();
                workbook.SaveAs(temp);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static XLWorkbook BuildWorkbook(IReadOnlyList<PolarionReportService.Row> rows,
        DateTimeOffset generatedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ct.ThrowIfCancellationRequested();
        var workbook = new XLWorkbook();
        try
        {
        var overview = workbook.Worksheets.Add("Riepilogo TICKET");
        overview.Cell("A1").Value = "RBG - QATRIVO · Ticketing TICKET lifecycle";
        overview.Cell("A1").Style.Font.Bold = true;
        overview.Cell("A1").Style.Font.FontSize = 17;
        overview.Cell("A2").Value = "Generato (ora locale)";
        overview.Cell("B2").Value = generatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        overview.Cell("A3").Value = "Apertura";
        overview.Cell("B3").Value = "Data created originale Ticketing";
        overview.Cell("A4").Value = "Testing / Closed / Reopen";
        overview.Cell("B4").Value = "Prima osservazione TestLoom, non data storica esatta";
        overview.Cell("A5").Value = "Baseline";
        overview.Cell("B5").Value = "Lo stato già presente alla prima osservazione non è una transizione";
        var summary = PolarionReportService.Summarize(rows);
        var metrics = new (string Label, int Count)[]
        {
            ("Ticket", summary.Total), ("Non sincronizzati", summary.NotSynced),
            ("Aperti", summary.Open), ("Closed", summary.Closed),
            ("Testing", summary.Testing), ("Già Closed al baseline", summary.AlreadyClosedAtBaseline),
            ("Closed rilevati", summary.ObservedClosures),
            ("Riaperture rilevate", summary.ObservedReopenings),
            ("Data apertura sconosciuta", summary.WithUnknownCreation),
            ("Cronologie non disponibili", summary.HistoriesUnavailable)
        };
        overview.Cell("A7").Value = "Indicatore";
        overview.Cell("B7").Value = "Totale";
        for (var i = 0; i < metrics.Length; i++)
        {
            overview.Cell(i + 8, 1).Value = metrics[i].Label;
            overview.Cell(i + 8, 2).Value = metrics[i].Count;
        }
        overview.Cell("A19").Value = "Stato";
        overview.Cell("B19").Value = "Ticket";
        for (var i = 0; i < summary.StatusDistribution.Count; i++)
        {
            overview.Cell(20 + i, 1).Value = summary.StatusDistribution[i].Status;
            overview.Cell(20 + i, 2).Value = summary.StatusDistribution[i].Count;
        }
        Header(overview.Range("A7:B7"));
        Header(overview.Range("A19:B19"));
        overview.Column("A").Width = 35;
        overview.Column("B").Width = 71;

        var columns = new[] { "TICKET", "Titolo", "Tipo", "Stato", "Risoluzione", "Priorità",
            "Severità", "NC / CR collegate", "Veicoli", "Gamme", "Archiviato",
            "Creato in Ticketing (esatto)", "Testing (rilevato TestLoom)",
            "Closed (rilevato TestLoom)", "Reopen (rilevato TestLoom)",
            "Già Testing al baseline", "Già Closed al baseline",
            "Ultima cache", "Commenti", "Qualità dati",
            "Apertura → Testing (limite superiore)", "Apertura → Closed (limite superiore)",
            "Testing osservati", "Closed osservati", "Reopen osservati" };
        // Usa il writer condiviso anche per TICKET: gestisce i limiti di Excel
        // (1.048.576 righe, 32.767 caratteri/cella), le formule da testo utente
        // e la continuazione su altri fogli senza perdita silenziosa.
        var detailWriter = new ExcelExportTableWriter(workbook, "Ticket TICKET", columns);
        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            object?[] values =
            [
                row.TicketId, row.Title, row.Type, row.Status, row.Resolution,
                row.Priority, row.Severity, row.Records, row.Vehicles, row.Ranges,
                row.ArchiveText, LocalDate(row.CreatedAt), LocalDate(row.FirstTestingObservedAt),
                LocalDate(row.FirstClosedObservedAt), LocalDate(row.LastReopenObservedAt),
                row.HistoryUnavailable ? null : row.AlreadyTestingAtBaseline ? "Sì" : "No",
                row.HistoryUnavailable ? null : row.AlreadyClosedAtBaseline ? "Sì" : "No",
                row.LastFetchedAt == default ? null : row.LastFetchedAt.LocalDateTime,
                row.CommentsCount, row.DataQuality,
                row.TestingUpperBoundText, row.ClosureUpperBoundText,
                row.HistoryUnavailable ? null : row.TestingTransitionsObserved,
                row.HistoryUnavailable ? null : row.ClosureTransitionsObserved,
                row.HistoryUnavailable ? null : row.ReopenTransitionsObserved
            ];
            detailWriter.AggiungiRiga(row.TicketId, values);
        }
        detailWriter.Completa();
        foreach (var details in workbook.Worksheets.Where(w =>
            w.Name == "Ticket TICKET" || w.Name.StartsWith("Ticket TICKET (", StringComparison.Ordinal)))
        {
            Header(details.Range(1, 1, 1, columns.Length));
            details.Column("A").Width = 15;
            details.Column("B").Width = 48;
            details.Columns("C:G").Width = 15;
            details.Column("H").Width = 30;
            details.Columns("I:J").Width = 24;
            details.Column("K").Width = 12;
            details.Columns("L:R").Width = 27;
            details.Column("S").Width = 14;
            details.Column("T").Width = 32;
            details.Columns("U:V").Width = 30;
            details.Columns("W:Y").Width = 18;
        }

        // Full observed state history for every filtered TICKET, including the initial
        // baseline and subsequent reopens/reclosures; never label observed dates exact.
        var timelineHeaders = new[] { "TICKET", "NC / CR", "Data osservazione TestLoom",
            "Stato", "Risoluzione", "Evento", "Provenienza data" };
        var timelineWriter = new ExcelExportTableWriter(workbook, "Timeline TICKET", timelineHeaders);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var observation in row.Timeline)
            {
                ct.ThrowIfCancellationRequested();
                timelineWriter.AggiungiRiga(row.TicketId,
                [
                    row.TicketId, row.Records, observation.AtUtc.LocalDateTime,
                    observation.Status, observation.Resolution,
                    observation.IsBaseline ? "Baseline TestLoom" : "Cambio stato osservato",
                    observation.IsBaseline
                        ? "Prima osservazione, NON data di ingresso nello stato"
                        : "Prima rilevazione TestLoom, NON data storica esatta Ticketing"
                ]);
            }
        }
        timelineWriter.Completa();
        foreach (var timeline in workbook.Worksheets.Where(w =>
            w.Name == "Timeline TICKET" || w.Name.StartsWith("Timeline TICKET (", StringComparison.Ordinal)))
        {
            Header(timeline.Range(1, 1, 1, timelineHeaders.Length));
            timeline.Columns("A:B").Width = 23;
            timeline.Column("C").Width = 25;
            timeline.Columns("D:F").Width = 24;
            timeline.Column("G").Width = 66;
        }

            ct.ThrowIfCancellationRequested();
            return workbook;
        }
        catch
        {
            workbook.Dispose();
            throw;
        }
    }

    private static DateTime? LocalDate(DateTimeOffset? value) => value?.LocalDateTime;

    private static void Header(IXLRange range)
    {
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#183153");
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
    }

}
