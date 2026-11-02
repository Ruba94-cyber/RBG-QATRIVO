using TestLoom.App.Models;

namespace TestLoom.App.Services;

public sealed record OnboardingStep(string Icona, string Titolo, string Testo, string NavId, string TargetId = "");

/// <summary>Definisce e versiona il tour iniziale. Non scrive autonomamente il config: in questo modo la logica
/// resta testabile e il chiamante può gestire esplicitamente gli eventuali errori di persistenza.
/// Versioni: 2 = tour 6.5.x; 3 = tour 6.6.0 (Indietro, Personalizza Home e Recenti, viste salvate, conflitti fra
/// postazioni per il Master, segnalazioni a un clic). La 6.6.1 corregge solo i nomi dei pulsanti nei testi dei passi,
/// senza alzare la versione. Alzare il numero fa rivedere il tour una volta a tutti gli utenti.</summary>
public static partial class OnboardingService
{
    public const int VersioneCorrente = 3;

    public static string Chiave(string operatore, RuoloOperatore ruolo) =>
        $"{operatore.Trim()}|{ruolo}";

    public static bool ECompletato(AppConfig config, string operatore, RuoloOperatore ruolo) =>
        config.VersioniOnboardingCompletate != null &&
        config.VersioniOnboardingCompletate.TryGetValue(Chiave(operatore, ruolo), out var versione) &&
        versione >= VersioneCorrente;

    public static void SegnaCompletato(AppConfig config, string operatore, RuoloOperatore ruolo)
    {
        config.VersioniOnboardingCompletate ??= new(StringComparer.OrdinalIgnoreCase);
        config.VersioniOnboardingCompletate[Chiave(operatore, ruolo)] = VersioneCorrente;
    }

    /// <summary>Passi del tour nella lingua indicata: "it", "en" o "fr" (ogni altro valore usa l'italiano).</summary>
    public static IReadOnlyList<OnboardingStep> CreaPassi(RuoloOperatore ruolo, string lingua)
    {
        if (lingua == "fr") return CreaPassiFrancesi(ruolo);
        var inglese = lingua == "en";
        var comuni = inglese
            ? new List<OnboardingStep>
            {
                new("🚛", "Welcome to TestLoom", "This guided tour follows your permissions. You can restart it at any time from Tour in the side menu.", "home", "account-menu"),
                new("🏠", "Your operational overview", "Home shows work to follow, completed activities and indicators. Recent reopens the tests, change requests and reports you opened recently on this PC. Customize Home chooses sections and their order; the choice stays on this PC.", "home", "home-filter"),
                new("↩️", "Go back without losing your place", "← Back or Alt+← returns to the section, Test, Change Request or report you opened before. If there are unsaved changes, TestLoom asks for confirmation before changing screen.", "home", "main-indietro"),
            }
            : new List<OnboardingStep>
            {
                new("🚛", "Benvenuto in TestLoom", "Questo percorso segue i tuoi permessi. Puoi riavviarlo in qualsiasi momento da Tour nel menu laterale.", "home", "account-menu"),
                new("🏠", "La tua panoramica operativa", "La Home mostra attività da seguire, lavori conclusi e indicatori. Recenti riapre i Test, le Change Request e le segnalazioni aperti di recente su questo PC. Personalizza Home sceglie sezioni e ordine: la scelta resta su questo PC.", "home", "home-filter"),
                new("↩️", "Torna indietro senza perdere il punto", "← Indietro o Alt+← riporta alla sezione, al Test, alla Change Request o alla segnalazione aperti prima. Se ci sono modifiche non salvate, TestLoom chiede conferma prima di cambiare schermata.", "home", "main-indietro"),
            };

        comuni.AddRange(ruolo switch
        {
            RuoloOperatore.Master => inglese ? MasterEn() : MasterIt(),
            RuoloOperatore.Standard => inglese ? OperatoreEn() : OperatoreIt(),
            _ => inglese ? ViewerEn() : ViewerIt(),
        });
        return comuni;
    }

    private static IEnumerable<OnboardingStep> MasterIt() =>
    [
        new("🧪", "Progetta e governa i test", "In Test puoi creare, pianificare, assegnare e revisionare le attività. Ogni salvataggio operativo conserva una nuova Esecuzione nello storico REV. Duplica ultima operazione aggiunge un'operazione indipendente, con anteprima e annullamento.", "test", "test-new"),
        new("⚠️", "Classifica le anomalie", "Da un Test ogni segnalazione si gestisce con un clic: Crea NC oppure Collega a NC-…; Gestisci tutte applica l'azione predefinita dopo conferma. In Anomalie/CR le viste salvate riapplicano filtri e ricerca con un clic.", "anomalie", "anomaly-workflow"),
        new("📊", "Controlla risultati e copertura", "Report e Audit trasformano i dati in esportazioni, matrici Requisito×componente e indicatori di conflitto verificabili. Le esportazioni lunghe girano in background e si possono annullare.", "report", "report-export"),
        new("🔀", "Risolvi i conflitti fra postazioni", "Se due PC modificano lo stesso NC, CR o segnalazione, quel record resta bloccato in scrittura e TestLoom lo segnala. In Impostazioni → Generale, nella card Conflitti fra postazioni, scegli Mantieni versione corrente oppure Usa questa versione.", "impostazioni", "settings-conflicts"),
        new("🔩", "Configura l'ambiente", "In Impostazioni gestisci Operatori, Tipi di test, Strumenti e relativi campi, componente, veicoli, funzionalità, requisiti, archivio e preferenze condivise. Il layout della Home per il team si pubblica da Personalizza Home.", "impostazioni", "settings-sections"),
        new("✅", "Pronto come Master", "Il tuo ruolo può modificare configurazioni e dati condivisi: controlla il banner Cartella condivisa incerta, i lock e gli avvisi di file non leggibili prima di intervenire.", "home", "global-search"),
    ];

    private static IEnumerable<OnboardingStep> OperatoreIt() =>
    [
        new("🧪", "Esegui il lavoro assegnato", "In Test apri le attività, compila Dettagli e Operazioni, allega evidenze e salva. Ogni salvataggio operativo crea una nuova Esecuzione consultabile da REV. Per una ripetizione usa Ripeti; per un'operazione simile a una già inserita usa Duplica ultima operazione: è un'operazione nuova e indipendente, senza esiti né evidenze copiati.", "test", "test-filter"),
        new("📎", "Rendi il risultato verificabile", "Per ogni Operazione indica strumento, esito e motivazioni richieste; allega log, immagini o tracciati. L'esito complessivo deriva dall'operazione peggiore.", "test", "test-filter"),
        new("⚠️", "Gestisci segnalazioni e retest", "Una non conformità non diventa automaticamente anomalia: confermala con severità oppure escludila motivando. Dal Test puoi usare Crea NC o Collega a NC-… con un clic; le scelte diventano effettive al Salva del Test.", "anomalie", "anomaly-workflow"),
        new("📊", "Consulta report e audit", "Usa Report e Audit per controllare copertura, punteggi e conflitti. Le azioni disponibili restano limitate ai permessi del tuo ruolo.", "report", "report-export"),
        new("✅", "Pronto a operare", "Parti dalla Home: Recenti riapre gli ultimi elementi aperti. Usa Ctrl+K per cercare e salva sempre prima di cambiare sezione; Aiuto, notifiche e Tour restano nel menu laterale.", "home", "global-search"),
    ];

    private static IEnumerable<OnboardingStep> ViewerIt() =>
    [
        new("🔎", "Consulta e filtra i test", "In Test puoi cercare e aprire le attività per ricostruire stato, dettagli, operazioni, evidenze e revisioni senza modificare i dati.", "test", "test-filter"),
        new("⚠️", "Monitora le anomalie", "La vista Anomalie permette di seguire classificazione, severità, retest e chiusure mantenendo la consultazione in sola lettura.", "anomalie", "anomaly-workflow"),
        new("📊", "Analizza risultati e tracciabilità", "Report e Audit consentono di leggere copertura, punteggi Requisito×componente e conflitti; le esportazioni disponibili non alterano i dati condivisi.", "audit", "audit-export"),
        new("🔒", "Il tuo accesso è in sola lettura", "Puoi esplorare tutte le informazioni consentite, usare filtri e cambiare preferenze locali, ma non creare o modificare test e configurazioni condivise.", "impostazioni", "settings-sections"),
        new("✅", "Pronto a consultare", "Parti dalla Home e usa Aiuto o Tour quando serve. Se ti occorre intervenire sui dati, chiedi a un Master di adeguare il ruolo.", "home", "global-search"),
    ];

    private static IEnumerable<OnboardingStep> MasterEn() =>
    [
        new("🧪", "Design and govern tests", "In Tests you can create, plan, assign and revise activities. Every operational save preserves a new Execution in REV history. Duplicate last operation adds an independent operation, with preview and undo.", "test", "test-new"),
        new("⚠️", "Classify anomalies", "From a Test each finding is handled in one click: Create NC or Link to NC-…; Handle all applies the default action after confirmation. In Anomalies/CR saved views reapply filters and search with one click.", "anomalie", "anomaly-workflow"),
        new("📊", "Control results and coverage", "Reports and Audit turn data into exports, Requirement×component matrices and verifiable conflict indicators. Long exports run in the background and can be cancelled.", "report", "report-export"),
        new("🔀", "Resolve conflicts between workstations", "If two PCs change the same NC, CR or report, that record stays locked for writing and TestLoom flags it. In Settings → General, in the card Conflicts between workstations, choose Keep current version or Use this version.", "impostazioni", "settings-conflicts"),
        new("🔩", "Configure the environment", "Settings contains Operators, Test types, Tools and their fields, Components, vehicles, functionality, requirements, archive and shared preferences. The team Home layout is published from Customize Home.", "impostazioni", "settings-sections"),
        new("✅", "Ready as a Master", "Your role can change shared configuration and data: check the Shared folder uncertain banner, locks and unreadable-file warnings before acting.", "home", "global-search"),
    ];

    private static IEnumerable<OnboardingStep> OperatoreEn() =>
    [
        new("🧪", "Execute assigned work", "In Tests open activities, complete Details and Operations, attach evidence and save. Each operational save creates a new Execution available from REV. For a repetition use Repeat; for an operation similar to one already entered use Duplicate last operation: an independent new operation, without copied outcomes or evidence.", "test", "test-filter"),
        new("📎", "Make results verifiable", "For every Operation enter its tool, outcome and required reasons; attach logs, images or traces. The overall result follows the worst operation.", "test", "test-filter"),
        new("⚠️", "Handle reports and retests", "A non-compliance is not automatically an anomaly: confirm it with severity or exclude it with a reason. From the Test, Create NC or Link to NC-… in one click; the choices take effect when the Test is saved.", "anomalie", "anomaly-workflow"),
        new("📊", "Consult reports and audit", "Use Reports and Audit to review coverage, scores and conflicts. Available actions remain limited by your role.", "report", "report-export"),
        new("✅", "Ready to work", "Start from Home: Recent reopens the last items you opened. Use Ctrl+K to search and always save before changing section; Help, notifications and Tour remain in the side menu.", "home", "global-search"),
    ];

    private static IEnumerable<OnboardingStep> ViewerEn() =>
    [
        new("🔎", "Browse and filter tests", "In Tests you can search and open activities to reconstruct status, details, operations, evidence and revisions without changing data.", "test", "test-filter"),
        new("⚠️", "Monitor anomalies", "The Anomalies view lets you follow classification, severity, retests and closure while remaining read-only.", "anomalie", "anomaly-workflow"),
        new("📊", "Analyse results and traceability", "Reports and Audit show coverage, Requirement×componente scores and conflicts; available exports do not alter shared data.", "audit", "audit-export"),
        new("🔒", "Your access is read-only", "You can explore permitted information, use filters and change local preferences, but cannot create or edit shared tests and configuration.", "impostazioni", "settings-sections"),
        new("✅", "Ready to browse", "Start from Home and use Help or Tour whenever needed. Ask a Master to adjust your role if you need to change data.", "home", "global-search"),
    ];
}
