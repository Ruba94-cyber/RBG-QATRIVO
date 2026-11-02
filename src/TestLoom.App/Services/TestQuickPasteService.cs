using System.Text;

namespace TestLoom.App.Services;

/// <summary>
/// Atomic text/Excel TSV import of independent, unexecuted Test operations.
/// Supports quoted Excel cells (including embedded tabs, quotes and newlines).
/// Never writes outcomes, evidence or anomaly fields to a Test.
/// </summary>
public static class TestQuickPasteService
{
    public enum ParseError { None, TooManyRows, RowTooLong, InvalidColumns, UnknownFunction }
    public sealed record Operation(string Function, string Note);
    public sealed record Result(IReadOnlyList<Operation> Operations, ParseError Error, string Detail = "", int RowNumber = 0)
    {
        public bool Success => Error == ParseError.None;
    }

    private const int MaxRows = 50;
    private const int MaxRowChars = 2048;
    // Bounded input protects UI responsiveness even for an accidentally pasted large worksheet.
    private const int MaxInputChars = (MaxRows + 2) * (MaxRowChars + 8);

    private static Result Failed(ParseError error, string detail = "", int rowNumber = 0) =>
        new(Array.Empty<Operation>(), error, detail, rowNumber);

    private sealed record ParsedRow(int Number, string[] Cells);

    private static bool IsFunctionHeader(string value) =>
        value.Trim().Equals("Funzionalità", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("Funzionalita", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("Function", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("Fonction", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoteHeader(string value) =>
        value.Trim().Equals("Nota", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("Note", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("Notes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns exact logical Excel rows: CRLF outside quotes separates rows;
    /// CRLF inside quotes remains inside the same cell. Parse errors are atomic.
    /// </summary>
    private static Result? ReadRows(string text, out List<ParsedRow> rowsOut)
    {
        var rows = new List<ParsedRow>();
        rowsOut = rows;
        var logicalRowNumber = 1;
        var cells = new List<string>(2);
        var field = new StringBuilder();
        var inQuotes = false;
        var afterClosingQuote = false;
        var rowChars = 0;

        bool AddCell()
        {
            cells.Add(field.ToString().Trim());
            field.Clear();
            afterClosingQuote = false;
            return cells.Count <= 2;
        }
        bool AddRow()
        {
            if (cells.Count > 0 && cells.Any(c => c.Length != 0))
                rows.Add(new ParsedRow(logicalRowNumber, cells.ToArray()));
            cells.Clear();
            rowChars = 0;
            logicalRowNumber++;
            return rows.Count <= MaxRows + 1;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        rowChars++;
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                        afterClosingQuote = true;
                    }
                }
                else
                {
                    field.Append(ch);
                    rowChars++;
                }
            }
            else if (ch == '\t' || ch is '\r' or '\n')
            {
                if (!AddCell()) return Failed(ParseError.InvalidColumns, rowNumber: logicalRowNumber);
                if (ch != '\t')
                {
                    if (!AddRow()) return Failed(ParseError.TooManyRows, rowNumber: logicalRowNumber - 1);
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
                else rowChars++;
            }
            else if (ch == '"' && field.Length == 0 && !afterClosingQuote)
            {
                inQuotes = true;
            }
            else if (afterClosingQuote)
            {
                // Reject extra unquoted content after a quoted Excel cell.
                return Failed(ParseError.InvalidColumns, rowNumber: logicalRowNumber);
            }
            else
            {
                field.Append(ch);
                rowChars++;
            }

            if (rowChars > MaxRowChars) return Failed(ParseError.RowTooLong, rowNumber: logicalRowNumber);
        }
        if (inQuotes) return Failed(ParseError.InvalidColumns, rowNumber: logicalRowNumber);
        if (!AddCell()) return Failed(ParseError.InvalidColumns, rowNumber: logicalRowNumber);
        if (!AddRow()) return Failed(ParseError.TooManyRows, rowNumber: logicalRowNumber - 1);
        return null;
    }

    public static Result Parse(string? text, IEnumerable<string> availableFunctions, string? defaultFunction)
    {
        ArgumentNullException.ThrowIfNull(availableFunctions);
        if (string.IsNullOrWhiteSpace(text)) return new Result(Array.Empty<Operation>(), ParseError.None);
        if (text.Length > MaxInputChars) return Failed(ParseError.TooManyRows);

        var error = ReadRows(text, out var rows);
        if (error is not null) return error;

        var functions = availableFunctions
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(f => f, f => f, StringComparer.OrdinalIgnoreCase);

        var start = 0;
        var reversed = false;
        if (rows.Count > 0 && rows[0].Cells.Length > 1)
        {
            var header = rows[0].Cells;
            if ((IsFunctionHeader(header[0]) && IsNoteHeader(header[1])) ||
                (IsNoteHeader(header[0]) && IsFunctionHeader(header[1])))
            {
                start = 1;
                reversed = IsNoteHeader(header[0]);
            }
            else if (IsFunctionHeader(header[0]) || IsNoteHeader(header[0]))
                return Failed(ParseError.InvalidColumns, rowNumber: rows[0].Number);
        }
        if (rows.Count - start > MaxRows) return Failed(ParseError.TooManyRows, rowNumber: rows[start + MaxRows].Number);

        var result = new List<Operation>(rows.Count - start);
        for (var i = start; i < rows.Count; i++)
        {
            var columns = rows[i].Cells;
            var rowNumber = rows[i].Number;
            if (columns.Sum(c => c.Length) > MaxRowChars)
                return Failed(ParseError.RowTooLong, rowNumber: rowNumber);
            if (columns.Length is < 1 or > 2 ||
                (start == 1 && columns.Length != 2))
                return Failed(ParseError.InvalidColumns, rowNumber: rowNumber);

            var function = defaultFunction ?? "";
            var note = columns[0];
            if (columns.Length == 2)
            {
                var requested = columns[reversed ? 1 : 0].Trim();
                note = columns[reversed ? 0 : 1];
                if (!functions.TryGetValue(requested, out function))
                    return Failed(ParseError.UnknownFunction, requested, rowNumber);
            }
            result.Add(new Operation(function, note));
        }
        return new Result(result, ParseError.None);
    }
}
