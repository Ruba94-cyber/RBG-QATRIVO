using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Pagina Impostazioni (Master). La sidebar espone un AutomationId per sotto-sezione con lo schema
/// "Impostazioni_Sub_{Id}" (stessi Id stabili di ImpostazioniViewModel.SottoSezioni: generale, archivio,
/// recovery [solo Master], operatori, requisiti, funzionalita, componente, veicoli, strumenti, tipiditest) — vedi
/// "Una scoperta empirica importante" nel README: l'Id e' impostato in uno Style su ListBoxItem, non nel
/// DataTemplate, quindi e' raggiungibile via UI Automation.</summary>
public sealed class ImpostazioniPage
{
    private readonly AutomationElement _scope;

    public ImpostazioniPage(AutomationElement scope)
    {
        _scope = scope;
        ElementFinders.WaitById(scope, "Impostazioni_Sidebar_List");
    }

    public bool IsShown => ElementFinders.ExistsById(_scope, "Impostazioni_Sidebar_List");

    public bool HaSottoSezione(string id) => ElementFinders.ExistsById(_scope, $"Impostazioni_Sub_{id}");

    /// <summary>Seleziona una sotto-sezione dalla sidebar (es. "operatori", "componente", "veicoli", "requisiti",
    /// "funzionalita", "strumenti", "tipiditest", "archivio", "recovery", "generale").</summary>
    public void VaiA(string id) =>
        ElementFinders.WaitById(_scope, $"Impostazioni_Sub_{id}").AsListBoxItem().Select();

    public MasterDataEditorPage VaiAOperatori() { VaiA("operatori"); return new MasterDataEditorPage(_scope, "Oper"); }
    public MasterDataEditorPage VaiAEcu() { VaiA("componente"); return new MasterDataEditorPage(_scope, "Componente"); }
    public MasterDataEditorPage VaiAVeicoli() { VaiA("veicoli"); return new MasterDataEditorPage(_scope, "Veic"); }
    public MasterDataEditorPage VaiARequisiti() { VaiA("requisiti"); return new MasterDataEditorPage(_scope, "Requis"); }
    public MasterDataEditorPage VaiAFunzionalita() { VaiA("funzionalita"); return new MasterDataEditorPage(_scope, "Funz"); }
}
