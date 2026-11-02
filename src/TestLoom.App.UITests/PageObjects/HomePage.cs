using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

public sealed class HomePage
{
    private readonly AutomationElement _scope;

    public HomePage(AutomationElement scope)
    {
        _scope = scope;
        ElementFinders.WaitById(scope, "Home_Title_Text");
    }

    public bool IsTitleVisible => ElementFinders.ExistsById(_scope, "Home_Title_Text");

    // ---- Grafici: selezione e tipo di rappresentazione (id: "copertura" | "andamento" | "donut")

    public AutomationElement GraficoCheckBox(string idGrafico) =>
        ElementFinders.WaitById(_scope, $"Home_Grafico_{idGrafico}_CheckBox");

    public ComboBox SelettoreTipo(string idGrafico) =>
        ElementFinders.WaitComboBoxById(_scope, $"Home_GraficoTipo_{idGrafico}_ComboBox");

    public bool GraficoVisibile(string idGrafico) => ElementFinders.ExistsById(_scope, $"Home_Chart_{idGrafico}");

    /// <summary>Nome del grafico negli AutomationId dei pulsanti della card (Home_Grafico_{Nome}_Apri_Button, ...).</summary>
    public static string NomeGrafico(string idGrafico) => idGrafico switch
    {
        "copertura" => "Copertura",
        "andamento" => "Andamento",
        "donut" => "Esiti",
        _ => idGrafico,
    };

    public Button ApriGraficoButton(string idGrafico) =>
        ElementFinders.WaitButtonById(_scope, $"Home_Grafico_{NomeGrafico(idGrafico)}_Apri_Button");

    public Button EsportaPngGraficoButton(string idGrafico) =>
        ElementFinders.WaitButtonById(_scope, $"Home_Grafico_{NomeGrafico(idGrafico)}_EsportaPng_Button");

    public Button CopiaGraficoButton(string idGrafico) =>
        ElementFinders.WaitButtonById(_scope, $"Home_Grafico_{NomeGrafico(idGrafico)}_Copia_Button");

    /// <summary>Tipo di rappresentazione effettivamente reso dal grafico visibile (HelpText = nome enum TipoGrafico).</summary>
    public string? TipoRenderizzato(string idGrafico) =>
        ElementFinders.TryFindById(_scope, $"Home_Chart_{idGrafico}")?.HelpText;

    public void SelezionaGrafico(string idGrafico)
    {
        var check = GraficoCheckBox(idGrafico);
        if (check.Patterns.Toggle.TryGetPattern(out var toggle))
        {
            if (toggle.ToggleState.Value != FlaUI.Core.Definitions.ToggleState.On) check.SafeInvoke();
        }
        else check.SafeInvoke();
        Wait.ForCondition(() => GraficoVisibile(idGrafico), $"grafico '{idGrafico}' visibile dopo la selezione");
    }

    /// <summary>Etichette dei tipi offerti dal selettore (aprendolo); il selettore viene richiuso.</summary>
    public IReadOnlyList<string> TipiOfferti(string idGrafico)
    {
        var combo = SelettoreTipo(idGrafico);
        combo.Expand();
        try
        {
            Wait.ForCondition(() => combo.Items.Length > 0, $"voci del selettore tipo '{idGrafico}'", TimeSpan.FromSeconds(10));
            return combo.Items.Select(i => i.Text).ToList();
        }
        finally { combo.Collapse(); }
    }

    /// <summary>Apre la sezione "Filtri avanzati" della Home (chiusa di default).</summary>
    public void ApriAltriFiltri() => ApriFiltriAvanzati();

    /// <summary>Apre la sezione "Filtri avanzati" della Home (chiusa di default): pulsante con AutomationId Home_FiltriAvanzati_Apri_Button.</summary>
    public void ApriFiltriAvanzati()
    {
        if (ElementFinders.ExistsById(_scope, "Home_FiltriAvanzati_Nascondi_Button"))
            return;
        var btn = ElementFinders.TryFindById(_scope, "Home_FiltriAvanzati_Apri_Button");
        if (btn != null)
        {
            btn.SafeInvoke();
            return;
        }
        var fallback = _scope.FindFirstDescendant(cf => cf.ByName("▸ Altri filtri") ?? cf.ByName("Filtri avanzati"));
        fallback?.SafeInvoke();
    }

    /// <summary>Filtro "Tempo operazione" della Home: operatore ("maggiore di" / "minore di") e minuti (vuoto = nessun filtro).</summary>
    public void ImpostaFiltroTempoOperazione(string operatore, string minuti)
    {
        ElementFinders.WaitComboBoxById(_scope, "Home_FiltroTempoOperazione_ComboBox").SelectByText(operatore);
        ElementFinders.WaitTextBoxById(_scope, "Home_FiltroTempoOperazione_TextBox").SetText(minuti);
    }

    /// <summary>Filtro "Tempo totale Test" della Home: operatore ("maggiore di" / "minore di") e minuti (vuoto = nessun filtro).</summary>
    public void ImpostaFiltroTempoTotaleTest(string operatore, string minuti)
    {
        ElementFinders.WaitComboBoxById(_scope, "Home_FiltroTempoTotaleTest_ComboBox").SelectByText(operatore);
        ElementFinders.WaitTextBoxById(_scope, "Home_FiltroTempoTotaleTest_TextBox").SetText(minuti);
    }

    /// <summary>Riga "Filtro attivo: N test corrispondenti." del riepilogo dei filtri dashboard, o null se nessun filtro è attivo.</summary>
    public string? TestoFiltroAttivo =>
        _scope.FindAllDescendants().Select(e => e.Name).FirstOrDefault(n => n != null && n.StartsWith("Filtro attivo:", StringComparison.Ordinal));

    public void ImpostaTipo(string idGrafico, string etichetta) => SelettoreTipo(idGrafico).SelectByText(etichetta);

    /// <summary>Etichetta mostrata dal selettore (il valore non si legge da ComboBox.SelectedItem, che Avalonia
    /// non espone in modo affidabile: si legge dal testo figlio, come negli altri test sui combo).</summary>
    public string? TipoSelezionatoNelSelettore(string idGrafico) =>
        SelettoreTipo(idGrafico).FindFirstDescendant(cf => cf.ByControlType(ControlType.Text))?.Name;
}
