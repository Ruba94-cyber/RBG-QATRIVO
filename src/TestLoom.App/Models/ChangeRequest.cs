using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestLoom.App.Models;

public sealed class ChangeRequest
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Uid { get; set; } = Guid.NewGuid();
    public long RevisioneDati { get; set; }
    public string Id { get; set; } = "";
    public Guid TemplateUid { get; set; }
    public string Titolo { get; set; } = "";
    public string DescrizioneBreve { get; set; } = "";
    public string Descrizione { get; set; } = "";
    public string MotivazioneDuplicato { get; set; } = "";

    /// <summary>Impostato una sola volta alla creazione, mai più sovrascritto (feedback utente 2026-09-15:
    /// "il creatore dovrebbe essere comunque sempre presente" — a differenza di Responsabile, che può cambiare
    /// nel tempo, non c'era alcun modo di sapere chi ha aperto la CR). Stesso principio di TestCase.CreatoDa.</summary>
    public string CreatoDa { get; set; } = "";
    public string Responsabile { get; set; } = "";
    public StatoChangeRequest Stato { get; set; } = StatoChangeRequest.Aperta;
    public DateTime DataCreazione { get; set; } = DateTime.Now;
    public DateTime UltimaModifica { get; set; } = DateTime.Now;
    public DateTime? ImplementataIl { get; set; }
    public string ImplementataDa { get; set; } = "";
    public int RevisioneImplementata { get; set; }
    public DateTime? ChiusaIl { get; set; }
    public string ChiusaDa { get; set; } = "";

    /// <summary>Motivo scritto della chiusura (obbligatorio). Vuoto per le CR chiuse prima di questa regola e per le CR
    /// aperte; la riapertura lo azzera e lo conserva nel Changelog.</summary>
    public string MotivoChiusura { get; set; } = "";

    /// <summary>True se la CR e' stata chiusa mentre NON era Validata (chiusura con motivo da parte del Master).
    /// Prima di questa regola si chiudeva solo da Validata, quindi il valore predefinito false e' corretto anche
    /// per le CR gia' chiuse. Azzerato alla riapertura.</summary>
    public bool ChiusaSenzaValidazione { get; set; }
    public bool Archiviata { get; set; }
    public DateTime? DataArchiviazione { get; set; }
    public List<string> VeicoliImpattati { get; set; } = new();
    public List<string> EcuImpattate { get; set; } = new();
    public List<string> StrumentiImpattati { get; set; } = new();
    public Dictionary<string, string> CampiCustom { get; set; } = new();
    public List<TicketRif> TicketCollegati { get; set; } = new();
    public List<Allegato> Allegati { get; set; } = new();
    public List<string> CrCorrelate { get; set; } = new();

    /// <summary>Campi popolati SOLO da una CR importata da una segnalazione esterna (import SharePoint
    /// 2026-09-22): il ciclo di vita normale di una Change Request non li usa e restano vuoti, quindi non
    /// sono mai obbligatori e nessuna validazione li richiede. Esistono perche' una richiesta di modifica
    /// nata dalla rete assistenziale porta con se' gli stessi riferimenti di una segnalazione (matricola, licenza,
    /// funzionalita' toccate) che altrimenti andrebbero persi: la NonConformita' ha gia' i campi equivalenti,
    /// la ChangeRequest no. Liste vuote per default: un JSON precedente che non le contiene si deserializza
    /// senza modifiche e continua a funzionare.</summary>
    public List<string> VinCliente { get; set; } = new();
    public List<string> Licenze { get; set; } = new();
    public List<string> FunzionalitaImpattate { get; set; } = new();

    /// <summary>Stesso ruolo dell'omonimo su NonConformita: piu' richieste nate dallo stesso ticket
    /// condividono questo identificativo, cosi' restano ritrovabili come famiglia senza tenere allineato un
    /// elenco di Id fratelli. Nullo per ogni CR creata normalmente in TestLoom.</summary>
    public Guid? GruppoSegnalazioneUid { get; set; }
    public List<CollegamentoValidazioneCr> TestValidazione { get; set; } = new();
    public List<RevisioneChangeRequest> Revisioni { get; set; } = new();
    public List<ChangelogEntry> Changelog { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public bool HaAmbito => VeicoliImpattati.Count + EcuImpattate.Count + StrumentiImpattati.Count > 0;

    [JsonIgnore]
    public bool PuoModificaRapida => !Archiviata && Stato is not (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata);

    /// <summary>La vista rapida resta disponibile per riaprire CR chiuse; le annullate e archiviate non hanno azioni.</summary>
    [JsonIgnore]
    public bool PuoAprireAzioniRapide => !Archiviata && Stato != StatoChangeRequest.Annullata;
}

public sealed class CollegamentoValidazioneCr
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string TestId { get; set; } = "";
    public bool Obbligatorio { get; set; } = true;
    public List<string> VeicoliCoperti { get; set; } = new();
    public List<string> EcuCoperte { get; set; } = new();
    public List<string> StrumentiCoperti { get; set; } = new();
    public bool EccezioneCopertura { get; set; }
    public string MotivazioneEccezione { get; set; } = "";
    public string ApprovataDa { get; set; } = "";
    public DateTime? ApprovataIl { get; set; }
    public bool DeviazioneApprovata { get; set; }
    public string MotivazioneDeviazione { get; set; } = "";
    public string DeviazioneApprovataDa { get; set; } = "";
    public int? EsecuzioneValidante { get; set; }
    public SnapshotValidazioneCr? SnapshotValidazione { get; set; }
    public DateTime CollegatoIl { get; set; } = DateTime.Now;
    public string CollegatoDa { get; set; } = "";
}

public sealed class SnapshotValidazioneCr
{
    public DateTime EseguitaIl { get; set; }
    public Esito Esito { get; set; }
    public List<string> Veicoli { get; set; } = new();
    public List<string> Componente { get; set; } = new();
    public List<string> Strumenti { get; set; } = new();
}

public sealed class RevisioneChangeRequest
{
    public int Numero { get; set; }
    public DateTime Data { get; set; } = DateTime.Now;
    public string Autore { get; set; } = "";
    public string Titolo { get; set; } = "";
    public string DescrizioneBreve { get; set; } = "";
    public string Descrizione { get; set; } = "";
    public string MotivazioneDuplicato { get; set; } = "";
    public List<string> VeicoliImpattati { get; set; } = new();
    public List<string> EcuImpattate { get; set; } = new();
    public List<string> StrumentiImpattati { get; set; } = new();
    public Dictionary<string, string> CampiCustom { get; set; } = new();
    public List<string> TestValidazioneId { get; set; } = new();
}
