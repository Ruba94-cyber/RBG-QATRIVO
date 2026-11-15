using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Campi strumento in visualizzazione: ogni riquadro e' aperto se e solo se contiene almeno un campo compilato (valore salvato
/// non vuoto), i campi vuoti si nascondono, i campi non sono modificabili e lo stato si ricalcola al cambio modifica/lettura.
/// In modifica resta tutto chiuso di default.</summary>
[Collection("Localization")]
public sealed class CampiStrumentoLetturaTests : IDisposable
{
    private const string Chiave = "UnitLogger::";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CampiLettura_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _opened = new();

    public void Dispose()
    {
        foreach (var o in _opened) { try { o.Dispose(); } catch { } }
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private static List<DefinizioneCampoStrumento> Campi() => new()
    {
        new() { Nome = "Diretto1", Ambito = TipoAmbitoCampo.PerOperazione, Ordine = 1 },
        new() { Nome = "Diretto2", Ambito = TipoAmbitoCampo.PerOperazione, Ordine = 2 },
        new() { Nome = "Spunta", Ambito = TipoAmbitoCampo.PerOperazione, Tipo = TipoCampoStrumento.Checkbox, Ordine = 3 },
        new() { Nome = "InGruppo", Ambito = TipoAmbitoCampo.PerOperazione, Gruppo = "G1", Ordine = 4 },
        new() { Nome = "DelTest", Ambito = TipoAmbitoCampo.PerTest, Ordine = 5 },
        new() { Nome = "RevInGruppo", Ambito = TipoAmbitoCampo.PerRevisione, Gruppo = "GR", Ordine = 6 },
    };

    /// <summary>Editor di un test nuovo con 3 operazioni; solo UnitLogger selezionato. Si passa in lettura con InModifica = false.</summary>
    private TestCaseEditorViewModel CreaVm()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var strumenti = state.Master.Strumenti.Load();
        strumenti.Single(s => s.Nome == "UnitLogger").Campi = Campi();
        state.Master.Strumenti.Save(strumenti);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        _opened.Add(state);
        _opened.Add(vm);
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        foreach (var strumento in vm.StrumentiOptions) strumento.Selezionato = strumento.Valore == "UnitLogger";
        while (vm.RevisioneCorrente!.Condizioni.Count < 3) vm.AggiungiCondizioneCommand.Execute(null);
        return vm;
    }

    private static Condizione Op(TestCaseEditorViewModel vm, int i) => vm.RevisioneCorrente!.Condizioni[i];

    [Fact]
    public void Lettura_RiquadroConValoriEspanso_SenzaValoriChiuso()
    {
        var vm = CreaVm();
        Op(vm, 0).ValoriStrumenti[Chiave + "Diretto1"] = "valore";
        vm.InModifica = false;

        Assert.True(Op(vm, 0).HaCampiStrumentoCompilati);
        Assert.True(Op(vm, 0).ParametriAperti);
        Assert.False(Op(vm, 1).HaCampiStrumentoCompilati);
        Assert.False(Op(vm, 1).ParametriAperti);
        Assert.False(Op(vm, 2).ParametriAperti);
        Assert.True(vm.NascondiCampiVuoti);
        Assert.False(vm.EspandiParametriOperazione); // l'apertura per contenuto e' per riquadro, non globale
    }

    [Fact]
    public void Lettura_ValoreSoloSpaziVuoto_NonContaComeCompilato()
    {
        var vm = CreaVm();
        Op(vm, 0).ValoriStrumenti[Chiave + "Diretto1"] = "   ";
        Op(vm, 1).ValoriStrumenti[Chiave + "Diretto1"] = "";
        vm.InModifica = false;

        Assert.False(Op(vm, 0).ParametriAperti);
        Assert.False(Op(vm, 1).ParametriAperti);
    }

    [Fact]
    public void Lettura_CheckboxFalseSalvatoEsplicitamenteConta_CheckboxMaiToccatoNo()
    {
        var vm = CreaVm();
        Op(vm, 0).ValoriStrumenti[Chiave + "Spunta"] = "false"; // salvato esplicitamente
        vm.InModifica = false;

        Assert.True(Op(vm, 0).ParametriAperti);
        Assert.False(Op(vm, 1).ParametriAperti); // chiave assente
    }

    [Fact]
    public void Lettura_GruppoConValoriAperto_GruppoSenzaValoriChiuso_EdApreIlRiquadroPrincipale()
    {
        var vm = CreaVm();
        Op(vm, 0).ValoriStrumenti[Chiave + "InGruppo"] = "guasto";
        vm.InModifica = false;

        var gruppoCompilato = Assert.Single(Op(vm, 0).GruppiVisual!);
        var gruppoVuoto = Assert.Single(Op(vm, 1).GruppiVisual!);
        Assert.True(gruppoCompilato.HaCampiCompilati);
        Assert.True(gruppoCompilato.Aperto);
        Assert.True(Op(vm, 0).ParametriAperti); // un campo compilato solo nel gruppo apre anche il riquadro principale
        Assert.False(gruppoVuoto.Aperto);
        Assert.False(Op(vm, 1).ParametriAperti);
    }

    [Fact]
    public void Lettura_RiquadroCampiDelTest_ApertoSoloSeContieneValori()
    {
        var vm = CreaVm();
        vm.InModifica = false;
        Assert.False(vm.CampiStrumentiSpecificiAperto);
        Assert.True(vm.MostraNessunCampoCompilatoTest);

        vm.TestCase.ValoriStrumentiTest[Chiave + "DelTest"] = "x";
        vm.InModifica = true;
        vm.InModifica = false;
        Assert.True(vm.CampiStrumentiSpecificiAperto);
        Assert.False(vm.MostraNessunCampoCompilatoTest);
    }

    [Fact]
    public void Lettura_CampiNonModificabili_InModificaSiTornaModificabili()
    {
        var vm = CreaVm();
        vm.InModifica = false;
        var tutti = TuttiICampi(vm).ToList();
        Assert.NotEmpty(tutti);
        Assert.All(tutti, c => Assert.False(c.Modificabile));

        vm.InModifica = true;
        Assert.All(TuttiICampi(vm), c => Assert.True(c.Modificabile));
    }

    [Fact]
    public void Modifica_TuttoChiusoDiDefault_AncheConValori_EAperturaManualeScartataTornandoInLettura()
    {
        var vm = CreaVm();
        Op(vm, 0).ValoriStrumenti[Chiave + "Diretto1"] = "valore";
        Assert.True(vm.InModifica);
        Assert.False(Op(vm, 0).ParametriAperti);
        Assert.All(Op(vm, 0).GruppiVisual!, g => Assert.False(g.Aperto));
        Assert.False(vm.CampiStrumentiSpecificiAperto);

        Op(vm, 1).ParametriAperti = true; // apertura manuale in modifica
        vm.InModifica = false;
        Assert.True(Op(vm, 0).ParametriAperti);
        Assert.False(Op(vm, 1).ParametriAperti); // ricalcolato per contenuto

        vm.InModifica = true;
        Assert.False(Op(vm, 0).ParametriAperti); // di nuovo chiuso in modifica
    }

    [Fact]
    public void Lettura_MostraAncheVuoti_ApreTuttiIRiquadriEMostraTuttiICampi_EPoiTornaPerContenuto()
    {
        var vm = CreaVm();
        Op(vm, 0).ValoriStrumenti[Chiave + "Diretto1"] = "valore";
        vm.InModifica = false;
        Assert.True(vm.NascondiCampiVuoti);
        var sporcoPrima = vm.ModificheNonSalvate;

        vm.ParametriApertiInLettura = true;
        Assert.False(vm.NascondiCampiVuoti);
        Assert.True(Op(vm, 0).ParametriAperti);
        Assert.True(Op(vm, 1).ParametriAperti);
        Assert.True(vm.CampiStrumentiSpecificiAperto);
        Assert.All(Op(vm, 1).GruppiVisual!, g => Assert.True(g.Aperto));
        Assert.Equal(sporcoPrima, vm.ModificheNonSalvate); // solo presentazione

        vm.ParametriApertiInLettura = false;
        Assert.True(vm.NascondiCampiVuoti);
        Assert.True(Op(vm, 0).ParametriAperti);
        Assert.False(Op(vm, 1).ParametriAperti);
    }

    [Fact]
    public void ValidazioneApreTuttiIRiquadriAnchePerIVuotiInModifica()
    {
        var vm = CreaVm();
        Assert.False(Op(vm, 1).ParametriAperti);
        vm.ApriParametriPerValidazione();
        Assert.True(Op(vm, 0).ParametriAperti);
        Assert.True(Op(vm, 1).ParametriAperti);
        Assert.True(vm.CampiStrumentiSpecificiAperto);
        Assert.True(Op(vm, 1).GruppiVisual!.All(g => g.Aperto));
        Assert.False(vm.NascondiCampiVuoti); // in modifica i campi non si nascondono mai
    }

    [Fact]
    public void ConverterNascondeICampiVuotiEIGruppiSenzaValori_SoloQuandoRichiesto()
    {
        var valori = new Dictionary<string, string> { ["UnitLogger::A"] = "x" };
        var piena = new CampoStrumentoValore(valori, "UnitLogger", new DefinizioneCampoStrumento { Nome = "A" }, materializzaDefault: false);
        var vuota = new CampoStrumentoValore(valori, "UnitLogger", new DefinizioneCampoStrumento { Nome = "B" }, materializzaDefault: false);
        var campi = new List<CampoStrumentoValore> { piena, vuota };
        var gruppi = new List<GruppoCampiStrumento>
        {
            new() { Nome = "Pieno", Campi = new() { piena } },
            new() { Nome = "Vuoto", Campi = new() { vuota } },
        };
        object? Converti(object lista, bool nascondi) => CampiSeEspansoConverter.Instance.Convert(
            new List<object?> { lista, true, nascondi }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Same(campi, Converti(campi, false));
        Assert.Equal(new[] { piena }, Assert.IsAssignableFrom<IEnumerable<CampoStrumentoValore>>(Converti(campi, true)));
        Assert.Equal(new[] { "Pieno" }, Assert.IsAssignableFrom<IEnumerable<GruppoCampiStrumento>>(Converti(gruppi, true)).Select(g => g.Nome));
        Assert.Null(CampiSeEspansoConverter.Instance.Convert(new List<object?> { campi, false, true }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IEnumerable<CampoStrumentoValore> TuttiICampi(TestCaseEditorViewModel vm)
    {
        foreach (var c in vm.CampiTestCostanti) yield return c;
        foreach (var g in vm.GruppiTestCostanti) foreach (var c in g.Campi) yield return c;
        foreach (var c in vm.CampiRevisioneCostanti) yield return c;
        foreach (var g in vm.GruppiRevisioneCostanti) foreach (var c in g.Campi) yield return c;
        foreach (var esecuzione in vm.Esecuzioni)
            foreach (var op in esecuzione.Condizioni)
            {
                foreach (var c in op.CampiVisual ?? new()) yield return c;
                foreach (var g in op.GruppiVisual ?? new()) foreach (var c in g.Campi) yield return c;
            }
    }
}
