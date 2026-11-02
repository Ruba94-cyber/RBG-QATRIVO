using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public class HelpViewModelTests
{
    [Fact]
    public void Argomenti_SonoFiltratiInBaseAlRuolo()
    {
        var ruolo = RuoloOperatore.Master;
        var vm = new HelpViewModel(() => ruolo);
        Assert.Contains(vm.Sezioni, s => s.Titolo == "Campi di sistema e Riferimenti");

        ruolo = RuoloOperatore.Standard;
        vm.AggiornaRuolo();
        Assert.DoesNotContain(vm.Sezioni, s => s.Titolo == "Campi di sistema e Riferimenti");
        Assert.Contains(vm.Sezioni, s => s.Titolo == "Creare un test");

        ruolo = RuoloOperatore.Viewer;
        vm.AggiornaRuolo();
        Assert.DoesNotContain(vm.Sezioni, s => s.Titolo == "Creare un test");
        Assert.Contains(vm.Sezioni, s => s.Titolo == "Punteggio di conformità e Audit");
    }

    [Fact]
    public void Ricerca_FiltraTitoloETesto_EIlContestoRiordina()
    {
        var vm = new HelpViewModel(() => RuoloOperatore.Master) { Contesto = "anomalie" };
        Assert.Equal("Segnalazioni e anomalie", vm.Sezioni[0].Titolo);

        vm.Ricerca = "Evidenze";
        Assert.Single(vm.Sezioni);
        Assert.Equal("Evidenze e Ticketing", vm.Sezioni[0].Titolo);
        vm.Ricerca = "termine-che-non-esiste";
        Assert.True(vm.NessunRisultato);
    }

    [Fact]
    public void Reset_CancellaRicercaEContestoTraDueSessioni()
    {
        var vm = new HelpViewModel(() => RuoloOperatore.Viewer) { Contesto = "test", Ricerca = "ZIP" };

        vm.Reset();

        Assert.Equal("", vm.Ricerca);
        Assert.Equal("home", vm.Contesto);
        Assert.True(vm.Sezioni.Count > 1);
    }

    [Fact]
    public void Argomenti_NuoviDel660_RispettanoIRuoli()
    {
        var viewer = new HelpViewModel(() => RuoloOperatore.Viewer);
        Assert.Contains(viewer.Sezioni, s => s.Titolo == "Scorciatoie da tastiera");
        Assert.Contains(viewer.Sezioni, s => s.Titolo == "Bozze di emergenza e recupero");
        Assert.DoesNotContain(viewer.Sezioni, s => s.Titolo == "Conflitti fra postazioni");

        var master = new HelpViewModel(() => RuoloOperatore.Master);
        Assert.Contains(master.Sezioni, s => s.Titolo == "Conflitti fra postazioni");
    }

    [Fact]
    public void Argomenti_ItalianoEInglese_HannoLoStessoIndice()
    {
        var it = HelpViewModel.TitoliArgomenti("it");
        var en = HelpViewModel.TitoliArgomenti("en");

        Assert.Equal(it.Count, en.Count);
        Assert.All(en, titolo => Assert.False(string.IsNullOrWhiteSpace(titolo)));
        Assert.Equal("Bulk actions and saved views", en[12]);
        Assert.Equal("Azioni di massa e viste salvate", it[12]);
    }
}
