using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestLoom.App.Models;

public class TestCase
{
    /// <summary>v11: TestTemplate.CampiOperazioneNascosti (campi strumento per operazione nascosti per tipo di test). Additivo, ma un client
    /// precedente che ri-salvasse il template perderebbe in silenzio l'elenco: il bump lo porta in sola lettura (gate globale del manifest).
    /// v10 (6.5.2): identità immutabile TestUid in NC/prove/segnalazioni; il gate globale
    /// impedisce ai vecchi client di interpretare per codice i collegamenti del nuovo schema.
    /// v9 (6.5.1): DtcRequisito.Veicolo (voci guasto dei requisiti, anche negli archivi master "requisiti-guasto"),
    /// StrumentoDiagnostico.Tipo, Condizione.Strumento/DtcVeicolo. Additivo e retro-compatibile in lettura, ma un client
    /// 6.5.0 (schema 8) che ri-salvasse quei documenti perderebbe in silenzio i nuovi campi: il bump lo porta in sola lettura
    /// su TUTTO il workspace (gate globale del manifest, master inclusi).
    /// v8 (6.5.0, punto 7): Condizione.Guasto/DtcPasso (operazioni guasto dei test Banco basati su requisito/componente/
    /// guasto). Additivo e retro-compatibile in lettura, ma un client 7 che ri-salvasse il test perderebbe in silenzio
    /// quei due campi (Condizione non ha ExtensionData): il bump porta i client piu vecchi in sola lettura
    /// (CompatibilitaVersioneService, deploy/version-manifest.json.schemaDatiMassimo).
    /// v6 (decisione utente 6.4.0, punto 1): Esito guadagna il valore DaEseguire (0 = default di
    /// una nuova operazione, sostituisce l'uso del legacy NonEseguitoNA come sentinella "non eseguita"). Un
    /// client 6.3.x non conosce questo nome e fallirebbe la deserializzazione: il gate di compatibilità
    /// (CompatibilitaVersioneService, deploy/version-manifest.json.schemaDatiMassimo) lo porta in sola
    /// lettura prima che possa scrivere/incontrare il nuovo valore.</summary>
    public const int CurrentSchemaVersion = 11;

    /// <summary>Ultima SchemaVersion per cui vale ancora la migrazione "operazioni Pianificate mai eseguite"
    /// (6.4.0): disaccoppiata da CurrentSchemaVersion cosi il bump a 7 (Esecuzione.Origine) non la riattiva sui
    /// file 6.4.x legittimi con SchemaVersion 6.</summary>
    public const int SchemaMigrazioneOperazioniEsplicite = 6;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>Identità tecnica collision-resistant. L'ID leggibile resta un'etichetta operativa.</summary>
    public Guid Uid { get; set; } = Guid.NewGuid();
    /// <summary>Incrementata a ogni salvataggio e verificata prima della scrittura per evitare lost update.</summary>
    public long RevisioneDati { get; set; }
    public string Id { get; set; } = "";
    public DateTime DataCreazione { get; set; }
    public DateTime UltimaModifica { get; set; }
    public string ModificatoDa { get; set; } = "";
    public bool Eliminato { get; set; }
    public DateTime? DataEliminazione { get; set; }

    /// <summary>Componenti coinvolti dal test, senza distinzione primaria/secondaria.</summary>
    public EcuSelection Componente { get; set; } = new();

    [JsonIgnore]
    public IReadOnlyList<string> EcuEffettive => Componente;

    /// <summary>Riferisce un Veicolo (livello Sottoveicolo, es. "Daily MY24"); la Gamma si ricava per lookup
    /// dal Sottoveicolo scelto (Addendum §17.3), non è duplicata qui.</summary>
    public string Veicolo { get; set; } = "";

    /// <summary>Ambito veicoli del Test. Veicolo resta serializzato per compatibilita con i Test
    /// esistenti e contiene sempre il primo elemento della lista.</summary>
    public List<string> Veicoli { get; set; } = new();

    [JsonIgnore]
    public IReadOnlyList<string> VeicoliEffettivi => Veicoli.Count > 0
        ? Veicoli
        : string.IsNullOrWhiteSpace(Veicolo) ? Array.Empty<string>() : new[] { Veicolo };

    [JsonIgnore]
    public string VeicoliTesto => string.Join(", ", VeicoliEffettivi);

    public void NormalizzaVeicoli()
    {
        if (Veicoli.Count == 0 && !string.IsNullOrWhiteSpace(Veicolo)) Veicoli.Add(Veicolo);
        Veicoli = ValoriVeicoliDistinti(Veicoli);
        Veicolo = Veicoli.FirstOrDefault() ?? "";
        Componente.Normalizza();
    }

    /// <summary>Stesso risultato di Where(non vuoto).Distinct(OrdinalIgnoreCase).ToList(): la prima occorrenza resta nella
    /// sua posizione. Senza iteratori LINQ, perche' la normalizzazione gira per ogni Test in ogni lettura dell'archivio.</summary>
    private static List<string> ValoriVeicoliDistinti(List<string> valori)
    {
        var risultato = new List<string>(valori.Count);
        var visti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in valori)
            if (!string.IsNullOrWhiteSpace(v) && visti.Add(v)) risultato.Add(v);
        return risultato;
    }

    /// <summary>
    /// Migrazione IN LETTURA, idempotente e non distruttiva (decisione utente 6.4.0, punto 1): non riscrive
    /// mai il file su disco, corregge solo l'oggetto in memoria appena deserializzato — va richiamata subito
    /// dopo il parsing JSON dai punti che alimentano conteggi/copertura/esito aggregato (oggi:
    /// LocalTestIndexStore, che alimenta indice SQLite/Home/report — vedi anche OWNERSHIP).
    ///
    /// REGOLA: solo su un Test ancora Pianificato (mai davvero eseguito), un'operazione con Esito=Conforme e
    /// NESSUN'ALTRA evidenza di un vero esito inserito dall'utente (nota, descrizione deviazione/problema,
    /// motivazione N.A. tutte vuote) è quasi certamente il default silenzioso dell'enum pre-6.4.0 (Esito era
    /// un campo non-nullable con Conforme = valore 0), non una scelta reale — la sostituiamo con "Da
    /// eseguire". Sul dataset di riferimento (507 test, 500 Concluso con operazioni, 7 Pianificato di cui 1
    /// con operazioni) l'impatto reale è quindi volutamente minimo: un Test Concluso non viene mai toccato
    /// (l'ipotesi "operazione mai eseguita" non regge più una volta che il lavoro è dichiarato finito), e un
    /// Pianificato con una nota/motivazione già scritta viene lasciato intatto perché quella non è più
    /// un'operazione vuota. Qualunque altro Esito (incluso il legacy NonEseguitoNA, già escluso dai conteggi
    /// da Esito.EEseguito()) resta invariato: la scelta esplicita dell'utente non viene mai toccata. Annullato
    /// resta fuori: a differenza di Pianificato non è nel testo del piano utente, e un test annullato può
    /// portare dati reali raccolti prima della cancellazione.
    ///
    /// GUARDIA CRITICA: SchemaVersion &lt; CurrentSchemaVersion. Un TestCase costruito o già risalvato da un
    /// client 6.4.0+ ha sempre SchemaVersion == CurrentSchemaVersion (è il default della proprietà, e ogni
    /// Save lo riscrive) — e in un client 6.4.0+ l'editor non lascia MAI una nuova operazione a Esito=Conforme
    /// senza che l'utente l'abbia scelto davvero (parte già da "Da eseguire", vedi TestCaseEditorViewModel).
    /// Senza questa guardia, un Conforme genuino appena inserito da un operatore su un test ancora Pianificato
    /// verrebbe scambiato per l'artefatto legacy e silenziosamente riportato a "Da eseguire": è esattamente il
    /// bug preso di mira dai test unitari MultiEcuOperation_CountsOneOperationPerEcu e affini.
    /// </summary>
    public void MigraOperazioniPianificateNonEseguite()
    {
        if (SchemaVersion >= SchemaMigrazioneOperazioniEsplicite) return;
        if (StatoLavoro != StatoLavoroTest.Pianificato) return;
        foreach (var esecuzione in Esecuzioni)
            foreach (var condizione in esecuzione.Condizioni)
                if (condizione.Esito == Esito.Conforme &&
                    string.IsNullOrWhiteSpace(condizione.Nota) &&
                    string.IsNullOrWhiteSpace(condizione.DescrizioneDeviazione) &&
                    string.IsNullOrWhiteSpace(condizione.DescrizioneProblema) &&
                    string.IsNullOrWhiteSpace(condizione.MotivazioneNonApplicabile))
                    condizione.Esito = Esito.DaEseguire;
    }

    public StatoLavoroTest StatoLavoro { get; set; } = StatoLavoroTest.Pianificato;

    /// <summary>Origine esplicita (decisione utente 6.4.0, "Test importati: solo stato finale, non contati come
    /// test eseguiti"): default TestLoom per ogni Test creato/salvato da TestLoom. NON va valorizzato a ImportStatoUdt
    /// da codice applicativo — solo l'harness di correzione della cartella dati (via API) lo fa, sui Test con
    /// voce di Changelog IMPORT-STATUS-UnitLogger-2026-09-22 (vedi TestCaseOrigine.EImportStatoUdt più sotto e
    /// TestCaseLegacyMigrations per il perché la migrazione in lettura non è il punto giusto per questo campo:
    /// qui non c'è nulla da "correggere" in lettura, il valore persistito è già quello definitivo).</summary>
    public OrigineTest Origine { get; set; } = OrigineTest.TestLoom;

    public ImpegnoStimato Impegno { get; set; } = ImpegnoStimato.Standard;
    public DateTime? Scadenza { get; set; }
    public string? MotivoAnnullamento { get; set; }
    public ContestoProva ContestoPredefinito { get; set; } = new();

    /// <summary>Valori dei campi degli strumenti con CampiCostantiPerTest=true (es. licenza/versioni UnitLogger),
    /// costanti per l'intero test — a differenza dei campi tecnici sulle singole Condizioni, che variano per
    /// reiterazione. Chiave: "NomeStrumento::NomeCampo" (Addendum redesign 2026-08, revisione feedback QA:
    /// prima erano proprietà fisse su IdentificativiUdt, ora campi configurabili da Impostazioni → Strumenti).</summary>
    public Dictionary<string, string> ValoriStrumentiTest { get; set; } = new();
    public List<ImpattoPotenziale> ImpattiPotenziali { get; set; } = new();

    /// <summary>Riferisce un TestTemplate (Addendum redesign 2026-08, punto 2 del secondo grill-me): sostituisce
    /// il vecchio enum fisso TipoTest. Guid.Empty finché il passo 1 del wizard non sceglie un template.</summary>
    public Guid TestTemplateUid { get; set; }

    public List<string> RequisitoId { get; set; } = new();
    public List<string> Funzionalita { get; set; } = new();

    /// <summary>Impostato una sola volta alla creazione, mai più sovrascritto (a differenza di ModificatoDa,
    /// che cambia a ogni salvataggio): è "l'assegnato da" mostrato in Pianificazione — chi ha creato il test,
    /// non necessariamente chi lo esegue o lo modifica ora (Addendum redesign 2026-08, feedback QA §2).</summary>
    public string CreatoDa { get; set; } = "";

    /// <summary>Sintesi di poche parole del test, mostrata subito sopra Descrizione e usata anche per comporre
    /// il nome del pacchetto Ticketing (data + componente + descrizione breve) invece del solo Id (Addendum §3).</summary>
    public string DescrizioneBreve { get; set; } = "";

    public string Descrizione { get; set; } = "";

    /// <summary>Descrizione estesa del problema riscontrato, distinta dalla Descrizione generale del test:
    /// compilabile quando l'esito indica un problema (Non Conforme/Con deviazioni/Da rivedere).</summary>
    public string? DescrizioneProblema { get; set; }

    /// <summary>Severità (feedback utente 2026-08-20, punto 18): vive già sul test, compilabile fin dalla
    /// pianificazione — non solo sulla NonConformita, dove prima veniva scelta da zero solo al momento della
    /// classificazione. Quando si apre una NC da questo test, eredita questo valore come default (resta comunque
    /// modificabile su quella specifica NC).</summary>
    public SeveritaAnomalia? Severita { get; set; }

    /// <summary>"Priorità interna" (feedback utente 2026-08-20, punto 18): indica la necessità di gestire la
    /// Non Conformità se non ancora gestita, o la priorità di eseguire il test se ancora solo pianificato (nessuna
    /// NC aperta). Assegnabile solo da un Master (gate lato UI, TestCaseEditorViewModel.PuoAssegnare) — se non
    /// assegnata resta Normal.</summary>
    public PrioritaOperativa Priorita { get; set; } = PrioritaOperativa.Normal;

    /// <summary>Nomi di StrumentoDiagnostico (lista modificabile, non più enum fisso, Addendum §17.5).</summary>
    public List<string> StrumentiUsati { get; set; } = new();

    public Dictionary<string, string> CampiCustom { get; set; } = new();

    public List<string> TestCorrelati { get; set; } = new();

    /// <summary>Ticket esterni collegati al test case (es. Claim, Change Request) — solo riferimento/
    /// promemoria testuale, l'app non si integra con i sistemi esterni dove questi ticket vivono davvero.</summary>
    public List<TicketRif> TicketCollegati { get; set; } = new();

    /// <summary>Collegamento a un test esistente richiesto dal TestTemplate.VincoloCollegamento del template
    /// scelto (Addendum redesign 2026-08, punto 2: sostituisce il vecchio UdtTestCollegatoId, generalizzato a
    /// qualunque template invece di essere specifico per UnitLogger — mai stato davvero applicato prima, solo
    /// dichiarato in un commento, vedi memoria project_test_template_system).</summary>
    public string? TestCollegatoRichiestoId { get; set; }

    /// <summary>Test creati da un'azione collegata del template (Addendum redesign 2026-08: sostituisce
    /// l'escalation fissa banco->veicolo con una lista generica, un elemento per ogni AzioneCollegataTemplate
    /// eseguita — un template può avere più azioni collegate, non solo una).</summary>
    public List<TestCollegatoCreato> TestCollegatiCreati { get; set; } = new();

    /// <summary>Operatore a cui il test è assegnato, indipendente dall'Esito (Addendum §17.1). Un test può
    /// essere "assegnato" e avere contemporaneamente Esito = Non eseguito/N.A.</summary>
    public string? Assegnatario { get; set; }

    /// <summary>Quando è stato assegnato l'attuale Assegnatario: impostata alla creazione, aggiornata solo se
    /// un Master riassegna il test (Addendum redesign 2026-08, feedback QA §7) — serve nel riquadro
    /// Pianificazione per individuare a colpo d'occhio eventuali ritardi.</summary>
    public DateTime? DataAssegnazione { get; set; }

    /// <summary>Traccia obbligatoria del "perché" ogni volta che un Master modifica Area di validazione,
    /// Scadenza o Assegnatario (Addendum §7): a differenza del changelog generico dei master data, qui la nota
    /// è obbligatoria e mostrata direttamente nel riquadro Pianificazione, non solo in uno storico nascosto.</summary>
    public List<NotaPianificazione> NotePianificazione { get; set; } = new();

    /// <summary>Vero se il test è stato creato/eseguito da un operatore Standard fuori dalla propria Gamma
    /// assegnata (o assegnato da un Master a uno Standard fuori categoria): mai bloccato, sempre segnalato e
    /// tracciato come eccezione rivedibile (Addendum §17.3).</summary>
    public bool FuoriCategoria { get; set; }

    public List<Esecuzione> Esecuzioni { get; set; } = new();

    public Esito? StatoCorrente => Esecuzioni.Count == 0 ? null : Esecuzioni[^1].Esito;

    /// <summary>Numero di revisione = numero di iterazioni: ogni nuova iterazione è una nuova revisione/
    /// snapshot del test case (Addendum §17.7). Parte da 0 (nessuna esecuzione ancora).</summary>
    public int NumeroRevisione => Esecuzioni.Count;

    /// <summary>Numero di reiterazioni (Condizioni) nella revisione corrente, per le liste riassuntive.</summary>
    public int NumeroReiterazioniCorrente => Esecuzioni.Count == 0 ? 0 : Esecuzioni[^1].Condizioni.Count;

    public List<Allegato> Allegati { get; set; } = new();

    public List<ChangelogEntry> Changelog { get; set; } = new();

    public bool NeedsClassification => RequisitoId.Count == 0 && Funzionalita.Count == 0;

    /// <summary>Serve ancora lavoro (Addendum §18: tabella "Da fare" della Home) — esito dell'ultima
    /// revisione non conclusivo, o non ancora classificato su nessun requisito/funzionalità.</summary>
    public bool DaFare => StatoLavoro is StatoLavoroTest.Pianificato or StatoLavoroTest.InCorso or
        StatoLavoroTest.DaRivedere or StatoLavoroTest.InAttesa or StatoLavoroTest.DaRitestare;

    /// <summary>Concluso con successo (Addendum §18: tabella "Fatti" della Home).</summary>
    public bool Fatto => StatoLavoro == StatoLavoroTest.Concluso;

    // Proiezione transitoria valorizzata dalla lista Test. Gli stati appartengono alle anomalie e vengono
    // ricalcolati dalla loro fonte, evitando due copie persistite divergenti dello stesso dato.
    [JsonIgnore]
    public List<StatoAnomalia> StatiAnomalieAperteVisual { get; set; } = new();
    [JsonIgnore]
    public int AnomalieChiuseVisual { get; set; }
    [JsonIgnore]
    public bool HaPolarionVisual { get; set; }

    /// <summary>Preserva campi sconosciuti scritti da versioni più nuove dell'app (Addendum §16.1: round-trip forward-compatibile).</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}

[JsonConverter(typeof(EcuSelectionJsonConverter))]
public sealed class EcuSelection : List<string>
{
    public EcuSelection() { }
    public EcuSelection(IEnumerable<string> valori) => Aggiungi(valori);
    public void Aggiungi(IEnumerable<string> valori) { AddRange(valori); Normalizza(); }
    public void Normalizza()
    {
        // Stesso risultato di Where(non vuoto).Select(Trim).Distinct(OrdinalIgnoreCase): senza iteratori LINQ per Test.
        var visti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valori = new List<string>(Count);
        foreach (var v in this)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var pulito = v.Trim();
            if (visti.Add(pulito)) valori.Add(pulito);
        }
        Clear();
        AddRange(valori);
    }
    /// <summary>Confronto componente intenzionalmente esatto (case-insensitive quando richiesto): usare Contains
    /// della stringa trattava "BCM" e "BCM2" come la stessa componente e poteva alterare copertura/riallineamenti.</summary>
    public bool Contains(string value, StringComparison comparison) => this.Any(v => v.Equals(value, comparison));
    public override string ToString() => string.Join(", ", this.AsEnumerable());
    public static implicit operator EcuSelection(string? valore) => string.IsNullOrWhiteSpace(valore) ? new() : new(new[] { valore });
    public static implicit operator string(EcuSelection? valori) => valori?.ToString() ?? "";
    public static bool operator ==(EcuSelection? valori, string? valore) => valori is not null && valori.Count == 1 && valori[0].Equals(valore, StringComparison.OrdinalIgnoreCase);
    public static bool operator !=(EcuSelection? valori, string? valore) => !(valori == valore);
    public static bool operator ==(string? valore, EcuSelection? valori) => valori == valore;
    public static bool operator !=(string? valore, EcuSelection? valori) => !(valori == valore);
    public override bool Equals(object? obj) => obj is EcuSelection other && this.SequenceEqual(other, StringComparer.OrdinalIgnoreCase);
    public override int GetHashCode() => this.Aggregate(17, (hash, value) => HashCode.Combine(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(value)));
}

public sealed class EcuSelectionJsonConverter : JsonConverter<EcuSelection>
{
    public override EcuSelection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        if (reader.TokenType == JsonTokenType.Null) return new();
        var valori = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } valore) valori.Add(valore);
        return new EcuSelection(valori);
    }

    public override void Write(Utf8JsonWriter writer, EcuSelection value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var componente in value) writer.WriteStringValue(componente);
        writer.WriteEndArray();
    }
}
public class ContestoProva
{
    public string Veicolo { get; set; } = "";
    public string Componente { get; set; } = "";
    public string Matricola { get; set; } = "";
    public AmbienteProva Ambiente { get; set; }
}

public class ImpattoPotenziale
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string Tipo { get; set; } = "Componente";
    public string Valore { get; set; } = "";
    public string Motivo { get; set; } = "";
    public StatoImpatto Stato { get; set; } = StatoImpatto.DaValutare;
    public string? TestCollegatoId { get; set; }
}

/// <summary>Una riga della tabella Esecuzioni = una iterazione = una revisione (Addendum §17.7): porta una
/// copia dei campi che possono variare da un tentativo all'altro (matricola, baseline, condizioni), copiata
/// automaticamente dall'iterazione precedente e poi modificabile, così da non dover reinserire tutto a mano
/// mantenendo comunque traccia di cosa è effettivamente cambiato tra un tentativo e l'altro.</summary>
public class Esecuzione
{
    public int Iterazione { get; set; }
    public DateTime Data { get; set; }
    public string Operatore { get; set; } = "";

    /// <summary>Origine della REV (default Operatore = REV normale/legacy). Rete = esito deciso dalla rete, senza
    /// operatore esecutore reale (Operatore e solo audit). Additivo: il JSON legacy senza il campo legge Operatore.</summary>
    public OrigineRevisione Origine { get; set; } = OrigineRevisione.Operatore;

    /// <summary>Etichetta mostrata accanto all'operatore nell'elenco REV: vuota per le REV normali, altrimenti
    /// " · Rete" / " · Rieseguito da noi".</summary>
    [JsonIgnore]
    public string OrigineEtichetta => Origine == OrigineRevisione.Operatore ? "" : " · " + Origine.Label();

    /// <summary>Id della NonConformita (NC) la cui chiusura ha generato questa REV; vuoto per le REV normali.</summary>
    public string NonConformitaOrigineId { get; set; } = "";

    /// <summary>Vero se la REV e stata eseguita da un operatore (Origine != Rete): le statistiche per operatore
    /// devono contare solo queste.</summary>
    [JsonIgnore]
    public bool EsecuzioneDaOperatore => Origine != OrigineRevisione.Rete;

    /// <summary>Chi mostrare come esecutore: l'operatore, oppure "Rete" se la chiusura e' stata decisa dalla rete
    /// (nessun operatore esecutore reale: Operatore resta solo audit e non va attribuito a nessuno).</summary>
    [JsonIgnore]
    public string OperatoreVisualizzato => EsecuzioneDaOperatore ? Operatore : Origine.Label();

    /// <summary>Esito effettivo dell'iterazione: di norma coincide con EsitoCalcolato (la condizione peggiore
    /// tra quelle in Condizioni) e viene tenuto allineato dal ViewModel a ogni modifica; EsitoSovrascritto lo
    /// scollega, lasciandolo fermo al valore scelto a mano finché non viene tolta la sovrascrittura.</summary>
    public Esito Esito { get; set; }

    public bool EsitoSovrascritto { get; set; }

    /// <summary>Motivazione obbligatoria quando EsitoSovrascritto è vero.</summary>
    public string MotivazioneOverride { get; set; } = "";

    public string Deviazioni { get; set; } = "";
    public string Note { get; set; } = "";
    public string? NonConformitaId { get; set; }

    /// <summary>Identificativo veicolo/banco (matricola per test a veicolo, riferimento banco/componente altrimenti).
    /// Obbligatorio (Addendum §17.4).</summary>
    public string Matricola { get; set; } = "";

    public string BaselineSw { get; set; } = "";
    public string BaselineHw { get; set; } = "";

    /// <summary>Valori dei campi con Ambito=PerRevisione (Addendum §3, feedback QA): stabili tra tutte le
    /// operazioni di questa revisione, senza doverli reinserire ogni volta, ma possono cambiare da una
    /// revisione alla successiva — a differenza di TestCase.ValoriStrumentiTest (PerTest, mai cambia) e di
    /// Condizione.ValoriStrumenti (PerOperazione, varia liberamente). Chiave: "NomeStrumento::NomeCampo".</summary>
    public Dictionary<string, string> ValoriStrumenti { get; set; } = new();

    /// <summary>Combinazioni servizio diagnostico/PIN/condizione/risposta provate in questa iterazione — quasi
    /// sempre una sola, ma spesso più di una quando lo stesso servizio va ricontrollato sotto condizioni diverse
    /// (Addendum §19): prima serviva un nuovo test case per ciascuna condizione, duplicando matricola/descrizione/
    /// requisiti/funzionalità già corretti nel primo. Sempre una lista, anche con un solo elemento, per avere
    /// un solo modello in tutta l'app invece di far convivere caso-singolo e caso-multiplo.</summary>
    public List<Condizione> Condizioni { get; set; } = new();

    /// <summary>Un punteggio di conformità per ciascun Requisito collegato al TestCase (TestCase.RequisitoId),
    /// specifico di questa Esecuzione — mantenuto sincronizzato con RequisitoId da PunteggioConformitaService.
    /// SincronizzaPunteggi, sullo stesso principio di Esecuzione.SincronizzaEsito per le Condizioni.</summary>
    public List<PunteggioRequisito> PunteggiRequisiti { get; set; } = new();

    /// <summary>Fotografia dei Dettagli del test nel momento in cui questa revisione è stata creata/salvata
    /// (Addendum §1, feedback QA: prima si salvavano solo i dati delle operazioni, non l'intero test — chi
    /// sfogliava una revisione storica vedeva comunque i Dettagli ATTUALI, non quelli dell'epoca). Null sulle
    /// revisioni create prima di questa modifica: mostrare "non disponibile", mai inventare un valore.</summary>
    public DettagliTestSnapshot? Dettagli { get; set; }

    /// <summary>La condizione peggiore vince (§19): NonConforme domina su tutto, poi le deviazioni, poi "da
    /// rivedere"; le condizioni non eseguite/non applicabili non abbassano l'esito complessivo da sole.
    /// Nessuna condizione ancora inserita equivale a "non eseguito".</summary>
    /// <summary>Per ogni operazione indipendente considera soltanto il tentativo piu recente. Le ripetizioni
    /// condividono OperazioneOrigineUid; le condizioni storiche restano nel JSON e nella UI, ma non continuano
    /// a rendere Non Conforme una revisione dopo che la stessa operazione e stata ripetuta con successo.</summary>
    [JsonIgnore]
    public IReadOnlyList<Condizione> CondizioniEffettive => Condizioni
        .Select((condizione, indice) => new { condizione, indice })
        .GroupBy(x => x.condizione.OperazioneOrigineUid ?? x.condizione.Uid)
        .Select(g => g.OrderBy(x => x.indice).Last().condizione)
        .ToList();

    /// <summary>Solo le operazioni realmente eseguite (decisione utente 6.4.0, punto 1: Conforme/Non
    /// Conforme/Conforme con deviazioni) concorrono all'esito aggregato — N.A. e "Da eseguire" non contano
    /// come tentativi svolti e da soli non possono mai rendere una revisione "Conforme". Nessuna operazione
    /// eseguita equivale quindi a "Da eseguire" per l'intera revisione, non più al legacy NonEseguitoNA.</summary>
    [JsonIgnore]
    public Esito EsitoCalcolato
    {
        get
        {
            var eseguite = CondizioniEffettive.Where(c => c.Esito.EEseguito()).ToList();
            return eseguite.Count == 0 ? Esito.DaEseguire : Peggiore(eseguite.Select(c => c.Esito));
        }
    }

    /// <summary>La condizione peggiore vince — stessa regola di EsitoCalcolato, esposta come helper riusabile
    /// (feedback utente 2026-08-24, terza revisione: serve anche a CoverageService.PerFunzionalita per decidere
    /// l'esito di una Funzionalità quando più operazioni della stessa esecuzione la coprono). I chiamanti che
    /// mescolano esiti eseguiti e non eseguiti devono filtrare con EEseguito() PRIMA di chiamare questo helper,
    /// esattamente come fa EsitoCalcolato sopra: qui non c'è alcun filtro implicito.</summary>
    public static Esito Peggiore(IEnumerable<Esito> esiti) => esiti.OrderBy(GravitaEsito).First();

    private static int GravitaEsito(Esito esito) => esito switch
    {
        Esito.NonConforme => 0,
        Esito.ConformeConDeviazioni => 1,
        Esito.DaRivedere => 2,
        Esito.NonEseguitoNA => 3,
        Esito.DaEseguire => 3,
        Esito.NonApplicabile => 4,
        Esito.Conforme => 5,
        _ => 6
    };

    /// <summary>Riallinea Esito a EsitoCalcolato, a meno che sia stato sovrascritto a mano. Va richiamata sia
    /// dal ViewModel a ogni modifica delle condizioni sia (per sicurezza, indipendentemente da eventuali buchi
    /// nella sincronizzazione live della UI) subito prima del salvataggio.</summary>
    public void SincronizzaEsito()
    {
        AggiornaDevalidazioniNascoste();
        if (!EsitoSovrascritto) Esito = EsitoCalcolato;
    }

    /// <summary>Regola guasto: la devalidazione compare (ed e' richiesta) solo quando la validazione dello stesso guasto e' Conforme o
    /// Conforme con deviazioni. Una devalidazione gia' eseguita non viene mai nascosta: l'evidenza registrata resta visibile.</summary>
    public void AggiornaDevalidazioniNascoste()
    {
        var effettive = CondizioniEffettive;
        static string Chiave(Condizione c) => (c.DtcRequisitoId ?? "").Trim() + "#" + (c.DtcVeicolo ?? "").Trim().ToUpperInvariant() + "#"
            + string.Join(",", c.Componente.Select(e => (e ?? "").Trim().ToUpperInvariant()).Where(e => e.Length > 0).OrderBy(e => e, StringComparer.Ordinal))
            + "#" + (c.Guasto ?? "").Trim().ToUpperInvariant();
        var validazioni = new Dictionary<string, Esito>();
        foreach (var c in effettive)
            if (c.RiguardaDtc && (c.DtcPasso ?? PassoDtc.Validazione) == PassoDtc.Validazione)
                validazioni[Chiave(c)] = c.Esito;
        var inseribili = new Dictionary<string, Condizione>();
        foreach (var c in Condizioni)
        {
            var nonRichiesta = c.RiguardaDtc && c.DtcPasso == PassoDtc.Devalidazione && c.Esito == Esito.DaEseguire
                && validazioni.TryGetValue(Chiave(c), out var esitoValidazione)
                && esitoValidazione is not (Esito.Conforme or Esito.ConformeConDeviazioni);
            c.DevalidazioneNonRichiesta = nonRichiesta;
            c.DevalidazioneNascosta = nonRichiesta && !c.DevalidazioneForzata;
            if (c.DevalidazioneNascosta) inseribili[Chiave(c)] = c;
        }
        foreach (var c in Condizioni)
            c.DevalidazioneInseribile = c.RiguardaDtc && (c.DtcPasso ?? PassoDtc.Validazione) == PassoDtc.Validazione
                && inseribili.TryGetValue(Chiave(c), out var dev) ? dev : null;
    }

}

/// <summary>Punteggio di conformità (0-5/N.A.) per un singolo Requisito collegato a questa Esecuzione:
/// un'Esecuzione può toccare più Requisiti (TestCase.RequisitoId), ciascuno con la propria valutazione,
/// perché la stessa esecuzione può essere pienamente conforme rispetto a un requisito e solo parzialmente
/// rispetto a un altro. Auto-calcolato da Esito quando non Sovrascritto — vedi PunteggioConformitaService.</summary>
public class PunteggioRequisito
{
    public string RequisitoId { get; set; } = "";
    public PunteggioConformita Valore { get; set; }
    public bool Sovrascritto { get; set; }

    /// <summary>Motivazione obbligatoria quando Sovrascritto è vero (stesso principio di
    /// Esecuzione.MotivazioneOverride per l'Esito).</summary>
    public string MotivazioneOverride { get; set; } = "";
}

/// <summary>Fotografia dei campi Dettagli del TestCase al momento del salvataggio di una revisione (Addendum
/// §1, feedback QA). Solo i campi che oggi vivono su TestCase invece che su Esecuzione — Matricola/BaselineSw/
/// BaselineHw/ValoriStrumenti restano dove sono già, direttamente su Esecuzione.</summary>
public class DettagliTestSnapshot
{
    public EcuSelection Componente { get; set; } = new();
    public string Veicolo { get; set; } = "";
    public List<string> Veicoli { get; set; } = new();
    public Guid TestTemplateUid { get; set; }

    /// <summary>Nome del template congelato al momento dello snapshot (Addendum redesign 2026-08): a
    /// differenza di TestTemplateUid, che punta al dato master live, questo non cambia mai — se il template
    /// viene poi rinominato o eliminato, una revisione storica continua a mostrare il nome di allora, mai un
    /// nome attuale che sfalserebbe la fotografia (stesso principio già applicato a Componente/Veicolo/Funzionalita
    /// qui sopra, tutte stringhe congelate).</summary>
    public string TestTemplateNome { get; set; } = "";
    public List<string> Funzionalita { get; set; } = new();
    public List<string> StrumentiUsati { get; set; } = new();
    public string DescrizioneBreve { get; set; } = "";
    public string Descrizione { get; set; } = "";
    public string? DescrizioneProblema { get; set; }
    public Dictionary<string, string> CampiCustom { get; set; } = new();
    public Dictionary<string, string> ValoriStrumentiTest { get; set; } = new();
}

/// <summary>Una condizione provata dentro un'iterazione — nel linguaggio degli operatori, una "reiterazione":
/// un servizio diagnostico/PIN con la condizione in cui è stato inviato e la risposta ottenuta, più l'esito
/// di quella specifica reiterazione (Addendum §19; terminologia allineata nel redesign 2026-08).</summary>
public class Condizione : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Guid Uid { get; set; } = Guid.NewGuid();

    /// <summary>Identita stabile dell'operazione logica. Null indica una nuova operazione indipendente e usa
    /// Uid come radice; il comando Ripeti copia la radice dell'operazione scelta. In questo modo soltanto
    /// l'ultimo tentativo della stessa operazione determina esito e anomalie, senza confronti fragili sui testi.</summary>
    public Guid? OperazioneOrigineUid { get; set; }

    /// <summary>Codice guasto (anagrafica DtcRequisitoEcu) a cui si riferisce questa operazione (6.5.0, punto 7):
    /// opzionale e additivo, null/assente = operazione normale (comportamento invariato per ogni Test esistente).
    /// Un'operazione con Guasto valorizzato conta nelle esecuzioni dell'operatore e nel punteggio di conformita per
    /// requisito (tramite l'esito della REV), ma NON nella copertura per Funzionalita ne nei report UnitLogger.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Guasto
    {
        get => _dtc;
        set { _dtc = value; OnPropertyChanged(nameof(Guasto)); OnPropertyChanged(nameof(RiguardaDtc)); OnPropertyChanged(nameof(DtcEtichetta)); OnPropertyChanged(nameof(DtcDaStrumento)); OnPropertyChanged(nameof(DtcLegacy)); }
    }
    private string? _dtc;

    /// <summary>Requisito (Requisito.Id) cui appartiene il guasto referenziato: la voce guasto vive dentro il Requisito
    /// (Requisito.Guasto), quindi il riferimento completo e (DtcRequisitoId, Guasto, componente dell'operazione).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DtcRequisitoId
    {
        get => _dtcRequisitoId;
        set { _dtcRequisitoId = value; OnPropertyChanged(nameof(DtcRequisitoId)); OnPropertyChanged(nameof(DtcEtichetta)); }
    }
    private string? _dtcRequisitoId;

    /// <summary>Veicolo della voce guasto referenziata (6.5.1, schema dati 9): la voce e identificata da (DtcVeicolo, componente,
    /// Guasto) dentro il requisito. Additivo: null/assente = guasto senza veicolo (dati precedenti, comportamento invariato).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DtcVeicolo
    {
        get => _dtcVeicolo;
        set { _dtcVeicolo = value; OnPropertyChanged(nameof(DtcVeicolo)); OnPropertyChanged(nameof(DtcEtichetta)); }
    }
    private string? _dtcVeicolo;

    /// <summary>Scelta ESPLICITA dell'utente (6.5.1): true = il guasto e stato lasciato/impostato "(nessuno)" apposta, quindi il riabbinamento
    /// automatico a un requisito Extra (cambio strumenti/componente/requisiti) non lo tocca. Additivo: null/assente = nessuna scelta esplicita
    /// (dati precedenti, comportamento 6.5.0 invariato: l'operazione senza requisito si riabbina). Mai scritto se null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DtcRequisitoVuotoScelto { get; set; }

    /// <summary>Strumento PROPRIETARIO dell'operazione (6.5.1, schema dati 9): le operazioni generate per uno strumento portano il
    /// suo nome. Null = operazione legacy/manuale "non posseduta" da nessuno strumento: NON va mai cancellata dalle
    /// rigenerazioni automatiche (vedi <see cref="TestLoom.App.Services.DtcOperazioneRegole.PuoDeselezionare"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Strumento
    {
        get => _strumento;
        set { _strumento = value; OnPropertyChanged(nameof(Strumento)); OnPropertyChanged(nameof(DtcDaStrumento)); OnPropertyChanged(nameof(DtcLegacy)); }
    }
    private string? _strumento;

    /// <summary>Campi sconosciuti di versioni piu recenti: preservati nella riscrittura invece di perderli in silenzio.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Etichetta del guasto referenziato come la mostra il selettore dell'editor (DtcRequisito.Etichetta), null se
    /// l'operazione non riguarda un guasto. Non persistita.</summary>
    [JsonIgnore]
    public string? DtcEtichetta => RiguardaDtc && !string.IsNullOrWhiteSpace(DtcRequisitoId) ? DtcRequisito.Etichetta(DtcRequisitoId, Guasto, Componente.FirstOrDefault(), DtcVeicolo) : null;

    /// <summary>Passo (validazione/devalidazione) dell'operazione guasto; null se Guasto non e valorizzato.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PassoDtc? DtcPasso
    {
        get => _dtcPasso;
        set { _dtcPasso = value; OnPropertyChanged(nameof(DtcPasso)); }
    }
    private PassoDtc? _dtcPasso;

    private string? _dtcAvviso;
    /// <summary>Avviso transitorio sull'operazione guasto (requisito scollegato dal test, nessuna componente): mai salvato.</summary>
    [JsonIgnore]
    public string? DtcAvviso
    {
        get => _dtcAvviso;
        set { _dtcAvviso = value; OnPropertyChanged(nameof(DtcAvviso)); OnPropertyChanged(nameof(HaDtcAvviso)); }
    }
    [JsonIgnore] public bool HaDtcAvviso => !string.IsNullOrEmpty(_dtcAvviso);

    private string? _dtcStatoTesto;
    /// <summary>Riepilogo di sola lettura mostrato nell'operazione (stato del guasto + N/M del requisito Extra): calcolato in
    /// background dall'editor, transitorio, mai salvato.</summary>
    [JsonIgnore]
    public string? DtcStatoTesto
    {
        get => _dtcStatoTesto;
        set { _dtcStatoTesto = value; OnPropertyChanged(nameof(DtcStatoTesto)); }
    }

    /// <summary>Vero se l'operazione e un'operazione guasto (Guasto valorizzato).</summary>
    [JsonIgnore]
    public bool RiguardaDtc => !string.IsNullOrWhiteSpace(Guasto);

    /// <summary>Solo runtime (mai persistito): vero per un passo di DEVALIDAZIONE guasto ancora "Da eseguire" la cui VALIDAZIONE
    /// (stesso guasto) non e' Conforme/Conforme con deviazioni. Se non si riesce a validare un guasto non ha senso devalidarlo: la riga
    /// non va mostrata ne' richiesta per concludere il Test. Calcolato da <see cref="Esecuzione.AggiornaDevalidazioniNascoste"/>.</summary>
    [JsonIgnore]
    public bool DevalidazioneNascosta
    {
        get => _devalidazioneNascosta;
        set { if (_devalidazioneNascosta == value) return; _devalidazioneNascosta = value; OnPropertyChanged(nameof(DevalidazioneNascosta)); }
    }
    private bool _devalidazioneNascosta;

    /// <summary>Solo runtime: l'operatore ha scelto di inserire comunque la devalidazione (facoltativa) anche se la validazione
    /// non e' conforme. Resta Non richiesta: non blocca la conclusione del Test.</summary>
    [JsonIgnore]
    public bool DevalidazioneForzata
    {
        get => _devalidazioneForzata;
        set { if (_devalidazioneForzata == value) return; _devalidazioneForzata = value; OnPropertyChanged(nameof(DevalidazioneForzata)); }
    }
    private bool _devalidazioneForzata;

    /// <summary>Solo runtime: devalidazione ancora "Da eseguire" con validazione non conforme. Non e' richiesta per concludere il Test.</summary>
    [JsonIgnore]
    public bool DevalidazioneNonRichiesta
    {
        get => _devalidazioneNonRichiesta;
        set { if (_devalidazioneNonRichiesta == value) return; _devalidazioneNonRichiesta = value; OnPropertyChanged(nameof(DevalidazioneNonRichiesta)); }
    }
    private bool _devalidazioneNonRichiesta;

    /// <summary>Solo runtime, sulla VALIDAZIONE: esiste la devalidazione dello stesso guasto, nascosta e inseribile su richiesta.</summary>
    [JsonIgnore]
    public Condizione? DevalidazioneInseribile
    {
        get => _devalidazioneInseribile;
        set { if (ReferenceEquals(_devalidazioneInseribile, value)) return; _devalidazioneInseribile = value; OnPropertyChanged(nameof(DevalidazioneInseribile)); OnPropertyChanged(nameof(HaDevalidazioneInseribile)); }
    }
    private Condizione? _devalidazioneInseribile;
    [JsonIgnore] public bool HaDevalidazioneInseribile => _devalidazioneInseribile != null;

    /// <summary>Operazione guasto di PROPRIETA di uno strumento (blocco "Aggiungi guasto" del Test): passo/componente/requisito si gestiscono dal
    /// blocco, non operazione per operazione.</summary>
    [JsonIgnore]
    public bool DtcDaStrumento => RiguardaDtc && !string.IsNullOrWhiteSpace(Strumento);

    /// <summary>Operazione guasto legacy/manuale (nessuno strumento proprietario): selettore per operazione invariato.</summary>
    [JsonIgnore]
    public bool DtcLegacy => RiguardaDtc && string.IsNullOrWhiteSpace(Strumento);

    /// <summary>A quale/i componente (tra quelle collegate al test, TestCase.Componente) questa operazione appartiene
    /// (feedback utente 2026-08-25, punto 9): con un test multi-componente, alcune componente possono avere operazioni per
    /// una sola Funzionalità mentre altre ne coprono più d'una, quindi l'abbinamento componente↔Funzionalità nasce
    /// dal basso, dalle singole operazioni, non da una tabella dichiarata a parte. Con una sola componente sul test è
    /// comunque valorizzata (nessuna ambiguità possibile, ma resta esplicita); con più componente nasce con tutte
    /// preselezionate e l'utente restringe solo dove serve (vedi TestCaseEditorViewModel per
    /// l'inizializzazione). CoverageService.PerEcu calcola l'esito di un componente solo dalle operazioni che la
    /// includono qui, stesso principio già usato per Funzionalita.</summary>
    private EcuSelection _ecu = new();

    /// <summary>Setter con notifica (feedback utente 2026-08-25 — riscontrato via registrazione: il pulsante
    /// componente dell'operazione restava sulla dicitura vuota anche dopo che il codice aveva già valorizzato il
    /// campo, perché una semplice auto-property non avvisa mai la UI di un riassegnamento fatto da codice
    /// invece che dall'interazione diretta dell'utente col controllo — stesso principio già usato per Esito):
    /// TestCaseEditorViewModel riallinea Componente su un'operazione già a video quando cambia l'elenco componente del test
    /// (RiallineaEcuOperazioniDopoCambioElenco), e senza questa notifica il binding restava "congelato" al
    /// valore letto al primo render.</summary>
    public EcuSelection Componente
    {
        get => _ecu;
        set { _ecu = value; OnPropertyChanged(nameof(Componente)); }
    }

    private string _funzionalita = "";

    /// <summary>Quale Funzionalità (tra quelle collegate al test, TestCase.Funzionalita) questa operazione
    /// sta verificando (feedback utente 2026-08-24, terza revisione — sostituisce la vecchia Etichetta libera
    /// tipo "Condizione A"): con un test che copre più funzionalità insieme, serve a sapere quale fallisce e a
    /// calcolare la copertura per-funzionalità sulla singola operazione invece che sull'esito aggregato
    /// dell'intero test (vedi CoverageService.PerFunzionalita). Obbligatoria quando il test ne collega 2+;
    /// con una sola è comunque valorizzata (nessuna ambiguità possibile, ma resta esplicita a video). Le
    /// sotto-condizioni della stessa Funzionalità (es. "chiave ON" vs "chiave OFF") si distinguono ora nella
    /// Nota di questa operazione, non più qui. Setter con notifica, stesso motivo di Componente sopra.</summary>
    public string Funzionalita
    {
        get => _funzionalita;
        set { _funzionalita = value; OnPropertyChanged(nameof(Funzionalita)); }
    }

    private Esito _esito;
    [JsonIgnore] public Esito EsitoPrecedente { get; private set; }
    public Esito Esito
    {
        get => _esito;
        set
        {
            if (_esito == value) return;
            EsitoPrecedente = _esito;
            _esito = value;
            OnPropertyChanged(nameof(Esito));
            OnPropertyChanged(nameof(RichiedeMotivazioneNonApplicabile));
            OnPropertyChanged(nameof(RichiedeDescrizioneDeviazione));
            OnPropertyChanged(nameof(RichiedeDescrizioneProblema));
            OnPropertyChanged(nameof(RichiedeGestioneSegnalazione));
        }
    }

    /// <summary>Nota libera propria di questa reiterazione (redesign 2026-08): ogni prova fatta con condizioni
    /// diverse deve poter avere la sua osservazione, distinta dalla nota generale dell'intera revisione.</summary>
    public string Nota { get; set; } = "";

    /// <summary>Motivazione obbligatoria quando Esito è NonApplicabile.</summary>
    public string MotivazioneNonApplicabile { get; set; } = "";

    public string DescrizioneDeviazione { get; set; } = "";

    private string _descrizioneProblema = "";

    /// <summary>Con notifica: il pannello segnalazioni del Test ricalcola il suggerimento "stessa anomalia" mentre
    /// l'operatore scrive il problema (vedi TestCaseEditorViewModel.AggiornaSuggerimentiSegnalazioni). Il valore
    /// salvato su disco resta identico: cambia solo il modo in cui e' conservato in memoria.</summary>
    public string DescrizioneProblema
    {
        get => _descrizioneProblema;
        set { _descrizioneProblema = value; OnPropertyChanged(nameof(DescrizioneProblema)); }
    }

    /// <summary>Evidenziazione dei 4 testi liberi della condizione (feedback utente 2026-08-24, punto 8) —
    /// solo dentro un'operazione, non su testi del test/della revisione. Un colore sull'intero campo: questi
    /// sono stringhe singole, non elenchi di righe come CampoStrumentoValore.RigheListaTesto.</summary>
    public ColoreEvidenziazione ColoreNota { get; set; } = ColoreEvidenziazione.Nessuno;
    public ColoreEvidenziazione ColoreMotivazioneNonApplicabile { get; set; } = ColoreEvidenziazione.Nessuno;
    public ColoreEvidenziazione ColoreDescrizioneDeviazione { get; set; } = ColoreEvidenziazione.Nessuno;
    public ColoreEvidenziazione ColoreDescrizioneProblema { get; set; } = ColoreEvidenziazione.Nessuno;

    [JsonIgnore] public bool RichiedeMotivazioneNonApplicabile => Esito == Esito.NonApplicabile;
    [JsonIgnore] public bool RichiedeDescrizioneDeviazione => Esito == Esito.ConformeConDeviazioni;
    [JsonIgnore] public bool RichiedeDescrizioneProblema => Esito == Esito.NonConforme;
    /// <summary>Quick UI action hint for results that need an anomaly/NC review. Not persisted.</summary>
    [JsonIgnore] public bool RichiedeGestioneSegnalazione => Esito is Esito.NonConforme or Esito.ConformeConDeviazioni;

    /// <summary>Valori dei campi degli strumenti con CampiCostantiPerTest=false, cioè quelli che variano per
    /// singola reiterazione (es. comando/risposta diagnostica). Chiave: "NomeStrumento::NomeCampo" (Addendum
    /// redesign 2026-08, revisione feedback QA: prima erano proprietà fisse su CampiSpecifici, ora campi
    /// configurabili da Impostazioni → Strumenti).</summary>
    public Dictionary<string, string> ValoriStrumenti { get; set; } = new();

    /// <summary>Durata manuale dell'operazione in minuti interi (TIME, 6.6.1): null = non nota, 0 = zero noto. Non e' una
    /// proprieta JSON: vive in <see cref="ValoriStrumenti"/> sotto la chiave riservata (vedi DurataOperazione).</summary>
    [JsonIgnore]
    public int? DurataMinuti
    {
        get => DurataOperazione.Leggi(ValoriStrumenti);
        set
        {
            ValoriStrumenti ??= new();
            DurataOperazione.Scrivi(ValoriStrumenti, value);
            // Una modifica dell'operatore non e' piu' una copia automatica: non verra' mai sovrascritta dalla regola di copia.
            DurataDaCopiaAutomatica = false;
            OnPropertyChanged(nameof(DurataMinuti));
        }
    }

    /// <summary>Numero di evidenze (allegati del Test) collegate a questa operazione nella sua REV. Non persistita: la calcola
    /// l'editor confrontando i collegamenti riservati con gli allegati realmente presenti (vedi EvidenzaOperazione).</summary>
    [JsonIgnore]
    public int NumeroEvidenze { get; private set; }

    [JsonIgnore] public bool HaEvidenze => NumeroEvidenze > 0;

    internal void ImpostaNumeroEvidenze(int numero)
    {
        if (numero == NumeroEvidenze) return;
        NumeroEvidenze = numero;
        OnPropertyChanged(nameof(NumeroEvidenze));
        OnPropertyChanged(nameof(HaEvidenze));
    }

    /// <summary>Ripristina il flag di copia automatica e notifica il campo TIME dopo l'annullamento di un import UnitLogger.
    /// La chiave del dizionario e' gia' stata riportata al valore precedente dal piano di annullamento.</summary>
    internal void NotificaDurataRipristinataDaImport(bool eraCopiaAutomatica)
    {
        DurataDaCopiaAutomatica = eraCopiaAutomatica;
        OnPropertyChanged(nameof(DurataMinuti));
    }

    /// <summary>True se la durata presente e' stata proposta dalla regola di copia (e non ancora modificata dall'operatore).
    /// Transitoria: la regola di copia puo' sostituirla quando cambiano componente o Funzionalita; un valore dell'operatore non lo e' mai.</summary>
    [JsonIgnore]
    public bool DurataDaCopiaAutomatica { get; private set; }

    /// <summary>Imposta la durata proposta dalla regola di copia (null = nessun precedente: svuota solo una copia automatica precedente).
    /// Un valore digitato dall'operatore, anche vuoto, non viene mai sovrascritto: vedi <see cref="DurataModificataDallOperatore"/>.</summary>
    public void ImpostaDurataDaCopia(int? minuti)
    {
        if (DurataModificataDallOperatore) return;
        ValoriStrumenti ??= new();
        DurataOperazione.Scrivi(ValoriStrumenti, minuti);
        DurataDaCopiaAutomatica = minuti is not null;
        OnPropertyChanged(nameof(DurataMinuti));
    }

    /// <summary>True una volta che l'operatore ha toccato il campo durata (valore, vuoto o testo non valido): la regola di copia non lo
    /// tocca piu'. Non e' un valore: un campo lasciato vuoto dall'operatore resta vuoto. Transitoria, mai salvata; sta sulla
    /// modifica dell'operatore (editor, +/-), non sulle assegnazioni programmatiche del modello.</summary>
    [JsonIgnore]
    public bool DurataModificataDallOperatore { get; private set; }

    /// <summary>Registra un intervento dell'operatore sulla durata (vedi <see cref="DurataModificataDallOperatore"/>).</summary>
    internal void SegnaDurataModificataDallOperatore() => DurataModificataDallOperatore = true;

    private DurataTestoModifica? _durataTesto;

    /// <summary>Testo modificabile della durata (campo dell'editor): un testo non valido resta nel campo con il suo messaggio e non
    /// cambia il modello. Creato solo quando la vista lo usa.</summary>
    [JsonIgnore]
    public DurataTestoModifica DurataTesto => _durataTesto ??= new DurataTestoModifica(this);

    /// <summary>Stacca il testo della durata dal modello quando l'operazione esce dalla revisione (nessuna sottoscrizione residua).</summary>
    internal void ScollegaDurataTesto()
    {
        _durataTesto?.Dispose();
        _durataTesto = null;
    }

    private List<CampoStrumentoValore>? _campiVisual;

    /// <summary>Campi senza Gruppo degli strumenti selezionati per questa reiterazione, ricalcolati dal
    /// ViewModel ad ogni cambio di Strumenti usati. Transitoria, mai salvata.</summary>
    [JsonIgnore]
    public List<CampoStrumentoValore>? CampiVisual
    {
        get => _campiVisual;
        set { _campiVisual = value; OnPropertyChanged(nameof(CampiVisual)); }
    }

    private List<GruppoCampiStrumento>? _gruppiVisual;

    /// <summary>Campi raggruppati (Gruppo valorizzato, es. "Guasto Injection") degli strumenti selezionati per
    /// questa reiterazione, ciascun gruppo renderizzato in un pannello a tendina collassato. Transitoria.</summary>
    [JsonIgnore]
    public List<GruppoCampiStrumento>? GruppiVisual
    {
        get => _gruppiVisual;
        set { _gruppiVisual = value; OnPropertyChanged(nameof(GruppiVisual)); }
    }

    /// <summary>Segnalazione generata automaticamente al salvataggio quando questa reiterazione risulta Non
    /// Conforme/Con deviazioni (redesign 2026-08: prima il collegamento viveva su ProvaTest/VerificaTest,
    /// struttura ormai eliminata — ogni reiterazione porta già il proprio esito, non serve più quel livello).</summary>
    public Guid? SegnalazioneUid { get; set; }

}

/// <summary>Wrapper di editing per un singolo campo-strumento in un contesto specifico (una Condizione per i
/// campi che variano per reiterazione, il TestCase per quelli costanti): incapsula lettura/scrittura nel
/// dizionario di valori sottostante dietro proprietà bindabili, una per tipo di controllo di editing.</summary>
/// <summary>Qualunque testo di un'operazione che può avere un colore di evidenziazione (feedback utente
/// 2026-08-24, punto 8) — un'unica interfaccia così il menu contestuale "Evidenzia" in XAML/code-behind
/// resta uno solo, riusabile sia per CampoStrumentoValore (campo intero) sia per RigaEvidenziata (singola
/// riga di un campo ListaTesto), invece di duplicare l'handler per ogni tipo.</summary>
public interface IEvidenziabile
{
    ColoreEvidenziazione Colore { get; set; }
}

public class CampoStrumentoValore : INotifyPropertyChanged, IEvidenziabile
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private readonly Dictionary<string, string> _valori;
    private readonly string _chiave;
    private CampoStrumentoValore? _campoPerRicordareDefault;
    private readonly CampoStrumentoValore? _campoOrigineDefault;
    private readonly string? _valoreDefaultCampoVuoto;
    private string? _valoreDefaultPersonaleApplicabile;
    private string? _valoreDefaultMasterApplicabile;

    public string Strumento { get; }
    public string Nome { get; }
    /// <summary>Campo da passare ai comandi di gestione del default personale. Un Checkbox vuoto viene
    /// rappresentato come false soltanto per questi comandi: il dizionario del Test resta vuoto finché
    /// l'operatore non cambia esplicitamente il toggle.</summary>
    [JsonIgnore]
    public CampoStrumentoValore CampoPerRicordareDefault
    {
        get
        {
            if (_campoOrigineDefault != null || Tipo != TipoCampoStrumento.Checkbox || Valore != null) return this;
            return _campoPerRicordareDefault ??= new CampoStrumentoValore(this, "false");
        }
    }
    /// <summary>Automation id stabile per l'azione di salvataggio del default personale in UI e nei test.</summary>
    public string AutomationIdRicordaDefault => $"Editor_Campo_{Strumento}_{Nome}_Default_Button";
    /// <summary>Automation id stabile per l'azione di rimozione del default personale in UI e nei test.</summary>
    public string AutomationIdRimuoviDefault => $"Editor_Campo_{Strumento}_{Nome}_RimuoviDefault_Button";
    /// <summary>Vero solo per i campi di uno strumento che supportano preferenze personali. I campi custom del
    /// template usano TestCase.CampiCustom e non hanno default associati al profilo dell'operatore.</summary>
    [JsonIgnore]
    public bool PuoRicordareDefault => _campoOrigineDefault?.PuoRicordareDefault
        ?? (!string.IsNullOrWhiteSpace(Strumento) && Tipo != TipoCampoStrumento.File);

    public TipoCampoStrumento Tipo { get; }
    public List<string> OpzioniLista { get; }

    /// <summary>Voci del menu a tendina del Test: le opzioni della definizione più il valore già salvato sul Test, se non compare più
    /// tra quelle (opzione rinominata o rimossa dopo il salvataggio). Così il valore resta leggibile e non sparisce dal campo senza una
    /// scelta dell'operatore. Vale allo stesso modo per i campi strumento e per i campi custom. Fissata alla costruzione del campo.</summary>
    public List<string> OpzioniVisualizzate { get; }

    private List<string> CalcolaOpzioniVisualizzate()
    {
        var voci = new List<string>(OpzioniLista ?? new List<string>());
        if (_valori.TryGetValue(_chiave, out var corrente) && !string.IsNullOrEmpty(corrente) && !voci.Contains(corrente)) voci.Add(corrente);
        return voci;
    }

    /// <summary>Solo per Tipo=Numero (Addendum: collaudo live 2026-08-17): unità mostrata accanto al valore,
    /// es. "km"/"°C"/"%". Vuota/null se non configurata — nessun cambio visivo per i campi esistenti.</summary>
    public string? UnitaMisura { get; }
    public bool HaUnitaMisura => !string.IsNullOrWhiteSpace(UnitaMisura);

    /// <summary>Valore proposto per il campo: quello personale salvato dall'operatore (Operatore.ValoriDefaultCampi)
    /// se presente, altrimenti il fallback configurato dal Master su DefinizioneCampoStrumento.ValoreDefault
    /// (feedback utente 2026-08-24, punto 3). Null per i campi custom (secondo costruttore), che non hanno
    /// questo concetto.</summary>
    public string? ValoreDefault { get; }

    /// <summary>Vero se il campo ha un default configurato e il valore corrente coincide ancora con esso —
    /// non distingue "mai toccato" da "reinserito uguale di proposito": in entrambi i casi vale comunque la
    /// pena di un promemoria non bloccante al salvataggio, non c'è modo di saperlo con certezza altrimenti.</summary>
    public bool EDefaultNonModificato => !string.IsNullOrEmpty(ValoreDefault) && Valore == ValoreDefault;

    /// <summary>Default personale dell'operatore attivo per questo campo (null se non ne ha salvato uno).</summary>
    private string? _valoreDefaultPersonale;
    public string? ValoreDefaultPersonale
    {
        get => _campoOrigineDefault != null ? _campoOrigineDefault.ValoreDefaultPersonale : _valoreDefaultPersonale;
        private set => _valoreDefaultPersonale = value;
    }

    /// <summary>Default configurato dal Master sulla definizione del campo (null se assente).</summary>
    public string? ValoreDefaultMaster { get; }

    private static string? ValidaDefault(TipoCampoStrumento tipo, IReadOnlyList<string>? opzioni, string? valore)
    {
        if (string.IsNullOrWhiteSpace(valore)) return null;
        var normalizzato = valore.Trim();
        return tipo switch
        {
            TipoCampoStrumento.Lista => opzioni?.FirstOrDefault(opzione => opzione.Equals(normalizzato, StringComparison.OrdinalIgnoreCase)),
            TipoCampoStrumento.Checkbox when normalizzato.Equals("true", StringComparison.OrdinalIgnoreCase) => "true",
            TipoCampoStrumento.Checkbox when normalizzato.Equals("false", StringComparison.OrdinalIgnoreCase) => "false",
            TipoCampoStrumento.Checkbox => null,
            TipoCampoStrumento.File => null, // Un filename non porta con sé l'allegato del Test precedente.
            TipoCampoStrumento.Data => DateTime.TryParseExact(normalizzato, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var data) ? data.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) : null,
            _ => normalizzato,
        };
    }

    /// <summary>Vero se il valore corrente coincide col TUO default personale (prevale sul Master).</summary>
    public bool EDefaultPersonale => _campoOrigineDefault?.EDefaultPersonale
        ?? (!string.IsNullOrEmpty(_valoreDefaultPersonaleApplicabile) && Valore == _valoreDefaultPersonaleApplicabile);

    /// <summary>Vero se il valore corrente coincide col default del Master e non col default personale.</summary>
    public bool EDefaultMaster => _campoOrigineDefault?.EDefaultMaster
        ?? (!EDefaultPersonale && !string.IsNullOrEmpty(_valoreDefaultMasterApplicabile) && Valore == _valoreDefaultMasterApplicabile);

    public bool HaDefaultPersonale => _campoOrigineDefault?.HaDefaultPersonale
        ?? (Tipo != TipoCampoStrumento.File && !string.IsNullOrEmpty(ValoreDefaultPersonale));
    public bool MostraRimuoviDefault => PuoRicordareDefault && HaDefaultPersonale;

    /// <summary>Vero se il campo e' stato precompilato da un default alla costruzione del form (valore assente
    /// nel dizionario e default presente): usato per spiegare l'origine del valore mostrato.</summary>
    public bool PrecompilatoDaDefault { get; }

    private bool _confermaDefaultSalvato;
    /// <summary>Conferma visibile e transitoria dopo "Ricorda come mio default" (resettata dal ViewModel).</summary>
    public bool ConfermaDefaultSalvato
    {
        get => _campoOrigineDefault?.ConfermaDefaultSalvato ?? _confermaDefaultSalvato;
        set
        {
            if (_campoOrigineDefault != null)
            {
                _campoOrigineDefault.ConfermaDefaultSalvato = value;
                return;
            }
            _confermaDefaultSalvato = value;
            OnPropertyChanged(nameof(ConfermaDefaultSalvato));
            OnPropertyChanged(nameof(MostraEtichettaDefaultPersonale));
            OnPropertyChanged(nameof(MostraRicordaDefault));
        }
    }

    public bool MostraEtichettaDefaultPersonale => _campoOrigineDefault?.MostraEtichettaDefaultPersonale
        ?? (EDefaultPersonale && !ConfermaDefaultSalvato);
    /// <summary>"Ricorda" non serve quando il valore e' gia' il tuo default (nulla da salvare) e c'e' un valore.</summary>
    public bool MostraRicordaDefault => _campoOrigineDefault?.MostraRicordaDefault
        ?? (PuoRicordareDefault
            && (!string.IsNullOrEmpty(Valore) || (Tipo == TipoCampoStrumento.Checkbox && _valoreDefaultPersonaleApplicabile != "false"))
            && !EDefaultPersonale && !ConfermaDefaultSalvato);

    /// <summary>Aggiorna il default personale dopo un salvataggio/rimozione, rinfrescando gli indicatori.</summary>
    public void ImpostaDefaultPersonale(string? valore)
    {
        if (_campoOrigineDefault != null)
        {
            _campoOrigineDefault.ImpostaDefaultPersonale(valore);
            return;
        }
        ValoreDefaultPersonale = string.IsNullOrEmpty(valore) ? null : valore;
        _valoreDefaultPersonaleApplicabile = ValidaDefault(Tipo, OpzioniLista, ValoreDefaultPersonale);
        OnPropertyChanged(nameof(ValoreDefaultPersonale));
        NotificaIndicatoriDefault();
    }

    private void NotificaIndicatoriDefault()
    {
        OnPropertyChanged(nameof(EDefaultPersonale));
        OnPropertyChanged(nameof(EDefaultMaster));
        OnPropertyChanged(nameof(HaDefaultPersonale));
        OnPropertyChanged(nameof(MostraRimuoviDefault));
        OnPropertyChanged(nameof(MostraEtichettaDefaultPersonale));
        OnPropertyChanged(nameof(MostraRicordaDefault));
    }

    public CampoStrumentoValore(Dictionary<string, string> valori, string strumento, DefinizioneCampoStrumento definizione,
        string? valoreDefaultOperatore = null, bool materializzaDefault = true)
    {
        _valori = valori;
        Strumento = strumento;
        Nome = definizione.Nome;
        Tipo = definizione.Tipo;
        OpzioniLista = definizione.OpzioniLista;
        UnitaMisura = definizione.UnitaMisura;
        ValoreDefaultPersonale = string.IsNullOrEmpty(valoreDefaultOperatore) ? null : valoreDefaultOperatore;
        ValoreDefaultMaster = string.IsNullOrEmpty(definizione.ValoreDefault) ? null : definizione.ValoreDefault;
        _valoreDefaultPersonaleApplicabile = ValidaDefault(Tipo, OpzioniLista, ValoreDefaultPersonale);
        _valoreDefaultMasterApplicabile = ValidaDefault(Tipo, OpzioniLista, ValoreDefaultMaster);
        ValoreDefault = _valoreDefaultPersonaleApplicabile ?? _valoreDefaultMasterApplicabile;
        _chiave = $"{strumento}::{definizione.Nome}";
        PrecompilatoDaDefault = materializzaDefault && !valori.ContainsKey(_chiave) && !string.IsNullOrEmpty(ValoreDefault);
        // Il default è un dato della revisione solo quando si sta costruendo/modificando la revisione corrente.
        // In visualizzazione storica materializzaDefault=false: leggere una REV non deve mai introdurre nel
        // dizionario valori/default configurati mesi dopo, altrimenti un successivo Salva contaminerebbe lo storico.
        if (materializzaDefault && !_valori.ContainsKey(_chiave) && !string.IsNullOrEmpty(ValoreDefault))
            _valori[_chiave] = ValoreDefault;
        OpzioniVisualizzate = CalcolaOpzioniVisualizzate();
    }

    private CampoStrumentoValore(CampoStrumentoValore origine, string valoreDefaultCampoVuoto)
    {
        _campoOrigineDefault = origine;
        _valoreDefaultCampoVuoto = valoreDefaultCampoVuoto;
        _valori = origine._valori;
        _chiave = origine._chiave;
        Strumento = origine.Strumento;
        Nome = origine.Nome;
        Tipo = origine.Tipo;
        OpzioniLista = origine.OpzioniLista;
        UnitaMisura = origine.UnitaMisura;
        ValoreDefault = origine.ValoreDefault;
        ValoreDefaultPersonale = origine.ValoreDefaultPersonale;
        ValoreDefaultMaster = origine.ValoreDefaultMaster;
        PrecompilatoDaDefault = origine.PrecompilatoDaDefault;
        _valoreDefaultPersonaleApplicabile = origine._valoreDefaultPersonaleApplicabile;
        _valoreDefaultMasterApplicabile = origine._valoreDefaultMasterApplicabile;
        OpzioniVisualizzate = origine.OpzioniVisualizzate;
    }

    /// <summary>Chiave diretta invece di "Strumento::Nome" (Addendum: grill-me 2026-08-18, follow-up): usato
    /// per i campi custom del catalogo campi di sistema e per i "campi libero" del template, che non sono
    /// legati a nessuno Strumento — TestCase.CampiCustom usa una chiave semplice, non composta.</summary>
    public CampoStrumentoValore(Dictionary<string, string> valori, string chiave, string nome, TipoCampoStrumento tipo, List<string> opzioniLista, string? unitaMisura)
    {
        _valori = valori;
        Strumento = "";
        Nome = nome;
        Tipo = tipo;
        OpzioniLista = opzioniLista;
        UnitaMisura = unitaMisura;
        _chiave = chiave;
        OpzioniVisualizzate = CalcolaOpzioniVisualizzate();
    }

    public string? Valore
    {
        get => _campoOrigineDefault != null
            ? _campoOrigineDefault.Valore ?? _valoreDefaultCampoVuoto
            : _valori.TryGetValue(_chiave, out var v) ? v : null;
        set
        {
            if (_campoOrigineDefault != null)
            {
                _campoOrigineDefault.Valore = value;
                return;
            }
            if (string.IsNullOrEmpty(value)) _valori.Remove(_chiave); else _valori[_chiave] = value;
            OnPropertyChanged(nameof(Valore));
            OnPropertyChanged(nameof(ValoreCheckbox));
            OnPropertyChanged(nameof(ValoreCheckboxVisual));
            OnPropertyChanged(nameof(EDefaultNonModificato));
            NotificaIndicatoriDefault();
        }
    }

    public bool? ValoreCheckbox
    {
        get => Valore switch { "true" => true, "false" => false, _ => null };
        set => Valore = value == true ? "true" : "false";
    }

    /// <summary>Valore non nullo usato dal ToggleSwitch nell'editor: un campo vuoto viene disegnato come spento
    /// senza scrivere "false" nel test finché l'utente non cambia lo stato.</summary>
    public bool ValoreCheckboxVisual
    {
        get => ValoreCheckbox == true;
        set => ValoreCheckbox = value;
    }

    /// <summary>Evidenziazione sull'intero campo (feedback utente 2026-08-24, punto 8) — solo dentro
    /// un'operazione (la UI la espone soltanto lì, non su CampiTestCostanti/CampiRevisioneCostanti). Chiave
    /// compagna nello stesso dizionario invece di un nuovo campo sul modello: nessuna modifica allo schema di
    /// TestCase/Condizione, coerente con come già vive il resto (ValoriStrumenti resta un semplice
    /// Dictionary&lt;string,string&gt;).</summary>
    private string ChiaveColore => _chiave + "#colore";

    public ColoreEvidenziazione Colore
    {
        get => _valori.TryGetValue(ChiaveColore, out var v) && Enum.TryParse<ColoreEvidenziazione>(v, out var c) ? c : ColoreEvidenziazione.Nessuno;
        set
        {
            if (value == ColoreEvidenziazione.Nessuno) _valori.Remove(ChiaveColore); else _valori[ChiaveColore] = value.ToString();
            OnPropertyChanged(nameof(Colore));
        }
    }

    /// <summary>Valore di un campo Tipo=ListaTesto: righe di testo libero, ciascuna con la propria
    /// evidenziazione (feedback utente 2026-08-24, punto 8 — es. un trace ValidationTool lungo, dove alcune righe
    /// vanno "messe in chiaro"). Serializzato come JSON dentro Valore: resta una singola stringa a livello di
    /// storage, nessun cambio allo schema del test. Un Valore non JSON valido (es. campo appena passato da un
    /// altro Tipo) torna una lista vuota invece di far esplodere il form.</summary>
    public List<RigaEvidenziata> RigheListaTesto
    {
        get
        {
            if (string.IsNullOrEmpty(Valore)) return new();
            try { return JsonSerializer.Deserialize<List<RigaEvidenziata>>(Valore) ?? new(); }
            catch (JsonException) { return new(); }
        }
        set
        {
            Valore = value.Count > 0 ? JsonSerializer.Serialize(value) : null;
            OnPropertyChanged(nameof(RigheListaTesto));
            OnPropertyChanged(nameof(RigheListaTestoConteggio));
        }
    }

    public int RigheListaTestoConteggio => RigheListaTesto.Count;
}

/// <summary>Una riga di un campo Tipo=ListaTesto, con la propria evidenziazione opzionale. INotifyPropertyChanged
/// non serializzato (JsonSerializer ignora eventi) — la stessa classe serve sia per lo storage (dentro
/// CampoStrumentoValore.RigheListaTesto) sia per il binding live nell'editor dedicato (ListaTestoEditorWindow),
/// senza bisogno di una seconda classe wrapper solo per la UI.</summary>
public class RigaEvidenziata : INotifyPropertyChanged, IEvidenziabile
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _testo = "";
    public string Testo { get => _testo; set { _testo = value; OnPropertyChanged(nameof(Testo)); } }

    private ColoreEvidenziazione _colore = ColoreEvidenziazione.Nessuno;
    // Salvato come nome, non come numero: l'ordine dell'enum non deve cambiare i colori già salvati. I valori numerici dei dati precedenti restano leggibili.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ColoreEvidenziazione Colore { get => _colore; set { _colore = value; OnPropertyChanged(nameof(Colore)); } }
}

/// <summary>Campi con lo stesso DefinizioneCampoStrumento.Gruppo, renderizzati insieme dentro un unico
/// pannello a tendina collassato con questo titolo (Addendum redesign 2026-08, revisione feedback QA).</summary>
public class GruppoCampiStrumento
{
    public string Nome { get; set; } = "";
    public List<CampoStrumentoValore> Campi { get; set; } = new();
}

/// <summary>File allegato a un test case (immagini o zip), salvato in una cartella compagna su disco —
/// solo i metadati vivono nel JSON del test case (Addendum §17.0).</summary>
public class Allegato
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string NomeFile { get; set; } = "";
    public DateTime DataCaricamento { get; set; }
    public string CaricatoDa { get; set; } = "";
    public long DimensioneByte { get; set; }
    public string Sha256 { get; set; } = "";
    public string Descrizione { get; set; } = "";
    public bool Archiviata { get; set; }

    /// <summary>REV/operazione a cui l'allegato serve da evidenza (testo di sola visualizzazione, vuoto = evidenza del Test).
    /// Derivato dai collegamenti riservati delle operazioni (vedi EvidenzaOperazione), mai persistito.</summary>
    [JsonIgnore]
    public string EtichettaOperazione { get; set; } = "";
}

/// <summary>Riferimento a un ticket esterno (qui, in pratica, sempre una Claim) collegato al test — solo
/// promemoria testuale, mai integrazione col sistema esterno dove questo ticket vive davvero.</summary>
public class TicketRif
{
    public string Riferimento { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime CollegatoIl { get; set; }
    public string CollegatoDa { get; set; } = "";
    public string Note { get; set; } = "";
}

public class ChangelogEntry
{
    public DateTime Data { get; set; }
    public string Operatore { get; set; } = "";
    public string Campo { get; set; } = "";
    public string? ValorePrecedente { get; set; }
    public string? ValoreNuovo { get; set; }
}

/// <summary>Nota obbligatoria quando un Master modifica Area di validazione, Scadenza o Assegnatario nel
/// riquadro Pianificazione (Addendum §7): elenca i campi toccati in quel salvataggio più il motivo.</summary>
public class NotaPianificazione
{
    public DateTime Data { get; set; }
    public string Operatore { get; set; } = "";
    public string CampiModificati { get; set; } = "";
    public string Nota { get; set; } = "";
}

/// <summary>Un test creato tramite "Crea test collegato" (feedback utente 2026-08-20, punto 17: menu
/// interattivo runtime — quali campi tenere, quale/i tipo/i di test collegato creare, anche più di uno insieme
/// o lo stesso tipo del test di partenza — al posto della vecchia AzioneCollegataTemplate configurata dal
/// Master nel template, ora rimossa. TemplateDestinazioneNome è catturato al momento della creazione, non
/// risolto da un Uid: resta leggibile anche se il template di destinazione viene poi rinominato o eliminato.</summary>
public class TestCollegatoCreato
{
    public string TemplateDestinazioneNome { get; set; } = "";
    public string TestCollegatoId { get; set; } = "";
    public DateTime CreatoIl { get; set; } = DateTime.Now;
}

/// <summary>
/// PUNTO UNICO di migrazione in lettura per Esito (decisione utente 6.4.0, punto 1). Ogni percorso che
/// materializza un TestCase da JSON esistente su disco — TestCaseRepository (editor/liste/statistiche in
/// memoria) e LocalTestIndexStore (proiezione SQLite locale) — deve richiamare uno dei due metodi qui sotto
/// SOLO sui propri percorsi di lettura fresca da disco, MAI sull'oggetto vivo passato a un Save (quello porta
/// le modifiche non ancora salvate dell'utente, non va mai "corretto" a posteriori). Usare la stessa funzione
/// condivisa da entrambi i consumer evita che editor e statistiche interpretino lo stesso file legacy in modo
/// diverso — è esattamente il disaccordo segnalato in review: applicare la normalizzazione separatamente nei
/// due posti, con logiche leggermente diverse, l'avrebbe reintrodotto silenziosamente.
///
/// Non riscrive MAI il file su disco: opera solo sull'oggetto TestCase già deserializzato in memoria.
/// </summary>
/// <summary>
/// Riconoscimento puro (nessun effetto collaterale, nessuna scrittura) dei Test importati dagli Status UnitLogger
/// (decisione utente 6.4.0, "Test importati: solo stato finale, non contati come test eseguiti"). L'app NON
/// usa questa funzione per dedurre l'origine a runtime — TestCase.Origine è l'unico campo che conta durante il
/// normale funzionamento di TestLoom. Questa funzione esiste SOLO perché l'harness di correzione della cartella
/// dati (uno script/tool esterno via API, non parte del runtime di TestLoom) ha bisogno di un unico punto di
/// verità, condiviso col resto del codebase, per decidere QUALI Test valorizzare a OrigineTest.ImportStatoUdt
/// durante quella correzione una tantum — evitando che l'harness reimplementi la propria logica di
/// riconoscimento, divergente da questa.
/// </summary>
public static class TestCaseOrigine
{
    /// <summary>Marcatore di Changelog usato dall'harness di correzione per etichettare i Test con stato
    /// importato dagli Status UnitLogger (piano 6.4.0, 2026-09-22).</summary>
    public const string MarkerChangelogImportStatoUdt = "IMPORT-STATUS-UnitLogger-2026-09-22";

    /// <summary>Vero se il TestCase porta nel proprio Changelog il marker dell'importazione Status UnitLogger, in uno
    /// qualunque dei campi testuali di una voce (Campo/ValorePrecedente/ValoreNuovo) — l'harness non ha bisogno
    /// di normalizzare la voce in un unico formato, questo riconoscimento è deliberatamente permissivo.</summary>
    public static bool EImportStatoUdt(TestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        return testCase.Changelog.Any(entry =>
            Contiene(entry.Campo) || Contiene(entry.ValorePrecedente) || Contiene(entry.ValoreNuovo));
    }

    private static bool Contiene(string? testo) =>
        !string.IsNullOrEmpty(testo) && testo.Contains(MarkerChangelogImportStatoUdt, StringComparison.Ordinal);
}

public static class TestCaseLegacyMigrations
{
    /// <summary>Variante con il percorso del file: rilegge il testo grezzo da disco SOLO se serve (Schema
    /// Version legacy), per il gap teorico "Esito assente dal JSON" — vedi MigraInLetturaDaTesto. Un file
    /// scritto da un client 6.4.0+ non paga mai questa lettura extra (DefaultIgnoreCondition.Never in
    /// JsonIo.Options garantisce che quei file abbiano sempre "Esito" esplicito su ogni Condizione).</summary>
    public static void MigraInLettura(TestCase tc, string? percorsoOriginale)
    {
        if (tc.SchemaVersion < TestCase.SchemaMigrazioneOperazioniEsplicite && !string.IsNullOrEmpty(percorsoOriginale))
        {
            string? testoGrezzo = null;
            try { testoGrezzo = File.ReadAllText(percorsoOriginale); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Lettura extra solo difensiva: se fallisce (file in transito/bloccato) si salta semplicemente
                // il controllo "Esito assente", non è un errore che deve propagarsi al chiamante.
            }
            if (testoGrezzo != null) RipristinaEsitoLegacyAssente(tc, testoGrezzo);
        }
        tc.MigraOperazioniPianificateNonEseguite();
    }

    /// <summary>Variante quando il testo grezzo è già disponibile (es. LocalTestIndexStore, che tiene il JSON
    /// in test_case_cache): evita una seconda lettura da disco.</summary>
    public static void MigraInLetturaDaTesto(TestCase tc, string? testoGrezzo)
    {
        if (tc.SchemaVersion < TestCase.SchemaMigrazioneOperazioniEsplicite && testoGrezzo != null)
            RipristinaEsitoLegacyAssente(tc, testoGrezzo);
        tc.MigraOperazioniPianificateNonEseguite();
    }

    /// <summary>
    /// Gap teorico (review 6.4.0): con l'enum riordinato, un JSON che non scrive affatto la proprietà "Esito"
    /// di una Condizione verrebbe letto come l'attuale default CLR (DaEseguire) invece del vecchio default
    /// implicito (Conforme, valore 0 nell'ordinamento enum pre-6.4.0). In pratica non può capitare per un file
    /// scritto da TestLoom stesso (JsonIo.Options ha DefaultIgnoreCondition.Never: ogni proprietà, compreso Esito
    /// al suo valore di default, viene sempre scritta esplicitamente, a qualunque SchemaVersion) — ma un file
    /// esterno/modificato a mano o più vecchio della property stessa potrebbe averla omessa. Qui ripristiniamo
    /// esplicitamente il vecchio default SOLO per le Condizioni la cui proprietà "Esito" è davvero assente dal
    /// JSON grezzo, non per quelle scritte con un valore (incluso "Conforme" scritto esplicitamente). Va
    /// chiamata PRIMA di MigraOperazioniPianificateNonEseguite, che può poi correggere ulteriormente un
    /// Conforme così ripristinato in DaEseguire su un Test ancora Pianificato senza altra evidenza reale.
    /// </summary>
    private static void RipristinaEsitoLegacyAssente(TestCase tc, string testoGrezzo)
    {
        JsonDocument documento;
        try { documento = JsonDocument.Parse(testoGrezzo); }
        catch (JsonException) { return; }

        using (documento)
        {
            if (!documento.RootElement.TryGetProperty(nameof(TestCase.Esecuzioni), out var esecuzioniJson) ||
                esecuzioniJson.ValueKind != JsonValueKind.Array)
                return;

            var indiceEsecuzione = 0;
            foreach (var esecuzioneJson in esecuzioniJson.EnumerateArray())
            {
                if (indiceEsecuzione >= tc.Esecuzioni.Count) break;
                if (esecuzioneJson.TryGetProperty(nameof(Esecuzione.Condizioni), out var condizioniJson) &&
                    condizioniJson.ValueKind == JsonValueKind.Array)
                {
                    var indiceCondizione = 0;
                    var condizioni = tc.Esecuzioni[indiceEsecuzione].Condizioni;
                    foreach (var condizioneJson in condizioniJson.EnumerateArray())
                    {
                        if (indiceCondizione >= condizioni.Count) break;
                        if (condizioneJson.ValueKind == JsonValueKind.Object &&
                            !condizioneJson.TryGetProperty(nameof(Condizione.Esito), out _))
                            condizioni[indiceCondizione].Esito = Esito.Conforme;
                        indiceCondizione++;
                    }
                }
                indiceEsecuzione++;
            }
        }
    }
}
