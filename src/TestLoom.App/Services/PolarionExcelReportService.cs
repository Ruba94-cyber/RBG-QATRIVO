using ClosedXML.Excel;

namespace TestLoom.App.Services;

/// <summary>Standalone, filter-matched TICKET workbook. No changes to the established NC workbook.</summary>
public static class PolarionExcelReportService
{
    public static byte[] Generate(IReadOnlyList<PolarionReportService.Row> rows,
        DateTimeOffset generatedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var workbook = new XLWorkbook();
        var overview = workbook.Worksheets.Add("Riepilogo TICKET");
        var details = workbook.Worksheets.Add("Ticket TICKET");
        var timeline = workbook.Worksheets.Add("Timeline TICKET");
        overview.Cell("A1").Value = "TestLoom · Ticketing TICKET lifecycle";
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
        for (var i = 0; i < columns.Length; i++) details.Cell(1, i + 1).Value = columns[i];
        Header(details.Range(1, 1, 1, columns.Length));
        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            var n = i + 2;
            var strings = new[] { row.TicketId, row.Title, row.Type, row.Status,
                row.Resolution, row.Priority, row.Severity, row.Records,
                row.Vehicles, row.Ranges, row.ArchiveText };
            for (var j = 0; j < strings.Length; j++)
                details.Cell(n, j + 1).Value = Sanitize(strings[j]);
            Date(details.Cell(n, 12), row.CreatedAt);
            Date(details.Cell(n, 13), row.FirstTestingObservedAt);
            Date(details.Cell(n, 14), row.FirstClosedObservedAt);
            Date(details.Cell(n, 15), row.LastReopenObservedAt);
            if (!row.HistoryUnavailable)
            {
                details.Cell(n, 16).Value = row.AlreadyTestingAtBaseline ? "Sì" : "No";
                details.Cell(n, 17).Value = row.AlreadyClosedAtBaseline ? "Sì" : "No";
            }
            if (row.LastFetchedAt != default) Date(details.Cell(n, 18), row.LastFetchedAt);
            details.Cell(n, 19).Value = row.CommentsCount;
            details.Cell(n, 20).Value = row.DataQuality;
            details.Cell(n, 21).Value = row.TestingUpperBoundText;
            details.Cell(n, 22).Value = row.ClosureUpperBoundText;
            if (!row.HistoryUnavailable)
            {
                details.Cell(n, 23).Value = row.TestingTransitionsObserved;
                details.Cell(n, 24).Value = row.ClosureTransitionsObserved;
                details.Cell(n, 25).Value = row.ReopenTransitionsObserved;
            }
        }
        details.SheetView.FreezeRows(1);
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
        if (rows.Count > 0) details.Range(1, 1, rows.Count + 1, columns.Length).SetAutoFilter();

        // Full observed state history for every filtered TICKET, including the initial
        // baseline and subsequent reopens/reclosures; never label observed dates exact.
        var timelineHeaders = new[] { "TICKET", "NC / CR", "Data osservazione TestLoom",
            "Stato", "Risoluzione", "Evento", "Provenienza data" };
        for (var i = 0; i < timelineHeaders.Length; i++)
            timeline.Cell(1, i + 1).Value = timelineHeaders[i];
        Header(timeline.Range(1, 1, 1, timelineHeaders.Length));
        var timelineRow = 2;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var observation in row.Timeline)
            {
                timeline.Cell(timelineRow, 1).Value = Sanitize(row.TicketId);
                timeline.Cell(timelineRow, 2).Value = Sanitize(row.Records);
                Date(timeline.Cell(timelineRow, 3), observation.AtUtc);
                timeline.Cell(timelineRow, 4).Value = Sanitize(observation.Status);
                timeline.Cell(timelineRow, 5).Value = Sanitize(observation.Resolution);
                timeline.Cell(timelineRow, 6).Value = observation.IsBaseline
                    ? "Baseline TestLoom" : "Cambio stato osservato";
                timeline.Cell(timelineRow, 7).Value = observation.IsBaseline
                    ? "Prima osservazione, NON data di ingresso nello stato"
                    : "Prima rilevazione TestLoom, NON data storica esatta Ticketing";
                timelineRow++;
            }
        }
        timeline.SheetView.FreezeRows(1);
        timeline.Columns("A:B").Width = 23;
        timeline.Column("C").Width = 25;
        timeline.Columns("D:F").Width = 24;
        timeline.Column("G").Width = 66;
        if (timelineRow > 2)
            timeline.Range(1, 1, timelineRow - 1, timelineHeaders.Length).SetAutoFilter();

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    private static void Date(IXLCell cell, DateTimeOffset? value)
    {
        if (!value.HasValue) return;
        cell.Value = value.Value.LocalDateTime;
        cell.Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
    }

    private static void Header(IXLRange range)
    {
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#183153");
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
    }

    private static string Sanitize(string? text)
    {
        var value = text ?? "";
        return value.TrimStart().StartsWith("=", StringComparison.Ordinal) ||
            value.TrimStart().StartsWith("+", StringComparison.Ordinal) ||
            value.TrimStart().StartsWith("-", StringComparison.Ordinal) ||
            value.TrimStart().StartsWith("@", StringComparison.Ordinal) ? "'" + value : value;
    }
}
