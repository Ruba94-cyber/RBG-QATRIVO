# Copertura FlaUI — TestLoom 6.4.0

> **Addendum post-6.5.3 (PR #4):** questa matrice rimane lo storico di
> copertura 6.4.0. Per i 89 nuovi scenari FlaUI UIA3 di impostazioni,
> copia operazioni TestCase, navigazione, validazione CR/segnalazioni
> e permessi Viewer, vedere [`FLAUI_POST_653_MATRIX.md`](FLAUI_POST_653_MATRIX.md).
> Le nuove prove sono scritte ma non ancora eseguite su Windows; non
> considerarle superate fino a build, test e collaudo reali.


Inventario delle pagine/funzioni dell'app (da `src/TestLoom.App/Views`, `ViewModels`, `MainViewModel.NavItems`
e `ImpostazioniViewModel.SottoSezioni`) e stato della copertura FlaUI in `src/TestLoom.App.UITests`.

Priorità dei GAP: **P1** = scrive dati / cambia stato-workflow / permessi; **P2** = navigazione e filtri;
**P3** = cosmetico/ordine visuale.

Legenda stato: file:metodo (esistente) · **GAP** (nessun test) · **GAP (parziale)** (coperto solo in parte).

**Nota su questa iterazione (review + seguito):** mergiato `release/6.4.0` (porta gli AutomationId
`Editor_Strumenti_Ricerca_Input`/`Editor_Strumenti_Lista` per la lista Strumenti virtualizzata, merge pulito
senza conflitti). Corretto il finding medio della review: gli AutomationId di riga delle Condizioni
(`Editor_Condizione_Esito_Combo`/`Ripeti_Button`/`Rimuovi_Button`/`MotivazioneNA_TextBox`) erano condivisi tra
tutte le condizioni — ora parametrizzati per `Condizione.Uid`, individuati per indice/posizione da
`TestEditorPage`. Corrette anche due assunzioni sbagliate della versione precedente di questa matrice: "Crea
test collegato" non richiede alcuna configurazione di template (`HaAzioniCollegate` e' sempre vera), e la
classificazione di una segnalazione avviene nel pannello Anomalie DENTRO l'editor Test, non nella card
disattivata di NonConformitaView.

## Iterazione `wt/allegati-fix`: AutomationId nel code-behind e fix allegati (xUnit eseguiti, FlaUI NON ESEGUITI)

**AutomationId aggiunti (solo id, nessuna logica cambiata):**
- `CampiStrumentoEditorWindow.axaml.cs` (una riga per campo, `{i}` = indice visuale): `CampiStrumento_Riga_{i}_Nome_TextBox`,
  `_Tipo_ComboBox`, `_Gruppo_TextBox`, `_Opzioni_Button`, `_Unita_TextBox`, `_Ambito_ComboBox`, `_ValoreDefault_TextBox`,
  `_Su_Button`, `_Giu_Button`, `_Rimuovi_Button`; pulsante aggiungi: `CampiStrumento_Aggiungi_Button`.
- `TestTemplateEditorWindow.axaml.cs` → `BuildCampiPanel` (usato dai campi template e dai campi di sistema di
  `CampiTemplateEditorWindow`), per riga: `TplCampo_Riga_{i}_Tipo_ComboBox`, `_Sistema_ComboBox`, `_NomeLibero_TextBox`,
  `_TipoLibero_ComboBox`, `_Opzioni_Button`, `_Unita_TextBox`, `_Condizione_Button`, `_Obbligatorio_Button`, `_Rimuovi_Button`;
  su/giu dei campi liberi: `TplCampo_Riga_{i}_Su_Button` / `_Giu_Button`; su/giu dei campi di sistema con chiave restano
  invariati `TplCampoSu_{chiave}` / `TplCampoGiu_{chiave}` (usati da `CardClassicheOrdineUiTests`); pulsante aggiungi:
  `TplCampo_Aggiungi_Button`.
- `CampoSistemaCatalogoWindow`: nessun controllo costruito nel code-behind (contenuto XAML): resta GAP.

**Fix allegati (data-safety):** su Test, CR e segnalazione rete la rimozione non tocca piu' il file finche' il Salva non
ha persistito il record senza quel riferimento (`AttachmentRemovalQueue`). Abbandono e Salva fallito lasciano file e JSON
invariati; dentro una Unit of Work la rimozione slitta al commit. Le aggiunte restano copiate subito: un abbandono esplicito
le ripulisce, un crash lascia un orfano accettato (nessun record lo referenzia).

**Uscita dalla tray:** la logica di "Esci" e' in `MainViewModel.PreparaUscitaDefinitivaAsync` (xUnit
`MainViewModelUscitaDefinitivaTests`). La voce di menu nativa resta non automatizzabile con FlaUI.

## Iterazione `wt/flaui-rest`: scenari RemainingCoverage (scritti, NON ESEGUITI)

> **Stato:** scritti e compilati (`dotnet build TestLoom.slnx -c Debug`, 0 errori). **Nessun test UI eseguito**: il PC
> era in uso da un'altra persona. Le prove vanno eseguite centralmente su una sessione Windows dedicata; finche' non
> passano non sono copertura. Nessun `Thread.Sleep` nei test nuovi (il polling passa da `Wait`).

| Area | Classe (`Tests/`) | Scenari | Stato |
|---|---|---|---|
| Lista Test, filtri secondari: Severita', Priorita', Stato anomalia (NC aperta), Veicolo, Funzionalita', Requisito, intervallo date (via vista salvata), Ticketing, combinazione in AND | `RemainingCoverageTestListaFiltriUiTests.cs` | 9 | scritto, non eseguito |
| Report: esporta Excel e PDF dal dialog di salvataggio (file, firma, cronologia), annulla il dialog (nessun file), annulla "Ordina colonne" | `RemainingCoverageReportExportUiTests.cs` | 4 | scritto, non eseguito |
| Ctrl+K: risultato NC (apre Anomalie), Change Request (apre l'editor), Segnalazione da classificare (apre il Test con il pannello) | `RemainingCoverageGlobalSearchUiTests.cs` | 3 | scritto, non eseguito |
| CR e Segnalazione rete: allegato aggiunto (dialog Apri), allegato rimosso, pacchetto (dialog sezioni, cartella di sistema, ZIP con `TestLoom_<Id>.pdf`) | `RemainingCoverageCrSegnalazioniAllegatiUiTests.cs` | 6 | scritto, non eseguito |
| Strumenti: "Gestisci campi" dalla riga (in modifica), apertura e Annulla senza salvare | `RemainingCoverageMasterCampiUiTests.cs` | 1 | scritto, non eseguito |

Totale: **23 scenari scritti, 0 eseguiti**. Trait `Category` come da README (`Navigation`, `Report`, `CRUD`, `Forms`).

Gap storici di questa matrice ora coperti *in scrittura* (da confermare con l'esecuzione): Export Excel/PDF del Report
(riga "Export Excel/PDF"), "Ordina colonne" annullato (riga "Ordina righe / colonne"), Ctrl+K su NC/CR/Segnalazioni
(README, "Scorciatoie"), filtri secondari della Lista Test (nessuna riga prima), allegati e pacchetto CR/Segnalazione.
Audit: "Esportazione CSV del dettaglio guasto" e' gia' coperta da `MasterGapsCoverageAuditUiTests` (dialog di sistema via
`NativeFileDialogPage`): nessuna modifica.

**Punti da confermare alla prima esecuzione (non osservati dal vivo):**
- Intervallo date Lista Test: verificato dal vivo per testo digitato, calendario e data non valida
  (`IntervalloDate_DigitatoNelTesto_ESceltoDalCalendario_FiltranoLeRighe`, `IntervalloDate_DataDigitataNonValida_NonApplicaIlVincoloEMostraIlMessaggio`).
  La vista salvata con `DataDa`/`DataA` resta coperta dal suo test, che passa per `ApplicaVista`.
- Pacchetto CR/Segnalazione: dopo il dialog sezioni, l'app apre un selettore di *cartella* (IFileOpenDialog con
  FOS_PICKFOLDERS). Verificato dal vivo: il campo e' "Cartella:" con AutomationId `1152`, la conferma "Selezione cartella" ha Id `1`
  (`NativeFileDialogPage` accetta sia `1148` sia `1152`).
- Allegato rimosso: la rimozione fisica e' differita al Salva riuscito (`AttachmentRemovalQueue`, commit 6e49795). I test asseriscono:
  dopo "Rimuovi" il file resta nella cartella; dopo "Salva" esce dalla cartella ed e' nel recupero; dopo l'abbandono resta dov'e'.
- Incoerenza da allineare: `AnomalieCrCoverageEsportazioneUiTests` cerca il campo nome del dialog di salvataggio con
  AutomationId `1001`, mentre `NativeFileDialogPage` usa `FileNameControlHost`/`1148`. Una delle due convenzioni puo' essere sbagliata.
- Il pulsante "Apri" della cronologia Report avvia il file con la shell (`Process.Start`): i test non lo premono mai.

**AutomationId aggiunti (solo XAML, nessuna logica):**
- `TestCaseListView.axaml`: `TestList_FiltroPolarion_ComboBox`, `TestList_FiltroDataDa_DatePicker`, `TestList_FiltroDataA_DatePicker`.
- `ChangeRequestEditorView.axaml`: `Cr_Allegato_Nome_Button`, `Cr_Allegato_Rimuovi_Button` (righe allegato, DataTemplate).
- `SegnalazioneReteEditorView.axaml`: `Segnalazione_Allegato_Nome_Button`, `Segnalazione_Allegato_Rimuovi_Button`.
- `PolarionSelezioneDialog.axaml`: `PolarionSelezione_Genera_Button`, `PolarionSelezione_Annulla_Button`.
- `CampiStrumentoEditorWindow.axaml`: `CampiStrumento_Titolo_Text`, `CampiStrumento_ErroreSalvataggio_Text`,
  `CampiStrumento_Anteprima_Button`, `CampiStrumento_Annulla_Button`, `CampiStrumento_Salva_Button`.
- `CampiTemplateEditorWindow.axaml`: `CampiTemplate_Titolo_Text`.
- `StrumentiView.axaml`: `Strumenti_GestisciCampi_Button` (pulsante di riga, condiviso: si usa il primo).

**Non automatizzabile con le modifiche consentite (motivi):**
- **Tray "Esci" con modifiche non salvate** (`App.axaml.cs` → `MainWindow.RichiediUscitaDefinitivaAsync`): il menu e' un
  `NativeMenu` della tray, un popup Win32 fuori dall'albero Avalonia, spesso nell'overflow della barra. Non verificabile senza
  esecuzione. Il metodo vive nel code-behind di `MainWindow` e richiede una finestra Avalonia: non testabile in xUnit
  senza modificare la logica. Da coprire con una iterazione dedicata. **Aggiornato in `wt/allegati-fix`:** la logica e' in `MainViewModel.PreparaUscitaDefinitivaAsync` (xUnit); la voce di menu resta non automatizzabile con FlaUI.
- **Add / edit / ordina campi** (template, strumento, campi di sistema): righe e pulsanti sono costruiti nel code-behind
  (`RootPanel`) senza AutomationId; con sole modifiche XAML non sono raggiungibili. `CampoSistemaCatalogoWindow` si apre solo
  da controlli costruiti nel codice. Richiede un'iterazione con `AutomationProperties.SetAutomationId` nel code-behind. **Ids aggiunti in `wt/allegati-fix`** per strumento e campi template/sistema (sezione sotto).
- **Widget data** (CalendarDatePicker dei filtri Anomalie e Lista Test): pilotati via ValuePattern (testo) e selezione del
  giorno nel calendario (`SelectionItem`), senza coordinate; vedi `AnomalieFiltriAvanzatiTests` e `RemainingCoverageTestListaFiltriUiTests`.

**Bug sospetti (letti dal codice, NON verificati dal vivo, nessun fix applicato):**
1. **Corretto in `wt/allegati-fix`** (rimozione differita fino al Salva riuscito, `AttachmentRemovalQueue`). Segnalazione originale: rimozione allegato di CR e Segnalazione rete (`ChangeRequestEditorViewModel.RimuoviAllegato`,
   `SegnalazioneReteEditorViewModel.RimuoviAllegato`): `AttachmentService.Elimina` sposta il file nel recupero subito,
   prima del Salva. Se l'editor viene abbandonato, l'elemento salvato continua a elencare l'allegato, ma il file non e' piu'
   nella sua cartella.

## Avvio / accesso

| Funzione | Copertura | Note |
|---|---|---|
| Primo avvio (cartella vuota, wizard iniziale) | `Tests/StartupTests.cs` | |
| Login con PIN (operatore esistente) | `Tests/LoginTests.cs` | |
| Login: PIN errato / tasti scorciatoia | `Tests/LoginTests.cs` (Trait Keyboard) | |
| Logout | `PageObjects/MainShellPage.Logout()` usato da più test | |
| Cambio cartella condivisa dopo il primo avvio | — | **GAP (P2)**: nessun test avvia l'app senza `--config` isolato puntando a un cambio cartella runtime; il flusso esiste solo lato `FirstRunViewModel`/unit test. |
| Centro notifiche (overlay al login) | `MainShellPage.DismissNotificationsIfShown()` (helper, usato implicitamente) | Nessun test verifica il *contenuto* delle notifiche, solo la chiusura per sbloccare la navigazione. **GAP (P3)**. |
| Ruoli: Master vs operatore Standard (sola lettura, permessi) | `Tests/RuoliPermessiTests.cs` | `TestWorkspace.CreateLoggedInAs(ruolo)` nuovo: Recovery nascosta, Modifica/Salva assenti su Operatori/componente, PIN/chiave altrui nascosti. Viewer non ancora esercitato (stesso schema, GAP (P3)). |

## Navigazione principale (MainViewModel.NavItems)

| Sezione | Copertura | Note |
|---|---|---|
| Home | `Tests/NavigationTests.cs`, `Tests/HomeDashboardTests.cs`, `PageObjects/HomePage.cs` | |
| Test (lista) | `Tests/NavigationTests.cs`, `Tests/SearchFilterTests.cs`, `Tests/TestCaseCrudTests.cs`, `Tests/HomeDashboardTests.cs` (badge/filtro UnitLogger) | |
| Anomalie / CR (apertura + filtri base) | `Tests/AnomalieBaseTests.cs` | |
| WU Status | `Tests/WuStatusTests.cs` | |
| Report | `Tests/ReportMatriceNonApplicabileTests.cs` | |
| Audit | `Tests/AuditPropagazioneTests.cs` | |
| Impostazioni | `Tests/ImpostazioniNavigazioneTests.cs` (tutte le 10 sotto-sezioni) | |
| Errore di navigazione (pagina che lancia un'eccezione al caricamento) | `MainShellPage.HasNavigationError` usato come assert negativo in vari test | Nessun test induce deliberatamente un errore. **GAP (P3)**. |
| Riordino voci di navigazione principale (drag/frecce) | — | **GAP (P3)**: `NavOrderService`/`SpostaSuNavPrincipale` non ha equivalente UI testato (a differenza dell'ordine Strumenti/Template, coperto). |

## Test — lista e editor

| Funzione | Copertura | Note |
|---|---|---|
| Creazione Test Case (wizard, template minimo) | `Flows/CreateTestCaseFlow.cs`, `Tests/TestCaseCrudTests.cs` | |
| Modifica Test Case esistente (EnterEditMode) | `Tests/TestCaseCrudTests.cs` | |
| Ricerca/filtro lista Test | `Tests/SearchFilterTests.cs` | |
| Validazione campi obbligatori / dialog "Campi mancanti" | `Tests/TestCaseCrudTests.cs` (Trait Forms) | |
| Modifiche non salvate (conferma navigazione, chiusura app) | `Tests/UnsavedChangesTests.cs` | |
| Card classiche: ordine visuale campi | `Tests/CardClassicheOrdineUiTests.cs` | |
| Operazioni/condizioni: esito incl. "Da eseguire"/"Non applicabile" (+ motivazione N.A.), Aggiungi/Rimuovi condizione, Salva+riapertura | `Tests/TestEditorOperazioniTests.cs` | AutomationId per riga parametrizzati per Condizione.Uid, individuati per indice/posizione da `TestEditorPage`. Coperti Conforme, Non Conforme e mostra/nascondi descrizione per Conforme con deviazioni; Non Conforme crea/classifica una NC in `ClassificazioneSegnalazioneNelTestTests`. Ripeti condizione: id presente, non ancora esercitato — **GAP (P2)**. |
| Revisioni/tentativi di esecuzione (cronologia multi-revisione, menu REV) | `Tests/TestEditorRevisioniTests.cs` | Cambia l'esito e salva di nuovo: verifica che nasca una 2a revisione (menu REV con 2 voci) e naviga tra Rev. 1/Rev. 2 (`Editor_MenuRev_Combo`). Sola-lettura di una revisione non corrente durante la navigazione: non verificata nel dettaglio — **GAP (P2)**. |
| Campi personalizzati per Tipo (Testo/Numero/Lista/Checkbox/Data/ListaTesto/File) | `Tests/TestEditorOperazioniTests.cs` | ListaTesto/File verificati solo per presenza (aprono finestre dedicate — `ListaTestoEditorWindow`/file picker di sistema — non interagite). Lista oltre soglia (>10 opzioni, dialog invece di ComboBox): **GAP (P2)**. |
| Strumenti usati nel Test (≤10, inline con checkbox) | `Tests/TestEditorOperazioniTests.cs` (`Editor_Strumento_{Nome}`) | |
| Solo strumenti di supporto: Funzionalità generale richiesta; aggiunta/rimozione strumenti in modifica | `Tests/TestEditorStrumentiSupportoUiTests.cs` | Parte da un template privo di Funzionalità, verifica che il campo sia assente senza supporti, visibile con il solo supporto e che il salvataggio vuoto venga bloccato; aggiunge e rimuove un secondo supporto dopo il salvataggio e verifica la persistenza e l'assenza di operazioni generate. |
| Strumenti usati nel Test (>10, lista virtualizzata con ricerca) | `Tests/TestEditorStrumentiVirtualizzatiTests.cs` | Merge di `release/6.4.0` (ramo `perf/6.4.0-editor-profilo`): ricerca su tutte le voci, spunta di una voce trovabile solo cercando, riepilogo selezionati persistente fuori filtro, Salva+riapertura. Righe individuate per Nome via `Editor_StrumentoLista_{Nome}` (Style su ListBoxItem, aggiunto in questa iterazione). |
| Test collegati (`CreaTestCollegatoWindow`) | `Tests/TestCollegatoTests.cs` | Corretta un'assunzione sbagliata della matrice precedente: `HaAzioniCollegate` e' sempre vera (nessuna configurazione di template richiesta). Verifica creazione con un solo template di destinazione (naviga direttamente al nuovo test) e comparsa del collegamento nel test di partenza. Piu' template insieme, campi da NON copiare: **GAP (P2)**. |
| Import (`ImportExcelOpzioniWindow`, import da UnitLogger) | `Tests/TestEditorOperazioniTests.cs` | La UI Test verifica la presenza dei due comandi UnitLogger distinti (log WU e log main). Gli handler Excel di componente/Gamma/Veicoli/Funzionalità/Requisiti/Strumenti e guasto requisiti passano dalla guida con schema dedicato prima del file picker. **GAP (P1)**: nessun test UI pilota ancora i picker nativi Excel o completa l'import end-to-end; parser/campi UnitLogger sono coperti da unit test. |
| Badge/filtro "Test importati da UnitLogger" | `Tests/HomeDashboardTests.cs` | Usa `TestList_Row_ImportatoUdt_Badge`/`TestList_FiltroImportatoUdt_CheckBox`, già presenti sul ramo. |
| Avviso "Test senza componente" (Home) | `Tests/HomeDashboardTests.cs` | Usa `Home_AvvisoTestSenzaEcu_*`, già presenti sul ramo. |
| Anteprima form (`TestFormPreviewWindow`) | — | **GAP (P3)**. |
| Viewer sola lettura (`TestCaseViewerWindow`) | — | **GAP (P3)**. |

## Anomalie / CR / Segnalazioni

| Funzione | Copertura | Note |
|---|---|---|
| Apertura pagina, ricerca testo, filtro componente (catalogo vuoto) | `Tests/AnomalieBaseTests.cs` | |
| Filtro ambito su archivio reale (100k) | `Tests/FiltroAmbitoSenzaTestRealeTests.cs` | Su cartella condivisa adottata, non seminata. |
| Lista virtualizzata con scroll (novità 6.4.0) | — | **GAP (P2)**: nessun test scrolla oltre la pagina corrente per verificare la virtualizzazione. |
| Workflow NC: Chiudi (motivo obbligatorio + conferma), Riapri (Master + conferma), Elimina (Master + conferma) | `Tests/NonConformitaWorkflowTests.cs` | Semina NC con e senza ticket TICKET; verifica i filtri Con/Senza ticket, la presenza e l'attivazione del link TICKET in tabella e le azioni verticali a 1280×800. Verifica anche il layout del dialogo entro 1280×800. Cambio Stato/Severità/Priorità/Owner e "Salva modifiche"/collegamento ticket Ticketing: id presenti ma non esercitati — **GAP (P2)**. "Collega a Test" a posteriori: id presente, non esercitato — **GAP (P2)**. |
| Creazione NC dall'editor Test (esito Non Conforme) | `Tests/ClassificazioneSegnalazioneNelTestTests.cs` | Vedi anche la riga "Classificazione/esclusione..." sotto: e' lo stesso flusso. |
| Creazione Change Request (template, titolo, descrizione, Salva) | `Tests/ChangeRequestSegnalazioneCreazioneTests.cs` | Richiede un template con `IsChangeRequest=true` seminato (nessuno esiste di default). |
| Workflow CR: selezione stato nel form completo e nella modifica rapida, implementazione, chiusura da Validata e riapertura | `Tests/AnomalieCrCoverageChangeRequestUiTests.cs`, `Tests/AnomalieCrCoverageListaUiTests.cs`, `Tests/ChangeRequestQuickEditValidationUiTests.cs` | Un solo selettore di stato e Salva in entrambe le modalità; la lista di destinazioni rispetta autorizzazioni e regole della CR. Gli errori di validazione e la conferma di successo restano visibili nella schermata. |
| Modifiche non salvate sull'editor CR | `Tests/ChangeRequestModificheNonSalvateTests.cs` | |
| Ambito impattato CR (Aggiungi veicoli/componente/strumenti/gamme) | — | **GAP (P2)**: id presenti su "Aggiungi componente/veicoli/strumenti", il flyout di selezione multipla stesso non ha Page Object dedicato. |
| Creazione segnalazione rete (numero pratica/matricola/licenza, descrizione, severità/priorità/stato, Salva) | `Tests/ChangeRequestSegnalazioneCreazioneTests.cs` | Verifica che compaia come Non Conformità (badge "Da rete") nella lista. |
| Classificazione/esclusione di una segnalazione "provvisoria" nel Test | `Tests/ClassificazioneSegnalazioneNelTestTests.cs` (classifica -> Nuova anomalia) | Corretta un'assunzione sbagliata della matrice precedente: il vero punto d'ingresso e' il pannello "Anomalie" DENTRO l'editor Test (creato automaticamente da un'operazione Non Conforme/Con deviazioni), non la card disattivata in NonConformitaView. Esclusione (`EscludiSegnalazioneDaTestCommand`) e "Collega ad anomalia esistente": id non ancora aggiunti — **GAP (P2)**. |
| Stati e comandi di workflow NC generico (InVerifica→...→Conclusa, tutti i valori intermedi) | `Tests/NonConformitaWorkflowTests.cs` (solo Chiudi/Riapri) | I valori intermedi (Confermata/EvidenzeDaCompletare/ProntaPerEsta/EstaAperto/InAttesa/UlterioriTestRichiesti/ProntaPerRetest/RetestFallito) non sono singolarmente percorsi — **GAP (P2)**. |
| Filtri multi-selezione virtualizzati con ricerca (novità 6.4.0) | `Tests/FiltroMultiSelectVirtualizzatoTests.cs` (Anomalie/componente completo: ricerca+selezione+riepilogo; Home/Gamma solo apertura+ricerca) | `FiltroMultiSelectVirtualizzato` (0 id prima di questa iterazione, condiviso da Home/Report/Anomalie) ora ha `FiltroMultiSelect_Ricerca_Input`/`_Lista`/`_Voce_{Etichetta}`/`_SelezionaTutto_Button`/`_DeselezionaTutto_Button`. Report non ancora esercitato (stesso componente, id `Report_FiltroTemplate_Button` gia' aggiunto) — **GAP (P2)**. |

## WU Status

| Funzione | Copertura | Note |
|---|---|---|
| Apertura pagina, filtro testo, filtri rapidi Tutti/Solo OK/Solo problemi, Rimuovi filtri | `Tests/WuStatusTests.cs` | |
| Override manuale impostato/rimosso DALLA RIGA (Auto/OK/Prob.), senza aprire il dettaglio | `Tests/WuStatusOverrideEFiltriUiTests.cs` (WU seminate con Test collegati via AppState prima dell'avvio) | AutomationId per riga: `WuStatus_Row_Wu_Text`, `WuStatus_Row_Stato_Text`, `WuStatus_Row_Divergenza_Text`, `WuStatus_Row_OverrideAuto/Ok/Problemi_Button` (stesso id su ogni riga, associati alla riga dal PageObject per sovrapposizione verticale). |
| Divergenza override/automatico + nota obbligatoria | stesso file | `WuStatus_NotaObbligatoria_Text`, `WuStatus_NotaManuale_Input`, `WuStatus_SalvaStatoManuale_Button`. |
| Filtri multipli in AND (Veicolo, Sottoveicolo, componente, Funzionalita', Stato automatico, Override, Divergenza, Ultimo operatore, Ultimo test) + ricerca testo + rapidi + Rimuovi filtri + persistenza dopo riavvio | stesso file | `WuStatus_Filtro{Veicolo,Sottoveicolo,Componente,Funzionalita,StatoAutomatico,Override,Divergenza,Operatore,UltimoTest}_Button`, `WuStatus_Totale_Text`; flyout = `FiltroMultiSelectPage`. |
| Layout tabella (colonne allineate a header, nessun taglio/schiacciamento, layout legacy ignorato) | `Tests/WuStatusLayoutTests.cs` | |
| Filtri principali visibili / altri collassati con riepilogo, persistenza per PC dopo riavvio, Rimuovi filtri svuota | `Tests/WuStatusFiltriCollassatiUiTests.cs` | `WuStatus_AltriFiltri_Expander` (header `ExpanderHeader` con nome = riepilogo). |
| Operatore non Master: pulsanti override disabilitati | `WuStatusOverrideStandardUiTests` (stesso file) | |
| Unione manuale di due WU ("Unisci WU": annulla e conferma; la WU scartata esce dal catalogo e i Test passano alla WU corretta) | `Tests/MasterGapsCoverageWuStatusUiTests.cs` | **BugAperto** (`UnioneWu_Conferma_RiscriveIRiferimentiSenzaAggiungereRevisioni`, categoria BugAperto, rosso di proposito): Unisci WU aggiunge una revisione al Test invece di riscrivere il riferimento come fa `MasterUnioneService` (`AmmettiRiscritturaRiferimentiCatalogo`). Coppie sospette (pulsante rapido): **GAP (P1)**. |
| Vista salvata (salva dai filtri correnti, riapplica dal combo) | `Tests/MasterGapsCoverageWuStatusUiTests.cs` | |
| Paginazione (100k WU) | — | **GAP (P2)**: nessun dato di scala seminato in questa iterazione (vedi `FiltroAmbitoSenzaTestRealeTests` per il pattern su archivio reale, riusabile). |

## Report

| Funzione | Copertura | Note |
|---|---|---|
| Matrice, marcatura N.A. per cella | `Tests/ReportMatriceNonApplicabileTests.cs` | |
| Asse X della matrice (cambio dimensione) | `Tests/MasterGapsCoverageReportUiTests.cs` | |
| Ordina righe / colonne (dialog OrdinaValori): conferma salvata in `config.json` e riflessa alla riapertura e nella matrice; annulla senza scritture | `Tests/MasterGapsCoverageReportUiTests.cs` | |
| Asse Y e ricerca della matrice | `PageObjects/ReportPage.cs` (metodi presenti) — non riverificato in questa iterazione | **GAP (P2)** per una verifica esplicita dell'asse Y e della ricerca. |
| Export Excel/PDF | — | **GAP (P2), rischio alto se affrontato senza esecuzione reale**: `ReportView.OnEsportaExcel/OnEsportaPdf` usano `TopLevel.StorageProvider.SaveFilePickerAsync`, un dialog NATIVO del sistema operativo (non un AutomationId Avalonia). Automatizzarlo con FlaUI è possibile in teoria (i common dialog di Windows espongono UIA) ma fragile e non verificabile da qui (nessuna esecuzione su Windows disponibile in questa sessione): scrivere un test "a naso" contro un dialog nativo mai osservato dal vivo rischierebbe di codificare assunzioni sbagliate spacciate per copertura. Lasciato esplicitamente come GAP da affrontare con accesso a una sessione Windows reale per osservare la struttura del dialog. |

## Audit

| Funzione | Copertura | Note |
|---|---|---|
| Matrice Audit, propagazione valutazione | `Tests/AuditPropagazioneTests.cs` | |
| Dettaglio guasto Extra (drill-down): filtri componente e Stato, "Solo non completi", esportazione CSV su file del workspace di test | `Tests/MasterGapsCoverageAuditUiTests.cs` | |
| Risoluzione conflitto (RisoluzioneConflittoAudit) | — | **GAP (P2)**. |

## Master / Impostazioni

| Sotto-sezione | Copertura | Note |
|---|---|---|
| Generale | `Tests/ImpostazioniNavigazioneTests.cs` (solo apertura); card "Conflitti nei cataloghi master" in `Tests/MasterGapsCoverageConflittiUiTests.cs` (Aggiorna, elenco delle due versioni, "Usa questa versione" confermata e annullata) | Sezioni Ticketing di default non testate. **GAP (P3)**. |
| Archivio | `Tests/ArchivioTests.cs` (Ripristina) | Elimina definitivamente: id presente (`Archivio_EliminaDefinitivamente_Button`), non esercitato — **GAP (P2)** (distruttivo, verificarlo richiede accettare la perdita del dato nel test stesso). Svuota archivio: id presente, non esercitato. |
| Recovery (solo Master) | `Tests/RecoveryBinNavigazioneTests.cs` (apertura + Aggiorna) | Ripristino/purga di un vero envelope: **GAP (P1)** — richiede `DomainRecoveryService` per generare un envelope reale (un file davvero eliminato), non seminabile con la sola scrittura diretta di un repository come le altre Master data. |
| Operatori (lista, ruoli, PIN/chiave recupero) | `Tests/MasterDataAzioniModificaTests.cs` (Modifica/Annulla), `Tests/RuoliPermessiTests.cs` (PIN nascosto a non-Master), `Tests/MasterGapsCoverageOperatoriUiTests.cs` (PIN impostato dal pannello e persistito; chiave di recupero rigenerata senza toccare il PIN) | Creazione nuovo operatore e cambio ruolo: **GAP (P1)** — richiede editing celle DataGrid (ComboBox Ruolo), non ancora affrontato in questa suite. |
| Requisiti (Standard/Extra, Banco/Veicolo) | `Tests/MasterDataAzioniModificaTests.cs` (Modifica/Annulla), `Tests/ImpostazioniModificheNonSalvateTests.cs` (Nuovo + modifiche non salvate), `Tests/MasterGapsCoverageMasterDataUiTests.cs` (cella Testo di una riga esistente, archivia e ripristina dall'Archivio, aggiungi e rimuovi un guasto su requisito Extra) | Scelta dei veicoli Extra (dialog dedicato `OnScegliSottoveicoli`), import Excel: **GAP (P1)**. Il tipo requisito (Standard/Extra) e fisso per lista e mostrato come etichetta di sola lettura: il cambio Tipo non e piu una funzione. |
| Funzionalità (+ Categorie annidate) | `Tests/MasterDataAzioniModificaTests.cs` (Modifica/Annulla) | Categorie annidate: nessun AutomationId proprio (solo il gruppo principale `Funz_*`), associazione componente (`OnScegliEcu` sulla cella): **GAP (P1)**. |
| componente (+ veicoli associati) | `Tests/MasterDataAzioniModificaTests.cs` (Modifica/Annulla), `Tests/ArchivioTests.cs` (Archivia/Ripristina), `Tests/MasterDataUnioneTests.cs` (pannello Unione) | Import Excel, associazione veicoli (`OnScegliVeicoli` sulla cella): **GAP (P1)**. |
| Veicoli (+ Gamme annidate) | `Tests/MasterDataAzioniModificaTests.cs` (Modifica/Annulla) | Gamme annidate hanno AutomationId (`VeicGamma_*`) ma nessun test le esercita ancora — **GAP (P2)**. |
| Strumenti (incl. lista virtualizzata >10 voci nell'editor Test) | `Tests/StrumentiOrdineUiTests.cs` (ordine), `Tests/TestEditorOperazioniTests.cs` (campi personalizzati per Tipo) | Ricerca virtualizzata >10: vedi riga dedicata sotto "Test — editor" (dipendenza dal ramo `perf/6.4.0-editor-profilo`). CRUD Modifica/Salva reale sulla pagina Strumenti stessa (non solo ordine): **GAP (P1)**. |
| Tipi di test / CR (`TestTemplateView`) | `Tests/TemplateClassiciOrdineUiTests.cs` (ordine campi), `Tests/TipiTestGestisciCampiTests.cs` (apertura/chiusura "Gestisci campi") | Creazione template, editing dei singoli campi dentro `CampiTemplateEditorWindow` (nessun AutomationId oltre "Chiudi"): **GAP (P1)**. |
| Campi di sistema (ruoli, catalogo) | — | **GAP (P2)**: `CampoSistemaCatalogoWindow` ha 0 AutomationId, si apre da dentro "Gestisci campi". |
| Unione/rinomina/divisione voci (tutte le Master data con `Unione`) | `Tests/MasterDataUnioneTests.cs` (Componente: selezione riga → pannello disponibile) | Completamento effettivo di Unisci/Rinomina/Dividi (dialog di conferma + esecuzione in background con `IsBusy`/`ProgressoTesto`): **GAP (P1)** — la sequenza esatta del dialog di conferma per queste tre azioni non è stata osservata dal vivo (nessuna esecuzione possibile), rischio alto di codificare un'assunzione sbagliata; da completare con accesso a una sessione Windows reale. |
| Ruoli (Master vs operatore Standard, sola lettura) | `Tests/RuoliPermessiTests.cs` | Viewer (terzo ruolo) non ancora esercitato — stesso schema, **GAP (P3)**. |
| Lingua | — | **GAP (P3)**. |

## Dialog e comportamenti trasversali

| Funzione | Copertura | Note |
|---|---|---|
| ConfirmDialog (Sì/No) | `Tests/DialogTests.cs`, `PageObjects/ConfirmDialogPage.cs`, riusato da `NonConformitaWorkflowTests`/`UnsavedChangesTests`/`ImpostazioniModificheNonSalvateTests`/`ChangeRequestModificheNonSalvateTests` | |
| SelezionaDialog (singola/multipla + filtro) | `PageObjects/SelectDialogPage.cs`, usato da vari Flow | |
| CampiMancantiDialog | `Tests/TestCaseCrudTests.cs` | |
| Modifiche non salvate (editor Test) | `Tests/UnsavedChangesTests.cs` | |
| Modifiche non salvate (Impostazioni: cambio sotto-sezione) | `Tests/ImpostazioniModificheNonSalvateTests.cs` | |
| Modifiche non salvate (editor CR) | `Tests/ChangeRequestModificheNonSalvateTests.cs` | |
| Modifiche non salvate (editor Segnalazione rete) | — | **GAP (P2)**: stesso schema di CR, non ancora scritto per limiti di tempo. |
| MergeConflittoDialog | — | **GAP (P1)**: conflitto di merge multi-PC non testato via UI (coperto solo a livello unit/integration in `TestLoom.App.Tests`, fuori scope di questa suite: richiederebbe due processi TestLoom.exe concorrenti sulla stessa cartella condivisa). |

## Scala / prestazioni (per contesto, non oggetto di questa iterazione)

| Funzione | Copertura |
|---|---|
| Navigazione su archivio reale 100k | `Tests/PerfNavigazioneTests.cs` |
| Filtro ambito senza test reale su archivio 100k | `Tests/FiltroAmbitoSenzaTestRealeTests.cs` |
| Persistenza dopo riavvio | `Tests/PersistenceTests.cs` |
