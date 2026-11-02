using CommunityToolkit.Mvvm.ComponentModel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Schermata di aiuto/legenda richiamabile in ogni momento (§7.10).</summary>
public class HelpSection
{
    public string Titolo { get; init; } = "";
    public string Testo { get; init; } = "";
}

public partial class HelpViewModel : ViewModelBase
{
    private readonly Func<RuoloOperatore> _ruolo;
    [ObservableProperty] private string ricerca = "";
    [ObservableProperty] private string contesto = "home";

    public HelpViewModel(Func<RuoloOperatore>? ruolo = null)
    {
        _ruolo = ruolo ?? (() => RuoloOperatore.Master);
        LocalizationService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LocalizationService.EInglese) or nameof(LocalizationService.Lingua))
            {
                OnPropertyChanged(nameof(Sezioni));
                OnPropertyChanged(nameof(TitoloContestuale));
                OnPropertyChanged(nameof(NessunRisultato));
            }
        };
    }

    public string TitoloContestuale => string.Format(
        LocalizationService.Instance.Traduci("GuidaContestoFmt"),
        LocalizationService.Instance.Traduci(Contesto switch
        {
            "test" => "NavTest", "anomalie" => "NavAnomalie", "report" => "NavReport",
            "audit" => "NavAudit", "impostazioni" => "NavImpostazioni", _ => "NavHome"
        }));

    public List<HelpSection> Sezioni
    {
        get
        {
            var baseList = SezioniDi(LocalizationService.Instance.Lingua);
            var ruolo = _ruolo();
            var query = Ricerca.Trim();
            return baseList.Select((sezione, indice) => (sezione, indice))
                .Where(x => VisibilePerRuolo(x.indice, ruolo))
                .Where(x => query.Length == 0 || x.sezione.Titolo.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || x.sezione.Testo.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(x => PrioritaContesto(x.indice, Contesto))
                .ThenBy(x => x.indice)
                .Select(x => x.sezione).ToList();
        }
    }

    public bool NessunRisultato => Sezioni.Count == 0;

    partial void OnRicercaChanged(string value)
    {
        OnPropertyChanged(nameof(Sezioni));
        OnPropertyChanged(nameof(NessunRisultato));
    }

    partial void OnContestoChanged(string value)
    {
        OnPropertyChanged(nameof(Sezioni));
        OnPropertyChanged(nameof(TitoloContestuale));
    }

    public void AggiornaRuolo()
    {
        OnPropertyChanged(nameof(Sezioni));
        OnPropertyChanged(nameof(NessunRisultato));
    }

    public void Reset()
    {
        Ricerca = "";
        Contesto = "home";
        AggiornaRuolo();
    }

    private static bool VisibilePerRuolo(int indice, RuoloOperatore ruolo) => ruolo switch
    {
        RuoloOperatore.Master => true,
        RuoloOperatore.Standard => indice is not 3 and not 14,
        // Indici: 17 scorciatoie, 19 cartella incerta/visualizzatore, 20 bozze (sola lettura), 21 notifiche.
        // 18 (conflitti fra postazioni) resta fuori dal Viewer: non può risolvere i conflitti.
        RuoloOperatore.Viewer => indice is 0 or 4 or 5 or 7 or 8 or 9 or 10 or 11 or 17 or 19 or 20 or 21,
        _ => false
    };

    private static int PrioritaContesto(int indice, string contesto) => contesto switch
    {
        "test" when indice is 1 or 2 or 3 or 5 or 6 or 7 or 8 or 9 or 12 or 13 => 0,
        "anomalie" when indice is 10 => 0,
        "report" or "audit" when indice is 4 or 9 or 10 => 0,
        "impostazioni" when indice is 0 or 3 or 14 or 18 or 19 or 20 => 0,
        "home" when indice is 11 => 0,
        _ => 1
    };

    private static readonly List<HelpSection> SezioniIt = new()
    {
        new HelpSection
        {
            Titolo = "Accesso",
            Testo = "All'avvio scegli il tuo nome operatore e inserisci il PIN. Per cambiare operatore (o a fine " +
                    "turno su un PC condiviso) usa \"Esci\" in fondo al menu laterale: torna alla schermata di " +
                    "accesso senza chiudere l'app. Se hai dimenticato il PIN, usa il codice di recupero dalla " +
                    "schermata di accesso; in alternativa un Master può rigenerarlo da Impostazioni → Operatori."
        },
        new HelpSection
        {
            Titolo = "Creare un test",
            Testo = "Il primo passo è scegliere il Tipo di test tra quelli configurati da un Master " +
                    "(Impostazioni → Tipi di test): non è un elenco fisso, ogni Tipo di test determina quali " +
                    "campi, sezioni Dettagli, Riferimenti e strumenti preselezionati compariranno nel resto del " +
                    "form. Un test nuovo si compila poi una scheda alla volta — Pianificazione, Dettagli, Prove, " +
                    "Allegati, Riferimenti — con Avanti/Indietro per muoversi in sequenza; Salva compare solo " +
                    "sull'ultima scheda. componente, veicolo/banco, matricola, assegnatario e descrizione sono in genere " +
                    "obbligatori, ma dipende da come il Master ha configurato quel Tipo di test. Aggiungi quindi " +
                    "una o più Operazioni, ciascuna con lo strumento usato e il proprio esito. Riaprendo poi il " +
                    "test già salvato, tutto il form torna a mostrarsi insieme come un'unica pagina, senza schede."
        },
        new HelpSection
        {
            Titolo = "Operazioni ed Esecuzioni",
            Testo = "Ogni salvataggio dei Dettagli o delle Operazioni crea una nuova Esecuzione (revisione), " +
                    "navigabile dal menu REV in alto — le Esecuzioni precedenti restano in sola lettura. " +
                    "Un'Esecuzione contiene una o più Operazioni, ciascuna con il proprio esito (Conforme, Non " +
                    "Conforme, Conforme con deviazioni, Non applicabile...): l'esito complessivo dell'Esecuzione " +
                    "si calcola sempre dalla peggiore Operazione."
        },
        new HelpSection
        {
            Titolo = "Campi di sistema e Riferimenti",
            Testo = "I campi che compaiono nel form (componente, Veicolo, Funzionalità, Strumenti usati...) vengono da " +
                    "un catalogo condiviso, gestibile da un Master in \"Gestione campi di sistema\" " +
                    "(Impostazioni → Tipi di test → Gestisci campi): un Master può rinominare l'etichetta di un " +
                    "campo esistente, cambiarne il tipo (se non ha ancora dati salvati) o crearne di nuovi. La " +
                    "card Riferimenti (Requisiti/Test/Ticket collegati) si mostra o nasconde per Tipo di test allo " +
                    "stesso modo."
        },
        new HelpSection
        {
            Titolo = "Punteggio di conformità e Audit",
            Testo = "Se un test ha uno o più Requisiti collegati, ogni Esecuzione mostra per ciascuno un " +
                    "punteggio 0-5/N.A. (di norma calcolato dall'esito, sempre correggibile a mano indicando un " +
                    "motivo). Il pannello Audit nel menu laterale riassume questi punteggi in una matrice " +
                    "Requisito×componente per Veicolo, segnalando eventuali conflitti quando due test in " +
                    "disaccordo coprono la stessa coppia."
        },
        new HelpSection
        {
            Titolo = "Stato del lavoro ed esito tecnico",
            Testo = "Lo stato del lavoro (Pianificato, In corso, In attesa, Da ritestare, Concluso...) è separato " +
                    "dall'esito tecnico della singola prova. Conforme, Non Conforme, Conforme con deviazioni e " +
                    "Non applicabile non cambiano automaticamente lo stato del test. N.A. richiede sempre una motivazione."
        },
        new HelpSection
        {
            Titolo = "Assegnazione",
            Testo = "Chi esegue un test ne diventa automaticamente l'assegnatario ad ogni nuova revisione, anche " +
                    "se in precedenza era assegnato a qualcun altro: un Master può assegnare un test a un " +
                    "collega, ma se un terzo se lo prende ed esegue, l'assegnazione passa a lui. Solo un Master " +
                    "può assegnare manualmente un test a qualcun altro da sé; gli altri operatori se lo prendono " +
                    "semplicemente lavorandoci."
        },
        new HelpSection
        {
            Titolo = "Verifiche e prove",
            Testo = "Una funzionalità contiene verifiche riutilizzabili. Un test può includere più verifiche e " +
                    "ogni verifica più prove: ad esempio UnitLogger a veicolo e ValidationTool a banco con risultati distinti. " +
                    "Per UnitLogger la versione è obbligatoria; per i tool ingegneristici è opzionale."
        },
        new HelpSection
        {
            Titolo = "Test correlati",
            Testo = "Quando un secondo test (es. a veicolo) copre lo stesso requisito/funzionalità già testato " +
                    "altrove (es. a banco), l'app propone il collegamento come test correlato. Un test può essere " +
                    "collegato a più di un altro test."
        },
        new HelpSection
        {
            Titolo = "Evidenze e Ticketing",
            Testo = "Sono supportati immagini, PDF, testo/CSV, ZIP e tracciati diagnostici; i duplicati identici " +
                    "sono riconosciuti tramite hash. Ticketing resta esterno: TestLoom non usa API o credenziali. " +
                    "Il pacchetto manuale contiene TXT, PDF, manifest ed evidenze; dopo l'apertura in Ticketing collega TICKET e URL."
        },
        new HelpSection
        {
            Titolo = "Segnalazioni e anomalie",
            Testo = "Una prova Non Conforme o con deviazioni crea una segnalazione da classificare, mai un'anomalia " +
                    "definitiva automatica. Dal Test la segnalazione si gestisce con un clic: «Crea NC» apre una nuova " +
                    "non conformità con la severità già indicata; «Collega a NC-…» compare quando un'anomalia aperta è " +
                    "chiaramente la stessa. «Gestisci tutte» applica l'azione predefinita: collega le segnalazioni con un'anomalia " +
                    "suggerita e crea le NC per quelle con severità; le altre restano da classificare. Crea e collega diventano " +
                    "effettivi al Salva del Test. Nel dettaglio di una non conformità, dopo un conflitto di salvataggio, " +
                    "«Ricarica versione corrente» carica la versione dell'altra postazione: il tuo input resta nel dialogo e in una " +
                    "bozza locale. La chiusura in TestLoom è manuale e richiede un motivo; non chiude mai Ticketing."
        },
        new HelpSection
        {
            Titolo = "Home",
            Testo = "Le tabelle \"Da fare\" e \"Fatti\" mostrano i test che hanno bisogno di attenzione e gli " +
                    "ultimi conclusi con successo. Se sei un operatore Standard con Gamma veicolari assegnate, vedi " +
                    "solo i test di quelle Gamma. La dashboard ha 5 filtri sempre visibili più \"Altri filtri\" per " +
                    "il resto, e un pannello a destra per scegliere quali grafici mostrare insieme. «Personalizza Home» " +
                    "sceglie quali sezioni mostrare (Riepilogo, Avvisi, Indicatori, Dashboard e grafici, Da fare e fatti, " +
                    "Recenti) e in che ordine: la scelta resta su questo PC. Gli Avvisi compaiono solo quando c'è qualcosa " +
                    "da segnalare; Indicatori include le Change Request aperte. «Recenti» elenca fino a 15 tra Test, Change " +
                    "Request e segnalazioni aperti di recente su questo PC. Un Master può pubblicare un layout per il team con " +
                    "«Pubblica come layout del team»: i PC senza un layout personale lo usano, quelli con un layout personale non " +
                    "cambiano. Ogni operatore può applicarlo con «Usa layout del team», e «Ripristina predefinito» torna " +
                    "all'ordine di fabbrica."
        },
        new HelpSection
        {
            Titolo = "Azioni di massa e viste salvate",
            Testo = "Nella Lista Test, selezionando più righe (Ctrl+click o Shift+click) compare una barra con " +
                    "Assegna a…, Cambia stato… e Archivia, per agire su tutte le righe scelte con un solo " +
                    "comando invece di aprirle una per una. Le viste salvate sono disponibili in Lista Test, in Anomalie/CR " +
                    "e in WU Status: una combinazione di filtri, ricerca e ordinamento si salva con un nome " +
                    "(\"💾 Salva vista attuale\") e si riapplica dal menu \"Viste salvate\". Viste, larghezze delle colonne e " +
                    "ordinamento scelto con un clic sull'intestazione restano su questo PC, nelle tabelle che lo consentono; " +
                    "la Lista Test mantiene un ordine fisso."
        },
        new HelpSection
        {
            Titolo = "Salvataggio",
            Testo = "Il salvataggio è sempre manuale (pulsante Salva, o Ctrl+S da tastiera quando disponibile). " +
                    "Se provi a lasciare una schermata — cambiando sezione, tornando alla lista o con ← Indietro, " +
                    "chiudendo la finestra o scegliendo Esci dal menu dell'icona nell'area di notifica — con modifiche " +
                    "non salvate, l'app chiede conferma prima di procedere e non scarta le modifiche senza che tu lo scelga."
        },
        new HelpSection
        {
            Titolo = "Lock in modifica",
            Testo = "Aprire un test o un file master in sola lettura è sempre libero. Il lock scatta solo quando " +
                    "inizi a modificare un campo, e si sblocca da solo dopo inattività prolungata. Se vedi un lock " +
                    "che sai essere \"orfano\" (es. un collega ha chiuso il PC senza salvare), puoi forzarne lo " +
                    "sblocco: l'app mostra sempre chi lo detiene e da quando, e chiede conferma prima di rompere " +
                    "il lavoro di qualcun altro."
        },
        new HelpSection
        {
            Titolo = "Archivio",
            Testo = "Archiviare un test o una riga di un elenco master data (Requisiti, componente, Strumenti, Tipi di " +
                    "test...) non la cancella mai subito: la sposta nell'Archivio (Impostazioni), da cui sparisce " +
                    "dalla tabella di lavoro di origine ma resta sempre ripristinabile. Da lì un Master può anche " +
                    "eliminarla definitivamente, ma solo se non è più referenziata da nulla."
        },
        new HelpSection
        {
            Titolo = "Log diagnostici (in caso di problemi)",
            Testo = "TestLoom tiene sul PC (mai nella cartella condivisa) tre log locali e circolari: gli errori " +
                    "(crash.log), le operazioni applicative principali anche quando vanno a buon fine (trace.log, " +
                    "utile anche per un comportamento sbagliato senza un vero errore) e la telemetria delle " +
                    "prestazioni. In Impostazioni → Pacchetto diagnostico trovi il percorso della cartella con un " +
                    "link per aprirla direttamente, e un bottone per esportare tutti e tre i log in un unico file " +
                    "zip da allegare a una segnalazione."
        },
        // Indici 17-21: aggiunti in 6.6.0 in coda, così non spostano gli indici usati dalle regole di ruolo.
        new HelpSection
        {
            Titolo = "Scorciatoie da tastiera",
            Testo = "Ctrl+K apre la ricerca globale su Test, anomalie, CR, segnalazioni, veicoli e strumenti; per i Master " +
                    "include anche componente, requisiti e funzionalità. Invio apre il primo risultato. Ctrl+S salva, Ctrl+N crea " +
                    "un nuovo elemento della pagina e Ctrl+F porta il cursore al filtro principale. Ctrl+1…9, anche con il " +
                    "tastierino numerico, passa alla sezione corrispondente del menu laterale. Alt+← torna alla posizione " +
                    "precedente, con la stessa conferma del pulsante ← Indietro. Esc chiude la ricerca, le notifiche o l'aiuto " +
                    "aperti; altrimenti annulla la modifica in corso, dopo conferma."
        },
        new HelpSection
        {
            Titolo = "Conflitti fra postazioni",
            Testo = "Se due PC salvano lo stesso NC, CR o segnalazione partendo dalla stessa versione con esiti diversi " +
                    "(anche una modifica contro un'eliminazione), TestLoom blocca in scrittura solo quel record e lo segnala ai " +
                    "Master. Il Master lo risolve in Impostazioni → Generale, nella card «Conflitti fra postazioni — NC, CR e " +
                    "Segnalazioni»: «Mantieni versione corrente» oppure «Usa questa versione». La scelta riguarda l'intera " +
                    "versione, non singoli campi; le altre versioni restano consultabili. Gli altri record restano modificabili. " +
                    "I cataloghi master hanno una card analoga, «Conflitti nei cataloghi master». Il rilevamento legge i log di " +
                    "sincronizzazione: una modifica presente solo su un PC il cui log non è ancora arrivato agli altri non è " +
                    "visibile finché il log non si sincronizza."
        },
        new HelpSection
        {
            Titolo = "Cartella condivisa incerta e visualizzatore locale",
            Testo = "TestLoom valuta lo stato della cartella condivisa con segnali solo locali. Se la cartella risulta incerta " +
                    "(per esempio radice lenta, file chiave illeggibili o copie di conflitto recenti), compare il banner " +
                    "«Cartella condivisa incerta». Il banner non blocca le modifiche e non significa che OneDrive sia " +
                    "sincronizzato: prima di salvare verifica OneDrive/SharePoint, oppure attiva il visualizzatore locale. " +
                    "Il visualizzatore blocca le nuove modifiche condivise su questo PC finché non lo disattivi e non tocca le " +
                    "bozze locali. Si attiva dal banner o da Impostazioni → Generale → Cartella condivisa, sempre con conferma. " +
                    "Se la cartella non è raggiungibile, TestLoom blocca già le scritture e lascia consultabili i dati."
        },
        new HelpSection
        {
            Titolo = "Bozze di emergenza e recupero",
            Testo = "Se il salvataggio di una Change Request, di una non conformità o di una segnalazione di rete fallisce, " +
                    "TestLoom conserva una copia locale del contenuto fuori da OneDrive, con identificativo e revisione attesa. " +
                    "Le bozze non vengono mai pubblicate automaticamente. In Impostazioni → Generale → Backup dei dati, " +
                    "«Esamina bozze (sola lettura)» elenca le bozze con anteprima, più recenti per prime, e «Apri bozze locali " +
                    "di emergenza» apre la cartella. Dall'editor, «Recupera bozza locale…» confronta la bozza con la versione " +
                    "corrente e importa, dopo conferma, solo i campi modificati nella bozza e rimasti invariati localmente; i " +
                    "campi cambiati da entrambe le parti non sono selezionabili. Il salvataggio resta quello normale, con i suoi controlli."
        },
        new HelpSection
        {
            Titolo = "Notifiche e avviso Windows",
            Testo = "Le notifiche non lette si vedono nel menu dell'operatore (il pulsante con nome e 🔔). In Impostazioni → " +
                    "Generale → Avvio e notifiche locali scegli quali tipi contano nel contatore: un tipo deselezionato resta nel " +
                    "centro notifiche ma non conta nel badge né nell'avviso Windows, e nessuna notifica viene eliminata. I " +
                    "conflitti di sincronizzazione dati sono sempre conteggiati. Con «Abilita notifiche Windows quando supportate» " +
                    "attivo, una nuova notifica arrivata mentre TestLoom è in secondo piano fa lampeggiare il pulsante nella barra " +
                    "delle applicazioni, e l'icona nell'area di notifica mostra il numero di notifiche non lette."
        },
    };

    private static readonly List<HelpSection> SezioniEn = new()
    {
        new HelpSection
        {
            Titolo = "Login",
            Testo = "On startup choose your operator name and enter your PIN. To change operator (or at the end " +
                    "of a shift on a shared PC) use \"Log out\" at the bottom of the side menu: it returns to the " +
                    "login screen without closing the app. If you forgot your PIN, use your recovery code from " +
                    "the login screen; alternatively a Master can regenerate it from Settings → Operators."
        },
        new HelpSection
        {
            Titolo = "Creating a test",
            Testo = "The first step is choosing the Test type among the ones a Master has configured " +
                    "(Settings → Test types): it's not a fixed list, each Test type determines which fields, " +
                    "Details sections, References and preselected instruments appear in the rest of the form. " +
                    "A new test is then filled in one tab at a time — Planning, Details, Runs, Attachments, " +
                    "References — with Back/Next to move through them in sequence; Save only appears on the " +
                    "last tab. component, vehicle/bench, serial, assignee and description are usually required, but it " +
                    "depends on how the Master configured that Test type. Then add one or more Operations, each " +
                    "with the instrument used and its own outcome. Reopening the saved test later shows the " +
                    "whole form together again as a single page, with no tabs."
        },
        new HelpSection
        {
            Titolo = "Operations and Executions",
            Testo = "Every time Details or Operations are saved, a new Execution (revision) is created, " +
                    "browsable from the REV menu at the top — earlier Executions stay read-only. An Execution " +
                    "contains one or more Operations, each with its own outcome (Compliant, Non-compliant, " +
                    "Compliant with deviations, Not applicable...): the Execution's overall outcome is always " +
                    "computed from the worst Operation."
        },
        new HelpSection
        {
            Titolo = "System fields and References",
            Testo = "The fields shown in the form (component, Vehicle, Functionality, Instruments used...) come from " +
                    "a shared catalog, manageable by a Master in \"System field management\" (Settings → Test " +
                    "types → Manage fields): a Master can rename an existing field's label, change its type (if " +
                    "it has no saved data yet) or create new ones. The References card (linked " +
                    "Requirements/Tests/Tickets) is shown or hidden per Test type the same way."
        },
        new HelpSection
        {
            Titolo = "Conformity score and Audit",
            Testo = "If a test has one or more linked Requirements, each Execution shows a 0-5/N.A. score for " +
                    "each one (normally computed from the outcome, always manually overridable with a reason). " +
                    "The Audit panel in the side menu summarizes these scores in a Requirement×componente matrix per " +
                    "vehicle, flagging any conflicts when two disagreeing tests cover the same pair."
        },
        new HelpSection
        {
            Titolo = "Work status and technical outcome",
            Testo = "The work status (Planned, In progress, Waiting, To retest, Completed...) is separate from " +
                    "the technical outcome of the single run. Compliant, Non-compliant, Compliant with deviations " +
                    "and Not applicable do not automatically change the test status. N/A always requires a justification."
        },
        new HelpSection
        {
            Titolo = "Assignment",
            Testo = "Whoever runs a test automatically becomes its assignee on each new revision, even if it was " +
                    "previously assigned to someone else: a Master can assign a test to a colleague, but if a " +
                    "third person picks it up and runs it, the assignment passes to them. Only a Master can " +
                    "manually assign a test to someone else; other operators simply pick it up by working on it."
        },
        new HelpSection
        {
            Titolo = "Checks and runs",
            Testo = "A functionality contains reusable checks. A test can include multiple checks and each check " +
                    "multiple runs: for example UnitLogger on vehicle and ValidationTool on bench with separate results. For UnitLogger " +
                    "the version is required; for engineering tools it's optional."
        },
        new HelpSection
        {
            Titolo = "Related tests",
            Testo = "When a second test (e.g. on vehicle) covers the same requirement/functionality already " +
                    "tested elsewhere (e.g. on bench), the app suggests linking it as a related test. A test can " +
                    "be linked to more than one other test."
        },
        new HelpSection
        {
            Titolo = "Evidence and Ticketing",
            Testo = "Images, PDF, text/CSV, ZIP and diagnostic traces are supported; identical duplicates are " +
                    "recognized via hash. Ticketing stays external: TestLoom uses no API or credentials. The manual " +
                    "package contains TXT, PDF, manifest and evidence; after opening in Ticketing, link the TICKET and URL."
        },
        new HelpSection
        {
            Titolo = "Reports and anomalies",
            Testo = "A Non-compliant or deviated run creates a report to classify, never an automatic definitive " +
                    "anomaly. From the Test it can be handled in one click: \"Create NC\" opens a new non-compliance with the " +
                    "severity already set; \"Link to NC-…\" appears when an open anomaly is clearly the same. \"Handle all\" " +
                    "applies the default action: it links reports to a suggested anomaly and creates NCs for those with a " +
                    "severity; the others stay to classify. Create and link take effect when the Test is saved. In a " +
                    "non-compliance, after a save conflict, the button \"Ricarica versione corrente\" (label in Italian) loads " +
                    "the version saved from the other workstation; your input stays in the dialog and in a local draft. " +
                    "Closing in TestLoom is manual and requires a reason; it never closes Ticketing."
        },
        new HelpSection
        {
            Titolo = "Home",
            Testo = "The \"To do\" and \"Done\" tables show tests that need attention and the latest ones " +
                    "completed successfully. If you're a Standard operator with assigned vehicle ranges, you only " +
                    "see tests from those ranges. The dashboard has 5 always-visible filters plus \"More filters\" " +
                    "for the rest, and a panel on the right to choose which charts to show together. \"Customize Home\" " +
                    "chooses which sections to show (Summary, Warnings, Indicators, Dashboard and charts, To do and done, " +
                    "Recent) and their order; the choice stays on this PC. Warnings appear only when there is something to " +
                    "report; Indicators include open Change Requests. \"Recent\" lists up to 15 Tests, Change Requests and " +
                    "findings opened recently on this PC. A Master can publish a team layout with \"Publish as team layout\": " +
                    "PCs without a personal layout use it, PCs with a personal layout keep theirs. Each operator can apply it " +
                    "with \"Use team layout\", and \"Restore default\" returns to the factory order."
        },
        new HelpSection
        {
            Titolo = "Bulk actions and saved views",
            Testo = "In the Test list, selecting several rows (Ctrl+click or Shift+click) shows a bar with Assign to…, " +
                    "Change status… and Archive, to act on all selected rows with one command. Saved views are available in " +
                    "the Test list, Anomalies/CR and WU Status: a combination of filters, search and sorting is saved with a " +
                    "name (\"💾 Save current view\") and reapplied from the \"Saved views\" menu. Views, column widths and the " +
                    "sorting chosen by clicking a header stay on this PC, in the tables that allow it; the Test list keeps a " +
                    "fixed order."
        },
        new HelpSection
        {
            Titolo = "Saving",
            Testo = "Saving is always manual (Save button, or Ctrl+S when available). If you try to leave a screen — " +
                    "changing section, returning to the list, using ← Back, closing the window or choosing \"Esci\" (Exit) " +
                    "from the notification-area icon — with unsaved changes, the app asks for confirmation before continuing " +
                    "and does not discard your changes unless you choose to."
        },
        new HelpSection
        {
            Titolo = "Edit lock",
            Testo = "Opening a test or a master file read-only is always free. The lock kicks in only when you " +
                    "start editing a field, and releases itself after prolonged inactivity. If you see a lock you " +
                    "know to be \"orphaned\" (e.g. a colleague closed their PC without saving), you can force its " +
                    "unlock: the app always shows who holds it and since when, and asks for confirmation before " +
                    "breaking someone else's work."
        },
        new HelpSection
        {
            Titolo = "Archive",
            Testo = "Archiving a test or a row from a master data list (Requirements, Components, Instruments, Test " +
                    "types...) never deletes it right away: it moves it to the Archive (Settings), where it " +
                    "disappears from its source working table but stays always restorable. From there a Master " +
                    "can also delete it permanently, but only if it's no longer referenced by anything."
        },
        new HelpSection
        {
            Titolo = "Diagnostic logs (in case of problems)",
            Testo = "TestLoom keeps three local, circular logs on the PC (never in the shared folder): errors " +
                    "(crash.log), the main application operations even when they succeed (trace.log, useful even " +
                    "for a wrong behavior with no actual error), and performance telemetry. In Settings → " +
                    "Diagnostic package you'll find the log folder's path with a link to open it directly, and a " +
                    "button to export all three logs into a single zip file to attach to a problem report."
        },
        // Indices 17-21 (added in 6.6.0 at the end, so the role-rule indices do not shift).
        new HelpSection
        {
            Titolo = "Keyboard shortcuts",
            Testo = "Ctrl+K opens global search across Tests, anomalies, change requests, findings, vehicles and instruments; " +
                    "for Masters it also covers Components, requirements and functionality. Enter opens the first result. Ctrl+S saves, " +
                    "Ctrl+N creates a new item on the current page and Ctrl+F moves the cursor to the main filter. Ctrl+1…9, " +
                    "including the numeric keypad, switches to the matching section of the side menu. Alt+← returns to the " +
                    "previous position, with the same confirmation as the ← Back button. Esc closes the open search, " +
                    "notifications or help; otherwise it cancels the edit in progress, after confirmation."
        },
        new HelpSection
        {
            Titolo = "Conflicts between workstations",
            Testo = "If two PCs save the same NC, CR or finding starting from the same version with different results " +
                    "(including a change against a deletion), TestLoom blocks writing only for that record and flags it to Masters. " +
                    "A Master resolves it in Settings → General, in the card \"Conflitti fra postazioni — NC, CR e Segnalazioni\" " +
                    "(labels in Italian): \"Mantieni versione corrente\" or \"Usa questa versione\". The choice applies to the " +
                    "whole version, not to single fields; the other versions remain viewable. Other records stay editable. " +
                    "Master catalogs have a similar card, \"Conflitti nei cataloghi master\". Detection reads the synchronization " +
                    "logs: a change that exists only on a PC whose log has not reached the others is not visible until that log " +
                    "synchronizes."
        },
        new HelpSection
        {
            Titolo = "Shared folder uncertain and local viewer",
            Testo = "TestLoom evaluates the state of the shared folder using local signals only. If the folder is uncertain (for " +
                    "example a slow or unreachable root, unreadable key files or recent conflict copies), the banner \"Shared " +
                    "folder uncertain\" appears. The banner does not block changes and does not mean that OneDrive is synchronized: " +
                    "before saving, check OneDrive/SharePoint, or turn on the local viewer. The local viewer blocks new shared " +
                    "changes on this PC until you turn it off, and does not touch local drafts. It is turned on from the banner or " +
                    "from Settings → General → Shared folder (labels in Italian), always with confirmation. If the folder cannot be " +
                    "reached, TestLoom already blocks writes and keeps data viewable."
        },
        new HelpSection
        {
            Titolo = "Emergency drafts and recovery",
            Testo = "If saving a Change Request, a non-compliance or a network report fails, TestLoom keeps a local copy of the " +
                    "content outside OneDrive, with its identifier and expected revision. Drafts are never published automatically. " +
                    "In Settings → General → Data backup, \"Esamina bozze (sola lettura)\" lists the drafts with a preview, most " +
                    "recent first, and \"Apri bozze locali di emergenza\" opens the folder (labels in Italian). From the editor, " +
                    "\"Recupera bozza locale…\" compares the draft with the current version and, after confirmation, imports only " +
                    "the fields changed in the draft and unchanged locally; fields changed on both sides cannot be selected. " +
                    "Saving remains the normal save, with its checks."
        },
        new HelpSection
        {
            Titolo = "Notifications and Windows alert",
            Testo = "Unread notifications are shown in the operator menu (the button with your name and 🔔). In Settings → General → " +
                    "Avvio e notifiche locali (startup and local notifications) you choose which types count in the counter: an " +
                    "unselected type stays in the notification centre but does not count in the badge or the Windows alert, and no " +
                    "notification is deleted. Synchronization conflicts on shared data are always counted. With \"Abilita notifiche " +
                    "Windows quando supportate\" on, a new notification arriving while TestLoom is in the background makes the taskbar " +
                    "button flash, and the notification-area icon shows the number of unread notifications."
        },
    };
}
