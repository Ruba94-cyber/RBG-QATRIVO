namespace TestLoom.App.Models;

/// <summary>Configurazione locale dell'istanza (mai nella cartella condivisa, §7.9).</summary>
public class AppConfig
{
    public string CartellaCondivisa { get; set; } = "";

    /// <summary>Modalità della cartella condivisa scelta su questo PC (6.6.1): "OneDrive", "Rete" oppure assente/null =
    /// rilevamento automatico (UNC o unità mappata di rete = cartella di rete, altrimenti OneDrive/SharePoint). Locale a
    /// questo PC come il resto di AppConfig: due PC sulla stessa cartella possono usare modalità diverse. Assente nei
    /// config precedenti o con valore sconosciuto = Automatica; null non viene scritto in config.json.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ModalitaCartellaCondivisa { get; set; }

    /// <summary>Chiave del workspace locale fissata per ogni ortografia nota della cartella condivisa (6.6.1, decisione A):
    /// lettera e UNC della stessa cartella puntano alla stessa chiave. Locale a questo PC, additiva: assente nei config
    /// precedenti (il primo avvio la calcola con la regola di apertura, vedi WorkspaceLocalPaths.FissaChiaveAllApertura).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ChiaveWorkspacePerCartella { get; set; }

    /// <summary>Destinazione facoltativa del backup ZIP secondario. Solo config locale per-PC:
    /// mai condivisa via OneDrive, mai richiesta per il normale funzionamento TestLoom.</summary>
    public string PercorsoBackupEsterno { get; set; } = "";
    public string OperatoreAttivo { get; set; } = "";
    public bool SoloMieiElementiDaSeguire { get; set; }
    public bool AvvioRidotto { get; set; }
    public bool NotificheWindows { get; set; } = false;

    /// <summary>Tipi di notifica da NON contare nel badge e nell'avviso Windows (Impostazioni: "Mostra nel contatore").
    /// Locale a questo PC/operatore come il resto di AppConfig. Le notifiche silenziate restano nel centro notifiche e
    /// non vengono mai eliminate; ConflittoSincronizzazione non è mai silenziabile (vedi NotificaPreferenzeService).
    /// Assente nei config.json precedenti = nessun tipo silenziato.</summary>
    public List<string> NotificheTipiSilenziati { get; set; } = new();

    /// <summary>Riepilogo notifiche (6.6.0): "Disattivato" (default), "Giornaliero" o "AllAvvio". Locale a questo PC.
    /// Assente nei config.json precedenti o con valore sconosciuto = Disattivato (vedi NotificaRiepilogoService).</summary>
    public string NotificheRiepilogoModo { get; set; } = "Disattivato";

    /// <summary>Tipi ESCLUSI dal riepilogo: gli altri tipi non critici vi entrano. Assente nei config.json precedenti =
    /// nessuna esclusione. ConflittoSincronizzazione e WuDaDecidere non vi entrano mai, anche se elencati a mano.</summary>
    public List<string> NotificheRiepilogoTipiEsclusi { get; set; } = new();
    public string PaginaIniziale { get; set; } = "Home";
    public List<string> WidgetHome { get; set; } = new() { "Attivita", "Copertura", "Anomalie", "Carico" };

    /// <summary>"it" o "en" (Addendum §20). Locale all'istanza come il resto di AppConfig: ogni PC può avere
    /// il proprio operatore che preferisce una lingua diversa da chi condivide la stessa cartella dati.</summary>
    public string Lingua { get; set; } = "it";

    /// <summary>Storico dei report esportati con "Salva con nome" (Addendum: grill-me 2026-08-18, follow-up):
    /// locale come il resto di AppConfig, perché il percorso scelto può essere ovunque sul PC, non solo nella
    /// cartella condivisa — non ha senso condividerlo con altri operatori. Più recente in cima.</summary>
    public List<ReportSalvato> CronologiaReport { get; set; } = new();

    /// <summary>Ordine scelto dall'utente per la barra di navigazione principale e per le sotto-sezioni di
    /// Impostazioni (Addendum: grill-me 2026-08-18, follow-up) — liste di NavItem.Id, locali come la lingua:
    /// ogni PC/operatore personalizza il proprio. Vuoto = ordine di default.</summary>
    public List<string> OrdineNavPrincipale { get; set; } = new();
    public List<string> OrdineImpostazioni { get; set; } = new();

    /// <summary>Icone personalizzate delle voci di navigazione, chiave "ambito:idVoce" (vedi
    /// IconeSezioniService). Locale al PC come l'ordine dei pannelli: mai nella cartella condivisa. Additivo e
    /// retrocompatibile: null/assente = icone predefinite, e null non viene scritto in config.json.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? IconeSezioni { get; set; }

    /// <summary>Versione dell'onboarding completata, distinta per utente e ruolo. La chiave include il ruolo
    /// così una promozione a Master (o un passaggio a Viewer) presenta il percorso pertinente. È intenzionalmente
    /// locale: su una nuova postazione l'utente riceve anche l'orientamento specifico di quel PC.</summary>
    public Dictionary<string, int> VersioniOnboardingCompletate
    {
        get => _versioniOnboardingCompletate;
        set => _versioniOnboardingCompletate = ConConfrontoIgnoraMaiuscole(value);
    }
    private Dictionary<string, int> _versioniOnboardingCompletate = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Layout delle tabelle per coppia utente/ruolo e identificativo vista. È locale al PC e non
    /// modifica i dati condivisi.</summary>
    public Dictionary<string, TableLayoutPreference> LayoutTabelle
    {
        get => _layoutTabelle;
        set => _layoutTabelle = ConConfrontoIgnoraMaiuscole(value);
    }
    private Dictionary<string, TableLayoutPreference> _layoutTabelle = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordine personalizzato dei valori degli assi della matrice Report, per operatore|ruolo|dimensione
    /// (OrdineAssiReportService.Chiave). Locale al PC come LayoutTabelle: personale, mai nella cartella dati.
    /// Vuoto/assente = ordine alfabetico.</summary>
    public Dictionary<string, List<string>> OrdineAssiReport
    {
        get => _ordineAssiReport;
        set => _ordineAssiReport = ConConfrontoIgnoraMaiuscole(value);
    }
    private Dictionary<string, List<string>> _ordineAssiReport = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Combinazioni di filtri salvate dall'utente, per pagina (Addendum: audit UX 2026-08-26, punto
    /// 3): la chiave è lo stesso identificativo usato da TableLayoutPreferences.TableId ("test-list", "home",
    /// ...). Locale come il resto di AppConfig: due colleghi sulla stessa cartella possono avere ciascuno le
    /// proprie viste, stessa logica già in uso per OrdineNavPrincipale e CronologiaReport (§19, regola 11).</summary>
    public Dictionary<string, List<VistaSalvataFiltri>> VisteSalvate
    {
        get => _visteSalvate;
        set => _visteSalvate = ConConfrontoIgnoraMaiuscole(value);
    }
    private Dictionary<string, List<VistaSalvataFiltri>> _visteSalvate = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ultima selezione automatica per pagina/controllo, distinta da VisteSalvate che è un salvataggio
    /// ESPLICITO con nome scelto dall'utente (feedback utente 2026-09-18: "chiudendo e riaprendo l'app... mi
    /// torna al selettore veicolo iniziale e non a quello precedentemente selezionato, tutte le viste
    /// dovrebbero rimanere esattamente come quando l'app è stata chiusa" — qui l'utente non salva nulla di
    /// proposito, l'app deve solo ricordarsi da sola cosa c'era aperto). Chiave libera per pagina/campo (es.
    /// "Audit.Sottoveicolo", "Home.DataDa"), vedi ViewStateService. Locale al PC come il resto di AppConfig:
    /// non ha senso condividere "cosa avevo aperto" con un collega sulla stessa cartella.</summary>
    public Dictionary<string, string?> UltimaSelezionePagine
    {
        get => _ultimaSelezionePagine;
        set => _ultimaSelezionePagine = ConConfrontoIgnoraMaiuscole(value);
    }
    private Dictionary<string, string?> _ultimaSelezionePagine = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marcature manuali "N.A." per cella della matrice del Report (decisione utente piano 6.4.0,
    /// punto 4: "nella matrice che vado a creare nei report devo poter mettere anche NA nel caso in cui non
    /// sia applicabile"). Chiave = MatriceNonApplicabileService.Chiave(AsseX, AsseY): la marcatura appartiene
    /// alla FORMA della matrice (quali due dimensioni sono scelte come assi), non a un singolo export Excel/PDF
    /// — resta valida finché l'operatore non cambia gli assi e sopravvive a "Salva con nome" ripetuti sulla
    /// stessa combinazione (nessun concetto di "report salvato con nome" esiste oggi in TestLoom al di fuori di
    /// CronologiaReport, che è solo lo storico dei file esportati). Locale come CronologiaReport/il resto di
    /// AppConfig — stesso config.json, stessi permessi di chi può generare report su questo PC.</summary>
    public Dictionary<string, List<CellaNonApplicabile>> MatriceNonApplicabili
    {
        get => _matriceNonApplicabili;
        set => _matriceNonApplicabili = ConConfrontoIgnoraMaiuscole(value);
    }
    private Dictionary<string, List<CellaNonApplicabile>> _matriceNonApplicabili = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Il caricamento JSON assegna un Dictionary nuovo con il confronto predefinito (sensibile alle maiuscole), e
    /// l'inizializzatore OrdinalIgnoreCase andrebbe perso. Ogni assegnazione riporta il dizionario al confronto senza
    /// distinzione di maiuscole: le chiavi che differiscono solo per maiuscole si fondono (vince l'ultima letta), senza
    /// eccezioni durante il caricamento di config.json.</summary>
    private static Dictionary<string, TValue> ConConfrontoIgnoraMaiuscole<TValue>(Dictionary<string, TValue>? valori)
    {
        if (valori != null && ReferenceEquals(valori.Comparer, StringComparer.OrdinalIgnoreCase)) return valori;
        var copia = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        if (valori != null)
            foreach (var coppia in valori) copia[coppia.Key] = coppia.Value;
        return copia;
    }
}

/// <summary>Una cella della matrice Report marcata manualmente come non applicabile. Identificata dai VALORI
/// reali degli assi (es. "BCM", "Diag. base"), non da un indice di riga/colonna: un riordino o un filtro che
/// nasconde temporaneamente altre righe/colonne non sposta la marcatura su una cella diversa. Se il valore
/// smette di comparire nella matrice (componente rinominata, filtro attivo, dato eliminato) la marcatura resta
/// semplicemente dormiente in questa lista, pronta a riattivarsi se il valore ricompare — nessun errore.</summary>
public class CellaNonApplicabile
{
    public string ValoreAsseX { get; set; } = "";
    public string ValoreAsseY { get; set; } = "";

    /// <summary>Motivazione breve opzionale (Addendum piano 6.4.0: "nota opzionale utile, motivazione breve"),
    /// riportata come commento sulla cella Excel corrispondente.</summary>
    public string? Nota { get; set; }
}

/// <summary>Una combinazione di filtri con un nome scelto dall'utente. Un solo Dictionary&lt;string,string?&gt;
/// invece di una proprietà per ogni possibile filtro (Addendum: audit UX 2026-08-26, punto 3): pagine diverse
/// hanno insiemi di filtri diversi (Test ne ha oltre 15, Home ne ha altrettanti più i campi custom per
/// catalogo), e serializzare tutto a stringa evita di dover aggiungere qui un campo ad ogni nuovo filtro che
/// una pagina introduce in futuro. Ogni ViewModel sa da solo come impacchettare/spacchettare i propri filtri
/// (vedi TestCaseListViewModel.CatturaFiltriCorrenti/ApplicaVista) — AppConfig resta agnostico del significato.</summary>
public class VistaSalvataFiltri
{
    public string Nome { get; set; } = "";
    public Dictionary<string, string?> Valori { get; set; } = new();
}

public class TableLayoutPreference
{
    /// <summary>Versione del formato delle larghezze salvate (0 = salvate prima della 6.4.6, anche con colonne
    /// schiacciate da una finestra piccola): vedi TableLayoutPreferences.VersioneLayoutCorrente.</summary>
    public int Versione { get; set; }
    public List<TableColumnPreference> Colonne { get; set; } = new();

    /// <summary>Altezze scelte per singola riga, indicizzate da una chiave stabile dell'elemento (o dalla posizione
    /// per righe anonime come l'anteprima report). La mappa è locale e limitata alle righe effettivamente ridimensionate.</summary>
    public Dictionary<string, double> AltezzeRighe { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordinamento scelto sull'intestazione (null = nessun ordinamento). Campo aggiunto dopo il formato
    /// iniziale: nei config precedenti manca e si legge come null (nessun ordinamento ripristinato), e quando e'
    /// null non viene scritto, cosi' il file resta identico a prima per le tabelle non ordinate.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TableSortPreference? Ordinamento { get; set; }
}

/// <summary>Colonna ordinata e direzione di una DataGrid. La colonna e' identificata dal percorso della proprieta' di
/// ordinamento (SortMemberPath, o il path del Binding) e non dall'indice: resta valida se le colonne vengono riordinate
/// o se una versione successiva ne aggiunge altre.</summary>
public class TableSortPreference
{
    public string Percorso { get; set; } = "";
    public System.ComponentModel.ListSortDirection Direzione { get; set; } = System.ComponentModel.ListSortDirection.Ascending;
}

public class TableColumnPreference
{
    public int Indice { get; set; }
    public int Ordine { get; set; }
    public double Larghezza { get; set; }
    public bool Visibile { get; set; } = true;
}

public class ReportSalvato
{
    public string Percorso { get; set; } = "";
    public DateTime Data { get; set; }
}

public enum StatoCondivisione { Operativa, SolaLettura, NonDisponibile }

public class EsitoStatoCondivisione
{
    public StatoCondivisione Stato { get; set; }
    public string Messaggio { get; set; } = "";
    public DateTime VerificatoIl { get; set; }

    /// <summary>Verifica non conclusa nel tetto di tempo: l'esito è incerto e non è una perdita della radice (P4).</summary>
    public bool Incerto { get; set; }
}

public class NotificaApp
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public DateTime Data { get; set; } = DateTime.Now;
    public string Destinatario { get; set; } = "";
    public string Tipo { get; set; } = "Informazione";
    public string Titolo { get; set; } = "";
    public string Messaggio { get; set; } = "";
    public string ElementoId { get; set; } = "";
    public bool Letta { get; set; }

    /// <summary>Se la notifica porta a un elemento apribile nell'app. Computata per la UI e mai persistita.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HaElementoApribile => !string.IsNullOrWhiteSpace(ElementoId) && Tipo switch
    {
        "Assegnazione" or "Classificazione" or "Conflitto" or "ChangeRequest" or "SegnalazioneRete" or "nc" => true,
        "WuDaDecidere" or "ConflittoSincronizzazione" => true,
        _ => false,
    };
}

public class FileConProblemi
{
    public string Percorso { get; set; } = "";
    public string Errore { get; set; } = "";
    public DateTime RilevatoIl { get; set; }

    /// <summary>Solo per le copie di conflitto OneDrive (6.4.2): false quando la copia non richiede nulla
    /// all'utente (contenuto identico all'originale, spostamento ancora in corso, versione già assorbita da un
    /// merge). Resta nella diagnostica ma non viene mostrata, vedi AppState.TutteLeCopieDiConflitto.</summary>
    public bool RichiedeAttenzione { get; set; } = true;
}

/// <summary>Due o più file che si risolvono nello stesso TestCase.Id — tipicamente una copia di conflitto
/// creata da SharePoint/OneDrive quando due operatori hanno scritto lo stesso file in una finestra di sync
/// troppo ravvicinata. I dati non sono persi (SharePoint mantiene entrambe le copie), ma restano ambigui
/// finché un Master non le riconcilia manualmente: questo elenco li rende visibili invece di lasciarli
/// silenziosamente indistinguibili in lista.</summary>
public class TestCaseDuplicato
{
    public string Id { get; set; } = "";
    public List<string> Percorsi { get; set; } = new();
}
