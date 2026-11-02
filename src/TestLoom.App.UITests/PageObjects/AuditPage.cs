using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Pagina Audit (Piano 6.3.1 §1, seconda consegna — FlaUI obbligatorio per la propagazione
/// esplicita). Gli AutomationId delle celle della matrice sono dinamici (dipendono dai dati: Componente e
/// RequisitoId, vedi AuditView.axaml.cs) — questa page object li ricostruisce dai valori che il test stesso
/// ha seminato, non prova mai a indovinarli. Il dialogo "!"/valutazione salvata è un Avalonia Flyout, che
/// FlaUI vede come una finestra di primo livello separata: <see cref="TestLoomApplication.WaitForWindowByContent"/>
/// la trova cercando un AutomationId noto al suo interno, non per titolo (i Flyout qui non hanno un titolo
/// stabile).</summary>
public sealed class AuditPage
{
    private readonly Window _window;
    private readonly AutomationElement _root;
    private readonly TestLoomApplication _app;

    public AuditPage(Window window, AutomationElement root, TestLoomApplication app)
    {
        _window = window;
        _root = root;
        _app = app;
    }

    public static bool IsShown(AutomationElement root) =>
        ElementFinders.ExistsById(root, "Audit_Sottoveicolo_ComboBox") || ElementFinders.ExistsById(root, "Audit_Sottoveicolo_Scegli_Button");

    public void SelezionaVeicolo(string nome)
    {
        // La pagina Audit carica SottoveicoliDisponibili in modo asincrono (MasterLetturaScope, Aggiorna()):
        // subito dopo la navigazione ne' la ComboBox ne' il bottone di fallback sono ancora nell'albero UIA.
        // Un TryFindById "a scatto" qui puo' fotografare questa finestra vuota, decidere (a torto) il ramo
        // "bottone" e restare bloccato ad aspettarlo per 15s anche quando la Combo appare un istante dopo
        // (sotto soglia, come nei workspace di questa suite) — bisogna aspettare che ALMENO UNO dei due
        // controlli compaia prima di scegliere il ramo.
        Wait.ForCondition(() => IsShown(_root), "matrice Audit caricata (Sottoveicolo ComboBox o bottone Scegli)");

        var combo = ElementFinders.TryFindById(_root, "Audit_Sottoveicolo_ComboBox");
        if (combo != null) { combo.AsComboBox().SelectByText(nome); return; }

        // Sopra la soglia di 10 elementi la matrice usa il bottone + dialog di ricerca invece della ComboBox
        // (stesso schema del campo Veicolo dell'editor Test) — non necessario per i workspace di test UI
        // (pochi veicoli seminati), ma copre comunque il fallback per coerenza con AuditView.axaml.
        ElementFinders.WaitButtonById(_root, "Audit_Sottoveicolo_Scegli_Button").SafeInvoke();
        var dialog = _app.WaitForWindowByContent("SelezionaDialog_Ricerca_TextBox");
        ElementFinders.WaitTextBoxById(dialog, "SelezionaDialog_Ricerca_TextBox").SetText(nome);
        var voce = Wait.ForElement(() => dialog.FindFirstDescendant(cf => cf.ByName(nome)), $"voce '{nome}' nel dialog di ricerca veicolo");
        voce.SafeInvoke();
    }

    private static string CellaPropagabileId(string componente, string requisitoId) => $"Audit_Propagabile_{componente}_{requisitoId}_Button";
    private static string CellaValutazioneSalvataId(string componente, string requisitoId) => $"Audit_ValutazioneSalvata_{componente}_{requisitoId}_Button";

    public bool CellaMostraPropagabile(string componente, string requisitoId) =>
        ElementFinders.ExistsById(_root, CellaPropagabileId(componente, requisitoId));

    public bool CellaMostraValutazioneSalvata(string componente, string requisitoId) =>
        ElementFinders.ExistsById(_root, CellaValutazioneSalvataId(componente, requisitoId));

    public AutomationElement AttendiCellaPropagabile(string componente, string requisitoId) =>
        ElementFinders.WaitById(_root, CellaPropagabileId(componente, requisitoId));

    public AutomationElement AttendiCellaValutazioneSalvata(string componente, string requisitoId) =>
        ElementFinders.WaitById(_root, CellaValutazioneSalvataId(componente, requisitoId));

    /// <summary>Apre il dialogo "Propagare?" cliccando la cella "!" e restituisce la finestra del Flyout
    /// (trovata per contenuto — vedi doc di classe).</summary>
    public Window ApriDialogoPropaga(string componente, string requisitoId)
    {
        AttendiCellaPropagabile(componente, requisitoId).AsButton().SafeInvoke();
        return _app.WaitForWindowByContent("Audit_Propaga_Conferma_Button");
    }

    /// <summary>Conferma la propagazione ai soli veicoli indicati (checkbox già preselezionato per il
    /// veicolo cliccato, gli altri restano deselezionati se non elencati qui — vera selezione di un
    /// SOTTOINSIEME, non "tutti/nessuno" per forza) con la nota indicata, poi attende che il dialog si
    /// chiuda.</summary>
    public void ConfermaPropagazione(Window dialogo, IReadOnlyList<string> veicoliDaSelezionare, string nota)
    {
        foreach (var veicolo in veicoliDaSelezionare)
        {
            var check = ElementFinders.WaitById(dialogo, $"Audit_Propaga_Veicolo_{veicolo}_CheckBox").AsCheckBox();
            if (check.IsChecked != true) check.IsChecked = true;
        }
        ElementFinders.WaitTextBoxById(dialogo, "Audit_Propaga_Nota_TextBox").SetText(nota);
        ElementFinders.WaitButtonById(dialogo, "Audit_Propaga_Conferma_Button").SafeInvoke();
        Wait.ForCondition(() => _app.IsWindowClosedByContent("Audit_Propaga_Conferma_Button"), "chiusura dialogo Propaga");
    }

    public void AnnullaDialogo(Window dialogo, string automationIdBottoneAnnulla)
    {
        ElementFinders.WaitButtonById(dialogo, automationIdBottoneAnnulla).SafeInvoke();
        Wait.ForCondition(() => _app.IsWindowClosedByContent(automationIdBottoneAnnulla), "chiusura dialogo");
    }

    // --- Piano 6.4.0: Propagazione massiva (per componente o per intero veicolo) ---

    private static string PropagaEcuId(string componente) => $"Audit_PropagaEcu_Button_{componente}";

    public bool PulsantePropagaEcuVisibile(string componente) => ElementFinders.ExistsById(_root, PropagaEcuId(componente));

    public bool PulsantePropagaVeicoloVisibile() => ElementFinders.ExistsById(_root, "Audit_PropagaVeicolo_Button");

    /// <summary>Apre il dialogo di anteprima/conferma della Propagazione massiva per UNA sola componente (pulsante
    /// nell'intestazione di colonna, Piano 6.4.0, punto 1).</summary>
    public Window ApriDialogoPropagaMassivaEcu(string componente)
    {
        ElementFinders.WaitButtonById(_root, PropagaEcuId(componente)).SafeInvoke();
        return _app.WaitForWindowByContent("Audit_PropagaMassiva_Conferma_Button");
    }

    /// <summary>Apre il dialogo di anteprima/conferma della Propagazione massiva per Tutti i componenti del veicolo
    /// selezionato (pulsante nell'intestazione della card matrice, Piano 6.4.0, punto 1).</summary>
    public Window ApriDialogoPropagaMassivaVeicolo()
    {
        ElementFinders.WaitButtonById(_root, "Audit_PropagaVeicolo_Button").SafeInvoke();
        return _app.WaitForWindowByContent("Audit_PropagaMassiva_Conferma_Button");
    }

    /// <summary>Compila la nota obbligatoria e conferma la Propagazione massiva, poi attende la chiusura del
    /// dialogo (Piano 6.4.0, punto 4: il pulsante di conferma resta disabilitato finché la nota è vuota).</summary>
    public void ConfermaPropagazioneMassiva(Window dialogo, string nota)
    {
        ElementFinders.WaitTextBoxById(dialogo, "Audit_PropagaMassiva_Nota_TextBox").SetText(nota);
        ElementFinders.WaitButtonById(dialogo, "Audit_PropagaMassiva_Conferma_Button").SafeInvoke();
        Wait.ForCondition(() => _app.IsWindowClosedByContent("Audit_PropagaMassiva_Conferma_Button"), "chiusura dialogo Propagazione massiva");
    }

    public void AnnullaPropagazioneMassiva(Window dialogo)
    {
        ElementFinders.WaitButtonById(dialogo, "Audit_PropagaMassiva_Annulla_Button").SafeInvoke();
        Wait.ForCondition(() => _app.IsWindowClosedByContent("Audit_PropagaMassiva_Annulla_Button"), "chiusura dialogo Propagazione massiva (Annulla)");
    }

    // --- Esportazione matrice Excel (dialog di sistema) e filtro per tipo di requisito ---

    /// <summary>Clic su "Esporta Excel" della matrice: restituisce il dialog di sistema di salvataggio.</summary>
    public NativeFileDialogPage EsportaMatriceExcel()
    {
        ElementFinders.WaitButtonById(_root, "Audit_EsportaExcel_Button").SafeInvoke();
        // Dialogo unico di export: si conferma con le impostazioni di partenza, poi compare il Salva con nome.
        var opzioni = _app.WaitForWindowByContent("EsportaOpzioni_Esporta");
        ElementFinders.WaitButtonById(opzioni, "EsportaOpzioni_Esporta").SafeInvoke();
        return NativeFileDialogPage.Attendi(_app);
    }

    /// <summary>Apre il flyout del filtro "Tipo requisito" (Standard, Extra) e restituisce il contenuto.</summary>
    public FiltroMultiSelectPage ApriFiltroTipoRequisito()
    {
        ElementFinders.WaitButtonById(_root, "Audit_FiltroTipoRequisito_Button").SafeInvoke();
        return new FiltroMultiSelectPage(_app);
    }

    /// <summary>Testo riepilogo del filtro tipo requisito ("Tutti" oppure i tipi scelti).</summary>
    public string TestoFiltroTipoRequisito => ElementFinders.WaitById(_root, "Audit_FiltroTipoRequisito_Button").Name ?? string.Empty;

    /// <summary>Titolo della sezione Standard della matrice ("Standard · N", con N i requisiti mostrati), o null se la
    /// sezione non è nell'albero UIA (AuditViewModel.MostraSezioneStandard è falso, es. filtro su Extra). Le celle della
    /// matrice senza valutazione manuale/propagata non hanno AutomationId, quindi il titolo è l'ancora stabile.</summary>
    public string? TitoloSezioneStandard => ElementFinders.TryFindById(_root, "Audit_Sezione_Standard_Text")?.Name;

    /// <summary>Azzera il filtro tipo requisito (pulsante accanto al filtro).</summary>
    public void AzzeraFiltroTipoRequisito() =>
        ElementFinders.WaitButtonById(_root, "Audit_FiltroTipoRequisito_Azzera_Button").SafeInvoke();

    public string? TooltipCella(string componente, string requisitoId, bool valutazioneSalvata)
    {
        var id = valutazioneSalvata ? CellaValutazioneSalvataId(componente, requisitoId) : CellaPropagabileId(componente, requisitoId);
        var cella = ElementFinders.WaitById(_root, id);
        // Avalonia mappa ToolTip.Tip sull'HelpText della automation peer: nessun altro punto della suite lo
        // verifica ancora, quindi qui c'è il primo precedente — se HelpText restasse vuoto in qualche build
        // futura di Avalonia, il fallback su Name copre comunque la marcatura minima già coperta da unit test.
        var help = cella.Properties.HelpText.ValueOrDefault;
        return string.IsNullOrEmpty(help) ? cella.Name : help;
    }
}
