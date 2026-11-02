using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>UX-4: anteprima prima degli import che applicano direttamente. guasto su Requisiti (Excel e CSV): conteggi,
/// annullamento senza modifiche, nessuna domanda se non si aggiunge nulla. Log UnitLogger su Segnalazione e Test: domanda solo
/// quando un valore non vuoto e diverso verrebbe sostituito, e WU per operazione tocca solo la riga cliccata. Lista di
/// testo da file: le righe gia presenti nella lista vengono saltate.</summary>
/// <remarks>[Collection("Localization")]: mutano ConfirmService.Handler, statico e condiviso da tutto il processo (stesso
/// motivo di AnomalieVisteSalvateTests). Il gestore precedente viene ripristinato in Dispose.</remarks>
[Collection("Localization")]
public sealed class ImportAnteprimaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ImportAnteprima_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;
    private readonly List<string> _richieste = new();

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    /// <summary>Risponde a ogni conferma con <paramref name="si"/> e registra titolo e testo di ciascuna richiesta.</summary>
    private void RispondiConferme(bool si)
    {
        _richieste.Clear();
        ConfirmService.Handler = (titolo, messaggio, _, _) =>
        {
            _richieste.Add(titolo + "\n" + messaggio);
            return Task.FromResult(si);
        };
    }

    private static string Annullato => LocalizationService.Instance.Traduci("DtcImportAnnullato");

    // ---- guasto su Requisiti ----------------------------------------------------------------------------------------

    private AppState CreaStatoRequisiti()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    private static Requisito Extra(string id)
    {
        var r = new Requisito { Id = id, Testo = "t" };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    /// <summary>Scheda Extra in modifica: D1 ha gia P0001/ECM, D2 e' vuoto; S1 e' uno Standard e non e' ammesso.</summary>
    private RequisitiViewModel ApriRequisitiD4d()
    {
        var state = CreaStatoRequisiti();
        var d1 = Extra("D1");
        d1.Guasto = new List<DtcRequisito> { new() { Codice = "P0001", Componente = "ECM" } };
        state.Master.Requisiti.Save(new List<Requisito> { d1, Extra("D2"), new() { Id = "S1", Testo = "t" } });
        var vm = new RequisitiViewModel(state, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        return vm;
    }

    private static Requisito Trova(RequisitiViewModel vm, string id) => vm.Items.Single(r => r.Id == id);

    /// <summary>Righe: D1 P0001 gia presente; D1 P0002 e D2 P0003 nuovi; S1 non ammesso, XX inesistente, D1 senza guasto
    /// (3 errori); D1 P0002 ripetuto nel file.</summary>
    private const string CsvMisto =
        "Requisito;guasto;componente\nD1;P0001;ECM\nD1;P0002;ECM\nD2;P0003;BCM\nS1;P0004;ECM\nXX;P0005;ECM\nD1;;ECM\nD1;P0002;ECM\n";

    [Fact]
    public async Task Guasto_Anteprima_MostraIConteggiEApplicaSoloDopoConferma()
    {
        using var vm = ApriRequisitiD4d();
        RispondiConferme(si: true);

        await vm.ImportaDtcDaRighe(DtcImportService.LeggiCsv(CsvMisto), massivo: true);

        var testo = Assert.Single(_richieste);
        Assert.Contains("Requisiti toccati: 2", testo);
        Assert.Contains("Guasto nuovi da aggiungere: 2", testo);
        Assert.Contains("Già presenti, saltati: 1", testo);
        Assert.Contains("Ripetuti nel file, saltati: 1", testo);
        Assert.Contains("Righe con errore, non importate: 3", testo);
        Assert.Equal(2, vm.UltimoReportDtc!.Importate.Count);
        Assert.Equal(new[] { "P0001", "P0002" }, vm.DtcDi(Trova(vm, "D1")).Select(v => v.Codice).OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(new[] { "P0003" }, vm.DtcDi(Trova(vm, "D2")).Select(v => v.Codice));
    }

    [Fact]
    public async Task Guasto_Annulla_NonAggiungeNessunDtcENessunReportFinale()
    {
        using var vm = ApriRequisitiD4d();
        RispondiConferme(si: false);

        await vm.ImportaDtcDaRighe(DtcImportService.LeggiCsv(CsvMisto), massivo: true);

        Assert.Single(_richieste);
        Assert.Equal(new[] { "P0001" }, vm.DtcDi(Trova(vm, "D1")).Select(v => v.Codice));
        Assert.Empty(vm.DtcDi(Trova(vm, "D2")));
        Assert.Null(vm.UltimoReportDtc);
        Assert.Equal(Annullato, vm.Messaggio);
    }

    [Fact]
    public async Task Guasto_CsvAnnullatoDallAnteprima_LasciaIDtcInvariati()
    {
        using var vm = ApriRequisitiD4d();
        var percorso = Path.Combine(_dir, "dtc_anteprima.csv");
        File.WriteAllText(percorso, CsvMisto);
        RispondiConferme(si: false);

        await vm.ImportaDtcDaCsvAsync(percorso, massivo: true);

        Assert.Single(_richieste);
        Assert.Equal(new[] { "P0001" }, vm.DtcDi(Trova(vm, "D1")).Select(v => v.Codice));
        Assert.Empty(vm.DtcDi(Trova(vm, "D2")));
        Assert.False(vm.ImportDtcInCorso);
        Assert.Equal(Annullato, vm.Messaggio);
    }

    [Fact]
    public async Task Guasto_SenzaDtcNuovi_NonChiedeNienteEMostraIlReportFinale()
    {
        using var vm = ApriRequisitiD4d();
        RispondiConferme(si: false);

        await vm.ImportaDtcDaRighe(DtcImportService.LeggiCsv("Requisito;guasto;componente\nD1;P0001;ECM\n"), massivo: true);

        Assert.Empty(_richieste);
        Assert.Equal(1, vm.UltimoReportDtc!.GiaPresenti);
        Assert.Equal(new[] { "P0001" }, vm.DtcDi(Trova(vm, "D1")).Select(v => v.Codice));
    }

    [Fact]
    public void Guasto_AnteprimaDalReport_SeparaGiaPresentiRipetizioniEErrori()
    {
        var esistenti = new Dictionary<string, IReadOnlyList<DtcRequisito>>(StringComparer.OrdinalIgnoreCase)
        {
            ["D1"] = new List<DtcRequisito> { new() { Codice = "P0001", Componente = "ECM" } },
            ["D2"] = Array.Empty<DtcRequisito>(),
        };
        var righe = DtcImportService.LeggiCsv("Requisito;guasto;componente\nD1;P0001;ECM\nD1;P0002;ECM\nD2;P0003;ECM\nD2;P0003;ECM\nD9;P1;ECM\n");

        var report = DtcImportRequisitoService.Interpreta(righe, id => esistenti.TryGetValue(id, out var v) ? v : null);
        var anteprima = AnteprimaDtcImport.Da(report);

        Assert.Equal(2, anteprima.RequisitiToccati);
        Assert.Equal(2, anteprima.DtcNuovi);
        Assert.Equal(1, anteprima.GiaPresenti);
        Assert.Equal(1, anteprima.DuplicatiNelFile);
        Assert.Equal(1, anteprima.RigheConErrore);
        Assert.Contains("D9", Assert.Single(anteprima.PrimiErrori));
    }

    // ---- Log UnitLogger su Segnalazione di rete --------------------------------------------------------------------------

    private AppState CreaStatoUdt()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "shared")); state.InizializzaCartella(Path.Combine(_dir, "shared"));
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", ModificatoDa = "Seed" } });
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "UnitLogger", Campi = new List<DefinizioneCampoStrumento>
            {
                new() { Nome = "Pacchetto", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerOperazione },
                new() { Nome = "Versione Pacchetto", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerOperazione },
            } }
        });
        var operatori = state.Master.Operatori.Load();
        var op = new Operatore { Nome = "Operatore Rete", Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(op); operatori.Add(op); state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Operatore Rete", "1234"));
        return state;
    }

    // ---- Log UnitLogger sul Test (WU per operazione) ---------------------------------------------------------------------

    private TestCaseEditorViewModel ApriTestConDueOperazioni(out Condizione prima, out Condizione seconda)
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.StrumentiOptions.Single(s => s.Valore == "UnitLogger").Selezionato = true;
        prima = vm.RevisioneCorrente!.Condizioni[0];
        vm.AggiungiCondizioneCommand.Execute(null);
        seconda = vm.RevisioneCorrente.Condizioni.Last();
        return vm;
    }

    // ---- Lista di testo da file -----------------------------------------------------------------------------------

    [Fact]
    public void ListaTesto_ImportDaFile_SaltaLeRigheGiaPresentiNellaLista()
    {
        var (daAggiungere, duplicate) = ListaTestoEditorWindow.SeparaDuplicatiImportFile(
            new[] { "TX 01", "RX 02" }, new[] { "RX 02", "TX 03", "TX 01", "TX 04", "TX 03" });

        // Le ripetizioni dentro il file restano (un trace le usa di proposito); saltate solo le righe gia nella lista.
        Assert.Equal(new[] { "TX 03", "TX 04", "TX 03" }, daAggiungere);
        Assert.Equal(2, duplicate);
    }

    [Fact]
    public void ListaTesto_ImportDaFileSuListaVuota_AggiungeTutteLeRighe()
    {
        var (daAggiungere, duplicate) = ListaTestoEditorWindow.SeparaDuplicatiImportFile(Array.Empty<string>(), new[] { "A", "B", "A" });

        Assert.Equal(new[] { "A", "B", "A" }, daAggiungere);
        Assert.Equal(0, duplicate);
    }
}
