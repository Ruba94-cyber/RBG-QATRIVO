using System.Text;

namespace TestLoom.Core;

/// <summary>
/// Atomic import of test steps pasted from a spreadsheet (tab-separated, Excel quoting rules).
/// Two columns are read as action and expected result; an optional header row is recognised.
/// Any error rejects the whole paste, so a test never receives half of a pasted table.
/// </summary>
public static class StepPasteParser
{
    public enum PasteError { None, TooManyRows, RowTooLong, InvalidColumns }

    public sealed record ParsedStep(string Action, string ExpectedResult);

    public sealed record PasteResult(IReadOnlyList<ParsedStep> Steps, PasteError Error, int RowNumber = 0)
    {
        public bool Success => Error == PasteError.None;
    }

    public const int MaxRows = 100;
    public const int MaxRowChars = 4096;
    private const int MaxInputChars = (MaxRows + 2) * (MaxRowChars + 8);

    private static PasteResult Failed(PasteError error, int row = 0) =>
        new(Array.Empty<ParsedStep>(), error, row);

    private static bool IsActionHeader(string v) =>
        v.Trim().ToLowerInvariant() is "step" or "action" or "azione" or "passo" or "passaggio" or "étape" or "etape";

    private static bool IsExpectedHeader(string v) =>
        v.Trim().ToLowerInvariant() is "expected" or "expected result" or "risultato atteso" or "atteso"
            or "résultat attendu" or "resultat attendu" or "attendu";

    public static PasteResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new PasteResult(Array.Empty<ParsedStep>(), PasteError.None);
        if (text.Length > MaxInputChars) return Failed(PasteError.TooManyRows);

        var rows = new List<(int Number, string[] Cells)>();
        var cells = new List<string>(2);
        var field = new StringBuilder();
        var inQuotes = false;
        var afterQuote = false;
        var rowChars = 0;
        var logical = 1;

        void EndCell() { cells.Add(field.ToString().Trim()); field.Clear(); afterQuote = false; }
        void EndRow()
        {
            if (cells.Any(c => c.Length != 0)) rows.Add((logical, cells.ToArray()));
            cells.Clear();
            rowChars = 0;
            logical++;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); rowChars++; i++; }
                    else { inQuotes = false; afterQuote = true; }
                }
                else { field.Append(ch); rowChars++; }
            }
            else if (ch == '\t' || ch is '\r' or '\n')
            {
                EndCell();
                if (cells.Count > 2) return Failed(PasteError.InvalidColumns, logical);
                if (ch != '\t')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    EndRow();
                    if (rows.Count > MaxRows + 1) return Failed(PasteError.TooManyRows, logical - 1);
                }
                else rowChars++;
            }
            else if (ch == '"' && field.Length == 0 && !afterQuote) inQuotes = true;
            else if (afterQuote) return Failed(PasteError.InvalidColumns, logical);
            else { field.Append(ch); rowChars++; }

            if (rowChars > MaxRowChars) return Failed(PasteError.RowTooLong, logical);
        }
        if (inQuotes) return Failed(PasteError.InvalidColumns, logical);
        EndCell();
        if (cells.Count > 2) return Failed(PasteError.InvalidColumns, logical);
        EndRow();

        var start = 0;
        var swapped = false;
        if (rows.Count > 0 && rows[0].Cells.Length == 2)
        {
            var h = rows[0].Cells;
            if (IsActionHeader(h[0]) && IsExpectedHeader(h[1])) start = 1;
            else if (IsExpectedHeader(h[0]) && IsActionHeader(h[1])) { start = 1; swapped = true; }
        }
        if (rows.Count - start > MaxRows) return Failed(PasteError.TooManyRows, rows[start + MaxRows].Number);

        var steps = new List<ParsedStep>(rows.Count - start);
        for (var i = start; i < rows.Count; i++)
        {
            var (number, c) = rows[i];
            if (c.Length != 2 || c[0].Length == 0 || c[1].Length == 0)
                return Failed(PasteError.InvalidColumns, number);
            steps.Add(swapped ? new ParsedStep(c[1], c[0]) : new ParsedStep(c[0], c[1]));
        }
        return new PasteResult(steps, PasteError.None);
    }
}
