using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Helper di test: seleziona per Valore le voci di un filtro multi-selezione (come farebbe l'utente nel
/// flyout). Fallisce se il valore non e' fra le opzioni offerte, cosi' un test non "seleziona" mai qualcosa che
/// la UI non permetterebbe di scegliere.</summary>
internal static class FiltroMultiSelectTestExt
{
    public static void Seleziona(this FiltroMultiSelect filtro, params string[] valori)
    {
        foreach (var v in valori)
        {
            var opzione = filtro.Opzioni.SingleOrDefault(o => o.Valore == v);
            Assert.True(opzione != null, $"Opzione '{v}' non presente. Disponibili: {string.Join(", ", filtro.Opzioni.Select(o => o.Valore))}");
            opzione!.Selezionato = true;
        }
    }
}

/// <summary>Filtri della pagina Test a selezione multipla (richiesta utente 6.4.2): stesso schema pulsante +
/// multi-selezione delle altre pagine. Copre percorso SQLite paginato, fallback in memoria, reset, viste
/// salvate (formato nuovo e retrocompatibilita' con il vecchio a valore singolo), quick-filter e persistenza.</summary>
/// <remarks>[Collection("Localization")]: come VisteSalvateTests, usa TextEditService.Handler statico.</remarks>
[Collection("Localization")]
public sealed class TestCaseListFiltriMultiSelectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TestListMulti_" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_TestListMultiCfg_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, Task<string?>>? _textEditOriginale = TextEditService.Handler;

    public void Dispose()
    {
        TextEditService.Handler = _textEditOriginale;
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    private AppState NuovaSessione() =>
        new(new AppConfigService(Path.Combine(_configDir, "config.json")), runBackgroundMaintenance: false);

    /// <summary>A: BCM/Mario Rossi (con Ticketing). B: BCM/Altro. C: BCM/Altro/creato Altro. D: TCU/Terzo, Conforme,
    /// InCorso, High, REQ-D/guasto. Operatore attivo "Mario Rossi" (Master).</summary>
    private AppState CreaStato(string operatoreAttivo = "Mario Rossi")
    {
        var state = NuovaSessione();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = operatoreAttivo, Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore(operatoreAttivo, "1234");

        state.TestCases.Save(new TestCase { Id = "TC-A", Componente = "BCM", Assegnatario = "Mario Rossi", CreatoDa = "Mario Rossi", TicketCollegati = new List<TicketRif> { new() { Riferimento = "TICKET-1" } } }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-B", Componente = "BCM", Assegnatario = "Altro Operatore", CreatoDa = "Mario Rossi" }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-C", Componente = "BCM", Assegnatario = "Altro Operatore", CreatoDa = "Altro Operatore" }, isNew: true);
        var d = new TestCase
        {
            Id = "TC-D", Componente = "TCU", Assegnatario = "Terzo", CreatoDa = "Terzo", StatoLavoro = StatoLavoroTest.InCorso,
            Severita = SeveritaAnomalia.High, RequisitoId = ["REQ-D"], Funzionalita = ["Guasto"],
        };
        d.Esecuzioni.Add(new Esecuzione { Data = DateTime.UtcNow, Esito = Esito.Conforme });
        state.TestCases.Save(d, isNew: true);
        return state;
    }

    private static string[] Ids(TestCaseListViewModel vm) => vm.Righe.Select(t => t.Id).OrderBy(x => x).ToArray();

    [Fact]
    public void OpzioniFiltri_PopolateDalCatalogoEDagliEnum()
    {
        var vm = new TestCaseListViewModel(CreaStato(), _ => { });

        Assert.Equal(new[] { "BCM", "TCU" }, vm.FiltroEcu.Opzioni.Select(o => o.Valore).OrderBy(x => x));
        Assert.Equal(new[] { "Altro Operatore", "Mario Rossi", "Terzo" }, vm.FiltroAssegnatario.Opzioni.Select(o => o.Valore).OrderBy(x => x));
        Assert.Equal(Enum.GetValues<Esito>().Length, vm.FiltroEsito.Opzioni.Count);
        Assert.Equal(Enum.GetValues<StatoLavoroTest>().Length, vm.FiltroStatoLavoro.Opzioni.Count);
        Assert.Equal(Enum.GetValues<SeveritaAnomalia>().Length, vm.FiltroSeverita.Opzioni.Count);
        Assert.Equal(Enum.GetValues<PrioritaOperativa>().Length, vm.FiltroPriorita.Opzioni.Count);
        Assert.Equal(Enum.GetValues<StatoAnomalia>().Length, vm.FiltroStatoAnomalia.Opzioni.Count);
        Assert.False(vm.HaFiltriAttivi);
        Assert.Equal(4, vm.Righe.Count);
    }

    [Fact]
    public void SelezioneMultipla_PercorsoSqlite_ApplicaOrFraValoriEAndFraFiltri()
    {
        var vm = new TestCaseListViewModel(CreaStato(), _ => { });

        vm.FiltroAssegnatario.Seleziona("Altro Operatore", "Terzo");
        Assert.Equal(new[] { "TC-B", "TC-C", "TC-D" }, Ids(vm));
        Assert.True(vm.HaFiltriAttivi);

        vm.FiltroEcu.Seleziona("BCM", "TCU");
        Assert.Equal(new[] { "TC-B", "TC-C", "TC-D" }, Ids(vm));

        vm.FiltroCreatoDa.Seleziona("Mario Rossi", "Terzo");
        Assert.Equal(new[] { "TC-B", "TC-D" }, Ids(vm));
    }

    [Fact]
    public void TuttiIFiltriLista_PercorsoSqlite_FiltranoDavvero()
    {
        var vm = new TestCaseListViewModel(CreaStato(), _ => { });

        vm.FiltroEsito.Seleziona(nameof(Esito.Conforme), nameof(Esito.NonConforme));
        Assert.Equal(new[] { "TC-D" }, Ids(vm));
        vm.FiltroEsito.DeselezionaTutto();
        Assert.Equal(4, vm.Righe.Count);

        vm.FiltroStatoLavoro.Seleziona(nameof(StatoLavoroTest.InCorso));
        Assert.Equal(new[] { "TC-D" }, Ids(vm));
        vm.FiltroStatoLavoro.DeselezionaTutto();

        vm.FiltroSeverita.Seleziona(nameof(SeveritaAnomalia.High), nameof(SeveritaAnomalia.Low));
        Assert.Equal(new[] { "TC-D" }, Ids(vm));
        vm.FiltroSeverita.DeselezionaTutto();

        vm.FiltroRequisito.Seleziona("REQ-D");
        Assert.Equal(new[] { "TC-D" }, Ids(vm));
        vm.FiltroRequisito.DeselezionaTutto();

        vm.FiltroFunzionalita.Seleziona("Guasto");
        Assert.Equal(new[] { "TC-D" }, Ids(vm));
        vm.FiltroFunzionalita.DeselezionaTutto();

        vm.FiltroVeicolo.DeselezionaTutto();
        Assert.False(vm.HaFiltriAttivi);
        Assert.Equal(4, vm.Righe.Count);
    }

    [Fact]
    public void FiltroTipoTest_UsaGliUidComeValore()
    {
        var state = CreaStato();
        var standard = new TestTemplate { Nome = "Standard" };
        var altro = new TestTemplate { Nome = "Altro tipo" };
        state.Master.TestTemplate.Save(new List<TestTemplate> { standard, altro });
        state.TestCases.Save(new TestCase { Id = "TC-T", Componente = "BCM", CreatoDa = "Mario Rossi", TestTemplateUid = standard.Uid }, isNew: true);
        var vm = new TestCaseListViewModel(state, _ => { });

        Assert.Contains("Standard", vm.FiltroTemplate.Opzioni.Select(o => o.Etichetta));
        Assert.Contains("Altro tipo", vm.FiltroTemplate.Opzioni.Select(o => o.Etichetta));
        vm.FiltroTemplate.Seleziona(standard.Uid.ToString());

        Assert.Equal(new[] { "TC-T" }, Ids(vm));
        vm.FiltroTemplate.Seleziona(altro.Uid.ToString());
        Assert.Equal(new[] { "TC-T" }, Ids(vm)); // OR fra i tipi: "Altro tipo" non ha test, resta solo TC-T
    }

    [Fact]
    public void FallbackInMemoria_ConSelezioniMultiple_DaLoStessoRisultatoDelPercorsoSqlite()
    {
        var vm = new TestCaseListViewModel(CreaStato(), _ => { });
        vm.FiltroEcu.Seleziona("BCM", "TCU");
        vm.FiltroAssegnatario.Seleziona("Mario Rossi", "Terzo");
        Assert.Equal(new[] { "TC-A", "TC-D" }, Ids(vm)); // percorso SQLite

        // Il filtro Ticketing impone il percorso legacy (in memoria): stesse liste, stesso significato.
        vm.FiltroHaPolarion = FiltroPolarioneOpzione.ConPolarion;
        Assert.Equal(new[] { "TC-A" }, Ids(vm));
        vm.FiltroHaPolarion = FiltroPolarioneOpzione.SenzaPolarion;
        Assert.Equal(new[] { "TC-D" }, Ids(vm));

        vm.FiltroHaPolarion = FiltroPolarioneOpzione.SenzaPolarion;
        vm.FiltroAssegnatario.DeselezionaTutto();
        vm.FiltroEsito.Seleziona(nameof(Esito.Conforme), nameof(Esito.NonConforme));
        vm.FiltroStatoLavoro.Seleziona(nameof(StatoLavoroTest.InCorso), nameof(StatoLavoroTest.Concluso));
        vm.FiltroSeverita.Seleziona(nameof(SeveritaAnomalia.High));
        vm.FiltroRequisito.Seleziona("REQ-D");
        vm.FiltroFunzionalita.Seleziona("Guasto");
        vm.FiltroCreatoDa.Seleziona("Terzo", "Altro Operatore");
        Assert.Equal(new[] { "TC-D" }, Ids(vm));

        vm.FiltroCreatoDa.DeselezionaTutto();
        vm.FiltroCreatoDa.Seleziona("Altro Operatore"); // D non e' piu' compatibile
        Assert.Empty(vm.Righe);
    }

    [Fact]
    public void FallbackInMemoria_StatoAnomalia_ConSelezioneMultipla()
    {
        var state = CreaStato();
        var tcB = state.TestCases.GetAll().Single(t => t.Id == "TC-B");
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-1", DataApertura = DateTime.Now, Severita = SeveritaAnomalia.High, TestCaseId = "TC-B", TestUid = tcB.Uid, Descrizione = "x",
        });
        var vm = new TestCaseListViewModel(state, _ => { });
        var statoAperta = state.NonConformita.GetAll().Single().StatoWorkflow;

        vm.FiltroStatoAnomalia.Seleziona(statoAperta.ToString());

        Assert.Equal(new[] { "TC-B" }, Ids(vm));
    }

    [Fact]
    public void ReimpostaFiltri_AzzeraTutteLeMultiSelezioni()
    {
        var vm = new TestCaseListViewModel(CreaStato(), _ => { });
        vm.FiltroEcu.Seleziona("BCM");
        vm.FiltroAssegnatario.Seleziona("Altro Operatore", "Terzo");
        vm.FiltroEsito.Seleziona(nameof(Esito.Conforme));
        vm.FiltroSeverita.Seleziona(nameof(SeveritaAnomalia.High));
        Assert.True(vm.HaFiltriAttivi);

        vm.ReimpostaFiltriCommand.Execute(null);

        Assert.False(vm.HaFiltriAttivi);
        Assert.False(vm.FiltroEcu.HaSelezione);
        Assert.False(vm.FiltroAssegnatario.HaSelezione);
        Assert.False(vm.FiltroEsito.HaSelezione);
        Assert.False(vm.FiltroSeverita.HaSelezione);
        Assert.Equal(4, vm.Righe.Count);
        Assert.Equal(LocalizationService.Instance.Traduci("FiltroTutteLeVoci"), vm.FiltroEcu.TestoRiepilogo);
    }

    [Fact]
    public void QuickFilter_AssegnatiAMe_SelezionaSoloLOperatoreESiDisattivaAlSecondoClic()
    {
        var vm = new TestCaseListViewModel(CreaStato(), _ => { });
        vm.FiltroAssegnatario.Seleziona("Terzo");

        vm.FiltraAssegnatiAMeCommand.Execute(null);

        Assert.True(vm.FiltroAssegnatiAMeAttivo);
        Assert.Equal(new[] { "Mario Rossi" }, vm.FiltroAssegnatario.Selezionati); // sostituisce la selezione precedente
        Assert.Equal(new[] { "TC-A" }, Ids(vm));

        vm.FiltraAssegnatiAMeCommand.Execute(null);
        Assert.False(vm.FiltroAssegnatiAMeAttivo);
        Assert.False(vm.FiltroAssegnatario.HaSelezione);
        Assert.Equal(4, vm.Righe.Count);
    }

    [Fact]
    public void QuickFilter_OperatoreSenzaTest_AggiungeLOpzioneEDaZeroRisultati()
    {
        var vm = new TestCaseListViewModel(CreaStato("Nuovo Operatore"), _ => { });
        Assert.DoesNotContain(vm.FiltroCreatoDa.Opzioni, o => o.Valore == "Nuovo Operatore");

        vm.FiltraCreatiDaMeCommand.Execute(null);

        Assert.True(vm.FiltroCreatiDaMeAttivo);
        Assert.Empty(vm.Righe);
    }

    [Fact]
    public void QuickFilter_OperatoreAssenteDalCatalogo_SopravvieAdAggiorna()
    {
        var vm = new TestCaseListViewModel(CreaStato("Nuovo Operatore"), _ => { });
        vm.FiltraCreatiDaMeCommand.Execute(null);
        Assert.Empty(vm.Righe);

        vm.Aggiorna();

        Assert.True(vm.FiltroCreatiDaMeAttivo);
        Assert.Equal(new[] { "Nuovo Operatore" }, vm.FiltroCreatoDa.Selezionati);
        Assert.Empty(vm.Righe); // il filtro resta applicato: non torna a tutti i Test
    }

    [Fact]
    public async Task VistaSalvata_NuovoFormato_ConservaPiuValoriPerFiltro()
    {
        var state = CreaStato();
        var vm = new TestCaseListViewModel(state, _ => { });
        vm.FiltroEcu.Seleziona("BCM", "TCU");
        vm.FiltroAssegnatario.Seleziona("Altro Operatore", "Terzo");
        vm.FiltroSeverita.Seleziona(nameof(SeveritaAnomalia.High), nameof(SeveritaAnomalia.Low));
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Multi");
        await vm.SalvaVistaCorrenteCommand.ExecuteAsync(null);
        var vista = vm.VisteSalvate.Single();
        vm.ReimpostaFiltriCommand.Execute(null);
        Assert.False(vm.HaFiltriAttivi);

        vm.VistaSelezionata = vista;

        Assert.Equal(new[] { "BCM", "TCU" }, vm.FiltroEcu.Selezionati.OrderBy(x => x));
        Assert.Equal(new[] { "Altro Operatore", "Terzo" }, vm.FiltroAssegnatario.Selezionati.OrderBy(x => x));
        Assert.Equal(new[] { nameof(SeveritaAnomalia.High), nameof(SeveritaAnomalia.Low) }, vm.FiltroSeverita.Selezionati.OrderBy(x => x));
        Assert.Equal(new[] { "TC-D" }, Ids(vm));

        // Il valore serializzato sopravvive a un riavvio (config riletta da disco).
        var vmRiavviato = new TestCaseListViewModel(NuovaSessione(), _ => { });
        vmRiavviato.VistaSelezionata = vmRiavviato.VisteSalvate.Single();
        Assert.Equal(new[] { "BCM", "TCU" }, vmRiavviato.FiltroEcu.Selezionati.OrderBy(x => x));
    }

    [Fact]
    public void VistaSalvata_VecchioFormatoAValoreSingolo_SiCaricaComeSelezioneDiUnElemento()
    {
        var state = CreaStato();
        var standard = new TestTemplate { Nome = "Standard" };
        state.Master.TestTemplate.Save(new List<TestTemplate> { standard });
        // Formato scritto dalle versioni precedenti: una sola stringa/enum/Uid per chiave.
        state.Config.VisteSalvate["test-list"] = new List<VistaSalvataFiltri>
        {
            new()
            {
                Nome = "Vecchia",
                Valori = new Dictionary<string, string?>
                {
                    ["Componente"] = "TCU",
                    ["Assegnatario"] = "Terzo",
                    ["CreatoDa"] = "Terzo",
                    ["Esito"] = "Conforme",
                    ["StatoLavoro"] = "InCorso",
                    ["Severita"] = "High",
                    ["Requisito"] = "REQ-D",
                    ["Funzionalita"] = "Guasto",
                    ["TemplateUid"] = standard.Uid.ToString(),
                    ["Testo"] = "",
                },
            },
        };
        state.ConfigService.Save(state.Config);
        var vm = new TestCaseListViewModel(state, _ => { });

        vm.VistaSelezionata = vm.VisteSalvate.Single(v => v.Nome == "Vecchia");

        Assert.Equal(new[] { "TCU" }, vm.FiltroEcu.Selezionati);
        Assert.Equal(new[] { "Terzo" }, vm.FiltroAssegnatario.Selezionati);
        Assert.Equal(new[] { "Terzo" }, vm.FiltroCreatoDa.Selezionati);
        Assert.Equal(new[] { "Conforme" }, vm.FiltroEsito.Selezionati);
        Assert.Equal(new[] { "InCorso" }, vm.FiltroStatoLavoro.Selezionati);
        Assert.Equal(new[] { "High" }, vm.FiltroSeverita.Selezionati);
        Assert.Equal(new[] { "REQ-D" }, vm.FiltroRequisito.Selezionati);
        Assert.Equal(new[] { "Guasto" }, vm.FiltroFunzionalita.Selezionati);
        Assert.Equal(new[] { standard.Uid.ToString() }, vm.FiltroTemplate.Selezionati);
        Assert.Empty(vm.Righe); // nessun test ha il tipo "Standard": l'AND fra i filtri e' rispettato
    }

    [Fact]
    public void VistaSalvata_ValoreNonPiuPresenteFraLeOpzioni_TestualeConservatoEnumIgnoratoSenzaErrori()
    {
        var state = CreaStato();
        state.Config.VisteSalvate["test-list"] = new List<VistaSalvataFiltri>
        {
            new() { Nome = "Fantasma", Valori = new Dictionary<string, string?> { ["Componente"] = "Componente-CANCELLATA", ["Esito"] = "ValoreInesistente" } },
        };
        state.ConfigService.Save(state.Config);
        var vm = new TestCaseListViewModel(state, _ => { });

        vm.VistaSelezionata = vm.VisteSalvate.Single();

        // Testuale: la voce sconosciuta resta selezionata (filtro NON allargato in silenzio -> lista vuota).
        Assert.Equal(new[] { "Componente-CANCELLATA" }, vm.FiltroEcu.Selezionati);
        Assert.Empty(vm.Righe);
        // Enum sconosciuto: ignorato e filtro non attivo.
        Assert.False(vm.FiltroEsito.HaSelezione);
    }

    [Fact]
    public void ImpostaSoloSenzaEcu_AzzeraFiltriAttiviENonPersisteIlFiltro()
    {
        var sessione1 = CreaStato();
        sessione1.TestCases.Save(new TestCase { Id = "TC-SENZA", CreatoDa = "Mario Rossi" }, isNew: true);
        var lista1 = new TestCaseListViewModel(sessione1, _ => { });
        lista1.FiltroEcu.Seleziona("BCM"); // filtro persistito che in AND svuoterebbe la lista

        lista1.ImpostaSoloSenzaEcu();

        Assert.False(lista1.FiltroEcu.HaSelezione);
        Assert.True(lista1.FiltroSoloSenzaEcu);
        Assert.Equal(new[] { "TC-SENZA" }, Ids(lista1));
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var lista2 = new TestCaseListViewModel(sessione2, _ => { });

        Assert.False(lista2.FiltroSoloSenzaEcu); // non riappare come filtro invisibile dopo il riavvio
        Assert.False(lista2.HaFiltriAttivi);
    }

    [Fact]
    public void VistaSelezionata_CambioManualeDeiFiltri_SvuotaIlComboERiselezionareRiapplica()
    {
        var state = CreaStato();
        state.Config.VisteSalvate["test-list"] = new List<VistaSalvataFiltri>
        {
            new() { Nome = "Solo TCU", Valori = new Dictionary<string, string?> { ["Componente"] = "TCU" } },
        };
        state.ConfigService.Save(state.Config);
        var vm = new TestCaseListViewModel(state, _ => { });
        var vista = vm.VisteSalvate.Single();
        vm.VistaSelezionata = vista;
        Assert.Equal(new[] { "TC-D" }, Ids(vm));

        vm.FiltroEcu.Seleziona("BCM"); // modifica manuale

        Assert.Null(vm.VistaSelezionata);
        vm.VistaSelezionata = vista;
        Assert.Equal(new[] { "TC-D" }, Ids(vm)); // riapplicata davvero
    }

    [Fact]
    public void Fallback_InMemoria_HaLoStessoOrdineDelPercorsoSqlite()
    {
        var state = CreaStato();
        foreach (var id in new[] { "TC-Z1", "TC-a2", "TC-Z3", "TC-b4" })
            state.TestCases.Save(new TestCase { Id = id, CreatoDa = "Mario Rossi" }, isNew: true);
        var vm = new TestCaseListViewModel(state, _ => { });
        var ordineSqlite = vm.Righe.Where(t => t.Componente.Count == 0).Select(t => t.Id).ToArray();
        Assert.Equal(4, ordineSqlite.Length);

        vm.FiltroSoloSenzaEcu = true; // forza il percorso legacy in memoria

        Assert.Equal(ordineSqlite, vm.Righe.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task FiltroTestoDigitato_ApplicaLaQueryDopoIlDebounce()
    {
        var originale = TestCaseListViewModel.DebounceTesto;
        TestCaseListViewModel.DebounceTesto = TimeSpan.FromMilliseconds(80);
        try
        {
            var vm = new TestCaseListViewModel(CreaStato(), _ => { });
            vm.FiltroTestoDigitato = "TC-D";
            Assert.Equal(4, vm.Righe.Count); // non ancora applicato

            var scadenza = DateTime.UtcNow.AddSeconds(5);
            while (vm.Righe.Count != 1 && DateTime.UtcNow < scadenza) await Task.Delay(20);

            Assert.Equal("TC-D", vm.FiltroTesto);
            Assert.Equal(new[] { "TC-D" }, Ids(vm));
        }
        finally { TestCaseListViewModel.DebounceTesto = originale; }
    }

    /// <summary>Review 2026-09-30: un debounce ancora pendente non deve sopravvivere al reset dei filtri e
    /// riapplicare il testo digitato subito prima.</summary>
    [Fact]
    public async Task ReimpostaFiltri_ConDebouncePendente_NonRiapplicaIlTestoDigitato()
    {
        var originale = TestCaseListViewModel.DebounceTesto;
        TestCaseListViewModel.DebounceTesto = TimeSpan.FromMilliseconds(80);
        try
        {
            var vm = new TestCaseListViewModel(CreaStato(), _ => { });
            vm.FiltroTestoDigitato = "TC-D"; // debounce pendente, FiltroTesto ancora ""
            vm.ReimpostaFiltriCommand.Execute(null);
            Assert.Equal("", vm.FiltroTestoDigitato);

            await Task.Delay(400);

            Assert.Equal("", vm.FiltroTesto);
            Assert.Equal("", vm.FiltroTestoDigitato);
            Assert.Equal(4, vm.Righe.Count);
        }
        finally { TestCaseListViewModel.DebounceTesto = originale; }
    }

    [Fact]
    public async Task ImpostaSoloSenzaEcu_ConDebouncePendente_NonRiapplicaIlTestoDigitato()
    {
        var originale = TestCaseListViewModel.DebounceTesto;
        TestCaseListViewModel.DebounceTesto = TimeSpan.FromMilliseconds(80);
        try
        {
            var vm = new TestCaseListViewModel(CreaStato(), _ => { });
            vm.FiltroTestoDigitato = "TC-D";
            vm.ImpostaSoloSenzaEcu();

            await Task.Delay(400);

            Assert.Equal("", vm.FiltroTesto);
            Assert.Equal("", vm.FiltroTestoDigitato);
        }
        finally { TestCaseListViewModel.DebounceTesto = originale; }
    }

    [Fact]
    public void QuickFilterAssegnatiAMe_ToggleDaViewRiflettoLoStatoReale()
    {
        var vm = new TestCaseListViewModel(CreaStato(), _ => { });

        vm.FiltroAssegnatiAMeAttivo = true;
        Assert.Equal(new[] { "Mario Rossi" }, vm.FiltroAssegnatario.Selezionati);
        Assert.True(vm.FiltroAssegnatiAMeAttivo);

        vm.FiltroAssegnatiAMeAttivo = false;
        Assert.False(vm.FiltroAssegnatario.HaSelezione);
    }

    [Fact]
    public void StatoPersistito_ConSelezioniMultiple_SopravviveAUnRiavvioDellApp()
    {
        var sessione1 = CreaStato();
        var lista1 = new TestCaseListViewModel(sessione1, _ => { });
        lista1.FiltroEcu.Seleziona("BCM", "TCU");
        lista1.FiltroCreatoDa.Seleziona("Terzo");
        lista1.FiltroEsito.Seleziona(nameof(Esito.Conforme));
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var lista2 = new TestCaseListViewModel(sessione2, _ => { });

        Assert.Equal(new[] { "BCM", "TCU" }, lista2.FiltroEcu.Selezionati.OrderBy(x => x));
        Assert.Equal(new[] { "Terzo" }, lista2.FiltroCreatoDa.Selezionati);
        Assert.Equal(new[] { "Conforme" }, lista2.FiltroEsito.Selezionati);
        Assert.Equal(new[] { "TC-D" }, Ids(lista2));
    }

    [Fact]
    public void Aggiorna_ConSelezioneAttiva_FaUnaSolaPassataDiFiltro()
    {
        var state = CreaStato();
        var vm = new TestCaseListViewModel(state, _ => { });
        vm.FiltroEcu.Seleziona("BCM");
        state.TestCases.Save(new TestCase { Id = "TC-E", Componente = "Componente-NUOVA", CreatoDa = "Mario Rossi" }, isNew: true);
        var righeCambiate = 0;
        vm.Righe.CollectionChanged += (_, _) => righeCambiate++;

        vm.Aggiorna();

        Assert.Equal(new[] { "TC-A", "TC-B", "TC-C" }, Ids(vm));
        Assert.Contains(vm.FiltroEcu.Opzioni, o => o.Valore == "Componente-NUOVA");
        Assert.Equal(new[] { "BCM" }, vm.FiltroEcu.Selezionati); // la selezione sopravvive alla Ripopola
        Assert.Equal(1, righeCambiate); // ReplaceWith = un solo Reset, quindi una sola passata
    }
}
