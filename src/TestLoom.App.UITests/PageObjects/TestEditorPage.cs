using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Editor Test Case (TestCaseEditorView), incorporato nella stessa MainWindow (mai una finestra
/// separata per il flusso normale — TestCaseEditWindow è usato solo dal viewer di un test correlato in
/// finestra dedicata, fuori scope di questa suite iniziale). Un test NUOVO parte dal passo 1 del wizard
/// (scelta del Tipo test, "Editor_TemplatePicker_Button"); un test ESISTENTE mostra già "Editor_Title_Text".
/// Cerca sempre a partire dalla finestra (mai da un contenitore intermedio, vedi MainShellPage.PageHost):
/// Avalonia non espone il ContentControl che ospita la pagina come AutomationElement via UI Automation, ma i
/// singoli marcatori di ogni pagina restano comunque raggiungibili direttamente dalla finestra.</summary>
public sealed class TestEditorPage
{
    private readonly Window _window;

    public TestEditorPage(Window window)
    {
        _window = window;
        Wait.ForCondition(
            () => ElementFinders.ExistsById(window, "Editor_TemplatePicker_Button") || ElementFinders.ExistsById(window, "Editor_Title_Text"),
            "editor Test Case caricato");
    }

    /// <summary>Passo 1 del wizard per un test nuovo: clicca il template unico seminato dal workspace di test
    /// (vedi TestWorkspace.DefaultTemplateName) — con un solo template a catalogo non c'è ambiguità sull'unico
    /// bottone che condivide l'AutomationId "Editor_TemplatePicker_Button".</summary>
    public void ChooseTemplate() =>
        ElementFinders.WaitButtonById(_window, "Editor_TemplatePicker_Button").SafeInvoke();

    /// <summary>Solo per un test NUOVO: la striscia di schede (MostraSchedeCreazione) esiste esclusivamente
    /// durante la creazione. Un test ESISTENTE mostra tutte le sezioni insieme fin da subito — vedi
    /// <see cref="EnterEditMode"/> per l'equivalente "sblocca i campi" di un test già salvato.</summary>
    public void GoToTab(int index)
    {
        // Legacy tab flows explicitly use Full; new Test defaults to the Quick editor.
        SwitchToFullMode();
        ElementFinders.WaitButtonById(_window, $"Editor_Tab_{index}_Button").SafeInvoke();
    }

    public void SwitchToQuickMode()
    {
        if (ElementFinders.ExistsById(_window, "Editor_Mode_Quick_Button"))
            ElementFinders.WaitButtonById(_window, "Editor_Mode_Quick_Button").SafeInvoke();
    }

    public void SwitchToFullMode()
    {
        if (ElementFinders.ExistsById(_window, "Editor_Mode_Full_Button"))
            ElementFinders.WaitButtonById(_window, "Editor_Mode_Full_Button").SafeInvoke();
    }

    /// <summary>Solo per un test ESISTENTE: appena aperto è in sola lettura (InModifica=false) finché non si
    /// preme "Modifica" — un test nuovo è invece già "in costruzione" (InModifica=true dalla creazione) e non
    /// mostra questo pulsante.</summary>
    public void EnterEditMode() =>
        ElementFinders.WaitButtonById(_window, "Editor_EditMode_Button").SafeInvoke();

    /// <summary>Campo "Descrizione" (dettagliata, obbligatorio nel tipo UnitLogger di default): non e' la Descrizione breve.
    /// Ha un Id dedicato (Editor_Descrizione_TextBox) perche' e' l'unico controllo di testo della scheda senza Id.</summary>
    public void SetDescrizione(string testo) =>
        ElementFinders.WaitTextBoxById(_window, "Editor_Descrizione_TextBox").SetText(testo);

    public void SetDescrizioneBreve(string testo) =>
        ElementFinders.WaitTextBoxById(_window, "Editor_DescrizioneBreve_TextBox").SetText(testo);

    /// <summary>Apre il dialog "Scegli componente", seleziona la voce indicata e conferma — la selezione del componente
    /// passa sempre per questo dialog (nessuna ComboBox alternativa nel form, vedi TestCaseEditorView.axaml),
    /// indipendentemente da quante componente esistono a catalogo.</summary>
    public void ChooseEcu(TestLoomApplication app, string ecuName)
    {
        ElementFinders.WaitButtonById(_window, "Editor_ScegliEcu_Button").SafeInvoke();
        var dialog = new SelectDialogPage(app.WaitForWindowByContent("SelectDialog_Items_ListBox"));
        dialog.SelectItem(ecuName);
        dialog.Confirm();
        Wait.ForCondition(() => app.IsWindowClosedByContent("SelectDialog_Items_ListBox"), "chiusura dialog 'Scegli componente'");
    }

    /// <summary>Aggiunta rapida al catalogo + al test (riga "Altra componente non in elenco...").</summary>
    public void AddEcuRapido(string nome)
    {
        ElementFinders.WaitTextBoxById(_window, "Editor_NuovoEcu_TextBox").SetText(nome);
        ElementFinders.WaitButtonById(_window, "Editor_AggiungiEcuRapido_Button").SafeInvoke();
    }

    public void AddVeicoloRapido(string nome)
    {
        ElementFinders.WaitTextBoxById(_window, "Editor_NuovoVeicolo_TextBox").SetText(nome);
        ElementFinders.WaitButtonById(_window, "Editor_AggiungiVeicoloRapido_Button").SafeInvoke();
    }

    /// <summary>Aggiunta rapida di una Funzionalita' (riga "Altra funzionalita' non in elenco...").</summary>
    public void AddFunzionalitaRapida(string nome)
    {
        ElementFinders.WaitTextBoxById(_window, "Editor_NuovaFunzionalita_TextBox").SetText(nome);
        ElementFinders.WaitButtonById(_window, "Editor_AggiungiFunzionalitaRapida_Button").SafeInvoke();
    }

    /// <summary>Vero se la voce compare come testo nella finestra (badge della funzionalita' scelta).</summary>
    public bool TestoVisibile(string testo) =>
        _window.FindFirstDescendant(cf => cf.ByName(testo)) != null;

    public bool HasValidationErrors => ElementFinders.ExistsById(_window, "Editor_ValidationErrors_List") &&
        ElementFinders.WaitById(_window, "Editor_ValidationErrors_List").FindAllChildren().Length > 0;

    public bool CanSave => ElementFinders.ExistsById(_window, "Editor_Save_Button");

    public bool SaveEnabled => ElementFinders.WaitButtonById(_window, "Editor_Save_Button").IsEnabled;

    public bool CancelEnabled => ElementFinders.WaitButtonById(_window, "Editor_Cancel_Button").IsEnabled;

    public void Save() =>
        ElementFinders.WaitButtonById(_window, "Editor_Save_Button").SafeInvoke();

    public void Cancel() =>
        ElementFinders.WaitButtonById(_window, "Editor_Cancel_Button").SafeInvoke();

    public string Title => ElementFinders.WaitById(_window, "Editor_Title_Text").AsLabel().Text;

    // --- Reiterazioni/Condizioni (scheda "Reiterazioni", Editor_Tab_2_Button) ---

    /// <summary>Crea la prima revisione/reiterazione (AggiungiIterazioneCommand) — condizione unica presente
    /// finché non si preme "+ Aggiungi condizione" (<see cref="AggiungiCondizione"/>).</summary>
    public void AvviaPrimaEsecuzione() =>
        ElementFinders.WaitButtonById(_window, "Editor_AvviaPrimaEsecuzione_Button").SafeInvoke();

    public void AggiungiCondizione() =>
        ElementFinders.WaitButtonById(_window, "Editor_AggiungiCondizione_Button").SafeInvoke();

    /// <summary>
    /// Post-6.5.3: non e' Ripeti (che conserva OperazioneOrigineUid). L'azione
    /// crea una nuova operazione autonoma, con soli dati riutilizzabili.
    /// </summary>
    public void CopiaDallaPrecedente()
    {
        ApriAnteprimaCopia();
        ConfermaCopia();
    }

    /// <summary>Apre soltanto l'anteprima dei campi riutilizzabili; nessuna operazione è ancora aggiunta.</summary>
    public void ApriAnteprimaCopia() =>
        ElementFinders.WaitButtonById(_window, "Editor_CopiaDallaPrecedente_Button").SafeInvoke();

    public void ConfermaCopia() =>
        ElementFinders.WaitButtonById(_window, "Editor_ConfermaCopiaPrecedente_Button").SafeInvoke();

    public void AnnullaAnteprimaCopia() =>
        ElementFinders.WaitButtonById(_window, "Editor_AnnullaAnteprimaCopiaPrecedente_Button").SafeInvoke();

    /// <summary>Undo locale della copia appena aggiunta (solo se non salvata né modificata).</summary>
    public void AnnullaCopia() =>
        ElementFinders.WaitButtonById(_window, "Editor_AnnullaCopiaPrecedente_Button").SafeInvoke();

    public bool AnteprimaCopiaVisibile =>
        ElementFinders.ExistsById(_window, "Editor_ConfermaCopiaPrecedente_Button");

    public bool HaCopiaDallaPrecedente =>
        ElementFinders.ExistsById(_window, "Editor_CopiaDallaPrecedente_Button");

    public string GetUidCondizione(int indice) => UidCondizione(indice);

    public void SetNotaCondizione(int indice, string nota) =>
        ElementFinders.WaitTextBoxById(_window,
            $"Editor_Condizione_{UidCondizione(indice)}_Nota_TextBox").SetText(nota);

    public string GetNotaCondizione(int indice) =>
        ElementFinders.WaitTextBoxById(_window,
            $"Editor_Condizione_{UidCondizione(indice)}_Nota_TextBox").Text;

    /// <summary>Con elementi templati Avalonia non valorizza SelectedItem.Text: si ripiega su Name dell'elemento
    /// selezionato e infine sul testo mostrato dal box chiuso della ComboBox.</summary>
    public string EsitoCondizioneTesto(int indice)
    {
        var combo = CondizioneEsitoCombos()[indice].AsComboBox();
        var selected = combo.SelectedItem;
        var candidates = new[]
        {
            selected?.Text, selected?.Name,
            selected?.FindAllDescendants().Select(d => d.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
            combo.FindAllDescendants().Select(d => d.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";
    }


    // L'AutomationId di ogni controllo di riga e' parametrizzato per Condizione.Uid (schema
    // "Editor_Condizione_{Uid}_<Controllo>", vedi TestCaseEditorView.axaml): con piu' condizioni sullo schermo
    // gli Esito/Ripeti/Rimuovi/MotivazioneNA di righe diverse non condividono piu' lo stesso id (review 6.4.0-
    // flaui-copertura, finding MEDIO). Qui si individuano le righe per POSIZIONE (indice 0-based, stesso
    // ordine di CondizioniVisual) leggendo l'Uid dalla ComboBox Esito di quella posizione, poi lo si riusa per
    // costruire l'id esatto degli altri controlli della stessa riga.
    private const string CondizionePrefix = "Editor_Condizione_";
    private const string EsitoComboSuffix = "_Esito_Combo";

    private IReadOnlyList<AutomationElement> CondizioneEsitoCombos() =>
        _window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ComboBox))
            .Where(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith(CondizionePrefix, StringComparison.Ordinal)
                        && (e.Properties.AutomationId.ValueOrDefault ?? "").EndsWith(EsitoComboSuffix, StringComparison.Ordinal))
            .ToList();

    /// <summary>Uid delle operazioni mostrate come righe compatte della Rapida (stesso ordine di CondizioniVisual), riconosciute dal
    /// pulsante OK di ogni riga. Vuoto in Completa, dove ogni operazione ha la scheda intera con il selettore dell'esito.</summary>
    public IReadOnlyList<string> UidRigheRapide() =>
        _window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .Select(e => e.Properties.AutomationId.ValueOrDefault ?? "")
            .Where(id => id.StartsWith(CondizionePrefix, StringComparison.Ordinal) && id.EndsWith("_QuickOk_Button", StringComparison.Ordinal))
            .Select(id => id[CondizionePrefix.Length..^"_QuickOk_Button".Length])
            .ToList();

    private string UidCondizione(int indice)
    {
        var righe = UidRigheRapide();
        if (righe.Count > 0)
        {
            if (indice < 0 || indice >= righe.Count)
                throw new InvalidOperationException($"Nessuna riga rapida all'indice {indice}: {righe.Count} righe visibili.");
            return righe[indice];
        }
        var combos = CondizioneEsitoCombos();
        if (indice < 0 || indice >= combos.Count)
            throw new InvalidOperationException($"Nessuna condizione all'indice {indice}: {combos.Count} condizioni visibili.");
        var id = combos[indice].Properties.AutomationId.ValueOrDefault ?? "";
        return id[CondizionePrefix.Length..^EsitoComboSuffix.Length];
    }

    /// <summary>Imposta l'esito della condizione all'indice indicato (0-based, stesso ordine di
    /// CondizioniVisual). Selezione per testo esatto della label italiana (EsitoLabelConverter):
    /// "Da eseguire", "Conforme", "Non Conforme", "Conforme con deviazioni", "Non applicabile".</summary>
    public void SetEsitoCondizione(int indice, string etichettaEsito) =>
        CondizioneEsitoCombos()[indice].AsComboBox().SelectByText(etichettaEsito);

    public bool EsitoCondizioneAbilitato(int indice) => CondizioneEsitoCombos()[indice].IsEnabled;

    public bool HaMotivazioneNonApplicabileVisibile(int indice) =>
        ElementFinders.ExistsById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_MotivazioneNA_TextBox");

    public void SetMotivazioneNonApplicabile(int indice, string testo) =>
        ElementFinders.WaitTextBoxById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_MotivazioneNA_TextBox").SetText(testo);

    /// <summary>Campo "Problema o risultato osservato", obbligatorio quando l'esito e' Non Conforme o Conforme
    /// con deviazioni (RichiedeDescrizioneProblema): senza compilarlo Salva() rifiuta il salvataggio, esattamente
    /// come farebbe un utente reale davanti al campo evidenziato.</summary>
    public void SetDescrizioneProblema(int indice, string testo) =>
        ElementFinders.WaitTextBoxById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_DescrizioneProblema_TextBox").SetText(testo);

    public bool HaDescrizioneDeviazioneVisibile(int indice) =>
        ElementFinders.ExistsById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_DescrizioneDeviazione_TextBox");

    public void SetDescrizioneDeviazione(int indice, string testo) =>
        ElementFinders.WaitTextBoxById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_DescrizioneDeviazione_TextBox").SetText(testo);

    public void RipetiCondizione(int indice) =>
        ElementFinders.WaitButtonById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_Ripeti_Button").SafeInvoke();

    public void RimuoviCondizione(int indice) =>
        ElementFinders.WaitButtonById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_Rimuovi_Button").SafeInvoke();

    /// <summary>Vero se la riga operazione all'indice mostra il campo durata (TIME, 6.6.1): compare per ogni operazione.</summary>
    public bool HaDurataOperazione(int indice) =>
        ElementFinders.ExistsById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_Durata_TextBox");
    // --- Durata operazione (TIME, 6.6.1): campo minuti "Editor_Condizione_{Uid}_Durata_TextBox" e pulsanti -5/+5 ---

    public void SetDurataCondizione(int indice, string testo) =>
        ElementFinders.WaitTextBoxById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_Durata_TextBox").SetText(testo);

    public string DurataCondizioneTesto(int indice) =>
        ElementFinders.WaitTextBoxById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_Durata_TextBox").Text;

    /// <summary>Vero quando il campo durata mostra il messaggio "durata non valida" (il testo non cambia il modello).</summary>
    public bool HaErroreDurata(int indice) =>
        ElementFinders.ExistsById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_Durata_Errore");

    public void ClickDurataPiu5(int indice) =>
        ElementFinders.WaitButtonById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_DurataPiu5_Button").SafeInvoke();

    public bool HaImportLogUdt(int indice, bool principale) =>
        ElementFinders.ExistsById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_ImportaLog{(principale ? "Main" : "Wu")}_Button");

    public int NumeroCondizioni() => Math.Max(CondizioneEsitoCombos().Count, UidRigheRapide().Count);

    // --- Campi personalizzati per Tipo (Editor_Campo_{Nome}, vedi TestCaseEditorView.axaml risorse
    // CampoValoreCampo*) ---

    public void SetCampoTesto(string nomeCampo, string valore) =>
        ElementFinders.WaitTextBoxById(_window, $"Editor_Campo_{nomeCampo}").SetText(valore);

    public void SetCampoNumero(string nomeCampo, string valore) => SetCampoTesto(nomeCampo, valore);

    public void SetCampoLista(string nomeCampo, string valore) =>
        ElementFinders.WaitComboBoxById(_window, $"Editor_Campo_{nomeCampo}").SelectByText(valore);

    /// <summary>SafeInvoke() (ElementActions) prova prima l'InvokePattern: la CheckBox di Avalonia lo espone
    /// insieme al TogglePattern, quindi un SafeInvoke() generico qui invoca il pattern SBAGLIATO (Invoke, non
    /// Toggle) — nessuna eccezione, ma lo stato ToggleState non cambia. Un singolo Toggle() esplicito non
    /// basta se il valore di partenza e' Indeterminate (campo bool? non ancora impostato, checkbox a tre
    /// stati: Indeterminate -> Off -> On, non Off -> On come per un bool semplice) — si ripete finche' non si
    /// arriva davvero a On, con un limite per non restare in loop su un controllo comunque rotto.</summary>
    public void ToggleCampoCheckbox(string nomeCampo)
    {
        var checkbox = ElementFinders.WaitById(_window, $"Editor_Campo_{nomeCampo}").AsCheckBox();
        for (var i = 0; i < 3 && checkbox.ToggleState != FlaUI.Core.Definitions.ToggleState.On; i++)
            checkbox.Toggle();
    }

    public bool CampoCheckboxAttivo(string nomeCampo) =>
        ElementFinders.WaitById(_window, $"Editor_Campo_{nomeCampo}").AsToggleButton().ToggleState == FlaUI.Core.Definitions.ToggleState.On;

    public bool HaCampo(string nomeCampo) => ElementFinders.ExistsById(_window, $"Editor_Campo_{nomeCampo}");

    /// <summary>Un campo di Tipo File non ha un contenitore con id semplice: solo il bottone "Scegli
    /// file..." (Editor_Campo_{Nome}_ScegliFile, vedi TestCaseEditorView.axaml) apre il FilePicker di sistema,
    /// non interagito da questa suite.</summary>
    public bool HaCampoFile(string nomeCampo) => ElementFinders.ExistsById(_window, $"Editor_Campo_{nomeCampo}_ScegliFile");

    /// <summary>Seleziona/deseleziona uno strumento nella lista "Strumenti usati" (caso inline, <=10 strumenti
    /// a catalogo — vedi Editor_Strumento_{Nome}). Necessario prima che i campi personalizzati di quello
    /// strumento (Editor_Campo_{Nome campo}) compaiano dentro ogni condizione.</summary>
    public void SelezionaStrumento(string nomeStrumento) =>
        ElementFinders.WaitById(_window, $"Editor_Strumento_{nomeStrumento}").SafeInvoke();

    public bool StrumentoSelezionato(string nomeStrumento) =>
        ElementFinders.WaitById(_window, $"Editor_Strumento_{nomeStrumento}").AsCheckBox().ToggleState == FlaUI.Core.Definitions.ToggleState.On;

    // --- Strumenti usati, caso >10 a catalogo: lista virtualizzata con ricerca (Editor_Strumenti_Ricerca_Input/
    // Editor_Strumenti_Lista, aggiunti dal ramo perf/6.4.0-editor-profilo). Ogni ListBoxItem ha un
    // AutomationId per Nome strumento (Editor_StrumentoLista_{Nome}, Style su ListBoxItem: un AutomationId
    // impostato dentro l'ItemTemplate non e' raggiungibile via UIA, vedi README). ---

    public bool MostraListaStrumentiVirtualizzata => ElementFinders.ExistsById(_window, "Editor_Strumenti_Lista");

    public void CercaStrumento(string testo) =>
        ElementFinders.WaitTextBoxById(_window, "Editor_Strumenti_Ricerca_Input").SetText(testo);

    /// <summary>Seleziona/deseleziona uno strumento nella lista virtualizzata (>10 a catalogo): il CheckBox
    /// riempie l'intera riga (HorizontalAlignment="Stretch"), quindi basta invocarlo dentro il ListBoxItem
    /// trovato per Nome, senza dipendere dalla selezione di riga (che il code-behind deseleziona subito,
    /// OnListaStrumentiSelectionChanged).</summary>
    public void SelezionaStrumentoInLista(string nomeStrumento)
    {
        var riga = ElementFinders.WaitById(_window, $"Editor_StrumentoLista_{nomeStrumento}");
        var checkbox = riga.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox))
            ?? throw new InvalidOperationException($"Nessun CheckBox trovato nella riga strumento '{nomeStrumento}'.");
        checkbox.SafeInvoke();
    }

    public bool StrumentoVisibileInLista(string nomeStrumento) =>
        ElementFinders.ExistsById(_window, $"Editor_StrumentoLista_{nomeStrumento}");

    public string StrumentiSelezionatiRiepilogo =>
        ElementFinders.WaitById(_window, "Editor_StrumentiSelezionatiRiepilogo_Text").Name ?? string.Empty;

    // --- Menu REV (naviga tra le revisioni gia' create, Editor_MenuRev_Combo) ---

    public bool HaMenuRevisioni => ElementFinders.ExistsById(_window, "Editor_MenuRev_Combo");

    public int NumeroRevisioniInMenu() =>
        ElementFinders.WaitComboBoxById(_window, "Editor_MenuRev_Combo").Items.Length;

    /// <summary>Seleziona la revisione con quel numero di iterazione (etichetta "Rev. {iterazione} · ...",
    /// vedi EsecuzioneRevLabelConverter — il resto dell'etichetta include data/operatore/esito, non
    /// riproducibile esattamente da un test, quindi si cerca per prefisso invece che per testo esatto come fa
    /// ElementActions.SelectByText).</summary>
    public void SelezionaRevisione(int iterazione)
    {
        var combo = ElementFinders.WaitComboBoxById(_window, "Editor_MenuRev_Combo");
        combo.Expand();
        var prefisso = $"Rev. {iterazione} ";
        Wait.ForCondition(() => combo.Items.Any(i => (i.Text ?? "").StartsWith(prefisso, StringComparison.Ordinal)),
            $"revisione '{prefisso}...' disponibile nel menu REV");
        combo.Items.First(i => (i.Text ?? "").StartsWith(prefisso, StringComparison.Ordinal)).Select();
        combo.Collapse();
    }

    /// <summary>Apre "Crea test collegato" (finestra modale reale, non un Flyout — vedi CreaTestCollegatoPage)
    /// e ne restituisce il Page Object.</summary>
    public CreaTestCollegatoPage CreaTestCollegato(TestLoomApplication app)
    {
        ElementFinders.WaitButtonById(_window, "Editor_CreaTestCollegato_Button").SafeInvoke();
        return new CreaTestCollegatoPage(app.WaitForWindowByContent("CreaTestCollegato_Crea_Button"));
    }

    public bool HaTestCollegato(string testCollegatoId) =>
        ElementFinders.ExistsById(_window, $"Editor_TestCollegato_{testCollegatoId}_Button");

    // --- Segnalazioni da classificare (pannello "Anomalie", creato automaticamente da una condizione con
    // esito Non Conforme/Conforme con deviazioni — Editor_Segnalazione_{Uid}_<Controllo>) ---

    public bool HaSegnalazioniDaClassificare => ElementFinders.ExistsById(_window, "Editor_SegnalazioniDaClassificare_Text");

    private const string SegnalazionePrefix = "Editor_Segnalazione_";
    private const string SeveritaSegnalazioneSuffix = "_Severita_Combo";

    private IReadOnlyList<AutomationElement> SegnalazioneSeveritaCombos() =>
        _window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ComboBox))
            .Where(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith(SegnalazionePrefix, StringComparison.Ordinal)
                        && (e.Properties.AutomationId.ValueOrDefault ?? "").EndsWith(SeveritaSegnalazioneSuffix, StringComparison.Ordinal))
            .ToList();

    public int NumeroSegnalazioniDaClassificare() => SegnalazioneSeveritaCombos().Count;

    private string UidSegnalazione(int indice)
    {
        var combos = SegnalazioneSeveritaCombos();
        if (indice < 0 || indice >= combos.Count)
            throw new InvalidOperationException($"Nessuna segnalazione all'indice {indice}: {combos.Count} visibili.");
        var id = combos[indice].Properties.AutomationId.ValueOrDefault ?? "";
        return id[SegnalazionePrefix.Length..^SeveritaSegnalazioneSuffix.Length];
    }

    public void SetSeveritaSegnalazione(int indice, string etichettaSeverita) =>
        SegnalazioneSeveritaCombos()[indice].AsComboBox().SelectByText(etichettaSeverita);

    /// <summary>Testo del pulsante primario della segnalazione all'indice: "Crea NC" oppure "Collega a NC-xxxx"
    /// (feedback 2026-10-10: l'azione predefinita della riga dichiara cosa succedera').</summary>
    public string TestoPrimariaSegnalazione(int indice)
    {
        var pulsante = ElementFinders.WaitButtonById(_window, $"{SegnalazionePrefix}{UidSegnalazione(indice)}_Primaria_Button");
        if (!string.IsNullOrWhiteSpace(pulsante.Name)) return pulsante.Name;
        return string.Join(" ", pulsante.FindAllDescendants().Select(d => d.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
    }

    /// <summary>Un clic sul pulsante primario della segnalazione all'indice: crea la NC (con ConfirmDialog, non gestito
    /// qui: il chiamante lo conferma con ConfirmDialogPage) oppure collega l'anomalia suggerita (nessuna conferma).</summary>
    public void ClickPrimariaSegnalazione(int indice) =>
        ElementFinders.WaitButtonById(_window, $"{SegnalazionePrefix}{UidSegnalazione(indice)}_Primaria_Button").SafeInvoke();

    /// <summary>Compatibilita' con i test di creazione: il pulsante primario, quando non c'e' un'anomalia suggerita,
    /// e' "Crea NC" e apre il ConfirmDialog di conferma (Confirm_Yes_Button/Confirm_No_Button).</summary>
    public void CreaAnomaliaDaSegnalazione(int indice) => ClickPrimariaSegnalazione(indice);

    /// <summary>"Gestisci tutte" del pannello segnalazioni: applica a ogni segnalazione la sua azione predefinita dopo
    /// un'unica conferma (ConfirmDialog, gestito dal chiamante).</summary>
    public void GestisciTutteSegnalazioni() =>
        ElementFinders.WaitButtonById(_window, "Editor_GestisciTutteSegnalazioni_Button").SafeInvoke();

    // --- Copertura Test editor (segnalazioni collega/escludi, WU per operazione, allegati, archivia, template per nome) ---

    private const string SegnalazioneRigaPrefix = "Editor_Segnalazione_";

    /// <summary>Uid delle segnalazioni visibili nel pannello, nell'ordine di comparsa, incluse quelle escluse (la
    /// Severita' di una riga esclusa non e' nell'albero UIA, quindi non si puo' ricavare dal combo). Gli id sono
    /// "Editor_Segnalazione_{Uid}_&lt;Controllo&gt;" (vedi TestCaseEditorView.axaml).</summary>
    private IReadOnlyList<string> UidSegnalazioniVisibili()
    {
        var uids = new List<string>();
        foreach (var elemento in _window.FindAllDescendants())
        {
            var id = elemento.Properties.AutomationId.ValueOrDefault ?? "";
            if (!id.StartsWith(SegnalazioneRigaPrefix, StringComparison.Ordinal)) continue;
            var resto = id[SegnalazioneRigaPrefix.Length..];
            var separatore = resto.IndexOf('_');
            if (separatore <= 0) continue;
            var uid = resto[..separatore];
            if (!uids.Contains(uid)) uids.Add(uid);
        }
        return uids;
    }

    private string UidSegnalazioneAllIndice(int indice)
    {
        var uids = UidSegnalazioniVisibili();
        if (indice < 0 || indice >= uids.Count)
            throw new InvalidOperationException($"Nessuna segnalazione all'indice {indice}: {uids.Count} visibili.");
        return uids[indice];
    }

    /// <summary>Testo di una voce di ComboBox: il Text puo' essere vuoto con template complessi, si ripiega sui Name dei figli.</summary>
    private static string TestoVoce(string? testo, IEnumerable<string> nomiFigli) =>
        !string.IsNullOrEmpty(testo) ? testo : string.Join(" ", nomiFigli);

    /// <summary>Apre il menu "Altre azioni" della segnalazione se i suoi controlli non sono ancora nell'albero UIA: il
    /// menu e' chiuso di default (scelta di un'altra NC, creazione esplicita, esclusione con motivazione).</summary>
    private void AssicuraAltreAzioniAperte(string uid)
    {
        if (ElementFinders.ExistsById(_window, $"{SegnalazioneRigaPrefix}{uid}_MotivazioneEsclusione_TextBox")) return;
        ElementFinders.WaitById(_window, $"{SegnalazioneRigaPrefix}{uid}_AltreAzioni_Toggle").SafeInvoke();
        Wait.ForCondition(() => ElementFinders.ExistsById(_window, $"{SegnalazioneRigaPrefix}{uid}_MotivazioneEsclusione_TextBox"),
            "menu 'Altre azioni' della segnalazione aperto");
    }

    /// <summary>Testo del pulsante primario della segnalazione identificata dal suo Uid ("Crea NC" o "Collega a NC-xxxx").</summary>
    private string TestoPulsantePrimario(string uid)
    {
        var pulsante = ElementFinders.WaitById(_window, $"{SegnalazioneRigaPrefix}{uid}_Primaria_Button");
        if (!string.IsNullOrWhiteSpace(pulsante.Name)) return pulsante.Name;
        return string.Join(" ", pulsante.FindAllDescendants().Select(d => d.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
    }

    /// <summary>Segnalazione all'indice: sceglie un'anomalia aperta esistente (per prefisso dell'Id) nel combo
    /// "AnomaliaCombo" del menu "Altre azioni". Dopo la scelta il pulsante primario dichiara "Collega a NC-xxxx" e un
    /// clic lo applica (non c'e' un pulsante "Collega" separato). Il combo compare solo se le candidate sono entro soglia.</summary>
    public void CollegaSegnalazioneAdAnomalia(int indice, string idAnomalia)
    {
        var uid = UidSegnalazioneAllIndice(indice);
        AssicuraAltreAzioniAperte(uid);
        var combo = ElementFinders.WaitComboBoxById(_window, $"{SegnalazioneRigaPrefix}{uid}_AnomaliaCombo");
        combo.Expand();
        Wait.ForCondition(() => combo.Items.Any(i => TestoVoce(i.Text, i.FindAllDescendants().Select(d => d.Name)).StartsWith(idAnomalia, StringComparison.Ordinal)),
            $"anomalia aperta '{idAnomalia}' proposta per il collegamento");
        combo.Items.First(i => TestoVoce(i.Text, i.FindAllDescendants().Select(d => d.Name)).StartsWith(idAnomalia, StringComparison.Ordinal)).Select();
        combo.Collapse();
        Wait.ForCondition(() => TestoPulsantePrimario(uid).Contains(idAnomalia, StringComparison.Ordinal),
            $"pulsante primario 'Collega a {idAnomalia}' dopo la scelta");
        ElementFinders.WaitById(_window, $"{SegnalazioneRigaPrefix}{uid}_Primaria_Button").SafeInvoke();
    }

    /// <summary>Esclude la segnalazione all'indice con la motivazione indicata (vuota = errore di validazione). L'esclusione
    /// sta nel menu "Altre azioni", quindi il menu viene aperto se serve.</summary>
    public void EscludiSegnalazione(int indice, string motivazione)
    {
        var uid = UidSegnalazioneAllIndice(indice);
        AssicuraAltreAzioniAperte(uid);
        ElementFinders.WaitTextBoxById(_window, $"{SegnalazioneRigaPrefix}{uid}_MotivazioneEsclusione_TextBox").SetText(motivazione);
        ElementFinders.WaitButtonById(_window, $"{SegnalazioneRigaPrefix}{uid}_Escludi_Button").SafeInvoke();
    }

    public void AnnullaEsclusioneSegnalazione(int indice) =>
        ElementFinders.WaitButtonById(_window, $"{SegnalazioneRigaPrefix}{UidSegnalazioneAllIndice(indice)}_AnnullaEsclusione_Button").SafeInvoke();

    /// <summary>Vero se la segnalazione all'indice e' esclusa (compare il pulsante "Annulla esclusione").</summary>
    public bool SegnalazioneEsclusa(int indice) =>
        ElementFinders.ExistsById(_window, $"{SegnalazioneRigaPrefix}{UidSegnalazioneAllIndice(indice)}_AnnullaEsclusione_Button");

    /// <summary>Pulsante "Importa log WU" della riga operazione all'indice (visibile con lo strumento UnitLogger selezionato).</summary>
    public void ImportaLogWu(int indice) =>
        ElementFinders.WaitButtonById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_ImportaLogWu_Button").SafeInvoke();

    /// <summary>"Copia dalla precedente" della riga operazione all'indice (6.6.1): copia i campi strumento dell'operazione
    /// subito precedente. Apre l'anteprima con le spunte; la conferma usa <see cref="ConfermaCopia"/>.</summary>
    public void CopiaCampiDallaPrecedenteOperazione(int indice) =>
        ElementFinders.WaitButtonById(_window, $"{CondizionePrefix}{UidCondizione(indice)}_CopiaDallaPrecedente_Button").SafeInvoke();

    /// <summary>Testo del campo Testo "Editor_Campo_{nome}" dell'operazione all'indice (0-based). Gli id dei campi non
    /// sono per riga: si prende l'occorrenza di pari posizione, nello stesso ordine delle operazioni. Null se assente.</summary>
    public string? ValoreCampoTestoOperazione(int indice, string nomeCampo)
    {
        var occorrenze = _window.FindAllDescendants(cf => cf.ByAutomationId($"Editor_Campo_{nomeCampo}"));
        if (indice < 0 || indice >= occorrenze.Length) return null;
        return occorrenze[indice].AsTextBox().Text;
    }

    /// <summary>Pulsante "Scegli file..." della scheda Allegati (apre il picker nativo di Windows).</summary>
    public void SceglieFileAllegato() =>
        ElementFinders.WaitButtonById(_window, "Editor_Allegato_Scegli_Button").SafeInvoke();

    public bool ScegliFileAllegatoAbilitato =>
        ElementFinders.WaitById(_window, "Editor_Allegato_Scegli_Button").IsEnabled;

    /// <summary>Vero se nella scheda Allegati compare un allegato con quel nome (il nome e' il testo del pulsante "Apri").</summary>
    public bool HaAllegato(string nomeFile) =>
        _window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button).And(cf.ByName(nomeFile))) != null;

    /// <summary>Rimuove l'allegato con quel nome: l'id del pulsante "✕" e' "Editor_Allegato_{Uid}_Rimuovi_Button", il Uid
    /// si ricava da quello del pulsante "Apri" dello stesso allegato.</summary>
    public void RimuoviAllegato(string nomeFile)
    {
        const string prefisso = "Editor_Allegato_";
        const string suffissoApri = "_Apri_Button";
        var apri = _window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button).And(cf.ByName(nomeFile)))
            ?? throw new InvalidOperationException($"Allegato '{nomeFile}' non visibile nella scheda Allegati.");
        var id = apri.Properties.AutomationId.ValueOrDefault ?? "";
        if (!id.StartsWith(prefisso, StringComparison.Ordinal) || !id.EndsWith(suffissoApri, StringComparison.Ordinal))
            throw new InvalidOperationException($"AutomationId inatteso per l'allegato '{nomeFile}': '{id}'.");
        var uid = id[prefisso.Length..^suffissoApri.Length];
        ElementFinders.WaitButtonById(_window, $"{prefisso}{uid}_Rimuovi_Button").SafeInvoke();
    }

    public void Archivia() =>
        ElementFinders.WaitButtonById(_window, "Editor_Archivia_Button").SafeInvoke();

    /// <summary>Vero se "Archivia" e' presente E abilitato (un Viewer non deve poterlo usare).</summary>
    public bool ArchiviaAbilitato =>
        ElementFinders.TryFindById(_window, "Editor_Archivia_Button") is { IsEnabled: true };

    /// <summary>Vero se "Modifica" e' presente E abilitato (un Viewer resta in sola lettura).</summary>
    public bool EditModeAbilitato =>
        ElementFinders.TryFindById(_window, "Editor_EditMode_Button") is { IsEnabled: true };

    /// <summary>Wizard di creazione: sceglie il tipo test per nome. Tutti i pulsanti condividono l'AutomationId, quindi
    /// si individua quello che contiene il testo del template.</summary>
    public void ChooseTemplateByName(string nomeTemplate)
    {
        var bottone = Wait.Until(
            () => _window.FindAllDescendants(cf => cf.ByAutomationId("Editor_TemplatePicker_Button"))
                .FirstOrDefault(b => b.FindAllDescendants().Any(d => (d.Name ?? "").Contains(nomeTemplate, StringComparison.Ordinal))),
            b => b != null, null, $"tipo test '{nomeTemplate}' nel wizard")!;
        bottone.AsButton().SafeInvoke();
    }

    // --- Requisiti collegati al Test (badge "Editor_Requisito_{Id}_Rimuovi_Button", selettore SelezionaDialog) ---

    /// <summary>Aggiunge un requisito dal selettore "Aggiungi requisito": sceglie la voce il cui testo inizia con l'Id
    /// del requisito e conferma. Il chiamante verifica il badge o il JSON salvato.</summary>
    public void AggiungiRequisitoDaDialogo(TestLoomApplication app, string idRequisito)
    {
        // Il pulsante apre un menu di selezione multipla con una casella per requisito (non SelezionaDialog):
        // si spunta la casella il cui testo inizia con l'Id, poi si chiude il menu con Esc solo se e' ancora aperto.
        ElementFinders.WaitButtonById(_window, "Editor_AggiungiRequisito_Button").SafeInvoke();
        var casella = Wait.Until(
            () => _window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox))
                .FirstOrDefault(c => (c.Name ?? "").StartsWith(idRequisito, StringComparison.Ordinal)),
            c => c != null, null, $"requisito '{idRequisito}' nel menu di selezione")!;
        var checkBox = casella.AsCheckBox();
        if (checkBox.ToggleState != FlaUI.Core.Definitions.ToggleState.On) checkBox.Toggle();
        Wait.ForCondition(() => HaRequisitoCollegato(idRequisito), "requisito collegato compare come badge");

        var menuAperto = _window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox))
            .Any(c => (c.Name ?? "").StartsWith(idRequisito, StringComparison.Ordinal));
        if (menuAperto) ElementActions.PressKey(_window, FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
    }

    public bool HaRequisitoCollegato(string idRequisito) =>
        ElementFinders.ExistsById(_window, $"Editor_Requisito_{idRequisito}_Rimuovi_Button");

    public void RimuoviRequisito(string idRequisito) =>
        ElementFinders.WaitButtonById(_window, $"Editor_Requisito_{idRequisito}_Rimuovi_Button").SafeInvoke();

    /// <summary>Attende che il titolo rifletta il salvataggio appena avvenuto (da "Nuovo Test" all'Id
    /// assegnato) prima di continuare — utile subito dopo Save(), dove l'assegnazione dell'Id è asincrona
    /// rispetto al click.</summary>
    public TestEditorPage Refresh()
    {
        Wait.ForCondition(() => ElementFinders.ExistsById(_window, "Editor_Title_Text"), "editor Test Case dopo il refresh");
        return this;
    }
}
