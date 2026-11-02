using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class DashboardFilterTests
{
    private static readonly Guid TemplateBanco = Guid.NewGuid();
    private static readonly Guid TemplateVeicolo = Guid.NewGuid();

    private static readonly Dictionary<string, string?> Gamme = new()
    {
        ["Daily MY24"] = "LCV Diesel",
        ["Medium MY24"] = "MCV Diesel",
    };

    private static string? GammaDi(string veicolo) => Gamme.GetValueOrDefault(veicolo);

    private static TestCase CasoBase() => new()
    {
        Id = "TC-1",
        Componente = "Componente-TEST-BCM",
        Veicolo = "Daily MY24",
        TestTemplateUid = TemplateBanco,
        Assegnatario = "Operatore B",
        Funzionalita = new List<string> { "Diagnosi attiva iniettori" },
        RequisitoId = new List<string> { "ST12" },
        StrumentiUsati = new List<string> { "ValidationTool" },
        DataCreazione = new DateTime(2026, 6, 1),
        Esecuzioni = new List<Esecuzione> { new() { Operatore = "Operatore A", Esito = Esito.Conforme, Matricola = "V", Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme } } } },
    };

    [Fact]
    public void FiltroVuoto_CorrispondeATutto()
    {
        var filtro = new DashboardFilter();
        Assert.True(filtro.Corrisponde(CasoBase(), GammaDi));
    }

    [Fact]
    public void FiltroGamma_RisolveTramiteIlVeicolo()
    {
        var filtro = new DashboardFilter { Gamma = new List<string> { "LCV Diesel" } };
        Assert.True(filtro.Corrisponde(CasoBase(), GammaDi));

        var altro = new DashboardFilter { Gamma = new List<string> { "MCV Diesel" } };
        Assert.False(altro.Corrisponde(CasoBase(), GammaDi));
    }

    [Fact]
    public void FiltroGamma_SelezioneMultipla_CorrispondeSeUnaQualsiasiCombacia()
    {
        var filtro = new DashboardFilter { Gamma = new List<string> { "MCV Diesel", "LCV Diesel" } };
        Assert.True(filtro.Corrisponde(CasoBase(), GammaDi));
    }

    [Fact]
    public void FiltroOperatore_GuardaLeEsecuzioniNonLAssegnatario()
    {
        var filtroEsecutore = new DashboardFilter { Operatore = new List<string> { "Operatore A" } };
        Assert.True(filtroEsecutore.Corrisponde(CasoBase(), GammaDi));

        var filtroAssegnatario = new DashboardFilter { Operatore = new List<string> { "Operatore B" } };
        Assert.False(filtroAssegnatario.Corrisponde(CasoBase(), GammaDi)); // B è l'assegnatario, non ha eseguito nulla
    }

    [Fact]
    public void FiltroAssegnatario_DistintoDalFiltroOperatore()
    {
        var filtro = new DashboardFilter { Assegnatario = new List<string> { "Operatore B" } };
        Assert.True(filtro.Corrisponde(CasoBase(), GammaDi));
    }

    [Fact]
    public void FiltroEsito_UsaLoStatoDellUltimaEsecuzione()
    {
        var filtro = new DashboardFilter { Esito = new List<Esito> { Esito.Conforme } };
        Assert.True(filtro.Corrisponde(CasoBase(), GammaDi));

        var altro = new DashboardFilter { Esito = new List<Esito> { Esito.NonConforme } };
        Assert.False(altro.Corrisponde(CasoBase(), GammaDi));
    }

    [Fact]
    public void FiltroCategoriaFunzionalita_UsaIlLookupEsterno()
    {
        IEnumerable<string> CategorieDi(string f) => f == "Diagnosi attiva iniettori" ? new[] { "Motore" } : Array.Empty<string>();

        var filtro = new DashboardFilter { CategoriaFunzionalita = new List<string> { "Motore" } };
        Assert.True(filtro.Corrisponde(CasoBase(), GammaDi, CategorieDi));

        var altro = new DashboardFilter { CategoriaFunzionalita = new List<string> { "Elettronica" } };
        Assert.False(altro.Corrisponde(CasoBase(), GammaDi, CategorieDi));
    }

    /// <summary>Copertura del punto multi-categoria (feedback utente 2026-08-24): una funzionalità con più
    /// categorie corrisponde al filtro se ANCHE UNA SOLA delle sue categorie è tra quelle selezionate.</summary>
    [Fact]
    public void FiltroCategoriaFunzionalita_FunzionalitaConPiuCategorie_BastaUnaSolaCorrispondente()
    {
        IEnumerable<string> CategorieDi(string f) => f == "Diagnosi attiva iniettori" ? new[] { "Motore", "UnitLogger" } : Array.Empty<string>();

        var filtro = new DashboardFilter { CategoriaFunzionalita = new List<string> { "UnitLogger" } };
        Assert.True(filtro.Corrisponde(CasoBase(), GammaDi, CategorieDi));
    }

    [Fact]
    public void PeriodoDataCreazione_RispettaEstremiInclusivi()
    {
        var dentro = new DashboardFilter { DataDa = new DateTime(2026, 1, 1), DataA = new DateTime(2026, 12, 31) };
        Assert.True(dentro.Corrisponde(CasoBase(), GammaDi));

        var fuori = new DashboardFilter { DataDa = new DateTime(2027, 1, 1) };
        Assert.False(fuori.Corrisponde(CasoBase(), GammaDi));
    }

    [Fact]
    public void PeriodoDataCreazione_DataAIncludeIlGiornoScelto_AncheConOrario()
    {
        // Data a a mezzanotte (selettore): il test creato alle 15:30 del giorno scelto resta nel periodo, sia in Corrisponde sia in Applica.
        var tc = CasoBase();
        tc.DataCreazione = new DateTime(2026, 6, 1, 15, 30, 0);
        var giornoScelto = new DashboardFilter { DataA = new DateTime(2026, 6, 1) };

        Assert.True(giornoScelto.Corrisponde(tc, GammaDi));
        Assert.Single(giornoScelto.Applica(new[] { tc }, GammaDi));
        Assert.Empty(new DashboardFilter { DataA = new DateTime(2026, 5, 31) }.Applica(new[] { tc }, GammaDi));
    }

    [Fact]
    public void PiuFiltriInsieme_DevonoCorrispondereTuttiContemporaneamente()
    {
        var filtro = new DashboardFilter { Componente = new List<string> { "Componente-TEST-BCM" }, TestTemplateUid = new List<Guid> { TemplateBanco }, Requisito = new List<string> { "ST12" } };
        Assert.True(filtro.Corrisponde(CasoBase(), GammaDi));

        var filtroConUnMismatch = new DashboardFilter { Componente = new List<string> { "Componente-TEST-BCM" }, TestTemplateUid = new List<Guid> { TemplateVeicolo } };
        Assert.False(filtroConUnMismatch.Corrisponde(CasoBase(), GammaDi));
    }

    [Fact]
    public void HaFiltriAttivi_FalseSoloQuandoTuttoENullODefault()
    {
        Assert.False(new DashboardFilter().HaFiltriAttivi);
        Assert.True(new DashboardFilter { Componente = new List<string> { "X" } }.HaFiltriAttivi);
        Assert.True(new DashboardFilter { DataDa = new DateTime(2026, 1, 1) }.HaFiltriAttivi);
        Assert.True(new DashboardFilter { CampiCustom = { ["custom-1"] = new List<string> { "X" } } }.HaFiltriAttivi);
    }

    [Fact]
    public void FiltroCampoCustom_CorrispondeSoloAiTestConValoreAmmesso()
    {
        var conValore = CasoBase();
        conValore.CampiCustom["custom-1"] = "Rosso";
        var conAltroValore = CasoBase();
        conAltroValore.Id = "TC-2";
        conAltroValore.CampiCustom["custom-1"] = "Blu";
        var senzaValore = CasoBase();
        senzaValore.Id = "TC-3";

        var filtro = new DashboardFilter { CampiCustom = { ["custom-1"] = new List<string> { "Rosso" } } };

        Assert.True(filtro.Corrisponde(conValore, GammaDi));
        Assert.False(filtro.Corrisponde(conAltroValore, GammaDi));
        Assert.False(filtro.Corrisponde(senzaValore, GammaDi));
    }

    [Fact]
    public void FiltroCampoCustom_ListaVuota_NonFiltraSuQuellaChiave()
    {
        var filtro = new DashboardFilter { CampiCustom = { ["custom-1"] = new List<string>() } };
        Assert.True(filtro.Corrisponde(CasoBase(), GammaDi));
    }

    [Fact]
    public void Applica_FiltraUnaListaDiTestCase()
    {
        var altro = CasoBase();
        altro.Id = "TC-2";
        altro.Componente = "Componente-TEST-EDC";

        var risultato = new DashboardFilter { Componente = new List<string> { "Componente-TEST-BCM" } }.Applica(new[] { CasoBase(), altro }, GammaDi);

        var r = Assert.Single(risultato);
        Assert.Equal("TC-1", r.Id);
    }
}
