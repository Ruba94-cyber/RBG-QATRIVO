using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Valori mostrati (sola lettura) da una riga compatta anomalia.</summary>
public sealed record RigaAnomaliaSnapshot(string Id, string Stato, string Priorita, string Owner);

/// <summary>Elenco COMPATTO delle anomalie collegate (un solo ListBox virtualizzato) nella pagina Test (prefisso
/// "Test_") oppure nella finestra "Verifica anomalie" della Lista Test (prefisso "ListaTest_"). Le righe non
/// realizzate dalla virtualizzazione non compaiono nell'albero UIA. Gli accessori "Try" non lanciano mai per
/// riga assente o elemento riciclato: restituiscono null, cosi' dentro Wait.ForCondition si limitano a ritentare.
/// La modifica (stato, campi, chiusura con motivazione) avviene nel pop-up di dettaglio, aperto dal pulsante
/// "Modifica stato" di ogni riga.</summary>
public sealed class TestAnomaliePanel
{
    private readonly AutomationElement _root;
    private readonly string _p; // prefisso: "Test_Anomalia" (editor) o "ListaTest_Anomalia" (finestra della Lista Test)
    private readonly string _lista;
    private AutomationElement? _elenco;

    /// <summary>Pannello dell'editor Test.</summary>
    public TestAnomaliePanel(Window window) : this(window, "Test_Anomalia", "Test_Anomalie_Aperte_List") { }

    private TestAnomaliePanel(AutomationElement root, string prefisso, string lista)
    {
        _root = root; _p = prefisso; _lista = lista;
    }

    /// <summary>Elenco della finestra "Verifica anomalie" aperta dalla Lista Test.</summary>
    public static TestAnomaliePanel DaFinestraListaTest(Window finestra) => new(finestra, "ListaTest_Anomalia", "ListaTest_Anomalie_List");

    /// <summary>Cache dell'elemento elenco (solo per cicli lunghi senza navigazione: dopo un reload del Test
    /// l'elemento diventa stantio, quindi di default si rilegge a ogni chiamata).</summary>
    public bool UsaCache { get; set; }

    private AutomationElement RadiceElenco()
    {
        try
        {
            if (UsaCache && _elenco != null && _elenco.IsAvailable) return _elenco;
            _elenco = _root.FindFirstDescendant(cf => cf.ByAutomationId(_lista));
        }
        catch (Exception) { _elenco = null; }
        return _elenco ?? _root;
    }

    private IReadOnlyList<AutomationElement> Tutti(string idSuffisso)
    {
        try { return RadiceElenco().FindAllDescendants(cf => cf.ByAutomationId($"{_p}_{idSuffisso}")).ToList(); }
        catch (Exception) { return Array.Empty<AutomationElement>(); }
    }

    /// <summary>Numero di elementi nell'albero UIA con l'id dell'elenco: deve essere SEMPRE 1 (un solo contenitore).</summary>
    public int NumeroContenitoriElenco()
    {
        try { return _root.FindAllDescendants(cf => cf.ByAutomationId(_lista)).Length; }
        catch (Exception) { return -1; }
    }

    public IReadOnlyList<string> IdVisibili()
    {
        try { return Tutti("Id_Text").Select(e => e.AsLabel().Text).ToList(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>La virtualizzazione realizza solo le righe nel viewport EFFETTIVO (anche quello della pagina che
    /// scorre): porta in vista la prima riga prima di contare/leggere.</summary>
    public void PortaInVista()
    {
        try
        {
            // Solo nell'editor (pagina che scorre): si porta il pannello al centro della finestra con la rotella
            // sopra il pulsante "Genera pacchetto TICKET" (fuori dall'elenco, quindi scorre la pagina e non la lista).
            var riferimento = _root.FindFirstDescendant(cf => cf.ByAutomationId("Test_Anomalie_GeneraPacchettoEsta_Button"));
            if (riferimento == null) return;
            riferimento.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            System.Threading.Thread.Sleep(200);
            Mouse.MoveTo(riferimento.GetClickablePoint());
            Mouse.Scroll(-3);
        }
        catch (Exception) { /* best effort */ }
        System.Threading.Thread.Sleep(400);
    }

    public int NumeroRigheRealizzate => Tutti("Id_Text").Count;
    public bool NessunaAperta => ElementFinders.ExistsById(_root, "Test_Anomalie_Nessuna_Text");

    public void AttendiPrimaRiga(string id, string descrizione) =>
        Infrastructure.Wait.ForCondition(() => IdVisibili().FirstOrDefault() == id, descrizione);

    private static string? Testo(AutomationElement? e)
    {
        try { return e?.AsLabel().Text; }
        catch (Exception) { return null; }
    }

    public string? TryOwner(int indice) => indice >= 0 && indice < Tutti("Owner_Text").Count ? Testo(Tutti("Owner_Text")[indice]) : null;
    public string? TryStato(int indice) => indice >= 0 && indice < Tutti("Stato_Text").Count ? Testo(Tutti("Stato_Text")[indice]) : null;
    public string? TryPriorita(int indice) => indice >= 0 && indice < Tutti("Priorita_Text").Count ? Testo(Tutti("Priorita_Text")[indice]) : null;

    /// <summary>Contenitore della riga con l'Id indicato: primo antenato della cella Id che contiene anche il pulsante.</summary>
    private AutomationElement? Contenitore(string id)
    {
        try
        {
            var cella = Tutti("Id_Text").FirstOrDefault(e => e.AsLabel().Text == id);
            var corrente = cella?.Parent;
            var bottone = _p.StartsWith("Test_", StringComparison.Ordinal) ? "Test_Anomalia_Dettaglio_Button" : "ListaTest_Anomalia_Modifica_Button";
            while (corrente != null && corrente.FindFirstDescendant(cf => cf.ByAutomationId(bottone)) == null)
                corrente = corrente.Parent;
            return corrente;
        }
        catch (Exception) { return null; }
    }

    private string IdBottone => _p.StartsWith("Test_", StringComparison.Ordinal) ? "Test_Anomalia_Dettaglio_Button" : "ListaTest_Anomalia_Modifica_Button";

    public RigaAnomaliaSnapshot? TrySnapshot(string id)
    {
        try
        {
            var riga = Contenitore(id);
            if (riga == null) return null;
            string T(string suff) => Testo(riga.FindFirstDescendant(cf => cf.ByAutomationId($"{_p}_{suff}"))) ?? "";
            return new RigaAnomaliaSnapshot(id, T("Stato_Text"), T("Priorita_Text"), T("Owner_Text"));
        }
        catch (Exception) { return null; }
    }

    public IReadOnlyList<RigaAnomaliaSnapshot> SnapshotVisibili() =>
        IdVisibili().Select(TrySnapshot).Where(s => s != null).Select(s => s!).ToList();

    // ---- tastiera ----

    public void FocusPrimaRiga()
    {
        var item = RadiceElenco().FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).FirstOrDefault()
                   ?? throw new InvalidOperationException("Nessuna riga anomalia realizzata");
        item.Focus();
    }

    public void PaginaGiu()
    {
        Keyboard.Type(VirtualKeyShort.NEXT);
        System.Threading.Thread.Sleep(400);
    }

    public void InCima()
    {
        Keyboard.Type(VirtualKeyShort.HOME);
        System.Threading.Thread.Sleep(400);
    }

    /// <summary>Id UIA dell'elemento che ha il focus (vuoto se nessuno).</summary>
    public string IdConFocus()
    {
        try { return _root.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault ?? ""; }
        catch (Exception) { return ""; }
    }

    /// <summary>Id dell'anomalia della riga che contiene l'elemento col focus (vuoto se fuori dall'elenco).</summary>
    public string IdRigaConFocus()
    {
        try
        {
            var corrente = _root.Automation.FocusedElement();
            for (var i = 0; i < 8 && corrente != null; i++, corrente = corrente.Parent)
            {
                var cella = corrente.FindFirstDescendant(cf => cf.ByAutomationId($"{_p}_Id_Text"));
                if (cella != null) return cella.AsLabel().Text;
            }
        }
        catch (Exception) { /* focus fuori dall'albero */ }
        return "";
    }

    public void FocusPulsante(string id)
    {
        var b = Contenitore(id)?.FindFirstDescendant(cf => cf.ByAutomationId(IdBottone))
                ?? throw new InvalidOperationException($"Riga {id} non realizzata");
        try { b.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch (Exception) { /* best effort */ }
        b.Focus();
        System.Threading.Thread.Sleep(300);
    }

    public void Tab(bool indietro = false)
    {
        if (indietro) Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        else Keyboard.Type(VirtualKeyShort.TAB);
        System.Threading.Thread.Sleep(150);
    }

    // ---- pop-up di dettaglio ----

    /// <summary>Apre il pop-up "Modifica stato" della riga con l'Id indicato (deve essere realizzata).</summary>
    public AnomaliaDettaglioPage ApriDettaglioDi(TestLoomApplication app, string id)
    {
        var b = Contenitore(id)?.FindFirstDescendant(cf => cf.ByAutomationId(IdBottone))
                ?? throw new InvalidOperationException($"Riga {id} non realizzata");
        b.AsButton().SafeInvoke();
        return new AnomaliaDettaglioPage(app.WaitForWindowByContent(AnomaliaDettaglioPage.SalvaId));
    }

    /// <summary>Apre il pop-up della prima riga aperta realizzata.</summary>
    public AnomaliaDettaglioPage ApriDettaglio(TestLoomApplication app, int indice)
    {
        var b = Tutti(_p.StartsWith("Test_", StringComparison.Ordinal) ? "Dettaglio_Button" : "Modifica_Button");
        if (indice < 0 || indice >= b.Count) throw new InvalidOperationException($"Nessuna anomalia all'indice {indice}");
        b[indice].AsButton().SafeInvoke();
        return new AnomaliaDettaglioPage(app.WaitForWindowByContent(AnomaliaDettaglioPage.SalvaId));
    }

    /// <summary>Apre il pop-up di un'anomalia CHIUSA (elenco "Anomalie chiuse" dell'editor, Expander aperto).</summary>
    public AnomaliaDettaglioPage ApriDettaglioChiusa(TestLoomApplication app, int indice)
    {
        var b = _root.FindAllDescendants(cf => cf.ByAutomationId("Test_Anomalia_Chiusa_Dettaglio_Button"));
        if (indice < 0 || indice >= b.Length) throw new InvalidOperationException($"Nessuna anomalia chiusa all'indice {indice}");
        b[indice].AsButton().SafeInvoke();
        return new AnomaliaDettaglioPage(app.WaitForWindowByContent(AnomaliaDettaglioPage.SalvaId));
    }

    /// <summary>Messaggio di errore mostrato dal pop-up (vuoto se assente).</summary>
    public static string ErroreDialogo(AnomaliaDettaglioPage dettaglio) =>
        ElementFinders.TryFindById(dettaglio.Window, "AnomaliaDettaglio_Errore_Text")?.AsLabel().Text ?? "";
}
