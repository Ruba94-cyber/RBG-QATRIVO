using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>6.5.2: la colonna "Veicoli Extra" esiste solo nella lista Requisiti Extra. Regressione: un layout colonne
/// gia salvato (tutte visibili, da prima del fix) la riportava in vista nella lista Standard perche
/// TableLayoutPreferences riapplica la visibilita salvata dopo l'attach, sovrascrivendo la scelta della vista.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
public sealed class RequisitiColonneD4dUiTests : UiTestBase
{
    private const string Intestazione = "Veicoli Extra";

    public RequisitiColonneD4dUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(RequisitiColonneD4dUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var std = new Requisito { Id = "REQ-STD-C", Testo = "Requisito standard colonne", ApplicabileBanco = true, ModificatoDa = "UITests" };
        var extra = new Requisito { Id = "REQ-Extra-C", Testo = "Requisito Extra colonne", ApplicabileBanco = true, ModificatoDa = "UITests" };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        state.Master.Requisiti.Save(new List<Requisito> { std, extra });

        // Layout salvato "storico": tutte le 9 colonne visibili (cosi era prima che la colonna Extra diventasse per-lista).
        var service = new AppConfigService(ws.ConfigPath);
        var config = service.Load();
        config.LayoutTabelle ??= new(StringComparer.OrdinalIgnoreCase);
        config.LayoutTabelle[TestWorkspace.DefaultOperatorName + "|Master|requirements"] = new TableLayoutPreference
        {
            Versione = 0,
            Colonne = Enumerable.Range(0, 9).Select(i => new TableColumnPreference { Indice = i, Ordine = i, Larghezza = 0, Visibile = true }).ToList()
        };
        service.Save(config);
        return ws;
    }

    private static bool HaIntestazione(MasterDataEditorPage pagina) =>
        pagina.Grid.FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem))
            .Any(h => h.Name == Intestazione && h.BoundingRectangle.Width > 0);

    private static int NumeroIntestazioni(MasterDataEditorPage pagina) =>
        pagina.Grid.FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem)).Count(h => h.BoundingRectangle.Width > 0);

}
