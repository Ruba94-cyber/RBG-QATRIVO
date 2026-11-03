# TestLoom.App.UITests — suite di UI automation per TestLoom

Suite FlaUI (UIA3) che avvia il vero `RBG - QATRIVO.exe` come processo esterno e lo pilota tramite UI Automation, per
verificare che un utente reale possa avviare l'app, navigare, creare/modificare/cercare Test, gestire dialog e
modifiche non salvate, e ritrovare i dati dopo un riavvio. Non è un sostituto di `TestLoom.App.Tests` (unit/
integration test sul motore, incluse le scale industriali a 100k record): questa suite copre lo strato che
`TestLoom.App.Tests` non può, l'interazione reale con le finestre Avalonia.

## Prerequisiti

- **Sessione Windows interattiva reale** con desktop visibile (RDP/console va bene, un servizio headless senza
  desktop no — FlaUI parla con UI Automation, che richiede una sessione interattiva).
- **Nessuna istanza reale di TestLoom già aperta** sullo stesso PC. TestLoom applica un lock singolo per macchina
  (`SingleInstanceService`): un secondo avvio non apre finestra, chiede alla prima di attivarsi ed esce. I test
  lo rilevano e falliscono subito con un messaggio chiaro (`TestLoomProcessGuard`) invece di andare in timeout
  incomprensibile — ma è comunque necessario chiudere ogni sessione TestLoom reale (anche ridotta a icona/tray)
  prima di lanciare la suite.
- **`src/TestLoom.App/TestLoom.App.csproj` già compilato** (Debug o Release): la suite non ricompila l'app,
  cerca l'eseguibile già presente sotto `src/TestLoom.App/bin/{Debug|Release}/net10.0/win-x64/RBG - QATRIVO.exe`
  (preferisce il più recente se esistono entrambi). Override esplicito: variabile d'ambiente `TestLoom_EXE_PATH`.
- .NET SDK in grado di compilare `net10.0-windows` (lo stesso SDK usato per il resto della solution).

## Build

```bash
dotnet build src/TestLoom.App/TestLoom.App.csproj -c Debug
dotnet build src/TestLoom.App.UITests/TestLoom.App.UITests.csproj -c Debug
```

## Suite ampliata post-6.5.3 — 89 nuovi scenari

La matrice di verifica e le lacune ancora da coprire sono in
[`FLAUI_POST_653_MATRIX.md`](FLAUI_POST_653_MATRIX.md). Prima di eseguire
gli UI test, compilare sempre la versione corrente dell'app: il runner seguente
fa entrambe le build e interrompe l'esecuzione se una fallisce.

```powershell
pwsh tools/validation/Run-Flaui-Post653.ps1          # 89 nuovi test, 8 suite
pwsh tools/validation/Run-Flaui-Post653.ps1 -All     # intera regressione
pwsh tools/validation/Run-Flaui-Post653.ps1 -Smoke   # smoke esistente
```

**Non eseguire come servizio headless**, non aprire RBG - QATRIVO.exe reale nello stesso
momento e non assumere che un test UI isolato dimostri sincronizzazione OneDrive
completata. Le prove multi-PC sono un gate separato e obbligatorio.
Sviluppo test-first: ad ogni nuova funzione/bugfix di questo branch aggiungere
prima o contestualmente il relativo test di comportamento, tenendo separate
verifiche unit/integration, FlaUI e pilot OneDrive.

## Eseguire la suite

Smoke (avvio, navigazione, login, CRUD/persistenza minimi — la si esegue a ogni cambiamento rilevante):

```bash
dotnet test src/TestLoom.App.UITests/TestLoom.App.UITests.csproj --filter "Category=Smoke"
```

Regression completa (tutta la suite):

```bash
dotnet test src/TestLoom.App.UITests/TestLoom.App.UITests.csproj
```

Filtro per categoria (Traits, Fase 23):

```bash
dotnet test src/TestLoom.App.UITests/TestLoom.App.UITests.csproj --filter "Category=Persistence"
dotnet test src/TestLoom.App.UITests/TestLoom.App.UITests.csproj --filter "Category=Dialogs|Category=Keyboard"
```

Categorie in uso: `Smoke`, `Navigation`, `Forms`, `CRUD`, `Persistence`, `Dialogs`, `Keyboard`, `Regression`.

`Tour` non è una regressione: `Tests/ScreenshotTourSchermoPiccoloTests.cs` fa un giro di screenshot di TestLoom su
schermo piccolo (demo completa sintetica, utente Master, finestra massimizzata e ridimensionata a 1280x672
logici) e pilota mouse e tastiera. Va lanciato esplicitamente con `--filter "Category=Tour"`; le regressioni
complete lo escludono con `--filter "Category!=Tour"`. Output in `.ai/artifacts/ui-tour/` (PNG e `INDEX.md`).

Un singolo test (nome esatto o sottostringa):

```bash
dotnet test src/TestLoom.App.UITests/TestLoom.App.UITests.csproj --filter "FullyQualifiedName~PersistenceTests"
```

I test girano **sempre in sequenza** (`xunit.runner.json`: `parallelizeAssembly=false`,
`parallelizeTestCollections=false`, e ogni classe di test è marcata `[Collection("TestLoom UI")]` con
`DisableParallelization=true`): competono per focus, mouse, tastiera e finestre di un solo desktop reale.

## Isolamento dei dati

Ogni test crea un `TestWorkspace` (`Infrastructure/TestWorkspace.cs`) sotto
`%TEMP%\TestLoom_UITests\<NomeTest>_<guid>\`: una cartella condivisa TestLoom temporanea vuota + un `config.json`
locale temporaneo, mai il config reale dell'utente (`%AppData%\TestLoom.App\config.json`) e mai la cartella
condivisa vera. Il seeding (un operatore Master, un template minimo, un componente e un veicolo) avviene istanziando
direttamente `AppState`/`AppConfigService` dallo stesso assembly `TestLoom.App` — la stessa tecnica già usata
da `TestLoom.App.Tests` — **mai** pilotando l'app per crearli. RBG - QATRIVO.exe viene poi avviato con
`--config <percorso-isolato>`, un argomento CLI già previsto da `App.axaml.cs` proprio per i collaudi end-to-end
isolati. Il workspace viene eliminato a fine test (`Dispose`, best-effort).

L'operatore seminato ha `Config.OperatoreAttivo` già impostato e il tour di onboarding già segnato completato:
TestLoom atterra direttamente sulla Home senza richiedere login manuale (coerente con
`RiallineaOperatoreCorrente`, che nella build attuale rilegge l'operatore attivo dal nome salvato in config
senza richiedere di nuovo il PIN a ogni riavvio — un PIN è descritto nel codice stesso come "gate di
comodo/igiene, non una vera barriera di sicurezza"). I pochi test che devono esercitare davvero la schermata
di login (`LoginTests`) usano invece `TestWorkspace.CreateLoggedOut`.

Il template minimo di QA (`TestWorkspace.DefaultTemplateName`, solo Descrizione breve + componente obbligatori)
esiste per rendere il wizard di creazione compilabile senza dipendere dai campi/vincoli dei 4 template di
prodotto (Validazione Banco/Veicolo/UnitLogger/UnitLogger Performance), che un Master può rinominare o modificare in
qualunque momento. Quei 4 template di bootstrap vengono **archiviati** (non cancellati) dal seed, altrimenti il
passo 1 del wizard mostrerebbe 5 pulsanti "scegli template" indistinguibili da UI Automation (stesso
AutomationId, sono ripetizioni dello stesso DataTemplate).

## Struttura

```
Infrastructure/   Avvio/arresto RBG - QATRIVO.exe, workspace isolato, wait/retry, screenshot su failure, guard single-instance
PageObjects/      Un Page Object per area funzionale reale (Login, Shell, Home, Test List, Editor, dialog...)
Flows/            Sequenze multi-schermata riusate da più test (es. creare un Test Case end-to-end)
Helpers/          Estensioni di localizzazione locator/azioni (click robusto, selezione ComboBox/ListBox per testo)
Tests/            I test veri e propri, un file per area
```

### Page Object

Un Page Object incapsula i locator e la meccanica FlaUI dietro azioni con nome (`TestListPage.ClickNew()`,
`TestEditorPage.ChooseEcu(...)`), mai chiamate FlaUI dirette dentro i test. Nessun "God Object": ogni area
(Login, Shell/navigazione, Home, Lista Test, Editor Test, dialog Conferma/Selezione) ha il proprio.

### Convenzione AutomationId

`Screen_Control_Purpose` (es. `TestList_Search_TextBox`, `Editor_Save_Button`, `Login_Pin_TextBox`). Gli
AutomationId sono API pubblica della suite: una volta aggiunti restano stabili, non rinominarli senza
aggiornare i Page Object corrispondenti. Ordine dei locator effettivamente usato, in questo ordine di
preferenza:

1. `AutomationId` (`ElementFinders.WaitById`) — il default per tutto ciò che non è ripetuto in una lista.
2. `AutomationId` + `ControlType` (`ElementFinders.WaitByIdAndControlType`) — per disambiguare un contenitore
   riusabile con più figli che condividessero per errore lo stesso Id.
3. Un AutomationId condiviso ma **scoped a un elemento contenitore preciso** (es. il bottone "Apri" di una
   riga della Lista Test, cercato dentro quella riga, non nell'intera griglia) — usato solo per i pochi
   controlli ripetuti per riga/voce dove serve.
4. Un valore **dati** noto (nome di un componente/operatore seminato dal test) come testo per selezionare una voce di
   ComboBox/ListBox — mai testo di interfaccia tradotto o localizzato.

Mai: posizione nella visual tree, indici, coordinate assolute, testo tradotto come unico locator, XPath.

### Una scoperta empirica importante: dove serve l'AutomationId in una `ListBox`

Un `AutomationId` (o `AutomationProperties.Name`) impostato su un elemento **dentro** l'`ItemTemplate` di una
`ListBox` non viene esposto da Avalonia via UI Automation: UIA vede solo l'`AutomationPeer` del `ListBoxItem`
generato, non quello dei suoi figli. La soluzione (applicata in `MainWindow.axaml`, `ImpostazioniView.axaml`,
`SelezionaDialog.axaml`) è impostare `AutomationProperties.AutomationId`/`Name` in uno `Style` che seleziona
`ListBoxItem`, con `x:DataType` sullo `Style` stesso per far risolvere il binding compilato contro il tipo
dell'item (non contro il ViewModel del contenitore). Un `ItemsControl` (senza container `ListBoxItem`, es. il
selettore "Tipo test" del wizard) non ha questo problema: l'AutomationId sul root del `DataTemplate` funziona
normalmente. Verificato dal vivo con FlaUI durante la stesura di questa suite, non da documentazione.

### Dialog e popup da conoscere

- `ConfirmDialog` (Sì/No) e `SelezionaDialog` (scelta singola/multipla con filtro) sono finestre generiche
  riusate in tutta l'app con **titolo impostato dinamicamente per ogni chiamata** — mai cercarle per titolo.
  `TestLoomApplication.WaitForWindowByContent(automationId)` le trova cercando quale finestra di primo livello
  contiene un dato AutomationId (es. `Confirm_Yes_Button`, `SelectDialog_Items_ListBox`).
- `CampiMancantiDialog` ("Campi obbligatori manca...") si apre **sempre**, oltre al banner in pagina, quando
  Salva fallisce la validazione — è modale e va chiuso (`MissingFields_Close_Button`) prima di poter
  interagire di nuovo con la finestra principale.
- Il centro notifiche interno può aprirsi da solo dopo un login/avvio se ci sono notifiche non lette,
  intercettando i click sulla sidebar sottostante. `MainShellPage.NavigateTo` lo chiude automaticamente prima
  di ogni navigazione (`DismissNotificationsIfShown`).
- La voce di sidebar già selezionata (es. "Test" mentre si è nell'editor di un Test nuovo) non genera un
  cambio pagina se ricliccata: per testare la conferma "modifiche non salvate" in navigazione serve navigare
  verso una sezione **diversa**.

### Comportamento reale da conoscere prima di scrivere un nuovo test

- `TestCaseEditorViewModel.Salva()` torna **sempre** alla Lista Test al termine di un salvataggio **riuscito**
  (mai in caso di errore di validazione, dove l'editor resta aperto). Un flow che crea un Test Case ritorna
  quindi una `TestListPage`, non l'editor.
- Un test **nuovo** mostra la striscia di schede del wizard (`Editor_Tab_0..4_Button`) ed è già in modifica
  (`InModifica=true`) dalla creazione. Un test **esistente** mostra tutte le sezioni insieme fin da subito, ma
  parte in sola lettura: serve `TestEditorPage.EnterEditMode()` (bottone "Modifica") prima di poter scrivere.
- La selezione del componente passa sempre per il dialog "Scegli componente" (nessuna ComboBox alternativa nel form),
  indipendentemente da quante componente esistono a catalogo.

## Screenshot su failure e diagnostica

Ogni `[Fact]` è scritto con `try { ... } catch (Exception ex) { CaptureFailureAndRethrow(nameof(Test), ex); }`.
`FailureArtifacts.Capture` (richiamato anche direttamente dai test che gestiscono un riavvio di TestLoom a metà,
vedi `PersistenceTests`) scrive sotto `TestResults/UIFailures/<NomeTest>/<timestamp>/`:

- `screenshot.png` — della finestra principale se ancora raggiungibile, dell'intero schermo altrimenti.
- `failure.txt` — nome test, timestamp, step, PID TestLoom, elemento cercato (AutomationId/Name/ControlType),
  elenco delle finestre di primo livello aperte, eccezione completa.
- `ui-tree.txt` — dump dell'albero UI Automation (AutomationId/Name/ControlType, profondità 6) della finestra
  principale al momento del fallimento.

`TestResults/` è già in `.gitignore` della solution: gli artifact restano locali.

## Cleanup

Ogni `TestWorkspace.Dispose()` elimina la propria cartella temporanea (best-effort: un file ancora bloccato da
un RBG - QATRIVO.exe non ancora terminato non fa fallire il test). Ogni `TestLoomApplication.Dispose()` chiude la finestra
e, se il processo non termina entro 5s, lo termina (`Kill`) — nessun test deve lasciare un RBG - QATRIVO.exe orfano che
tenga occupato il mutex single-instance per la prova successiva. Un'interruzione anomala della suite (es. Ctrl+C
durante l'esecuzione) può comunque lasciare un processo RBG - QATRIVO.exe orfano: verificarlo da Gestione attività prima
di rilanciare i test.

## Debugging di un test che fallisce in locale

1. Lancia solo quel test con `--filter "FullyQualifiedName~NomeDelTest"` e `--logger "console;verbosity=detailed"`.
2. Apri `screenshot.png` e `ui-tree.txt` sotto `TestResults/UIFailures/<NomeTest>/<ultimo timestamp>/`.
3. Se l'elemento cercato non compare in `ui-tree.txt`, verifica prima se è dentro una `ListBox` (vedi sopra) o
   se è semplicemente non ancora renderizzato (aumenta il timeout del `Wait` interessato solo se hai una
   ragione concreta, non come primo tentativo).
4. Se TestLoom non parte affatto, controlla `TestLoomProcessGuard`: un'istanza reale già aperta blocca tutto.

## Aggiungere un nuovo test

1. Se serve un nuovo controllo, aggiungi il suo `AutomationProperties.AutomationId` nell'AXAML seguendo
   `Screen_Control_Purpose`, ricompila `TestLoom.App`.
2. Se il controllo vive in una `ListBox.ItemTemplate`, leggi prima "Una scoperta empirica importante" sopra.
3. Aggiungi/estendi il Page Object dell'area interessata (mai chiamate FlaUI dirette nel test).
4. Se il flusso è multi-schermata e riusabile, valuta un Flow (`Flows/`).
5. Scrivi il test con il blocco `try/catch` + `CaptureFailureAndRethrow` per la diagnostica su failure.
6. Aggiungi almeno un `[Trait("Category", "...")]` fra quelli elencati sopra (o proponine uno nuovo solo se
   davvero necessario).
7. Esegui il test in isolamento prima di aggiungerlo alla suite completa.

## Gap residui (non ancora coperti)

- **Righe multiple nella Lista Test**: `TestListPage.OpenRowById`/il bottone "Apri" per riga condividono lo
  stesso AutomationId in ogni riga; con più di una riga visibile la risalita dalla cella Id al contenitore di
  riga più vicino è un'euristica (6 livelli di risalita nell'albero UI Automation, non garantita per ogni
  struttura di `DataGrid` virtualizzata) — funziona nei workspace di test attuali (sempre 0 o 1 riga), non
  ancora validata con più righe/scroll/virtualizzazione.
  - **Prossimo passo prioritario**: se servono test con più righe (es. selezione multipla, azioni di massa),
    verificare come Avalonia espone le righe di `DataGrid` via UIA (`GridPattern`/`TableItemPattern`) e
    stabilizzare `TestListPage.FindRowContainer` di conseguenza, invece di continuare a estendere l'euristica.
- **Griglie/liste oltre la Lista Test**: Anomalie/CR, Report, Audit, Impostazioni → sotto-pagine master data
  (Veicoli/componente/Requisiti/...) non hanno ancora Page Object né AutomationId dedicati — solo il titolo di pagina
  è verificato (`NonConformitaPage`, `ImpostazioniPage`).
- **File/path**: nessun test copre percorso invalido/inesistente, file corrotto, cartella in sola lettura,
  repository vuoto al primo avvio (FirstRunView con `EPrimoAccesso=true`, picker cartella nativo incluso) — il
  picker cartella nativo (`OnScegliCartella` in FirstRunView) è un common dialog di Windows, non automatizzato
  qui: i test bypassano la scelta cartella scrivendo `config.json` direttamente (vedi Isolamento dei dati).
- **Import Excel, esportazione (Excel/PDF/Ticketing)**: nessuna copertura.
- **Change Request, Segnalazione rete, NonConformitaEditor**: nessuna copertura (solo apertura pagina Anomalie/CR).
- **Scorciatoie da tastiera oltre Tab in login**: `Ctrl+F` (focus filtro), `Ctrl+K` (ricerca globale), `Esc`
  nei dialog non ancora testati esplicitamente (funzionano nell'app, solo non coperti qui).
- **Ruoli Standard/Viewer**: tutti i test seminano un operatore Master; permessi/restrizioni per Standard e
  Viewer non testati in questa suite (coperti invece da `TestLoom.App.Tests`, es.
  `PermessiCreazioneStandardTests`).

## Prossimi test prioritari (suggeriti)

1. `DataGrid` con più righe reale (sblocca selezione multipla, ordinamento, filtri avanzati sulla Lista Test).
2. FirstRunView al primissimo accesso (`EPrimoAccesso=true`, creazione del primo operatore/Master) — copre un
   percorso mai esercitato da `TestLoom.App.Tests` in modo end-to-end reale via UI.
3. Apertura/chiusura pagina Impostazioni → una sotto-sezione master data (es. Veicoli), con CRUD minimo.
4. Esportazione Excel dalla Lista Test o da Anomalie/CR (verifica solo che il flusso completi senza errori e
   produca un file, non il contenuto del foglio — quello resta a `TestLoom.App.Tests`).
5. Regression guard dedicato al bug storico "Anomalie/CR bloccava l'app con ~91k NC aperte" (RELEASE_NOTES
   6.2.1): fuori scope per uno smoke test leggero, ma un test `[Trait("Category","Regression")]` con un
   workspace seminato a scala (decine di migliaia di NC) e un tetto di tempo sull'apertura pagina
   chiuderebbe il cerchio fra il benchmark automatico (`TestLoom.App.Tests`) e l'interazione reale.
