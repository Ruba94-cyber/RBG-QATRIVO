using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using TestLoom.App.UITests.Infrastructure;
using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;

namespace TestLoom.App.UITests.PageObjects;

public sealed class NonConformitaPage
{
    private readonly AutomationElement _scope;

    public NonConformitaPage(AutomationElement scope)
    {
        _scope = scope;
        ElementFinders.WaitById(scope, "Anomalie_Title_Text");
    }

    public bool IsTitleVisible => ElementFinders.ExistsById(_scope, "Anomalie_Title_Text");

    /// <summary>Scrive nel campo di ricerca libera della pagina. Il ViewModel ricarica a ogni carattere
    /// (nessun debounce), quindi al ritorno la lista e' gia' aggiornata.</summary>
    public void Cerca(string testo) =>
        ElementFinders.WaitTextBoxById(_scope, "Anomalie_FiltroTesto_Input").SetText(testo);

    /// <summary>Testo della barra di paginazione: "Pagina X / Y - N non conformita'". E' l'unico punto della
    /// UI che espone il TOTALE filtrato e non le sole righe della pagina corrente.</summary>
    public string TestoPaginazione() =>
        ElementFinders.WaitById(_scope, "Anomalie_Paginazione_Text").Name ?? string.Empty;

    /// <summary>Totale delle Non Conformita' che soddisfano i filtri, estratto dalla barra di paginazione.</summary>
    public long TotaleFiltrato()
    {
        var testo = TestoPaginazione();
        var match = System.Text.RegularExpressions.Regex.Match(testo.Replace(".", "").Replace(",", ""), @"(\d+)\s+non\s+conformit");
        if (!match.Success)
            throw new InvalidOperationException("Barra di paginazione non interpretabile: '" + testo + "'");
        return long.Parse(match.Groups[1].Value);
    }

    /// <summary>Conteggio letto quando si e' stabilizzato: l'archivio viene ingerito in memoria mentre la
    /// finestra e' gia' utilizzabile, quindi una lettura immediata dopo aver digitato puo' fotografare un
    /// caricamento ancora in corso e restituire un totale parziale. Attende due letture consecutive uguali.</summary>
    public long TotaleFiltratoStabile(int tentativiMax = 30, int pausaMs = 1000)
    {
        long precedente = -1;
        for (var i = 0; i < tentativiMax; i++)
        {
            var attuale = TotaleFiltrato();
            if (attuale == precedente && attuale > 0) return attuale;
            precedente = attuale;
            Thread.Sleep(pausaMs);
        }
        return precedente;
    }

    /// <summary>Filtri multi-selezione della pagina (AutomationId Anomalie_Filtro{nome}_Button): Priorita, Gravita,
    /// StatoAnomalia, Assegnatario, Funzionalita, Veicolo (sottoveicolo), Componente.</summary>
    public void ApriFiltro(string nome)
    {
        if (nome != "Assegnatario") EspandiFiltri();
        ElementFinders.WaitButtonById(_scope, $"Anomalie_Filtro{nome}_Button").SafeInvoke();
    }

    /// <summary>Pannello "Altri filtri": parte sempre collassato. I filtri principali (ricerca, Stato,
    /// Assegnatario) sono sempre visibili e non richiedono l'espansione.</summary>
    public bool FiltriEspansi
    {
        get
        {
            var e = ElementFinders.WaitById(_scope, "Anomalie_Filtri_Expander");
            return e.Patterns.ExpandCollapse.TryGetPattern(out var p) && p.ExpandCollapseState.Value == FlaUI.Core.Definitions.ExpandCollapseState.Expanded;
        }
    }

    public void EspandiFiltri()
    {
        if (FiltriEspansi) return;
        var e = ElementFinders.WaitById(_scope, "Anomalie_Filtri_Expander");
        if (e.Patterns.ExpandCollapse.TryGetPattern(out var p)) p.Expand(); else e.Click();
        Wait.ForCondition(() => FiltriEspansi, "il pannello Altri filtri si espande");
    }

    public void CollassaFiltri()
    {
        if (!FiltriEspansi) return;
        var e = ElementFinders.WaitById(_scope, "Anomalie_Filtri_Expander");
        if (e.Patterns.ExpandCollapse.TryGetPattern(out var p)) p.Collapse(); else e.Click();
        Wait.ForCondition(() => !FiltriEspansi, "il pannello Altri filtri si collassa");
    }

    /// <summary>Testo del riepilogo del pannello (es. "Altri filtri (2 attivi)"), visibile anche collassato.</summary>
    public string RiepilogoFiltri() =>
        // L'intestazione dell'Expander e' il ToggleButton 'ExpanderHeader' del template: il suo Name e' il testo
        // del riepilogo.
        ElementFinders.WaitById(ElementFinders.WaitById(_scope, "Anomalie_Filtri_Expander"), "ExpanderHeader").Name ?? string.Empty;

    public string TestoFiltro(string nome)
    {
        if (nome != "Assegnatario") EspandiFiltri();
        return ElementFinders.WaitButtonById(_scope, $"Anomalie_Filtro{nome}_Button").Name ?? string.Empty;
    }

    /// <summary>Seleziona le voci indicate in un filtro multi-selezione e chiude il flyout con Esc.</summary>
    public void SelezionaVoci(TestLoomApplication app, string nomeFiltro, params string[] etichette)
    {
        ApriFiltro(nomeFiltro);
        var flyout = new FiltroMultiSelectPage(app);
        foreach (var e in etichette) flyout.SelezionaVoce(e);
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Wait.ForCondition(() => app.IsWindowClosedByContent("FiltroMultiSelect_Lista"), "il flyout del filtro si chiude");
    }

    /// <summary>campo: DataAperturaDa, DataAperturaA, DataChiusuraDa, DataChiusuraA. Il campo e' un CalendarDatePicker: il
    /// testo si scrive tramite ValuePattern (la stessa interpretazione di una digitazione confermata).</summary>
    public void ImpostaData(string campo, string testo)
    {
        EspandiFiltri();
        ElementFinders.WaitById(_scope, $"Anomalie_{campo}_Input").ImpostaTestoData(testo);
    }

    /// <summary>Sceglie un giorno dal calendario del campo data: il calendario si apre sul mese gia' impostato nel campo.</summary>
    public void ScegliDataDalCalendario(TestLoomApplication app, string campo, int giorno)
    {
        EspandiFiltri();
        ElementFinders.WaitById(_scope, $"Anomalie_{campo}_Input").SceglieGiornoDalCalendario(app, giorno);
    }

    /// <summary>Messaggio "data non valida" sotto i filtri: compare solo dopo una data digitata non valida.</summary>
    public bool MessaggioDataNonValidaVisibile => ElementFinders.ExistsById(_scope, "Anomalie_DataNonValida_Text");

    public void AzzeraFiltri() =>
        ElementFinders.WaitButtonById(_scope, "Anomalie_RimuoviFiltri_Button").SafeInvoke();

    public bool AzzeraFiltriVisibile => ElementFinders.ExistsById(_scope, "Anomalie_RimuoviFiltri_Button");

    /// <summary>Id delle NC mostrate nella pagina corrente, nell'ordine in cui compaiono nella griglia.</summary>
    public IReadOnlyList<string> IdRighe() =>
        ElementFinders.WaitById(_scope, "Anomalie_Righe_DataGrid")
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .Where(t => (t.Name ?? "").StartsWith("NC-", StringComparison.Ordinal) && t.BoundingRectangle.Height > 0)
            // L'ordine dell'albero UIA segue i contenitori riciclati dalla virtualizzazione, non l'ordine visivo.
            .OrderBy(t => t.BoundingRectangle.Top).Select(t => t.Name!).ToList();

    /// <summary>Porta l'elemento nell'area visibile della pagina scorrendo con la rotella (ScrollViewer della pagina).</summary>
    private void AssicuraVisibile(AutomationElement el)
    {
        var area = _scope.BoundingRectangle;
        for (var i = 0; i < 12; i++)
        {
            var r = el.BoundingRectangle;
            if (r.Width > 0 && r.Top >= area.Top && r.Bottom <= area.Bottom) return;
            FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point((int)(area.Left + area.Width / 2), (int)(area.Top + area.Height / 3)));
            FlaUI.Core.Input.Mouse.Scroll(r.Top >= area.Bottom ? -3 : 3);
            Thread.Sleep(150);
        }
    }

    /// <summary>Clic sull'intestazione di colonna (ordinamento). Il testo puo' avere una freccia finale.</summary>
    public void OrdinaPerColonna(string titolo)
    {
        var grid = ElementFinders.WaitById(_scope, "Anomalie_Righe_DataGrid");
        var intestazione = Wait.Until(
            () => grid.FindAllDescendants().FirstOrDefault(e =>
                e.ControlType is FlaUI.Core.Definitions.ControlType.HeaderItem or FlaUI.Core.Definitions.ControlType.Header
                && (e.Name ?? "").StartsWith(titolo, StringComparison.Ordinal)),
            h => h != null, description: $"intestazione colonna '{titolo}'");
        AssicuraVisibile(intestazione!);
        intestazione!.Click();
    }

    /// <summary>Titoli delle colonne visibili (intestazioni con larghezza &gt; 0), da sinistra a destra.</summary>
    public string[] TitoliColonneVisibili() =>
        ElementFinders.WaitById(_scope, "Anomalie_Righe_DataGrid")
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.HeaderItem))
            .Where(h => h.BoundingRectangle.Width > 0).OrderBy(h => h.BoundingRectangle.Left)
            .Select(e => e.Name ?? "").ToArray();

    /// <summary>Menu colonne di TableLayoutPreferences: tasto destro su un'intestazione e clic sulla voce
    /// (nome = titolo della colonna senza freccia di ordinamento).</summary>
    public void CommutaColonna(Window window, string titolo)
    {
        var grid = ElementFinders.WaitById(_scope, "Anomalie_Righe_DataGrid");
        var header = grid.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.HeaderItem))
            .Where(h => h.BoundingRectangle.Width > 0).OrderBy(h => h.BoundingRectangle.Left).First();
        AssicuraVisibile(header);
        var r = header.BoundingRectangle;
        var punto = new System.Drawing.Point((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        var voce = Wait.ForElement(() =>
        {
            FlaUI.Core.Input.Mouse.RightClick(punto);
            return window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.MenuItem).And(cf.ByName(titolo)));
        }, $"voce di menu colonna '{titolo}'");
        voce.Click();
    }

    /// <summary>Apre la tendina multi-selezione del filtro componente.</summary>
    public void ApriFiltroEcu() => ApriFiltro("Componente");

    public string TestoFiltroEcu() => TestoFiltro("Componente");

    /// <summary>Apre l'editor di una nuova Change Request (stessa finestra, nessuna finestra separata).</summary>
    public ChangeRequestEditorPage NuovaChangeRequest()
    {
        ElementFinders.WaitButtonById(_scope, "Anomalie_NuovaChangeRequest_Button").SafeInvoke();
        return new ChangeRequestEditorPage(_scope);
    }

    /// <summary>Apre l'editor di una nuova segnalazione dalla rete assistenziale (stessa finestra).</summary>
    public SegnalazioneReteEditorPage NuovaSegnalazioneRete()
    {
        ElementFinders.WaitButtonById(_scope, "Anomalie_NuovaSegnalazioneRete_Button").SafeInvoke();
        return new SegnalazioneReteEditorPage(_scope);
    }
}
