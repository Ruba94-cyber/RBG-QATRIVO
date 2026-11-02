namespace TestLoom.App.Models;

/// <summary>Sostituisce il vecchio enum fisso TipoTest: un Master può creare, modificare e rinominare
/// liberamente i tipi di test (sezioni, campi, azioni collegate, vincoli, colonne di report), invece di
/// essere limitato ai soli 4 valori originali (Validazione Banco/Veicolo/UnitLogger/UnitLogger Performance). Quei 4
/// diventano template di partenza modificabili come tutti gli altri (Addendum redesign 2026-08, punto 2
/// del secondo grill-me: "tutto deve essere personalizzabile, ogni singolo campo").</summary>
public class TestTemplate : MasterEntity
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = "";
    public string Icona { get; set; } = "🧪";
    public bool Archiviata { get; set; }
    public int Ordine { get; set; }

    /// <summary>Marca questo template come Change Request invece che Test vero e proprio (feedback utente
    /// 2026-08-21, punto 11): riusa lo stesso motore (sezioni/campi liberi, ticket collegato come campo di
    /// sistema) ma le istanze create con un template così marcato non compaiono nella lista/filtri Test e nel
    /// wizard di creazione di un nuovo Test — vivono invece nella pagina Anomalie, filtrabile per tipo.</summary>
    public bool IsChangeRequest { get; set; }

    /// <summary>Sezioni del riquadro Dettagli, in ordine: creabili, rinominabili, riordinabili. Operazioni/
    /// Esecuzioni, Allegati, Riferimenti/Ticket e Revisioni restano una struttura fissa comune a tutti i
    /// template (Addendum §17.7/§7) — solo il contenuto di Dettagli e Pianificazione è governato dal template.</summary>
    public List<SezioneTemplate> Sezioni { get; set; } = new();

    /// <summary>Campi del riquadro Pianificazione (Stato lavoro/Area validazione/Scadenza/Assegnatario): quali
    /// mostrare, in che ordine, con quale etichetta (Addendum: collaudo live 2026-08-17 — solo presentazione,
    /// i valori di Stato lavoro restano fissi in tutta l'app; Area validazione è invece diventata un elenco
    /// gestibile, vedi Models/MasterData.cs). Piatto, non annidato in sezioni: Pianificazione resta sempre
    /// un solo riquadro, non più sezioni personalizzabili come Dettagli.</summary>
    public List<CampoTemplate> CampiPianificazione { get; set; } = new();

    /// <summary>Configurazione della card "Riferimenti" (Requisiti collegati/Test correlati/Ticket collegati):
    /// mostra/nascondi/obbligatorio per Tipo di test, stesso meccanismo già usato per CampiPianificazione —
    /// nessun nuovo tipo di collegamento, solo visibilità/obbligatorietà dei 3 esistenti (Addendum: grill-me
    /// 2026-08-18, punto 2 — "manca la parte dei test collegati... non editabile/removibile").</summary>
    public List<CampoTemplate> CampiRiferimenti { get; set; } = new();

    /// <summary>Nomi di StrumentoDiagnostico preselezionati quando si sceglie questo template nel wizard.</summary>
    public List<string> StrumentiPreselezionati { get; set; } = new();

    /// <summary>Campi strumento PER OPERAZIONE (chiave "Strumento::Campo") nascosti di default nei Test di questo tipo (schema 11).
    /// Solo visibilita': i dati restano, e il singolo Test puo' mostrarli/nasconderne altri (vedi VistaCampiOperazione).</summary>
    public List<string> CampiOperazioneNascosti { get; set; } = new();

    /// <summary>Sostituisce il vincolo UnitLogger "deve essere collegato a un test esistente": nullo di default (non
    /// era mai stato davvero applicato, vedi TestCase.UdtTestCollegatoId), impostabile per template.</summary>
    public VincoloCollegamentoTemplate? VincoloCollegamento { get; set; }

    /// <summary>Fogli Excel dedicati a questo template (sostituisce i 4 fogli hard-coded di ReportService,
    /// es. "Validazione Banco" oggi genera sia "Test On Bench" sia "Guasto Injection" come fogli separati).
    /// Vuoto = nessun foglio dedicato per questo template nel report.</summary>
    public List<FoglioReportTemplate> FogliReport { get; set; } = new();

    /// <summary>Sezioni del pacchetto Ticketing (PolarionSezioni) proposte nel dialogo di esportazione per i
    /// test di questo template: vuoto = tutte disponibili (comportamento di prima, PolarionSezioni.Tutte),
    /// così i template esistenti non cambiano finché un Master non restringe esplicitamente l'elenco
    /// (feedback utente 2026-08-18, punto 3 — "il pacchetto ticketing dovrebbe poter essere dinamico in base
    /// ai vari report").</summary>
    public List<string> SezioniPolarionDisponibili { get; set; } = new();

    /// <summary>Vero dopo la prima volta che un Master ha usato ▲/▼ su uno dei campi classici del gruppo
    /// Dettagli (Veicolo/Componente/Matricola/BaselineSw/BaselineHw) nell'editor del template (spec riordino campi
    /// 2026-09-23 §2, CampiClassiciOrdineService). Falso (default, anche sui template creati prima della
    /// 6.3.1) = il modulo Test reale ignora Ordine per questi 5 campi e usa la posizione fissa storica
    /// (Veicolo prima di Componente — vedi CampiClassiciOrdineService.CorreggiCompatibilitaVeicoloEcu per il perché
    /// non basta leggere Ordine da solo). Vero = il modulo Test rispetta davvero CampoTemplate.Ordine per
    /// questi campi, come già per tutti gli altri campi riordinabili.</summary>
    public bool CampiClassiciDettagliRiordinati { get; set; }

}

public class SezioneTemplate : Services.IOrdinabile
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = "";
    public int Ordine { get; set; }
    public List<CampoTemplate> Campi { get; set; } = new();
}

public enum TipoCampoTemplate
{
    /// <summary>Punta a un campo che esiste già su TestCase (vedi CampiSistema.Chiavi): riusa il picker/dato
    /// master già collegato invece di reinventarlo.</summary>
    CampoSistema,
    /// <summary>Campo custom puro del template, stessa forma di DefinizioneCampoStrumento (Testo/Numero/Lista/
    /// Checkbox) ma non legato a uno strumento.</summary>
    CampoLibero,
}

public class CampoTemplate : Services.IOrdinabile
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public int Ordine { get; set; }
    public TipoCampoTemplate Tipo { get; set; } = TipoCampoTemplate.CampoLibero;

    /// <summary>Solo per Tipo=CampoSistema: una delle chiavi in CampiSistema.Chiavi (es. "Componente", "Matricola").</summary>
    public string? CampoSistemaChiave { get; set; }

    /// <summary>Etichetta mostrata nel form. Per CampoSistema, se vuota si usa l'etichetta di default della
    /// chiave; per CampoLibero è il nome del campo.</summary>
    public string Nome { get; set; } = "";

    public TipoCampoStrumento TipoLibero { get; set; } = TipoCampoStrumento.Testo;
    public List<string> OpzioniListaLibero { get; set; } = new();

    /// <summary>Obbligatorietà di base. Se StatiObbligatorio è valorizzato, questo flag conta solo mentre lo
    /// Stato del lavoro del test è tra quelli elencati lì — altrimenti il campo resta facoltativo (Addendum:
    /// collaudo live 2026-08-17, feedback QA — es. la matricola è obbligatorio salvo quando il test è ancora solo
    /// "Pianificato", perché il veicolo/banco reale può non essere ancora stato scelto).</summary>
    public bool Obbligatorio { get; set; }

    /// <summary>Vuoto = Obbligatorio vale sempre (comportamento di default per i campi seminati diversi da matricola).
    /// Valorizzato = Obbligatorio viene applicato solo quando lo Stato del lavoro del test è tra questi.</summary>
    public List<StatoLavoroTest> StatiObbligatorio { get; set; } = new();

    /// <summary>Solo per TipoLibero=Numero: unità di misura o simbolo mostrato accanto al valore (es. "km",
    /// "%"), stesso principio di DefinizioneCampoStrumento.UnitaMisura.</summary>
    public string? UnitaMisuraLibero { get; set; }

    /// <summary>Vuoto = campo sempre visibile (comportamento di default). Valorizzato = il campo compare solo
    /// quando l'esito della revisione visualizzata è tra quelli elencati (Addendum: collaudo live 2026-08-17 —
    /// generalizza il vecchio "Descrizione problema visibile solo su esito problematico", prima fisso nel
    /// codice, ora configurabile per qualunque campo).</summary>
    public List<Esito> CondizioneEsito { get; set; } = new();
    public List<StatoChangeRequest> StatiCrVisibile { get; set; } = new();
    public List<StatoChangeRequest> StatiCrObbligatorio { get; set; } = new();
}

/// <summary>Chiavi riconosciute per CampoTemplate.CampoSistemaChiave: i campi di TestCase/DettagliTestSnapshot
/// che oggi vivono nel riquadro Dettagli. Elenco chiuso perché ciascuna chiave corrisponde a una proprietà
/// concreta sul modello — un Master non può inventare chiavi nuove qui, solo scegliere quali mostrare, dove,
/// in che sezione (per campi realmente liberi/nuovi si usa invece CampoLibero).</summary>
public static class CampiSistema
{
    public const string Componente = "Componente";
    public const string Veicolo = "Veicolo";
    public const string Funzionalita = "Funzionalita";
    public const string StrumentiUsati = "StrumentiUsati";
    public const string DescrizioneBreve = "DescrizioneBreve";
    public const string Descrizione = "Descrizione";
    public const string DescrizioneProblema = "DescrizioneProblema";
    public const string Matricola = "Matricola";
    public const string BaselineSw = "BaselineSw";
    public const string BaselineHw = "BaselineHw";

    public static readonly IReadOnlyList<string> Chiavi = new[]
    {
        Componente, Veicolo, Funzionalita, StrumentiUsati,
        DescrizioneBreve, Descrizione, DescrizioneProblema, Matricola, BaselineSw, BaselineHw,
    };

    // Campi del riquadro Pianificazione (Addendum: collaudo live 2026-08-17): elenco separato da Chiavi
    // sopra, così non compaiono nel picker "+ Aggiungi campo" di Dettagli — restano scelte solo per
    // TestTemplate.CampiPianificazione, dove sono popolati di default dal seed.
    public const string StatoLavoro = "StatoLavoro";
    public const string Scadenza = "Scadenza";
    public const string Assegnatario = "Assegnatario";
    public const string AssegnatoDa = "AssegnatoDa";

    public static readonly IReadOnlyList<string> ChiaviPianificazione = new[]
    {
        StatoLavoro, Scadenza, Assegnatario, AssegnatoDa,
    };

    // Campi della card "Riferimenti" (Requisiti collegati / Test correlati / Ticket collegati): come
    // ChiaviPianificazione, elenco separato — questi non sono voci di CampiSistema.Chiavi né compaiono nel
    // picker di Dettagli, sono selezionabili solo per TestTemplate.CampiRiferimenti (Addendum: grill-me
    // 2026-08-18, punto 2 — "manca la parte dei test collegati... non editabile/removibile").
    public const string RequisitiCollegati = "RequisitiCollegati";
    public const string TestCorrelati = "TestCorrelati";
    public const string TicketCollegati = "TicketCollegati";

    public static readonly IReadOnlyList<string> ChiaviRiferimenti = new[]
    {
        RequisitiCollegati, TestCorrelati, TicketCollegati,
    };

    /// <summary>Chiave sintetica (non un vero campo di template, quindi assente da Chiavi/ChiaviPianificazione/
    /// ChiaviRiferimenti e dal picker "+ Aggiungi campo") usata solo per instradare un errore di validazione
    /// per-operazione verso il popup "campi mancanti": la sezione Operazioni è un ItemsControl ripetuto senza
    /// uno Spotlight per singola riga, quindi la destinazione è la sezione nel suo insieme.</summary>
    public const string OperazioneCondizione = "OperazioneCondizione";

    public static string LabelDefault(string chiave) => chiave switch
    {
        Componente => "Componente",
        Veicolo => "Veicolo",
        StatoLavoro => "Stato lavoro",
        AssegnatoDa => "Assegnato da",
        RequisitiCollegati => "Requisiti collegati",
        TestCorrelati => "Test correlati",
        TicketCollegati => "Ticket collegati",
        Scadenza => "Scadenza",
        Assegnatario => "Assegnatario",
        Funzionalita => "Funzionalità",
        StrumentiUsati => "Strumenti usati",
        DescrizioneBreve => "Descrizione breve",
        Descrizione => "Descrizione",
        DescrizioneProblema => "Descrizione problema",
        Matricola => "Matricola",
        BaselineSw => "Baseline SW",
        BaselineHw => "Baseline HW",
        _ => chiave,
    };
}

/// <summary>Sostituisce il vincolo UnitLogger "richiede un test collegato esistente": opzionale per template invece
/// che assunto globalmente (era dichiarato obbligatorio solo in un commento del codice, mai davvero applicato
/// — vedi memoria project_test_template_system).</summary>
public class VincoloCollegamentoTemplate
{
    public bool Obbligatorio { get; set; }
    public string Etichetta { get; set; } = "Test collegato";

    /// <summary>Uid dei template a cui è ammesso collegarsi. Lista vuota = qualunque template. Il tipo RAS UnitLogger nasce con
    /// Validazione Banco e Validazione Veicolo: la migrazione lascia la lista vuota solo se nel catalogo non c'è né l'uno
    /// né l'altro, caso registrato nel log (vedi CatalogMigrationService).</summary>
    public List<Guid> TemplateAmmessiUid { get; set; } = new();
}

public enum OrigineColonnaReport
{
    CampoSistema,
    CampoTemplate,
    CampoStrumento,
}

/// <summary>Un foglio Excel dedicato a questo template (Addendum: il Master configura anche le colonne del
/// report per ogni nuovo template, non generazione automatica generica). Un template può avere più fogli,
/// come oggi "Validazione Banco" genera sia "Test On Bench" sia "Guasto Injection".</summary>
public class FoglioReportTemplate
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public int Ordine { get; set; }
    public string NomeFoglio { get; set; } = "";
    public List<ColonnaReportTemplate> Colonne { get; set; } = new();
}

public class ColonnaReportTemplate
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public int Ordine { get; set; }
    public string Intestazione { get; set; } = "";
    public OrigineColonnaReport Origine { get; set; } = OrigineColonnaReport.CampoSistema;

    /// <summary>Chiave della colonna: per CampoSistema una voce di CampiSistema.Chiavi; per CampoTemplate lo
    /// Uid (stringa) del CampoTemplate; per CampoStrumento "NomeStrumento::NomeCampo" come nel resto dell'app.</summary>
    public string Chiave { get; set; } = "";
}
