using System.ComponentModel;

namespace TestLoom.App.Models;

/// <summary>Ruolo funzionale stabile di un campo del catalogo: disaccoppia "a cosa serve nel codice"
/// (Coverage/filtri dashboard/dedup/import Excel) da "come si chiama e che tipo ha oggi" (rinominabile e
/// ritipizzabile liberamente dal Master in Impostazioni). Al più un CampoSistemaDefinizione per ruolo
/// (diverso da Nessuno) — se nessun campo porta un dato ruolo, la funzione collegata si disattiva invece
/// di lanciare un'eccezione (vedi RuoloCampoSistemaLookup).</summary>
public enum RuoloCampoSistema
{
    Nessuno,
    Componente,
    Veicolo,
    Funzionalita,
    StrumentiUsati,
    StatoLavoro,
    Scadenza,
    Assegnatario,

    /// <summary>Non è un campo di TestCase (vive su Veicolo.GammaNome): il ruolo serve solo a rendere
    /// rinominabile l'intestazione del pannello "Gamma" annidato dentro Impostazioni → Veicoli (Addendum:
    /// grill-me 2026-08-18, follow-up — "ogni campo di sistema... dovrebbe essere possibile editarlo").</summary>
    Gamma,

    /// <summary>Non è un campo di TestCase (vive su Funzionalita.Categoria): stesso principio di Gamma, per
    /// l'intestazione del pannello "Categoria" annidato dentro Impostazioni → Funzionalità.</summary>
    Categoria,
}

public enum GruppoCampoSistema { Pianificazione, Dettagli }

/// <summary>Una voce del catalogo campi di sistema, editabile in Impostazioni: sostituisce le costanti fisse
/// di CampiSistema.Chiavi/ChiaviPianificazione con righe dati vere, sullo stesso principio già usato per
/// Gamma/Categoria (Addendum: grill-me 2026-08-18, punto 1).</summary>
public class CampoSistemaDefinizione : MasterEntity, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string nome) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nome));

    public Guid Uid { get; set; } = Guid.NewGuid();

    /// <summary>Chiave macchina stabile, mai mostrata: generata una volta alla creazione e mai più cambiata,
    /// a differenza di Etichetta — è quello che i campi custom sui TestCase usano come chiave, così rinominare
    /// Etichetta non spezza i dati già salvati.</summary>
    public string Chiave { get; set; } = "";

    public string Etichetta { get; set; } = "";
    public GruppoCampoSistema Gruppo { get; set; } = GruppoCampoSistema.Dettagli;
    private TipoCampoStrumento _tipo = TipoCampoStrumento.Testo;
    private List<string> _opzioniLista = new();

    /// <summary>Notifica il cambio di Tipo: il pulsante delle opzioni nella griglia del catalogo compare o scompare
    /// appena cambia il menu a tendina Tipo, senza ricreare la riga. Le proprietà restano nello stesso ordine di
    /// dichiarazione, quindi il JSON persistito (e il payload Phase D) non cambia.</summary>
    public TipoCampoStrumento Tipo
    {
        get => _tipo;
        set { if (_tipo == value) return; _tipo = value; OnPropertyChanged(nameof(Tipo)); }
    }

    /// <summary>Opzioni del menu a tendina, solo per Tipo=Lista (come DefinizioneCampoStrumento.OpzioniLista).</summary>
    public List<string> OpzioniLista
    {
        get => _opzioniLista;
        set { _opzioniLista = value; OnPropertyChanged(nameof(OpzioniLista)); }
    }

    /// <summary>Solo per Tipo=Numero: unità di misura mostrata accanto al valore nel Test (es. "km", "°C"), come per i campi strumento
    /// e i campi liberi del Tipo di test.</summary>
    public string? UnitaMisura { get; set; }
    public RuoloCampoSistema Ruolo { get; set; } = RuoloCampoSistema.Nessuno;
    public bool Archiviata { get; set; }

    /// <summary>A discrezione del Master, non automatico (Addendum: grill-me 2026-08-18, terza follow-up —
    /// "non mi sembra possibile togliere dai filtri i campi a mia discrezione, tipo descrizione... non hanno
    /// senso come filtri"): di default false anche per i campi Ruolo=Nessuno appena creati, sia perché i campi
    /// storici (Descrizione, DescrizioneBreve, Matricola, ecc. — seminati con Ruolo=Nessuno per mancanza di un ruolo
    /// dedicato) non devono comparire come filtro senza un'azione esplicita, sia perché senza un default
    /// esplicito ogni riga già su disco prima di questo campo lo leggerebbe comunque come false alla
    /// deserializzazione — coerente in entrambi i casi, nessuna migrazione dati necessaria.</summary>
    public bool UsabileComeFiltro { get; set; }
}
