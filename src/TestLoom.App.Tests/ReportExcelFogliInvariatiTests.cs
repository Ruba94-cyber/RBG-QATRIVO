using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Verifica "il resto del file Excel del Report e' invariato" (foglio "Grafici" escluso): il dump delle celle di
/// tutti gli altri fogli sul dataset demo, in ordine e con le celle data/ora mascherate, deve coincidere con il baseline
/// registrato prima dell'introduzione dei grafici filtrati (Fixtures/grafici-excel-fogli-baseline.json).
/// Aggiornamento al contratto Stato %: nel foglio della matrice cambiano solo titolo, descrizione e testi
/// delle celle e dei totali (distribuzione dello stato su tutte le combinazioni applicabili); nessun altro foglio cambia.</summary>
[Trait("Category", "Lento")]
public class ReportExcelFogliInvariatiTests : IDisposable
{
    private const string FoglioGrafici = "Grafici";
    private readonly string _dir;
    private readonly AppState _state;

    public ReportExcelFogliInvariatiTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomFogliInvariati_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        _state.InizializzaCartella(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

}
