using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Schermata di accesso (FirstRunView), mostrata sia al primissimo avvio sia dopo un logout.
/// Assume l'elenco operatori entro la soglia "combobox vs ricerca" (workspace di test con pochi operatori,
/// coerente con [[project_ditm_lista_soglia_universale]]): usa quindi la ComboBox, non l'AutoCompleteBox.</summary>
public sealed class LoginPage
{
    private readonly AutomationElement _root;

    public LoginPage(AutomationElement root) => _root = root;

    public static bool IsShown(AutomationElement root) =>
        ElementFinders.ExistsById(root, "Login_Operatore_ComboBox") || ElementFinders.ExistsById(root, "Login_Pin_TextBox");

    public void SelectOperator(string name) =>
        ElementFinders.WaitComboBoxById(_root, "Login_Operatore_ComboBox").SelectByText(name);

    public void EnterPin(string pin) =>
        ElementFinders.WaitTextBoxById(_root, "Login_Pin_TextBox").SetText(pin);

    public void Confirm() =>
        ElementFinders.WaitButtonById(_root, "Login_Confirm_Button").SafeInvoke();

    public bool HasError => ElementFinders.ExistsById(_root, "Login_Error_Text");

    public string ErrorText => ElementFinders.WaitById(_root, "Login_Error_Text").AsLabel().Text;

    public void LoginAs(string name, string pin)
    {
        SelectOperator(name);
        EnterPin(pin);
        Confirm();
    }
}
