using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace TestLoom.App.Services;

public enum TipoGraficoNativo { Torta, Colonne, ColonneImpilate, Linee }

/// <summary>Un grafico Excel nativo collegato a un intervallo di celle di un foglio. Numeri di riga e colonna 1-based; la riga di
/// intestazione porta i nomi delle serie. Le colonne delle serie non devono essere contigue.</summary>
public sealed record GraficoNativoSpec(string Foglio, string Titolo, TipoGraficoNativo Tipo, int RigaIntestazione,
    int PrimaRiga, int UltimaRiga, int ColonnaCategorie, IReadOnlyList<int> ColonneSerie, int AncoraColonna, int AncoraRiga,
    int Colonne = 9, int Righe = 19);

/// <summary>
/// Aggiunge grafici Excel nativi (modificabili e collegati ai dati del foglio) a un file .xlsx gia' scritto. ClosedXML non crea grafici:
/// si usa l'OpenXml SDK, gia' dipendenza di ClosedXML, sul pacchetto salvato. Un foglio che ha gia' un disegno (per esempio le immagini
/// dei grafici) lo mantiene: i nuovi grafici si aggiungono alle sue ancore.
/// </summary>
public static class ExcelChartInjector
{
    public static byte[] Inietta(byte[] xlsx, IReadOnlyList<GraficoNativoSpec> grafici)
    {
        if (grafici.Count == 0) return xlsx;
        using var ms = new MemoryStream();
        ms.Write(xlsx, 0, xlsx.Length);
        ms.Position = 0;
        using (var doc = SpreadsheetDocument.Open(ms, true))
        {
            var wbPart = doc.WorkbookPart ?? throw new InvalidOperationException("Cartella di lavoro senza contenuto.");
            foreach (var gruppo in grafici.GroupBy(g => g.Foglio, StringComparer.Ordinal))
            {
                var sheet = wbPart.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
                    .FirstOrDefault(s => string.Equals(s.Name?.Value, gruppo.Key, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"Foglio '{gruppo.Key}' non trovato.");
                var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
                var dp = wsPart.DrawingsPart;
                var nuovoDisegno = dp == null;
                dp ??= wsPart.AddNewPart<DrawingsPart>();
                dp.WorksheetDrawing ??= new Xdr.WorksheetDrawing();
                var disegno = dp.WorksheetDrawing;
                var prossimoId = Math.Max(1u, disegno.Descendants<Xdr.NonVisualDrawingProperties>().Select(p => p.Id?.Value ?? 0u).DefaultIfEmpty(0u).Max() + 1);
                foreach (var spec in gruppo)
                {
                    var cp = dp.AddNewPart<ChartPart>();
                    cp.ChartSpace = CostruisciChart(spec);
                    cp.ChartSpace.Save();
                    disegno.Append(Ancora(spec, dp.GetIdOfPart(cp), prossimoId++));
                }
                disegno.Save();
                if (nuovoDisegno)
                {
                    var riferimento = new DocumentFormat.OpenXml.Spreadsheet.Drawing { Id = wsPart.GetIdOfPart(dp) };
                    // L'elemento drawing segue margini, impostazione pagina e intestazione/pie' di pagina.
                    OpenXmlElement? precedente = wsPart.Worksheet.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.HeaderFooter>()
                        ?? wsPart.Worksheet.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.PageSetup>()
                        ?? (OpenXmlElement?)wsPart.Worksheet.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.PageMargins>();
                    if (precedente != null) precedente.InsertAfterSelf(riferimento); else wsPart.Worksheet.Append(riferimento);
                    wsPart.Worksheet.Save();
                }
            }
        }
        return ms.ToArray();
    }

    private static string Rif(string foglio) => "'" + foglio.Replace("'", "''") + "'!";

    private static string Cella(int colonna, int riga) => $"${ClosedXML.Excel.XLHelper.GetColumnLetterFromNumber(colonna)}${riga}";

    private static string Intervallo(GraficoNativoSpec s, int colonna) =>
        $"{Rif(s.Foglio)}{Cella(colonna, s.PrimaRiga)}:{Cella(colonna, s.UltimaRiga)}";

    private static Xdr.TwoCellAnchor Ancora(GraficoNativoSpec s, string idRelazione, uint id) => new(
        new Xdr.FromMarker(new Xdr.ColumnId((s.AncoraColonna - 1).ToString()), new Xdr.ColumnOffset("0"),
            new Xdr.RowId((s.AncoraRiga - 1).ToString()), new Xdr.RowOffset("0")),
        new Xdr.ToMarker(new Xdr.ColumnId((s.AncoraColonna - 1 + s.Colonne).ToString()), new Xdr.ColumnOffset("0"),
            new Xdr.RowId((s.AncoraRiga - 1 + s.Righe).ToString()), new Xdr.RowOffset("0")),
        new Xdr.GraphicFrame(
            new Xdr.NonVisualGraphicFrameProperties(
                new Xdr.NonVisualDrawingProperties { Id = id, Name = "Grafico " + id },
                new Xdr.NonVisualGraphicFrameDrawingProperties()),
            new Xdr.Transform(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 }),
            new A.Graphic(new A.GraphicData(new C.ChartReference { Id = idRelazione })
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" })),
        new Xdr.ClientData());

    private static C.ChartSpace CostruisciChart(GraficoNativoSpec s)
    {
        var plotArea = new C.PlotArea(new C.Layout());
        switch (s.Tipo)
        {
            case TipoGraficoNativo.Torta:
            {
                var pie = new C.PieChart(new C.VaryColors { Val = true });
                pie.Append(Serie<C.PieChartSeries>(s, 0, s.ColonneSerie[0]));
                pie.Append(new C.FirstSliceAngle { Val = 0 });
                plotArea.Append(pie);
                break;
            }
            case TipoGraficoNativo.Linee:
            {
                var line = new C.LineChart(new C.Grouping { Val = C.GroupingValues.Standard }, new C.VaryColors { Val = false });
                for (var i = 0; i < s.ColonneSerie.Count; i++) line.Append(Serie<C.LineChartSeries>(s, i, s.ColonneSerie[i]));
                line.Append(new C.AxisId { Val = 1u }, new C.AxisId { Val = 2u });
                plotArea.Append(line);
                plotArea.Append(Assi());
                break;
            }
            default:
            {
                var impilato = s.Tipo == TipoGraficoNativo.ColonneImpilate;
                var bar = new C.BarChart(new C.BarDirection { Val = C.BarDirectionValues.Column },
                    new C.BarGrouping { Val = impilato ? C.BarGroupingValues.Stacked : C.BarGroupingValues.Clustered },
                    new C.VaryColors { Val = false });
                for (var i = 0; i < s.ColonneSerie.Count; i++) bar.Append(Serie<C.BarChartSeries>(s, i, s.ColonneSerie[i]));
                bar.Append(new C.GapWidth { Val = 80 });
                if (impilato) bar.Append(new C.Overlap { Val = 100 });
                bar.Append(new C.AxisId { Val = 1u }, new C.AxisId { Val = 2u });
                plotArea.Append(bar);
                plotArea.Append(Assi());
                break;
            }
        }

        var chart = new C.Chart(
            new C.Title(new C.ChartText(new C.RichText(new A.BodyProperties(), new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.Text(s.Titolo))))), new C.Overlay { Val = false }),
            new C.AutoTitleDeleted { Val = false },
            plotArea,
            new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Right }, new C.Overlay { Val = false }),
            new C.PlotVisibleOnly { Val = true });
        return new C.ChartSpace(chart);
    }

    private static T Serie<T>(GraficoNativoSpec s, int indice, int colonna) where T : OpenXmlCompositeElement, new()
    {
        var serie = new T();
        serie.Append(new C.Index { Val = (uint)indice }, new C.Order { Val = (uint)indice },
            new C.SeriesText(new C.StringReference(new C.Formula($"{Rif(s.Foglio)}{Cella(colonna, s.RigaIntestazione)}"))),
            new C.CategoryAxisData(new C.StringReference(new C.Formula(Intervallo(s, s.ColonnaCategorie)))),
            new C.Values(new C.NumberReference(new C.Formula(Intervallo(s, colonna)))));
        return serie;
    }

    private static IEnumerable<OpenXmlElement> Assi()
    {
        yield return new C.CategoryAxis(new C.AxisId { Val = 1u }, new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false }, new C.AxisPosition { Val = C.AxisPositionValues.Bottom }, new C.CrossingAxis { Val = 2u });
        yield return new C.ValueAxis(new C.AxisId { Val = 2u }, new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false }, new C.AxisPosition { Val = C.AxisPositionValues.Left }, new C.MajorGridlines(),
            new C.CrossingAxis { Val = 1u });
    }
}
