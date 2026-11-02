using ClosedXML.Excel;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Export Excel dei requisiti nel formato di interscambio letto dall'import posizionale di RequisitiViewModel
/// (riga 1 intestazioni, dati da riga 2): A Id, B Testo, C Banco, D Veicolo (invariate) e E "Veicolo-componente Extra" con le coppie
/// <see cref="Requisito.VeicoliEcuD4D"/> ("Veicolo|componente; Veicolo|componente"), anche per requisiti senza guasto. La colonna E e'
/// facoltativa in lettura: un file prodotto prima della sua introduzione si importa senza toccare le coppie esistenti.</summary>
public static class RequisitiExcelExportService
{
    public const string NomeFoglio = "Requisiti";
    public const string IntestazioneCoppieD4D = "Veicolo-componente Extra";

    public static void Esporta(IEnumerable<Requisito> requisiti, string percorso)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(NomeFoglio);
        ws.Cell(1, 1).Value = "Id";
        ws.Cell(1, 2).Value = "Testo";
        ws.Cell(1, 3).Value = "Banco";
        ws.Cell(1, 4).Value = "Veicolo";
        ws.Cell(1, 5).Value = IntestazioneCoppieD4D;
        var r = 2;
        foreach (var q in requisiti.Where(x => !x.Archiviata))
        {
            ws.Cell(r, 1).SetValue(q.Id);
            ws.Cell(r, 2).SetValue(q.Testo);
            ws.Cell(r, 3).SetValue(q.ApplicabileBanco ? "si" : "no");
            ws.Cell(r, 4).SetValue(q.ApplicabileVeicolo ? "si" : "no");
            ws.Cell(r, 5).SetValue(q.VeicoliEcuD4DTesto);
            r++;
        }
        wb.SaveAs(percorso);
    }
}
