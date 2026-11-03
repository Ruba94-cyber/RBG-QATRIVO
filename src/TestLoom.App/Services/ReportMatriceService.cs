using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>L'universo delle combinazioni supera il limite di righe di un foglio Excel: l'export delle pivot deve degradare in modo esplicito.</summary>
public sealed class LimiteUniversoExportSuperatoException() : InvalidOperationException(LocalizationService.Instance.Scegli(
    "Troppe combinazioni per le pivot Excel.", "Too many combinations for Excel pivots.", "Trop de combinaisons pour les tableaux croisés Excel."));

public static class DimensioniMatrice
{
    public const string Gamma = "gamma";
    public const string Sottoveicolo = "sottoveicolo";
    public const string Componente = "componente";
    public const string CategoriaFunzionalita = "categoria-funzionalita";
    public const string Funzionalita = "funzionalita";
    public const string Template = "template";
    public const string Esito = "esito";
    public const string Assegnatario = "assegnatario";
    public const string Requisito = "requisito";
    public const string CampoCustomPrefix = "custom:";
}
/// <summary>Modo della cella della matrice (decisione utente 6.6.1). Percentuale (etichetta UI "Stato %", default) mostra
/// la distribuzione dello stato corrente per funzionalità×componente×veicolo; Stato mostra testo/colore; TestPercentuale distribuisce
/// lo stato corrente dei TestCase unici.</summary>
/// <para>TIME (6.6.1): TempoTotale e TempoMedio sono modalita' temporali (base in ConfigurazioneMatriceReport.BaseTempo). In coda
/// per non cambiare i codici numerici gia' salvati delle preferenze.</para>
public enum ModalitaCellaMatrice { Percentuale, Stato, TestPercentuale, TempoTotale, TempoMedio }

public sealed class ConfigurazioneMatriceReport
{
    /// <summary>Stati nascosti solo nella resa della matrice. I conteggi e i denominatori non vengono filtrati.</summary>
    public HashSet<StatoCellaMatrice> StatiNascostiPresentazione { get; set; } = [];
    /// <summary>Filtro per stato (vuoto = nessun filtro): restano visibili solo le righe e le colonne con almeno una cella
    /// (non N.A.) in uno di questi stati. Agisce sugli assi, quindi vale anche per l'export; i totali non cambiano.</summary>
    public HashSet<StatoCellaMatrice> StatiFiltroRigheColonne { get; set; } = [];
    /// <summary>Requisiti ristretti ad alcune componente (id requisito -> componente ammesse, solo Extra con coppie veicolo-componente): le celle verso le
    /// altre componente sono N.A. e i componenti senza alcun requisito applicabile escono dalla matrice. Derivato dai dati, mai persistito.</summary>
    public Dictionary<string, HashSet<string>> EcuRistrettePerRequisito { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Decisione utente 6.6.1: fino a tre campi per asse, annidati (il primo è il livello esterno).</summary>
    public const int MassimoCampiPerAsse = 3;

    /// <summary>Campi dell'asse X (colonne) e Y (righe). Lista vuota = asse a colonna/riga unica (etichetta del modo scelto).
    /// Almeno uno dei due assi deve avere un campo (verificato dal ViewModel).</summary>
    public List<string> CampiX { get; set; } = new() { DimensioniMatrice.Componente };
    public List<string> CampiY { get; set; } = new() { DimensioniMatrice.CategoriaFunzionalita };

    /// <summary>Compatibilità con la forma a un campo per asse (configurazioni e chiamanti esistenti): primo campo.</summary>
    public string AsseX
    {
        get => CampiX.Count > 0 ? CampiX[0] : "";
        set => CampiX = string.IsNullOrEmpty(value) ? new List<string>() : new List<string> { value };
    }
    public string AsseY
    {
        get => CampiY.Count > 0 ? CampiY[0] : "";
        set => CampiY = string.IsNullOrEmpty(value) ? new List<string>() : new List<string> { value };
    }

    public ModalitaCellaMatrice ModalitaCella { get; set; } = ModalitaCellaMatrice.Percentuale;

    /// <summary>Base dei tempi (TIME, 6.6.1): usata solo dalle modalità Tempo totale e Tempo medio.</summary>
    public BaseTempoMatrice BaseTempo { get; set; } = BaseTempoMatrice.TutteLeEsecuzioni;

    /// <summary>Filtro TIME sulla durata della singola operazione (tentativo): null = nessun filtro. Un'operazione senza durata non corrisponde.</summary>
    public FiltroDurataMinuti? FiltroTempoOperazione { get; set; }

    /// <summary>Filtro TIME sul totale minuti del Test (somma delle sue operazioni eseguite): null = nessun filtro.</summary>
    public FiltroDurataMinuti? FiltroTempoTotaleTest { get; set; }

    public Dictionary<string, List<string>> Filtri { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordine personale per DIMENSIONE (§11.3b): vale per ogni livello che usa quella dimensione.</summary>
    public Dictionary<string, IReadOnlyList<string>> Ordini { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Compatibilità: ordine del primo campo di ciascun asse, letto se <see cref="Ordini"/> non lo contiene.</summary>
    public IReadOnlyList<string>? OrdineAsseX { get; set; }
    public IReadOnlyList<string>? OrdineAsseY { get; set; }

    /// <summary>Gruppi compressi della vista (chiavi da <see cref="ReportMatriceService.ChiaveCompressione"/>). Solo vista:
    /// non cambia conteggi né totali, e l'export usa sempre la matrice completa.</summary>
    public HashSet<string> GruppiCompressi { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Copia della configurazione con altri filtri di matrice: uso interno dell'universo "Non testato" (vedi ConfigurazioneUniverso).</summary>
    internal ConfigurazioneMatriceReport ConFiltri(Dictionary<string, List<string>> filtri)
    {
        var copia = (ConfigurazioneMatriceReport)MemberwiseClone();
        copia.Filtri = filtri;
        return copia;
    }
}

/// <summary>Stato di una combinazione per la modalità Stato: esito eseguito più recente, oppure prevista ma non
/// eseguita, oppure mai testata (solo catalogo o solo N.A.).</summary>
public enum StatoCombinazioneMatrice { Conforme, NonConforme, ConformeConDeviazioni, DaEseguire, NonTestato, DaDecidere }

/// <summary>Codice di stato di una cella: uno stato solo, oppure Misto se gli stati presenti sono più d'uno.</summary>
public enum StatoCellaMatrice { Vuota, Conforme, NonConforme, ConformeConDeviazioni, DaEseguire, NonTestato, DaDecidere, Misto }

/// <summary>Conteggi per stato di una cella o di un totale: ogni combinazione conta UNA volta, con il suo ultimo esito.</summary>
public readonly record struct StatiMatrice(int Ok, int NonConforme, int ConformeConDeviazioni, int DaEseguire, int NonTestato, int DaDecidere)
{
    public int Totale => Ok + NonConforme + ConformeConDeviazioni + DaEseguire + NonTestato + DaDecidere;

    public StatoCellaMatrice Codice
    {
        get
        {
            var presenti = (Ok > 0 ? 1 : 0) + (NonConforme > 0 ? 1 : 0) + (ConformeConDeviazioni > 0 ? 1 : 0)
                + (DaEseguire > 0 ? 1 : 0) + (NonTestato > 0 ? 1 : 0) + (DaDecidere > 0 ? 1 : 0);
            if (presenti == 0) return StatoCellaMatrice.Vuota;
            if (presenti > 1) return StatoCellaMatrice.Misto;
            if (Ok > 0) return StatoCellaMatrice.Conforme;
            if (NonConforme > 0) return StatoCellaMatrice.NonConforme;
            if (ConformeConDeviazioni > 0) return StatoCellaMatrice.ConformeConDeviazioni;
            if (DaDecidere > 0) return StatoCellaMatrice.DaDecidere;
            return DaEseguire > 0 ? StatoCellaMatrice.DaEseguire : StatoCellaMatrice.NonTestato;
        }
    }

    /// <summary>Riepilogo dei conteggi, es. "2 OK / 1 NOK / 1 dev.": solo gli stati presenti.</summary>
    public string Riepilogo
    {
        get
        {
            var parti = new List<string>(5);
            if (Ok > 0) parti.Add($"{Ok} {Traduci("MatriceStatoAbbrOk")}");
            if (NonConforme > 0) parti.Add($"{NonConforme} {Traduci("MatriceStatoAbbrNok")}");
            if (ConformeConDeviazioni > 0) parti.Add($"{ConformeConDeviazioni} {Traduci("MatriceStatoAbbrDev")}");
            if (DaEseguire > 0) parti.Add($"{DaEseguire} {Traduci("MatriceStatoAbbrDa")}");
            if (NonTestato > 0) parti.Add($"{NonTestato} {Traduci("MatriceStatoAbbrNt")}");
            if (DaDecidere > 0) parti.Add($"{DaDecidere} {Traduci("MatriceStatoAbbrDecidere")}");
            return string.Join(" / ", parti);
        }
    }

    public string RiepilogoVisibile(IReadOnlySet<StatoCellaMatrice>? nascosti)
    {
        var parti = new List<string>(6);
        if (Ok > 0 && nascosti?.Contains(StatoCellaMatrice.Conforme) != true) parti.Add($"{Ok} {Traduci("MatriceStatoAbbrOk")}");
        if (NonConforme > 0 && nascosti?.Contains(StatoCellaMatrice.NonConforme) != true) parti.Add($"{NonConforme} {Traduci("MatriceStatoAbbrNok")}");
        if (ConformeConDeviazioni > 0 && nascosti?.Contains(StatoCellaMatrice.ConformeConDeviazioni) != true) parti.Add($"{ConformeConDeviazioni} {Traduci("MatriceStatoAbbrDev")}");
        if (DaEseguire > 0 && nascosti?.Contains(StatoCellaMatrice.DaEseguire) != true) parti.Add($"{DaEseguire} {Traduci("MatriceStatoAbbrDa")}");
        if (NonTestato > 0 && nascosti?.Contains(StatoCellaMatrice.NonTestato) != true) parti.Add($"{NonTestato} {Traduci("MatriceStatoAbbrNt")}");
        if (DaDecidere > 0 && nascosti?.Contains(StatoCellaMatrice.DaDecidere) != true) parti.Add($"{DaDecidere} {Traduci("MatriceStatoAbbrDecidere")}");
        return string.Join(" / ", parti);
    }

    public string PercentualiVisibili(IReadOnlySet<StatoCellaMatrice>? nascosti)
    {
        var totale = Totale;
        if (totale == 0) return "—";
        string Quota(int valore) => $"{100d * valore / totale:0.#}%";
        var parti = new List<string>(6);
        if (nascosti?.Contains(StatoCellaMatrice.Conforme) != true) parti.Add($"{Traduci("MatriceStatoAbbrOk")} {Quota(Ok)}");
        if (nascosti?.Contains(StatoCellaMatrice.NonConforme) != true) parti.Add($"{Traduci("MatriceStatoAbbrNok")} {Quota(NonConforme)}");
        if (nascosti?.Contains(StatoCellaMatrice.ConformeConDeviazioni) != true) parti.Add($"{Traduci("MatriceStatoAbbrDev")} {Quota(ConformeConDeviazioni)}");
        if (nascosti?.Contains(StatoCellaMatrice.DaEseguire) != true) parti.Add($"{Traduci("MatriceStatoAbbrDa")} {Quota(DaEseguire)}");
        if (nascosti?.Contains(StatoCellaMatrice.NonTestato) != true) parti.Add($"{Traduci("MatriceStatoAbbrNt")} {Quota(NonTestato)}");
        if (nascosti?.Contains(StatoCellaMatrice.DaDecidere) != true) parti.Add($"{Traduci("MatriceStatoAbbrDecidere")} {Quota(DaDecidere)}");
        return string.Join(" · ", parti);
    }

    public string TestoVisibile(IReadOnlySet<StatoCellaMatrice>? nascosti) => Codice switch
    {
        StatoCellaMatrice.Vuota => "",
        StatoCellaMatrice.Misto => RiepilogoVisibile(nascosti) is { Length: > 0 } r ? $"{Traduci("MatriceStatoMisto")}: {r}" : "",
        StatoCellaMatrice.Conforme when nascosti?.Contains(StatoCellaMatrice.Conforme) == true => "",
        StatoCellaMatrice.NonConforme when nascosti?.Contains(StatoCellaMatrice.NonConforme) == true => "",
        StatoCellaMatrice.ConformeConDeviazioni when nascosti?.Contains(StatoCellaMatrice.ConformeConDeviazioni) == true => "",
        StatoCellaMatrice.DaEseguire when nascosti?.Contains(StatoCellaMatrice.DaEseguire) == true => "",
        StatoCellaMatrice.NonTestato when nascosti?.Contains(StatoCellaMatrice.NonTestato) == true => "",
        StatoCellaMatrice.DaDecidere when nascosti?.Contains(StatoCellaMatrice.DaDecidere) == true => "",
        _ => Testo,
    };

    /// <summary>Distribuzione di tutte le categorie sul totale, inclusi stati con quota zero. Il totale resta
    /// identico alla somma delle quote (a parte l'arrotondamento visuale).</summary>
    public string TestoPercentuali
    {
        get
        {
            var totale = Totale;
            if (totale == 0) return "—";
            string Quota(int valore) => $"{100d * valore / totale:0.#}%";
            return string.Join(" · ", new[]
            {
                $"{Traduci("MatriceStatoAbbrOk")} {Quota(Ok)}",
                $"{Traduci("MatriceStatoAbbrNok")} {Quota(NonConforme)}",
                $"{Traduci("MatriceStatoAbbrDev")} {Quota(ConformeConDeviazioni)}",
                $"{Traduci("MatriceStatoAbbrDa")} {Quota(DaEseguire)}",
                $"{Traduci("MatriceStatoAbbrNt")} {Quota(NonTestato)}",
                $"{Traduci("MatriceStatoAbbrDecidere")} {Quota(DaDecidere)}",
            });
        }
    }

    /// <summary>Testo della cella in modalità Stato: lo stato dell'esito, o "Misto: ..." con i conteggi.</summary>
    public string Testo => Codice switch
    {
        StatoCellaMatrice.Vuota => "",
        StatoCellaMatrice.Misto => $"{Traduci("MatriceStatoMisto")}: {Riepilogo}",
        StatoCellaMatrice.Conforme => Esito.Conforme.Label(),
        StatoCellaMatrice.NonConforme => Esito.NonConforme.Label(),
        StatoCellaMatrice.ConformeConDeviazioni => Esito.ConformeConDeviazioni.Label(),
        StatoCellaMatrice.DaEseguire => Esito.DaEseguire.Label(),
        StatoCellaMatrice.DaDecidere => Traduci("MatriceStatoAbbrDecidere"),
        _ => Traduci("MatriceStatoNonTestato"),
    };

    private static string Traduci(string chiave) => LocalizationService.Instance.Traduci(chiave);
}

/// <summary>Autore, data e motivazione di un valore manuale presente nella cella della matrice.</summary>
public sealed record DettaglioValoreManuale(string Autore, DateTime? Data, string Nota, bool Superato);

public sealed class CellaMatriceReport
{
    public IReadOnlySet<StatoCellaMatrice> StatiNascostiPresentazione { get; init; } = new HashSet<StatoCellaMatrice>();
    public string Colonna { get; init; } = "";

    /// <summary>Valore della riga proprietaria (popolato da MatricePreparata.Finestra insieme a Colonna): non
    /// entra nel calcolo dei conteggi, serve solo a identificare la cella per l'overlay N.A. e i comandi della
    /// vista senza binding relativi annidati complessi in Avalonia.</summary>
    public string Riga { get; init; } = "";

    public int Conformi { get; init; }
    public int Valutati { get; init; }
    public int Totali { get; init; }

    /// <summary>Conteggi di stato correnti per cella, usati dalla modalità Stato e per la distribuzione percentuale.</summary>
    public StatiMatrice Stati { get; init; }

    /// <summary>Distribuzione percentuale secondo la base selezionata, senza cambiare il testo dello stato corrente.</summary>
    public StatiMatrice StatiPercentuale { get; init; }
    public bool MostraConteggioTest { get; init; }

    /// <summary>Combinazioni della cella il cui esito e' un valore manuale del Master che vince (matita) o che e' stato superato da un Test piu' recente.</summary>
    public int Manuali { get; init; }
    public int ManualiSuperati { get; init; }
    public string? NotaManuale { get; init; }
    public IReadOnlyList<DettaglioValoreManuale> ValoriManuali { get; init; } = [];
    public string? TooltipManuale => ValoriManuali.Count == 0 ? null : string.Join(Environment.NewLine, ValoriManuali.Select(v =>
        string.Format(LocalizationService.Instance.Traduci("MatriceValoreManualeTooltipFmt"),
            v.Superato ? LocalizationService.Instance.Traduci("MatriceValoreManualeTooltipSuperato") : "",
            v.Autore, v.Data?.ToString("dd/MM/yyyy") ?? "—", v.Nota)));

    /// <summary>Modo della vista per questa cella: sceglie il testo mostrato (vedi TestoVisualizzato).</summary>
    public bool ModalitaStato { get; init; }

    /// <summary>Valore temporale della cella (TIME): usato dalle modalità Tempo totale e Tempo medio.</summary>
    public ValoreTempo Tempo { get; init; }

    /// <summary>Modalita' temporale della vista (TIME): quando valorizzata, Testo mostra tempo e completezza (N.A. resta prioritario).</summary>
    public ModalitaCellaMatrice? ModalitaTempo { get; init; }

    /// <summary>Cella aggregata (gruppo compresso, totale di gruppo, asse senza campi): conteggi per combinazione distinta.</summary>
    public bool Aggregata { get; init; }

    /// <summary>False se la cella cade in un gruppo compresso: la marcatura N.A. vale solo per le celle elementari.</summary>
    public bool Marcabile { get; init; } = true;

    /// <summary>Marcatura manuale "N.A." (decisione utente 6.4.0, punto 4), applicata DOPO il calcolo dei
    /// conteggi da MatriceNonApplicabileService.Applica — mai impostata qui. Una cella marcata è esclusa dalle
    /// percentuali (vedi Percentuale sotto) ma il conteggio grezzo resta intatto per quando la marcatura viene
    /// rimossa.</summary>
    public bool NonApplicabile { get; set; }
    public string? NotaNonApplicabile { get; set; }

    public double? Percentuale => NonApplicabile || Totali == 0 ? null : 100d * Conformi / Totali;
    public string Testo
    {
        get
        {
            if (NonApplicabile) return "N.A.";
            if (ModalitaTempo is { } temporale) return ReportMatriceService.TestoTempo(Tempo, temporale);
            var testo = StatiPercentuale.PercentualiVisibili(StatiNascostiPresentazione);
            return MostraConteggioTest && Totali > 0 ? $"{testo} ({Totali} test)" : testo;
        }
    }

    /// <summary>Testo in modalità Stato: lo stato della cella, o "Misto" con i conteggi.</summary>
    public string TestoStato => Stati.TestoVisibile(StatiNascostiPresentazione) + IndicatoreManuale;

    /// <summary>Indicatore del valore manuale: matita se il manuale vince, "superato" se un Test piu' recente lo ha superato.</summary>
    public string IndicatoreManuale => Manuali > 0 ? " ✎" : ManualiSuperati > 0 ? " ✎ " + Traduci("MatriceManualeSuperato") : "";

    private static string Traduci(string chiave) => LocalizationService.Instance.Traduci(chiave);

    /// <summary>Testo effettivamente mostrato dalla vista e dall'export: N.A. ha la precedenza, poi il modo scelto.</summary>
    public string TestoVisualizzato => NonApplicabile ? "N.A." : ModalitaStato ? TestoStato : Testo;

    /// <summary>Codice di stato per colore e legenda (sempre Vuota in modalità Percentuale).</summary>
    public StatoCellaMatrice CodiceStato => Stati.Codice;

    /// <summary>AutomationId stabile per la cella (test FlaUI): Riga/Colonna sono valori dati, quindi
    /// sanitizzati per restare validi come identificativo. Nessun uso in logica/persistenza, solo per la vista.
    /// AutomationIdTesto e' sul TextBlock interno (non sul Border): letto da FlaUI per verificare il testo
    /// mostrato ("N.A.", una percentuale...), stessa tecnica di NonConformitaPage.TestoPaginazione.</summary>
    public string AutomationId => $"ReportMatrice_Cella_{Sanitizza(Riga)}_{Sanitizza(Colonna)}";
    public string AutomationIdTesto => AutomationId + "_Text";
    private static string Sanitizza(string valore) => new(valore.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
}

/// <summary>Totale di intestazione (colonna, riga o generale): distribuzione degli stati per base selezionata, deduplicata
/// fra le celle in cui compare ciascuna combinazione funzionalita×componente×veicolo o TestCase. Vuoto quando la base non contiene elementi applicabili.</summary>
public sealed class TotaleMatriceReport
{
    public IReadOnlySet<StatoCellaMatrice> StatiNascostiPresentazione { get; init; } = new HashSet<StatoCellaMatrice>();
    public int Conformi { get; init; }
    public int Valutati { get; init; }
    public StatiMatrice Stati { get; init; }
    public StatiMatrice StatiPercentuale { get; init; }
    public bool MostraConteggioTest { get; init; }
    public double? Percentuale => StatiPercentuale.Totale == 0 ? null : 100d * StatiPercentuale.Ok / StatiPercentuale.Totale;
    public string Testo
    {
        get
        {
            if (ModalitaTempo is { } temporale) return ReportMatriceService.TestoTempo(Tempo, temporale);
            var testo = StatiPercentuale.PercentualiVisibili(StatiNascostiPresentazione);
            return MostraConteggioTest && StatiPercentuale.Totale > 0 ? $"{testo} ({StatiPercentuale.Totale} test)" : testo;
        }
    }

    /// <summary>Modalita' temporale del totale (TIME): quando valorizzata, Testo mostra tempo e completezza.</summary>
    public ModalitaCellaMatrice? ModalitaTempo { get; init; }

    /// <summary>Stato dell'intestazione in modalità Stato ("—" se nessuna combinazione).</summary>
    public string TestoStato => Stati.Totale == 0 ? "—" : Stati.TestoVisibile(StatiNascostiPresentazione);

    /// <summary>Valore temporale del totale (TIME): stessa deduplica per operazione dei totali di stato.</summary>
    public ValoreTempo Tempo { get; init; }

    /// <summary>AutomationId stabile per i test FlaUI (punto 7 dell'addendum): mai riusato da AutomationId di
    /// cella (prefisso diverso), cosi' un valore di riga/colonna omonimo a un valore dell'altro asse non puo'
    /// mai collidere.</summary>
    public string AutomationId { get; init; } = "";

    internal static string Sanitizza(string valore) => new(valore.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
    public const string AutomationIdGenerale = "ReportMatrice_TotaleGenerale";
    public static string AutomationIdColonna(string valoreColonna) => $"ReportMatrice_TotaleColonna_{Sanitizza(valoreColonna)}";
    public static string AutomationIdRiga(string valoreRiga) => $"ReportMatrice_TotaleRiga_{Sanitizza(valoreRiga)}";
}

public sealed class RigaMatriceReport
{
    /// <summary>Chiave della riga: valori dei campi dell'asse annidati (con un solo campo coincide col valore).</summary>
    public string Riga { get; init; } = "";
    public List<CellaMatriceReport> Celle { get; init; } = new();

    /// <summary>Totale della riga (decisione utente 6.4.0, addendum totali): sempre presente, mai null — vale
    /// "—" (Valutati=0) quando nessuna combinazione non-N.A. della riga e' stata valutata.</summary>
    public TotaleMatriceReport TotaleRiga { get; init; } = new();

    /// <summary>Profondità della riga nel suo gruppo (1..3); 0 per l'asse senza campi.</summary>
    public int Livello { get; init; } = 1;

    /// <summary>Riga aggregata: gruppo compresso oppure asse senza campi (conteggi per combinazione distinta).</summary>
    public bool Aggregata { get; init; }

    /// <summary>Gruppo compresso: la riga rappresenta tutte le righe del gruppo.</summary>
    public bool Compressa { get; init; }

    /// <summary>Percorso completo leggibile, es. "Daily › BCM".</summary>
    public string Percorso => ReportMatriceService.EtichettaPercorso(Riga);
}

/// <summary>Finestra visuale della matrice con più campi per asse: i totali descrivono l'intero risultato, non soltanto
/// le righe/celle materializzate per la pagina corrente.</summary>
public sealed class RisultatoMatriceReport
{
    /// <summary>Chiavi delle colonne visibili (nell'ordine di visualizzazione).</summary>
    public List<string> Colonne { get; init; } = new();
    public List<RigaMatriceReport> Righe { get; init; } = new();

    /// <summary>Totale per colonna, stesso ordine/stessa cardinalità di Colonne (decisione utente 6.4.0,
    /// addendum totali). Popolato da MatricePreparata.Finestra/Completa insieme al resto del risultato.</summary>
    public List<TotaleMatriceReport> TotaliColonna { get; init; } = new();

    /// <summary>Totale generale nell'angolo della matrice: combinazioni distinte dell'INTERA matrice filtrata,
    /// indipendente dalla finestra/pagina corrente (punto 4 dell'addendum).</summary>
    public TotaleMatriceReport TotaleGenerale { get; init; } = new();

    /// <summary>Intestazioni raggruppate di colonna, per livello (indice 0 = livello esterno). Vuote per l'asse senza campi.</summary>
    public List<List<FasciaMatrice>> FasceColonne { get; init; } = new();

    /// <summary>Intestazioni raggruppate di riga, per livello (indice 0 = livello esterno). Vuote per l'asse senza campi.</summary>
    public List<List<FasciaMatrice>> FasceRighe { get; init; } = new();
}

/// <summary>Segmento di un'intestazione raggruppata: copre Span righe (o colonne) consecutive della finestra a un livello
/// dell'asse. Etichetta e Chiave vuote = segmento senza valore a quel livello (riga o colonna aggregata sopra).</summary>
public sealed record FasciaMatrice(int Livello, string Etichetta, string Chiave, int Span, bool Comprimibile, bool Compressa);

/// <summary>Finestra visuale della matrice: i totali descrivono l'intero risultato, non soltanto le
/// righe/celle materializzate per la pagina corrente. L'export continua a usare Genera().</summary>
public sealed record FinestraMatriceReport(RisultatoMatriceReport Risultato, int TotaleRighe, int TotaleColonne);

/// <summary>Combinazione funzionalita' x componente x veicolo sottostante a una cella, per il pannello di correzione del Master (fase 2).</summary>
public sealed record CombinazioneCellaMatrice(string Funzionalita, string Componente, string Veicolo, StatoCombinazioneMatrice Stato,
    bool ManualeVincente, bool ManualeSuperato, bool Propagabile);

public static partial class ReportMatriceService
{
    /// <summary>Separatore fra i livelli di un percorso di riga/colonna (valori dei campi annidati). È un carattere di
    /// controllo (Record Separator): non compare nei valori di catalogo digitati dall'utente. Con un solo campo per asse la
    /// chiave coincide col valore, quindi configurazioni e marcature N.A. esistenti restano identiche.</summary>
    public const char SepLivello = '\u001e';

    /// <summary>Prefisso dell'identificativo dei valori manuali del Master (6.6.1, fase 2): '~' ordina dopo le lettere, quindi a pari data il manuale vince sul Test.</summary>
    public const string ManualePrefisso = "~manuale|";

    /// <summary>Percorso leggibile di una chiave di riga/colonna, es. "Daily › BCM" (vuoto per l'asse senza campi).</summary>
    public static string EtichettaPercorso(string chiave) =>
        chiave.Length == 0 ? "" : string.Join(" › ", chiave.Split(SepLivello));


    public static string ChiaveEtichettaModalita(ModalitaCellaMatrice modalita) => modalita switch
    {
        ModalitaCellaMatrice.Stato => "MatriceColonnaStato",
        ModalitaCellaMatrice.TestPercentuale => "MatriceColonnaTestPercentuale",
        ModalitaCellaMatrice.TempoTotale => "MatriceColonnaTempoTotale",
        ModalitaCellaMatrice.TempoMedio => "MatriceColonnaTempoMedio",
        _ => "MatriceColonnaPercentuale",
    };

    /// <summary>Chiave della vista per un gruppo compresso: asse (Y = righe, X = colonne) più la chiave del nodo.</summary>
    public static string ChiaveCompressione(bool righe, string chiaveNodo) => (righe ? "Y:" : "X:") + chiaveNodo;

    internal static string[] SpezzaPercorso(string chiave) => chiave.Length == 0 ? Array.Empty<string>() : chiave.Split(SepLivello);

    /// <summary>Prefissi di un percorso: indice d = primi d valori (0 = radice ""). Con 0 livelli: solo la radice.</summary>
    private static string[] PrefissiPercorso(string chiave, int livelli)
    {
        var prefissi = new string[livelli + 1];
        prefissi[0] = "";
        if (livelli == 0) return prefissi;
        var parti = chiave.Split(SepLivello);
        var corrente = "";
        for (var d = 0; d < livelli; d++)
        {
            var parte = d < parti.Length ? parti[d] : "";
            corrente = d == 0 ? parte : corrente + SepLivello + parte;
            prefissi[d + 1] = corrente;
        }
        return prefissi;
    }

    /// <summary>Ordine personale di una dimensione (§11.3b): vale per ogni livello che usa quella dimensione.</summary>
    private static IReadOnlyList<string>? OrdineDi(ConfigurazioneMatriceReport config, string dimensione)
    {
        if (config.Ordini.TryGetValue(dimensione, out var ordine)) return ordine;
        if (config.CampiX.Count > 0 && string.Equals(config.CampiX[0], dimensione, StringComparison.OrdinalIgnoreCase)) return config.OrdineAsseX;
        if (config.CampiY.Count > 0 && string.Equals(config.CampiY[0], dimensione, StringComparison.OrdinalIgnoreCase)) return config.OrdineAsseY;
        return null;
    }

    /// <summary>Confronto delle coppie (riga, colonna) senza distinzione di maiuscole, come i dizionari della matrice.</summary>
    internal sealed class ConfrontoCoppiaMatrice : IEqualityComparer<(string Y, string X)>
    {
        public static readonly ConfrontoCoppiaMatrice Istanza = new();

        public bool Equals((string Y, string X) a, (string Y, string X) b) =>
            StringComparer.OrdinalIgnoreCase.Equals(a.Y, b.Y) && StringComparer.OrdinalIgnoreCase.Equals(a.X, b.X);

        public int GetHashCode((string Y, string X) c) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(c.Y), StringComparer.OrdinalIgnoreCase.GetHashCode(c.X));
    }

    /// <summary>Nodo di un asse annidato: la chiave è il percorso dei valori dal livello 1 fino al nodo. Le foglie (profondità
    /// = numero di campi) sono le combinazioni complete. Inizio/Fine = intervallo delle foglie del nodo nell'ordine di
    /// visualizzazione (le foglie di un nodo sono contigue).</summary>
    internal sealed class NodoAsse
    {
        public NodoAsse(string chiave, int profondita, string etichetta, int inizio)
        {
            Chiave = chiave;
            Profondita = profondita;
            Etichetta = etichetta;
            Inizio = inizio;
            Fine = inizio + 1;
        }

        public string Chiave { get; }
        public int Profondita { get; }
        public string Etichetta { get; }
        public List<string> Figli { get; } = new();
        public int Inizio { get; }
        public int Fine { get; set; }
    }

    /// <summary>Voce visibile di un asse (riga o colonna): una foglia, oppure un nodo compresso che rappresenta tutte le
    /// sue foglie. Aggregata = i conteggi sono per combinazione distinta (gruppo compresso, o asse senza campi).</summary>
    internal sealed record VoceAsseMatrice(string Chiave, int Profondita, bool Aggregata, bool Compressa, int Inizio, int Fine);

    /// <summary>Struttura ordinata di un asse della matrice (0..3 campi annidati). Con 0 o 1 campo non ci sono nodi: le foglie
    /// sono la lista piatta. Con più campi le foglie sono ordinate per livello (ordine personale per dimensione, poi
    /// alfabetico) e raggruppate in nodi, così ogni gruppo è un intervallo contiguo di foglie.</summary>
    internal sealed class StrutturaAsse
    {
        private readonly Dictionary<string, NodoAsse>? _nodi;
        private readonly List<string> _radice;

        private StrutturaAsse(int livelli, List<string> foglie, Dictionary<string, NodoAsse>? nodi, List<string> radice)
        {
            Livelli = livelli;
            Foglie = foglie;
            _nodi = nodi;
            _radice = radice;
        }

        public int Livelli { get; }

        /// <summary>Chiavi delle foglie nell'ordine di visualizzazione. Asse senza campi: una sola foglia "" (radice).</summary>
        public List<string> Foglie { get; }

        public static StrutturaAsse Costruisci(IEnumerable<string> chiaviFoglia, IReadOnlyList<string> dimensioni,
            Func<string, IReadOnlyList<string>?> ordineDi)
        {
            var livelli = dimensioni.Count;
            if (livelli == 0) return new StrutturaAsse(0, new List<string> { "" }, null, new List<string>());

            var foglie = chiaviFoglia.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (livelli == 1)
                return new StrutturaAsse(1, OrdineAssiReportService.Ordina(foglie, ordineDi(dimensioni[0])), null, new List<string>());

            // Rango di ogni valore per livello: stesso ordine di OrdineAssiReportService (salvato per dimensione, poi alfabetico).
            var percorsi = foglie.Select(SpezzaPercorso).ToList();
            var ranghi = new Dictionary<string, int>[livelli];
            for (var d = 0; d < livelli; d++)
            {
                var valori = percorsi.Where(p => p.Length > d).Select(p => p[d]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var ordinati = OrdineAssiReportService.Ordina(valori, ordineDi(dimensioni[d]));
                ranghi[d] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < ordinati.Count; i++) ranghi[d].TryAdd(ordinati[i], i);
            }
            int Rango(int foglia, int d) => percorsi[foglia].Length > d && ranghi[d].TryGetValue(percorsi[foglia][d], out var r) ? r : int.MaxValue;
            var indici = Enumerable.Range(0, foglie.Count).ToArray();
            Array.Sort(indici, (a, b) =>
            {
                for (var d = 0; d < livelli; d++)
                {
                    var confronto = Rango(a, d).CompareTo(Rango(b, d));
                    if (confronto != 0) return confronto;
                }
                return a.CompareTo(b);
            });

            var nodi = new Dictionary<string, NodoAsse>(StringComparer.OrdinalIgnoreCase);
            var radice = new List<string>();
            var ordinate = new List<string>(foglie.Count);
            foreach (var indice in indici)
            {
                var percorso = percorsi[indice];
                var posizione = ordinate.Count;
                ordinate.Add(foglie[indice]);
                string? padre = null;
                var prefisso = "";
                for (var d = 0; d < livelli; d++)
                {
                    var parte = d < percorso.Length ? percorso[d] : "";
                    prefisso = d == 0 ? parte : prefisso + SepLivello + parte;
                    if (!nodi.TryGetValue(prefisso, out var nodo))
                    {
                        nodo = new NodoAsse(prefisso, d + 1, parte, posizione);
                        nodi[prefisso] = nodo;
                        if (padre == null) radice.Add(prefisso);
                        else nodi[padre].Figli.Add(prefisso);
                    }
                    nodo.Fine = posizione + 1;
                    padre = prefisso;
                }
            }
            return new StrutturaAsse(livelli, ordinate, nodi, radice);
        }

        /// <summary>Voce della foglia i-esima per un asse con 0 o 1 campo (nessun gruppo).</summary>
        public VoceAsseMatrice VoceFoglia(int indice) => Livelli == 0
            ? new VoceAsseMatrice(Foglie[indice], 0, true, false, 0, 1)
            : new VoceAsseMatrice(Foglie[indice], 1, false, false, indice, indice + 1);

        /// <summary>Voci visibili dell'asse: foglie dei gruppi espansi e nodi aggregati dei gruppi compressi, in ordine.
        /// <paramref name="compresse"/> contiene le chiavi di <see cref="ReportMatriceService.ChiaveCompressione"/>.</summary>
        public List<VoceAsseMatrice> Elenca(IReadOnlySet<string>? compresse, string prefisso)
        {
            var voci = new List<VoceAsseMatrice>();
            if (Livelli <= 1)
            {
                for (var i = 0; i < Foglie.Count; i++) voci.Add(VoceFoglia(i));
                return voci;
            }
            foreach (var radice in _radice) Visita(radice, voci, compresse, prefisso);
            return voci;
        }

        private void Visita(string chiave, List<VoceAsseMatrice> voci, IReadOnlySet<string>? compresse, string prefisso)
        {
            var nodo = _nodi![chiave];
            if (nodo.Profondita == Livelli)
            {
                voci.Add(new VoceAsseMatrice(chiave, nodo.Profondita, false, false, nodo.Inizio, nodo.Fine));
                return;
            }
            if (compresse != null && compresse.Contains(prefisso + chiave))
            {
                voci.Add(new VoceAsseMatrice(chiave, nodo.Profondita, true, true, nodo.Inizio, nodo.Fine));
                return;
            }
            foreach (var figlio in nodo.Figli) Visita(figlio, voci, compresse, prefisso);
        }

        /// <summary>Finestra su un asse: solo le voci della pagina richiesta (con 0/1 campo non si materializza l'intero asse).</summary>
        public (List<VoceAsseMatrice> Voci, int Totale) Finestra(IReadOnlySet<string>? compresse, string prefisso, int primo, int numero)
        {
            if (Livelli <= 1)
            {
                var totale = Foglie.Count;
                var voci = new List<VoceAsseMatrice>(Math.Max(0, Math.Min(numero, totale - primo)));
                for (var i = primo; i < totale && voci.Count < numero; i++) voci.Add(VoceFoglia(i));
                return (voci, totale);
            }
            var tutte = Elenca(compresse, prefisso);
            return (tutte.Skip(primo).Take(numero).ToList(), tutte.Count);
        }

        /// <summary>Intestazioni raggruppate: per ogni livello, segmenti di voci consecutive con lo stesso valore a quel livello.
        /// Una voce aggregata più in alto del livello lascia un segmento vuoto.</summary>
        public static List<List<FasciaMatrice>> CostruisciFasce(IReadOnlyList<VoceAsseMatrice> voci, int livelli)
        {
            var fasce = new List<List<FasciaMatrice>>(livelli);
            for (var livello = 1; livello <= livelli; livello++)
            {
                var segmenti = new List<FasciaMatrice>();
                foreach (var voce in voci)
                {
                    var vuoto = voce.Profondita < livello;
                    var parti = vuoto ? Array.Empty<string>() : voce.Chiave.Split(SepLivello);
                    var chiave = vuoto ? "" : string.Join(SepLivello, parti.Take(livello));
                    var etichetta = vuoto || parti.Length < livello ? "" : parti[livello - 1];
                    var comprimibile = !vuoto && livello < livelli;
                    var compressa = voce.Compressa && voce.Profondita == livello;
                    if (segmenti.Count > 0 && !compressa && !segmenti[^1].Compressa && segmenti[^1].Chiave == chiave)
                        segmenti[^1] = segmenti[^1] with { Span = segmenti[^1].Span + 1 };
                    else
                        segmenti.Add(new FasciaMatrice(livello, etichetta, chiave, 1, comprimibile, compressa));
                }
                fasce.Add(segmenti);
            }
            return fasce;
        }
    }

    /// <summary>
    /// Decisione utente 6.4.0 ("Matrice dei report: stato delle funzionalità, non conteggio delle
    /// operazioni"), corretta in review indipendente 2026-09-25: l'unità della matrice NON è più
    /// un'operazione dell'ultima revisione di un Test, ma la combinazione funzionalità × componente × veicolo
    /// (PIÙ i valori degli assi X/Y configurati che non sono già parte di quell'identità — vedi
    /// DimensioneAggiuntiva/CostruisciCombinazioni), contata UNA sola volta con il suo esito più recente fra
    /// TUTTE le revisioni di TUTTI i Test che vi corrispondono. Così la stessa combinazione fallita in un
    /// Test e poi riuscita in un altro pesa 1 (conforme), mai 2 (1 conforme + 1 non conforme).
    ///
    /// Un candidato viene generato per ogni (funzionalità dichiarata dall'operazione o dal Test) ×
    /// (componente secondo OperationalSemantics.EcuUnits, "" se ambigua/assente) × (veicolo di
    /// TestCase.VeicoliEffettivi, "" se assente) di ogni Condizione EFFETTIVA (CondizioniEffettive: solo
    /// l'ultimo tentativo di ogni operazione ripetuta nella stessa revisione) di ogni Esecuzione di ogni
    /// Test — un Test multi-veicolo genera un candidato per ciascun veicolo. Una Condizione non ancora
    /// eseguita (DaEseguire/N.A.) genera comunque un candidato (la combinazione esiste e conta nei Totali)
    /// ma non concorre mai allo stato (Valutati/Conformi): "non valutata" finché nessuna combinazione
    /// equivalente riceve un esito Esito.EEseguito().
    /// </summary>
    internal sealed record CandidatoCombinazione(
        string? Funzionalita, string Componente, string Veicolo, Esito? Esito, bool DeviazioneAccettata,
        TestCase Test, DateTime Data, int RevisioneIndice, int TentativoIndice)
    {
        /// <summary>Durata manuale dell'operazione (TIME): null = non nota. Vale solo per i candidati da una Condizione reale.</summary>
        public int? DurataMinuti { get; init; }
        /// <summary>Ordinale stabile del candidato generato dal Test; consente di ricostruire la stessa riga in una seconda passata, anche con duplicati.</summary>
        public int IndiceCandidato { get; init; }

        /// <summary>Uid della Condizione: identita' del tentativo per la deduplica dei tempi (un tentativo = una Condizione).</summary>
        public Guid CondizioneUid { get; init; }

        /// <summary>TIME "Ultimo stato": vero se il tentativo e' nell'ultima revisione in cui compare il suo Uid (copia superata = falso).
        /// Non cambia i candidati degli altri usi: il valore di default e' vero e lo passa solo il calcolo temporale.</summary>
        public bool UltimaOccorrenza { get; init; } = true;
    }


    internal sealed class EntitaTestPercentuale(StatoCombinazioneMatrice stato)
    {
        public StatoCombinazioneMatrice Stato { get; } = stato;
        public HashSet<string> Celle { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TestIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<Guid> TestUids { get; } = new();
    }

    /// <summary>Dimensioni già interamente determinate dalla tripla funzionalità/componente/veicolo della
    /// combinazione, senza bisogno di moltiplicarla per i loro valori: Funzionalita/Componente/Sottoveicolo SONO
    /// quei campi, Gamma è una funzione 1:1 del veicolo (un Sottoveicolo appartiene a una sola Gamma). Esito
    /// non è "core" nello stesso senso (è l'OUTPUT della riduzione, non un ingresso) ma non va comunque
    /// aggiunto alla chiave: si calcola sempre dopo, da acc.EsitoFinale.</summary>
    private static readonly HashSet<string> DimensioniCoreCombinazione = new(StringComparer.OrdinalIgnoreCase)
    {
        DimensioniMatrice.Funzionalita, DimensioniMatrice.Componente, DimensioniMatrice.Sottoveicolo, DimensioniMatrice.Gamma
    };

    /// <summary>Review indipendente 2026-09-25 (un asse non-core entra nella CHIAVE della combinazione), esteso in 6.6.1 a
    /// TUTTI i campi dei due assi (fino a tre per asse): ogni dimensione distinta dalle dimensioni core entra
    /// nell'identità. Esito è escluso: è l'output della riduzione, mai un ingresso della chiave.</summary>
    private static List<string> DimensioniAggiuntive(ConfigurazioneMatriceReport config)
    {
        var extra = new List<string>();
        void Aggiungi(string dimensione)
        {
            if (dimensione != DimensioniMatrice.Esito && !DimensioniCoreCombinazione.Contains(dimensione)
                && !extra.Contains(dimensione, StringComparer.OrdinalIgnoreCase))
                extra.Add(dimensione);
        }
        foreach (var dimensione in config.CampiX) Aggiungi(dimensione);
        foreach (var dimensione in config.CampiY) Aggiungi(dimensione);
        return extra;
    }

    /// <summary>
    /// Stato accumulato di UNA combinazione (funzionalità×componente×veicolo più i valori degli assi aggiuntivi,
    /// FISSI: la combinazione è la chiave, quindi tutti i candidati che vi ricadono condividono già questi
    /// valori — nessuno "spareggio" serve qui, a differenza dello stato). Lo stato (Eseguita/EsitoFinale/
    /// DeviazioneAccettataFinale) è invece il risultato di una riduzione: solo il candidato REALMENTE
    /// ESEGUITO (Esito.EEseguito()) più recente vince — "non valutata" finché non ne esiste alcuno. Ordine
    /// totale deterministico e di dominio (mai un Guid casuale, review indipendente 2026-09-25): Data della
    /// revisione, poi Id del Test (stringa, ordinale), poi indice di revisione, poi indice di tentativo
    /// entro la revisione ("ordine di revisione/tentativo" per la parità sulla stessa Data).
    /// Modalità Stato (6.6.1): Da eseguire e N.A. non determinano lo stato. HaPrevisto segnala solo che esiste almeno
    /// un'operazione prevista e non eseguita: serve come stato solo quando NON esiste alcun esito eseguito.
    /// </summary>
    internal sealed class CombinazioneAccumulatore
    {
        public readonly string? Funzionalita;
        public readonly string Componente;
        public readonly string Veicolo;
        public readonly IReadOnlyDictionary<string, string> ValoriEstra;

        public bool Eseguita { get; private set; }
        public Esito EsitoFinale { get; private set; }
        public bool DeviazioneAccettataFinale { get; private set; }

        /// <summary>Almeno un'operazione prevista e non eseguita (Da eseguire, oppure senza esito). Vale come stato
        /// "Da eseguire" solo se la combinazione non ha alcun esito eseguito.</summary>
        public bool HaPrevisto { get; private set; }

        /// <summary>Combinazione del catalogo senza Test sul veicolo, ma con un esito eseguito su un altro veicolo della stessa componente
        /// (regola di propagabilita' dell'Audit): mostra "!" finche' un Master non decide (6.6.1, fase 2).</summary>
        public bool Propagabile { get; set; }

        /// <summary>Esiste almeno un valore manuale del Master eseguito per questa combinazione (vedi ManualePrefisso).</summary>
        public bool ManualeEsiste { get; private set; }

        /// <summary>Ultimo valore manuale noto, anche se superato da un Test piu' recente, per mostrarne i dettagli nel tooltip.</summary>
        public CandidatoCombinazione? ValoreManuale { get; private set; }

        /// <summary>Il valore manuale e' l'esito che determina lo stato (matita).</summary>
        public bool ManualeVincente => Eseguita && _vincenteManuale;

        /// <summary>Esiste un valore manuale, ma un esito di Test piu' recente lo ha superato ("superato").</summary>
        public bool ManualeSuperato => Eseguita && ManualeEsiste && !ManualeVincente;

        /// <summary>Nota del valore manuale che determina lo stato: commento della cella nell'Excel e dettaglio nel pannello.</summary>
        public string? NotaManuale => ManualeVincente ? Vincente?.Test.Descrizione : null;

        /// <summary>Candidato vincente (solo se Eseguita), per riferimento: serve al foglio "Origine" dell'export
        /// per marcare quale dato determina lo stato — nessun effetto sul calcolo.</summary>
        public CandidatoCombinazione? Vincente { get; private set; }

        /// <summary>Test da cui derivare gli assi descrittivi della combinazione corrente. Una decisione manuale
        /// cambia lo stato ma non sostituisce i metadati del Test; in assenza di un Test eseguito si usa l'ultimo
        /// Test pianificato.</summary>
        public CandidatoCombinazione? CandidatoTestRappresentante => CandidatoTestEseguito ?? CandidatoTestPrevisto;
        private CandidatoCombinazione? CandidatoTestEseguito { get; set; }
        private CandidatoCombinazione? CandidatoTestPrevisto { get; set; }
        private DateTime _dataTestEseguito;
        private string _idTestEseguito = "";
        private int _revisioneTestEseguito;
        private int _tentativoTestEseguito;
        private DateTime _dataTestPrevisto;
        private string _idTestPrevisto = "";
        private int _revisioneTestPrevisto;
        private int _tentativoTestPrevisto;

        private bool _haMiglioreEseguito;
        private bool _vincenteManuale;
        private DateTime _dataEseguita;
        private string _testEseguito = "";
        private int _revisioneEseguita;
        private int _tentativoEseguito;

        /// <summary>Accumulatore senza candidati: combinazione del catalogo mai presente nei Test (stato "Non testato").</summary>
        public CombinazioneAccumulatore(string? funzionalita, string componente, string veicolo, IReadOnlyDictionary<string, string> valoriEstra)
        {
            Funzionalita = funzionalita;
            Componente = componente;
            Veicolo = veicolo;
            ValoriEstra = valoriEstra;
        }

        public CombinazioneAccumulatore(CandidatoCombinazione primo, IReadOnlyDictionary<string, string> valoriEstra)
            : this(primo.Funzionalita, primo.Componente, primo.Veicolo, valoriEstra)
        {
            Aggiorna(primo);
        }

        public CombinazioneAccumulatore(CombinazioneAccumulatore sorgente, IReadOnlyDictionary<string, string> valoriEstra)
            : this(sorgente.Funzionalita, sorgente.Componente, sorgente.Veicolo, valoriEstra)
        {
            Eseguita = sorgente.Eseguita;
            EsitoFinale = sorgente.EsitoFinale;
            DeviazioneAccettataFinale = sorgente.DeviazioneAccettataFinale;
            HaPrevisto = sorgente.HaPrevisto;
            Propagabile = sorgente.Propagabile;
            ManualeEsiste = sorgente.ManualeEsiste;
            ValoreManuale = sorgente.ValoreManuale;
            Vincente = sorgente.Vincente;
            CandidatoTestEseguito = sorgente.CandidatoTestEseguito;
            CandidatoTestPrevisto = sorgente.CandidatoTestPrevisto;
            _haMiglioreEseguito = sorgente._haMiglioreEseguito;
            _vincenteManuale = sorgente._vincenteManuale;
            _dataEseguita = sorgente._dataEseguita;
            _testEseguito = sorgente._testEseguito;
            _revisioneEseguita = sorgente._revisioneEseguita;
            _tentativoEseguito = sorgente._tentativoEseguito;
            _dataTestEseguito = sorgente._dataTestEseguito;
            _idTestEseguito = sorgente._idTestEseguito;
            _revisioneTestEseguito = sorgente._revisioneTestEseguito;
            _tentativoTestEseguito = sorgente._tentativoTestEseguito;
            _dataTestPrevisto = sorgente._dataTestPrevisto;
            _idTestPrevisto = sorgente._idTestPrevisto;
            _revisioneTestPrevisto = sorgente._revisioneTestPrevisto;
            _tentativoTestPrevisto = sorgente._tentativoTestPrevisto;
        }

        public void Aggiorna(CandidatoCombinazione c)
        {
            if (!c.Esito.HasValue || c.Esito.Value == Esito.DaEseguire) HaPrevisto = true;
            var manuale = c.Test.Id.StartsWith(ManualePrefisso, StringComparison.Ordinal);
            if (!manuale)
            {
                if (c.Esito.HasValue && c.Esito.Value.EEseguito())
                {
                    if (CandidatoTestEseguito == null || Successivo(c.Data, c.Test.Id, c.RevisioneIndice, c.TentativoIndice,
                            _dataTestEseguito, _idTestEseguito, _revisioneTestEseguito, _tentativoTestEseguito))
                    {
                        CandidatoTestEseguito = c;
                        _dataTestEseguito = c.Data; _idTestEseguito = c.Test.Id;
                        _revisioneTestEseguito = c.RevisioneIndice; _tentativoTestEseguito = c.TentativoIndice;
                    }
                }
                else if ((!c.Esito.HasValue || c.Esito.Value == Esito.DaEseguire)
                         && (CandidatoTestPrevisto == null || Successivo(c.Data, c.Test.Id, c.RevisioneIndice, c.TentativoIndice,
                             _dataTestPrevisto, _idTestPrevisto, _revisioneTestPrevisto, _tentativoTestPrevisto)))
                {
                    CandidatoTestPrevisto = c;
                    _dataTestPrevisto = c.Data; _idTestPrevisto = c.Test.Id;
                    _revisioneTestPrevisto = c.RevisioneIndice; _tentativoTestPrevisto = c.TentativoIndice;
                }
            }
            if (!c.Esito.HasValue || !c.Esito.Value.EEseguito()) return;
            if (manuale)
            {
                ManualeEsiste = true;
                if (ValoreManuale == null || Successivo(c.Data, c.Test.Id, c.RevisioneIndice, c.TentativoIndice,
                        ValoreManuale.Data, ValoreManuale.Test.Id, ValoreManuale.RevisioneIndice, ValoreManuale.TentativoIndice))
                    ValoreManuale = c;
            }
            if (_haMiglioreEseguito && !Successivo(c.Data, c.Test.Id, c.RevisioneIndice, c.TentativoIndice,
                    _dataEseguita, _testEseguito, _revisioneEseguita, _tentativoEseguito)) return;

            _haMiglioreEseguito = true;
            Vincente = c;
            _vincenteManuale = c.Test.Id.StartsWith(ManualePrefisso, StringComparison.Ordinal);
            _dataEseguita = c.Data; _testEseguito = c.Test.Id; _revisioneEseguita = c.RevisioneIndice; _tentativoEseguito = c.TentativoIndice;
            Eseguita = true;
            EsitoFinale = c.Esito.Value;
            DeviazioneAccettataFinale = c.DeviazioneAccettata;
        }

        /// <summary>Ordine totale deterministico fra due candidati: Data (la revisione più recente vince),
        /// poi Id del Test (stringa, confronto ordinale — mai un Guid: due esecuzioni sullo stesso Test/
        /// revisione/tentativo non possono avere Id diversi, quindi il confronto è sempre significativo e
        /// riproducibile fra run diverse), poi indice di revisione, poi indice di tentativo entro la
        /// revisione — "ordine di revisione/tentativo" richiesto per la parità sulla stessa Data.</summary>
        internal static bool Successivo(DateTime data, string testId, int revisione, int tentativo,
            DateTime dataAttuale, string testIdAttuale, int revisioneAttuale, int tentativoAttuale)
        {
            if (data != dataAttuale) return data > dataAttuale;
            var confrontoTest = string.CompareOrdinal(testId, testIdAttuale);
            if (confrontoTest != 0) return confrontoTest > 0;
            if (revisione != revisioneAttuale) return revisione > revisioneAttuale;
            return tentativo > tentativoAttuale;
        }
    }

    /// <summary>Stato di una combinazione per la modalità Stato. L'esito eseguito più recente determina lo stato; senza esito
    /// eseguito conta solo la previsione (Da eseguire); N.A. o solo catalogo = Non testato.</summary>
    private static StatoCombinazioneMatrice StatoDi(CombinazioneAccumulatore acc) => acc.Eseguita
        ? acc.EsitoFinale switch
        {
            Esito.NonConforme => StatoCombinazioneMatrice.NonConforme,
            Esito.ConformeConDeviazioni => StatoCombinazioneMatrice.ConformeConDeviazioni,
            _ => StatoCombinazioneMatrice.Conforme,
        }
        : acc.HaPrevisto ? StatoCombinazioneMatrice.DaEseguire
        : acc.Propagabile ? StatoCombinazioneMatrice.DaDecidere
        : StatoCombinazioneMatrice.NonTestato;

    public static string EtichettaStatoDisponibile(StatoCombinazioneMatrice stato) => stato switch
    {
        StatoCombinazioneMatrice.Conforme => Esito.Conforme.Label(),
        StatoCombinazioneMatrice.NonConforme => Esito.NonConforme.Label(),
        StatoCombinazioneMatrice.ConformeConDeviazioni => Esito.ConformeConDeviazioni.Label(),
        StatoCombinazioneMatrice.DaEseguire => Esito.DaEseguire.Label(),
        StatoCombinazioneMatrice.DaDecidere => new StatiMatrice(0, 0, 0, 0, 0, 1).Testo,
        _ => new StatiMatrice(0, 0, 0, 0, 1, 0).Testo,
    };

    private static bool DimensioneIdentitaCore(string dimensione) =>
        dimensione is DimensioniMatrice.Funzionalita or DimensioniMatrice.Componente
            or DimensioniMatrice.Sottoveicolo or DimensioniMatrice.Gamma;

    private static (Dictionary<string, CombinazioneAccumulatore> Estese, Dictionary<string, CombinazioneAccumulatore> Core)
        ProiettaStatoCorrente(
            Dictionary<string, CombinazioneAccumulatore> esteseCatalogo,
            Dictionary<string, CombinazioneAccumulatore> coreRidotte,
            ConfigurazioneMatriceReport config,
            IReadOnlyDictionary<string, string> gammaDi,
            IReadOnlyDictionary<string, Funzionalita> funzioneDi,
            IReadOnlyDictionary<Guid, TestTemplate> templateDi)
    {
        var filtriCorrenti = config.Filtri
            .Where(f => f.Key != DimensioniMatrice.Esito)
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        var filtroEsito = FiltroEsitoStatoCorrente(config);
        var estra = DimensioniAggiuntive(config);
        var esteseCatalogoPerCore = new Dictionary<string, List<CombinazioneAccumulatore>>(StringComparer.OrdinalIgnoreCase);
        foreach (var estesa in esteseCatalogo.Values)
        {
            var chiaveCore = ChiaveCore(estesa.Funzionalita, estesa.Componente, estesa.Veicolo);
            if (!esteseCatalogoPerCore.TryGetValue(chiaveCore, out var lista))
                esteseCatalogoPerCore[chiaveCore] = lista = [];
            lista.Add(estesa);
        }

        var core = new Dictionary<string, CombinazioneAccumulatore>(StringComparer.OrdinalIgnoreCase);
        var estese = new Dictionary<string, CombinazioneAccumulatore>(StringComparer.OrdinalIgnoreCase);
        foreach (var (chiaveCore, acc) in coreRidotte)
        {
            if (!PassaFiltroEsitoCorrente(acc, filtroEsito)) continue;

            var rappresentante = acc.CandidatoTestRappresentante;
            var candidatoFiltro = rappresentante ?? acc.Vincente;
            if (candidatoFiltro != null && !PassaFiltri(candidatoFiltro, filtriCorrenti, gammaDi, funzioneDi, templateDi))
                continue;

            core[chiaveCore] = acc;
            IEnumerable<Dictionary<string, string>> proiezioni;
            if (rappresentante != null)
            {
                proiezioni = EspandiEstra(rappresentante, estra, gammaDi, funzioneDi, templateDi);
            }
            else if (esteseCatalogoPerCore.TryGetValue(chiaveCore, out var catalogo))
            {
                proiezioni = catalogo.Select(c => new Dictionary<string, string>(c.ValoriEstra, StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                proiezioni = EspandiEstraSenzaTest(acc, estra, funzioneDi);
            }

            foreach (var valori in proiezioni)
            {
                if (estra.Contains(DimensioniMatrice.Esito, StringComparer.OrdinalIgnoreCase))
                    valori[DimensioniMatrice.Esito] = EsitoVisualizzato(acc);
                var chiave = ChiaveCombinazione(acc.Funzionalita, acc.Componente, acc.Veicolo, valori);
                estese[chiave] = new CombinazioneAccumulatore(acc, valori);
            }
        }

        return (estese, core);
    }

    private static List<Dictionary<string, string>> EspandiEstraSenzaTest(
        CombinazioneAccumulatore acc,
        List<string> estra,
        IReadOnlyDictionary<string, Funzionalita> funzioneDi)
    {
        var risultato = new List<Dictionary<string, string>> { new(StringComparer.OrdinalIgnoreCase) };
        foreach (var dimensione in estra)
        {
            IEnumerable<string> disponibili = dimensione switch
            {
                DimensioniMatrice.Esito => [EsitoVisualizzato(acc)],
                DimensioniMatrice.CategoriaFunzionalita
                    when acc.Funzionalita != null && funzioneDi.TryGetValue(acc.Funzionalita, out var funzione) => funzione.Categorie,
                _ => Array.Empty<string>(),
            };
            var valori = disponibili.DefaultIfEmpty("").ToList();
            var esteso = new List<Dictionary<string, string>>(risultato.Count * valori.Count);
            foreach (var basePronta in risultato)
            foreach (var valore in valori)
                esteso.Add(new Dictionary<string, string>(basePronta, StringComparer.OrdinalIgnoreCase) { [dimensione] = valore });
            risultato = esteso;
        }
        return risultato;
    }

    private static IReadOnlyList<string>? FiltroEsitoStatoCorrente(ConfigurazioneMatriceReport config) =>
        config.ModalitaCella != ModalitaCellaMatrice.TestPercentuale
        && config.Filtri.TryGetValue(DimensioniMatrice.Esito, out var selezionati)
        && selezionati.Count > 0 ? selezionati : null;

    private static bool PassaFiltroEsitoCorrente(CombinazioneAccumulatore acc, IReadOnlyList<string>? esiti) =>
        esiti == null || esiti.Contains(EsitoVisualizzato(acc), StringComparer.OrdinalIgnoreCase);

    /// <summary>Valore mostrato sull'asse Esito dopo la riduzione. Le operazioni previste e le
    /// combinazioni mai testate sono entrambe "Non testato".</summary>
    private static string EsitoVisualizzato(CombinazioneAccumulatore acc) =>
        acc.Eseguita ? acc.EsitoFinale.Label() : "Non testato";


    private static StatoCombinazioneMatrice StatoTestCase(TestCase testCase) => testCase.StatoCorrente switch
    {
        Esito.Conforme => StatoCombinazioneMatrice.Conforme,
        Esito.NonConforme => StatoCombinazioneMatrice.NonConforme,
        Esito.ConformeConDeviazioni => StatoCombinazioneMatrice.ConformeConDeviazioni,
        Esito.DaRivedere => StatoCombinazioneMatrice.DaDecidere,
        Esito.NonApplicabile or Esito.NonEseguitoNA => StatoCombinazioneMatrice.NonTestato,
        _ => StatoCombinazioneMatrice.DaEseguire,
    };

    /// <summary>"Conforme" nei conteggi delle percentuali: Conforme, oppure Conforme con deviazioni accettate.</summary>
    private static bool ConformeCombinazione(CombinazioneAccumulatore acc) =>
        acc.Eseguita && (acc.EsitoFinale == Esito.Conforme || acc.EsitoFinale == Esito.ConformeConDeviazioni && acc.DeviazioneAccettataFinale);

    /// <summary>Valore di UNA combinazione su una dimensione di asse (null = nessun valore). Stessa semantica di Valori(),
    /// senza allocare una collezione per ogni combinazione.</summary>
    private static string? ValoreAsse(CombinazioneAccumulatore acc, string dimensione, IReadOnlyDictionary<string, string> gammaDi)
    {
        if (dimensione == DimensioniMatrice.Gamma)
            return string.IsNullOrEmpty(acc.Veicolo) || !gammaDi.TryGetValue(acc.Veicolo, out var g) || string.IsNullOrWhiteSpace(g) ? null : g;
        if (dimensione == DimensioniMatrice.Sottoveicolo) return string.IsNullOrEmpty(acc.Veicolo) ? null : acc.Veicolo;
        if (dimensione == DimensioniMatrice.Componente) return string.IsNullOrEmpty(acc.Componente) ? null : acc.Componente;
        if (dimensione == DimensioniMatrice.Funzionalita) return string.IsNullOrWhiteSpace(acc.Funzionalita) ? null : acc.Funzionalita;
        if (dimensione == DimensioniMatrice.Esito) return EsitoVisualizzato(acc);
        return acc.ValoriEstra.TryGetValue(dimensione, out var valore) && !string.IsNullOrEmpty(valore) ? valore : null;
    }

    /// <summary>Chiave di riga/colonna di una combinazione: valori dei campi dell'asse annidati; un campo mancante usa
    /// l'etichetta "senza valore" di quella dimensione. Asse senza campi: chiave vuota (colonna/riga unica).</summary>
    private static string ChiaveAsse(CombinazioneAccumulatore acc, IReadOnlyList<string> dimensioni, IReadOnlyDictionary<string, string> gammaDi)
    {
        if (dimensioni.Count == 0) return "";
        if (dimensioni.Count == 1) return ValoreAsse(acc, dimensioni[0], gammaDi) ?? EtichettaValoreVuoto(dimensioni[0]);
        var parti = new string[dimensioni.Count];
        for (var i = 0; i < parti.Length; i++) parti[i] = ValoreAsse(acc, dimensioni[i], gammaDi) ?? EtichettaValoreVuoto(dimensioni[i]);
        return string.Join(SepLivello, parti);
    }


    private static IReadOnlyList<string> ChiaviAsseCandidato(CandidatoCombinazione candidato,
        IReadOnlyList<string> dimensioni, IReadOnlyDictionary<string, string> gammaDi,
        IReadOnlyDictionary<string, Funzionalita> funzioneDi, IReadOnlyDictionary<Guid, TestTemplate> templateDi)
    {
        var chiavi = new List<string> { "" };
        foreach (var dimensione in dimensioni)
        {
            var valori = ValoriCandidato(candidato, dimensione, gammaDi, funzioneDi, templateDi)
                .Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (valori.Length == 0) valori = [EtichettaValoreVuoto(dimensione)];
            chiavi = chiavi.SelectMany(prefisso => valori,
                (prefisso, valore) => prefisso.Length == 0 ? valore : string.Concat(prefisso, SepLivello, valore)).ToList();
        }
        return chiavi;
    }

    /// <summary>Universo "Non testato" (6.6.1, modalità Stato e Stato %): le combinazioni del catalogo — componente associata al veicolo
    /// (Componente.VeicoliAssociati) e funzionalità assegnata a quella componente (TutteLeEcu/EcuAssociate) — che non compaiono in alcun Test.
    /// Per le dimensioni che non sono di catalogo si mostrano solo le combinazioni presenti nei dati: vedi UniversoAttivo.
    /// Entra nel totale di Stato %, ma non nella base Test %.</summary>
    private static bool UniversoNonTestatoAttivo(ConfigurazioneMatriceReport config, IReadOnlyList<Componente>? ecuCatalogo) =>
        ecuCatalogo != null && config.ModalitaCella != ModalitaCellaMatrice.TestPercentuale
        // Esito come asse: le combinazioni del catalogo senza Test sono "Non testato" (vedi EsitoVisualizzato), quindi l'asse non
        // le esclude. Senza questo, la riga "Non testato" della modalita' Stato non mostrerebbe mai le funzionalita' non testate.
        && config.CampiX.Concat(config.CampiY).All(d => DimensioneDiCatalogo(d) || d == DimensioniMatrice.Esito);

    /// <summary>Etichetta dell'esito delle combinazioni del catalogo senza Test (vedi EsitoVisualizzato).</summary>
    private const string EsitoNonTestato = "Non testato";

    /// <summary>Universo "Non testato" attivo nel Report (regola di prodotto 6.7.0). Entra solo se TUTTI i criteri sono di identita'
    /// (componente, veicolo, funzionalita', categoria, gamma). Ogni altro criterio lo spegne, in ogni modalita': tipo di Test, operatore,
    /// assegnatario, requisito, campi custom, date, filtri TIME ed esito diverso da "Non testato". Una combinazione del catalogo senza
    /// Test non ha un rappresentante che possa superare quel criterio, quindi non deve comparire. componente e veicolo del Report restringono
    /// l'universo. Stesso predicato per la matrice, la Home (Prepara senza filtro), l'export Excel e PDF (Genera con il filtro del Report)
    /// e i valori dei filtri.</summary>
    public static bool UniversoAttivo(ConfigurazioneMatriceReport config, IReadOnlyList<Componente>? ecuCatalogo, ReportFilter? filtro = null) =>
        UniversoNonTestatoAttivo(config, ecuCatalogo) && !HaCriterioNonIdentita(config, filtro) && IdentitaCompatibili(config, filtro);

    /// <summary>Vero se un criterio del Report non e' di identita'. Il filtro Esito e' di identita' solo se ammette "Non testato".</summary>
    private static bool HaCriterioNonIdentita(ConfigurazioneMatriceReport config, ReportFilter? filtro)
    {
        // TIME: il filtro sulla durata (operazione o totale del Test) non ha valore per le combinazioni senza Test, in ogni modalita'.
        if (config.FiltroTempoOperazione != null || config.FiltroTempoTotaleTest != null) return true;
        if (filtro != null && (filtro.HaFiltroTempo || filtro.TemplateUid.Count > 0 || filtro.DataDa.HasValue || filtro.DataA.HasValue
            || filtro.CampiCustom.Values.Any(v => v.Count > 0) || filtro.Operatore.Count > 0 || filtro.Strumento.Count > 0)) return true;
        foreach (var (dimensione, valori) in config.Filtri)
        {
            if (valori.Count == 0 || DimensioneDiCatalogo(dimensione)) continue;
            if (dimensione == DimensioniMatrice.Esito && valori.Contains(EsitoNonTestato)) continue;
            return true;
        }
        return false;
    }

    /// <summary>componente e veicolo del filtro di pagina: senza valori in comune con il filtro della matrice, l'universo e' vuoto.</summary>
    private static bool IdentitaCompatibili(ConfigurazioneMatriceReport config, ReportFilter? filtro) =>
        filtro == null || (IdentitaCompatibile(config.Filtri, DimensioniMatrice.Componente, filtro.Componente)
            && IdentitaCompatibile(config.Filtri, DimensioniMatrice.Sottoveicolo, filtro.Veicoli));

    private static bool IdentitaCompatibile(Dictionary<string, List<string>> filtri, string dimensione, List<string> ammessi) =>
        ammessi.Count == 0 || !filtri.TryGetValue(dimensione, out var valori) || valori.Count == 0
        || valori.Intersect(ammessi, StringComparer.Ordinal).Any();

    /// <summary>Configurazione per l'universo: componente e veicolo del Report si intersecano con i filtri di matrice. Il resto della
    /// configurazione resta uguale; la matrice dei Test continua a usare la configurazione originale.</summary>
    private static ConfigurazioneMatriceReport ConfigurazioneUniverso(ConfigurazioneMatriceReport config, ReportFilter? filtro)
    {
        if (filtro == null || (filtro.Componente.Count == 0 && filtro.Veicoli.Count == 0)) return config;
        var filtri = new Dictionary<string, List<string>>(config.Filtri, StringComparer.OrdinalIgnoreCase);
        RestringiIdentita(filtri, DimensioniMatrice.Componente, filtro.Componente);
        RestringiIdentita(filtri, DimensioniMatrice.Sottoveicolo, filtro.Veicoli);
        return config.ConFiltri(filtri);
    }

    private static void RestringiIdentita(Dictionary<string, List<string>> filtri, string dimensione, List<string> ammessi)
    {
        if (ammessi.Count == 0) return;
        filtri[dimensione] = filtri.TryGetValue(dimensione, out var esistenti) && esistenti.Count > 0
            ? esistenti.Intersect(ammessi, StringComparer.Ordinal).ToList()
            : ammessi.ToList();
    }

    /// <summary>Configurazione senza il filtro della dimensione indicata: i valori di quell'asse non dipendono dalla sua selezione.</summary>
    private static ConfigurazioneMatriceReport SenzaFiltroDimensione(ConfigurazioneMatriceReport config, string dimensione)
    {
        if (!config.Filtri.ContainsKey(dimensione)) return config;
        var filtri = new Dictionary<string, List<string>>(config.Filtri, StringComparer.OrdinalIgnoreCase);
        filtri.Remove(dimensione);
        return config.ConFiltri(filtri);
    }

    private static bool DimensioneDiCatalogo(string dimensione) =>
        dimensione is DimensioniMatrice.Gamma or DimensioniMatrice.Sottoveicolo or DimensioniMatrice.Componente
            or DimensioniMatrice.Funzionalita or DimensioniMatrice.CategoriaFunzionalita;

    /// <summary>Valori di una funzionalità su una dimensione di catalogo che NON è parte dell'identità core (Categoria).</summary>
    private static IEnumerable<string> ValoriCatalogoFunzionalita(Funzionalita f, string dimensione) =>
        dimensione == DimensioniMatrice.CategoriaFunzionalita
            ? f.Categorie.Where(c => !string.IsNullOrWhiteSpace(c))
            : Array.Empty<string>();

    /// <summary>Valori di una combinazione del catalogo su una dimensione di catalogo (per i filtri dell'universo).</summary>
    private static IEnumerable<string> ValoriCatalogo(string dimensione, Funzionalita f, string componente, string veicolo,
        IReadOnlyDictionary<string, string> gammaDi)
    {
        if (dimensione == DimensioniMatrice.Gamma)
            return !string.IsNullOrEmpty(veicolo) && gammaDi.TryGetValue(veicolo, out var g) && !string.IsNullOrWhiteSpace(g)
                ? new[] { g } : Array.Empty<string>();
        if (dimensione == DimensioniMatrice.Sottoveicolo) return string.IsNullOrEmpty(veicolo) ? Array.Empty<string>() : new[] { veicolo };
        if (dimensione == DimensioniMatrice.Componente) return string.IsNullOrEmpty(componente) ? Array.Empty<string>() : new[] { componente };
        if (dimensione == DimensioniMatrice.Funzionalita) return new[] { f.Nome };
        return ValoriCatalogoFunzionalita(f, dimensione);
    }

    /// <summary>Valori ammessi da un filtro di vista su una dimensione di catalogo; null se il filtro non c'e' (o e' vuoto). Ordinale,
    /// come il Contains di PassaFiltriCatalogo che la potatura sostituisce.</summary>
    private static HashSet<string>? InsiemeFiltro(Dictionary<string, List<string>> filtri, string dimensione) =>
        filtri.TryGetValue(dimensione, out var valori) && valori.Count > 0 ? new HashSet<string>(valori, StringComparer.Ordinal) : null;

    private static bool PassaFiltriCatalogo(Dictionary<string, List<string>> filtri, Funzionalita f, string componente, string veicolo,
        IReadOnlyDictionary<string, string> gammaDi) =>
        // Senza filtri nessuna allocazione: il corpo con la lambda sta in un metodo a parte (la cattura dei parametri alloca a ogni chiamata).
        filtri.Count == 0 || PassaFiltriCatalogoConFiltri(filtri, f, componente, veicolo, gammaDi);

    // Esito e' valutato dopo la riduzione anche per le combinazioni aggiunte dal catalogo.
    private static bool PassaFiltriCatalogoConFiltri(Dictionary<string, List<string>> filtri, Funzionalita f, string componente, string veicolo,
        IReadOnlyDictionary<string, string> gammaDi) =>
        filtri.All(filtro => filtro.Key == DimensioniMatrice.Esito || filtro.Value.Count == 0
            || ValoriCatalogo(filtro.Key, f, componente, veicolo, gammaDi).Any(filtro.Value.Contains));

    /// <summary>Prodotto cartesiano dei valori di catalogo sulle dimensioni aggiuntive (in pratica la sola Categoria).</summary>
    private static List<Dictionary<string, string>> EspandiCatalogo(Funzionalita f, List<string> estra)
    {
        var risultato = new List<Dictionary<string, string>> { new(StringComparer.OrdinalIgnoreCase) };
        foreach (var dimensione in estra)
        {
            var valori = ValoriCatalogoFunzionalita(f, dimensione).DefaultIfEmpty("").ToList();
            var esteso = new List<Dictionary<string, string>>(risultato.Count * valori.Count);
            foreach (var basePronta in risultato)
                foreach (var valore in valori)
                    esteso.Add(new Dictionary<string, string>(basePronta, StringComparer.OrdinalIgnoreCase) { [dimensione] = valore });
            risultato = esteso;
        }
        return risultato;
    }

    /// <summary>Una combinazione senza Test del catalogo (core) con le sue estese (una per valore delle dimensioni aggiuntive).</summary>
    private sealed record VoceUniversoNonTestato(string Chiave, CombinazioneAccumulatore Core,
        List<(string Chiave, CombinazioneAccumulatore Estesa)> Estese);

    /// <summary>Enumera l'universo "Non testato": le combinazioni del catalogo che non compaiono nei Test e sono applicabili. Una
    /// combinazione del catalogo è coerente se la sua componente è associata al veicolo (o non ha veicoli) e la funzionalità è assegnata a
    /// quell'componente; i componenti e i veicoli archiviati non entrano. I filtri di vista potano l'enumerazione (senza cambiare il risultato).</summary>
    private static List<VoceUniversoNonTestato> UniversoNonTestato(ConfigurazioneMatriceReport config, IReadOnlyList<Componente> ecuCatalogo,
        HashSet<string> eseguite, IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita,
        IReadOnlyDictionary<string, string> gammaDi, Dictionary<string, CombinazioneAccumulatore> combinazioniCore,
        Func<Componente, string, Funzionalita, bool>? ammesso = null, CancellationToken ct = default, int? limite = null)
    {
        var righe = new List<VoceUniversoNonTestato>();
        foreach (var voce in EnumeraUniversoNonTestato(config, ecuCatalogo, eseguite, veicoli, funzionalita, gammaDi, combinazioniCore, ammesso, ct))
        {
            if (limite is { } max && righe.Count >= max) throw new LimiteUniversoExportSuperatoException();
            righe.Add(voce);
        }
        return righe;
    }

    private static IEnumerable<VoceUniversoNonTestato> EnumeraUniversoNonTestato(ConfigurazioneMatriceReport config, IReadOnlyList<Componente> ecuCatalogo,
        HashSet<string> eseguite, IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita,
        IReadOnlyDictionary<string, string> gammaDi, Dictionary<string, CombinazioneAccumulatore> combinazioniCore,
        Func<Componente, string, Funzionalita, bool>? ammesso = null, CancellationToken ct = default)
    {
        var veicoliAttivi = new HashSet<string>(veicoli.Where(v => !v.Archiviata).Select(v => v.Nome), StringComparer.OrdinalIgnoreCase);
        var funzioniAttive = funzionalita.Where(f => !f.Archiviata).ToList();
        // Restrizioni per componente (Funzionalita.VeicoliPerEcu) indicizzate una volta per funzionalità: null per le funzionalità senza voci.
        var restrizioniPerFunzione = funzioniAttive.Select(f => f.IndiceVeicoliPerEcu()).ToList();
        var estra = DimensioniAggiuntive(config);
        var estraConEsito = estra.Contains(DimensioniMatrice.Esito, StringComparer.OrdinalIgnoreCase);
        // Con componente o funzionalita' duplicate (a meno di maiuscole) la stessa combinazione del catalogo puo' essere enumerata due volte:
        // solo in quel caso si tiene l'elenco delle gia' viste, come il ContainsKey su combinazioniCore che sostituisce.
        var chiaviUniche = ecuCatalogo.Select(e => e.Nome).Distinct(StringComparer.OrdinalIgnoreCase).Count() == ecuCatalogo.Count
            && funzioniAttive.Select(f => f.Nome).Distinct(StringComparer.OrdinalIgnoreCase).Count() == funzioniAttive.Count;
        var viste = chiaviUniche ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Potatura esatta dai filtri di vista: PassaFiltriCatalogo scarterebbe comunque ogni combinazione fuori dal filtro (il filtro
        // Esito non entra qui), quindi componente, veicoli, funzionalita' e categorie fuori dal filtro non vengono nemmeno enumerati. Con un
        // filtro componente il lavoro scende dal catalogo intero alle sole Componenti selezionati.
        var ecuAmmesse = InsiemeFiltro(config.Filtri, DimensioniMatrice.Componente);
        var veicoliAmmessi = InsiemeFiltro(config.Filtri, DimensioniMatrice.Sottoveicolo);
        var funzionalitaAmmesse = InsiemeFiltro(config.Filtri, DimensioniMatrice.Funzionalita);
        var categorieAmmesse = InsiemeFiltro(config.Filtri, DimensioniMatrice.CategoriaFunzionalita);
        foreach (var componente in ecuCatalogo.Where(e => !e.Archiviata))
        {
            if (ecuAmmesse != null && !ecuAmmesse.Contains(componente.Nome)) continue;
            // componente senza veicolo associato: la combinazione esiste senza veicolo (valore vuoto, come nei Test senza veicolo).
            var veicoliEcu = componente.VeicoliAssociati.Count == 0
                ? new List<string> { "" }
                : componente.VeicoliAssociati.Where(v => veicoliAttivi.Contains(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (veicoliAmmessi != null) veicoliEcu = veicoliEcu.Where(veicoliAmmessi.Contains).ToList();
            for (var j = 0; j < funzioniAttive.Count; j++)
            {
                ct.ThrowIfCancellationRequested();
                var f = funzioniAttive[j];
                if (!f.ValeSuEcu(componente.Nome)) continue;
                if (funzionalitaAmmesse != null && !funzionalitaAmmesse.Contains(f.Nome)) continue;
                if (categorieAmmesse != null && !f.Categorie.Any(c => !string.IsNullOrWhiteSpace(c) && categorieAmmesse.Contains(c))) continue;
                var ammessiRistretti = restrizioniPerFunzione[j]?.GetValueOrDefault(componente.Nome);
                foreach (var veicolo in veicoliEcu)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!VeicoloAmmesso(ammessiRistretti, veicolo)) continue;
                    if (ammesso != null && !ammesso(componente, veicolo, f)) continue; // restringimento del dettaglio di una cella
                    var chiaveCore = ChiaveCore(f.Nome, componente.Nome, veicolo);
                    if (combinazioniCore.ContainsKey(chiaveCore)) continue; // presente in almeno un Test: già nei conteggi
                    if (!PassaFiltriCatalogo(config.Filtri, f, componente.Nome, veicolo, gammaDi)) continue;
                    if (viste != null && !viste.Add(chiaveCore)) continue;
                    var propagabile = PropagabileNonTestato(f, componente, veicolo, eseguite);
                    var core = new CombinazioneAccumulatore(f.Nome, componente.Nome, veicolo, ValoriEstraVuoti) { Propagabile = propagabile };
                    var estese = new List<(string Chiave, CombinazioneAccumulatore Estesa)>();
                    foreach (var valoriEstra in EspandiCatalogo(f, estra))
                    {
                        // Esito non e' una dimensione del catalogo: il valore e' quello dello stato corrente (vedi ProiettaStatoCorrente).
                        if (estraConEsito) valoriEstra[DimensioniMatrice.Esito] = EsitoVisualizzato(core);
                        estese.Add((ChiaveCombinazione(f.Nome, componente.Nome, veicolo, valoriEstra),
                            new CombinazioneAccumulatore(f.Nome, componente.Nome, veicolo, valoriEstra) { Propagabile = propagabile }));
                    }
                    yield return new VoceUniversoNonTestato(chiaveCore, core, estese);
                }
            }
        }
    }

    /// <summary>Aggiunge l'universo "Non testato" alle combinazioni gia' proiettate sullo stato corrente. Un core senza Test ha stato
    /// "Non testato" e nessun candidato da filtrare: l'unica regola che si applica e' il filtro Esito, come in ProiettaStatoCorrente.</summary>
    private static void AggiungiUniversoNonTestato(List<VoceUniversoNonTestato> universo, ConfigurazioneMatriceReport config,
        Dictionary<string, CombinazioneAccumulatore> estese, Dictionary<string, CombinazioneAccumulatore> core)
    {
        var filtroEsito = FiltroEsitoStatoCorrente(config);
        foreach (var voce in universo)
        {
            if (!PassaFiltroEsitoCorrente(voce.Core, filtroEsito)) continue;
            core[voce.Chiave] = voce.Core;
            foreach (var (chiave, estesa) in voce.Estese) estese[chiave] = estesa;
        }
    }

    /// <summary>Conteggi dell'universo "Non testato" per cella, colonna, riga e totale generale: (Non testato, Da decidere). Nessuna
    /// combinazione del catalogo viene materializzata (vedi ConteggiUniversoNonTestato).</summary>
    internal sealed class UniversoAggregato
    {
        /// <summary>Chiavi degli assi (X = colonne, Y = righe) e conteggi per cella in forma densa: l'elemento [y * ChiaviX.Length + x]
        /// dà le combinazioni non testate (NonTestatiCelle) e da decidere (DaDecidereCelle) della cella (riga Y, colonna X). Nessun
        /// oggetto per cella: la griglia legge i conteggi di una cella solo quando la disegna (vedi MatricePreparata.CreaCella).</summary>
        public string[] ChiaviX = [];
        public string[] ChiaviY = [];
        public int[] NonTestatiCelle = [];
        public int[] DaDecidereCelle = [];
        public readonly Dictionary<string, (int Nt, int Dd)> Colonne = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (int Nt, int Dd)> Righe = new(StringComparer.OrdinalIgnoreCase);
        public (int Nt, int Dd) Generale;
    }

    /// <summary>Combinazione dell'universo materializzata (solo per i totali con N.A. e per il dettaglio di una cella).</summary>
    internal sealed record VoceUniversoCelle(string Chiave, CombinazioneAccumulatore Acc, HashSet<string> Celle);

    /// <summary>Dimensioni di catalogo che il conteggio aggregato sa calcolare per combinazione senza istanziarla.</summary>
    private static readonly HashSet<string> DimensioniUniversoAggregabili = new(StringComparer.OrdinalIgnoreCase)
    {
        DimensioniMatrice.Componente, DimensioniMatrice.Sottoveicolo, DimensioniMatrice.Funzionalita,
        DimensioniMatrice.Gamma, DimensioniMatrice.CategoriaFunzionalita, DimensioniMatrice.Esito,
    };

    /// <summary>Chiave di un asse per una combinazione dell'universo, come ChiaveAsse su un accumulatore senza Test: valore del
    /// catalogo oppure l'etichetta del valore vuoto. La Categoria viene dall'espansione (categoria, null se non espansa).</summary>
    private static string ChiaveAsseUniverso(string dimensione, string componente, string veicolo, Funzionalita f, string? categoria,
        IReadOnlyDictionary<string, string> gammaDi)
    {
        string? valore = dimensione switch
        {
            DimensioniMatrice.Componente => string.IsNullOrEmpty(componente) ? null : componente,
            DimensioniMatrice.Sottoveicolo => string.IsNullOrEmpty(veicolo) ? null : veicolo,
            DimensioniMatrice.Funzionalita => string.IsNullOrWhiteSpace(f.Nome) ? null : f.Nome,
            DimensioniMatrice.Gamma => string.IsNullOrEmpty(veicolo) || !gammaDi.TryGetValue(veicolo, out var g) || string.IsNullOrWhiteSpace(g) ? null : g,
            DimensioniMatrice.Esito => "Non testato",
            _ => string.IsNullOrEmpty(categoria) ? null : categoria,
        };
        return valore ?? EtichettaValoreVuoto(dimensione);
    }

    /// <summary>Categorie di una funzionalità come le espande EspandiCatalogo: non vuote, senza duplicati a meno di maiuscole (vince
    /// l'ultimo valore, come nel dizionario delle combinazioni), oppure un solo valore vuoto.</summary>
    private static List<string> CategorieEspanse(Funzionalita f)
    {
        var valori = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in f.Categorie.Where(c => !string.IsNullOrWhiteSpace(c))) valori[c] = c;
        return valori.Count == 0 ? new List<string> { "" } : valori.Values.ToList();
    }

    private static void Incrementa<TChiave>(Dictionary<TChiave, (int Nt, int Dd)> conteggi, TChiave chiave, bool daDecidere) where TChiave : notnull
    {
        conteggi.TryGetValue(chiave, out var v);
        conteggi[chiave] = daDecidere ? (v.Nt, v.Dd + 1) : (v.Nt + 1, v.Dd);
    }

    /// <summary>Conteggi aggregati dell'universo "Non testato" (stesse regole di UniversoNonTestato) per le configurazioni con un
    /// campo per asse e dimensioni di catalogo: un incremento per combinazione, nessun oggetto, nessuna chiave composta per voce.
    /// Null se il modello non copre il caso (più campi per asse, altre dimensioni, componente o funzionalità con nomi duplicati): in quel
    /// caso il chiamante usa il percorso materializzato.</summary>
    private static UniversoAggregato? ConteggiUniversoNonTestato(ConfigurazioneMatriceReport config, IReadOnlyList<Componente> ecuCatalogo,
        HashSet<string> eseguite, IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita,
        IReadOnlyDictionary<string, string> gammaDi, Dictionary<string, CombinazioneAccumulatore> combinazioniCore)
    {
        if (config.CampiX.Count != 1 || config.CampiY.Count != 1) return null;
        var dimX = config.CampiX[0];
        var dimY = config.CampiY[0];
        if (!DimensioniUniversoAggregabili.Contains(dimX) || !DimensioniUniversoAggregabili.Contains(dimY)) return null;
        var componenti = ecuCatalogo.Where(e => !e.Archiviata).ToList();
        var funzioniAttive = funzionalita.Where(f => !f.Archiviata).ToList();
        if (componenti.Select(e => e.Nome).Distinct(StringComparer.OrdinalIgnoreCase).Count() != componenti.Count
            || funzioniAttive.Select(f => f.Nome).Distinct(StringComparer.OrdinalIgnoreCase).Count() != funzioniAttive.Count) return null;

        // Forma comune (componente x Categoria, nessun filtro di vista, un veicolo per componente): conteggio per componente e categoria, senza scorrere
        // componente x funzionalita' (vedi ConteggiUniversoEcuCategoria).
        // Un filtro senza valori non esclude nulla (vedi InsiemeFiltro e PassaFiltriCatalogo): conta come assenza di filtri.
        if (dimX == DimensioniMatrice.Componente && dimY == DimensioniMatrice.CategoriaFunzionalita && config.Filtri.All(f => f.Value.Count == 0)
            && componenti.All(e => e.VeicoliAssociati.Count <= 1))
            return ConteggiUniversoEcuCategoria(componenti, funzioniAttive, veicoli, combinazioniCore, gammaDi);

        // Stessa espansione di DimensioniAggiuntive: la Categoria (se e' un asse) genera una combinazione per categoria.
        var espandiCategoria = dimX == DimensioniMatrice.CategoriaFunzionalita || dimY == DimensioniMatrice.CategoriaFunzionalita;
        var veicoliAttivi = new HashSet<string>(veicoli.Where(v => !v.Archiviata).Select(v => v.Nome), StringComparer.OrdinalIgnoreCase);
        var ecuAmmesse = InsiemeFiltro(config.Filtri, DimensioniMatrice.Componente);
        var veicoliAmmessi = InsiemeFiltro(config.Filtri, DimensioniMatrice.Sottoveicolo);
        var funzionalitaAmmesse = InsiemeFiltro(config.Filtri, DimensioniMatrice.Funzionalita);
        var categorieAmmesse = InsiemeFiltro(config.Filtri, DimensioniMatrice.CategoriaFunzionalita);

        // Entita' del catalogo indicizzate una sola volta (componente, veicoli per componente, funzionalita', categorie): nel ciclo delle
        // combinazioni restano solo accessi per indice, senza stringhe, dizionari o LINQ per combinazione.
        var veicoliPerEcu = new List<string>?[componenti.Count];
        var indiciVeicoliPerEcu = new int[componenti.Count][];
        var indiceVeicolo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var e = 0; e < componenti.Count; e++)
        {
            var componente = componenti[e];
            if (ecuAmmesse != null && !ecuAmmesse.Contains(componente.Nome)) continue;
            // componente senza veicolo associato: la combinazione esiste senza veicolo (valore vuoto, come nei Test senza veicolo).
            var veicoliEcu = componente.VeicoliAssociati.Count == 0
                ? new List<string> { "" }
                : componente.VeicoliAssociati.Where(v => veicoliAttivi.Contains(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (veicoliAmmessi != null) veicoliEcu = veicoliEcu.Where(veicoliAmmessi.Contains).ToList();
            veicoliPerEcu[e] = veicoliEcu;
            indiciVeicoliPerEcu[e] = new int[veicoliEcu.Count];
            for (var k = 0; k < veicoliEcu.Count; k++)
            {
                if (!indiceVeicolo.TryGetValue(veicoliEcu[k], out var iv)) { iv = indiceVeicolo.Count; indiceVeicolo[veicoliEcu[k]] = iv; }
                indiciVeicoliPerEcu[e][k] = iv;
            }
        }
        var categoriePerFunzione = new List<string>?[funzioniAttive.Count];
        for (var j = 0; j < funzioniAttive.Count; j++)
            categoriePerFunzione[j] = espandiCategoria ? CategorieEspanse(funzioniAttive[j]) : null;
        var indiciCategoriePerFunzione = categoriePerFunzione
            .Select(c => c == null ? null : Enumerable.Range(0, c.Count).ToArray()).ToArray();

        // Chiavi degli assi: un indice per valore distinto, per dimensione.
        var chiaviX = new IndiciChiavi();
        var chiaviY = new IndiciChiavi();
        var mappaX = CreaMappaAsse(dimX, componenti, veicoliPerEcu, indiciVeicoliPerEcu, funzioniAttive, categoriePerFunzione, gammaDi, chiaviX);
        var mappaY = CreaMappaAsse(dimY, componenti, veicoliPerEcu, indiciVeicoliPerEcu, funzioniAttive, categoriePerFunzione, gammaDi, chiaviY);

        // Presenza nei Test: stessa chiave core (componente, funzionalita', veicolo) senza distinzione di maiuscole, come combinazioniCore.
        var indiceEcu = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var e = 0; e < componenti.Count; e++) indiceEcu[componenti[e].Nome] = e;
        var indiceFunzione = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var j = 0; j < funzioniAttive.Count; j++) indiceFunzione[funzioniAttive[j].Nome] = j;
        var presenti = new HashSet<(int J, int E, int V)>();
        foreach (var acc in combinazioniCore.Values)
            if (indiceFunzione.TryGetValue(acc.Funzionalita ?? "", out var jj) && indiceEcu.TryGetValue(acc.Componente, out var ee)
                && indiceVeicolo.TryGetValue(acc.Veicolo, out var vv))
                presenti.Add((jj, ee, vv));

        var nX = chiaviX.Chiavi.Count;
        var nY = chiaviY.Chiavi.Count;
        var celleNt = new int[nX * nY];
        var celleDd = new int[nX * nY];
        var colonneNt = new int[nX];
        var colonneDd = new int[nX];
        var righeNt = new int[nY];
        var righeDd = new int[nY];
        var generaleNt = 0;
        var generaleDd = 0;
        var xsDistinti = new List<int>();
        var ysDistinti = new List<int>();
        var categorieNulle = new int[] { -1 };
        // Restrizioni per componente (Funzionalita.VeicoliPerEcu) indicizzate una volta per funzionalità: null per le funzionalità senza voci.
        var restrizioniPerFunzione = funzioniAttive.Select(f => f.IndiceVeicoliPerEcu()).ToList();

        for (var e = 0; e < componenti.Count; e++)
        {
            var veicoliEcu = veicoliPerEcu[e];
            if (veicoliEcu == null) continue;
            var componente = componenti[e];
            var piuVeicoli = componente.VeicoliAssociati.Count > 1;
            for (var j = 0; j < funzioniAttive.Count; j++)
            {
                var f = funzioniAttive[j];
                if (!f.ValeSuEcu(componente.Nome)) continue;
                if (funzionalitaAmmesse != null && !funzionalitaAmmesse.Contains(f.Nome)) continue;
                if (categorieAmmesse != null && !f.Categorie.Any(c => !string.IsNullOrWhiteSpace(c) && categorieAmmesse.Contains(c))) continue;
                var categorie = categoriePerFunzione[j];
                var ammessiRistretti = restrizioniPerFunzione[j]?.GetValueOrDefault(componente.Nome);
                for (var k = 0; k < veicoliEcu.Count; k++)
                {
                    var veicolo = veicoliEcu[k];
                    if (!VeicoloAmmesso(ammessiRistretti, veicolo)) continue;
                    if (presenti.Contains((j, e, indiciVeicoliPerEcu[e][k]))) continue; // presente in almeno un Test: gia' nei conteggi
                    if (!PassaFiltriCatalogo(config.Filtri, f, componente.Nome, veicolo, gammaDi)) continue;
                    var daDecidere = piuVeicoli && PropagabileNonTestato(f, componente, veicolo, eseguite);
                    var cicloCategorie = categorie == null ? categorieNulle : indiciCategoriePerFunzione[j]!;
                    xsDistinti.Clear();
                    ysDistinti.Clear();
                    foreach (var c in cicloCategorie)
                    {
                        var x = IndiceAsseUniverso(mappaX, e, k, j, c);
                        var y = IndiceAsseUniverso(mappaY, e, k, j, c);
                        if (daDecidere) celleDd[y * nX + x]++; else celleNt[y * nX + x]++;
                        if (!xsDistinti.Contains(x)) xsDistinti.Add(x);
                        if (!ysDistinti.Contains(y)) ysDistinti.Add(y);
                    }
                    // Totali per combinazione: una volta per colonna e per riga che tocca, e una volta nel generale.
                    foreach (var x in xsDistinti) { if (daDecidere) colonneDd[x]++; else colonneNt[x]++; }
                    foreach (var y in ysDistinti) { if (daDecidere) righeDd[y]++; else righeNt[y]++; }
                    if (daDecidere) generaleDd++; else generaleNt++;
                }
            }
        }

        var risultato = new UniversoAggregato
        {
            ChiaviX = chiaviX.Chiavi.ToArray(), ChiaviY = chiaviY.Chiavi.ToArray(), NonTestatiCelle = celleNt, DaDecidereCelle = celleDd,
        };
        for (var x = 0; x < nX; x++)
            if (colonneNt[x] > 0 || colonneDd[x] > 0) risultato.Colonne[chiaviX.Chiavi[x]] = (colonneNt[x], colonneDd[x]);
        for (var y = 0; y < nY; y++)
            if (righeNt[y] > 0 || righeDd[y] > 0) risultato.Righe[chiaviY.Chiavi[y]] = (righeNt[y], righeDd[y]);
        risultato.Generale = (generaleNt, generaleDd);
        return risultato;
    }

    /// <summary>Forma comune del conteggio aggregato: asse componente x Categoria, nessun filtro di vista e al piu' un veicolo per componente (quindi
    /// nessuna combinazione "da decidere"). Le combinazioni non testate di ogni (componente, categoria) sono i veicoli del componente moltiplicati
    /// per le funzionalita' applicabili che hanno la categoria, meno quelle presenti nei Test: si calcola per componente e categoria, senza
    /// scorrere componente x funzionalita'. Stessi risultati di ConteggiUniversoNonTestato (verificati da ReportUniversoAggregatoTests).</summary>
    private static UniversoAggregato? ConteggiUniversoEcuCategoria(List<Componente> componenti, List<Funzionalita> funzioniAttive,
        IReadOnlyList<Veicolo> veicoli, Dictionary<string, CombinazioneAccumulatore> combinazioniCore,
        IReadOnlyDictionary<string, string> gammaDi)
    {
        var veicoliAttivi = new HashSet<string>(veicoli.Where(v => !v.Archiviata).Select(v => v.Nome), StringComparer.OrdinalIgnoreCase);
        var veicoliPerEcu = componenti.Select(e => e.VeicoliAssociati.Count == 0
            ? new List<string> { "" }
            : e.VeicoliAssociati.Where(v => veicoliAttivi.Contains(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()).ToList();
        var funzioneVuota = new Funzionalita();
        var chiaviEcu = new IndiciChiavi();
        var chiaviY = new IndiciChiavi();
        var chiaveEcuPerIndice = componenti.Select(e => chiaviEcu.Ottieni(ChiaveAsseUniverso(DimensioniMatrice.Componente, e.Nome, "", funzioneVuota, null, gammaDi))).ToArray();
        var yPerF = new int[funzioniAttive.Count][];
        for (var j = 0; j < funzioniAttive.Count; j++)
            yPerF[j] = CategorieEspanse(funzioniAttive[j])
                .Select(c => chiaviY.Ottieni(ChiaveAsseUniverso(DimensioniMatrice.CategoriaFunzionalita, "", "", funzioniAttive[j], c, gammaDi)))
                .Distinct().ToArray();

        var nTutte = 0;
        var nonTutte = new List<int>();
        var cntTutteY = new int[chiaviY.Chiavi.Count];
        for (var j = 0; j < funzioniAttive.Count; j++)
        {
            if (!funzioniAttive[j].TutteLeEcu) { nonTutte.Add(j); continue; }
            nTutte++;
            foreach (var y in yPerF[j]) cntTutteY[y]++;
        }

        var nE = componenti.Count;
        var nY = chiaviY.Chiavi.Count;
        var celle = new int[nE * nY];   // [e * nY + y]: combinazioni non testate del componente e nella categoria y
        var colonne = new int[nE];      // combinazioni non testate per componente
        for (var e = 0; e < nE; e++)
        {
            var nVeicoli = veicoliPerEcu[e].Count;
            var base0 = e * nY;
            for (var y = 0; y < nY; y++) celle[base0 + y] = nVeicoli * cntTutteY[y];
            colonne[e] = nVeicoli * nTutte;
            foreach (var j in nonTutte)
            {
                if (!funzioniAttive[j].ValeSuEcu(componenti[e].Nome)) continue;
                colonne[e] += nVeicoli;
                foreach (var y in yPerF[j]) celle[base0 + y] += nVeicoli;
            }
        }

        // Restrizioni per componente (Funzionalita.VeicoliPerEcu): il conteggio di base conta tutti i veicoli del componente per ogni funzionalita'
        // applicabile; per le coppie (funzionalita', componente) con voce si tolgono i veicoli non ammessi. Stessa regola di ValeSu: un componente
        // senza veicoli conserva la sua combinazione senza veicolo.
        for (var j = 0; j < funzioniAttive.Count; j++)
        {
            var indiceRistretti = funzioniAttive[j].IndiceVeicoliPerEcu();
            if (indiceRistretti == null) continue;
            for (var e = 0; e < nE; e++)
            {
                if (!funzioniAttive[j].ValeSuEcu(componenti[e].Nome) || !indiceRistretti.TryGetValue(componenti[e].Nome, out var ammessi)) continue;
                var nonAmmessi = veicoliPerEcu[e].Count(v => !VeicoloAmmesso(ammessi, v));
                if (nonAmmessi == 0) continue;
                colonne[e] -= nonAmmessi;
                foreach (var y in yPerF[j]) celle[e * nY + y] -= nonAmmessi;
            }
        }

        // Presenti nei Test: tolte dal conteggio (solo se applicabili: le altre non erano nell'universo).
        var indiceEcu = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var e = 0; e < nE; e++) indiceEcu[componenti[e].Nome] = e;
        var indiceFunzione = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var j = 0; j < funzioniAttive.Count; j++) indiceFunzione[funzioniAttive[j].Nome] = j;
        foreach (var acc in combinazioniCore.Values)
        {
            if (!indiceFunzione.TryGetValue(acc.Funzionalita ?? "", out var j) || !indiceEcu.TryGetValue(acc.Componente, out var e)) continue;
            var f = funzioniAttive[j];
            if (!f.ValeSu(componenti[e].Nome, acc.Veicolo)) continue;
            if (!veicoliPerEcu[e].Contains(acc.Veicolo, StringComparer.OrdinalIgnoreCase)) continue;
            colonne[e]--;
            foreach (var y in yPerF[j]) celle[e * nY + y]--;
        }

        // Chiavi X = componente, una per indice e. Due componente con la stessa chiave non si distinguono nella forma densa: percorso materializzato.
        var chiaviEcuX = Enumerable.Range(0, nE).Select(e => chiaviEcu.Chiavi[chiaveEcuPerIndice[e]]).ToArray();
        if (chiaviEcuX.Distinct(StringComparer.OrdinalIgnoreCase).Count() != nE) return null;
        var nonTestatiCelle = new int[nY * nE];   // [y * nE + e]
        var risultato = new UniversoAggregato
        {
            ChiaviX = chiaviEcuX, ChiaviY = chiaviY.Chiavi.ToArray(), NonTestatiCelle = nonTestatiCelle, DaDecidereCelle = new int[nY * nE],
        };
        var righe = new int[nY];
        var generale = 0;
        for (var e = 0; e < nE; e++)
        {
            var chiaveEcu = chiaviEcuX[e];
            for (var y = 0; y < nY; y++)
            {
                var n = celle[e * nY + y];
                if (n <= 0) continue;
                nonTestatiCelle[y * nE + e] = n;
                righe[y] += n;
            }
            if (colonne[e] > 0) risultato.Colonne[chiaveEcu] = (colonne[e], 0);
            generale += colonne[e];
        }
        for (var y = 0; y < nY; y++)
            if (righe[y] > 0) risultato.Righe[chiaviY.Chiavi[y]] = (righe[y], 0);
        risultato.Generale = (generale, 0);
        return risultato;
    }

    /// <summary>Indici delle chiavi di un asse: un numero per ciascun valore distinto (senza distinzione di maiuscole).</summary>
    private sealed class IndiciChiavi
    {
        public readonly Dictionary<string, int> Indici = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Chiavi = new();

        public int Ottieni(string chiave)
        {
            if (Indici.TryGetValue(chiave, out var indice)) return indice;
            indice = Chiavi.Count;
            Indici[chiave] = indice;
            Chiavi.Add(chiave);
            return indice;
        }
    }

    private enum TipoAsseUniverso { Componente, Veicolo, Funzione, Categoria, Costante }

    /// <summary>Indice della chiave di un asse per le entita' del catalogo: una tabella per tipo di dimensione.</summary>
    private sealed class MappaAsseUniverso
    {
        public TipoAsseUniverso Tipo;
        public int[] PerEcu = Array.Empty<int>();
        public int[][] PerVeicolo = Array.Empty<int[]>();
        public int[] PerFunzione = Array.Empty<int>();
        public int[][] PerCategoria = Array.Empty<int[]>();
        public int Costante;
    }

    private static MappaAsseUniverso CreaMappaAsse(string dimensione, IReadOnlyList<Componente> componenti, List<string>?[] veicoliPerEcu,
        int[][] indiciVeicoliPerEcu, IReadOnlyList<Funzionalita> funzioniAttive, List<string>?[] categoriePerFunzione,
        IReadOnlyDictionary<string, string> gammaDi, IndiciChiavi chiavi)
    {
        var mappa = new MappaAsseUniverso();
        var funzioneVuota = new Funzionalita();
        switch (dimensione)
        {
            case DimensioniMatrice.Componente:
                mappa.Tipo = TipoAsseUniverso.Componente;
                mappa.PerEcu = componenti.Select(e => chiavi.Ottieni(ChiaveAsseUniverso(dimensione, e.Nome, "", funzioneVuota, null, gammaDi))).ToArray();
                break;
            case DimensioniMatrice.Sottoveicolo:
            case DimensioniMatrice.Gamma:
                mappa.Tipo = TipoAsseUniverso.Veicolo;
                mappa.PerVeicolo = new int[componenti.Count][];
                for (var e = 0; e < componenti.Count; e++)
                {
                    var veicoli = veicoliPerEcu[e];
                    mappa.PerVeicolo[e] = veicoli == null ? Array.Empty<int>()
                        : veicoli.Select(v => chiavi.Ottieni(ChiaveAsseUniverso(dimensione, componenti[e].Nome, v, funzioneVuota, null, gammaDi))).ToArray();
                }
                break;
            case DimensioniMatrice.Funzionalita:
                mappa.Tipo = TipoAsseUniverso.Funzione;
                mappa.PerFunzione = funzioniAttive.Select(f => chiavi.Ottieni(ChiaveAsseUniverso(dimensione, "", "", f, null, gammaDi))).ToArray();
                break;
            case DimensioniMatrice.CategoriaFunzionalita:
                mappa.Tipo = TipoAsseUniverso.Categoria;
                mappa.PerCategoria = new int[funzioniAttive.Count][];
                for (var j = 0; j < funzioniAttive.Count; j++)
                {
                    var categorie = categoriePerFunzione[j];
                    mappa.PerCategoria[j] = categorie == null ? Array.Empty<int>()
                        : categorie.Select(c => chiavi.Ottieni(ChiaveAsseUniverso(dimensione, "", "", funzioniAttive[j], c, gammaDi))).ToArray();
                }
                break;
            default:
                mappa.Tipo = TipoAsseUniverso.Costante;
                mappa.Costante = chiavi.Ottieni(ChiaveAsseUniverso(dimensione, "", "", funzioneVuota, null, gammaDi));
                break;
        }
        return mappa;
    }

    private static int IndiceAsseUniverso(MappaAsseUniverso mappa, int e, int k, int j, int c) => mappa.Tipo switch
    {
        TipoAsseUniverso.Componente => mappa.PerEcu[e],
        TipoAsseUniverso.Veicolo => mappa.PerVeicolo[e][k],
        TipoAsseUniverso.Funzione => mappa.PerFunzione[j],
        TipoAsseUniverso.Categoria => mappa.PerCategoria[j][c],
        _ => mappa.Costante,
    };


    /// <summary>Uguaglianza delle combinazioni core (funzionalità, componente, veicolo) senza distinzione di maiuscole, come le chiavi core.</summary>
    private sealed class ConfrontoTriplaCatalogo : IEqualityComparer<(string F, string E, string V)>
    {
        public static readonly ConfrontoTriplaCatalogo Istanza = new();

        public bool Equals((string F, string E, string V) a, (string F, string E, string V) b) =>
            string.Equals(a.F, b.F, StringComparison.OrdinalIgnoreCase) && string.Equals(a.E, b.E, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.V, b.V, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string F, string E, string V) t) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(t.F), StringComparer.OrdinalIgnoreCase.GetHashCode(t.E),
            StringComparer.OrdinalIgnoreCase.GetHashCode(t.V));
    }

    /// <summary>Filtro di pre-selezione per il dettaglio di una cella: scarta le combinazioni che non possono stare nella cella.
    /// È solo un restringimento: la verifica esatta della cella resta quella di CombinazioniDi. Null se la cella non vincola nulla.</summary>
    private static Func<Componente, string, Funzionalita, bool>? AmmessoPerCella(ConfigurazioneMatriceReport config,
        IReadOnlyDictionary<string, string> gammaDi, string? riga, string? colonna)
    {
        var vincoli = new List<(string Dimensione, string Valore)>();
        void Aggiungi(string dimensione, string? valore)
        {
            if (string.IsNullOrEmpty(valore) || dimensione == DimensioniMatrice.CategoriaFunzionalita) return;
            vincoli.Add((dimensione, valore));
        }
        if (config.CampiX.Count == 1) Aggiungi(config.CampiX[0], colonna);
        if (config.CampiY.Count == 1) Aggiungi(config.CampiY[0], riga);
        if (vincoli.Count == 0) return null;
        return (componente, veicolo, f) => vincoli.All(v => string.Equals(
            ChiaveAsseUniverso(v.Dimensione, componente.Nome, veicolo, f, null, gammaDi), v.Valore, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Converte le combinazioni materializzate dell'universo nella forma usata dai totali e dal dettaglio: per ciascuna, le
    /// celle "colonna\u0001riga" che occupa, calcolate come ChiaveAsse.</summary>
    private static List<VoceUniversoCelle> UniversoCelle(List<VoceUniversoNonTestato> voci, ConfigurazioneMatriceReport config,
        IReadOnlyDictionary<string, string> gammaDi)
    {
        var risultato = new List<VoceUniversoCelle>(voci.Count);
        foreach (var voce in voci)
        {
            var celle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, estesa) in voce.Estese)
                celle.Add(string.Concat(ChiaveAsse(estesa, config.CampiX, gammaDi), "\u0001", ChiaveAsse(estesa, config.CampiY, gammaDi)));
            risultato.Add(new VoceUniversoCelle(voce.Chiave, voce.Core, celle));
        }
        return risultato;
    }

    /// <summary>Aggiunge agli assi le chiavi che hanno almeno una combinazione dell'universo: le stesse righe e colonne che entravano
    /// con le celle materializzate. Le celle non si creano: i conteggi restano nella forma densa e CreaCella li somma alla cella dei Test.</summary>
    private static void AggiungiAssiUniverso(UniversoAggregato universo, Dictionary<string, string> colonneViste,
        Dictionary<string, Dictionary<string, CellaAccumulatore>> perRiga)
    {
        // Una colonna (riga) ha una cella con conteggi se e solo se i suoi conteggi di colonna (riga) sono positivi: ogni combinazione
        // che tocca una colonna conta in almeno una sua cella. Quindi non serve scorrere tutte le celle.
        foreach (var (x, (nonTestati, daDecidere)) in universo.Colonne)
            if ((nonTestati > 0 || daDecidere > 0) && !colonneViste.ContainsKey(x)) colonneViste[x] = x;
        foreach (var (y, (nonTestati, daDecidere)) in universo.Righe)
            if ((nonTestati > 0 || daDecidere > 0) && !perRiga.ContainsKey(y))
                perRiga[y] = new Dictionary<string, CellaAccumulatore>(StringComparer.OrdinalIgnoreCase);
    }

    private static void AggiungiConteggiUniverso(CellaAccumulatore cella, int nonTestati, int daDecidere)
    {
        // Stesso effetto di nonTestati chiamate Aggiungi(NonTestato) e di daDecidere chiamate Aggiungi(DaDecidere) senza valutazione
        // ne' dati manuali: ciascuna incrementa solo Totali e il contatore del proprio stato. Senza ciclo per combinazione.
        cella.Totali += nonTestati + daDecidere;
        cella.NonTestato += nonTestati;
        cella.DaDecidere += daDecidere;
    }

    /// <summary>Regola di propagabilita' (come nell'Audit): una combinazione senza Test sul veicolo e' "!" se un altro veicolo
    /// della stessa componente ha un esito eseguito per la stessa funzionalita' (6.6.1, fase 2).</summary>
    /// (Con restrizioni per componente un altro veicolo fa da fonte solo se la funzionalita' vale anche su di lui, come ValeSu.)
    private static bool PropagabileNonTestato(Funzionalita f, Componente componente, string veicolo, HashSet<string> eseguite) =>
        !string.IsNullOrEmpty(veicolo) && componente.VeicoliAssociati.Any(altro =>
            !string.Equals(altro, veicolo, StringComparison.OrdinalIgnoreCase)
            && f.ValeSu(componente.Nome, altro)
            && eseguite.Contains(ChiaveCore(f.Nome, componente.Nome, altro)));

    /// <summary>Restrizione per componente di una funzionalita' (Funzionalita.IndiceVeicoliPerEcu) applicata a un veicolo: null = nessuna
    /// restrizione per quell'componente; veicolo vuoto (componente senza veicoli) = si verifica solo il componente, come Funzionalita.ValeSu.</summary>
    private static bool VeicoloAmmesso(HashSet<string>? ammessi, string veicolo) =>
        ammessi == null || string.IsNullOrEmpty(veicolo) || ammessi.Contains(veicolo);

    /// <summary>Come <see cref="EseguiteNonFiltrate"/>, ma il passaggio sui Test si fa solo se serve: <see cref="PropagabileNonTestato"/>
    /// consulta l'insieme solo per un componente con piu' veicoli distinti (con un solo veicolo non trova mai altro veicolo da confrontare).
    /// Senza nessuna componente del genere l'insieme vuoto dà lo stesso risultato, e il passaggio sui Test (un candidato per Test) si salta.</summary>
    private static HashSet<string> EseguiteSePropagabili(IEnumerable<TestCase> testCases, IReadOnlyList<Componente> ecuCatalogo) =>
        ecuCatalogo.Any(e => e.VeicoliAssociati.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            ? EseguiteNonFiltrate(testCases)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Combinazioni con almeno un esito eseguito in qualunque Test o valore manuale, filtri di vista esclusi: la
    /// propagabilita' dei "!" non dipende dai filtri (un veicolo nascosto dal filtro resta fonte, come nell'Audit).</summary>
    private static HashSet<string> EseguiteNonFiltrate(IEnumerable<TestCase> testCases)
    {
        var risultato = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in testCases)
        {
            if (tc.Eliminato) continue;
            foreach (var c in CreaCandidati(tc))
                if (c.Esito.HasValue && c.Esito.Value.EEseguito()) risultato.Add(ChiaveCore(c.Funzionalita, c.Componente, c.Veicolo));
        }
        return risultato;
    }

    /// <summary>Valore manuale del Master come Test sintetico (fase 2): un'unica esecuzione alla DataDecisione, con l'esito scelto.
    /// Entra nella stessa riduzione "ultimo esito" dei Test reali, senza modificare alcun Test.</summary>
    internal static TestCase CreaTestManuale(StatoFunzionalitaEcuVeicolo manuale)
    {
        var tc = new TestCase
        {
            Id = ManualePrefisso + manuale.Uid.ToString("N"),
            // Il Test sintetico viene ricreato nelle due scansioni dell'Origine: riusa l'identità master
            // così la chiave del candidato (e del vincitore) resta stabile tra le scansioni.
            Uid = manuale.Uid,
            Componente = new EcuSelection([manuale.Componente]),
            Veicoli = [manuale.Veicolo],
            Funzionalita = [manuale.Funzionalita],
            Descrizione = manuale.Nota,
            DescrizioneBreve = "Manuale: " + manuale.Operatore + ", " + manuale.DataDecisione.ToString("dd/MM/yyyy") + " — " + manuale.Nota,
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = manuale.DataDecisione,
            Operatore = manuale.Operatore,
            Note = manuale.Nota,
            Condizioni = [new Condizione { Uid = manuale.Uid, Funzionalita = manuale.Funzionalita, Esito = manuale.Esito, Componente = new EcuSelection([manuale.Componente]) }],
        });
        return tc;
    }

    public static MatricePreparata Prepara(IEnumerable<TestCase> testCases, ConfigurazioneMatriceReport config,
        IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita, IReadOnlyList<TestTemplate> templates,
        IReadOnlyList<Componente>? ecuCatalogo = null, IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manuali = null,
        bool aggregaUniverso = true, ReportFilter? filtro = null, CancellationToken ct = default, int? limiteUniversoExport = null)
    {
        ct.ThrowIfCancellationRequested();
        var gammaDi = veicoli.ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
        var funzioneDi = funzionalita.ToDictionary(f => f.Nome, StringComparer.OrdinalIgnoreCase);
        var templateDi = templates.ToDictionary(t => t.Uid);
        var modalitaStato = config.ModalitaCella == ModalitaCellaMatrice.Stato;
        var modalitaTestPercentuale = config.ModalitaCella == ModalitaCellaMatrice.TestPercentuale;

        if (!modalitaTestPercentuale && manuali is { Count: > 0 })
            testCases = testCases.Concat(manuali.Select(CreaTestManuale));

        var entitaTest = new Dictionary<Guid, EntitaTestPercentuale>();
        Action<CandidatoCombinazione>? raccogliTest = modalitaTestPercentuale
            ? candidato =>
            {
                if (candidato.Test.Origine == OrigineTest.ImportStatoUdt) return;
                if (!entitaTest.TryGetValue(candidato.Test.Uid, out var entita))
                {
                    entita = new EntitaTestPercentuale(StatoTestCase(candidato.Test));
                    entitaTest.Add(candidato.Test.Uid, entita);
                }
                entita.TestIds.Add(candidato.Test.Id);
                entita.TestUids.Add(candidato.Test.Uid);
                foreach (var x in ChiaviAsseCandidato(candidato, config.CampiX, gammaDi, funzioneDi, templateDi))
                foreach (var y in ChiaviAsseCandidato(candidato, config.CampiY, gammaDi, funzioneDi, templateDi))
                    entita.Celle.Add(string.Concat(x, "\u0001", y));
            }
            : null;

        // TIME "Ultimo stato": la stessa riduzione dello stato corrente produce anche il core dei soli tentativi dell'ultima revisione.
        var coreUltimo = ModalitaTempo(config.ModalitaCella) && config.BaseTempo == BaseTempoMatrice.UltimoStato
            ? new Dictionary<string, CombinazioneAccumulatore>(StringComparer.OrdinalIgnoreCase)
            : null;
        var (combinazioni, combinazioniCore) = CostruisciCombinazioni(
            testCases, config, gammaDi, funzioneDi, templateDi, raccogliTest, coreUltimo);
        // Universo "Non testato" (6.6.1): le combinazioni del catalogo senza alcun Test non hanno esiti ne' valori manuali, quindi
        // lo stato corrente e' gia' "Non testato" e non serve proiettarle una per una. Si enumerano sui soli core con Test, si
        // proiettano questi ultimi, e poi si aggiunge l'universo nello stesso ordine di prima (core con Test, poi universo).
        // Con un campo per asse il conteggio dell'universo e' aggregato (nessuna combinazione del catalogo materializzata): i totali
        // e le celle lo ricevono come contatori. Gli altri casi, e il caso con N.A., usano l'universo materializzato.
        // TIME: nelle modalita di Stato e Stato % il filtro sulla durata e' sulla combinazione vincente (vedi FiltraVincentePerDurata): una
        // combinazione non testata non ha un'operazione vincente con durata, quindi non corrisponde e l'universo "Non testato" non entra.
        // Nelle modalita temporali il filtro agisce per tentativo (o per vincente) nel calcolo dei valori, e le celle restano quelle dello stato.
        // Regola 6.7.0 (vedi UniversoAttivo): l'universo entra solo con criteri di identita'; il TIME lo spegne in ogni modalita'.
        var universoAttivo = UniversoAttivo(config, ecuCatalogo, filtro);
        var configUniverso = universoAttivo ? ConfigurazioneUniverso(config, filtro) : config; // componente e veicolo del Report restringono l'universo
        // TIME (Stato e Stato %): il filtro sulla durata agisce sulla combinazione vincente (vedi FiltraVincentePerDurata).
        var filtroDurataStato = !ModalitaTempo(config.ModalitaCella) ? config.FiltroTempoOperazione : null;
        var eseguite = universoAttivo ? EseguiteSePropagabili(testCases, ecuCatalogo!) : null;
        var coreTest = combinazioniCore; // pre-proiezione: ProiettaStatoCorrente non la modifica
        var universoAgg = universoAttivo && aggregaUniverso
            ? ConteggiUniversoNonTestato(configUniverso, ecuCatalogo!, eseguite!, veicoli, funzionalita, gammaDi, coreTest)
            : null;
        var universo = universoAttivo && universoAgg == null
            ? UniversoNonTestato(configUniverso, ecuCatalogo!, eseguite!, veicoli, funzionalita, gammaDi, coreTest, ct: ct, limite: limiteUniversoExport)
            : null;
        Func<string?, string?, List<VoceUniversoCelle>>? materializzaUniverso = universoAgg == null ? null
            : (riga, colonna) => UniversoCelle(UniversoNonTestato(configUniverso, ecuCatalogo!, eseguite!, veicoli, funzionalita, gammaDi,
                coreTest, AmmessoPerCella(configUniverso, gammaDi, riga, colonna)), configUniverso, gammaDi);
        Func<CancellationToken, IEnumerable<VoceUniversoCelle>>? enumeraUniversoExport = universoAgg == null ? null
            : ct => EnumeraUniversoNonTestato(configUniverso, ecuCatalogo!, eseguite!, veicoli, funzionalita, gammaDi, coreTest, ct: ct)
                .SelectMany(voce => UniversoCelle(new List<VoceUniversoNonTestato> { voce }, configUniverso, gammaDi));
        if (!modalitaTestPercentuale)
            (combinazioni, combinazioniCore) = ProiettaStatoCorrente(
                combinazioni, combinazioniCore, config, gammaDi, funzioneDi, templateDi);
        // TIME (6.6.1): Stato e Stato %: il filtro sulla durata si applica DOPO la riduzione allo stato corrente, alla sola operazione
        // vincente di ogni combinazione F x componente x veicolo (non a qualunque tentativo del Test). Test % usa il filtro a livello di Test.
        if (!modalitaTestPercentuale && filtroDurataStato is { } filtroDurataOperazione)
        {
            combinazioni = FiltraVincentePerDurata(combinazioni, filtroDurataOperazione);
            combinazioniCore = FiltraVincentePerDurata(combinazioniCore, filtroDurataOperazione);
        }
        if (universo != null) AggiungiUniversoNonTestato(universo, config, combinazioni, combinazioniCore);

        var colonneViste = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var perRiga = new Dictionary<string, Dictionary<string, CellaAccumulatore>>(StringComparer.OrdinalIgnoreCase);
        var perRigaTest = new Dictionary<string, Dictionary<string, CellaAccumulatore>>(StringComparer.OrdinalIgnoreCase);
        var celleToccatePerCore = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var acc in combinazioni.Values)
        {
            var valutata = acc.Eseguita;
            var conforme = ConformeCombinazione(acc);
            var stato = StatoDi(acc);
            var chiaveCore = ChiaveCore(acc.Funzionalita, acc.Componente, acc.Veicolo);
            var y = ChiaveAsse(acc, config.CampiY, gammaDi);
            var x = ChiaveAsse(acc, config.CampiX, gammaDi);
            if (!colonneViste.ContainsKey(x)) colonneViste[x] = x;
            if (!perRiga.TryGetValue(y, out var celleRiga))
            {
                celleRiga = new Dictionary<string, CellaAccumulatore>(StringComparer.OrdinalIgnoreCase);
                perRiga[y] = celleRiga;
            }
            if (!celleRiga.TryGetValue(x, out var cella))
            {
                cella = new CellaAccumulatore();
                celleRiga[x] = cella;
            }
            cella.Aggiungi(stato, valutata, conforme, acc.ManualeVincente, acc.ManualeSuperato, acc.NotaManuale, acc.ValoreManuale);

            if (!celleToccatePerCore.TryGetValue(chiaveCore, out var celle))
            {
                celle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                celleToccatePerCore[chiaveCore] = celle;
            }
            celle.Add(string.Concat(x, "\u0001", y));
        }

        if (universoAgg != null) AggiungiAssiUniverso(universoAgg, colonneViste, perRiga);
        if (modalitaTestPercentuale)
        {
            foreach (var entita in entitaTest.Values)
            foreach (var cellaChiave in entita.Celle)
            {
                var separatore = cellaChiave.IndexOf('\u0001');
                var x = cellaChiave[..separatore];
                var y = cellaChiave[(separatore + 1)..];
                if (!perRigaTest.TryGetValue(y, out var celleRiga))
                {
                    celleRiga = new Dictionary<string, CellaAccumulatore>(StringComparer.OrdinalIgnoreCase);
                    perRigaTest[y] = celleRiga;
                }
                if (!celleRiga.TryGetValue(x, out var cella))
                {
                    cella = new CellaAccumulatore();
                    celleRiga[x] = cella;
                }
                var valutata = entita.Stato is StatoCombinazioneMatrice.Conforme
                    or StatoCombinazioneMatrice.NonConforme or StatoCombinazioneMatrice.ConformeConDeviazioni;
                cella.Aggiungi(entita.Stato, valutata, entita.Stato == StatoCombinazioneMatrice.Conforme);
            }
        }

        var testIdsVista = modalitaTestPercentuale
            ? entitaTest.Values.SelectMany(e => e.TestIds).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : combinazioni.Values.Select(a => a.CandidatoTestRappresentante ?? a.Vincente)
                .Where(c => c != null).Select(c => c!.Test.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var testUidsVista = modalitaTestPercentuale
            ? entitaTest.Values.SelectMany(e => e.TestUids).ToHashSet()
            : combinazioni.Values.Select(a => a.CandidatoTestRappresentante ?? a.Vincente)
                .Where(c => c != null).Select(c => c!.Test.Uid).ToHashSet();
        var preparata = new MatricePreparata(perRiga, perRigaTest, colonneViste.Keys, perRiga.Keys,
            combinazioniCore, celleToccatePerCore, entitaTest,
            testIdsVista, testUidsVista, config.CampiX, config.CampiY, dimensione => OrdineDi(config, dimensione),
            modalitaStato, modalitaTestPercentuale, config.StatiNascostiPresentazione, universoAgg, materializzaUniverso, enumeraUniversoExport);
        preparata.ConfiguraVisibilita(config);
        // TIME: valori temporali con le stesse chiavi della matrice, solo nelle modalita' temporali (il costo resta fuori dallo Stato).
        if (ModalitaTempo(config.ModalitaCella))
        {
            var tempo = config.BaseTempo == BaseTempoMatrice.UltimoStato
                ? CalcolaTempoUltimoStato(CombinazioniUltimoStatoTempo(coreUltimo!, config, gammaDi, funzioneDi, templateDi), config, gammaDi)
                : CalcolaTempoTutteLeEsecuzioni(testCases, config, gammaDi, funzioneDi, templateDi);
            preparata.ImpostaTempo(tempo, config.ModalitaCella);
        }
        return preparata;
    }

    public static RisultatoMatriceReport Genera(IEnumerable<TestCase> testCases, ConfigurazioneMatriceReport config,
        IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita, IReadOnlyList<TestTemplate> templates,
        IReadOnlyList<CellaNonApplicabile>? marcature = null, IReadOnlyList<Componente>? ecuCatalogo = null,
        IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manuali = null, ReportFilter? filtro = null)
        => Prepara(testCases, config, veicoli, funzionalita, templates, ecuCatalogo, manuali, filtro: filtro).Completa(marcature);

    /// <summary>
    /// La vista Report usa ItemsControl annidati: con 1000 valori per asse materializzare un milione di
    /// CellaMatriceReport crea poi un milione di controlli Avalonia (feedback utente: pagina molto lenta).
    /// Accumuliamo comunque tutti i conteggi, ma proiettiamo solo la finestra visibile su ENTRAMBI gli assi.
    /// </summary>
    public static FinestraMatriceReport GeneraFinestra(IEnumerable<TestCase> testCases, ConfigurazioneMatriceReport config,
        IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita, IReadOnlyList<TestTemplate> templates,
        int primaRiga, int numeroRighe, int primaColonna, int numeroColonne,
        IReadOnlyList<CellaNonApplicabile>? marcature = null, IReadOnlyList<Componente>? ecuCatalogo = null,
        IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manuali = null)
    {
        return Prepara(testCases, config, veicoli, funzionalita, templates, ecuCatalogo, manuali)
            .Finestra(primaRiga, numeroRighe, primaColonna, numeroColonne, marcature, config.GruppiCompressi);
    }

    /// <summary>Aggregato sparso riutilizzabile fra i cambi di pagina: conserva solo celle realmente popolate, non
    /// l'archivio di Test clonato ne' la griglia densa da un milione di celle. Con più campi per asse le righe e le colonne
    /// sono annidate: le celle elementari (foglia × foglia) vengono dall'identità; i gruppi, gli assi senza campi e i totali
    /// sono aggregati per combinazione distinta (una volta sola, col suo ultimo esito).</summary>
    public sealed class MatricePreparata
    {
        private readonly Dictionary<string, Dictionary<string, CellaAccumulatore>> _perRiga;
        private readonly Dictionary<string, Dictionary<string, CellaAccumulatore>> _perRigaTest;
        private readonly Dictionary<string, CombinazioneAccumulatore> _combinazioniCore;
        private readonly Dictionary<string, HashSet<string>> _celleToccatePerCore;
        private readonly Dictionary<Guid, EntitaTestPercentuale> _entitaTestPercentuale;
        public IReadOnlySet<string> TestIdsVista { get; }
        public IReadOnlySet<Guid> TestUidsVista { get; }
        private readonly bool _modalitaStato;
        private readonly bool _modalitaTestPercentuale;
        private IReadOnlySet<StatoCellaMatrice> _statiNascostiPresentazione;
        private readonly IReadOnlyList<string> _dimensioniX;
        private readonly IReadOnlyList<string> _dimensioniY;
        private readonly Func<string, IReadOnlyList<string>?> _ordineDi;
        private StrutturaAsse _asseX;
        private StrutturaAsse _asseY;

        /// <summary>Cache dei totali e dei gruppi (revisione indipendente 2026-09-26): si ricalcola solo se la FIRMA delle
        /// marcature N.A. è cambiata; la paginazione pura riusa il risultato. Campi mutabili, mai letti/scritti da più thread
        /// contemporanei: una MatricePreparata è usata da un solo ViewModel/chiamata di export alla volta.</summary>
        private string? _firmaTotaliCache;
        private Dictionary<(string Y, string X), CellaAccumulatore>? _totaliCache;

        /// <summary>Universo "Non testato" aggregato (null se materializzato) e il suo materializzatore, per i soli casi che lo richiedono:
        /// totali con N.A. e dettaglio di una cella (vedi CalcolaTotaliSemplici e CombinazioniDi).</summary>
        private readonly UniversoAggregato? _universoAgg;
        private readonly Func<string?, string?, List<VoceUniversoCelle>>? _materializzaUniverso;
        private readonly Func<CancellationToken, IEnumerable<VoceUniversoCelle>>? _enumeraUniversoExport;
        private List<VoceUniversoCelle>? _universoCompleto;

        /// <summary>Indici delle chiavi dell'universo aggregato (per leggere i conteggi di una cella) e righe con almeno una cella da
        /// decidere dell'universo (per il filtro "Solo celle con !"). Null senza universo aggregato.</summary>
        private readonly Dictionary<string, int>? _indiceUniversoX;
        private readonly Dictionary<string, int>? _indiceUniversoY;
        private readonly HashSet<string>? _righeUniversoConDaDecidere;

        /// <summary>Valori temporali (TIME) con le stesse chiavi di riga e colonna della matrice; null fuori dalle modalità temporali.</summary>
        private RisultatoTempoMatrice? _tempo;

        private ModalitaCellaMatrice? _modalitaTempo;

        internal void ImpostaTempo(RisultatoTempoMatrice tempo, ModalitaCellaMatrice modalita)
        {
            _tempo = tempo;
            _modalitaTempo = modalita;
        }

        private ValoreTempo ValoreTempoDi(string riga, string colonna) => _tempo?.Valore(riga, colonna) ?? default;

        internal MatricePreparata(Dictionary<string, Dictionary<string, CellaAccumulatore>> perRiga,
            Dictionary<string, Dictionary<string, CellaAccumulatore>> perRigaTest,
            IEnumerable<string> chiaviX, IEnumerable<string> chiaviY,
            Dictionary<string, CombinazioneAccumulatore> combinazioniCore, Dictionary<string, HashSet<string>> celleToccatePerCore,
            Dictionary<Guid, EntitaTestPercentuale> entitaTestPercentuale,
            IReadOnlySet<string> testIdsVista,
            IReadOnlySet<Guid> testUidsVista,
            IReadOnlyList<string> dimensioniX, IReadOnlyList<string> dimensioniY,
            Func<string, IReadOnlyList<string>?> ordineDi, bool modalitaStato, bool modalitaTestPercentuale,
            IReadOnlySet<StatoCellaMatrice> statiNascostiPresentazione,
            UniversoAggregato? universoAgg = null, Func<string?, string?, List<VoceUniversoCelle>>? materializzaUniverso = null,
            Func<CancellationToken, IEnumerable<VoceUniversoCelle>>? enumeraUniversoExport = null)
        {
            _universoAgg = universoAgg;
            _materializzaUniverso = materializzaUniverso;
            _enumeraUniversoExport = enumeraUniversoExport;
            if (universoAgg != null)
            {
                _indiceUniversoX = IndiciUniverso(universoAgg.ChiaviX);
                _indiceUniversoY = IndiciUniverso(universoAgg.ChiaviY);
                _righeUniversoConDaDecidere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var nX = universoAgg.ChiaviX.Length;
                for (var y = 0; y < universoAgg.ChiaviY.Length; y++)
                    for (var x = 0; x < nX; x++)
                        if (universoAgg.DaDecidereCelle[y * nX + x] > 0)
                        {
                            _righeUniversoConDaDecidere.Add(universoAgg.ChiaviY[y]);
                            break;
                        }
            }
            _perRiga = perRiga;
            _perRigaTest = perRigaTest;
            _combinazioniCore = combinazioniCore;
            _celleToccatePerCore = celleToccatePerCore;
            _entitaTestPercentuale = entitaTestPercentuale;
            TestIdsVista = testIdsVista;
            TestUidsVista = testUidsVista;
            _dimensioniX = dimensioniX.ToList();
            _dimensioniY = dimensioniY.ToList();
            _ordineDi = ordineDi;
            _modalitaStato = modalitaStato;
            _modalitaTestPercentuale = modalitaTestPercentuale;
            _statiNascostiPresentazione = statiNascostiPresentazione;
            _asseX = StrutturaAsse.Costruisci(chiaviX, _dimensioniX, ordineDi);
            _asseY = StrutturaAsse.Costruisci(chiaviY, _dimensioniY, ordineDi);
        }

        /// <summary>Foglie (combinazioni complete) di riga e di colonna nell'ordine corrente, non solo la finestra visibile.</summary>
        public IReadOnlyList<string> TutteLeRighe => _asseY.Foglie;
        public IReadOnlyList<string> TutteLeColonne => _asseX.Foglie;

        /// <summary>Riapplica un ordine al PRIMO livello di ciascun asse senza ricostruire l'aggregato (null = alfabetico). Gli
        /// altri livelli usano l'ordine personale salvato per dimensione. Totali e conteggi non dipendono dall'ordine.</summary>
        public void Riordina(IReadOnlyList<string>? ordineColonne, IReadOnlyList<string>? ordineRighe)
        {
            _asseX = RiordinaAsse(_asseX, _dimensioniX, ordineColonne);
            _asseY = RiordinaAsse(_asseY, _dimensioniY, ordineRighe);
        }

        /// <summary>Applicabilita' requisito x componente derivata dai dati (null se non serve): celle N.A. e componente non coinvolte nascoste.</summary>
        private ApplicabilitaEcuMatrice? _applicabilitaEcu;

        /// <summary>Applica la visibilita' derivata dalla configurazione: (1) componente non applicabili a nessun requisito dell'altro asse
        /// nascoste; (2) con un filtro per stato, righe e colonne senza alcuna cella (non N.A.) negli stati scelti nascoste.
        /// Toglie solo foglie dagli assi: conteggi, totali e dati sorgente restano identici. Chiamata una volta in Prepara.</summary>
        internal void ConfiguraVisibilita(ConfigurazioneMatriceReport config)
        {
            _applicabilitaEcu = ApplicabilitaEcuMatrice.Crea(config);
            var applicabilita = _applicabilitaEcu;
            var stati = config.StatiFiltroRigheColonne;
            if (applicabilita == null && stati.Count == 0) return;

            var nascostiX = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nascostiY = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (applicabilita != null)
            {
                // Un componente senza alcun requisito applicabile nell'altro asse non e' coinvolta: fuori dalla matrice.
                if (applicabilita.EcuSuX && _asseX.Livelli > 0 && _asseY.Foglie.Count > 0)
                    foreach (var x in _asseX.Foglie)
                        if (_asseY.Foglie.All(y => applicabilita.NonApplicabile(x, y))) nascostiX.Add(x);
                if (!applicabilita.EcuSuX && _asseY.Livelli > 0 && _asseX.Foglie.Count > 0)
                    foreach (var y in _asseY.Foglie)
                        if (_asseX.Foglie.All(x => applicabilita.NonApplicabile(x, y))) nascostiY.Add(y);
            }

            if (stati.Count > 0)
            {
                bool Selezionato(StatiMatrice s) =>
                    (stati.Contains(StatoCellaMatrice.Conforme) && s.Ok > 0)
                    || (stati.Contains(StatoCellaMatrice.NonConforme) && s.NonConforme > 0)
                    || (stati.Contains(StatoCellaMatrice.ConformeConDeviazioni) && s.ConformeConDeviazioni > 0)
                    || (stati.Contains(StatoCellaMatrice.DaEseguire) && s.DaEseguire > 0)
                    || (stati.Contains(StatoCellaMatrice.NonTestato) && s.NonTestato > 0)
                    || (stati.Contains(StatoCellaMatrice.DaDecidere) && s.DaDecidere > 0);
                var righeConStato = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var colonneConStato = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (y, celleRiga) in _perRiga)
                    foreach (var (x, acc) in celleRiga)
                    {
                        if (applicabilita?.NonApplicabile(x, y) == true) continue;
                        if (!Selezionato(acc.Stati)) continue;
                        righeConStato.Add(y);
                        colonneConStato.Add(x);
                    }
                // Universo aggregato: le combinazioni non testate / da decidere non hanno celle materializzate.
                if (_universoAgg != null)
                {
                    var nt = stati.Contains(StatoCellaMatrice.NonTestato);
                    var dd = stati.Contains(StatoCellaMatrice.DaDecidere);
                    // Le righe/colonne le cui combinazioni sono tutte N.A. automatiche non contano come "non testate".
                    foreach (var (y, c) in _universoAgg.Righe)
                        if (((nt && c.Nt > 0) || (dd && c.Dd > 0))
                            && (applicabilita == null || !_asseX.Foglie.All(x => applicabilita.NonApplicabile(x, y)))) righeConStato.Add(y);
                    foreach (var (x, c) in _universoAgg.Colonne)
                        if (((nt && c.Nt > 0) || (dd && c.Dd > 0))
                            && (applicabilita == null || !_asseY.Foglie.All(y => applicabilita.NonApplicabile(x, y)))) colonneConStato.Add(x);
                }
                if (_asseY.Livelli > 0)
                    foreach (var y in _asseY.Foglie) if (!righeConStato.Contains(y)) nascostiY.Add(y);
                if (_asseX.Livelli > 0)
                    foreach (var x in _asseX.Foglie) if (!colonneConStato.Contains(x)) nascostiX.Add(x);
            }

            if (nascostiX.Count > 0)
                _asseX = StrutturaAsse.Costruisci(_asseX.Foglie.Where(f => !nascostiX.Contains(f)).ToList(), _dimensioniX, _ordineDi);
            if (nascostiY.Count > 0)
                _asseY = StrutturaAsse.Costruisci(_asseY.Foglie.Where(f => !nascostiY.Contains(f)).ToList(), _dimensioniY, _ordineDi);
        }

        /// <summary>Aggiorna solo quali etichette vengono proiettate nella vista; gli aggregati restano invariati.</summary>
        public void ImpostaStatiNascostiPresentazione(IReadOnlySet<StatoCellaMatrice> statiNascosti) =>
            _statiNascostiPresentazione = new HashSet<StatoCellaMatrice>(statiNascosti);

        private StrutturaAsse RiordinaAsse(StrutturaAsse asse, IReadOnlyList<string> dimensioni, IReadOnlyList<string>? ordinePrimoLivello) =>
            StrutturaAsse.Costruisci(asse.Foglie, dimensioni,
                dimensione => dimensioni.Count > 0 && dimensione == dimensioni[0] ? ordinePrimoLivello : _ordineDi(dimensione));

        /// <summary>Firma deterministica dell'insieme di marcature N.A. (revisione indipendente 2026-09-26): costruita
        /// ordinando le coppie (ValoreAsseX, ValoreAsseY). La Nota NON entra nella firma: non influenza i conteggi.</summary>
        private static string FirmaMarcature(IReadOnlyList<CellaNonApplicabile> marcature)
        {
            if (marcature.Count == 0) return "";
            var chiavi = new string[marcature.Count];
            for (var i = 0; i < marcature.Count; i++)
                chiavi[i] = string.Concat(marcature[i].ValoreAsseX, "\u0002", marcature[i].ValoreAsseY);
            Array.Sort(chiavi, StringComparer.OrdinalIgnoreCase);
            return string.Join("\u0001", chiavi);
        }

        /// <summary>Punto di ingresso cache-aware: ricalcola i totali e i gruppi solo se la firma delle marcature N.A. è cambiata.</summary>
        private Dictionary<(string Y, string X), CellaAccumulatore> CalcolaTotaliCacheAware(IReadOnlyList<CellaNonApplicabile>? marcature)
        {
            var lista = marcature ?? Array.Empty<CellaNonApplicabile>();
            var firma = FirmaMarcature(lista);
            if (_totaliCache != null && _firmaTotaliCache == firma)
            {
                PerfContatori.IncrementaTotaliMatriceRiusati();
                return _totaliCache;
            }
            PerfContatori.IncrementaTotaliMatriceRicalcolati();
            var naSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in lista) naSet.Add(string.Concat(m.ValoreAsseX, "\u0001", m.ValoreAsseY));
            var automatiche = _applicabilitaEcu;
            bool CellaNonApplicabile(string x, string y) =>
                naSet.Contains(string.Concat(x, "\u0001", y)) || (automatiche?.NonApplicabile(x, y) ?? false);
            var risultato = _modalitaTestPercentuale
                ? CalcolaTotaliTest(CellaNonApplicabile)
                : _asseX.Livelli == 1 && _asseY.Livelli == 1
                    ? CalcolaTotaliSemplici(CellaNonApplicabile, lista.Count == 0 && automatiche == null)
                    : CalcolaAggregati(CellaNonApplicabile);
            _totaliCache = risultato;
            _firmaTotaliCache = firma;
            return risultato;
        }


        private Dictionary<(string Y, string X), CellaAccumulatore> CalcolaTotaliTest(Func<string, string, bool> cellaNonApplicabile)
        {
            var risultato = new Dictionary<(string Y, string X), CellaAccumulatore>(ConfrontoCoppiaMatrice.Istanza);
            foreach (var entita in _entitaTestPercentuale.Values)
            {
                var coppieGiaContate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cellaChiave in entita.Celle)
                {
                    var separatore = cellaChiave.IndexOf('\u0001');
                    var x = cellaChiave[..separatore];
                    var y = cellaChiave[(separatore + 1)..];
                    if (cellaNonApplicabile(x, y)) continue;
                    var prefissiY = PrefissiPercorso(y, _asseY.Livelli);
                    var prefissiX = PrefissiPercorso(x, _asseX.Livelli);
                    for (var i = 0; i < prefissiY.Length; i++)
                    for (var j = 0; j < prefissiX.Length; j++)
                    {
                        var chiave = string.Concat(prefissiY[i], "\u0001", prefissiX[j]);
                        if (!coppieGiaContate.Add(chiave)) continue;
                        var coppia = (prefissiY[i], prefissiX[j]);
                        if (!risultato.TryGetValue(coppia, out var accumulatore))
                        {
                            accumulatore = new CellaAccumulatore();
                            risultato.Add(coppia, accumulatore);
                        }
                        var valutata = entita.Stato is StatoCombinazioneMatrice.Conforme
                            or StatoCombinazioneMatrice.NonConforme or StatoCombinazioneMatrice.ConformeConDeviazioni;
                        accumulatore.Aggiungi(entita.Stato, valutata, entita.Stato == StatoCombinazioneMatrice.Conforme);
                    }
                }
            }
            return risultato;
        }

        /// <summary>Totali con un solo campo per asse (percorso invariato dalla 6.4.0): per ogni combinazione CORE, conta una
        /// volta per colonna, per riga e nel generale, se almeno una delle sue celle non è N.A. Chiavi: ("", colonna) per le
        /// colonne, (riga, "") per le righe, ("", "") per il generale.</summary>
        private Dictionary<(string Y, string X), CellaAccumulatore> CalcolaTotaliSemplici(Func<string, string, bool> cellaNonApplicabile,
            bool senzaNonApplicabili)
        {
            var contatoriColonna = new Dictionary<string, CellaAccumulatore>(StringComparer.OrdinalIgnoreCase);
            var contatoriRiga = new Dictionary<string, CellaAccumulatore>(StringComparer.OrdinalIgnoreCase);
            var generale = new CellaAccumulatore();

            // Universo aggregato senza N.A.: entra come contatori alla fine. Con N.A. ogni combinazione del catalogo conta da sola
            // (regola per combinazione): si uniscono quelle materializzate, come i core con Test.
            IEnumerable<KeyValuePair<string, HashSet<string>>> celleDaScorrere = _celleToccatePerCore;
            IReadOnlyDictionary<string, CombinazioneAccumulatore> coreDaScorrere = _combinazioniCore;
            if (_universoAgg != null && !senzaNonApplicabili && _materializzaUniverso != null)
            {
                _universoCompleto ??= _materializzaUniverso(null, null);
                var celleUnite = new Dictionary<string, HashSet<string>>(_celleToccatePerCore, StringComparer.OrdinalIgnoreCase);
                var coreUniti = new Dictionary<string, CombinazioneAccumulatore>(_combinazioniCore, StringComparer.OrdinalIgnoreCase);
                foreach (var voce in _universoCompleto)
                {
                    celleUnite[voce.Chiave] = voce.Celle;
                    coreUniti[voce.Chiave] = voce.Acc;
                }
                celleDaScorrere = celleUnite;
                coreDaScorrere = coreUniti;
            }

            foreach (var (chiaveCore, celle) in celleDaScorrere)
            {
                if (!coreDaScorrere.TryGetValue(chiaveCore, out var acc)) continue; // difensivo: non dovrebbe accadere
                var valutata = acc.Eseguita;
                var conforme = ConformeCombinazione(acc);
                var stato = StatoDi(acc);

                var righeToccatePerColonna = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var colonneToccatePerRiga = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var cellaChiave in celle)
                {
                    var separatore = cellaChiave.IndexOf('\u0001');
                    var x = cellaChiave[..separatore];
                    var y = cellaChiave[(separatore + 1)..];
                    if (!righeToccatePerColonna.TryGetValue(x, out var righeX)) { righeX = new List<string>(); righeToccatePerColonna[x] = righeX; }
                    righeX.Add(y);
                    if (!colonneToccatePerRiga.TryGetValue(y, out var colonneY)) { colonneY = new List<string>(); colonneToccatePerRiga[y] = colonneY; }
                    colonneY.Add(x);
                }

                var qualcheCellaNonNaNellaMatrice = false;
                foreach (var (x, righeX) in righeToccatePerColonna)
                {
                    if (!righeX.Any(y => !cellaNonApplicabile(x, y))) continue; // tutte N.A. in questa colonna: esclusa (punto 3)
                    qualcheCellaNonNaNellaMatrice = true;
                    if (!contatoriColonna.TryGetValue(x, out var c)) { c = new CellaAccumulatore(); contatoriColonna[x] = c; }
                    c.Aggiungi(stato, valutata, conforme, acc.ManualeVincente, acc.ManualeSuperato, acc.NotaManuale);
                }
                foreach (var (y, colonneY) in colonneToccatePerRiga)
                {
                    if (!colonneY.Any(x => !cellaNonApplicabile(x, y))) continue;
                    if (!contatoriRiga.TryGetValue(y, out var c)) { c = new CellaAccumulatore(); contatoriRiga[y] = c; }
                    c.Aggiungi(stato, valutata, conforme, acc.ManualeVincente, acc.ManualeSuperato, acc.NotaManuale);
                }
                if (qualcheCellaNonNaNellaMatrice) generale.Aggiungi(stato, valutata, conforme, acc.ManualeVincente, acc.ManualeSuperato, acc.NotaManuale);
            }
            if (_universoAgg != null && senzaNonApplicabili)
                AggiungiUniversoAggregatoAiTotali(_universoAgg, contatoriColonna, contatoriRiga, generale);

            var risultato = new Dictionary<(string Y, string X), CellaAccumulatore>(ConfrontoCoppiaMatrice.Istanza);
            foreach (var (x, c) in contatoriColonna) risultato[("", x)] = c;
            foreach (var (y, c) in contatoriRiga) risultato[(y, "")] = c;
            risultato[("", "")] = generale;
            return risultato;
        }

        /// <summary>Accumulatore di una coppia (riga, colonna) aggregata, con l'ultima combinazione già contata (per non
        /// contare due volte la stessa combinazione nella stessa coppia).</summary>
        private static bool ContieneCella(string cellaChiave, string riga, string colonna)
        {
            var separatore = cellaChiave.IndexOf('\u0001');
            return InPercorso(colonna, cellaChiave[..separatore]) && InPercorso(riga, cellaChiave[(separatore + 1)..]);
        }

        /// <summary>Totali di colonna, riga e generale dell'universo aggregato (senza N.A.): stessi contatori che la regola per
        /// combinazione produrrebbe, una volta per combinazione e per riga/colonna che tocca.</summary>
        private static void AggiungiUniversoAggregatoAiTotali(UniversoAggregato universo, Dictionary<string, CellaAccumulatore> colonne,
            Dictionary<string, CellaAccumulatore> righe, CellaAccumulatore generale)
        {
            foreach (var (x, (nt, dd)) in universo.Colonne)
            {
                if (!colonne.TryGetValue(x, out var c)) { c = new CellaAccumulatore(); colonne[x] = c; }
                AggiungiConteggiUniverso(c, nt, dd);
            }
            foreach (var (y, (nt, dd)) in universo.Righe)
            {
                if (!righe.TryGetValue(y, out var c)) { c = new CellaAccumulatore(); righe[y] = c; }
                AggiungiConteggiUniverso(c, nt, dd);
            }
            AggiungiConteggiUniverso(generale, universo.Generale.Nt, universo.Generale.Dd);
        }

        private sealed class AccumulatoCoppia
        {
            public readonly CellaAccumulatore Cella = new();
            public int UltimoCore;
        }

        /// <summary>Aggregati con più campi: per ogni combinazione CORE e per ogni cella elementare in cui compare (non N.A.),
        /// conta una volta in ciascuna coppia (antenato o sé stesso sull'asse Y, antenato o sé stesso sull'asse X), escludendo la
        /// coppia foglia × foglia (cella elementare, dall'identità). Copre gruppi, totali di riga/colonna e generale.</summary>
        private Dictionary<(string Y, string X), CellaAccumulatore> CalcolaAggregati(Func<string, string, bool> cellaNonApplicabile)
        {
            var coppie = new Dictionary<(string Y, string X), AccumulatoCoppia>(ConfrontoCoppiaMatrice.Istanza);
            var livelliY = _asseY.Livelli;
            var livelliX = _asseX.Livelli;
            var indiceCore = 0;
            foreach (var (chiaveCore, celle) in _celleToccatePerCore)
            {
                if (!_combinazioniCore.TryGetValue(chiaveCore, out var acc)) continue;
                indiceCore++;
                var valutata = acc.Eseguita;
                var conforme = ConformeCombinazione(acc);
                var stato = StatoDi(acc);
                foreach (var cellaChiave in celle)
                {
                    var separatore = cellaChiave.IndexOf('\u0001');
                    var x = cellaChiave[..separatore];
                    var y = cellaChiave[(separatore + 1)..];
                    if (cellaNonApplicabile(x, y)) continue; // N.A.: la combinazione non entra nei totali/gruppi di questa cella
                    var prefissiY = PrefissiPercorso(y, livelliY);
                    var prefissiX = PrefissiPercorso(x, livelliX);
                    for (var i = 0; i < prefissiY.Length; i++)
                    {
                        for (var j = 0; j < prefissiX.Length; j++)
                        {
                            if (livelliY > 0 && livelliX > 0 && i == livelliY && j == livelliX) continue; // foglia × foglia: identità
                            var coppia = (prefissiY[i], prefissiX[j]);
                            if (!coppie.TryGetValue(coppia, out var accumulato))
                            {
                                accumulato = new AccumulatoCoppia();
                                coppie[coppia] = accumulato;
                            }
                            if (accumulato.UltimoCore == indiceCore) continue; // già contata per questa coppia
                            accumulato.UltimoCore = indiceCore;
                            accumulato.Cella.Aggiungi(stato, valutata, conforme, acc.ManualeVincente, acc.ManualeSuperato, acc.NotaManuale);
                        }
                    }
                }
            }
            var risultato = new Dictionary<(string Y, string X), CellaAccumulatore>(ConfrontoCoppiaMatrice.Istanza);
            foreach (var (coppia, accumulato) in coppie) risultato[coppia] = accumulato.Cella;
            return risultato;
        }

        /// <summary>Finestra visuale: totali sempre sull'intera matrice filtrata (punto 4 dell'addendum). Le righe e le colonne
        /// visibili seguono i gruppi compressi: foglie dei gruppi espansi più i nodi aggregati dei gruppi compressi.</summary>
        public (IReadOnlyList<string> Righe, int TotaleRighe, IReadOnlyList<string> Colonne, int TotaleColonne) AssiFinestra(
            int primaRiga, int numeroRighe, int primaColonna, int numeroColonne,
            IReadOnlySet<string>? gruppiCompressi = null, bool soloConDaDecidere = false)
        {
            if (primaRiga < 0 || primaColonna < 0 || numeroRighe < 1 || numeroColonne < 1)
                throw new ArgumentOutOfRangeException(nameof(primaRiga), "La finestra della matrice deve essere positiva.");
            var (vociColonne, totaleColonne) = _asseX.Finestra(gruppiCompressi, "X:", primaColonna, numeroColonne);
            var (vociRighe, totaleRighe) = soloConDaDecidere
                ? FinestraConDaDecidere(primaRiga, numeroRighe)
                : _asseY.Finestra(gruppiCompressi, "Y:", primaRiga, numeroRighe);
            return (vociRighe.Select(v => v.Chiave).ToList(), totaleRighe,
                vociColonne.Select(v => v.Chiave).ToList(), totaleColonne);
        }

        public FinestraMatriceReport Finestra(int primaRiga, int numeroRighe, int primaColonna, int numeroColonne,
            IReadOnlyList<CellaNonApplicabile>? marcature = null, IReadOnlySet<string>? gruppiCompressi = null,
            bool soloConDaDecidere = false)
        {
            if (primaRiga < 0 || primaColonna < 0 || numeroRighe < 1 || numeroColonne < 1)
                throw new ArgumentOutOfRangeException(nameof(primaRiga), "La finestra della matrice deve essere positiva.");
            var (vociColonne, totaleColonne) = _asseX.Finestra(gruppiCompressi, "X:", primaColonna, numeroColonne);
            var (vociRighe, totaleRighe) = soloConDaDecidere
                ? FinestraConDaDecidere(primaRiga, numeroRighe)
                : _asseY.Finestra(gruppiCompressi, "Y:", primaRiga, numeroRighe);
            var totali = CalcolaTotaliCacheAware(marcature);
            var righe = vociRighe.Select(riga => new RigaMatriceReport
            {
                Riga = riga.Chiave,
                Livello = riga.Profondita,
                Aggregata = riga.Aggregata,
                Compressa = riga.Compressa,
                Celle = vociColonne.Select(colonna => CreaCella(riga, colonna, totali)).ToList(),
                TotaleRiga = TotaleDi(totali, riga.Chiave, "", TotaleMatriceReport.AutomationIdRiga(riga.Chiave)),
            }).ToList();
            var risultato = new RisultatoMatriceReport
            {
                Colonne = vociColonne.Select(c => c.Chiave).ToList(),
                Righe = righe,
                TotaliColonna = vociColonne.Select(c => TotaleDi(totali, "", c.Chiave, TotaleMatriceReport.AutomationIdColonna(c.Chiave))).ToList(),
                TotaleGenerale = TotaleDi(totali, "", "", TotaleMatriceReport.AutomationIdGenerale),
                FasceColonne = StrutturaAsse.CostruisciFasce(vociColonne, _asseX.Livelli),
                FasceRighe = StrutturaAsse.CostruisciFasce(vociRighe, _asseY.Livelli),
            };
            return new FinestraMatriceReport(risultato, totaleRighe, totaleColonne);
        }

        /// <summary>Filtro rapido del Master "Solo celle con !": solo le righe foglia con almeno una cella da decidere.</summary>
        private (List<VoceAsseMatrice> Voci, int Totale) FinestraConDaDecidere(int primo, int numero)
        {
            var voci = new List<VoceAsseMatrice>();
            for (var i = 0; i < _asseY.Foglie.Count; i++)
            {
                var riga = _asseY.Foglie[i];
                if ((_perRiga.TryGetValue(riga, out var celle) && celle.Values.Any(c => c.DaDecidere > 0))
                    || (_righeUniversoConDaDecidere?.Contains(riga) ?? false))
                    voci.Add(_asseY.VoceFoglia(i));
            }
            return (voci.Skip(primo).Take(numero).ToList(), voci.Count);
        }

        /// <summary>Conteggi dell'universo aggregato di una cella (riga, colonna): (0, 0) se la cella non ha combinazioni dell'universo.</summary>
        private (int NonTestati, int DaDecidere) UniversoCella(string riga, string colonna)
        {
            if (_universoAgg == null || _indiceUniversoY == null || _indiceUniversoX == null) return (0, 0);
            if (!_indiceUniversoY.TryGetValue(riga, out var y) || !_indiceUniversoX.TryGetValue(colonna, out var x)) return (0, 0);
            var i = y * _universoAgg.ChiaviX.Length + x;
            return (_universoAgg.NonTestatiCelle[i], _universoAgg.DaDecidereCelle[i]);
        }

        private static StatiMatrice ConUniverso(StatiMatrice stati, int nonTestati, int daDecidere) =>
            stati with { NonTestato = stati.NonTestato + nonTestati, DaDecidere = stati.DaDecidere + daDecidere };

        private static Dictionary<string, int> IndiciUniverso(IReadOnlyList<string> chiavi)
        {
            var indici = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < chiavi.Count; i++) indici.TryAdd(chiavi[i], i);
            return indici;
        }

        private CellaMatriceReport CreaCella(VoceAsseMatrice riga, VoceAsseMatrice colonna,
            Dictionary<(string Y, string X), CellaAccumulatore> totali)
        {
            var marcabile = !riga.Compressa && !colonna.Compressa;
            if (!riga.Aggregata && !colonna.Aggregata)
            {
                CellaAccumulatore? accStato = null;
                if (_perRiga.TryGetValue(riga.Chiave, out var celleRigaStato))
                    celleRigaStato.TryGetValue(colonna.Chiave, out accStato);

                CellaAccumulatore? accPercentuale = accStato;
                if (_modalitaTestPercentuale)
                {
                    accPercentuale = null;
                    if (_perRigaTest.TryGetValue(riga.Chiave, out var celleRigaTest))
                        celleRigaTest.TryGetValue(colonna.Chiave, out accPercentuale);
                }

                // Universo aggregato: le combinazioni non testate e da decidere della cella si sommano qui (nessuna cella materializzata).
                var (nonTestati, daDecidere) = UniversoCella(riga.Chiave, colonna.Chiave);
                var stati = accStato?.Stati ?? default;
                var statiPercentuale = accPercentuale?.Stati ?? default;
                var totaleCella = accPercentuale?.Totali ?? 0;
                if (nonTestati != 0 || daDecidere != 0)
                {
                    stati = ConUniverso(stati, nonTestati, daDecidere);
                    statiPercentuale = ConUniverso(statiPercentuale, nonTestati, daDecidere);
                    totaleCella += nonTestati + daDecidere;
                }

                var naAutomatica = marcabile && _applicabilitaEcu?.NonApplicabile(colonna.Chiave, riga.Chiave) == true;
                return new CellaMatriceReport
                {
                    NonApplicabile = naAutomatica,
                    NotaNonApplicabile = naAutomatica
                        ? LocalizationService.Instance.Scegli("Requisito non relativo a questa componente", "Requirement not related to this component", "Exigence non liée à cette componente")
                        : null,
                    StatiNascostiPresentazione = _statiNascostiPresentazione,
                    Riga = riga.Chiave, Colonna = colonna.Chiave,
                    Totali = totaleCella, Valutati = accPercentuale?.Valutati ?? 0,
                    Conformi = accPercentuale?.Conformi ?? 0,
                    Stati = stati,
                    StatiPercentuale = statiPercentuale,
                    ModalitaStato = _modalitaStato, MostraConteggioTest = _modalitaTestPercentuale, Marcabile = marcabile,
                    Tempo = ValoreTempoDi(riga.Chiave, colonna.Chiave), ModalitaTempo = _modalitaTempo,
                    Manuali = accStato?.Manuali ?? 0, ManualiSuperati = accStato?.ManualiSuperati ?? 0,
                    NotaManuale = accStato?.NotaManuale, ValoriManuali = accStato?.ValoriManuali ?? [],
                };
            }

            totali.TryGetValue((riga.Chiave, colonna.Chiave), out var aggregato);
            return new CellaMatriceReport
            {
                StatiNascostiPresentazione = _statiNascostiPresentazione,
                Riga = riga.Chiave, Colonna = colonna.Chiave,
                Totali = aggregato?.Totali ?? 0, Valutati = aggregato?.Valutati ?? 0,
                Conformi = aggregato?.Conformi ?? 0, Stati = aggregato?.Stati ?? default,
                StatiPercentuale = aggregato?.Stati ?? default,
                ModalitaStato = _modalitaStato, MostraConteggioTest = _modalitaTestPercentuale,
                Aggregata = true, Marcabile = marcabile,
                Tempo = ValoreTempoDi(riga.Chiave, colonna.Chiave), ModalitaTempo = _modalitaTempo,
                Manuali = aggregato?.Manuali ?? 0, ManualiSuperati = aggregato?.ManualiSuperati ?? 0,
                NotaManuale = aggregato?.NotaManuale, ValoriManuali = aggregato?.ValoriManuali ?? [],
            };
        }

        private TotaleMatriceReport TotaleDi(Dictionary<(string Y, string X), CellaAccumulatore> totali, string riga, string colonna, string automationId)
        {
            if (!totali.TryGetValue((riga, colonna), out var t))
                return new TotaleMatriceReport { StatiNascostiPresentazione = _statiNascostiPresentazione, AutomationId = automationId, MostraConteggioTest = _modalitaTestPercentuale, Tempo = ValoreTempoDi(riga, colonna), ModalitaTempo = _modalitaTempo };
            return new TotaleMatriceReport
            {
                StatiNascostiPresentazione = _statiNascostiPresentazione,
                Conformi = t.Conformi, Valutati = t.Valutati, Stati = t.Stati,
                StatiPercentuale = t.Stati, MostraConteggioTest = _modalitaTestPercentuale, AutomationId = automationId,
                Tempo = ValoreTempoDi(riga, colonna), ModalitaTempo = _modalitaTempo,
            };
        }

        /// <summary>Combinazioni che concorrono a una cella della vista (anche aggregata): elenco per il pannello di correzione del Master.</summary>
        public IReadOnlyList<CombinazioneCellaMatrice> CombinazioniDi(string riga, string colonna)
        {
            var risultato = new List<CombinazioneCellaMatrice>();
            foreach (var (chiaveCore, celle) in _celleToccatePerCore)
            {
                if (!_combinazioniCore.TryGetValue(chiaveCore, out var acc)) continue;
                var contiene = false;
                foreach (var cellaChiave in celle)
                {
                    var separatore = cellaChiave.IndexOf('\u0001');
                    var x = cellaChiave[..separatore];
                    var y = cellaChiave[(separatore + 1)..];
                    if (InPercorso(colonna, x) && InPercorso(riga, y)) { contiene = true; break; }
                }
                if (contiene)
                    risultato.Add(new CombinazioneCellaMatrice(acc.Funzionalita ?? "", acc.Componente, acc.Veicolo, StatoDi(acc),
                        acc.ManualeVincente, acc.ManualeSuperato, acc.Propagabile));
            }
            // Universo aggregato: le combinazioni del catalogo della cella, materializzate solo per questa cella.
            if (_universoAgg != null && _materializzaUniverso != null)
            {
                foreach (var voce in _materializzaUniverso(riga, colonna))
                {
                    if (!voce.Celle.Any(c => ContieneCella(c, riga, colonna))) continue;
                    risultato.Add(new CombinazioneCellaMatrice(voce.Acc.Funzionalita ?? "", voce.Acc.Componente, voce.Acc.Veicolo, StatoDi(voce.Acc),
                        voce.Acc.ManualeVincente, voce.Acc.ManualeSuperato, voce.Acc.Propagabile));
                }
            }
            return risultato;
        }

        /// <summary>Combinazioni e celle applicabili della vista, per costruire una sorgente pivot senza ripetere i tentativi.</summary>
        public IEnumerable<(CombinazioneCellaMatrice Combinazione, IReadOnlyList<string> Celle)> CombinazioniPerExport(
            IReadOnlyList<CellaNonApplicabile>? marcature, CancellationToken ct)
        {
            var na = new HashSet<string>((marcature ?? Array.Empty<CellaNonApplicabile>())
                .Select(m => string.Concat(m.ValoreAsseX, "\u0001", m.ValoreAsseY)), StringComparer.OrdinalIgnoreCase);
            IEnumerable<(CombinazioneCellaMatrice, IReadOnlyList<string>)> Aggiungi(CombinazioneAccumulatore acc, IEnumerable<string> celle)
            {
                ct.ThrowIfCancellationRequested();
                var automatiche = _applicabilitaEcu;
                var applicabili = celle.Where(c => !na.Contains(c) && !(automatiche?.NonApplicabileChiave(c) ?? false)).ToArray();
                if (applicabili.Length == 0) yield break;
                yield return (new CombinazioneCellaMatrice(acc.Funzionalita ?? "", acc.Componente, acc.Veicolo, StatoDi(acc),
                    acc.ManualeVincente, acc.ManualeSuperato, acc.Propagabile), applicabili);
            }
            foreach (var (chiave, celle) in _celleToccatePerCore)
                if (_combinazioniCore.TryGetValue(chiave, out var acc))
                    foreach (var riga in Aggiungi(acc, celle)) yield return riga;
            if (_enumeraUniversoExport != null)
                foreach (var voce in _enumeraUniversoExport(ct))
                    foreach (var riga in Aggiungi(voce.Acc, voce.Celle)) yield return riga;
        }

        /// <summary>La chiave foglia sta sotto il nodo (o e' il nodo stesso); il nodo radice "" contiene tutto.</summary>
        private static bool InPercorso(string nodo, string foglia) =>
            nodo.Length == 0 || string.Equals(nodo, foglia, StringComparison.OrdinalIgnoreCase)
            || foglia.StartsWith(nodo + SepLivello, StringComparison.OrdinalIgnoreCase);

        public RisultatoMatriceReport Completa(IReadOnlyList<CellaNonApplicabile>? marcature = null) =>
            Finestra(0, int.MaxValue, 0, int.MaxValue, marcature).Risultato;

        /// <summary>Solo il totale generale di <see cref="Completa"/> (stessi totali cache-aware, stessa riduzione), senza costruire
        /// righe, colonne e celle: per le viste che leggono il solo totale, come la distribuzione di conformita' della Home.</summary>
        public TotaleMatriceReport CalcolaTotaleGenerale(IReadOnlyList<CellaNonApplicabile>? marcature = null) =>
            TotaleDi(CalcolaTotaliCacheAware(marcature), "", "", TotaleMatriceReport.AutomationIdGenerale);

    }

    /// <summary>Origine della vista con contratto storico: <paramref name="massimoRighe"/> è il tetto complessivo
    /// delle righe; tutti i vincitori sono comunque inclusi anche se superano il tetto. Per il nuovo
    /// comportamento usare <see cref="OrigineConLimiteRigheAggiuntive"/> che applica il limite solo alle righe extra.
    /// La matrice è ricalcolata in due passate: la prima determina gli stati/vincitori, la seconda emette righe in streaming.</summary>
    public static (IEnumerable<OrigineDato> Righe, int Mostrate, int Totale) Origine(IEnumerable<TestCase> testCases,
        ConfigurazioneMatriceReport config, IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita,
        IReadOnlyList<TestTemplate> templates, int massimoRighe, IReadOnlyList<CellaNonApplicabile>? marcature = null,
        IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manuali = null, CancellationToken ct = default) =>
        OrigineInterna(testCases, config, veicoli, funzionalita, templates, massimoRighe, limiteSoloAggiuntive: false, marcature, manuali, ct);

    /// <summary>Export completo della selezione filtrata con tutte le righe che determinano lo stato più il numero richiesto di righe aggiuntive.</summary>
    public static (IEnumerable<OrigineDato> Righe, int Mostrate, int Totale) OrigineConLimiteRigheAggiuntive(IEnumerable<TestCase> testCases,
        ConfigurazioneMatriceReport config, IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita,
        IReadOnlyList<TestTemplate> templates, int limiteRigheAggiuntive, IReadOnlyList<CellaNonApplicabile>? marcature = null,
        IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manuali = null, CancellationToken ct = default) =>
        OrigineInterna(testCases, config, veicoli, funzionalita, templates, limiteRigheAggiuntive, limiteSoloAggiuntive: true, marcature, manuali, ct);

    private static (IEnumerable<OrigineDato> Righe, int Mostrate, int Totale) OrigineInterna(IEnumerable<TestCase> testCases,
        ConfigurazioneMatriceReport config, IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita,
        IReadOnlyList<TestTemplate> templates, int massimoRighe, bool limiteSoloAggiuntive,
        IReadOnlyList<CellaNonApplicabile>? marcature, IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manuali, CancellationToken ct)
    {
        var gammaDi = veicoli.ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
        var funzioneDi = funzionalita.ToDictionary(f => f.Nome, StringComparer.OrdinalIgnoreCase);
        var templateDi = templates.ToDictionary(t => t.Uid);
        if (massimoRighe < 0) throw new ArgumentOutOfRangeException(nameof(massimoRighe));
        var totale = 0;
        var modalitaStatoCorrente = config.ModalitaCella != ModalitaCellaMatrice.TestPercentuale;
        var esitiCorrenti = FiltroEsitoStatoCorrente(config);
        var filtriCorrenti = config.Filtri
            .Where(f => f.Key != DimensioniMatrice.Esito)
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        var candidatiPerCore = modalitaStatoCorrente ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : null;
        // I filtri non-core selezionano le righe aggiuntive dell'Origine; il filtro Esito si applica
        // invece alla proiezione di stato (o al tentativo TIME) tramite il reducer dedicato.
        var filtriAggiuntive = config.Filtri
            .Where(f => f.Key != DimensioniMatrice.Esito)
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        var modalitaTestPercentuale = config.ModalitaCella == ModalitaCellaMatrice.TestPercentuale;
        var modalitaTempo = ModalitaTempo(config.ModalitaCella);
        var tempoTutteEsecuzioni = modalitaTempo && config.BaseTempo == BaseTempoMatrice.TutteLeEsecuzioni;
        bool EContributoreTempo(CandidatoCombinazione c)
        {
            if (!tempoTutteEsecuzioni) return true;
            if (c.Test.Origine == OrigineTest.ImportStatoUdt || c.Test.Id.StartsWith(ManualePrefisso, StringComparison.Ordinal)
                || !c.UltimaOccorrenza || c.Esito is not { } esito || !esito.EEseguito()
                || config.FiltroTempoOperazione is { } filtroDurata && !filtroDurata.Corrisponde(c.DurataMinuti)
                || !PassaFiltri(c, filtriAggiuntive, gammaDi, funzioneDi, templateDi))
                return false;
            var accTempo = new CombinazioneAccumulatore(c, ValoriEstraVuoti);
            return esitiCorrenti == null || PassaFiltroEsitoCorrente(accTempo, esitiCorrenti);
        }
        var coreUltimeOccorrenze = modalitaTempo
            ? new Dictionary<string, CombinazioneAccumulatore>(StringComparer.OrdinalIgnoreCase)
            : null;
        var contributiTempoPerCore = modalitaTempo && config.BaseTempo == BaseTempoMatrice.TutteLeEsecuzioni
            ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            : null;
        if (manuali != null && config.ModalitaCella != ModalitaCellaMatrice.TestPercentuale && manuali.Count > 0)
            testCases = testCases.Concat(manuali.Select(CreaTestManuale));
        var (_, core) = CostruisciCombinazioni(testCases, config, gammaDi, funzioneDi, templateDi, c =>
        {
            if ((totale & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            if (modalitaTestPercentuale && c.Test.Origine == OrigineTest.ImportStatoUdt) return;
            totale++;
            if (contributiTempoPerCore != null && EContributoreTempo(c))
            {
                var coreKey = ChiaveCore(c.Funzionalita, c.Componente, c.Veicolo);
                if (!contributiTempoPerCore.TryGetValue(coreKey, out var keys))
                    contributiTempoPerCore[coreKey] = keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                keys.Add(ChiaveCandidato(c));
            }
            if (candidatiPerCore != null)
            {
                if (PassaFiltri(c, filtriAggiuntive, gammaDi, funzioneDi, templateDi)
                    && (!tempoTutteEsecuzioni || EContributoreTempo(c)))
                {
                    var chiaveCore = ChiaveCore(c.Funzionalita, c.Componente, c.Veicolo);
                    candidatiPerCore[chiaveCore] = candidatiPerCore.GetValueOrDefault(chiaveCore) + 1;
                }
            }
        }, coreUltimeOccorrenze);

        HashSet<string>? coreAmmessi = null;
        if (modalitaStatoCorrente)
        {
            // TIME: stessa regola della matrice in Stato e Stato %: la combinazione entra solo se la sua operazione vincente ha una durata
            // che corrisponde (vedi FiltraVincentePerDurata). Nelle modalita temporali il filtro e' per tentativo, non qui.
            var filtroDurataStato = !ModalitaTempo(config.ModalitaCella) ? config.FiltroTempoOperazione : null;
            coreAmmessi = core.Where(kv =>
                {
                    if (!PassaFiltroEsitoCorrente(kv.Value, esitiCorrenti)) return false;
                    if (filtroDurataStato is { } durata && (!kv.Value.Eseguita || kv.Value.ManualeVincente
                        || kv.Value.Vincente is not { } vincente || !durata.Corrisponde(vincente.DurataMinuti))) return false;
                    var rappresentante = kv.Value.CandidatoTestRappresentante ?? kv.Value.Vincente;
                    return rappresentante == null || PassaFiltri(rappresentante, filtriCorrenti, gammaDi, funzioneDi, templateDi);
                })
                .Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            totale = coreAmmessi.Sum(chiave => candidatiPerCore!.GetValueOrDefault(chiave));
            if (contributiTempoPerCore != null)
                foreach (var key in contributiTempoPerCore.Keys.Where(k => !coreAmmessi.Contains(k)).ToList())
                    contributiTempoPerCore.Remove(key);
        }

        // Vincitori: uno per combinazione core, identificati per RIFERIMENTO (due candidati con stesso
        // Test/revisione/tentativo e stessa chiave core restano distinti: uno solo è "Sì").
        var vincitori = new HashSet<CandidatoCombinazione>(ReferenceEqualityComparer.Instance);
        foreach (var (chiave, acc) in core)
            if (coreAmmessi == null || coreAmmessi.Contains(chiave))
            if (acc.Vincente != null && (!tempoTutteEsecuzioni || EContributoreTempo(acc.Vincente))) vincitori.Add(acc.Vincente);
        static string ChiaveCandidato(CandidatoCombinazione c)
        {
            // I Test duplicati possono condividere Id legacy ma hanno Uid distinti. Le righe manuali hanno
            // un Uid temporaneo ricreato a ogni passata, quindi usano l'ID deterministico basato sull'Uid master.
            var identitaTest = c.Test.Id.StartsWith(ManualePrefisso, StringComparison.Ordinal)
                ? c.Test.Id
                : c.Test.Uid.ToString("N");
            return string.Join("\u0001", identitaTest, c.Test.Id, c.RevisioneIndice, c.TentativoIndice,
                c.Funzionalita, c.Componente, c.Veicolo, c.CondizioneUid, c.IndiceCandidato);
        }
        IEnumerable<CandidatoCombinazione> CandidatiTempoNonEffettivi(TestCase tc)
        {
            if (!ModalitaTempo(config.ModalitaCella) || config.BaseTempo != BaseTempoMatrice.TutteLeEsecuzioni
                || tc.Eliminato || tc.Origine == OrigineTest.ImportStatoUdt
                || tc.Id.StartsWith(ManualePrefisso, StringComparison.Ordinal))
                yield break;

            // La matrice temporale "Tutte le esecuzioni" conta ogni tentativo eseguito, comprese le
            // condizioni storiche che CondizioniEffettive elimina. Genera qui solo tali righe aggiuntive;
            // la riduzione di stato continua a usare esclusivamente CreaCandidati.
            var ultimaRevisione = new Dictionary<Guid, int>();
            for (var r = 0; r < tc.Esecuzioni.Count; r++)
                foreach (var condizione in tc.Esecuzioni[r].Condizioni) ultimaRevisione[condizione.Uid] = r;
            var veicoli = tc.VeicoliEffettivi.Count > 0 ? (IEnumerable<string>)tc.VeicoliEffettivi : new[] { "" };
            var indiceCandidato = CreaCandidati(tc).Count();
            for (var revisioneIndice = 0; revisioneIndice < tc.Esecuzioni.Count; revisioneIndice++)
            {
                var revisione = tc.Esecuzioni[revisioneIndice];
                var effettive = revisione.CondizioniEffettive.Select(c => c.Uid).ToHashSet();
                for (var tentativoIndice = 0; tentativoIndice < revisione.Condizioni.Count; tentativoIndice++)
                {
                    var condizione = revisione.Condizioni[tentativoIndice];
                    if (effettive.Contains(condizione.Uid)
                        || !ultimaRevisione.TryGetValue(condizione.Uid, out var ultima) || ultima != revisioneIndice
                        || !condizione.Esito.EEseguito() || condizione.RiguardaDtc
                        || config.FiltroTempoOperazione is { } filtro && !filtro.Corrisponde(condizione.DurataMinuti))
                        continue;
                    var funzioni = string.IsNullOrWhiteSpace(condizione.Funzionalita)
                        ? tc.Funzionalita.DefaultIfEmpty(null!) : new[] { condizione.Funzionalita };
                    var componenti = OperationalSemantics.EcuUnits(tc, condizione);
                    foreach (var f in funzioni)
                    foreach (var componente in componenti.Count > 0 ? componenti : new[] { "" })
                    foreach (var veicolo in veicoli)
                    {
                        var deviazioneAccettata = condizione.Esito == Esito.ConformeConDeviazioni
                            && revisione.EsitoSovrascritto && revisione.Esito == Esito.Conforme;
                        yield return new CandidatoCombinazione(f, componente, veicolo, condizione.Esito, deviazioneAccettata,
                            tc, revisione.Data, revisioneIndice, tentativoIndice)
                        {
                            DurataMinuti = condizione.DurataMinuti,
                            CondizioneUid = condizione.Uid,
                            UltimaOccorrenza = true,
                            IndiceCandidato = indiceCandidato++
                        };
                    }
                }
            }
        }

        var filtriCandidati = modalitaStatoCorrente
            ? config.Filtri.Where(f => DimensioneIdentitaCore(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase)
            : config.Filtri;
        // Completa la prima scansione con le evidenze storiche che il reducer di stato non usa ma che
        // l'aggregatore TIME "Tutte le esecuzioni" somma realmente.
        if (contributiTempoPerCore != null)
        {
            foreach (var tc in testCases)
            {
                ct.ThrowIfCancellationRequested();
                if (config.FiltroTempoTotaleTest is { } filtroTotale && !filtroTotale.Corrisponde(TotaleMinutiTest(tc))) continue;
                foreach (var c in CandidatiTempoNonEffettivi(tc))
                {
                    if (!PassaFiltri(c, filtriCandidati, gammaDi, funzioneDi, templateDi)
                        || !EContributoreTempo(c)) continue;
                    var coreKey = ChiaveCore(c.Funzionalita, c.Componente, c.Veicolo);
                    if (coreAmmessi != null && !coreAmmessi.Contains(coreKey)) continue;
                    totale++;
                    if (!contributiTempoPerCore.TryGetValue(coreKey, out var keys))
                        contributiTempoPerCore[coreKey] = keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    keys.Add(ChiaveCandidato(c));
                }
            }
        }
        var chiaviVincitori = vincitori.Select(ChiaveCandidato).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chiaviDeterminanti = new HashSet<string>(chiaviVincitori, StringComparer.OrdinalIgnoreCase);
        var chiaviProtetteFuoriFiltro = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidato in vincitori)
            if (!PassaFiltri(candidato, filtriAggiuntive, gammaDi, funzioneDi, templateDi))
                chiaviProtetteFuoriFiltro.Add(ChiaveCandidato(candidato));
        foreach (var (coreKey, acc) in core)
        {
            if (coreAmmessi != null && !coreAmmessi.Contains(coreKey)) continue;
            if (!tempoTutteEsecuzioni && !acc.Eseguita && acc.CandidatoTestRappresentante is { } pianificato)
            {
                chiaviDeterminanti.Add(ChiaveCandidato(pianificato));
                if (!PassaFiltri(pianificato, filtriAggiuntive, gammaDi, funzioneDi, templateDi))
                    chiaviProtetteFuoriFiltro.Add(ChiaveCandidato(pianificato));
            }
        }
        if (contributiTempoPerCore != null)
            foreach (var keys in contributiTempoPerCore.Values) chiaviDeterminanti.UnionWith(keys);
        if (!tempoTutteEsecuzioni && coreUltimeOccorrenze != null)
            foreach (var (coreKey, acc) in coreUltimeOccorrenze)
                if ((coreAmmessi == null || coreAmmessi.Contains(coreKey))
                    && acc.Vincente is { } vincente
                    && (config.FiltroTempoOperazione is not { } filtroUltimo || filtroUltimo.Corrisponde(vincente.DurataMinuti)))
                {
                    chiaviDeterminanti.Add(ChiaveCandidato(vincente));
                    if (!PassaFiltri(vincente, filtriAggiuntive, gammaDi, funzioneDi, templateDi))
                        chiaviProtetteFuoriFiltro.Add(ChiaveCandidato(vincente));
                }

        var tuttiDeterminanti = modalitaTestPercentuale;
        var determinanti = tuttiDeterminanti ? totale : chiaviDeterminanti.Count;
        // Il conteggio di partenza contiene le righe filtrate. Aggiunge i soli protetti fuori filtro,
        // deduplicati per identità di riga: i protetti già nel filtro sono già conteggiati.
        totale += chiaviProtetteFuoriFiltro.Count;
        var limiteAggiuntiveEffettivo = limiteSoloAggiuntive ? massimoRighe : Math.Max(0, massimoRighe - determinanti);
        var mostrate = determinanti + Math.Min(limiteAggiuntiveEffettivo, Math.Max(0, totale - determinanti));
        IEnumerable<CandidatoCombinazione> SelezionatiStreaming()
        {
            var aggiuntiveIncluse = 0;
            foreach (var tc in testCases)
            {
                ct.ThrowIfCancellationRequested();
                if (tc.Eliminato) continue;
                if (config.FiltroTempoTotaleTest is { } filtroTotaleTest && !filtroTotaleTest.Corrisponde(TotaleMinutiTest(tc))) continue;
                if (config.ModalitaCella == ModalitaCellaMatrice.TestPercentuale && config.FiltroTempoOperazione is { } filtroOperazioneTest
                    && !HaOperazioneConDurata(tc, filtroOperazioneTest)) continue;
                var candidati = CreaCandidati(tc, marcaUltimaOccorrenza: tempoTutteEsecuzioni).ToList();
                var indiceCandidato = 0;
                foreach (var candidato in candidati)
                {
                    ct.ThrowIfCancellationRequested();
                    if (modalitaTestPercentuale && candidato.Test.Origine == OrigineTest.ImportStatoUdt) continue;
                    var c = config.ModalitaCella == ModalitaCellaMatrice.TestPercentuale
                        ? candidato with { Esito = tc.StatoCorrente, IndiceCandidato = indiceCandidato++ }
                        : candidato with { IndiceCandidato = indiceCandidato++ };
                    if (!EContributoreTempo(c)) continue;
                    if (!PassaFiltri(c, filtriCandidati, gammaDi, funzioneDi, templateDi)) continue;
                    if (coreAmmessi != null && !coreAmmessi.Contains(ChiaveCore(c.Funzionalita, c.Componente, c.Veicolo))) continue;
                    var determinante = tuttiDeterminanti || chiaviDeterminanti.Contains(ChiaveCandidato(c));
                    if (!determinante && !PassaFiltri(c, filtriAggiuntive, gammaDi, funzioneDi, templateDi)) continue;
                    if (!determinante && aggiuntiveIncluse++ >= limiteAggiuntiveEffettivo) continue;
                    yield return c;
                }
                foreach (var c in CandidatiTempoNonEffettivi(tc))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!PassaFiltri(c, filtriCandidati, gammaDi, funzioneDi, templateDi)) continue;
                    var coreKey = ChiaveCore(c.Funzionalita, c.Componente, c.Veicolo);
                    if (coreAmmessi != null && !coreAmmessi.Contains(coreKey)) continue;
                    var accTempo = new CombinazioneAccumulatore(c, ValoriEstraVuoti);
                    if (esitiCorrenti != null && !PassaFiltroEsitoCorrente(accTempo, esitiCorrenti)) continue;
                    if (!PassaFiltri(c, filtriAggiuntive, gammaDi, funzioneDi, templateDi)) continue;
                    var determinante = chiaviDeterminanti.Contains(ChiaveCandidato(c));
                    if (!determinante && !PassaFiltri(c, filtriAggiuntive, gammaDi, funzioneDi, templateDi)) continue;
                    if (!determinante && aggiuntiveIncluse++ >= limiteAggiuntiveEffettivo) continue;
                    yield return c;
                }
            }
        }

        var automatiche = ApplicabilitaEcuMatrice.Crea(config);
        var naSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (marcature != null)
            foreach (var m in marcature) naSet.Add(string.Concat(m.ValoreAsseX, "\u0001", m.ValoreAsseY));

        string[] ValoriDimensione(CandidatoCombinazione c, string dimensione, CombinazioneAccumulatore acc)
        {
            var valori = dimensione == DimensioniMatrice.Esito
                ? new[] { tempoTutteEsecuzioni
                    ? EsitoVisualizzato(new CombinazioneAccumulatore(c, ValoriEstraVuoti))
                    : EsitoVisualizzato(acc) }
                : ValoriCandidato(c, dimensione, gammaDi, funzioneDi, templateDi).ToArray();
            return valori.Length == 0 ? new[] { EtichettaValoreVuoto(dimensione) } : valori;
        }

        // Percorsi di un asse: prodotto cartesiano dei valori di ciascun campo, con il separatore dei livelli (la stessa
        // chiave delle celle della matrice, per il controllo N.A.).
        IEnumerable<string> PercorsiAsse(CandidatoCombinazione c, IReadOnlyList<string> dimensioni, CombinazioneAccumulatore acc)
        {
            if (dimensioni.Count == 0) return new[] { "" };
            IEnumerable<string> percorsi = ValoriDimensione(c, dimensioni[0], acc);
            for (var i = 1; i < dimensioni.Count; i++)
            {
                var valori = ValoriDimensione(c, dimensioni[i], acc);
                percorsi = percorsi.SelectMany(p => valori.Select(v => p + SepLivello + v)).ToArray();
            }
            return percorsi;
        }

        IEnumerable<OrigineDato> Righe()
        {
            foreach (var c in SelezionatiStreaming())
            {
                ct.ThrowIfCancellationRequested();
                var acc = core[ChiaveCore(c.Funzionalita, c.Componente, c.Veicolo)];
                var revisione = c.RevisioneIndice >= 0 && c.RevisioneIndice < c.Test.Esecuzioni.Count ? c.Test.Esecuzioni[c.RevisioneIndice] : null;
                var note = revisione?.Note ?? "";
                if (revisione != null)
                {
                    // Tutte le esecuzioni include anche i tentativi non effettivi: in quel caso l'indice
                    // identifica la Condizione grezza, non l'indice della lista CondizioniEffettive.
                    var condizioneOrigine = ModalitaTempo(config.ModalitaCella) && config.BaseTempo == BaseTempoMatrice.TutteLeEsecuzioni
                        ? revisione.Condizioni.FirstOrDefault(condizione => condizione.Uid == c.CondizioneUid)
                        : c.TentativoIndice < revisione.CondizioniEffettive.Count
                            ? revisione.CondizioniEffettive[c.TentativoIndice]
                            : null;
                    if (!string.IsNullOrWhiteSpace(condizioneOrigine?.DescrizioneProblema))
                        note = string.IsNullOrWhiteSpace(note) ? condizioneOrigine.DescrizioneProblema : $"{note} | {condizioneOrigine.DescrizioneProblema}";
                }
                var gamma = !string.IsNullOrEmpty(c.Veicolo) && gammaDi.TryGetValue(c.Veicolo, out var g) ? g : "";
                var categorie = c.Funzionalita != null && funzioneDi.TryGetValue(c.Funzionalita, out var fn) ? string.Join(", ", fn.Categorie) : "";
                // In Stato la vista proietta la combinazione sul rappresentante corrente (che può provenire
                // da un'altra revisione/Test). I dati sorgente restano quelli di c, ma le coordinate devono
                // coincidere con la cella che l'utente vede.
                var proiettaCoordinateSulRappresentante = modalitaStatoCorrente
                    && !(ModalitaTempo(config.ModalitaCella) && config.BaseTempo == BaseTempoMatrice.TutteLeEsecuzioni);
                var candidatoCoordinate = proiettaCoordinateSulRappresentante
                    ? acc.CandidatoTestRappresentante ?? acc.Vincente ?? c
                    : c;
                var righeVista = PercorsiAsse(candidatoCoordinate, config.CampiY, acc).ToArray();
                var colonneVista = PercorsiAsse(candidatoCoordinate, config.CampiX, acc).ToArray();
                // Coerente con i Totali della vista: il dato è "escluso" solo se OGNI cella in cui ricade è N.A.
                var nonApplicabile = (naSet.Count > 0 || automatiche != null)
                    && righeVista.All(y => colonneVista.All(x => naSet.Contains(string.Concat(x, "\u0001", y)) || (automatiche?.NonApplicabile(x, y) ?? false)));
                yield return new OrigineDato(c.Test.Id, c.Test.DescrizioneBreve, c.Test.Origine, revisione == null ? 0 : c.RevisioneIndice + 1,
                    revisione?.Data, revisione?.OperatoreVisualizzato ?? "", c.Veicolo, gamma, c.Componente, c.Funzionalita ?? "", categorie,
                    c.Esito.HasValue ? c.Esito.Value.Label() : "Non testato",
                    acc.Eseguita ? acc.EsitoFinale.Label() : "Non testato",
                    tuttiDeterminanti || chiaviDeterminanti.Contains(ChiaveCandidato(c)),
                    string.Join("; ", righeVista.Select(EtichettaPercorso)), string.Join("; ", colonneVista.Select(EtichettaPercorso)),
                    note, nonApplicabile, c.Test.Uid);
            }
        }
        return (Righe(), mostrate, totale);
    }

    /// <summary>Chiave di una combinazione del catalogo (universo "Non testato"): stessa forma di ChiaveCombinazione.</summary>
    private static string ChiaveCombinazione(string? funzionalita, string componente, string veicolo, IReadOnlyDictionary<string, string> valoriEstra)
    {
        var chiave = ChiaveCore(funzionalita, componente, veicolo);
        foreach (var kv in valoriEstra.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            chiave = string.Concat(chiave, "\u0001", kv.Key, "=", kv.Value);
        return chiave;
    }

    /// <summary>Contatori di una cella o di un totale: stato corrente per categoria e informazioni manuali di provenienza.</summary>
    internal sealed class CellaAccumulatore
    {
        public int Totali;
        public int Valutati;
        public int Conformi;
        public int Ok;
        public int NonConforme;
        public int ConformeConDeviazioni;
        public int DaEseguire;
        public int NonTestato;
        public int DaDecidere;
        public int Manuali;
        public int ManualiSuperati;
        public string? NotaManuale;
        public List<DettaglioValoreManuale> ValoriManuali { get; } = [];

        public StatiMatrice Stati => new(Ok, NonConforme, ConformeConDeviazioni, DaEseguire, NonTestato, DaDecidere);

        public void Aggiungi(StatoCombinazioneMatrice stato, bool valutata, bool conforme, bool manuale = false,
            bool superato = false, string? nota = null, CandidatoCombinazione? valoreManuale = null)
        {
            Totali++;
            if (valutata) Valutati++;
            if (conforme) Conformi++;
            if (manuale) Manuali++;
            if (superato) ManualiSuperati++;
            if (manuale && NotaManuale == null) NotaManuale = nota;
            if (valoreManuale != null)
            {
                var indice = valoreManuale.RevisioneIndice;
                var esecuzione = indice >= 0 && indice < valoreManuale.Test.Esecuzioni.Count
                    ? valoreManuale.Test.Esecuzioni[indice]
                    : null;
                var autore = string.IsNullOrWhiteSpace(esecuzione?.Operatore) ? "—" : esecuzione.Operatore;
                var data = valoreManuale.Data == default ? (DateTime?)null : valoreManuale.Data;
                var motivazione = string.IsNullOrWhiteSpace(valoreManuale.Test.Descrizione)
                    ? "—"
                    : valoreManuale.Test.Descrizione;
                ValoriManuali.Add(new DettaglioValoreManuale(autore, data, motivazione, superato));
            }
            switch (stato)
            {
                case StatoCombinazioneMatrice.Conforme: Ok++; break;
                case StatoCombinazioneMatrice.NonConforme: NonConforme++; break;
                case StatoCombinazioneMatrice.ConformeConDeviazioni: ConformeConDeviazioni++; break;
                case StatoCombinazioneMatrice.DaEseguire: DaEseguire++; break;
                case StatoCombinazioneMatrice.DaDecidere: DaDecidere++; break;
                default: NonTestato++; break;
            }
        }
    }

    /// <summary>Un dato elementare (operazione di una revisione di un Test, per un veicolo/componente/funzionalità)
    /// che concorre alla vista: serve al foglio "Origine" dell'export per verificare/ricostruire ogni cella.
    /// NonApplicabile = tutte le celle della vista in cui ricade sono marcate N.A. (escluse dai totali).</summary>
    public sealed record OrigineDato(string TestId, string Descrizione, OrigineTest Origine, int Revisione, DateTime? Data,
        string Operatore, string Veicolo, string Gamma, string Componente, string Funzionalita, string Categorie,
        string EsitoOperazione, string StatoCombinazione, bool DeterminaStato, string Riga, string Colonna, string Note,
        bool NonApplicabile, Guid TestUid = default);

    public static List<string> ValoriDisponibili(string dimensione, IEnumerable<TestCase> testCases,
        IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita, IReadOnlyList<TestTemplate> templates)
    {
        var gammaDi = veicoli.ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
        var funzioneDi = funzionalita.ToDictionary(f => f.Nome, StringComparer.OrdinalIgnoreCase);
        var templateDi = templates.ToDictionary(t => t.Uid);
        // Elenco dei valori POSSIBILI per la tendina filtro: sui candidati grezzi, non sulle combinazioni
        // ridotte a stato (che dipendono dagli assi scelti e dai filtri già attivi — vedi Prepara).
        return testCases.SelectMany(tc => CreaCandidati(tc)).SelectMany(c => ValoriCandidato(c, dimensione, gammaDi, funzioneDi, templateDi))
            .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Dizionario vuoto immutabile riusato per TUTTI gli accumulatori "core" dei Totali (addendum
    /// 6.4.0): non hanno assi extra per definizione, quindi non serve un'istanza per combinazione.</summary>
    private static readonly Dictionary<string, string> ValoriEstraVuoti = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Espande i Test in candidati (CreaCandidati), scarta quelli che NON passano i filtri configurati
    /// (a livello di operazione/test, PRIMA di calcolare lo stato; il filtro Esito corrente è applicato
    /// dopo la riduzione), poi li riduce a
    /// una combinazione per chiave: funzionalità×componente×veicolo PIÙ i valori degli assi X/Y configurati che non
    /// ne fanno già parte (DimensioniAggiuntive/EspandiEstra, punto 2) — così un Test con più valori su un
    /// asse (es. più Requisiti) genera una combinazione per ciascuno, come già avviene per i veicoli, e due
    /// Test con lo stesso funzionalità×componente×veicolo ma valori diversi su quell'asse (es. due Assegnatari)
    /// restano due combinazioni distinte invece di un'unica proiezione scelta a caso. Addendum 6.4.0: nello
    /// stesso passaggio costruisce anche l'accumulatore "core" (Core, senza assi extra) usato dai Totali di
    /// intestazione — vedi MatricePreparata.CalcolaTotali.</summary>
    private static (Dictionary<string, CombinazioneAccumulatore> Estese, Dictionary<string, CombinazioneAccumulatore> Core)
        CostruisciCombinazioni(IEnumerable<TestCase> testCases, ConfigurazioneMatriceReport config,
        IReadOnlyDictionary<string, string> gammaDi, IReadOnlyDictionary<string, Funzionalita> funzioneDi,
        IReadOnlyDictionary<Guid, TestTemplate> templateDi, Action<CandidatoCombinazione>? perCandidato = null,
        Dictionary<string, CombinazioneAccumulatore>? coreUltimeOccorrenze = null)
    {
        var estra = DimensioniAggiuntive(config);
        var modalitaStatoCorrente = config.ModalitaCella != ModalitaCellaMatrice.TestPercentuale;
        var filtriCandidato = modalitaStatoCorrente
            ? config.Filtri.Where(f => DimensioneIdentitaCore(f.Key))
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase)
            : config.Filtri;
        var combinazioni = new Dictionary<string, CombinazioneAccumulatore>(StringComparer.OrdinalIgnoreCase);
        // Addendum 6.4.0 (totali di intestazione): stesso passaggio sui candidati già filtrati, nessuna
        // scansione aggiuntiva dei Test. L'accumulatore "core" (funzionalità×componente×veicolo, SENZA gli assi
        // extra) è la "combinazione" di cui parlano i Totali — una combinazione con più valori su un asse
        // extra (es. due Assegnatari, o una Funzionalità con due Categorie sull'asse riga) genera più righe/
        // celle distinte (combinazioni ESTESE, invariato) ma resta LA STESSA combinazione core, quindi va
        // contata una sola volta nel totale di colonna/riga/generale — vedi MatricePreparata.CalcolaTotali.
        var combinazioniCore = new Dictionary<string, CombinazioneAccumulatore>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in testCases)
        {
            if (tc.Eliminato) continue;
            // TIME: filtro sul totale minuti del Test (una volta per Test, prima dei candidati).
            if (config.FiltroTempoTotaleTest is { } filtroTotaleTest && !filtroTotaleTest.Corrisponde(TotaleMinutiTest(tc))) continue;
            // TIME, Test %: il Test conta se almeno una sua operazione corrente effettiva corrisponde al filtro sulla durata.
            if (config.ModalitaCella == ModalitaCellaMatrice.TestPercentuale && config.FiltroTempoOperazione is { } filtroOperazioneTest
                && !HaOperazioneConDurata(tc, filtroOperazioneTest)) continue;
            var indiceCandidato = 0;
            foreach (var candidato in CreaCandidati(tc, coreUltimeOccorrenze != null))
            {
                var c = config.ModalitaCella == ModalitaCellaMatrice.TestPercentuale
                    ? candidato with { Esito = tc.StatoCorrente, IndiceCandidato = indiceCandidato++ }
                    : candidato with { IndiceCandidato = indiceCandidato++ };
                if (!PassaFiltri(c, filtriCandidato, gammaDi, funzioneDi, templateDi)) continue;
                perCandidato?.Invoke(c);

                var chiaveCore = ChiaveCore(c.Funzionalita, c.Componente, c.Veicolo);
                if (!combinazioniCore.TryGetValue(chiaveCore, out var accCore)) combinazioniCore[chiaveCore] = new CombinazioneAccumulatore(c, ValoriEstraVuoti);
                else accCore.Aggiorna(c);

                // Base TIME "Ultimo stato": stessa riduzione del core, ma solo sui tentativi dell'ultima revisione. Un passaggio solo:
                // il calcolo non ripete piu' la scansione dei Test (stessa chiave, stesso accumulatore, stesso ordine).
                if (coreUltimeOccorrenze != null && c.UltimaOccorrenza)
                {
                    if (!coreUltimeOccorrenze.TryGetValue(chiaveCore, out var accUltimo)) coreUltimeOccorrenze[chiaveCore] = new CombinazioneAccumulatore(c, ValoriEstraVuoti);
                    else accUltimo.Aggiorna(c);
                }

                if (modalitaStatoCorrente) continue;

                if (estra.Count == 1)
                {
                    // Il caso più comune (asse X componente + asse Y Categoria) ha un solo asse extra. Evita la
                    // lista di combinazioni e l'ordinamento LINQ del dizionario che EspandiEstra/ChiaveCombinazione
                    // costruivano per ciascun candidato, mantenendo una proiezione e una chiave per ogni valore.
                    var dimensione = estra[0];
                    foreach (var valore in ValoriCandidato(c, dimensione, gammaDi, funzioneDi, templateDi).DefaultIfEmpty(""))
                    {
                        var chiave = string.Concat(chiaveCore, "\u0001", dimensione, "=", valore);
                        if (!combinazioni.TryGetValue(chiave, out var acc))
                        {
                            var valoriEstra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [dimensione] = valore };
                            combinazioni[chiave] = new CombinazioneAccumulatore(c, valoriEstra);
                        }
                        else acc.Aggiorna(c);
                    }
                }
                else
                {
                    foreach (var valoriEstra in EspandiEstra(c, estra, gammaDi, funzioneDi, templateDi))
                    {
                        var chiave = ChiaveCombinazione(c, valoriEstra);
                        if (!combinazioni.TryGetValue(chiave, out var acc)) combinazioni[chiave] = new CombinazioneAccumulatore(c, valoriEstra);
                        else acc.Aggiorna(c);
                    }
                }
            }
        }
        return (combinazioni, combinazioniCore);
    }

    private static bool PassaFiltri(CandidatoCombinazione c, Dictionary<string, List<string>> filtri,
        IReadOnlyDictionary<string, string> gammaDi, IReadOnlyDictionary<string, Funzionalita> funzioneDi,
        IReadOnlyDictionary<Guid, TestTemplate> templateDi)
        => filtri.Count == 0 || filtri.All(f => f.Value.Count == 0 || ValoriCandidato(c, f.Key, gammaDi, funzioneDi, templateDi).Any(f.Value.Contains));

    /// <summary>Prodotto cartesiano dei valori del candidato sulle dimensioni aggiuntive (di norma 0-2, gli
    /// assi X/Y non-core): un candidato senza valore su una dimensione aggiuntiva genera comunque UNA
    /// combinazione con il segnaposto "" (stessa etichetta "nessun valore"/"senza componente" già usata per
    /// funzionalità/componente/veicolo, applicata da EtichettaValoreVuoto in Prepara), un candidato con più valori
    /// (es. più Requisiti) genera una combinazione per ciascuno.</summary>
    private static List<Dictionary<string, string>> EspandiEstra(CandidatoCombinazione c, List<string> estra,
        IReadOnlyDictionary<string, string> gammaDi, IReadOnlyDictionary<string, Funzionalita> funzioneDi,
        IReadOnlyDictionary<Guid, TestTemplate> templateDi)
    {
        var risultato = new List<Dictionary<string, string>> { new(StringComparer.OrdinalIgnoreCase) };
        foreach (var dimensione in estra)
        {
            var valori = ValoriCandidato(c, dimensione, gammaDi, funzioneDi, templateDi).DefaultIfEmpty("").ToList();
            var esteso = new List<Dictionary<string, string>>(risultato.Count * valori.Count);
            foreach (var basePronta in risultato)
            foreach (var valore in valori)
            {
                var copia = new Dictionary<string, string>(basePronta, StringComparer.OrdinalIgnoreCase) { [dimensione] = valore };
                esteso.Add(copia);
            }
            risultato = esteso;
        }
        return risultato;
    }

    /// <summary>Identità core funzionalità×componente×veicolo, SENZA i valori degli assi non-identità: è la chiave
    /// usata sia da ChiaveCombinazione (con gli extra appesi, per le celle) sia dall'accumulatore "core" dei
    /// Totali di intestazione (addendum 6.4.0: la "combinazione" contata nei totali è questa, non la chiave
    /// estesa — vedi CostruisciCombinazioni/MatricePreparata.CalcolaTotali).</summary>
    private static string ChiaveCore(string? funzionalita, string componente, string veicolo) =>
        string.Concat(funzionalita ?? "", "\u0001", componente, "\u0001", veicolo);

    private static string ChiaveCombinazione(CandidatoCombinazione c, IReadOnlyDictionary<string, string> valoriEstra)
    {
        var chiave = ChiaveCore(c.Funzionalita, c.Componente, c.Veicolo);
        foreach (var kv in valoriEstra.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            chiave = string.Concat(chiave, "\u0001", kv.Key, "=", kv.Value);
        return chiave;
    }

    /// <summary>
    /// Stesso risultato di N chiamate a ValoriDisponibili, ma espandendo i Test in Unita' UNA SOLA volta invece
    /// che una per dimensione (feedback utente 2026-09-17, volumi 100k): con 9+ dimensioni della matrice più i
    /// campi custom, ripetere CreaUnita per ognuna su tutto l'archivio era il costo dominante della pagina
    /// Report (ogni chiamata ~150-200ms, 9-14 dimensioni = 1.5-2.5s solo per popolare le tendine filtro).
    /// </summary>
    public static Dictionary<string, List<string>> ValoriDisponibiliPerDimensioni(
        IReadOnlyList<string> dimensioni, IEnumerable<TestCase> testCases,
        IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita, IReadOnlyList<TestTemplate> templates,
        ConfigurazioneMatriceReport? configurazioneMatrice = null, IReadOnlyList<Componente>? ecuCatalogo = null,
        IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manuali = null)
    {
        var gammaDi = veicoli.ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
        var funzioneDi = funzionalita.ToDictionary(f => f.Nome, StringComparer.OrdinalIgnoreCase);
        var templateDi = templates.ToDictionary(t => t.Uid);

        // Un solo passaggio in streaming (feedback utente 2026-09-17, volumi 100k): prima l'elenco completo
        // delle unita' veniva materializzato in memoria e poi riletto una volta per ciascuna dimensione
        // (9-14 riletture dell'intero archivio espanso). Qui ogni unita' viene prodotta una volta sola e
        // alimenta subito l'insieme di tutte le dimensioni.
        var insiemi = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dimensione in dimensioni) insiemi[dimensione] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool hasGamma = insiemi.TryGetValue(DimensioniMatrice.Gamma, out var setGamma);
        bool hasSottoveicolo = insiemi.TryGetValue(DimensioniMatrice.Sottoveicolo, out var setSotto);
        bool hasEcu = insiemi.TryGetValue(DimensioniMatrice.Componente, out var setEcu);
        bool hasFunz = insiemi.TryGetValue(DimensioniMatrice.Funzionalita, out var setFunz);
        bool hasCat = insiemi.TryGetValue(DimensioniMatrice.CategoriaFunzionalita, out var setCat);
        bool hasTemplate = insiemi.TryGetValue(DimensioniMatrice.Template, out var setTemplate);
        bool hasEsito = insiemi.TryGetValue(DimensioniMatrice.Esito, out var setEsito);
        bool hasAssegnatario = insiemi.TryGetValue(DimensioniMatrice.Assegnatario, out var setAssegnatario);
        bool hasRequisito = insiemi.TryGetValue(DimensioniMatrice.Requisito, out var setRequisito);

        var proiezioneEsito = hasEsito && configurazioneMatrice != null;
        var modalitaTest = configurazioneMatrice?.ModalitaCella == ModalitaCellaMatrice.TestPercentuale;
        var statiPerCombinazione = hasEsito && proiezioneEsito && !modalitaTest
            ? new Dictionary<string, CombinazioneAccumulatore>(StringComparer.OrdinalIgnoreCase)
            : null;
        void AccumulaStato(CandidatoCombinazione candidato)
        {
            if (statiPerCombinazione == null) return;
            var chiave = ChiaveCore(candidato.Funzionalita, candidato.Componente, candidato.Veicolo);
            if (!statiPerCombinazione.TryGetValue(chiave, out var acc))
                statiPerCombinazione[chiave] = new CombinazioneAccumulatore(candidato, ValoriEstraVuoti);
            else acc.Aggiorna(candidato);
        }

        var customFields = new List<(string Key, HashSet<string> Target)>();
        foreach (var d in dimensioni)
        {
            if (d.StartsWith(DimensioniMatrice.CampoCustomPrefix, StringComparison.OrdinalIgnoreCase))
                customFields.Add((d[DimensioniMatrice.CampoCustomPrefix.Length..], insiemi[d]));
        }

        bool needUnita = hasFunz || hasCat || hasEsito;

        foreach (var tc in testCases)
        {
            if (hasEsito && modalitaTest && !tc.Eliminato && tc.Origine != OrigineTest.ImportStatoUdt)
                setEsito!.Add(EtichettaStatoDisponibile(StatoTestCase(tc)));

            if (hasGamma || hasSottoveicolo)
            {
                foreach (var v in tc.VeicoliEffettivi)
                {
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (hasSottoveicolo) setSotto!.Add(v);
                    if (hasGamma && gammaDi.TryGetValue(v, out var g) && !string.IsNullOrWhiteSpace(g)) setGamma!.Add(g);
                }
            }

            if (hasEcu)
            {
                foreach (var componente in tc.Componente)
                    if (!string.IsNullOrWhiteSpace(componente)) setEcu!.Add(componente);
            }

            if (hasTemplate && templateDi.TryGetValue(tc.TestTemplateUid, out var tmpl) && !string.IsNullOrWhiteSpace(tmpl.Nome))
            {
                setTemplate!.Add(tmpl.Nome);
            }

            if (hasAssegnatario && !string.IsNullOrWhiteSpace(tc.Assegnatario))
            {
                setAssegnatario!.Add(tc.Assegnatario);
            }

            if (hasRequisito)
            {
                foreach (var r in tc.RequisitoId)
                    if (!string.IsNullOrWhiteSpace(r)) setRequisito!.Add(r);
            }

            if (customFields.Count > 0 && tc.CampiCustom.Count > 0)
            {
                foreach (var (key, target) in customFields)
                {
                    if (tc.CampiCustom.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                        target.Add(val);
                }
            }

            if (needUnita)
            {
                // Per un Test mai eseguito, Funzionalita'/Categoria/Esito non dipendono dalla combinazione
                // componente×veicolo. Aggiungerli direttamente evita di allocare e iterare un candidato per ogni
                // prodotto dichiarato (100k Test con un candidato ciascuno nel benchmark FullScale), senza
                // cambiare l'espansione necessaria per gli assi componente/veicolo o per le operazioni eseguite.
                if (tc.Esecuzioni.Count == 0)
                {
                    if (statiPerCombinazione != null && !tc.Eliminato)
                        foreach (var candidato in CreaCandidati(tc)) AccumulaStato(candidato);

                    if (hasFunz || hasCat)
                    {
                        foreach (var funzione in tc.Funzionalita)
                        {
                            if (string.IsNullOrWhiteSpace(funzione)) continue;
                            if (hasFunz) setFunz!.Add(funzione);
                            if (hasCat && funzioneDi.TryGetValue(funzione, out var fn))
                            {
                                foreach (var categoria in fn.Categorie)
                                    if (!string.IsNullOrWhiteSpace(categoria)) setCat!.Add(categoria);
                            }
                        }
                    }

                    if (hasEsito && !proiezioneEsito) setEsito!.Add("Non testato");
                    continue;
                }

                // Le stesse Condizioni prodotte da CreaCandidati (una per candidato funzionalità/componente/veicolo)
                // bastano qui: per popolare le tendine filtro serve solo l'insieme dei valori possibili, non
                // la riduzione a "ultimo esito per combinazione" fatta da CostruisciCombinazioni.
                foreach (var c in CreaCandidati(tc))
                {
                    if (statiPerCombinazione != null && !tc.Eliminato) AccumulaStato(c);

                    if (hasFunz && !string.IsNullOrWhiteSpace(c.Funzionalita))
                        setFunz!.Add(c.Funzionalita);

                    if (hasCat && c.Funzionalita != null && funzioneDi.TryGetValue(c.Funzionalita, out var fn))
                    {
                        foreach (var cat in fn.Categorie)
                            if (!string.IsNullOrWhiteSpace(cat)) setCat!.Add(cat);
                    }

                    if (hasEsito && !proiezioneEsito)
                    {
                        setEsito!.Add(c.Esito.HasValue && c.Esito.Value.EEseguito() ? c.Esito.Value.Label() : "Non testato");
                    }
                }
            }
        }

        if (statiPerCombinazione != null)
        {
            if (manuali is { Count: > 0 })
            {
                foreach (var manuale in manuali)
                foreach (var candidato in CreaCandidati(CreaTestManuale(manuale)))
                    AccumulaStato(candidato);
            }

            foreach (var acc in statiPerCombinazione.Values)
                setEsito!.Add(EsitoVisualizzato(acc));

            // "Non testato" resta un valore dell'asse Esito anche con un filtro Esito attivo: la lista non dipende dalla selezione.
            if (configurazioneMatrice is { } config && UniversoAttivo(SenzaFiltroDimensione(config, DimensioniMatrice.Esito), ecuCatalogo))
            {
                // Valore dell'asse Esito (e filtro) per una combinazione solo catalogo: lo stato di propagazione "Da decidere"
                // resta un indicatore della cella, non il suo Esito. Il valore e' lo stesso per tutte: basta una combinazione.
                if (UniversoNonTestatoNonCoperto(config, ecuCatalogo!, veicoli, funzionalita, gammaDi, statiPerCombinazione))
                    setEsito!.Add("Non testato");
            }
        }

        if (hasEsito && proiezioneEsito && configurazioneMatrice!.Filtri.TryGetValue(DimensioniMatrice.Esito, out var esitiSelezionati))
            foreach (var selezionato in esitiSelezionati) setEsito!.Add(selezionato);

        var risultato = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dimensione in dimensioni)
            risultato[dimensione] = insiemi[dimensione].OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        return risultato;
    }

    /// <summary>Vero se almeno una combinazione del catalogo (componente x funzionalita' x veicolo, come nell'universo "Non testato") non e' tra
    /// i Test e passa i filtri di vista. Si ferma alla prima: per il valore "Non testato" il conteggio non serve.</summary>
    private static bool UniversoNonTestatoNonCoperto(ConfigurazioneMatriceReport config, IReadOnlyList<Componente> ecuCatalogo,
        IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita, IReadOnlyDictionary<string, string> gammaDi,
        Dictionary<string, CombinazioneAccumulatore> statiPerCombinazione)
    {
        var veicoliAttivi = new HashSet<string>(veicoli.Where(v => !v.Archiviata).Select(v => v.Nome), StringComparer.OrdinalIgnoreCase);
        var funzioniAttive = funzionalita.Where(f => !f.Archiviata).ToList();
        foreach (var componente in ecuCatalogo.Where(e => !e.Archiviata))
        {
            var veicoliEcu = componente.VeicoliAssociati.Count == 0
                ? new List<string> { "" }
                : componente.VeicoliAssociati.Where(veicoliAttivi.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in funzioniAttive)
            {
                if (!f.ValeSuEcu(componente.Nome)) continue;
                foreach (var veicolo in veicoliEcu)
                {
                    if (!f.ValeSu(componente.Nome, veicolo)
                        || statiPerCombinazione.ContainsKey(ChiaveCore(f.Nome, componente.Nome, veicolo))
                        || !PassaFiltriCatalogo(config.Filtri, f, componente.Nome, veicolo, gammaDi)) continue;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Espande un Test in un candidato per ogni combinazione (funzionalità dell'operazione o del
    /// Test) × (componente secondo OperationalSemantics.EcuUnits, "" se ambigua/assente) × (veicolo di
    /// VeicoliEffettivi, "" se assente) di OGNI Condizione effettiva di OGNI Esecuzione — non solo l'ultima,
    /// a differenza del vecchio CreaUnita: la riduzione "un solo esito per combinazione, il più recente fra
    /// tutti i Test" è compito di CombinazioneAccumulatore/CostruisciCombinazioni, non di questo metodo.
    /// Un Test senza Esecuzioni, o con una revisione senza Condizioni effettive, genera comunque un candidato
    /// "non eseguito" (Esito null) per ogni funzionalità×componente-dichiarata×veicolo dichiarati dal Test: la
    /// combinazione esiste e conta nei Totali anche se non è mai stata provata.</summary>
    private static IEnumerable<CandidatoCombinazione> CreaCandidati(TestCase tc, bool marcaUltimaOccorrenza = false)
    {
        var veicoli = tc.VeicoliEffettivi.Count > 0 ? (IEnumerable<string>)tc.VeicoliEffettivi : new[] { "" };
        // TIME "Ultimo stato": con marcaUltimaOccorrenza ogni candidato porta il flag UltimaOccorrenza (una copia salvata con lo
        // stesso Uid e' superata in tutte le revisioni tranne l'ultima in cui compare). Senza flag nessun calcolo in piu'.
        var ultime = marcaUltimaOccorrenza ? UltimeOccorrenze(tc) : null;

        if (tc.Esecuzioni.Count == 0)
        {
            foreach (var c in CandidatiDichiarati(tc, null, false, DateTime.MinValue, -1, veicoli))
                yield return c;
            yield break;
        }

        for (var revisioneIndice = 0; revisioneIndice < tc.Esecuzioni.Count; revisioneIndice++)
        {
            var revisione = tc.Esecuzioni[revisioneIndice];
            var effettive = revisione.CondizioniEffettive;
            if (effettive.Count == 0)
            {
                foreach (var c in CandidatiDichiarati(tc, revisione.Esito, false, revisione.Data, revisioneIndice, veicoli))
                    yield return c;
                continue;
            }
            for (var tentativoIndice = 0; tentativoIndice < effettive.Count; tentativoIndice++)
            {
                var condizione = effettive[tentativoIndice];
                // 6.5.0 punto 7: le operazioni guasto non entrano nella matrice per Funzionalita (indici invariati).
                if (condizione.RiguardaDtc) continue;
                var ultimaOccorrenza = ultime == null
                    || (ultime.TryGetValue(condizione.Uid, out var occorrenza) && occorrenza.Revisione == revisioneIndice);
                var funzioni = string.IsNullOrWhiteSpace(condizione.Funzionalita)
                    ? tc.Funzionalita.DefaultIfEmpty(null!) : new[] { condizione.Funzionalita };
                var ecuUnita = OperationalSemantics.EcuUnits(tc, condizione);
                var componenti = ecuUnita.Count > 0 ? (IEnumerable<string>)ecuUnita : new[] { "" };
                var deviazioneAccettata = condizione.Esito == Esito.ConformeConDeviazioni
                    && revisione.EsitoSovrascritto && revisione.Esito == Esito.Conforme;
                foreach (var f in funzioni)
                foreach (var componente in componenti)
                foreach (var v in veicoli)
                    yield return new CandidatoCombinazione(f, componente, v, condizione.Esito, deviazioneAccettata,
                        tc, revisione.Data, revisioneIndice, tentativoIndice)
                    { DurataMinuti = condizione.DurataMinuti, CondizioneUid = condizione.Uid, UltimaOccorrenza = ultimaOccorrenza };
            }
            // REV di sole operazioni guasto: la combinazione dichiarata dal Test non sparisce, resta "non valutata"
            // (le operazioni guasto non entrano mai nella matrice per Funzionalita).
            if (effettive.All(c => c.RiguardaDtc))
                foreach (var c in CandidatiDichiarati(tc, null, false, revisione.Data, revisioneIndice, veicoli))
                    yield return c;
        }
    }

    /// <summary>Fallback quando una revisione (o l'intero Test) non ha Condizioni: la combinazione dichiarata
    /// dal Test (Funzionalita/Componente/VeicoliEffettivi) esiste comunque — "non valutata" finché non riceve un
    /// esito eseguito da qualunque Test — coerente col vecchio comportamento di CreaUnita sull'ultima
    /// revisione, generalizzato a ciascuna revisione priva di condizioni.</summary>
    private static IEnumerable<CandidatoCombinazione> CandidatiDichiarati(TestCase tc, Esito? esito,
        bool deviazioneAccettata, DateTime data, int revisioneIndice, IEnumerable<string> veicoli)
    {
        var componenti = tc.Componente.Count > 0 ? (IEnumerable<string>)tc.Componente : new[] { "" };
        foreach (var f in tc.Funzionalita.DefaultIfEmpty(null!))
        foreach (var componente in componenti)
        foreach (var v in veicoli)
            yield return new CandidatoCombinazione(f, componente, v, esito, deviazioneAccettata, tc, data, revisioneIndice, 0);
    }

    /// <summary>Etichetta della riga/colonna quando un'unità non ha alcun valore per quell'asse (es. un Test
    /// senza componente sull'asse componente): prima un semplice "—" senza spiegazione (feedback utente: "non si sa a cosa
    /// serva e cosa sia", stesso problema già risolto sulla Home per il conteggio). Per l'asse componente riusa la
    /// stessa dicitura dell'avviso Home; per gli altri assi una dicitura generica ma esplicita. Non cambia il
    /// calcolo: è solo l'etichetta della chiave riga/colonna già usata da conteggi, overlay N.A. ed export
    /// Excel/PDF (che leggono Riga/Colonna direttamente da qui, senza logica propria per il caso vuoto).</summary>
    private static string EtichettaValoreVuoto(string dimensione) => dimensione == DimensioniMatrice.Componente
        ? LocalizationService.Instance.Traduci("MatriceSenzaEcuLabel")
        : LocalizationService.Instance.Traduci("MatriceValoreVuotoLabel");

    /// <summary>Valori di UN candidato (operazione/test, prima della riduzione a combinazione) sulla
    /// dimensione richiesta — usata per i filtri (PassaFiltri, a livello di operazione, review indipendente
    /// 2026-09-25 punto 1), per l'espansione della chiave sugli assi aggiuntivi (EspandiEstra, punto 2) e per
    /// popolare le tendine filtro (ValoriDisponibili*). Gamma/Sottoveicolo/Componente/Funzionalita vengono dai campi
    /// già risolti del candidato (non più "tutti i componenti/tutti i veicoli del Test" del vecchio modello
    /// per-operazione); Template/Assegnatario/Requisito/campi custom restano proprietà del TestCase, stabili
    /// fra le revisioni. Esito collassa Da eseguire/N.A. su "Non testato", coerente col resto della matrice.</summary>
    private static IEnumerable<string> ValoriCandidato(CandidatoCombinazione c, string dimensione,
        IReadOnlyDictionary<string, string> gammaDi, IReadOnlyDictionary<string, Funzionalita> funzioneDi,
        IReadOnlyDictionary<Guid, TestTemplate> templateDi)
    {
        if (dimensione == DimensioniMatrice.Gamma)
            return string.IsNullOrEmpty(c.Veicolo) || !gammaDi.TryGetValue(c.Veicolo, out var g) || string.IsNullOrWhiteSpace(g)
                ? Array.Empty<string>() : new[] { g };
        if (dimensione == DimensioniMatrice.Sottoveicolo) return string.IsNullOrEmpty(c.Veicolo) ? Array.Empty<string>() : new[] { c.Veicolo };
        if (dimensione == DimensioniMatrice.Componente) return string.IsNullOrEmpty(c.Componente) ? Array.Empty<string>() : new[] { c.Componente };
        if (dimensione == DimensioniMatrice.Funzionalita) return string.IsNullOrWhiteSpace(c.Funzionalita) ? Array.Empty<string>() : new[] { c.Funzionalita };
        if (dimensione == DimensioniMatrice.CategoriaFunzionalita) return c.Funzionalita != null && funzioneDi.TryGetValue(c.Funzionalita, out var f) ? f.Categorie : Array.Empty<string>();
        if (dimensione == DimensioniMatrice.Template) return templateDi.TryGetValue(c.Test.TestTemplateUid, out var t) ? new[] { t.Nome } : Array.Empty<string>();
        if (dimensione == DimensioniMatrice.Esito) return c.Esito.HasValue && c.Esito.Value.EEseguito() ? new[] { c.Esito.Value.Label() } : new[] { "Non testato" };
        if (dimensione == DimensioniMatrice.Assegnatario) return string.IsNullOrWhiteSpace(c.Test.Assegnatario) ? Array.Empty<string>() : new[] { c.Test.Assegnatario! };
        if (dimensione == DimensioniMatrice.Requisito) return c.Test.RequisitoId;
        if (dimensione.StartsWith(DimensioniMatrice.CampoCustomPrefix, StringComparison.OrdinalIgnoreCase))
            return c.Test.CampiCustom.TryGetValue(dimensione[DimensioniMatrice.CampoCustomPrefix.Length..], out var valore) && !string.IsNullOrWhiteSpace(valore) ? new[] { valore } : Array.Empty<string>();
        return Array.Empty<string>();
    }

}
