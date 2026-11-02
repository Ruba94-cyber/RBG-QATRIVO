using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Proiezione di sola lettura di un Test per le viste analitiche Audit e Report (LocalTestArchiveQueryService.QueryAllAnalisi).
/// Legge lo stesso JSON autorevole della cache di indice e produce un <see cref="TestCase"/> con i soli campi che le matrici,
/// l'anteprima e l'abbinamento componente dell'Audit leggono: identita, componente, veicoli, template, requisiti, funzionalita,
/// assegnatario, campi custom, e per ogni revisione data, esito, condizioni (esito, componente, funzionalita, guasto, legame di
/// ripetizione) e punteggi per requisito. Changelog, allegati, Dettagli per revisione, valori strumenti, note, collegamenti,
/// impatti e pianificazione NON vengono letti e restano al valore predefinito. Il risultato serve solo al calcolo e alla
/// visualizzazione: export, editor e salvataggi continuano a usare il Test completo (QueryAll o lo snapshot in memoria).
/// Il JSON condiviso e l'indice non cambiano: questo tipo non e' mai persistito.
/// </summary>
public sealed class TestCaseProiezioneAnalisi
{
    public Guid Uid { get; set; }
    public string Id { get; set; } = "";
    public DateTime DataCreazione { get; set; }
    public DateTime UltimaModifica { get; set; }
    public bool Eliminato { get; set; }
    public EcuSelection Componente { get; set; } = new();
    public string Veicolo { get; set; } = "";
    public List<string> Veicoli { get; set; } = new();
    public Guid TestTemplateUid { get; set; }
    public List<string> RequisitoId { get; set; } = new();
    public List<string> Funzionalita { get; set; } = new();
    public string? Assegnatario { get; set; }
    public Dictionary<string, string> CampiCustom { get; set; } = new();
    public List<EsecuzioneProiezioneAnalisi> Esecuzioni { get; set; } = new();

    /// <summary>Test con i campi proiettati; gli altri restano ai valori predefiniti del modello.</summary>
    public TestCase ToTestCase() => new()
    {
        Uid = Uid,
        Id = Id,
        DataCreazione = DataCreazione,
        UltimaModifica = UltimaModifica,
        Eliminato = Eliminato,
        Componente = Componente,
        Veicolo = Veicolo,
        Veicoli = Veicoli,
        TestTemplateUid = TestTemplateUid,
        RequisitoId = RequisitoId,
        Funzionalita = Funzionalita,
        Assegnatario = Assegnatario,
        CampiCustom = CampiCustom,
        Esecuzioni = Esecuzioni.ConvertAll(e => e.ToEsecuzione()),
    };
}

/// <summary>Revisione (Esecuzione) nella proiezione: stessi campi letti dalle matrici; Dettagli, valori strumenti, note e matricola non vengono letti.</summary>
public sealed class EsecuzioneProiezioneAnalisi
{
    public int Iterazione { get; set; }
    public DateTime Data { get; set; }
    public string Operatore { get; set; } = "";
    public OrigineRevisione Origine { get; set; } = OrigineRevisione.Operatore;
    public Esito Esito { get; set; }
    public bool EsitoSovrascritto { get; set; }
    public List<CondizioneProiezioneAnalisi> Condizioni { get; set; } = new();
    public List<PunteggioRequisito> PunteggiRequisiti { get; set; } = new();

    internal Esecuzione ToEsecuzione() => new()
    {
        Iterazione = Iterazione,
        Data = Data,
        Operatore = Operatore,
        Origine = Origine,
        Esito = Esito,
        EsitoSovrascritto = EsitoSovrascritto,
        Condizioni = Condizioni.ConvertAll(c => c.ToCondizione()),
        PunteggiRequisiti = PunteggiRequisiti,
    };
}

/// <summary>Operazione nella proiezione: <see cref="TestCase"/> e <see cref="Esecuzione.CondizioniEffettive"/> raggruppano per
/// OperazioneOrigineUid oppure Uid, quindi entrambi restano; il DTO serve a <c>RiguardaDtc</c> e a <c>EcuUnits</c>.</summary>
public sealed class CondizioneProiezioneAnalisi
{
    public Guid Uid { get; set; }
    public Guid? OperazioneOrigineUid { get; set; }
    public string? Guasto { get; set; }
    public EcuSelection Componente { get; set; } = new();
    public string Funzionalita { get; set; } = "";
    public Esito Esito { get; set; }

    /// <summary>TIME (6.6.1): dai valori strumenti la proiezione legge solo la durata riservata; gli altri valori restano non letti.</summary>
    public Dictionary<string, string> ValoriStrumenti
    {
        get => new();
        set => _durataMinuti = DurataOperazione.Leggi(value);
    }

    private int? _durataMinuti;

    internal Condizione ToCondizione() => new()
    {
        Uid = Uid,
        OperazioneOrigineUid = OperazioneOrigineUid,
        Guasto = Guasto,
        Componente = Componente,
        Funzionalita = Funzionalita,
        Esito = Esito,
        DurataMinuti = _durataMinuti,
    };
}
