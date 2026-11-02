using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Il Report Excel calcola copertura e audit con indici costruiti una volta per foglio, invece di una scansione completa
/// per ogni requisito. Qui le celle scritte si confrontano con il calcolo originale, su dati con casi di bordo: maiuscole
/// diverse negli ID requisito, collegamenti solo per UID e un ID Test duplicato.</summary>
public class ReportExcelIndiciTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportIndici_" + Guid.NewGuid().ToString("N"));
    private readonly AppState _state;
    private readonly ReportFilter _filtro = new();

    public ReportExcelIndiciTests()
    {
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        _state.InizializzaCartella(_dir);

        var tests = _state.TestCases.GetAll();
        for (var i = 0; i < tests.Count; i++)
        {
            if (i % 3 == 0 && tests[i].RequisitoId.Count > 0) tests[i].RequisitoId[0] = tests[i].RequisitoId[0].ToLowerInvariant();
            if (i == 7 && tests.Count > 9) tests[i].Id = tests[9].Id;
        }
        var nc = _state.NonConformita.GetAll();
        for (var i = 0; i < nc.Count; i++)
            if (i % 4 == 0 && nc[i].TestUid.HasValue) nc[i].TestCaseId = "";
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void Copertura_TestENonConformitaCollegati_UgualiAlCalcoloCompleto()
    {
        var bytes = ReportService.GeneraExcel(_filtro, _state, "Tester");
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var righe = workbook.Worksheet("Copertura Requisiti").RowsUsed().Where(r => r.RowNumber() > 10).ToDictionary(r => r.Cell(1).GetString());
        var testCases = _state.TestCases.GetAll().Where(_filtro.Corrisponde).ToList();
        var nc = _state.NonConformita.GetAll();

        foreach (var req in _state.Master.Requisiti.Load().Where(rq => !rq.Archiviata).OrderBy(rq => rq.Id))
        {
            // Calcolo completo: tutti i Test che citano il requisito, poi tutte le non conformita contro quei Test.
            var testDelReq = testCases.Where(tc => tc.RequisitoId.Any(id => string.Equals(id, req.Id, StringComparison.OrdinalIgnoreCase))).ToList();
            var ncAperte = nc.Where(n => testDelReq.Any(tc => AnomaliaWorkflowService.AppartieneAlTest(n, tc.Id, tc)) && n.Stato == StatoNonConformita.Aperta)
                .Select(n => n.Id);

            var riga = righe[req.Id];
            Assert.Equal(string.Join(", ", testDelReq.Select(tc => tc.Id)), riga.Cell(5).GetString());
            Assert.Equal(string.Join(", ", ncAperte), riga.Cell(7).GetString());
        }
    }

    [Fact]
    public void Audit_PunteggioEStoricoDelRequisito_UgualiAlCalcoloCompleto()
    {
        var bytes = ReportService.GeneraExcel(_filtro, _state, "Tester");
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var righe = workbook.Worksheet("Audit Conformità").RowsUsed().Where(r => r.RowNumber() > 3).ToDictionary(r => r.Cell(1).GetString());
        var testCases = _state.TestCases.GetAll().Where(_filtro.Corrisponde).ToList();

        foreach (var req in _state.Master.Requisiti.Load().Where(rq => !rq.Archiviata).OrderBy(rq => rq.Id))
        {
            var storico = testCases
                .Where(tc => tc.RequisitoId.Any(id => string.Equals(id, req.Id, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(tc => tc.Esecuzioni.Select(e => (Test: tc, Esecuzione: e)))
                .Where(x => x.Esecuzione.PunteggiRequisiti.Any(p => string.Equals(p.RequisitoId, req.Id, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Esecuzione.Data)
                .Select(x =>
                {
                    var p = x.Esecuzione.PunteggiRequisiti.First(pp => string.Equals(pp.RequisitoId, req.Id, StringComparison.OrdinalIgnoreCase));
                    var voce = $"{x.Test.Id}#{x.Esecuzione.Iterazione} ({x.Esecuzione.Data:dd/MM/yyyy}): {p.Valore.Label()}";
                    return p.Sovrascritto ? $"{voce} [corretto: {p.MotivazioneOverride}]" : voce;
                });
            var punteggio = CoverageService.PunteggioCorrentePerRequisito(req.Id, testCases)?.Label() ?? "Non valutato";

            var riga = righe[req.Id];
            Assert.Equal(punteggio, riga.Cell(3).GetString());
            Assert.Equal(string.Join(" | ", storico), riga.Cell(4).GetString());
        }
    }
}
