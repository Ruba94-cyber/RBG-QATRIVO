using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace TestLoom.App.Tests;

/// <summary>Dump deterministico delle celle di un workbook (nome foglio, ordine, valori, numero immagini). Le celle
/// data/ora (dipendenti da DateTime.Now del dataset demo o dall'istante di export) sono mascherate: il confronto
/// "fogli esistenti invariati" resta stabile tra giorni e tra esecuzioni.</summary>
internal static class FogliExcelDumpHelper
{
    private static readonly Regex DataOra = new(@"\d{2}/\d{2}/\d{4}(?: \d{2}:\d{2}(?::\d{2})?)?|\d{8}_\d{6}", RegexOptions.Compiled);

    /// <summary><paramref name="foglioEscluso"/> resta nell'elenco dei fogli (per ordine e nome) ma non ha celle nel dump.</summary>
    internal static string Da(byte[] xlsx, string? foglioEscluso = null)
    {
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        var fogli = new List<object>();
        foreach (var ws in wb.Worksheets)
        {
            if (ws.Name == foglioEscluso)
            {
                fogli.Add(new { nome = ws.Name, escluso = true });
                continue;
            }
            var celle = ws.CellsUsed()
                .OrderBy(c => c.Address.RowNumber).ThenBy(c => c.Address.ColumnNumber)
                .Select(c => new object[] { c.Address.RowNumber, c.Address.ColumnNumber, c.Value.Type.ToString(), Maschera(c) })
                .ToList();
            fogli.Add(new { nome = ws.Name, immagini = ws.Pictures.Count(), celle });
        }
        return JsonSerializer.Serialize(fogli, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Maschera(IXLCell cella) =>
        cella.Value.Type == XLDataType.DateTime ? "<DATA>" : DataOra.Replace(cella.Value.ToString(), "<DATA>");
}
