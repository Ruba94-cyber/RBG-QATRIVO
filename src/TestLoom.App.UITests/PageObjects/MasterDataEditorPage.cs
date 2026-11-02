using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using Keyboard = FlaUI.Core.Input.Keyboard;
using Mouse = FlaUI.Core.Input.Mouse;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Page Object generico per le sotto-sezioni di Impostazioni costruite sullo stesso schema
/// (MasterDataEditorViewModelBase): Operatori, componente, Veicoli, Requisiti, Funzionalita' condividono la stessa
/// disposizione (Modifica/Nuovo[/ImportaExcel]/Annulla/Salva in alto, filtro testo, DataGrid "GridPrincipale"),
/// solo con prefisso AutomationId diverso per sezione (es. "Oper_", "Componente_", "Veic_", "Requis_", "Funz_").
/// Evita di duplicare lo stesso Page Object cinque volte: i test restano leggibili passando solo il prefisso.
/// Il DataGrid non e' scoped al prefisso (x:Name="GridPrincipale" condiviso da piu' viste), ma essendo una
/// sola vista visibile alla volta nello shell va bene cercarlo direttamente nella finestra, come gia' fa
/// StrumentiOrdineUiTests.</summary>
public sealed class MasterDataEditorPage
{
    private readonly AutomationElement _scope;
    private readonly string _prefix;

    public MasterDataEditorPage(AutomationElement scope, string automationIdPrefix)
    {
        _scope = scope;
        _prefix = automationIdPrefix;
        ElementFinders.WaitById(scope, "GridPrincipale");
    }

    public bool IsShown => ElementFinders.ExistsById(_scope, "GridPrincipale");

    // "Modifica" e' nascosto (non solo disabilitato) a chi non puo' modificare: l'assenza del pulsante non implica
    // piu' "gia' in modifica". Salva compare solo dentro una sessione di modifica.
    public bool InModifica => ElementFinders.ExistsById(_scope, $"{_prefix}_Salva_Button");

    public AutomationElement Grid => ElementFinders.WaitById(_scope, "GridPrincipale");

    public void IniziaModifica() =>
        ElementFinders.WaitButtonById(_scope, $"{_prefix}_Modifica_Button").SafeInvoke();

    public void Annulla() =>
        ElementFinders.WaitButtonById(_scope, $"{_prefix}_Annulla_Button").SafeInvoke();

    public void Salva() =>
        ElementFinders.WaitButtonById(_scope, $"{_prefix}_Salva_Button").SafeInvoke();

    /// <summary>"Modifica" e' visibile solo a chi puo' entrare in modifica (azione non disponibile = nascosta, non
    /// disabilitata) e solo fuori dalla modifica: presenza e Enabled coincidono con "puo' modificare".
    /// ATTENZIONE: nei cataloghi dove uno Standard puo' creare voci (Componente, Funzionalita') la visibilita' segue
    /// PuoCreare, non PuoModificare: li' questo metodo misura "puo' entrare in modifica".</summary>
    public bool PuoModificare()
    {
        var bottone = ElementFinders.TryFindById(_scope, $"{_prefix}_Modifica_Button");
        return bottone != null && bottone.AsButton().IsEnabled;
    }

    public bool PuoSalvare() => ElementFinders.ExistsById(_scope, $"{_prefix}_Salva_Button");

    // ---------- Operazioni su catalogo: ricerca, paginazione, righe, messaggi ----------

    /// <summary>Filtro testo della sotto-sezione (la griglia mostra solo le righe che lo contengono).</summary>
    public void Cerca(string testo) => ElementFinders.WaitTextBoxById(_scope, $"{_prefix}_Cerca_TextBox").SetText(testo);

    /// <summary>Numero di pagina visualizzato (es. "1" o "2"); vuoto se la paginazione non è visibile.</summary>
    public string PaginaCorrente => ElementFinders.TryFindById(_scope, $"{_prefix}_PaginaCorrente_Text")?.Name ?? string.Empty;

    public void PaginaSuccessiva() => ElementFinders.WaitButtonById(_scope, $"{_prefix}_PaginaSucc_Button").SafeInvoke();

    public void PaginaPrecedente() => ElementFinders.WaitButtonById(_scope, $"{_prefix}_PaginaPrec_Button").SafeInvoke();

    /// <summary>Testo del banner di errore della sotto-sezione (es. Salva senza Nome), o null se non mostrato.</summary>
    public string? MessaggioErrore => ElementFinders.TryFindById(_scope, $"{_prefix}_MessaggioErrore_Text")?.Name;

    /// <summary>Aggiunge una riga vuota con "+Nuovo" (solo in modifica).</summary>
    public void NuovaRiga() => ElementFinders.WaitButtonById(_scope, $"{_prefix}_Nuovo_Button").SafeInvoke();

    /// <summary>Chiavi (prima colonna: Nome/Id) delle righe realizzate, nell'ordine della griglia.</summary>
    public IReadOnlyList<string> ChiaviVisibili() =>
        Grid.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem))
            .Select(riga => TestoPrimaCella(riga)).ToList();

    /// <summary>Scrive la chiave dell'ultima riga della griglia (quella appena creata con "+Nuovo") passando
    /// dall'editor di cella: F2 sulla prima colonna apre un TextBox (AutomationId "CellTextBox"), la chiave è digitata
    /// da tastiera e Invio conferma la modifica. Verificato dal vivo: Invio chiude l'editor e committa il valore; il
    /// solo cambio di ValuePattern seguito da Tab non lo chiudeva.</summary>
    public void ScriviChiaveUltimaRiga(string valore)
    {
        var riga = Wait.ForElement(
            () => Grid.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)).LastOrDefault(),
            "riga nuova nella griglia");
        // La prima colonna può essere di comandi (es. ▲▼ in Strumenti): si prova F2 su ogni cella; se nessuna apre l'editor,
        // un doppio clic sulla cella di testo (senza pulsanti, così non si sposta mai una riga con ▲▼) lo apre.
        AutomationElement? editor = null;
        foreach (var cella in riga.FindAllChildren())
        {
            editor = ApriEditorConF2(riga, cella);
            if (editor != null) break;
        }
        if (editor == null)
        {
            foreach (var cella in riga.FindAllChildren().Where(c => c.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)) == null))
            {
                editor = ApriEditorConDoppioClic(riga, cella);
                if (editor != null) break;
            }
        }
        if (editor == null) throw new TimeoutException("editor della cella Nome: nessuna colonna della riga nuova lo apre");
        editor.Focus();
        Keyboard.Type(valore);
        Wait.ForCondition(() => editor.AsTextBox().Text == valore, $"testo digitato '{valore}' nell'editor della cella");
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
        Wait.ForCondition(() => riga.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)) == null,
            "editor della cella chiuso dopo Invio");
    }

    /// <summary>Archivia la riga con la chiave indicata tramite il pulsante "Archivia" della riga (solo in modifica).</summary>
    public void ArchiviaRiga(string chiave)
    {
        var riga = RigaConChiave(chiave);
        ElementFinders.WaitByIdWithinRow(riga, $"{_prefix}_RigaArchivia_Button").AsButton().SafeInvoke();
    }

    private AutomationElement RigaConChiave(string chiave) =>
        Wait.ForElement(
            () => Grid.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem))
                .FirstOrDefault(riga => string.Equals(TestoPrimaCella(riga), chiave, StringComparison.Ordinal)),
            $"riga con chiave '{chiave}'");

    private static AutomationElement? EditorDellaRiga(AutomationElement riga) =>
        riga.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));

    /// <summary>Focus sulla cella e F2: ritorna l'editor della riga se si apre, null altrimenti.</summary>
    private static AutomationElement? ApriEditorConF2(AutomationElement riga, AutomationElement cella)
    {
        try { cella.Focus(); }
        catch (Exception) { return null; }   // cella non focalizzabile: si prova la successiva
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.F2);
        return Wait.TryForCondition(() => EditorDellaRiga(riga) != null, TimeSpan.FromSeconds(2)) ? EditorDellaRiga(riga) : null;
    }

    /// <summary>Doppio clic sulla cella: ritorna l'editor della riga se si apre, null altrimenti.</summary>
    private static AutomationElement? ApriEditorConDoppioClic(AutomationElement riga, AutomationElement cella)
    {
        try { Mouse.LeftDoubleClick(cella.GetClickablePoint()); }
        catch (Exception) { return null; }
        return Wait.TryForCondition(() => EditorDellaRiga(riga) != null, TimeSpan.FromSeconds(2)) ? EditorDellaRiga(riga) : null;
    }

    private static string TestoPrimaCella(AutomationElement riga)
    {
        // Si legge la prima cella con testo: le colonne di comandi (es. ▲▼ in Strumenti) non hanno testo e vanno saltate.
        // Le celle con AutomationId "CellTextBlock" hanno la precedenza; nelle griglie che non lo espongono (es. Veicoli)
        // si usa il primo testo della cella.
        foreach (var cella in riga.FindAllChildren())
        {
            var testo = cella.FindFirstDescendant(cf => cf.ByAutomationId("CellTextBlock"))?.Name
                ?? cella.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))?.Name;
            if (!string.IsNullOrEmpty(testo)) return testo;
        }
        return string.Empty;
    }
}
