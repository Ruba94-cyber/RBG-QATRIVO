using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Adatta il read model indicizzato della Home all'aggregatore di dominio della matrice Report.
/// Riceve già un solo vincitore per tripla core dallo store SQLite: non carica o riesamina lo storico Test.</summary>
public static class HomeConformitySummaryService
{
    private static readonly ConfigurazioneMatriceReport ConfigurazioneStato = new()
    {
        CampiX = [DimensioniMatrice.Componente],
        CampiY = [DimensioniMatrice.Funzionalita],
        ModalitaCella = ModalitaCellaMatrice.Percentuale,
    };

    public static StatiMatrice CalcolaTestPercentuale(IReadOnlyList<TestCase> testCases) =>
        Somma(testCases.Where(t => t.Origine != OrigineTest.ImportStatoUdt)
            .DistinctBy(t => t.Uid)
            .Select(t => StatoTestCase(t.StatoCorrente)));

    public static StatiMatrice CalcolaStatoCorrente(
        IReadOnlyList<LocalHomeFunctionalitySource> sorgenti,
        IReadOnlyList<Veicolo> veicoli,
        IReadOnlyList<Funzionalita> funzionalita,
        IReadOnlyList<Componente> componente,
        IReadOnlyList<StatoFunzionalitaEcuVeicolo> manuali)
    {
        var candidati = sorgenti.Select(CreaTestSintetico).ToList();
        // Solo il totale generale: la griglia (1M celle al catalogo 1000x1000) non serve alla Home e non si costruisce.
        var totale = ReportMatriceService.Prepara(candidati, ConfigurazioneStato, veicoli, funzionalita, [],
            ecuCatalogo: componente, manuali: manuali).CalcolaTotaleGenerale();
        return totale.StatiPercentuale;
    }

    public static StatiMatrice CalcolaStatoCorrenteDaTestCases(IReadOnlyList<TestCase> testCases,
        IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita, IReadOnlyList<Componente> componente,
        IReadOnlyList<StatoFunzionalitaEcuVeicolo> manuali) =>
        ReportMatriceService.Prepara(testCases, ConfigurazioneStato, veicoli, funzionalita, [],
            ecuCatalogo: componente, manuali: manuali).CalcolaTotaleGenerale().StatiPercentuale;

    /// <summary>Stato % della Home con i filtri della dashboard (6.7.1), con la stessa regola del Report (UniversoAttivo): l'universo
    /// "Non testato" entra solo se i filtri sono di identita'. I criteri di matrice passano per la stessa Prepara del Report e il TIME
    /// per combinazione; il resto del filtro per ReportFilter, come nella pagina del Report.</summary>
    public static StatiMatrice CalcolaStatoCorrenteFiltrato(IReadOnlyList<TestCase> testCases, DashboardFilter filtro,
        IReadOnlyList<Veicolo> veicoli, IReadOnlyList<Funzionalita> funzionalita, IReadOnlyList<Componente> componente,
        IReadOnlyList<StatoFunzionalitaEcuVeicolo> manuali)
    {
        var (filtroReport, configurazione) = TraduciFiltro(filtro);
        return ReportMatriceService.Prepara(testCases.Where(filtroReport.CorrispondeMatrice), configurazione, veicoli, funzionalita, [],
            ecuCatalogo: componente, manuali: manuali, filtro: filtroReport).CalcolaTotaleGenerale().StatiPercentuale;
    }

    /// <summary>Filtro del Report e configurazione Stato % equivalenti al filtro della Home. componente e veicolo sono il filtro di pagina
    /// del Report; gamma, funzionalita', categoria, requisito, assegnatario ed esito sono filtri di matrice; operatore, strumento,
    /// tipo di Test, date, campi custom e TIME sono criteri non di identita' (spengono l'universo).</summary>
    private static (ReportFilter Filtro, ConfigurazioneMatriceReport Configurazione) TraduciFiltro(DashboardFilter f)
    {
        var filtro = new ReportFilter
        {
            DataDa = f.DataDa,
            DataA = f.DataA,
            CampiCustom = f.CampiCustom.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            TempoTotaleTest = f.TempoTotaleTest,
            Operatore = f.Operatore.ToList(),
            Strumento = f.Strumento.ToList(),
        };
        filtro.Componente.AddRange(f.Componente);
        filtro.Veicoli.AddRange(f.Veicolo);
        filtro.TemplateUid.AddRange(f.TestTemplateUid);

        var configurazione = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.Funzionalita],
            ModalitaCella = ModalitaCellaMatrice.Percentuale,
            FiltroTempoOperazione = f.TempoOperazione,
            FiltroTempoTotaleTest = f.TempoTotaleTest,
        };
        AggiungiFiltroMatrice(configurazione, DimensioniMatrice.Gamma, f.Gamma);
        AggiungiFiltroMatrice(configurazione, DimensioniMatrice.Funzionalita, f.Funzionalita);
        AggiungiFiltroMatrice(configurazione, DimensioniMatrice.CategoriaFunzionalita, f.CategoriaFunzionalita);
        AggiungiFiltroMatrice(configurazione, DimensioniMatrice.Requisito, f.Requisito);
        AggiungiFiltroMatrice(configurazione, DimensioniMatrice.Assegnatario, f.Assegnatario);
        AggiungiFiltroMatrice(configurazione, DimensioniMatrice.Esito, f.Esito.Select(e => e.Label()));
        return (filtro, configurazione);
    }

    private static void AggiungiFiltroMatrice(ConfigurazioneMatriceReport configurazione, string dimensione, IEnumerable<string> valori)
    {
        var lista = valori.ToList();
        if (lista.Count > 0) configurazione.Filtri[dimensione] = lista;
    }

    private static TestCase CreaTestSintetico(LocalHomeFunctionalitySource source)
    {
        var outcome = source.Outcome ?? Esito.DaEseguire;
        var test = new TestCase
        {
            Id = source.TestId,
            Componente = string.IsNullOrWhiteSpace(source.Componente) ? [] : new EcuSelection([source.Componente]),
            Veicoli = string.IsNullOrWhiteSpace(source.Veicolo) ? [] : [source.Veicolo],
            Funzionalita = string.IsNullOrWhiteSpace(source.Funzionalita) ? [] : [source.Funzionalita],
        };
        test.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(source.DataTicks, DateTimeKind.Utc),
            Esito = outcome,
            Condizioni =
            [
                new Condizione
                {
                    Funzionalita = source.Funzionalita,
                    Componente = string.IsNullOrWhiteSpace(source.Componente) ? [] : new EcuSelection([source.Componente]),
                    Esito = outcome,
                }
            ],
        });
        return test;
    }

    private static StatoCombinazioneMatrice StatoTestCase(Esito? esito) => esito switch
    {
        Esito.Conforme => StatoCombinazioneMatrice.Conforme,
        Esito.NonConforme => StatoCombinazioneMatrice.NonConforme,
        Esito.ConformeConDeviazioni => StatoCombinazioneMatrice.ConformeConDeviazioni,
        Esito.DaRivedere => StatoCombinazioneMatrice.DaDecidere,
        Esito.NonApplicabile or Esito.NonEseguitoNA => StatoCombinazioneMatrice.NonTestato,
        _ => StatoCombinazioneMatrice.DaEseguire,
    };

    private static StatiMatrice Somma(IEnumerable<StatoCombinazioneMatrice> stati)
    {
        var conteggi = new int[6];
        foreach (var stato in stati)
            conteggi[(int)stato] = checked(conteggi[(int)stato] + 1);
        return new StatiMatrice(conteggi[(int)StatoCombinazioneMatrice.Conforme],
            conteggi[(int)StatoCombinazioneMatrice.NonConforme],
            conteggi[(int)StatoCombinazioneMatrice.ConformeConDeviazioni],
            conteggi[(int)StatoCombinazioneMatrice.DaEseguire],
            conteggi[(int)StatoCombinazioneMatrice.NonTestato],
            conteggi[(int)StatoCombinazioneMatrice.DaDecidere]);
    }
}
