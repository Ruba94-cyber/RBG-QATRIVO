using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Regola di prodotto 6.7.0 per l'universo "Non testato" (ReportMatriceService.UniversoAttivo): entra solo con criteri di
/// identita' (componente, veicolo, funzionalita', categoria, gamma); ogni altro criterio del Report lo spegne, in ogni modalita'; componente e
/// veicolo del Report lo restringono. Stesso predicato per la matrice, la Home e i valori dei filtri.</summary>
public sealed class ReportUniversoAttivoTests
{
    private static readonly Guid TipoA = new("00000000-0000-0000-0000-00000000000a");
    private static readonly DateTime Gennaio1 = new(2026, 1, 1);
    private static readonly DateTime Gennaio2 = new(2026, 1, 2);

    private static readonly List<Veicolo> Veicoli = [new() { Nome = "V1", GammaNome = "G1" }];
    private static readonly List<Funzionalita> Funzioni = [new() { Nome = "F1", Categorie = ["CatA"] }];
    private static readonly List<TestTemplate> Templates = [new() { Uid = TipoA, Nome = "Tipo A" }];

    // E2 ha F1 sul veicolo V1 ma nessun Test: (F1, E2, V1) e' la combinazione "Non testato" dell'universo.
    private static readonly List<Componente> CatalogoEcu =
    [
        new() { Nome = "E1", VeicoliAssociati = ["V1"] },
        new() { Nome = "E2", VeicoliAssociati = ["V1"] },
    ];

    private static TestCase Test(string id, string componente, Esito esito, DateTime data) => new()
    {
        Id = id,
        Componente = new EcuSelection([componente]),
        Veicoli = ["V1"],
        Funzionalita = ["F1"],
        TestTemplateUid = TipoA,
        DataCreazione = data,
        Esecuzioni =
        [
            new Esecuzione
            {
                Data = data,
                Condizioni = [new Condizione { Funzionalita = "F1", Esito = esito, Componente = new EcuSelection([componente]) }],
            },
        ],
    };

    /// <summary>Un solo Test su E1: E2 non e' testata.</summary>
    private static List<TestCase> UnTest() => [Test("T-1", "E1", Esito.Conforme, Gennaio1)];

    private static ConfigurazioneMatriceReport Config(params (string Dimensione, string Valore)[] filtri)
    {
        var config = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.CategoriaFunzionalita],
            ModalitaCella = ModalitaCellaMatrice.Stato,
        };
        foreach (var (dimensione, valore) in filtri)
        {
            if (!config.Filtri.TryGetValue(dimensione, out var valori)) config.Filtri[dimensione] = valori = new List<string>();
            valori.Add(valore);
        }
        return config;
    }

    /// <summary>Genera la matrice dai Test che passano il filtro del Report, come fa la pagina (criteri non TIME e totale del Test).</summary>
    private static RisultatoMatriceReport Genera(IEnumerable<TestCase> test, ConfigurazioneMatriceReport config, ReportFilter? filtro = null) =>
        ReportMatriceService.Genera(test.Where(t => filtro == null || filtro.CorrispondeMatrice(t)), config, Veicoli, Funzioni, Templates,
            ecuCatalogo: CatalogoEcu, filtro: filtro);

    /// <summary>Un criterio alla volta, di ogni tipo previsto dalla regola: criteri del Report (pagina) e filtri di matrice.</summary>
    private static (ConfigurazioneMatriceReport Config, ReportFilter? Filtro) CasoNonIdentita(int caso)
    {
        var config = Config();
        ReportFilter? filtro = null;
        switch (caso)
        {
            case 0: filtro = new ReportFilter { TemplateUid = [TipoA] }; break;
            case 1: filtro = new ReportFilter { DataDa = new DateTime(2025, 12, 1) }; break;
            case 2: filtro = new ReportFilter { CampiCustom = new() { ["Ambito"] = ["Nord"] } }; break;
            case 3: config.FiltroTempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 10); break;
            case 4: config.FiltroTempoTotaleTest = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 10); break;
            case 5: filtro = new ReportFilter { TempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 10) }; break;
            case 6: config = Config(("template", "Tipo A")); break;
            case 7: config = Config(("assegnatario", "Mario")); break;
            default: config = Config(("esito", "Conforme")); break;
        }
        return (config, filtro);
    }

    public static IEnumerable<object[]> CriteriNonIdentita()
    {
        for (var caso = 0; caso < 9; caso++) yield return [caso];
    }

    [Fact]
    public void SenzaCriteriNonIdentita_LUniversoEntra()
    {
        var r = Genera(UnTest(), Config());

        Assert.Contains("E2", r.Colonne);
        Assert.True(r.TotaleGenerale.StatiPercentuale.NonTestato > 0);
    }

    [Fact]
    public void SenzaCriteriNonIdentita_LaHomeContaLUniverso()
    {
        var stati = HomeConformitySummaryService.CalcolaStatoCorrenteDaTestCases(UnTest(), Veicoli, Funzioni, CatalogoEcu, []);

        Assert.True(stati.NonTestato > 0);
    }

    [Theory]
    [MemberData(nameof(CriteriNonIdentita))]
    public void CriterioNonIdentita_SpegneLUniverso(int caso)
    {
        var (config, filtro) = CasoNonIdentita(caso);

        var r = Genera(UnTest(), config, filtro);

        Assert.DoesNotContain("E2", r.Colonne);
        Assert.Equal(0, r.TotaleGenerale.StatiPercentuale.NonTestato);
    }

    [Fact]
    public void EsitoNonTestato_MantieneLUniverso()
    {
        var r = Genera(UnTest(), Config(("esito", "Non testato")));

        Assert.Contains("E2", r.Colonne);
        Assert.True(r.TotaleGenerale.StatiPercentuale.NonTestato > 0);
    }

    [Fact]
    public void FiltroEcuDelReport_RestringeLUniversoAllaSolaEcu()
    {
        // T-2 (su E2) e' escluso dal filtro componente: E2 non risulta testata nel Report, ma l'universo resta sulla sola componente E1.
        var casi = new List<TestCase> { Test("T-1", "E1", Esito.Conforme, Gennaio1), Test("T-2", "E2", Esito.Conforme, Gennaio2) };

        var r = Genera(casi, Config(), new ReportFilter { Componente = ["E1"] });

        Assert.DoesNotContain("E2", r.Colonne);
        Assert.Equal(0, r.TotaleGenerale.StatiPercentuale.NonTestato);
    }

    [Fact]
    public void FiltroVeicoloDelReport_LUniversoRestaSulVeicoloSelezionato()
    {
        var r = Genera(UnTest(), Config(), new ReportFilter { Veicoli = ["V1"] });

        Assert.Contains("E2", r.Colonne);
    }

    [Fact]
    public void FiltroVeicoloSenzaCombinazioni_SvuotaLUniverso()
    {
        var r = Genera(UnTest(), Config(), new ReportFilter { Veicoli = ["V9"] });

        Assert.DoesNotContain("E2", r.Colonne);
        Assert.Equal(0, r.TotaleGenerale.StatiPercentuale.NonTestato);
    }

    [Fact]
    public void ValoriEsito_NonTestatoRestaDisponibileConFiltroEsitoAttivo()
    {
        var valori = ReportMatriceService.ValoriDisponibiliPerDimensioni([DimensioniMatrice.Esito], UnTest(), Veicoli, Funzioni, Templates,
            Config(("esito", "Conforme")), CatalogoEcu);

        Assert.Contains("Non testato", valori[DimensioniMatrice.Esito]);
    }
}
