using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Lista Test (§/TestCaseListView). Il conteggio/apertura riga assume deliberatamente pochi
/// risultati (workspace di test isolato, mai decine di migliaia di righe reali): con più di una manciata di
/// righe visibili andrebbe scoperto anche l'AutomationId di riga tramite scroll/virtualizzazione, fuori dallo
/// scope della smoke suite iniziale (vedi README, "Gap residui").</summary>
public sealed class TestListPage
{
    private readonly Window _window;
    private readonly AutomationElement _scope;

    public TestListPage(Window window, AutomationElement scope)
    {
        _window = window;
        _scope = scope;
        ElementFinders.WaitById(scope, "TestList_Title_Text");
    }

    public void Search(string text) =>
        ElementFinders.WaitTextBoxById(_scope, "TestList_Search_TextBox").SetText(text);

    /// <summary>Data di un filtro Lista Test (CalendarDatePicker, testo digitabile): automationId
    /// TestList_FiltroDataDa_DatePicker oppure TestList_FiltroDataA_DatePicker. Richiede i filtri avanzati aperti.</summary>
    public void ImpostaData(string automationId, string testo) =>
        ElementFinders.WaitById(_scope, automationId).ImpostaTestoData(testo);

    /// <summary>Come ImpostaData, ma digitando sulla tastiera (Ctrl+A, caratteri, Invio): il commit avviene dal tasto.</summary>
    public void DigitaData(string automationId, string testo) =>
        ElementFinders.WaitById(_scope, automationId).DigitaDataConTastiera(testo);

    /// <summary>Sceglie un giorno dal calendario del filtro data indicato (si apre sul mese gia' impostato).</summary>
    public void ScegliDataDalCalendario(TestLoomApplication app, string automationId, int giorno) =>
        ElementFinders.WaitById(_scope, automationId).SceglieGiornoDalCalendario(app, giorno);

    /// <summary>Messaggio "data non valida" sotto i filtri Lista Test: compare solo dopo una data digitata non valida.</summary>
    public bool MessaggioDataNonValidaVisibile => ElementFinders.ExistsById(_scope, "TestList_DataNonValida_Text");

    /// <summary>Il gruppo di filtri che include "Solo importati da UnitLogger" vive dentro "Filtri avanzati"
    /// (TestCaseListView.axaml, StackPanel IsVisible="{Binding MostraFiltriAvanzati}"), collassato di default
    /// e senza un AutomationId proprio sul link che lo espande — cercato per Nome/ControlType, stesso limite
    /// gia' noto per altri link senza Id (vedi RimuoviFiltriLink di NonConformitaView).</summary>
    public void ApriFiltriAvanzati()
    {
        if (ElementFinders.ExistsById(_scope, "TestList_FiltroImportatoUdt_CheckBox")) return;
        var btn = ElementFinders.TryFindById(_scope, "TestList_FiltriAvanzati_Button");
        if (btn != null) { btn.SafeInvoke(); return; }
        _scope.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Filtri avanzati")))
            ?.AsButton().SafeInvoke();
    }

    /// <summary>Esito del filtro Lista Test. La ricerca ha un debounce: finche' si vedono ancora righe il filtro
    /// potrebbe non essere stato applicato, quindi "non vuota" vale solo se le righe restano presenti stabilmente
    /// (1,5 s di fila); "vuota" appena compare il messaggio di elenco vuoto. Evita la corsa in cui la riga
    /// precedente al filtro veniva letta come risultato.</summary>
    public bool IsEmpty
    {
        get
        {
            var limite = DateTime.UtcNow.AddSeconds(10);
            DateTime? righeDa = null;
            while (DateTime.UtcNow < limite)
            {
                if (ElementFinders.ExistsById(_scope, "TestList_Empty_Text")) return true;
                if (RowCount() > 0)
                {
                    righeDa ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - righeDa.Value > TimeSpan.FromMilliseconds(1500)) return false;
                }
                else righeDa = null;
                Thread.Sleep(100);
            }
            return ElementFinders.ExistsById(_scope, "TestList_Empty_Text");
        }
    }

    public int RowCount() => _scope.FindAllDescendants(cf => cf.ByAutomationId("TestList_Open_Button")).Length;

    public IReadOnlyList<string> VisibleRowIds() =>
        _scope.FindAllDescendants(cf => cf.ByAutomationId("TestList_Row_Id_Text")).Select(e => e.AsLabel().Text).ToList();

    /// <summary>Apre l'editor a partire dalla riga con l'Id indicato — usato dai test di persistenza dopo un
    /// riavvio di TestLoom, dove l'Id del Test creato in una sessione precedente è già noto.</summary>
    public TestEditorPage OpenRowById(string id, TimeSpan? timeout = null)
    {
        var idCell = Wait.Until(
            () => _scope.FindAllDescendants(cf => cf.ByAutomationId("TestList_Row_Id_Text")).FirstOrDefault(e => e.AsLabel().Text == id),
            e => e != null, timeout, $"riga Test con Id='{id}'")!;
        var row = FindRowContainer(idCell);
        ElementFinders.WaitByIdWithinRow(row, "TestList_Open_Button").AsButton().SafeInvoke();
        return new TestEditorPage(_window);
    }

    /// <summary>Apre l'unica riga presente — richiede esattamente un risultato, coerente con un workspace di
    /// test appena popolato da un singolo Test creato nello stesso test.</summary>
    public TestEditorPage OpenOnlyRow()
    {
        Wait.ForCondition(() => RowCount() == 1, "esattamente una riga in Lista Test");
        ElementFinders.WaitButtonById(_scope, "TestList_Open_Button").SafeInvoke();
        return new TestEditorPage(_window);
    }

    public TestEditorPage ClickNew()
    {
        ElementFinders.WaitButtonById(_scope, "TestList_New_Button").SafeInvoke();
        return new TestEditorPage(_window);
    }

    /// <summary>Risale dalla cella Id al contenitore di riga più vicino con più di una cella (DataRow), così
    /// il bottone "Apri" cercato dopo è sicuramente quello della STESSA riga e non di una riga diversa che
    /// condivide lo stesso AutomationId di template. Ogni singola cella della DataGrid è a sua volta
    /// avvolta in un elemento ControlType.Custom (es. "Avalonia.Controls.TextBlock"), quindi il primo
    /// antenato di ControlType Custom/DataItem/ListItem è quasi sempre proprio quel wrapper di cella, troppo
    /// STRETTO per contenere anche il bottone "Apri" della stessa riga (mai verificato prima con più di una
    /// riga in griglia, unico caso che lo fa emergere): si risale finché l'antenato non contiene DAVVERO un
    /// "TestList_Open_Button" tra i suoi discendenti, il segnale affidabile di "questo è un contenitore di
    /// riga", non il ControlType da solo.</summary>
    private static AutomationElement FindRowContainer(AutomationElement idCell)
    {
        var current = idCell.Parent;
        for (var i = 0; i < 10 && current != null; i++, current = current.Parent)
        {
            if (current.FindFirstDescendant(cf => cf.ByAutomationId("TestList_Open_Button")) != null)
                return current;
        }
        // Fallback documentato: se nessun antenato entro 10 livelli contiene un "TestList_Open_Button",
        // risale di un solo livello — sufficiente solo quando la griglia ha una sola riga.
        return idCell.Parent ?? idCell;
    }
}
