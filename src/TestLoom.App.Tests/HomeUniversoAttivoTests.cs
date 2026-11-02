using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Home "Stato %" (6.7.1): stessa regola di ReportMatriceService.UniversoAttivo del Report. Con i filtri della dashboard
/// l'universo "Non testato" entra solo se tutti i criteri sono di identita' (componente, veicolo, funzionalita', categoria, gamma); ogni
/// altro criterio (operatore, TIME, tipo di Test, date, campi custom, assegnatario, requisito, esito) lo spegne.</summary>
public sealed class HomeUniversoAttivoTests
{
    private static readonly Guid TipoA = new("00000000-0000-0000-0000-00000000000a");
    private static readonly DateTime Gennaio1 = new(2026, 1, 1);

    private static readonly List<Veicolo> Veicoli = [new() { Nome = "V1", GammaNome = "G1" }];
    private static readonly List<Funzionalita> Funzioni = [new() { Nome = "F1", Categorie = ["CatA"] }];
    private static readonly List<TestTemplate> Templates = [new() { Uid = TipoA, Nome = "Tipo A" }];

    // E2 ha F1 sul veicolo V1 ma nessun Test: (F1, E2, V1) e' la combinazione "Non testato" dell'universo.
    private static readonly List<Componente> CatalogoEcu =
    [
        new() { Nome = "E1", VeicoliAssociati = ["V1"] },
        new() { Nome = "E2", VeicoliAssociati = ["V1"] },
    ];

    private static readonly List<StatoFunzionalitaEcuVeicolo> NessunManuale = [];

    /// <summary>Un solo Test su E1 eseguito da "Mario": E2 non e' testata.</summary>
    private static List<TestCase> UnTest() =>
    [
        new()
        {
            Id = "T-1",
            Componente = new EcuSelection(["E1"]),
            Veicoli = ["V1"],
            Funzionalita = ["F1"],
            TestTemplateUid = TipoA,
            DataCreazione = Gennaio1,
            Esecuzioni =
            [
                new Esecuzione
                {
                    Data = Gennaio1,
                    Operatore = "Mario",
                    Condizioni = [new Condizione { Funzionalita = "F1", Esito = Esito.Conforme, Componente = new EcuSelection(["E1"]) }],
                },
            ],
        },
    ];

    private static StatiMatrice Home(DashboardFilter filtro) =>
        HomeConformitySummaryService.CalcolaStatoCorrenteFiltrato(UnTest(), filtro, Veicoli, Funzioni, CatalogoEcu, NessunManuale);

    private static StatiMatrice Report(ConfigurazioneMatriceReport config, ReportFilter? filtro = null) =>
        ReportMatriceService.Genera(UnTest().Where(t => filtro == null || filtro.CorrispondeMatrice(t)), config, Veicoli, Funzioni, Templates,
            ecuCatalogo: CatalogoEcu, filtro: filtro).TotaleGenerale.StatiPercentuale;

    /// <summary>Stessa configurazione di Stato % della Home (componente x funzionalita'), con i filtri di matrice indicati.</summary>
    private static ConfigurazioneMatriceReport ConfigStato(params (string Dimensione, string Valore)[] filtri)
    {
        var config = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.Funzionalita],
            ModalitaCella = ModalitaCellaMatrice.Percentuale,
        };
        foreach (var (dimensione, valore) in filtri)
        {
            if (!config.Filtri.TryGetValue(dimensione, out var valori)) config.Filtri[dimensione] = valori = new List<string>();
            valori.Add(valore);
        }
        return config;
    }

    [Fact]
    public void HomeSenzaFiltri_IncludeLUniversoNonTestato()
    {
        var stati = Home(new DashboardFilter());

        Assert.True(stati.NonTestato > 0);
        Assert.Equal(stati, Report(ConfigStato()));
    }

    [Fact]
    public void HomeConFiltroOperatore_EscludeLUniversoNonTestato()
    {
        var stati = Home(new DashboardFilter { Operatore = ["Mario"] });

        Assert.Equal(1, stati.Ok);
        Assert.Equal(0, stati.NonTestato);
    }

    [Fact]
    public void HomeConFiltroTempoOperazione_EscludeLUniversoNonTestato()
    {
        var stati = Home(new DashboardFilter { TempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 10) });

        Assert.Equal(0, stati.NonTestato);
    }

    [Fact]
    public void HomeConFiltroTempoTotaleTest_EscludeLUniversoNonTestato()
    {
        var stati = Home(new DashboardFilter { TempoTotaleTest = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 10) });

        Assert.Equal(0, stati.NonTestato);
    }

    [Fact]
    public void HomeConFiltroStrumento_EscludeLUniversoNonTestato()
    {
        var stati = Home(new DashboardFilter { Strumento = ["Strumento X"] });

        Assert.Equal(0, stati.NonTestato);
    }

    [Fact]
    public void HomeConFiltroAssegnatarioRequisitoOEsito_EscludeLUniversoNonTestato()
    {
        Assert.Equal(0, Home(new DashboardFilter { Assegnatario = ["Mario"] }).NonTestato);
        Assert.Equal(0, Home(new DashboardFilter { Requisito = ["REQ-1"] }).NonTestato);
        Assert.Equal(0, Home(new DashboardFilter { Esito = [Esito.Conforme] }).NonTestato);
    }

    [Fact]
    public void HomeConFiltroTipoTestDataOCampiCustom_EscludeLUniversoNonTestato()
    {
        Assert.Equal(0, Home(new DashboardFilter { TestTemplateUid = [TipoA] }).NonTestato);
        Assert.Equal(0, Home(new DashboardFilter { DataDa = new DateTime(2025, 12, 1) }).NonTestato);
        Assert.Equal(0, Home(new DashboardFilter { CampiCustom = new() { ["Ambito"] = ["Nord"] } }).NonTestato);
    }

    /// <summary>Filtri di sola identita': Home e Report danno gli stessi numeri, e l'universo resta acceso dove il filtro lo ammette.</summary>
    [Fact]
    public void FiltroDiIdentita_HomeEReportDannoLoStessoStatoPercentuale()
    {
        // Veicolo V1 e categoria/gamma/funzionalita F1 contengono E2: l'universo "Non testato" resta.
        AssertUguali(new DashboardFilter { Veicolo = ["V1"] }, Report(ConfigStato(), new ReportFilter { Veicoli = ["V1"] }), keepsUniverse: true);
        AssertUguali(new DashboardFilter { Funzionalita = ["F1"] }, Report(ConfigStato((DimensioniMatrice.Funzionalita, "F1"))), keepsUniverse: true);
        AssertUguali(new DashboardFilter { Gamma = ["G1"] }, Report(ConfigStato((DimensioniMatrice.Gamma, "G1"))), keepsUniverse: true);
        AssertUguali(new DashboardFilter { CategoriaFunzionalita = ["CatA"] },
            Report(ConfigStato((DimensioniMatrice.CategoriaFunzionalita, "CatA"))), keepsUniverse: true);
        // componente del filtro: l'universo resta sulla sola E1, che ha il suo Test, quindi nessun "Non testato".
        AssertUguali(new DashboardFilter { Componente = ["E1"] }, Report(ConfigStato(), new ReportFilter { Componente = ["E1"] }), keepsUniverse: false);
    }

    private void AssertUguali(DashboardFilter filtro, StatiMatrice report, bool keepsUniverse)
    {
        var home = Home(filtro);

        Assert.Equal(report, home);
        if (keepsUniverse) Assert.True(home.NonTestato > 0);
        else Assert.Equal(0, home.NonTestato);
    }
}
