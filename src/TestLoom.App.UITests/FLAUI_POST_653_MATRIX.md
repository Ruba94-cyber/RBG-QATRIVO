# FlaUI UIA3 — Matrice di regressione post-6.5.3

Stato: **test scritti, non ancora eseguiti sul vero desktop Windows**.  
Branch: `feat/testloom-safety-scale-ux-post-6-5-3`; PR #4 **Draft / non merge**.

## Obiettivo e vincoli

Ogni funzionalità nuova o modificata deve avere almeno un test unitario di
dominio e, quando ha un controllo grafico, una prova FlaUI end-to-end sulla UI
Avalonia reale. Il test non deve cambiare il comportamento dell'app né
introdurre sincronizzazioni false, bypass, reset di dati o modifiche di workflow.
Usare sempre `RBG - QATRIVO.exe` reale, `UIA3Automation`, AutomationId stabili,
`Wait.ForCondition` con timeout e workspace di prova isolati.
Nessun `Thread.Sleep` arbitrario nei **nuovi** test: usare il polling bounded
di `Infrastructure/Wait.cs`.

## Scenari aggiunti in questa tranche

| Area | Classe | Scenari | Che cosa provano |
|---|---|---:|---|
| Impostazioni / dati | `Post653ImpostazioniSafetyUiTests` | **9** | Visualizzatore attiva/annulla/disattiva, gate scrittura, consultazione, testo verifica locale, conferma pinning, ZIP di backup SHA-256, rifiuto destinazione in workspace, persistenza config |
| TestCase | `Post653CopiaOperazioneUiTests` | **5** | Copia autonoma, esito iniziale, note non duplicate, più copie con UID distinti, rimozione, differenza da Ripeti, JSON persistito |
| Shell / Impostazioni / Anomalie / WU | `CrossModuleNavigationMatrixUiTests` | **24** | 7 navigazioni primarie, 10 sotto-sezioni, 3 schede Anomalie, 3 filtri WU, ricerca TestCase senza risultati |
| Change Request / Segnalazioni | `CrossModuleValidationMatrixUiTests` | **9** | Campi obbligatori mancanti nei due form, popup, rifiuto del salvataggio, nessun nuovo JSON autorevole |
| Viewer / permessi | `ViewerPermissionsMatrixUiTests` | **4** | Consultazione, TestCase non creabili, Master data non modificabili, PIN non esposti, Recovery non disponibile |
| Bozze locali / anteprima | `EmergencyDraftInspectorUiTests` | **6** | Elenco reale, anteprima su richiesta, file corrotto, paginazione 30+, refresh e assenza di scritture condivise |
| Bozze / permessi Viewer | `EmergencyDraftInspectorViewerUiTests` | **4** | Elenco vuoto non mutante, anteprima senza permessi di scrittura, isolamento workspace e bozza modificata dopo la selezione |
| Confronto bozza/documento canonico e differenze campi | `EmergencyDraftComparisonUiTests` | **12** | Payload uguale/diverso, file mancante, UID/ID discordanti, revisione obsoleta, tombstone, JSON corrotto, bozza cambiata dopo apertura; nessuna scrittura |
| Centro integrità locale | `WorkspaceDiagnosticsUiTests` | **5** | Metadati locali, avviso OneDrive, allegati non mutati, conteggi bozze, modalità visualizzatore e ritorno alla Home |
| Centro integrità per Viewer | `WorkspaceDiagnosticsViewerUiTests` | **2** | Lettura, refresh, assenza di scritture e permessi Viewer rispettati |
| Verifica ZIP locale | `BackupArchiveAuditUiTests` | **3** | Archivio valido, corrotto, assente, nessuna estrazione |
| Conflitto CR arrivato dopo refresh | `ConflictCopyLateArrivalUiTests` | **1** | Fork divergente prima del salvataggio, nessuna sovrascrittura, bozza conservata |
| Preparazione bozza con conferma | `DraftRecoveryPreparationUiTests` | **2** | Annulla non crea file; conferma crea solo candidato locale non autorevole |
| Gestione WU in pagina Status | `WuArchiveUiTests` | **2** | Master archivia/ripristina con conferma, senza eliminare record e riferimenti |
| Restrizioni WU per Viewer | `WuArchiveViewerUiTests` | **1** | Viewer consulta ma non può archiviare né ripristinare Pacchetto |
| **Totale aggiunti** | | **89** | *Casi parametrizzati contati come singoli casi di esecuzione* |

Si aggiungono ai test già esistenti in `TestLoom.App.UITests/Tests`
(non sostituiscono le suite su TestCase CRUD, guasto, requisiti, master data, NC,
CR, WU, Audit, filtri, report, recovery, prestazioni e layout).

## Identificatori UI aggiunti (solo metadati per test e accessibilità)

- `Impostazioni_Dati_MantieniLocali_Button`
- `Impostazioni_Dati_ViewerAttiva_Button`, `_ViewerDisattiva_Button`
- `Impostazioni_Dati_VerificaLocale_Button`
- `Impostazioni_StatoSincronizzazione_Text`
- `Impostazioni_Backup_Crea_Button`, `_EsternoPercorso_TextBox`, `_SalvaPercorso_Button`, `_CopiaEsterno_Button`
- `Impostazioni_Bozze_Apri_Button`, `Impostazioni_Errore_Text`
- `Audit_Title_Text`
- `Editor_CopiaDallaPrecedente_Button`
- `Impostazioni_Bozze_Esamina_Button`
- `DraftInspector_Title_Text`, `DraftInspector_List`, `DraftInspector_Status_Text`,
  `DraftInspector_Details_Text`, `DraftInspector_Preview_TextBox`
- `DraftInspector_Preview_Button`, `DraftInspector_Previous_Button`,
  `DraftInspector_Next_Button`, `DraftInspector_Refresh_Button`, `DraftInspector_Close_Button`
- `Impostazioni_CentroIntegrita_Button`, `Integrity_Title_Text`,
  `Integrity_Status_Text`, `Integrity_Details_TextBox`,
  `Integrity_Refresh_Button`, `Integrity_Close_Button`
- `DraftInspector_Prepare_Button`, `DraftInspector_Prepared_Text`
- `Impostazioni_Backup_VerificaZip_Button`, `Impostazioni_Messaggio_Text`, `Cr_Errore_Text`
- `DraftInspector_Compare_Button`, `DraftInspector_CompareStatus_Text`,
  `DraftInspector_CompareLocal_TextBox`, `DraftInspector_Differences_TextBox`

Non sono stati cambiati Command, repository, ruoli o flussi di produzione
per accomodare i test: gli AutomationId non devono mutare le logiche.

## Esecuzione reale su Windows

1. Chiudere RBG - QATRIVO.exe eventualmente già in esecuzione (single-instance).
2. Aprire una sessione desktop **interattiva**; niente runner come servizio/headless.
3. Usare `pwsh tools/validation/Run-Flaui-Post653.ps1` (build app, build UI tests,
   esecuzione 89 scenari nuovi con report TRX).
4. Se fallisce una build, **non** proseguire a lanciare test su EXE vecchio.
5. Dopo test mirati verdi: lanciare la suite completa
   `pwsh tools/validation/Run-Flaui-Post653.ps1 -All`.
6. Conservare screenshot, albero UIA e stack trace generati da
   `FailureArtifacts.Capture`; correggere solo difetti effettivamente
   riprodotti, senza allentare assert o saltare test per ottenere verde.
7. Per modalità viewer e dati sincronizzati, eseguire **in aggiunta** la matrice
   2 PC OneDrive prevista da `TestLoom_PILOT_VALIDATION_GUIDE.md`.

## GAP ancora da coprire e priorità

**P0 (bloccano release):**
- Scrittura condivisa cross-PC con OneDrive in pausa/disconnesso: il marker
  visualizzatore **manuale** non equivale a rilevazione automatica.
- Race R9: repliche conflittuali, retention di entrambe, intervento Master
  su **ogni** dominio, tombstone e rollback.
- Recovery guidato delle bozze di CR/NC/master/Test dopo una scrittura fallita,
  diff e conferma; perdita evitata anche dopo crash o interruzione brusca.
- Dataset intero 6.5.3, tombstone, audit, recovery, allegati online-only,
  compatibilità cutover di tutti i PC.

**P1 (aggiungere con i relativi interventi):**
- Anteprima+validazione import Excel/UnitLogger (inclusi dialog di selezione file
  e righe scartate), esportazione Excel/PDF reale su dataset grandi.
- Nuovi TestCase: assegnazione/priority/deadline Master-only, copia
  di campi strumento personalizzati, interazione con guasto, più revisioni,
  cancellazioni/riapertura senza perdita dati.
- Link bidirezionali NC/CR/segnalazioni/TestCase, contesto navigazione.
- Dashboard Home personali/di team, filtri salvati, ricerca globale.
- Notifiche interne/Windows e preferenze per utente.
- Backup allegati separati, backup locale/esterno con restore reale,
  ripresa download Files On-Demand e spazio insufficiente.

**P2:**
- Visualizzazione a risoluzione ridotta, localizzazione IT/EN,
  navigazione tastiera, accessibilità tab order, centri notifiche,
  tema scuro, primo avvio e viewer TestCase completi.
- Virtualizzazione e scroll per 50k–100k requisiti/TestCase, guasto molto
  grandi e report: UI automation + benchmark applicativo separato.

**Non certificare copertura completa o esito verde** finché tutti i test non
sono stati eseguiti su Windows. Le prove E2E isolate non sostituiscono il
collaudo su due macchine aziendali.

### Correzione importazione UnitLogger/Pacchetto

- Il pulsante su ciascuna condizione aggiorna `UnitLogger::Pacchetto` e `UnitLogger::Versione Pacchetto` della **stessa operazione**, anche nel `CampoStrumentoValore` già visibile.
- Il file picker Pacchetto ammette qualunque nome `*.log`; il parsing richiede comunque dati UnitLogger validi all'interno.
- Sono aggiunti test unitari per due operazioni distinte, tre nomi di log arbitrari e contratto del file picker.
- `WuArchiveUiTests`: test UIA3 della funzione Master Archivia/Ripristina (senza cancellazione fisica o perdita di storico).
