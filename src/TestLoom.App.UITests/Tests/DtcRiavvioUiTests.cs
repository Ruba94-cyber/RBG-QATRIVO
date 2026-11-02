using FlaUI.Core.AutomationElements;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.Tests;

/// <summary>Filtri del dettaglio guasto (Audit Extra) mantenuti dopo un riavvio di TestLoom, per PC (config locale), e "Azzera" che
/// svuota anche la persistenza. Classe senza avvio automatico: il riavvio e parte del test.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Persistence")]
public sealed class DtcRiavvioUiTests : IDisposable
{
    private readonly TestWorkspace _ws = DtcUiTests.CreaWorkspace();
    public void Dispose() => _ws.Dispose();

    [Fact]
    public void AuditD4d_FiltriDtc_MantenutiDopoRiavvio_EAzzeraSvuotaLaPersistenza()
    {
        using (var app1 = TestLoomApplication.Launch(_ws))
        {
            DtcUiTests.ApriDettaglioDtcInAudit(app1, out var w1);
            ElementFinders.WaitById(w1, "Audit_Guasto_SoloNonCompleti_Check").AsCheckBox().Toggle();
            Wait.ForCondition(() => !ElementFinders.ExistsById(w1, "Audit_Guasto_Voce_P0001"), "filtro applicato");
            System.Threading.Thread.Sleep(1200);
        }

        using var app2 = TestLoomApplication.Launch(_ws);
        DtcUiTests.ApriDettaglioDtcInAudit(app2, out var w2);
        // La lista sta sotto la piega della pagina (solo le righe visibili sono realizzate): si verifica il conteggio nel titolo.
        Wait.ForCondition(() => ElementFinders.WaitById(w2, "Audit_Guasto_Titolo_Text").Name.Contains("(2/3)"),
            "filtro ancora applicato dopo il riavvio");
        Assert.True(ElementFinders.WaitById(w2, "Audit_Guasto_SoloNonCompleti_Check").AsCheckBox().IsChecked);

        ElementFinders.WaitButtonById(w2, "Audit_Guasto_Azzera_Button").SafeInvoke();
        Wait.ForCondition(() => ElementFinders.WaitById(w2, "Audit_Guasto_Titolo_Text").Name.Contains("(3/3)"), "Azzera");
        System.Threading.Thread.Sleep(500);
        using var state = new AppState(new AppConfigService(_ws.ConfigPath), runBackgroundMaintenance: false);
        Assert.Null(ViewStateService.Leggi(state, "Audit.Guasto.SoloNonCompleti"));
    }
}
