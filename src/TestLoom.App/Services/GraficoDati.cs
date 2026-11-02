using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Dati immutabili di un grafico: snapshot degli aggregati (nessuna query dopo la creazione), sufficiente a
/// ridisegnare il grafico a qualunque dimensione. <see cref="Id"/> coincide con <see cref="HomeChartCatalog"/>.</summary>
public abstract record DatiGrafico(string Id);

/// <summary>Copertura requisiti a 4 bucket (Conforme, con deviazioni, non conforme, non testato).</summary>
public sealed record DatiCopertura(long Conformi, long ConformiConDeviazioni, long NonConformi, long NonTestati)
    : DatiGrafico(HomeChartCatalog.IdCopertura)
{
    public static DatiCopertura Da(CoverageBucket bucket) =>
        new(bucket.Conformi, bucket.ConformiConDeviazioni, bucket.NonConformi, bucket.NonTestati);

    public CoverageBucket Bucket => new()
    {
        Conformi = Conformi, ConformiConDeviazioni = ConformiConDeviazioni, NonConformi = NonConformi, NonTestati = NonTestati,
    };
}

/// <summary>Andamento delle anomalie (aperte/chiuse) per data.</summary>
public sealed record DatiAndamento(IReadOnlyList<(DateTime Data, int Aperte, int Chiuse)> Punti)
    : DatiGrafico(HomeChartCatalog.IdAndamento);

/// <summary>Esiti per sottoveicolo: distribuzione per veicolo, per esito e incrociata veicolo x esito.</summary>
public sealed record DatiEsiti(IReadOnlyList<(string Nome, long Valore)> PerSottoveicolo,
    IReadOnlyList<(Esito Esito, long Valore)> PerEsito,
    IReadOnlyList<(string Nome, Esito Esito, long Valore)> PerVeicoloEsito)
    : DatiGrafico(HomeChartCatalog.IdDonut);

/// <summary>Grafico pronto da mostrare o esportare: identita', titolo localizzato, dati e tipo di rappresentazione.</summary>
public sealed record GraficoSnapshot(string Id, string Titolo, DatiGrafico Dati, TipoGrafico Tipo);

/// <summary>Calcolo degli snapshot dei grafici a partire dagli insiemi gia' filtrati. Stesse formule della Home (la Home
/// e il Report ne usano una sola): copertura su tutti i test del filtro, andamento sulle anomalie dei test del filtro,
/// esiti sui test contati (esclusi quelli importati solo con stato finale da UnitLogger).</summary>
public static class GraficiDatiService
{
    public static DatiCopertura Copertura(IReadOnlyList<Requisito> requisiti, IReadOnlyList<TestCase> testCases) =>
        DatiCopertura.Da(CoverageService.PerRequisito(requisiti, testCases, soloBanco: false, soloVeicolo: false));

    public static DatiAndamento Andamento(IReadOnlyList<NonConformita> nonConformita) =>
        new(ReportService.AndamentoNc(nonConformita));

    /// <summary>Test che entrano nei conteggi operativi: escludono i Test importati con solo stato finale da UnitLogger.</summary>
    public static List<TestCase> TestContati(IEnumerable<TestCase> testCases) =>
        testCases.Where(tc => tc.Origine != OrigineTest.ImportStatoUdt).ToList();

    public static DatiEsiti Esiti(IEnumerable<TestCase> testCasesContati)
    {
        // Un solo passaggio sui Test: prima erano tre GroupBy, ciascuno con una propria enumerazione e proprie copie di
        // VeicoliEffettivi (~180 ms a 100k Test). Ogni gruppo conserva l'ordine di primo incontro, come GroupBy, e gli
        // ordinamenti sono quelli di prima. Un nome veicolo nullo (dato malformato) resta un gruppo distinto da "".
        var sottoveicoli = new ContatoriGruppo<(bool Presente, string Testo)>();
        var esiti = new ContatoriGruppo<Esito>();
        var veicoloEsito = new ContatoriGruppo<(bool Presente, string Testo, Esito Esito)>();
        foreach (var tc in testCasesContati)
        {
            var esito = tc.StatoCorrente ?? Esito.NonEseguitoNA;
            esiti.Aggiungi(esito);
            var veicoli = tc.VeicoliEffettivi;
            // Sottoveicoli: un Test senza veicolo conta come "—" (DefaultIfEmpty). Dettaglio veicolo-esito: resta fuori (come
            // nel percorso SQL, che parte da test_vehicle), quindi qui non si aggiunge nulla.
            if (veicoli.Count == 0) sottoveicoli.Aggiungi((true, "—"));
            foreach (var veicolo in veicoli)
            {
                sottoveicoli.Aggiungi((veicolo is not null, veicolo ?? ""));
                veicoloEsito.Aggiungi((veicolo is not null, veicolo ?? "", esito));
            }
        }
        var perSottoveicolo = sottoveicoli.Gruppi()
            .Select(g => (Nome: g.Chiave.Presente ? g.Chiave.Testo : null!, Valore: g.Valore))
            .OrderByDescending(x => x.Valore).ToList();
        var perEsito = esiti.Gruppi().Select(g => (Esito: g.Chiave, Valore: g.Valore)).ToList();
        var perVeicoloEsito = veicoloEsito.Gruppi()
            .Select(g => (Nome: g.Chiave.Presente ? g.Chiave.Testo : null!, Esito: g.Chiave.Esito, Valore: g.Valore))
            .OrderBy(x => x.Nome, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Esito).ToList();
        return new DatiEsiti(perSottoveicolo, perEsito, perVeicoloEsito);
    }

    /// <summary>Conteggi per chiave; i gruppi escono nell'ordine di primo incontro, come con GroupBy.</summary>
    private sealed class ContatoriGruppo<TChiave> where TChiave : notnull
    {
        private readonly Dictionary<TChiave, int> _posizioni = new();
        private readonly List<TChiave> _chiavi = new();
        private readonly List<long> _valori = new();

        public void Aggiungi(TChiave chiave)
        {
            if (_posizioni.TryGetValue(chiave, out var posizione)) _valori[posizione]++;
            else
            {
                _posizioni[chiave] = _chiavi.Count;
                _chiavi.Add(chiave);
                _valori.Add(1);
            }
        }

        public IEnumerable<(TChiave Chiave, long Valore)> Gruppi() => _chiavi.Select((chiave, i) => (Chiave: chiave, Valore: _valori[i]));
    }

    /// <summary>Tre grafici del foglio Grafici dell'export Excel del Report (stesso insieme di test filtrato dell'export): copertura, andamento
    /// delle anomalie dei test filtrati, esiti per sottoveicolo. Tipi predefiniti della Home.</summary>
    public static IReadOnlyList<GraficoSnapshot> PerReport(IReadOnlyList<Requisito> requisiti, IReadOnlyList<TestCase> testCasesFiltrati,
        IReadOnlyList<NonConformita> nonConformitaDeiFiltrati)
    {
        var loc = LocalizationService.Instance;
        var contati = TestContati(testCasesFiltrati);
        return new[]
        {
            new GraficoSnapshot(HomeChartCatalog.IdCopertura, loc.Traduci("GraficoCoperturaRequisiti"),
                Copertura(requisiti, testCasesFiltrati), HomeChartCatalog.Predefinito(HomeChartCatalog.IdCopertura)),
            new GraficoSnapshot(HomeChartCatalog.IdAndamento, loc.Traduci("GraficoAndamentoAnomalie"),
                Andamento(nonConformitaDeiFiltrati), HomeChartCatalog.Predefinito(HomeChartCatalog.IdAndamento)),
            new GraficoSnapshot(HomeChartCatalog.IdDonut, loc.Traduci("GraficoEsitiSottoveicolo"),
                Esiti(contati), HomeChartCatalog.Predefinito(HomeChartCatalog.IdDonut)),
        };
    }
}
