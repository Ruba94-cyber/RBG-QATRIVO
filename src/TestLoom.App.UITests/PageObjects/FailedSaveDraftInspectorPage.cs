using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>
/// Dialogo modale di anteprima bozze locali post-6.5.3.
/// Tutti i locator sono AutomationId; nessun testo tradotto come selettore.
/// </summary>
public sealed class FailedSaveDraftInspectorPage
{
    private readonly Window _window;

    public FailedSaveDraftInspectorPage(Window window)
    {
        _window = window;
        ElementFinders.WaitById(window, "DraftInspector_Title_Text");
        ElementFinders.WaitById(window, "DraftInspector_List");
    }

    public string Status => ElementFinders.WaitById(_window, "DraftInspector_Status_Text").AsLabel().Text;
    public string Details => ElementFinders.WaitById(_window, "DraftInspector_Details_Text").AsLabel().Text;
    public string Preview =>
        ElementFinders.WaitTextBoxById(_window, "DraftInspector_Preview_TextBox").Text;
    public bool CanPreview =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Preview_Button").IsEnabled;
    public bool CanCompare =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Compare_Button").IsEnabled;
    public string ComparisonStatus =>
        ElementFinders.WaitById(_window, "DraftInspector_CompareStatus_Text").AsLabel().Text;
    public string CanonicalLocalPreview =>
        ElementFinders.WaitTextBoxById(_window, "DraftInspector_CompareLocal_TextBox").Text;
    public string FieldDifferences =>
        ElementFinders.WaitTextBoxById(_window, "DraftInspector_Differences_TextBox").Text;
    public void CompareLocal() =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Compare_Button").SafeInvoke();
    public string PreparedStatus =>
        ElementFinders.WaitById(_window, "DraftInspector_Prepared_Text").AsLabel().Text;
    public void PrepareLocal() =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Prepare_Button").SafeInvoke();
    public bool CanNext =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Next_Button").IsEnabled;
    public bool CanPrevious =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Previous_Button").IsEnabled;
    public ListBox List =>
        ElementFinders.WaitListBoxById(_window, "DraftInspector_List");

    public void SelectRow(int index)
    {
        var list = List;
        Wait.ForCondition(() => list.Items.Length > index, $"bozza locale indice {index} disponibile");
        list.Items[index].Select();
    }

    public void LoadPreview() =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Preview_Button").SafeInvoke();
    public void Next() =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Next_Button").SafeInvoke();
    public void Previous() =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Previous_Button").SafeInvoke();
    public void Refresh() =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Refresh_Button").SafeInvoke();
    public void Close() =>
        ElementFinders.WaitButtonById(_window, "DraftInspector_Close_Button").SafeInvoke();
}
