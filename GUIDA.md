# Guida utente di TestLoom

Questa guida spiega come installare TestLoom, preparare un'area di lavoro condivisa e usarlo ogni giorno. La versione
inglese è in [GUIDE.md](GUIDE.md).

## 1. Installazione

1. Scarica `TestLoom-<versione>-win-x64.zip` dalla pagina [Releases](../../releases) e decomprimilo dove vuoi.
2. Avvia `TestLoom.exe`: è un unico file autonomo, senza installer e senza bisogno del runtime .NET.
3. Il programma è firmato con il certificato personale dell'autore (autofirmato, soggetto "Paolo Rubagotti"). Windows può
   mostrare "Autore sconosciuto" o un avviso SmartScreen, perché il certificato non è emesso da un'autorità pubblica.
   Per far riconoscere la firma al tuo PC, importa prima il certificato pubblico:
   - confronta l'impronta SHA-256 di `Paolo-Rubagotti-CodeSigning.cer` con quella pubblicata nelle note della release e
     nel repository (`SIGNING.md`);
   - esegui `powershell -ExecutionPolicy Bypass -File .\Import-Certificate.ps1` (aggiungi `-ForAllUsers` da una finestra
     elevata per fidarti del certificato per tutti gli utenti del PC). Fidati del certificato solo dopo aver verificato
     l'impronta.
4. Controlla l'integrità del file con `SHA256SUMS.txt`, pubblicato accanto al download.

## 2. Primo avvio e cartella dei dati

Al primo avvio TestLoom chiede la **cartella dei dati**. Tutti i dati sono salvati lì come normali file.

- **Lavori da solo:** scegli una cartella vuota sul tuo disco.
- **Lavori in squadra (multi-PC):** scegli la *stessa* cartella su ogni PC: una condivisione di rete o una cartella
  mantenuta allineata da un client di sincronizzazione. Più persone possono tenerla aperta contemporaneamente.

Se la cartella è vuota puoi scegliere un **punto di partenza per il tuo tipo di collaudo** (QA software, ingegneria
hardware, qualità di produzione, laboratorio di ricerca oppure area vuota) e ottenere i primi modelli di test. Non viene
creato nulla in una cartella che contiene già dati.

`--config <cartella>` avvia il programma con una cartella di configurazione isolata (utile per provare).

## 3. Cosa puoi fare

- **Anagrafiche:** prodotti, famiglie di prodotti, componenti e funzioni; importazione da fogli di calcolo.
- **Requisiti e casi di test:** passi ordinati, campi strumento configurabili, modelli, risultati per prodotto e
  componente.
- **Esecuzioni:** registrate per operazione con un editor veloce da tastiera, evidenze e storico.
- **Anomalie e richieste di modifica:** classificazione, collegamenti ai test, cestino di recupero.
- **Audit e copertura:** matrici di copertura, dashboard e grafici.
- **Report:** esportazione Excel interattiva di elenchi, matrici e report.
- **Backup e archivio** dei dati dell'area di lavoro.
- **Lingue:** inglese, italiano e francese; si cambia dalle impostazioni.

## 4. Lavorare con più PC

- Tutti aprono la stessa cartella dei dati. Chi scrive è coordinato con blocchi e giornali, quindi due persone che
  modificano insieme non si rovinano il lavoro a vicenda.
- Un indice SQLite locale su ogni PC mantiene veloci gli archivi grandi. Si ricostruisce dai file condivisi e non li
  sostituisce mai: i file nella cartella sono i dati di riferimento.
- Se un client di sincronizzazione produce copie in conflitto, TestLoom le unisce invece di perdere modifiche.
- Usa una cartella sempre raggiungibile e fai backup regolari.

## 5. Buone pratiche

- Fai un backup prima di aggiornare TestLoom e porta alla stessa versione tutti i PC che condividono la cartella.
- Chiunque abbia accesso alla cartella può leggerne il contenuto: nomi di tester e responsabili, note, file di evidenza
  e traccia di audit.
- È una versione pubblica iniziale: considerala un'anteprima e conserva i backup.

## 6. Risoluzione dei problemi

- *Windows avvisa di un autore sconosciuto:* vedi il punto 3 dell'installazione.
- *Il programma non apre la cartella:* controlla di avere i permessi di scrittura e che la condivisione sia raggiungibile.
- *Un secondo PC non vede ancora i dati nuovi:* attendi la fine della sincronizzazione e riapri la pagina.
- *Qualcosa non torna:* chiudi il programma, copia la cartella dei dati e apri una segnalazione su GitHub indicando la
  versione e cosa hai fatto.

## 7. Licenza e crediti

PolyForm Shield 1.0.0; vedi [LICENSE](LICENSE) e [NOTICE](NOTICE). Basato su TestLoom di Paolo Rubagotti.
