using TestLoom.App.Services;

namespace TestLoom.App.Models;

/// <summary>Valore 0 = default(Esito): una Condizione appena creata nasce "Da eseguire" senza bisogno di un
/// Esito nullable in giro per tutto il codice (decisione utente 6.4.0, punto 1). Gli enum sono serializzati
/// come stringa (vedi JsonIo.Options → JsonStringEnumConverter), quindi l'ordine qui non incide sul JSON
/// esistente: solo il nome conta. Un client 6.3.x che legge "DaEseguire" non lo riconosce e lancerebbe
/// un'eccezione in deserializzazione — per questo va di pari passo con l'innalzamento di
/// TestCase.CurrentSchemaVersion/deploy/version-manifest.json.SchemaDatiMassimo, che porta quei client in
/// sola lettura prima che possano incontrare il nuovo valore (vedi CompatibilitaVersioneService).
///
/// VINCOLO (review 6.4.0): è VIETATO persistere il valore intero (int)Esito in un formato non ricostruibile
/// — cioè fuori da una proiezione locale che si può sempre ributtare via e rigenerare dal JSON autorevole
/// (oggi: le colonne "outcome"/"current_outcome" ecc. dell'indice SQLite locale, schema v6). La rinumerazione
/// fatta qui (DaEseguire=0 al posto di Conforme=0) è sicura SOLO perché quell'indice si ricostruisce sempre
/// da zero a ogni bump di LocalTestIndexStore.CurrentSchemaVersion — vedi RecomputeOperationCounts. Il JSON
/// autorevole condiviso non ha mai sofferto di questo, perché usa JsonStringEnumConverter (nomi, non interi).
/// Qualunque nuovo storage che persista (int)Esito FUORI da un indice/cache così rigenerabile (es. un export
/// binario, un file di log strutturato riletto in futuro, una tabella "storica" mai ricostruita) romperebbe
/// silenziosamente il proprio significato alla prossima rinumerazione dell'enum: non farlo, o accoppia quel
/// riferimento a un TestCase.SchemaVersion esplicito e a una migrazione dedicata, come qui.</summary>
public enum Esito
{
    DaEseguire = 0,
    Conforme = 1,
    NonConforme = 2,
    ConformeConDeviazioni = 3,
    NonApplicabile = 4,
    // Valori legacy: letti dai JSON v1 ma non proposti per le nuove prove.
    NonEseguitoNA = 5,
    DaRivedere = 6
}

/// <summary>Unica definizione di "operazione eseguita" (decisione utente 6.4.0, punto 1): un'operazione conta
/// nei conteggi operativi/copertura/esito aggregato solo quando ha ricevuto un vero esito di verifica. N.A.
/// e "Da eseguire" (e i legacy NonEseguitoNA/DaRivedere, mai più proposti ma ancora leggibili) restano fuori
/// dal conteggio: sono, rispettivamente, "non applicabile" e "non ancora fatto", non un tentativo svolto.</summary>
public static class EsitoSemantica
{
    public static bool EEseguito(this Esito esito) => esito is Esito.Conforme or Esito.NonConforme or Esito.ConformeConDeviazioni;
}

/// <summary>Punteggio di conformità 0-5 + N.A. usato per l'audit: distinto da Esito (che resta l'unico
/// input per il workflow delle Non Conformità) — questo è un secondo giudizio, più granulare, applicato
/// solo quando l'esecuzione ha almeno un Requisito collegato.</summary>
public enum PunteggioConformita
{
    NonConforme = 0,
    ScarsamenteConforme = 1,
    ParzialmenteConforme = 2,
    ModeratamenteConforme = 3,
    AmpiamenteConforme = 4,
    Conforme = 5,
    NonApplicabile = 6,
}

/// <summary>Passo di una operazione guasto (Test Banco basato su requisito/componente/guasto, punto 7 della 6.5.0): ogni guasto
/// dell'anagrafica ha due passi distinti, validazione e devalidazione. Persistito come stringa (JsonStringEnumConverter).</summary>
public enum PassoDtc
{
    Validazione = 0,
    Devalidazione = 1
}

/// <summary>Origine di una REV (chiusura anomalia con nuova REV): Operatore = REV normale eseguita da un
/// operatore (valore 0 = default, compatibilita con i JSON legacy senza il campo); Rete = esito deciso dalla rete
/// assistenziale/problema non nostro, nessun operatore esecutore reale (il campo Operatore della REV e solo
/// audit); RieseguitoDaNoi = ritest eseguito da noi alla chiusura. Serializzato come stringa (JsonStringEnumConverter).
/// Richiede TestCase.CurrentSchemaVersion 7: un client 6.4.x non conosce il campo e lo perderebbe riscrivendo la REV.</summary>
public enum OrigineRevisione
{
    Operatore = 0,
    Rete = 1,
    RieseguitoDaNoi = 2
}

public enum StatoLavoroTest
{
    Pianificato,
    InCorso,
    DaRivedere,
    InAttesa,
    DaRitestare,
    Concluso,
    Annullato
}

/// <summary>Origine esplicita del TestCase (decisione utente 6.4.0, "Test importati: solo stato finale, non
/// contati come test eseguiti"): additivo, serializzato come stringa come ogni altro enum (JsonStringEnumConverter),
/// non richiede un bump di TestCase.CurrentSchemaVersion — un client 6.3.x che non conosce questa proprietà la
/// ignora semplicemente in lettura (System.Text.Json non fallisce su una proprietà sconosciuta) e non la scrive
/// mai in scrittura, quindi non introduce un nome/valore nuovo che quel client potrebbe incontrare come invece
/// accade per un enum esistente rinumerato (vedi il commento su Esito sopra). Ditto = 0 = default: ogni Test
/// creato/salvato da TestLoom stesso resta TestLoom senza bisogno di valorizzarlo esplicitamente ovunque.
///
/// NON dedurre ImportStatoUdt a runtime dal Changelog nell'app (vedi invece TestCaseOrigine.EImportStatoUdt,
/// funzione pura di sola lettura usata dall'harness di correzione della cartella dati): la valorizzazione sui
/// dati esistenti è un'operazione una tantum sui file, non un calcolo che deve rieseguire a ogni apertura.</summary>
public enum OrigineTest
{
    /// <summary>Test creato/eseguito in TestLoom (default).</summary>
    TestLoom = 0,

    /// <summary>Fotografia dello stato noto importata dagli Status UnitLogger: non è un'esecuzione fatta in TestLoom,
    /// resta marcato "Importato da UnitLogger" ed esclusa dai conteggi operativi, ma contribuisce allo stato finale
    /// (copertura, matrice report, audit, NC collegate) esattamente come un Test eseguito in TestLoom.</summary>
    ImportStatoUdt = 1
}

/// <summary>Ciclo di vita dedicato alle Change Request. Non e un esito di prova:</n+/// descrive l'avanzamento della modifica e della sua validazione.</summary>
public enum StatoChangeRequest
{
    Aperta,
    Implementata,
    DaValidare,
    Validata,
    Chiusa,
    Annullata
}

public enum AmbienteProva { Banco, Veicolo }

/// <summary>Tipo di un campo personalizzato definito per uno Strumento (Impostazioni → Strumenti), che
/// determina quale controllo di editing viene renderizzato nel form del test.</summary>
/// <summary>File (Addendum: collaudo live 2026-08-17, punto 2 del grill-me): il valore del campo è il
/// riferimento a un file collegato al test (es. un .dbc) invece di testo/numero/lista/booleano — riusa
/// AttachmentService/TestCase.Allegati come storage, vedi TestCaseEditorViewModel.CollegaFileCampoStrumento.</summary>
/// <summary>ListaTesto (feedback utente 2026-08-24, punto 8): il valore è un elenco di righe di testo libero
/// scritte/incollate dall'operatore (es. un trace ValidationTool riga per riga), non opzioni fisse decise dal Master
/// come in Lista — ogni riga può anche avere un colore di evidenziazione, vedi CampoStrumentoValore.RigheListaTesto.</summary>
public enum TipoCampoStrumento { Testo, Numero, Lista, Checkbox, File, Data, ListaTesto }

/// <summary>Evidenziazione di un testo dentro un'operazione (feedback utente 2026-08-24, punto 8): solo un
/// colore, nessuna sfumatura — "metti in chiaro questa riga/questo campo", non un vero editor di rich text.</summary>
public enum ColoreEvidenziazione { Nessuno, Giallo, Rosso }

public enum StatoImpatto { DaValutare, DaVerificare, Escluso, VerificatoStessaEsecuzione, VerificatoAltroTest }

public enum ImpegnoStimato { Rapido, Standard, Impegnativo }

public enum StatoSegnalazione
{
    DaClassificare,
    Classificata,
    Esclusa,
    /// <summary>Il tentativo negativo e stato superato da una ripetizione Conforme prima della
    /// classificazione. Resta nello storico, ma non compare piu nel lavoro operativo.</summary>
    RisoltaDaRipetizione
}

public enum StatoAnomalia
{
    InVerifica,
    Confermata,
    EvidenzeDaCompletare,
    ProntaPerEsta,
    EstaAperto,
    InAttesa,
    UlterioriTestRichiesti,
    ProntaPerRetest,
    RetestFallito,
    RetestSuperato,
    Conclusa
}

/// <summary>Scala 0-5 (feedback utente 2026-08-20, punto 18): sostituisce la vecchia scala a 4 valori
/// (Bloccante/Alta/Media/Bassa). Vive anche su TestCase (non solo su NonConformita): compilabile già in fase
/// di pianificazione, ereditata quando si apre una NC da quel test.</summary>
public enum SeveritaAnomalia { NA = 0, Lowest = 1, Low = 2, Medium = 3, High = 4, Highest = 5 }

/// <summary>Scala 0-5 (feedback utente 2026-08-20, punto 18): "Priorità interna" nell'interfaccia — sostituisce
/// la vecchia scala a 4 valori (Urgente/Alta/Normale/Bassa). Assegnabile solo da un Master, in qualunque
/// momento del ciclo di vita: se non assegnata resta Normal.</summary>
public enum PrioritaOperativa { Enhancement = 0, Trivial = 1, Minor = 2, Normal = 3, Major = 4, Critical = 5 }

public enum StatoNonConformita
{
    Aperta,
    Chiusa
}

public enum DestinatarioSegnalazione
{
    Diag,
    TD,
    Supplier,
    Altro
}

/// <summary>Stato di avanzamento della risoluzione (Addendum §18), indipendente dallo Stato Aperta/Chiusa:
/// una NC resta "Aperta" per tutta la sua vita finché non viene chiusa, ma questo campo dice A CHE PUNTO è
/// il lavoro nel frattempo — utile per non dover aprire ogni NC per sapere se è già in carico a qualcuno.</summary>
public enum StatoDettaglioNc
{
    InAnalisi,
    InFix,
    BugFixedInRetest,
    NoSolutionAccettato
}

public enum CausaProbabileUdt
{
    Veicolo,
    Componente,
    UnitLogger,
    Altro
}

public enum RuoloOperatore
{
    Master,
    Standard,
    Viewer
}

/// <summary>Le etichette sono bilingui (Addendum §20: switch IT/EN da Impostazioni): ogni Label() legge
/// LocalizationService.Instance.Lingua al momento della chiamata (francese in EnumDisplay.Fr.cs), quindi mostra sempre la lingua corrente
/// senza bisogno che chi la chiama sappia nulla della localizzazione.</summary>
public static partial class EnumDisplay
{
    // Non italiano: inglese per en, e per fr senza etichetta francese (vedi FrOppure).
    private static bool En => LocalizationService.Instance.Lingua != "it";

    public static string Label(this Esito e) => FrOppure(FrEsito, e) ?? (e, En) switch
    {
        (Esito.DaEseguire, false) => "Da eseguire",
        (Esito.DaEseguire, true) => "To be executed",
        (Esito.Conforme, false) => "Conforme",
        (Esito.Conforme, true) => "Pass",
        (Esito.NonConforme, false) => "Non Conforme",
        (Esito.NonConforme, true) => "Fail",
        (Esito.ConformeConDeviazioni, false) => "Conforme con deviazioni",
        (Esito.ConformeConDeviazioni, true) => "Pass with deviations",
        (Esito.NonApplicabile, false) => "Non applicabile",
        (Esito.NonApplicabile, true) => "Not applicable",
        (Esito.NonEseguitoNA, false) => "Non eseguito/N.A.",
        (Esito.NonEseguitoNA, true) => "Not executed/N.A.",
        (Esito.DaRivedere, false) => "Da rivedere",
        (Esito.DaRivedere, true) => "To review",
        _ => e.ToString()
    };

    public static string Label(this PunteggioConformita p) => FrOppure(FrPunteggioConformita, p) ?? (p, En) switch
    {
        (PunteggioConformita.NonConforme, false) => "Non conforme (0)",
        (PunteggioConformita.NonConforme, true) => "Not compliant (0)",
        (PunteggioConformita.ScarsamenteConforme, false) => "Scarsamente conforme (1)",
        (PunteggioConformita.ScarsamenteConforme, true) => "Poorly compliant (1)",
        (PunteggioConformita.ParzialmenteConforme, false) => "Parzialmente conforme (2)",
        (PunteggioConformita.ParzialmenteConforme, true) => "Partially compliant (2)",
        (PunteggioConformita.ModeratamenteConforme, false) => "Moderatamente conforme (3)",
        (PunteggioConformita.ModeratamenteConforme, true) => "Moderately compliant (3)",
        (PunteggioConformita.AmpiamenteConforme, false) => "Ampiamente conforme (4)",
        (PunteggioConformita.AmpiamenteConforme, true) => "Largely compliant (4)",
        (PunteggioConformita.Conforme, false) => "Conforme (5)",
        (PunteggioConformita.Conforme, true) => "Compliant (5)",
        (PunteggioConformita.NonApplicabile, false) => "N.A.",
        (PunteggioConformita.NonApplicabile, true) => "N/A",
        _ => p.ToString(),
    };

    public static string Label(this StatoLavoroTest s) => FrOppure(FrStatoLavoroTest, s) ?? (s, En) switch
    {
        (StatoLavoroTest.Pianificato, false) => "Pianificato",
        (StatoLavoroTest.Pianificato, true) => "Planned",
        (StatoLavoroTest.InCorso, false) => "In corso",
        (StatoLavoroTest.InCorso, true) => "In progress",
        (StatoLavoroTest.DaRivedere, false) => "Da rivedere",
        (StatoLavoroTest.DaRivedere, true) => "To review",
        (StatoLavoroTest.InAttesa, false) => "In attesa",
        (StatoLavoroTest.InAttesa, true) => "Waiting",
        (StatoLavoroTest.DaRitestare, false) => "Da ritestare",
        (StatoLavoroTest.DaRitestare, true) => "To retest",
        (StatoLavoroTest.Concluso, false) => "Concluso",
        (StatoLavoroTest.Concluso, true) => "Done",
        (StatoLavoroTest.Annullato, false) => "Annullato",
        (StatoLavoroTest.Annullato, true) => "Cancelled",
        _ => s.ToString()
    };

    /// <summary>Etichetta breve dell'Origine di una REV (vuota per l'Operatore: REV normale).</summary>
    public static string Label(this OrigineRevisione o) => o switch
    {
        OrigineRevisione.Rete => LocalizationService.Instance.Traduci("OrigineRevRete"),
        OrigineRevisione.RieseguitoDaNoi => LocalizationService.Instance.Traduci("OrigineRevRieseguito"),
        _ => ""
    };

    public static string Label(this AmbienteProva a) => FrOppure(FrAmbienteProva, a) ?? (a == AmbienteProva.Banco
        ? LocalizationService.Instance.Scegli("Banco", "Bench", "Banc")
        : LocalizationService.Instance.Scegli("Veicolo", "Vehicle", "Véhicule"));

    public static string Label(this TipoCampoStrumento t) => FrOppure(FrTipoCampoStrumento, t) ?? (t, En) switch
    {
        (TipoCampoStrumento.Testo, false) => "Testo libero",
        (TipoCampoStrumento.Testo, true) => "Free text",
        (TipoCampoStrumento.Numero, false) => "Numero",
        (TipoCampoStrumento.Numero, true) => "Number",
        (TipoCampoStrumento.Lista, false) => "Lista a tendina",
        (TipoCampoStrumento.Lista, true) => "Dropdown list",
        (TipoCampoStrumento.Checkbox, false) => "Casella di spunta",
        (TipoCampoStrumento.Checkbox, true) => "Checkbox",
        (TipoCampoStrumento.File, false) => "File collegato",
        (TipoCampoStrumento.File, true) => "Linked file",
        (TipoCampoStrumento.Data, false) => "Data",
        (TipoCampoStrumento.Data, true) => "Date",
        (TipoCampoStrumento.ListaTesto, false) => "Elenco di righe",
        (TipoCampoStrumento.ListaTesto, true) => "List of lines",
        _ => t.ToString()
    };

    public static string Label(this StatoManualeWorkingUnit s) => FrOppure(FrStatoManualeWorkingUnit, s) ?? (s, En) switch
    {
        (StatoManualeWorkingUnit.Nessuno, false) => "Automatico",
        (StatoManualeWorkingUnit.Nessuno, true) => "Automatic",
        (StatoManualeWorkingUnit.Ok, false) => "OK (manuale)",
        (StatoManualeWorkingUnit.Ok, true) => "OK (manual)",
        (StatoManualeWorkingUnit.Problemi, false) => "Problemi (manuale)",
        (StatoManualeWorkingUnit.Problemi, true) => "Issues (manual)",
        _ => s.ToString()
    };

    public static string Label(this ColoreEvidenziazione c) => FrOppure(FrColoreEvidenziazione, c) ?? (c, En) switch
    {
        (ColoreEvidenziazione.Nessuno, false) => "Nessuna evidenziazione",
        (ColoreEvidenziazione.Nessuno, true) => "No highlight",
        (ColoreEvidenziazione.Giallo, false) => "Giallo",
        (ColoreEvidenziazione.Giallo, true) => "Yellow",
        (ColoreEvidenziazione.Rosso, false) => "Rosso",
        (ColoreEvidenziazione.Rosso, true) => "Red",
        _ => c.ToString()
    };

    public static string Label(this TipoAmbitoCampo a) => FrOppure(FrTipoAmbitoCampo, a) ?? (a, En) switch
    {
        (TipoAmbitoCampo.PerOperazione, false) => "Per operazione",
        (TipoAmbitoCampo.PerOperazione, true) => "Per operation",
        (TipoAmbitoCampo.PerRevisione, false) => "Stabile per revisione",
        (TipoAmbitoCampo.PerRevisione, true) => "Stable per revision",
        (TipoAmbitoCampo.PerTest, false) => "Costante per l'intero test",
        (TipoAmbitoCampo.PerTest, true) => "Constant for the whole test",
        _ => a.ToString()
    };

    public static string Label(this StatoAnomalia s) => FrOppure(FrStatoAnomalia, s) ?? (s, En) switch
    {
        (StatoAnomalia.InVerifica, false) => "In verifica",
        (StatoAnomalia.InVerifica, true) => "Under review",
        (StatoAnomalia.Confermata, false) => "Confermata",
        (StatoAnomalia.Confermata, true) => "Confirmed",
        (StatoAnomalia.EvidenzeDaCompletare, false) => "Evidenze da completare",
        (StatoAnomalia.EvidenzeDaCompletare, true) => "Evidence incomplete",
        (StatoAnomalia.ProntaPerEsta, false) => "Pronta per TICKET",
        (StatoAnomalia.ProntaPerEsta, true) => "Ready for TICKET",
        (StatoAnomalia.EstaAperto, false) => "TICKET aperto",
        (StatoAnomalia.EstaAperto, true) => "TICKET open",
        (StatoAnomalia.InAttesa, false) => "In attesa",
        (StatoAnomalia.InAttesa, true) => "Waiting",
        (StatoAnomalia.UlterioriTestRichiesti, false) => "Ulteriori test richiesti",
        (StatoAnomalia.UlterioriTestRichiesti, true) => "Further testing required",
        (StatoAnomalia.ProntaPerRetest, false) => "Pronta per retest",
        (StatoAnomalia.ProntaPerRetest, true) => "Ready for retest",
        (StatoAnomalia.RetestFallito, false) => "Retest fallito",
        (StatoAnomalia.RetestFallito, true) => "Retest failed",
        (StatoAnomalia.RetestSuperato, false) => "Retest superato",
        (StatoAnomalia.RetestSuperato, true) => "Retest passed",
        (StatoAnomalia.Conclusa, false) => "Conclusa",
        (StatoAnomalia.Conclusa, true) => "Closed",
        _ => s.ToString()
    };

    public static string Label(this PrioritaOperativa p) => FrOppure(FrPrioritaOperativa, p) ?? (p, En) switch
    {
        (PrioritaOperativa.Enhancement, false) => "Enhancement",
        (PrioritaOperativa.Enhancement, true) => "Enhancement",
        (PrioritaOperativa.Trivial, false) => "Trivial",
        (PrioritaOperativa.Trivial, true) => "Trivial",
        (PrioritaOperativa.Minor, false) => "Minor",
        (PrioritaOperativa.Minor, true) => "Minor",
        (PrioritaOperativa.Normal, false) => "Normal",
        (PrioritaOperativa.Normal, true) => "Normal",
        (PrioritaOperativa.Major, false) => "Major",
        (PrioritaOperativa.Major, true) => "Major",
        (PrioritaOperativa.Critical, false) => "Critical",
        (PrioritaOperativa.Critical, true) => "Critical",
        _ => p.ToString()
    };

    public static string Label(this SeveritaAnomalia s) => FrOppure(FrSeveritaAnomalia, s) ?? (s, En) switch
    {
        (SeveritaAnomalia.NA, false) => "N/D",
        (SeveritaAnomalia.NA, true) => "N/A",
        (SeveritaAnomalia.Lowest, false) => "Minima",
        (SeveritaAnomalia.Lowest, true) => "Lowest",
        (SeveritaAnomalia.Low, false) => "Bassa",
        (SeveritaAnomalia.Low, true) => "Low",
        (SeveritaAnomalia.Medium, false) => "Media",
        (SeveritaAnomalia.Medium, true) => "Medium",
        (SeveritaAnomalia.High, false) => "Alta",
        (SeveritaAnomalia.High, true) => "High",
        (SeveritaAnomalia.Highest, false) => "Massima",
        (SeveritaAnomalia.Highest, true) => "Highest",
        _ => s.ToString()
    };

    public static string Label(this StatoNonConformita s) => FrOppure(FrStatoNonConformita, s) ?? (s, En) switch
    {
        (StatoNonConformita.Aperta, false) => "Aperta",
        (StatoNonConformita.Aperta, true) => "Open",
        (StatoNonConformita.Chiusa, false) => "Chiusa",
        (StatoNonConformita.Chiusa, true) => "Closed",
        _ => s.ToString()
    };

    public static string Label(this StatoDettaglioNc s) => FrOppure(FrStatoDettaglioNc, s) ?? (s, En) switch
    {
        (StatoDettaglioNc.InAnalisi, false) => "In analisi",
        (StatoDettaglioNc.InAnalisi, true) => "Under analysis",
        (StatoDettaglioNc.InFix, false) => "In fix",
        (StatoDettaglioNc.InFix, true) => "Being fixed",
        (StatoDettaglioNc.BugFixedInRetest, false) => "Bug fixed - in retest",
        (StatoDettaglioNc.BugFixedInRetest, true) => "Bug fixed - retesting",
        (StatoDettaglioNc.NoSolutionAccettato, false) => "No solution (accettato)",
        (StatoDettaglioNc.NoSolutionAccettato, true) => "No solution (accepted)",
        _ => s.ToString()
    };

    public static string Label(this DestinatarioSegnalazione d) => FrOppure(FrDestinatarioSegnalazione, d) ?? d switch
    {
        DestinatarioSegnalazione.Diag => "Diag",
        DestinatarioSegnalazione.TD => "T&D",
        DestinatarioSegnalazione.Supplier => "Supplier",
        DestinatarioSegnalazione.Altro => LocalizationService.Instance.Scegli("Altro", "Other", "Autre"),
        _ => d.ToString()
    };

    public static string Label(this CausaProbabileUdt c) => FrOppure(FrCausaProbabileUdt, c) ?? (c, En) switch
    {
        (CausaProbabileUdt.Veicolo, false) => "Veicolo",
        (CausaProbabileUdt.Veicolo, true) => "Vehicle",
        (CausaProbabileUdt.Componente, _) => "Componente",
        (CausaProbabileUdt.UnitLogger, _) => "UnitLogger",
        (CausaProbabileUdt.Altro, false) => "Altro",
        (CausaProbabileUdt.Altro, true) => "Other",
        _ => c.ToString()
    };

    public static string Label(this RuoloOperatore r) => FrOppure(FrRuoloOperatore, r) ?? (r, En) switch
    {
        (RuoloOperatore.Master, _) => "Master",
        (RuoloOperatore.Standard, false) => "Operatore",
        (RuoloOperatore.Standard, true) => "Operator",
        (RuoloOperatore.Viewer, false) => "Viewer (sola lettura)",
        (RuoloOperatore.Viewer, true) => "Viewer (read-only)",
        _ => r.ToString()
    };
}
