using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using TestLoom.App.Services;

namespace TestLoom.App.Models;

public abstract class MasterEntity
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime UltimaModifica { get; set; }
    public string ModificatoDa { get; set; } = "";

    /// <summary>Operatore che ha creato la riga, mai più sovrascritto dopo (a differenza di ModificatoDa, che
    /// cambia ad ogni salvataggio) — feedback utente 2026-09-18: "gli standard... dovrebbero poter creare ed
    /// eliminare esclusivamente le cose create da loro stessi", non voci create da un Master o da un altro
    /// Standard. Vuoto per le righe esistenti prima di questa modifica (nessuna attribuzione retroattiva
    /// possibile): MasterDataEditorViewModelBase.PuoEliminareRiga tratta un CreatoDa vuoto/diverso dal proprio
    /// come "non mio", mai come "eliminabile per default" — l'errore va sempre dalla parte cautelativa.</summary>
    public string CreatoDa { get; set; } = "";
    public List<ChangelogEntry> Changelog { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class Requisito : MasterEntity
{
    public string Id { get; set; } = "";
    public string Testo { get; set; } = "";
    public bool ApplicabileBanco { get; set; }
    public bool ApplicabileVeicolo { get; set; }
    public bool Archiviata { get; set; }

    /// <summary>Associazioni informative Extra tra un veicolo e le sue componente, indipendenti dai guasti.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RequisitoVeicoloEcu>? VeicoliEcuD4D { get; set; }

    /// <summary>Separatori del formato testuale di interscambio Excel delle coppie Extra (colonna opzionale E):
    /// "Veicolo|componente; Veicolo|componente" (le coppie anche su righe separate nella cella).</summary>
    public const char SeparatoreCoppiaD4D = '|';

    /// <summary>Vista testuale di <see cref="VeicoliEcuD4D"/> per import/export Excel (colonna opzionale E "Veicolo-componente Extra").
    /// Round-trip: le coppie vengono scritte "Veicolo|componente" separate da "; ". In lettura i segmenti malformati non vengono persi
    /// in silenzio ma elencati in <see cref="VeicoliEcuD4DNonValide"/> (l'import li segnala come errore). Mai serializzata.</summary>
    [JsonIgnore]
    public string VeicoliEcuD4DTesto
    {
        get => string.Join("; ", (VeicoliEcuD4D ?? new()).Select(c => c.Veicolo + SeparatoreCoppiaD4D + c.Componente));
        set
        {
            var coppie = new List<RequisitoVeicoloEcu>();
            var nonValide = new List<string>();
            foreach (var segmento in (value ?? "").Split(new[] { ';', '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var parti = segmento.Split(SeparatoreCoppiaD4D, StringSplitOptions.TrimEntries);
                if (parti.Length != 2 || parti[0].Length == 0 || parti[1].Length == 0) { nonValide.Add(segmento); continue; }
                if (coppie.Any(c => string.Equals(c.Veicolo, parti[0], StringComparison.OrdinalIgnoreCase) && string.Equals(c.Componente, parti[1], StringComparison.OrdinalIgnoreCase))) continue;
                coppie.Add(new RequisitoVeicoloEcu { Veicolo = parti[0], Componente = parti[1] });
            }
            VeicoliEcuD4D = coppie.Count == 0 ? null : coppie;
            VeicoliEcuD4DNonValide = nonValide.Count == 0 ? null : nonValide;
        }
    }

    /// <summary>Segmenti della colonna Extra non interpretabili nell'ultima lettura testuale (solo memoria, mai salvato).</summary>
    [JsonIgnore]
    public List<string>? VeicoliEcuD4DNonValide { get; set; }

    /// <summary>guasto da verificare per questo requisito (6.5.0, punto 7: test Banco basati su requisito/componente/guasto):
    /// voci {Codice guasto, componente}. Additivo e retro-compatibile: null (default, anche per ogni Requisito esistente)
    /// non viene scritto nel JSON (WhenWritingNull), quindi i requisiti senza guasto restano byte-identici; un
    /// JSON senza il campo si legge come "nessun guasto". Usare <see cref="DtcVoci"/> per leggere.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DtcRequisito>? Guasto { get; set; }

    /// <summary>Numero di guasto nell'ARCHIVIO separato del requisito (catalogo Phase D "requisiti-guasto", un documento per
    /// requisito: vedi <see cref="TestLoom.App.Services.RequisitoDtcArchivio"/>). SOLO un suggerimento economico (evita di
    /// leggere gli archivi quando non servono, es. riepilogo Audit): l'archivio resta la fonte autorevole e il valore e
    /// ricostruibile. Additivo: null (nessun archivio) non viene scritto. Quando i guasti sono ancora embedded in
    /// <see cref="Guasto"/> (dati precedenti all'archivio) vale il campo embedded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DtcConteggio { get; set; }

    /// <summary>guasto embedded (formato precedente all'archivio) oppure vuoto: NON legge l'archivio. Per i guasti archiviati usare
    /// RequisitoDtcArchivio.</summary>
    [JsonIgnore]
    public IReadOnlyList<DtcRequisito> DtcVoci => (IReadOnlyList<DtcRequisito>?)Guasto ?? Array.Empty<DtcRequisito>();

    /// <summary>Numero di guasto noto senza leggere l'archivio: embedded se presente, altrimenti il conteggio dell'archivio.</summary>
    [JsonIgnore]
    public int DtcTotale => Guasto?.Count ?? (DtcConteggio is > 0 and var c ? c : DtcConteggioRisolto ?? 0);

    /// <summary>Conteggio ricavato dagli archivi quando <see cref="DtcConteggio"/> e null/0 (= ignoto per i Extra): overlay SOLO in
    /// memoria (RequisitoDtcArchivio.Risolvi), mai serializzato.</summary>
    [JsonIgnore]
    public int? DtcConteggioRisolto { get; set; }

    /// <summary>Impronta del catalogo degli archivi guasto al momento di <see cref="DtcConteggioRisolto"/> (solo memoria): se un altro PC
    /// cambia i guasti l'impronta cambia e il conteggio risolto viene ricalcolato alla Risolvi successiva.</summary>
    [JsonIgnore]
    public string? DtcConteggioRisoltoImpronta { get; set; }
}

/// <summary>Una coppia veicolo–componente associata esplicitamente a un requisito Extra.</summary>
public class RequisitoVeicoloEcu
{
    public string Veicolo { get; set; } = "";
    public string Componente { get; set; } = "";
}

/// <summary>Documento dell'archivio guasto di UN requisito (catalogo Phase D "requisiti-guasto"). L'Uid tecnico e quello del
/// requisito (stabile anche se l'Id del requisito viene rinominato); chiave business = Uid del requisito in forma "N".
/// Scritto con gli stessi meccanismi atomici/journal/merge per-Uid degli altri master: due PC che modificano i guasti di
/// requisiti diversi non confliggono, lo stesso requisito segue il normale conflitto per-Uid.</summary>
public class DtcArchivioRequisito : MasterEntity
{
    /// <summary>Uid del requisito in forma "N" (chiave business del documento).</summary>
    public string Id { get; set; } = "";
    /// <summary>Id leggibile del requisito al momento dell'ultima scrittura (solo diagnostica).</summary>
    public string RequisitoId { get; set; } = "";
    public List<DtcRequisito> Voci { get; set; } = new();
}

/// <summary>Un guasto di un Requisito (6.5.0, punto 7): codice e componente su cui va verificato. Ogni voce ha due passi
/// (validazione e devalidazione, vedi DtcAvanzamentoService). Dalla 6.5.1 (schema dati 9) la voce puo indicare anche il
/// Veicolo: lo storage resta una lista PIATTA, l'albero Veicolo -> componente -> guasto e solo una vista raggruppata.
/// Chiave di unicita = (Veicolo, componente, Codice).</summary>
public class DtcRequisito
{
    /// <summary>Etichetta di raggruppamento per le voci senza veicolo (valide: dati precedenti alla 6.5.1).</summary>
    public const string SenzaVeicolo = "(senza veicolo)";

    public string Codice { get; set; } = "";
    public string Componente { get; set; } = "";

    /// <summary>Veicolo (Veicolo.Nome) cui appartiene il guasto. Additivo: null (default, ogni voce precedente) non viene scritto
    /// nel JSON, quindi le voci senza veicolo restano byte-identiche; un file 6.5.0 senza il campo si legge come null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Veicolo { get; set; }

    /// <summary>Campi sconosciuti di versioni piu recenti: preservati nella riscrittura invece di perderli in silenzio.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Veicolo normalizzato (trim); stringa vuota = nessun veicolo.</summary>
    [JsonIgnore]
    public string VeicoloNorm => (Veicolo ?? "").Trim();

    /// <summary>Nome da mostrare nella vista ad albero: il veicolo oppure <see cref="SenzaVeicolo"/>.</summary>
    [JsonIgnore]
    public string VeicoloVista => VeicoloNorm.Length == 0 ? SenzaVeicolo : VeicoloNorm;

    /// <summary>Chiave di unicita (Veicolo, componente, Codice): da confrontare con StringComparer.OrdinalIgnoreCase.</summary>
    public static string Chiave(string? veicolo, string? componente, string? codice) =>
        (veicolo ?? "").Trim() + "|" + (componente ?? "").Trim() + "|" + (codice ?? "").Trim();

    public static string Chiave(DtcRequisito v) => Chiave(v.Veicolo, v.Componente, v.Codice);

    /// <summary>Copia profonda (preserva Veicolo ed ExtensionData): usare SEMPRE questa invece di ricostruire la voce campo per campo.</summary>
    public DtcRequisito Clona() => new()
    {
        Codice = Codice ?? "", Componente = Componente ?? "", Veicolo = Veicolo,
        ExtensionData = ExtensionData == null ? null : new Dictionary<string, JsonElement>(ExtensionData)
    };

    /// <summary>Etichetta mostrata nei selettori (stessa costruzione per la voce e per l'operazione che la
    /// referenzia, cosi il selettore ritrova la voce scelta): "Guasto · componente · Requisito", oppure
    /// "Guasto · componente · Veicolo · Requisito" quando la voce ha un veicolo.</summary>
    public static string Etichetta(string? requisitoId, string? codice, string? componente, string? veicolo = null) =>
        string.IsNullOrWhiteSpace(veicolo)
            ? $"{codice} · {componente} · {requisitoId}"
            : $"{codice} · {componente} · {veicolo.Trim()} · {requisitoId}";
}

/// <summary>Voce proposta dal selettore guasto di un'operazione di un Test (Requisito + guasto + componente [+ Veicolo]).</summary>
public sealed record DtcOpzione(string RequisitoId, string Codice, string Componente, string? Veicolo = null)
{
    public string Etichetta => DtcRequisito.Etichetta(RequisitoId, Codice, Componente, Veicolo);
    public override string ToString() => Etichetta;
}

/// <summary>Risolve un conflitto nella matrice Audit (Addendum: grill-me 2026-08-18, follow-up — più Test
/// diversi possono coprire la stessa coppia componente×Requisito con punteggi discordanti; "un Master può marcare
/// quale Test è quello valido"): punta al Test da considerare autorevole per quella coppia, non congela il
/// punteggio — se quel Test viene poi ritestato, la cella segue comunque il suo punteggio più recente. Se
/// TestCaseIdValido non è più tra i candidati in conflitto (test cancellato/modificato), la risoluzione viene
/// ignorata da AuditMatriceService finché non se ne crea una nuova.</summary>
public class RisoluzioneConflittoAudit : MasterEntity
{
    public string Componente { get; set; } = "";
    public string RequisitoId { get; set; } = "";

    /// <summary>Popolato quando la risoluzione sceglie uno dei Test candidati in conflitto come autorevole
    /// (comportamento originale, Addendum: grill-me 2026-08-18). Vuoto per un inserimento manuale — vedi
    /// PunteggioManuale, che è alternativo a questo, mai entrambi valorizzati sulla stessa riga.</summary>
    public string TestCaseIdValido { get; set; } = "";

    /// <summary>Valore manuale legacy componente×Requisito inserito da un Master senza dimensione veicolo. È una
    /// candidata globale finché resta attiva, ma la matrice mostra la fonte con timestamp più recente tra questo
    /// valore, i Test diretti e le valutazioni locali/propagate del veicolo. Un nuovo inserimento Audit usa il
    /// catalogo veicolo-specifico invece di creare nuovi record legacy.</summary>
    public PunteggioConformita? PunteggioManuale { get; set; }

    /// <summary>Identità del punteggio legacy attualmente salvato. Nullable per mantenere deterministico il
    /// caricamento dei record precedenti all'introduzione dello storico.</summary>
    public Guid? EventoPunteggioManualeCorrenteId { get; set; }

    /// <summary>Snapshot append-only dei punteggi manuali legacy sostituiti o rimossi. La riga resta nel
    /// catalogo anche quando PunteggioManuale è null, così lo storico non elimina né altera TestCaseIdValido.</summary>
    public List<SnapshotValutazioneAudit> StoricoPunteggiManuali { get; set; } = new();

    public string Motivazione { get; set; } = "";
}

/// <summary>Origine di un evento mostrato nello storico della cella Audit. Gli eventi Test sono ricostruiti
/// dalle REV immutabili; Manuale, Propagata e Legacy sono snapshot persistiti quando il valore viene sostituito.</summary>
public enum OrigineStoricoValutazioneAudit
{
    Test,
    Manuale,
    Propagata,
    Legacy,
}

/// <summary>Snapshot tipizzato e immutabile di un punteggio Audit precedente. EventId rende idempotente la
/// fusione dello storico quando Phase D consolida righe collise su più postazioni.</summary>
public sealed class SnapshotValutazioneAudit
{
    public Guid EventId { get; set; }
    public OrigineStoricoValutazioneAudit Origine { get; set; }
    public PunteggioConformita Punteggio { get; set; }
    public DateTime Data { get; set; }
    public string Operatore { get; set; } = "";
    public string Nota { get; set; } = "";
    public string? BaselineSw { get; set; }
    public string VeicoloSorgente { get; set; } = "";
    public string TestCaseId { get; set; } = "";
    public int? IterazioneTest { get; set; }
}

/// <summary>Origine di una valutazione non diretta salvata per una cella (requisito, componente, veicolo) della
/// matrice Audit (Piano 6.3.1 §1). Manuale: inserita esplicitamente da un operatore (per singola coppia
/// componente×veicolo o applicata a un sottoinsieme di veicoli propagabili di un componente). Propagata: l'operatore ha
/// confermato di voler propagare a questo veicolo il valore visto su un altro veicolo per la stessa componente×
/// requisito ("!" nella matrice) — la sorgente resta tracciata sui campi Sorgente* qui sotto.</summary>
public enum OrigineValutazioneRequisito
{
    Manuale,
    Propagata,
}

/// <summary>Valutazione persistita di una cella (Requisito × componente × Veicolo) della matrice Audit quando NON
/// deriva da un Test diretto su quel veicolo (Piano 6.3.1 §1, decisioni utente 2026-09-23). Il vecchio
/// comportamento riempiva automaticamente ogni veicolo che condivide la stessa componente con il punteggio visto su
/// un Test di un altro veicolo ("propagazione silenziosa"); da qui in poi quella cella mostra invece lo stato
/// "!" (PropagabileNonConfermato, vedi AuditMatriceService/ValutazioneRequisitoEcuVeicoloService) finché un
/// operatore non decide esplicitamente se propagare. Una volta propagata (o inserita a mano), il valore
/// diventa una riga di questo catalogo e compete per timestamp con Test diretti e valori Manuali legacy o
/// Propagati (se una fonte successiva arriva dopo, la cella torna a seguirla — vedi
/// ValutazioneRequisitoEcuVeicoloService.Risolvi).
///
/// Registrata in MasterDataStore/PhaseDMasterCatalog seguendo esattamente lo stesso pattern di
/// RisoluzioneConflittoAudit (proprio file/catalogo, bootstrap Phase D, business key Requisito|Componente|Veicolo).
/// A differenza di RisoluzioneConflittoAudit (che vive per coppia Componente×Requisito, senza dimensione veicolo)
/// questa entità introduce esplicitamente il terzo asse: un Test con più veicoli copre automaticamente tutte
/// le coppie Componente×Veicolo che include (propagazione "interna al test", invariata), ma NON copre più altri
/// veicoli della stessa componente che quel Test non ha effettivamente coperto.</summary>
public class ValutazioneRequisitoEcuVeicolo : MasterEntity
{
    public Guid Uid { get; set; } = Guid.NewGuid();

    public string RequisitoId { get; set; } = "";
    public string Componente { get; set; } = "";
    public string Veicolo { get; set; } = "";

    public PunteggioConformita Punteggio { get; set; }

    /// <summary>Evento del valore corrente, nullo per i record precedenti allo storico strutturato.</summary>
    public Guid? EventoCorrenteId { get; set; }

    /// <summary>Il valore corrente è stato rimosso. La riga resta come aggregato storico per la business key;
    /// i consumer Audit la escludono dai punteggi correnti e gli inserimenti successivi riattivano lo stesso UID.</summary>
    public bool IsArchiviata { get; set; }

    /// <summary>Snapshot append-only dei punteggi Manuali/Propagati sostituiti o rimossi.</summary>
    public List<SnapshotValutazioneAudit> Storico { get; set; } = new();

    public OrigineValutazioneRequisito Origine { get; set; } = OrigineValutazioneRequisito.Manuale;

    /// <summary>Popolati solo quando Origine=Propagata: veicolo, Test e punteggio da cui il valore è stato
    /// derivato al momento della conferma dell'operatore (il valore propagato è "congelato" alla conferma,
    /// non insegue automaticamente eventuali ri-esecuzioni successive del Test sorgente — coerente con la
    /// scelta utente di rendere la propagazione un atto esplicito e tracciato, non un calcolo che continua a
    /// muoversi da solo). Vuoti per Origine=Manuale.</summary>
    public string SorgenteVeicolo { get; set; } = "";
    public string SorgenteTestId { get; set; } = "";
    public PunteggioConformita? SorgentePunteggio { get; set; }

    /// <summary>Versione SW opzionale per questa coppia (requisito utente: "solo punteggi requisito + Baseline
    /// SW opzionale per coppia"), puramente informativa: non entra nella risoluzione della cella.</summary>
    public string? BaselineSw { get; set; }

    /// <summary>Obbligatoria per un inserimento Manuale (validata dal servizio/editor, non dal modello);
    /// per una Propagata riporta la nota indicata dall'operatore al momento della conferma. Usata per
    /// costruire il testo del tooltip insieme ai campi Sorgente* — vedi
    /// ValutazioneRequisitoEcuVeicoloService.TestoTooltip.</summary>
    public string Nota { get; set; } = "";

    public string Operatore { get; set; } = "";
}

/// <summary>Correzione manuale dello stato di una combinazione funzionalità × componente × veicolo nel Report (6.6.1, fase 2).
/// Inserita da un Master, oppure propagata dall'esito di un altro veicolo della stessa componente. La sua DataDecisione entra nella
/// riduzione "ultimo esito" della combinazione: vale finché non esiste un esito di Test più recente. Catalogo ADDITIVO con file
/// proprio: una versione precedente lo ignora senza perdere né sovrascrivere nulla.</summary>
public class StatoFunzionalitaEcuVeicolo : MasterEntity
{
    public Guid Uid { get; set; } = Guid.NewGuid();

    public string Funzionalita { get; set; } = "";
    public string Componente { get; set; } = "";
    public string Veicolo { get; set; } = "";

    /// <summary>Esito scelto dal Master: Conforme, NonConforme oppure ConformeConDeviazioni.</summary>
    public Esito Esito { get; set; } = Esito.Conforme;

    public OrigineStatoFunzionalita Origine { get; set; } = OrigineStatoFunzionalita.Manuale;

    /// <summary>Veicolo e Test da cui e' stato propagato l'esito (solo per Origine = Propagata).</summary>
    public string SorgenteVeicolo { get; set; } = "";
    public string SorgenteTestId { get; set; } = "";
    public Esito? SorgenteEsito { get; set; }

    /// <summary>Motivazione obbligatoria: la scrive chi decide (manuale o propagazione).</summary>
    public string Nota { get; set; } = "";
    public string Operatore { get; set; } = "";

    /// <summary>Momento della decisione (UTC): decide la precedenza rispetto agli esiti dei Test.</summary>
    public DateTime DataDecisione { get; set; }
}

public enum OrigineStatoFunzionalita
{
    Manuale = 0,
    Propagata = 1,
}

/// <summary>Restrizione di una Funzionalità ai veicoli di una sua componente (dato reale: "la funzionalità F sul componente E vale solo per
/// i veicoli A e B", non per tutti i veicoli di E). Senza voce per il componente la Funzionalità vale su tutti i veicoli del componente
/// (comportamento precedente). Con voce vale solo sui veicoli elencati, intersecati con Componente.VeicoliAssociati; una voce con
/// elenco vuoto non vale su nessun veicolo di quell'componente. Additivo e retrocompatibile: senza il campo si legge come vuoto.</summary>
public class VeicoliFunzionalitaEcu
{
    public string Componente { get; set; } = "";

    private List<string> _veicoli = new();
    /// <summary>Un JSON con "Veicoli": null si legge come lista vuota, mai come riferimento nullo.</summary>
    public List<string> Veicoli
    {
        get => _veicoli;
        set => _veicoli = value ?? new();
    }
}

/// <summary>Unifica quella che prima erano due tabelle separate (Funzionalità "diagnostica" con verifiche
/// riutilizzabili, e UnitLogger con solo Nome+Categoria): stessa funzionalità reale (es. "Letture guasti") si
/// applica sia a diagnosi engineering sia a UnitLogger, tenerle divise creava solo duplicati e confusione — Categoria
/// distingue il contesto d'uso senza duplicare la voce. La libreria di verifiche riutilizzabili è stata
/// rimossa (ridondante rispetto ai suggerimenti automatici dalla cronologia nel form del test).</summary>
public class Funzionalita : MasterEntity
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = "";

    /// <summary>Una funzionalità può appartenere a più categorie (feedback utente 2026-08-24): confermato
    /// dai menu UnitLogger reali, dove lo stesso Param (es. "BSM - Brake System Module") ricorre sotto più Page/
    /// Function diverse (Maintenance, Configure→componente Programming, Configure→Update...) — costringerla a una
    /// sola categoria in TestLoom avrebbe richiesto duplicare la voce una volta per categoria, come già capitato
    /// per Diagnostica/UnitLogger prima di questa stessa unificazione. StringOrArrayJsonConverter legge sia il vecchio
    /// formato ("Categoria": "X", singola stringa) sia il nuovo (array) senza perdere dati sui file esistenti;
    /// il nome JSON resta "Categoria" per compatibilità, sempre scritto come array da qui in poi.</summary>
    [JsonPropertyName("Categoria")]
    [JsonConverter(typeof(StringOrArrayJsonConverter))]
    public List<string> Categorie { get; set; } = new();

    /// <summary>Vista testuale (comma-separated), stesso schema di Componente.VeicoliAssociatiTesto — usata sia
    /// dall'import Excel posizionale (una colonna, valori multipli separati da virgola) sia dal riepilogo
    /// mostrato nel picker multiplo del form.</summary>
    [JsonIgnore]
    public string CategorieTesto
    {
        get => string.Join(", ", Categorie);
        set => Categorie = value
            .Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> ApplicabilePerVeicolo { get; set; } = new();

    /// <summary>Interruttore esplicito e mutuamente esclusivo con EcuAssociate (feedback utente 2026-09-23,
    /// precisazione vincolante: "per ogni funzionalità l'utente deve poter associare i componenti che hanno quella
    /// funzionalità OPPURE scegliere esplicitamente 'Tutti i componenti'"). Default true (non solo sul modello C#:
    /// anche su ogni riga storica scritta prima di questo campo, che lo deserializza mancante e quindi True col
    /// default dell'initializer — System.Text.Json non tocca un campo assente nel JSON) cosi' nessuna
    /// funzionalità sparisce da alcun editor dopo l'aggiornamento: senza una scelta esplicita resta valida per
    /// tutti i componenti esattamente come il comportamento precedente (EcuAssociate vuoto = generica). Quando true,
    /// EcuAssociate deve restare vuoto (invariante mantenuta da EcuAssociateTesto/dagli editor, non imposta a
    /// livello di modello per non rompere un caso limite di deserializzazione con entrambi i campi presenti su
    /// un JSON scritto a mano).</summary>
    public bool TutteLeEcu { get; set; } = true;

    /// <summary>componente a cui questa funzionalità è associata quando TutteLeEcu è false (feedback utente
    /// 2026-09-23: "le funzionalità... devono poter essere assegnate a una o più componente, in modo da non dover
    /// scorrere troppo all'interno del test"). Ignorato quando TutteLeEcu è true. Indipendente da Categorie: le
    /// categorie restano un raggruppamento a parte, questo campo filtra invece l'elenco funzionalità negli
    /// editor che scelgono prima un componente (Test, NC/CR quando derivano da un Test, Segnalazioni rete). Nome
    /// scelto in coordinamento con l'inventario riferimenti usato da Unisci/Rinomina/Dividi componente: qualunque
    /// servizio di merge/rename/split sul catalogo componente deve aggiornare anche questa lista sulle Funzionalità
    /// esistenti (stesso principio già applicato a Componente.VeicoliAssociati quando un Veicolo viene rinominato/
    /// unito), altrimenti un'associazione punterebbe a un nome componente non più esistente nel master.</summary>
    public List<string> EcuAssociate { get; set; } = new();

    /// <summary>Token speciale usato in EcuAssociateTesto (colonna Excel import/export) per rappresentare
    /// TutteLeEcu=true esplicito, invece di un elenco di nomi componente. Scelto compatto e indipendente dalla lingua
    /// (IT/EN) per non dover mantenere due grafie diverse nello stesso file. Una cella vuota nell'import produce
    /// lo stesso risultato (il default dell'initializer, TutteLeEcu=true) perché MasterDataEditorViewModelBase.
    /// ImportaDaExcel salta le celle vuote senza toccare la proprietà.</summary>
    public const string TokenTutteLeEcu = "*";

    /// <summary>Vista testuale round-trip di TutteLeEcu/EcuAssociate per l'import/export Excel (colonna unica,
    /// stesso schema posizionale di CategorieTesto/Componente.VeicoliAssociatiTesto): "*" (o "tutte"/"all", tollerati
    /// in lettura) per TutteLeEcu, altrimenti l'elenco nomi separato da virgola. Per il riepilogo mostrato nel
    /// picker del form Master Funzionalità vedi invece EcuAssociateRiepilogo, pensato per la UI e non per il
    /// round-trip Excel.</summary>
    [JsonIgnore]
    public string EcuAssociateTesto
    {
        get => TutteLeEcu ? TokenTutteLeEcu : string.Join(", ", EcuAssociate);
        set
        {
            var testo = (value ?? "").Trim();
            if (testo.Length == 0 || testo == TokenTutteLeEcu || testo.Equals("tutte", StringComparison.OrdinalIgnoreCase) || testo.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                TutteLeEcu = true;
                EcuAssociate = new();
                return;
            }
            TutteLeEcu = false;
            EcuAssociate = testo
                .Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Riepilogo leggibile mostrato sul pulsante/tooltip del picker componente nella pagina Master Funzionalità
    /// (a differenza di EcuAssociateTesto, pensato per l'Excel e quindi con il token compatto "*"). Non
    /// JSON-serializzato: puramente derivato da TutteLeEcu/EcuAssociate.</summary>
    [JsonIgnore]
    public string EcuAssociateRiepilogo => TutteLeEcu
        ? "Tutti i componenti"
        : (EcuAssociate.Count == 0 ? "(nessuna componente)" : string.Join(", ", EcuAssociate));

    /// <summary>Restrizione per componente (vedi VeicoliFunzionalitaEcu): al più una voce per componente, normalizzata da
    /// NormalizzaVeicoliPerEcu al salvataggio. Additivo e retrocompatibile: un catalogo senza il campo, o con "null",
    /// si legge come nessuna restrizione. Le voci di componente non più associate alla funzionalità vengono scartate.</summary>
    private List<VeicoliFunzionalitaEcu> _veicoliPerEcu = new();
    public List<VeicoliFunzionalitaEcu> VeicoliPerEcu
    {
        get => _veicoliPerEcu;
        set => _veicoliPerEcu = value?.Where(v => v != null).ToList() ?? new();
    }

    /// <summary>Token di VeicoliPerEcuTesto per "nessuna restrizione" (come "*" per EcuAssociateTesto): svuota le voci.</summary>
    public const string TokenNessunaRestrizioneVeicoli = "*";

    /// <summary>Segmenti di VeicoliPerEcuTesto non riconosciuti all'ultima lettura del testo (formato errato). Non
    /// persistiti: servono solo all'import Excel per bloccare la riga invece di perdere il dato in silenzio.</summary>
    [JsonIgnore]
    public List<string> VeicoliPerEcuTestoNonValidi { get; } = new();

    /// <summary>Vista testuale round-trip di VeicoliPerEcu per l'import/export Excel (colonna unica, come EcuAssociateTesto):
    /// "Componente => V1, V2; componente2 => V3". Voci separate da ';', veicoli da ','. "Componente =>" significa nessun veicolo; testo vuoto o
    /// "*" nessuna restrizione. Ordinata per componente e veicolo, così il testo è stabile. Si usa "=>" perché ':' e '|' non sono
    /// vietati nei nomi componente/Veicolo: nei nomi non vanno usati "=>", ';' e ','.</summary>
    [JsonIgnore]
    public string VeicoliPerEcuTesto
    {
        get => string.Join("; ", VeicoliPerEcu
            .Where(v => !string.IsNullOrWhiteSpace(v.Componente))
            .OrderBy(v => v.Componente.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(FormattaVoceVeicoliPerEcu));
        set
        {
            var testo = (value ?? "").Trim();
            VeicoliPerEcuTestoNonValidi.Clear();
            var voci = new List<VeicoliFunzionalitaEcu>();
            if (testo.Length == 0 || testo == TokenNessunaRestrizioneVeicoli)
            {
                VeicoliPerEcu = voci;
                return;
            }
            foreach (var segmento in testo.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var separatore = segmento.IndexOf("=>", StringComparison.Ordinal);
                var componente = separatore < 0 ? "" : segmento[..separatore].Trim();
                if (componente.Length == 0)
                {
                    VeicoliPerEcuTestoNonValidi.Add(segmento);
                    continue;
                }
                voci.Add(new VeicoliFunzionalitaEcu
                {
                    Componente = componente,
                    Veicoli = segmento[(separatore + 2)..]
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                });
            }
            VeicoliPerEcu = voci;
        }
    }

    private static string FormattaVoceVeicoliPerEcu(VeicoliFunzionalitaEcu voce)
    {
        var veicoli = voce.Veicoli
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return $"{voce.Componente.Trim()} => {string.Join(", ", veicoli)}".TrimEnd();
    }

    /// <summary>Vero se la Funzionalità vale sul componente: su tutti i componenti se TutteLeEcu, altrimenti se il componente è tra le
    /// EcuAssociate (senza distinzione di maiuscole). Regola unica per ogni filtro o conteggio che chiede se la
    /// funzionalità vale su un componente.</summary>
    public bool ValeSuEcu(string componente) =>
        TutteLeEcu || EcuAssociate.Any(e => string.Equals(e, componente, StringComparison.OrdinalIgnoreCase));

    /// <summary>Vero se la Funzionalità vale sul veicolo del componente. Con veicolo vuoto o nullo verifica solo il componente.
    /// Senza voce per il componente vale su tutti i suoi veicoli; con voce, solo su quelli elencati.</summary>
    public bool ValeSu(string componente, string? veicolo)
    {
        if (!ValeSuEcu(componente)) return false;
        if (string.IsNullOrEmpty(veicolo)) return true;
        var ristretti = VeicoliRistretti(componente);
        return ristretti == null || ristretti.Contains(veicolo, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Veicoli ammessi sul componente (unione delle voci di quell'componente), o null se la Funzionalità vale su tutti i
    /// veicoli del componente (nessuna voce). Un elenco vuoto significa nessun veicolo.</summary>
    public IReadOnlyList<string>? VeicoliRistretti(string componente)
    {
        List<string>? risultato = null;
        foreach (var voce in VeicoliPerEcu)
        {
            if (!string.Equals(voce.Componente, componente, StringComparison.OrdinalIgnoreCase)) continue;
            risultato ??= new List<string>();
            risultato.AddRange(voce.Veicoli.Where(v => !string.IsNullOrWhiteSpace(v)));
        }
        return risultato;
    }

    /// <summary>Stessa semantica di VeicoliRistretti per tutti i componenti in una volta (componente -> veicoli ammessi), per i cicli
    /// che scorrono molte combinazioni: calcolato una volta per funzionalità. Null se non ci sono restrizioni.</summary>
    public Dictionary<string, HashSet<string>>? IndiceVeicoliPerEcu()
    {
        if (VeicoliPerEcu.Count == 0) return null;
        var indice = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var voce in VeicoliPerEcu)
        {
            if (string.IsNullOrWhiteSpace(voce.Componente)) continue;
            if (!indice.TryGetValue(voce.Componente, out var veicoli)) indice[voce.Componente] = veicoli = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            veicoli.UnionWith(voce.Veicoli.Where(v => !string.IsNullOrWhiteSpace(v)));
        }
        return indice;
    }

    /// <summary>Invariante delle restrizioni, unico punto di riferimento: scarta le voci di componente non più applicabili (ne'
    /// TutteLeEcu ne' in EcuAssociate), fonde le voci duplicate della stessa componente (unione dei veicoli, senza distinguere le
    /// maiuscole) ed elimina veicoli vuoti e duplicati. Le voci di componente o veicoli archiviati restano: archiviare non cancella.
    /// Chiamata a ogni salvataggio della funzionalità e dopo Unisci/Rinomina/Dividi. Ritorna true se il contenuto cambia.</summary>
    public bool NormalizzaVeicoliPerEcu()
    {
        if (VeicoliPerEcu.Count == 0) return false;
        var prima = VeicoliPerEcuTesto;
        var voci = new List<VeicoliFunzionalitaEcu>();
        var perEcu = new Dictionary<string, VeicoliFunzionalitaEcu>(StringComparer.OrdinalIgnoreCase);
        foreach (var voce in VeicoliPerEcu)
        {
            var componente = (voce.Componente ?? "").Trim();
            if (componente.Length == 0 || !ValeSuEcu(componente)) continue;
            if (!perEcu.TryGetValue(componente, out var unita))
            {
                unita = new VeicoliFunzionalitaEcu { Componente = componente };
                perEcu[componente] = unita;
                voci.Add(unita);
            }
            foreach (var veicolo in voce.Veicoli)
            {
                var nome = (veicolo ?? "").Trim();
                if (nome.Length > 0 && !unita.Veicoli.Contains(nome, StringComparer.OrdinalIgnoreCase)) unita.Veicoli.Add(nome);
            }
        }
        VeicoliPerEcu = voci;
        return VeicoliPerEcuTesto != prima;
    }

    /// <summary>Sostituzione di un componente con uno o più componente sulle restrizioni, con la regola di combinazione del modello.
    /// Unisci: nuovi = [corretta]; Rinomina: nuovi = [nuovo]; Dividi: nuovi = [originale, ...nuove]. Va chiamata PRIMA di
    /// sostituire EcuAssociate, perché legge l'applicabilità precedente. Nessuna voce resta sul componente sostituito se non è in nuovi.
    /// <paramref name="ecuEsistenti"/> sono i componenti del catalogo prima dell'operazione: solo un componente già esistente (Unisci) unisce la
    /// propria restrizione; con "Tutti i componenti" ValeSuEcu è vero anche per un nome nuovo (Rinomina/Dividi), che quindi eredita la
    /// restrizione del solo nome vecchio.</summary>
    public void SostituisciEcuNelleRestrizioni(string vecchio, IReadOnlyList<string> nuovi, IReadOnlySet<string> ecuEsistenti)
    {
        if (VeicoliPerEcu.Count == 0) return;
        var fonteVecchio = (Applicabile: ValeSuEcu(vecchio), Veicoli: VeicoliRistretti(vecchio));
        var voci = new List<VeicoliFunzionalitaEcu>();
        foreach (var voce in VeicoliPerEcu)
        {
            // Le voci del componente sostituito e dei nuovi vengono ricalcolate sotto, non copiate.
            if (string.Equals(voce.Componente, vecchio, StringComparison.OrdinalIgnoreCase)
                || nuovi.Any(n => string.Equals(n, voce.Componente, StringComparison.OrdinalIgnoreCase))) continue;
            voci.Add(voce);
        }
        foreach (var nuovo in nuovi.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fonti = new List<(bool Applicabile, IReadOnlyList<string>? Veicoli)> { fonteVecchio };
            // Un nome nuovo con "Tutti i componenti" non ha una voce propria: senza questa guardia ValeSuEcu(nuovo) sarebbe vero e
            // la sua assenza di restrizione ("tutti i veicoli") cancellerebbe quella del nome vecchio.
            if (!string.Equals(nuovo, vecchio, StringComparison.OrdinalIgnoreCase) && (!TutteLeEcu || ecuEsistenti.Contains(nuovo)))
                fonti.Add((ValeSuEcu(nuovo), VeicoliRistretti(nuovo)));
            var (applicabile, veicoli) = CombinaVeicoli(fonti);
            if (applicabile && veicoli != null) voci.Add(new VeicoliFunzionalitaEcu { Componente = nuovo, Veicoli = veicoli });
        }
        VeicoliPerEcu = voci;
    }

    /// <summary>Unione delle restrizioni di un'altra Funzionalità in questa (Unisci Funzionalità, import di un duplicato):
    /// stessa regola di SostituisciEcuNelleRestrizioni. Va chiamata PRIMA di unire TutteLeEcu/EcuAssociate, perché legge
    /// l'applicabilità precedente di entrambe.</summary>
    public void UnisciVeicoliPerEcuDa(Funzionalita altra)
    {
        if (VeicoliPerEcu.Count == 0 && altra.VeicoliPerEcu.Count == 0) return;
        var componente = VeicoliPerEcu.Concat(altra.VeicoliPerEcu).Select(v => v.Componente)
            .Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var voci = new List<VeicoliFunzionalitaEcu>();
        foreach (var nome in componente)
        {
            var (applicabile, veicoli) = CombinaVeicoli(new[]
            {
                (ValeSuEcu(nome), VeicoliRistretti(nome)),
                (altra.ValeSuEcu(nome), altra.VeicoliRistretti(nome)),
            });
            if (applicabile && veicoli != null) voci.Add(new VeicoliFunzionalitaEcu { Componente = nome, Veicoli = veicoli });
        }
        VeicoliPerEcu = voci;
    }

    /// <summary>Regola unica di combinazione di più fonti sulla stessa componente: vale se vale in almeno una fonte. Se una fonte
    /// che vale lo fa su tutti i veicoli (nessuna voce), il risultato è tutti i veicoli (nessuna voce); altrimenti l'unione
    /// degli elenchi senza duplicati. Non ammette di restringere per effetto di un'unione.</summary>
    private static (bool Applicabile, List<string>? Veicoli) CombinaVeicoli(IEnumerable<(bool Applicabile, IReadOnlyList<string>? Veicoli)> fonti)
    {
        var applicabile = false;
        var unione = new List<string>();
        foreach (var (vale, veicoli) in fonti)
        {
            if (!vale) continue;
            applicabile = true;
            if (veicoli == null) return (true, null);
            foreach (var veicolo in veicoli)
                if (!unione.Contains(veicolo, StringComparer.OrdinalIgnoreCase)) unione.Add(veicolo);
        }
        return (applicabile, applicabile ? unione : null);
    }

    public bool Archiviata { get; set; }
}

/// <summary>Legge sia una stringa singola (formato Funzionalita.Categoria prima del 2026-08-24) sia un array
/// di stringhe, sempre come List&lt;string&gt; — nessun meccanismo di migrazione dello schema esiste altrove in
/// questo codebase (MasterDataRepository/JsonIo deserializzano senza convertitori dedicati), quindi senza
/// questo un vecchio funzionalita.json con "Categoria": "Diagnostica" farebbe fallire la deserializzazione
/// dell'intera lista e sparire silenziosamente tutte le Funzionalità al primo caricamento (l'errore di
/// JsonIo.TryRead viene scartato da MasterDataRepository.Load, che ripiega su una lista vuota). Scrive sempre
/// come array, il vecchio formato stringa non viene mai riprodotto.</summary>
public class StringOrArrayJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var valore = reader.GetString();
            return string.IsNullOrWhiteSpace(valore) ? new List<string>() : new List<string> { valore };
        }
        if (reader.TokenType == JsonTokenType.Null) return new List<string>();

        var lista = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } s) lista.Add(s);
        return lista;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}

/// <summary>Stato di una Pacchetto UnitLogger (feedback utente 2026-08-24, punto "WU Status"): il valore calcolato
/// dagli esiti dei test collegati resta sempre disponibile a parte (RevisioneUdtService.CalcolaStatoAutomatico,
/// mai persistito qui — con centinaia di Pacchetto tenerlo aggiornato in ogni riga sarebbe pesante da
/// mantenere sincronizzato). Nessuno = nessun override, vale il calcolo automatico.</summary>
public enum StatoManualeWorkingUnit { Nessuno, Ok, Problemi }

/// <summary>Catalogo delle coppie Pacchetto+Rev viste nei test (chiave = il valore esatto del campo
/// strumento "Pacchetto + rev.", es. "TRUCK_L24.READNEWVMU_BCM - Rev. 39869") — a differenza degli altri
/// master data, le righe compaiono da sole quando un test con quel valore viene salvato (vedi
/// RevisioneUdtService.Sincronizza), non da "+Nuovo". Solo lo stato manuale/nota sono dati veri da
/// conservare; lo stato automatico e lo storico dei test collegati si ricalcolano al volo quando servono,
/// mai duplicati qui (centinaia di Pacchetto renderebbero pesante un catalogo che li tenesse tutti pronti).</summary>
public class RevisioneUdt : MasterEntity
{
    // Additivo e retrocompatibile: sui dati 6.5.3 il valore non presente è false.
    // Archiviare NON cancella storico, test collegati, override, audit o note.
    public bool Archiviata { get; set; }
    public string WorkingUnitRev { get; set; } = "";
    public StatoManualeWorkingUnit StatoManuale { get; set; } = StatoManualeWorkingUnit.Nessuno;
    public string NotaManuale { get; set; } = "";

    /// <summary>Decisioni del Master per singolo veicolo (6.6.0, una WU puo' essere condivisa da piu' veicoli): al massimo
    /// una voce per veicolo. Additivo e retrocompatibile: un catalogo senza il campo si legge come lista vuota (nessuna
    /// decisione). Regola di validita' in RevisioneUdtService.DecisioneValida.</summary>
    private List<DecisioneVeicoloWorkingUnit> _decisioniVeicolo = new();
    /// <summary>Un JSON con "DecisioniVeicolo": null si legge come lista vuota, mai come riferimento nullo.</summary>
    public List<DecisioneVeicoloWorkingUnit> DecisioniVeicolo
    {
        get => _decisioniVeicolo;
        set => _decisioniVeicolo = value ?? new();
    }
}

/// <summary>Decisione del Master su una Pacchetto per un singolo veicolo (6.6.0). Registra il risultato automatico su
/// cui e' stata presa (StatoBase): la decisione vale finche' lo stato automatico di quel veicolo resta StatoBase. Se un
/// nuovo test cambia l'ultimo esito del veicolo, la decisione non vale piu' e la WU torna "Da decidere". TestBaseId e
/// DataBase sono solo tracciabilita' (quale test ha fornito il risultato su cui si e' deciso).</summary>
public class DecisioneVeicoloWorkingUnit
{
    public string Veicolo { get; set; } = "";
    public StatoManualeWorkingUnit Decisione { get; set; } = StatoManualeWorkingUnit.Nessuno;
    public string Nota { get; set; } = "";
    public StatoManualeWorkingUnit StatoBase { get; set; } = StatoManualeWorkingUnit.Nessuno;
    public string TestBaseId { get; set; } = "";
    public DateTime DataBase { get; set; }
    public string DecisoDa { get; set; } = "";
    public DateTime DecisoIl { get; set; }
}

public class Componente : MasterEntity
{
    public string Nome { get; set; } = "";
    public bool Archiviata { get; set; }

    /// <summary>Sottoveicoli su cui questa componente è montata (Addendum redesign 2026-08, revisione feedback QA):
    /// un componente può essere condivisa da più veicoli (es. una BCM comune a più modelli). Vuoto = non ancora
    /// categorizzata, non blocca la scelta del componente nel form test.</summary>
    public List<string> VeicoliAssociati { get; set; } = new();

    /// <summary>Vista testuale (comma-separated) di VeicoliAssociati, stesso schema di Operatore.GammeAssegnateTesto
    /// — con setter (feedback utente 2026-08-20, punto 8) per poterla importare da una singola cella Excel.</summary>
    [JsonIgnore]
    public string VeicoliAssociatiTesto
    {
        get => string.Join(", ", VeicoliAssociati);
        set => VeicoliAssociati = value
            .Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Gamma veicolare: classe x motorizzazione (es. "LCV Diesel", "MCV CNG", "HCV BEV").
/// Livello superiore della gerarchia veicolare (Addendum §17.3), modificabile solo da utenti Master.</summary>
public class Gamma : MasterEntity
{
    public string Nome { get; set; } = "";
    public bool Archiviata { get; set; }
}

/// <summary>Categoria di Funzionalità (es. "Motore", "Elettronica"): stesso ruolo di Gamma per i Veicoli,
/// gestita allo stesso modo — un elenco proprio invece di testo libero (Addendum §10, feedback QA).</summary>
public class Categoria : MasterEntity
{
    public string Nome { get; set; } = "";
    public bool Archiviata { get; set; }
}

/// <summary>Sottoveicolo: modello + model year (es. "Daily MY24"), appartiene a una Gamma.
/// Corrisponde a quello che nella prima versione dell'app era semplicemente "Veicolo".
/// GammaNome può essere vuoto per dati non ancora categorizzati (migrazione non bloccante, Addendum §17.3).</summary>
public class Veicolo : MasterEntity
{
    public string Nome { get; set; } = "";
    public string GammaNome { get; set; } = "";
    public bool Archiviata { get; set; }
}

/// <summary>Strumento diagnostico usabile nei test case. Diventa lista modificabile (come componente/Veicoli)
/// invece di un elenco fisso, cosi' da poter correggere voci sbagliate (es. CANdelaStudio non e' un tool
/// di invio comandi) e aggiungerne di nuove (Addendum §17.5).</summary>
public class StrumentoDiagnostico : MasterEntity, Services.IOrdinabile
{
    public string Nome { get; set; } = "";
    public bool Archiviata { get; set; }

    /// <summary>Campi tecnici collegati a questo strumento (Addendum redesign 2026-08, revisione feedback
    /// QA): definiti qui una volta, poi renderizzati automaticamente nel form del test — dove, dipende
    /// dall'Ambito di ciascun campo (vedi DefinizioneCampoStrumento.Ambito).</summary>
    public List<DefinizioneCampoStrumento> Campi { get; set; } = new();

    /// <summary>Ordine in cui questo strumento compare nel modulo Test (elenco "Strumenti usati" e sezioni dei
    /// suoi campi), nella pagina master Strumenti e in ogni elenco che li enumera (spec riordino campi
    /// 2026-09-23 §2, seguito). Additivo e retro-compatibile: 0 per ogni strumento esistente (default int),
    /// nessun cambio visibile finché un Master non riordina esplicitamente con ▲/▼ nella pagina master
    /// Strumenti — vedi StrumentiViewModel.OrdinaPerVisualizzazione, che applica l'ordine alfabetico storico
    /// come base stabile prima di leggere Ordine, così dati con Ordine tutto a zero restano nell'ordine già
    /// mostrato oggi (stesso principio di CampoOrdinamentoService).</summary>
    public int Ordine { get; set; }

    /// <summary>Tipo dello strumento (6.5.1, schema dati 9). Additivo: null (default, ogni strumento esistente) non viene scritto nel
    /// JSON; il valore effettivo si legge da <see cref="TipoEffettivo"/> (null = classificazione nota per nome, altrimenti Supporto).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TipoStrumento? Tipo { get; set; }

    /// <summary>Tipo effettivo: <see cref="Tipo"/> esplicito, altrimenti la classificazione nota per nome, altrimenti Supporto.</summary>
    [JsonIgnore]
    public TipoStrumento TipoEffettivo => Tipo ?? TipoPerNome(Nome) ?? TipoStrumento.Supporto;

    /// <summary>Valore mostrato/scelto dall'editor Strumenti (combo "Tipo"): legge <see cref="TipoEffettivo"/>, scrive il <see cref="Tipo"/>
    /// esplicito. Non persistita (il JSON porta solo <see cref="Tipo"/>).</summary>
    [JsonIgnore]
    public TipoStrumento TipoScelto
    {
        get => TipoEffettivo;
        set => Tipo = value;
    }

    /// <summary>Tipi proponibili nella combo dell'editor Strumenti, nell'ordine mostrato.</summary>
    public static IReadOnlyList<TipoStrumento> TipiDisponibili { get; } = new[] { TipoStrumento.DaTestare, TipoStrumento.Comportamento, TipoStrumento.Supporto };

    /// <summary>Classificazione nota per nome (case-insensitive): Easy/UnitLogger = DaTestare; guasto Injection = Comportamento;
    /// ValidationTool/CaptureTool = Supporto. Null per un nome non riconosciuto.</summary>
    public static TipoStrumento? TipoPerNome(string? nome)
    {
        var n = (nome ?? "").Trim();
        if (n.Equals("Easy", StringComparison.OrdinalIgnoreCase) || n.Equals("UnitLogger", StringComparison.OrdinalIgnoreCase)) return TipoStrumento.DaTestare;
        if (n.Equals("Guasto Injection", StringComparison.OrdinalIgnoreCase)) return TipoStrumento.Comportamento;
        if (n.Equals("ValidationTool", StringComparison.OrdinalIgnoreCase) || n.Equals("CaptureTool", StringComparison.OrdinalIgnoreCase)) return TipoStrumento.Supporto;
        return null;
    }
}

/// <summary>Ruolo di uno strumento diagnostico nei test guasto (6.5.1): Supporto = strumento di appoggio (nessuna operazione
/// generata); DaTestare = lo strumento e l'oggetto del test (operazioni attese per ogni guasto); Comportamento = verifica del
/// comportamento (es. guasto Injection).</summary>
public enum TipoStrumento
{
    Supporto,
    DaTestare,
    Comportamento,
}

/// <summary>Per quanto resta valido il valore di un campo prima di dover essere ricontrollato/reinserito
/// (Addendum §3, feedback QA: prima un solo flag per l'intero strumento, "costante per test" sì/no — troppo
/// grezzo per strumenti con campi di natura diversa, es. licenza UnitLogger costante ma Km percorsi no).</summary>
public enum TipoAmbitoCampo
{
    /// <summary>Varia liberamente da un'operazione all'altra dentro la stessa revisione (comportamento
    /// di default, es. comando/risposta di un servizio diagnostico).</summary>
    PerOperazione,
    /// <summary>Stabile tra le operazioni della stessa revisione, senza doverlo reinserire ogni volta, ma
    /// può cambiare da una revisione alla successiva (es. baseline di un sotto-componente).</summary>
    PerRevisione,
    /// <summary>Costante per l'intero test, invariato tra tutte le revisioni (es. licenza/versione UnitLogger).</summary>
    PerTest,
}

/// <summary>Definizione di un campo personalizzato collegato a uno Strumento diagnostico: nome, tipo di
/// editing e — solo per Tipo=Lista — le opzioni selezionabili.</summary>
public class DefinizioneCampoStrumento : Services.IOrdinabile
{
    public string Nome { get; set; } = "";

    /// <summary>Ordine di visualizzazione nel form del test (spec riordino campi 2026-09-23): l'ordine
    /// "vero" resta la posizione nella List&lt;DefinizioneCampoStrumento&gt; di StrumentoDiagnostico.Campi —
    /// questo campo la rispecchia solo per eventuali consumer futuri che leggano Ordine invece di iterare la
    /// lista (stesso principio di CampoTemplate.Ordine/SezioneTemplate.Ordine). Dati esistenti senza questo
    /// campo lo ricevono a 0 dalla deserializzazione JSON; CampoOrdinamentoService.Normalizza lo riallinea
    /// alla posizione nella lista al primo utilizzo dell'editor, senza cambiare l'ordine di visualizzazione
    /// (che è già, e resta, l'ordine della lista).</summary>
    public int Ordine { get; set; }
    public TipoCampoStrumento Tipo { get; set; } = TipoCampoStrumento.Testo;
    public List<string> OpzioniLista { get; set; } = new();
    public TipoAmbitoCampo Ambito { get; set; } = TipoAmbitoCampo.PerOperazione;

    /// <summary>Vuoto/null: il campo compare direttamente nel form. Valorizzato: il campo finisce, insieme
    /// agli altri con lo stesso nome di gruppo, dentro un pannello a tendina collassato con questo titolo
    /// (Addendum redesign 2026-08, revisione feedback QA: es. i campi guasto di ValidationTool, non il punto principale
    /// delle prove, raggruppati a parte invece che mischiati con quelli sempre rilevanti).</summary>
    public string? Gruppo { get; set; }

    /// <summary>Solo per Tipo=Numero: unità di misura o simbolo mostrato accanto al valore (es. "km", "°C",
    /// "%"), testo libero (Addendum: collaudo live 2026-08-17, feedback QA — prima un numero nudo senza unità,
    /// costringeva a infilarla nel nome del campo, es. "Km percorsi" invece di "Percorsi" + unità "km").</summary>
    public string? UnitaMisura { get; set; }

    /// <summary>Valore proposto quando il campo compare per la prima volta in un test (feedback utente
    /// 2026-08-24, punto 3: campi che restano quasi sempre uguali, es. Lingua/Licenza di uno strumento UnitLogger,
    /// dovrebbero arrivare già compilati invece di richiedere di reinserirli ogni volta) — resta comunque
    /// modificabile come qualunque altro valore. Generico su DefinizioneCampoStrumento, quindi vale per il
    /// campo di qualsiasi strumento, non solo UnitLogger. Vuoto/null: nessun default, comportamento invariato.</summary>
    public string? ValoreDefault { get; set; }
}

public class Operatore : MasterEntity, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Nome { get; set; } = "";
    public string Iniziali { get; set; } = "";
    public bool Disattivo { get; set; }

    // Privilegio minimo di default (feedback utente 2026-08-24): un Operatore appena creato da un Master
    // nella griglia (MasterDataEditorViewModelBase.Nuovo, new T()) parte come Viewer finché non viene
    // esplicitamente promosso, invece di ereditare scrittura di default.
    public RuoloOperatore Ruolo { get; set; } = RuoloOperatore.Viewer;

    /// <summary>Sigillo HMAC di Nome+Ruolo (piano 6.4.2, §4b, decisione utente 2026-09-28): protegge Ruolo da
    /// una modifica diretta di operatori.json fuori dall'app. Assente o non valido → <see cref="RuoloEffettivo"/>
    /// ricade su Viewer, mai su Ruolo. Scritto SOLO da RuoloSigilloService.Sigilla — vedi quella classe per il
    /// punto unico di calcolo. Vuoto per gli operatori creati prima di questa funzionalità (migrazione:
    /// nessuna eccezione "legacy accettato", diventano Viewer finché un Master o l'harness di correzione non
    /// li risigilla).</summary>
    public string? SigilloRuolo { get; set; }

    /// <summary>PUNTO UNICO da cui la UI/AppState devono leggere il ruolo: mai <see cref="Ruolo"/>
    /// direttamente per decidere permessi. Delega a RuoloSigilloService.RuoloEffettivo (piano 6.4.2, §4b:
    /// "dove si calcola il ruolo effettivo, usa un punto unico, non spargere controlli").</summary>
    [JsonIgnore]
    public RuoloOperatore RuoloEffettivo => RuoloSigilloService.RuoloEffettivo(this);

    /// <summary>Vero quando Ruolo è protetto da un sigillo valido: usato dall'editor Operatori per mostrare
    /// l'avviso ai Master (piano 6.4.2, §4b, punto 2: "sigillo assente o non valido... i Master vedono un
    /// avviso").</summary>
    [JsonIgnore]
    public bool SigilloRuoloValido => RuoloSigilloService.SigilloRuoloValido(this);

    private string? _pinHash;

    /// <summary>Hash del PIN, mantenuto solo per compatibilità con la verifica esistente (PinService.Verifica).
    /// Confermato esplicitamente come gate di comodo/igiene, non come vera barriera di sicurezza: chiunque ha
    /// accesso alla cartella condivisa può comunque leggere questo file (Addendum §17.2).</summary>
    public string? PinHash { get => _pinHash; set { _pinHash = value; OnPropertyChanged(nameof(PinHash)); } }

    private string? _pinCifrato;

    /// <summary>PIN cifrato su disco (PinService.Cifra, AES a chiave fissa incorporata nell'app — non un vero
    /// segreto, solo per evitare che il PIN sia leggibile a colpo d'occhio aprendo operatori.json a mano).
    /// Non usare direttamente: leggere/scrivere tramite PinChiaro qui sotto.</summary>
    public string? PinCifrato { get => _pinCifrato; set { _pinCifrato = value; OnPropertyChanged(nameof(PinCifrato)); OnPropertyChanged(nameof(PinChiaro)); } }

    /// <summary>PIN in chiaro, sempre visibile a un Master (feedback utente 2026-08-20, punto 2 — inverte
    /// deliberatamente la scelta del 16/08 di tenere solo l'hash: coerente col fatto che il PIN è già
    /// dichiarato "igiene minima", non una vera barriera). Decifra/cifra al volo da/verso PinCifrato — sul
    /// disco resta comunque solo il blob cifrato, mai il PIN in testo semplice.</summary>
    [JsonIgnore]
    public string? PinChiaro
    {
        get => PinService.Decifra(PinCifrato);
        set { PinCifrato = PinService.Cifra(value); }
    }

    private string? _recoveryCodeHash;

    /// <summary>Hash del codice di recupero, mantenuto per compatibilità con la verifica esistente
    /// (recupero autonomo dalla schermata di login).</summary>
    public string? RecoveryCodeHash { get => _recoveryCodeHash; set { _recoveryCodeHash = value; OnPropertyChanged(nameof(RecoveryCodeHash)); } }

    private string? _recoveryCodeCifrato;

    /// <summary>Chiave di recupero cifrata su disco, stesso principio di PinCifrato. Non usare direttamente:
    /// leggere/scrivere tramite RecoveryCodeChiaro qui sotto.</summary>
    public string? RecoveryCodeCifrato { get => _recoveryCodeCifrato; set { _recoveryCodeCifrato = value; OnPropertyChanged(nameof(RecoveryCodeCifrato)); OnPropertyChanged(nameof(RecoveryCodeChiaro)); } }

    /// <summary>Chiave di recupero in chiaro, fissa per operatore e sempre visibile a un Master (feedback
    /// utente 2026-08-20, punto 2): a differenza di prima non viene più rigenerata automaticamente ad ogni
    /// reset del PIN, solo tramite l'azione esplicita "Rigenera chiave". Decifra/cifra al volo da/verso
    /// RecoveryCodeCifrato — sul disco resta solo il blob cifrato.</summary>
    [JsonIgnore]
    public string? RecoveryCodeChiaro
    {
        get => PinService.Decifra(RecoveryCodeCifrato);
        set { RecoveryCodeCifrato = PinService.Cifra(value); }
    }

    /// <summary>Impostato sui soli account dimostrativi creati con il PIN noto 1234. Dopo il primo
    /// accesso l'utente deve scegliere un PIN personale prima di entrare nell'app.</summary>
    public bool RichiedeCambioPin { get; set; }

    /// <summary>Gamma veicolari assegnate (scoping a livello Gamma, non Sottoveicolo, Addendum §17.3).
    /// Vuoto = nessuno scoping specifico (es. per i Master, o per uno Standard non ancora categorizzato).</summary>
    public List<string> GammeAssegnate { get; set; } = new();

    /// <summary>Vista testuale (comma-separated) di GammeAssegnate, per editing diretto in tabella.</summary>
    [JsonIgnore]
    public string GammeAssegnateTesto
    {
        get => string.Join(", ", GammeAssegnate);
        set => GammeAssegnate = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private string _nuovoPinTemp = "";

    /// <summary>Campo transitorio (mai salvato) usato dall'editor per reimpostare il PIN in chiaro prima
    /// di essere hashato in PinHash.</summary>
    [JsonIgnore]
    public string NuovoPinTemp { get => _nuovoPinTemp; set { _nuovoPinTemp = value; OnPropertyChanged(nameof(NuovoPinTemp)); } }

    /// <summary>Valori "di default personali" per i campi strumento (feedback utente 2026-08-24, punto 3):
    /// ogni operatore salva il proprio valore preferito per un campo (es. Lingua) da qui in poi, invece di un
    /// unico default imposto dal Master per tutti — chiave composta "Strumento::Campo", stessa convenzione di
    /// CampoStrumentoValore. Vince sempre su DefinizioneCampoStrumento.ValoreDefault quando presente, che resta
    /// solo un fallback per chi non ha ancora un proprio valore salvato. Vive sulla cartella condivisa (come il
    /// resto del profilo Operatore) così l'operatore lo ritrova su qualunque postazione.</summary>
    public Dictionary<string, string> ValoriDefaultCampi { get; set; } = new();
}

public class NonConformita
{
    public int SchemaVersion { get; set; } = 3;
    public Guid Uid { get; set; } = Guid.NewGuid();
    public long RevisioneDati { get; set; }
    public string Id { get; set; } = "";
    public string TestCaseId { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TestUid { get; set; }
    public int EsecuzioneIterazione { get; set; }
    /// <summary>Derivato da StatoWorkflow (feedback utente 2026-08-20, punto 6): prima era un campo scritto a
    /// parte, mai risincronizzato quando StatoWorkflow veniva cambiato dal pannello anomalie del Test —
    /// un'anomalia poteva restare "Aperta" qui anche dopo aver raggiunto StatoWorkflow=Conclusa a mano, invece
    /// di sparire dalla lista delle anomalie aperte. Un solo stato scritto, questo è sempre coerente con esso.</summary>
    public StatoNonConformita Stato => StatoWorkflow == StatoAnomalia.Conclusa ? StatoNonConformita.Chiusa : StatoNonConformita.Aperta;
    public DateTime DataApertura { get; set; }
    public DateTime? DataChiusura { get; set; }
    public string Owner { get; set; } = "";
    public StatoAnomalia StatoWorkflow { get; set; } = StatoAnomalia.InVerifica;
    public SeveritaAnomalia? Severita { get; set; }
    public PrioritaOperativa Priorita { get; set; } = PrioritaOperativa.Normal;
    public DateTime? Scadenza { get; set; }
    public string MotivoChiusura { get; set; } = "";
    public List<RiferimentoProva> ProveCollegate { get; set; } = new();
    public List<CollegamentoPolarion> CollegamentiPolarion { get; set; } = new();
    public string Descrizione { get; set; } = "";
    public string Azione { get; set; } = "";
    public Guid? CondizioneOrigineUid { get; set; }
    public List<string> ChangeRequestIds { get; set; } = new();

    /// <summary>Esteso a tutte le NC, non solo UnitLogger (Addendum §16.2).</summary>
    public DestinatarioSegnalazione? Destinatario { get; set; }

    /// <summary>Avanzamento della risoluzione, indipendente da Stato Aperta/Chiusa (Addendum §18).</summary>
    public StatoDettaglioNc? StatoDettaglio { get; set; }

    // Campi extra per NC originate da test UnitLogger (§5)
    public DateTime? DataFix { get; set; }
    public CausaProbabileUdt? CausaProbabile { get; set; }
    public string? CausaProbabileAltro { get; set; }

    /// <summary>Vero solo per una NC nata da una segnalazione diretta della rete assistenziale, mai passata da
    /// un Test (feedback utente 2026-09-08): la rete può segnalare un problema riscontrato su un veicolo prima
    /// ancora che qualcuno esegua un Test per riprodurlo/verificarlo. Resta true anche dopo un eventuale
    /// collegamento successivo a un Test (CollegaATest): serve a distinguere nei report quante NC arrivano da
    /// Test rispetto a quante dalla rete, non solo "questa NC ha oggi un Test collegato o no".</summary>
    public bool DaSegnalazioneRete { get; set; }

    /// <summary>Campi descrittivi propri della NC, popolati SOLO quando DaSegnalazioneRete e' vero: senza un
    /// Test da cui ereditarli (componente/Veicolo/Funzionalità/Strumento vivono normalmente su TestCase, non su
    /// NonConformita) la segnalazione di rete deve poterli portare direttamente con sé. Restano popolati anche
    /// se in seguito CollegaATest imposta TestCaseId: sono la fotografia di cosa la rete ha effettivamente
    /// segnalato, non necessariamente identica a quanto poi il Test di verifica riporta. Nessun Requisito qui
    /// (feedback utente 2026-09-15): a differenza di componente/Veicolo/Funzionalità, il Requisito è un concetto
    /// esclusivo dei Test, non applicabile a una segnalazione che non ne ha ancora uno.</summary>
    public EcuSelection EcuSegnalate { get; set; } = new();
    public List<string> VeicoliSegnalati { get; set; } = new();
    public List<string> FunzionalitaSegnalate { get; set; } = new();
    public string StrumentoSegnalato { get; set; } = "";

    /// <summary>Valori dei campi con Ambito PerTest o PerRevisione (una segnalazione di rete non ha revisioni,
    /// quindi i due livelli restano fusi in un solo dizionario stabile per l'intera segnalazione) — a
    /// differenza di OperazioniSegnalate sotto, che porta i campi PerOperazione.</summary>
    public Dictionary<string, string> ValoriStrumentoSegnalato { get; set; } = new();

    /// <summary>Operazioni della segnalazione (feedback utente 2026-09-18: "i campi stabili per operazioni
    /// dovrebbero essere per operazioni com'avviene durante il test e quindi legate a funzionalità e componente, in
    /// modo da poter avere nel campo strumenti la versione SW/HW legata al componente nel caso... dovrebbe essere il
    /// più simile possibile all'inserimento presente nei test"): granularità esattamente come Condizione in
    /// un Test — una riga per ciascuna coppia Componente×Funzionalità scelta dall'utente fra EcuSegnalate/
    /// FunzionalitaSegnalate sopra, ognuna con i propri valori dei campi Ambito=PerOperazione (es. versione
    /// SW/HW dello strumento, diversa da un componente all'altra). Nessuna semantica di esecuzione/esito qui: una
    /// segnalazione di rete non rieseguisce nulla, a differenza di una Condizione.</summary>
    public List<OperazioneSegnalazione> OperazioniSegnalate { get; set; } = new();

    /// <summary>Sintesi del problema in poche parole (obbligatoria). La descrizione dettagliata riusa il campo
    /// Descrizione già condiviso da tutte le NC, invece di un terzo campo descrittivo separato (feedback utente
    /// 2026-09-15: solo due campi descrizione, "breve" e "dettagliata").</summary>
    public string DescrizioneBreveSegnalata { get; set; } = "";

    /// <summary>Riferimenti pratica/ticket della rete assistenziale che ha originato la segnalazione (una
    /// segnalazione può coprire più pratiche collegate allo stesso problema) — diversi da CollegamentiPolarion,
    /// che sono i ticket TICKET di tracciamento interno del bug. Facoltativi: una segnalazione di rete può non avere ancora un numero Asist.</summary>
    public List<string> NumeriPraticaRete { get; set; } = new();

    /// <summary>matricola dei veicoli cliente coinvolti (una segnalazione può riguardare più veicoli con lo stesso
    /// problema). Obbligatorio: almeno una matricola.</summary>
    public List<string> VinCliente { get; set; } = new();

    /// <summary>Numeri di licenza (feedback utente 2026-09-16: "deve esserci anche licenza e possono esserci
    /// più licenze"): stesso principio di NumeriPraticaRete/VinCliente, campo dedicato in "Origine
    /// segnalazione" invece del campo libero "Licence"/"Licenza" definito dentro lo strumento (che uno
    /// strumento come UnitLogger può comunque avere: resta nascosto dalla stessa logica di dedup che nasconde
    /// "Asist", vedi SegnalazioneReteEditorViewModel.ECampoOrigineSegnalazione). Obbligatorio: almeno una
    /// licenza.</summary>
    public List<string> Licenze { get; set; } = new();
    public string ConcessionariaOfficina { get; set; } = "";

    /// <summary>Nullo per ogni NC "normale". Quando una segnalazione stand-alone genera più NC separate (una
    /// per funzionalità selezionata, feedback utente 2026-09-16: "o più di una non conformità per
    /// funzionalità... dovrà poi essere definita come si fa con tutte le non conformità che hanno ticket ticket
    /// e stato"), tutte le NC nate dalla stessa segnalazione condividono lo stesso Uid qui — non un elenco di
    /// Id "sorelle" da tenere sincronizzato a ogni salvataggio/archiviazione di una di esse, solo un aggancio
    /// per ritrovarle con una query al bisogno (vedi SegnalazioneReteEditorViewModel.TrovaSorelle).</summary>
    public Guid? GruppoSegnalazioneUid { get; set; }

    /// <summary>Allegati della NC (feedback utente 2026-09-15: "dovrà essere possibile... inserire gli
    /// allegati esattamente come i test"), stessa infrastruttura di TestCase/ChangeRequest (AttachmentService,
    /// bucket dedicato su disco keyed su Uid). Non esclusivo delle segnalazioni di rete: qualunque NC può avere
    /// allegati propri, distinti da quelli del Test eventualmente collegato.</summary>
    public List<Allegato> Allegati { get; set; } = new();

    public List<ChangelogEntry> Changelog { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Copia indipendente esplicita (feedback utente 2026-09-17, volumi 100k): NonConformitaRepository.GetAll()
    /// deve restituire una copia isolata a ogni chiamata — l'editor lega i controlli direttamente a Nc.* prima
    /// del Save, e senza isolamento un salvataggio mai confermato dall'utente diventerebbe visibile altrove
    /// nell'app come se fosse già persistito. Prima questa copia passava per un giro JSON completo
    /// (EntityStateFingerprint.Clone), corretto ma troppo lento su un intero archivio da 100k+ record (ogni
    /// GetAll() costava diversi secondi). Stesso risultato di un clone JSON, MemberwiseClone per i campi
    /// scalari più una copia esplicita di ogni collezione mutabile: se si aggiunge una nuova List/Dictionary
    /// qui sopra, va aggiunta anche qui sotto.
    /// </summary>
    /// <summary>Chiamato per OGNI Non Conformità a ogni NonConformitaRepository.GetAll() (isolamento
    /// obbligatorio: TestCaseEditorViewModel muta di proposito un oggetto ottenuto da GetAll() prima di
    /// deciderne il salvataggio, vedi i commenti lì — niente può condividere queste liste con la cache). A
    /// scala 100k il costo fisso di ognuna delle liste sotto, quasi sempre vuote in pratica (una segnalazione
    /// di rete/UnitLogger è la minoranza dei casi), dominava il tempo di apertura pagina: bypassare LINQ quando la
    /// lista sorgente è già vuota evita un enumeratore+iteratore Select per lista per record (Addendum
    /// prestazioni 2026-09-20, feedback utente "navigazione fluida, 700ms su qualunque pagina a piena scala").</summary>
    public NonConformita Clona()
    {
        var copia = (NonConformita)MemberwiseClone();
        copia.ProveCollegate = ProveCollegate.Count == 0 ? new List<RiferimentoProva>() : ProveCollegate.Select(p => new RiferimentoProva
        {
            TestId = p.TestId, TestUid = p.TestUid, EsecuzioneIterazione = p.EsecuzioneIterazione,
            ExtensionData = p.ExtensionData == null ? null : new Dictionary<string, JsonElement>(p.ExtensionData),
            CondizioneUid = p.CondizioneUid, EvidenzaComparativa = p.EvidenzaComparativa
        }).ToList();
        copia.CollegamentiPolarion = CollegamentiPolarion.Count == 0 ? new List<CollegamentoPolarion>() : CollegamentiPolarion.Select(c => new CollegamentoPolarion
        {
            Uid = c.Uid, Codice = c.Codice, Url = c.Url, CollegatoIl = c.CollegatoIl,
            CollegatoDa = c.CollegatoDa, Note = c.Note
        }).ToList();
        copia.ChangeRequestIds = new List<string>(ChangeRequestIds);
        copia.EcuSegnalate = new EcuSelection(EcuSegnalate);
        copia.VeicoliSegnalati = new List<string>(VeicoliSegnalati);
        copia.FunzionalitaSegnalate = new List<string>(FunzionalitaSegnalate);
        copia.ValoriStrumentoSegnalato = new Dictionary<string, string>(ValoriStrumentoSegnalato);
        copia.OperazioniSegnalate = OperazioniSegnalate.Count == 0 ? new List<OperazioneSegnalazione>() : OperazioniSegnalate.Select(o => new OperazioneSegnalazione
        {
            Uid = o.Uid, Componente = o.Componente, Funzionalita = o.Funzionalita, ValoriStrumenti = new Dictionary<string, string>(o.ValoriStrumenti)
        }).ToList();
        copia.NumeriPraticaRete = new List<string>(NumeriPraticaRete);
        copia.VinCliente = new List<string>(VinCliente);
        copia.Licenze = new List<string>(Licenze);
        copia.Allegati = Allegati.Count == 0 ? new List<Allegato>() : Allegati.Select(a => new Allegato
        {
            Uid = a.Uid, NomeFile = a.NomeFile, DataCaricamento = a.DataCaricamento, CaricatoDa = a.CaricatoDa,
            DimensioneByte = a.DimensioneByte, Sha256 = a.Sha256, Descrizione = a.Descrizione, Archiviata = a.Archiviata
        }).ToList();
        copia.Changelog = Changelog.Count == 0 ? new List<ChangelogEntry>() : Changelog.Select(c => new ChangelogEntry
        {
            Data = c.Data, Operatore = c.Operatore, Campo = c.Campo,
            ValorePrecedente = c.ValorePrecedente, ValoreNuovo = c.ValoreNuovo
        }).ToList();
        copia.ExtensionData = ExtensionData == null ? null : new Dictionary<string, JsonElement>(ExtensionData);
        return copia;
    }
}

/// <summary>Un'operazione di una segnalazione di rete (feedback utente 2026-09-18): coppia Componente×Funzionalità
/// scelta fra NonConformita.EcuSegnalate/FunzionalitaSegnalate, con i propri valori dei campi strumento
/// Ambito=PerOperazione — esattamente il ruolo che Condizione ha per un Test, senza però la semantica di
/// esecuzione/esito che qui non si applica. CampiVisual/GruppiVisual transitori, mai salvati, ricalcolati dal
/// ViewModel: stesso schema di Condizione.CampiVisual/GruppiVisual in TestCase.cs.</summary>
public sealed class OperazioneSegnalazione : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Guid Uid { get; set; } = Guid.NewGuid();

    private string _ecu = "";
    public string Componente { get => _ecu; set { _ecu = value; OnPropertyChanged(nameof(Componente)); } }

    private string _funzionalita = "";
    public string Funzionalita { get => _funzionalita; set { _funzionalita = value; OnPropertyChanged(nameof(Funzionalita)); } }

    public Dictionary<string, string> ValoriStrumenti { get; set; } = new();

    private List<CampoStrumentoValore>? _campiVisual;
    [JsonIgnore]
    public List<CampoStrumentoValore>? CampiVisual
    {
        get => _campiVisual;
        set { _campiVisual = value; OnPropertyChanged(nameof(CampiVisual)); }
    }

    private List<GruppoCampiStrumento>? _gruppiVisual;
    [JsonIgnore]
    public List<GruppoCampiStrumento>? GruppiVisual
    {
        get => _gruppiVisual;
        set { _gruppiVisual = value; OnPropertyChanged(nameof(GruppiVisual)); }
    }
}

public class RiferimentoProva
{
    public string TestId { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TestUid { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Revisione che contiene la prova. Zero mantiene compatibili i riferimenti storici creati
    /// prima che una stessa CondizioneUid potesse apparire negli snapshot di piu revisioni.</summary>
    public int EsecuzioneIterazione { get; set; }

    /// <summary>Riferisce direttamente la Condizione (reiterazione) che ha generato la segnalazione: ogni
    /// reiterazione porta già il proprio esito, non serve più il doppio livello Verifica/Prova.</summary>
    public Guid CondizioneUid { get; set; }
    public bool EvidenzaComparativa { get; set; }
}

public class CollegamentoPolarion
{
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string Codice { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime CollegatoIl { get; set; } = DateTime.Now;
    public string CollegatoDa { get; set; } = "";
    public string Note { get; set; } = "";
}

public class SegnalazioneDaClassificare
{
    public int SchemaVersion { get; set; } = 2;
    public Guid Uid { get; set; } = Guid.NewGuid();

    /// <summary>Incrementata a ogni salvataggio e verificata prima della scrittura (Addendum §20), come già
    /// per TestCase/NonConformita: senza, due Master che classificano/escludono la stessa segnalazione nello
    /// stesso momento si sovrascrivono in silenzio invece di ricevere un conflitto da risolvere.</summary>
    public long RevisioneDati { get; set; }
    public string TestId { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TestUid { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    public Guid CondizioneUid { get; set; }
    public int EsecuzioneIterazione { get; set; }
    public DateTime CreataIl { get; set; } = DateTime.Now;
    public string CreataDa { get; set; } = "";
    public StatoSegnalazione Stato { get; set; } = StatoSegnalazione.DaClassificare;
    public string MotivazioneEsclusione { get; set; } = "";
    public Guid? AnomaliaUid { get; set; }
}
