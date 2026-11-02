using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>6.5.2, editor Test con guasto Injection: (1) senza strumento Da testare niente righe operazione libere/vuote ne Funzionalita, solo le
/// coppie Validazione/Devalidazione di "Aggiungi guasto"; (2) con uno strumento Da testare la Funzionalita resta solo sulle sue operazioni;
/// (3) Veicolo/componente del test preselezionati nel blocco (prima la coppia senza guasto); (4) copertura Veicolo x componente con almeno un guasto.</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class DtcInjectionSoloOperazioniWizardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_DtcSoloOp_" + Guid.NewGuid().ToString("N"));

    public DtcInjectionSoloOperazioniWizardTests() => SeedDemoDataService.Genera(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var operatore = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        state.ImpostaOperatore(operatore.Nome, "1234");
        return state;
    }

    /// <summary>Il template "Validazione Banco" dell'utente: strumenti preselezionati configurati, wizard fino al passo operazioni.</summary>
    private static TestCaseEditorViewModel Wizard(AppState state, params string[] preselezionati)
    {
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        var tpl = vm.TemplatesDisponibili[0];
        tpl.StrumentiPreselezionati = preselezionati.ToList();
        vm.SceglieTemplateCommand.Execute(tpl);
        return vm;
    }

    private static void ImpostaVeicoloEEcu(AppState state, TestCaseEditorViewModel vm, params string[] componente)
    {
        vm.VeicoloSelezionato = vm.VeicoliDisponibili[0];
        vm.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        foreach (var e in componente) vm.AggiungiEcuCommand.Execute(e);
    }

    [Fact]
    public void DtcInjectionEVenus_SenzaDaTestare_NienteRigheLibereNeFunzionalita_SoloCoppieDtc()
    {
        var state = CreateState();
        var vm = Wizard(state, "ValidationTool", "Guasto Injection");

        Assert.True(vm.UsaStrumentoDtcInjection);
        Assert.False(vm.HaStrumentoDaTestare);
        Assert.True(vm.SoloOperazioniDtc);
        Assert.Empty(vm.RevisioneCorrente!.Condizioni);               // l'operazione vuota iniziale del wizard non resta
        Assert.False(vm.MostraCampoFunzionalita);
        Assert.False(vm.FunzionalitaObbligatoria);
        Assert.False(vm.FunzionalitaOperazioneObbligatoria);
        Assert.False(vm.MostraAggiungiOperazioneLibera);              // "+ Operazione" libero nascosto

        vm.AggiungiCondizioneCommand.Execute(null);                   // anche se invocato: nessuna riga vuota
        Assert.Empty(vm.RevisioneCorrente.Condizioni);

        ImpostaVeicoloEEcu(state, vm, "Componente-TEST-BCM");
        Assert.Equal("Componente-TEST-BCM", vm.DtcInsEcu);                   // componente del test preselezionata
        Assert.Null(vm.AggiungiDtcInjection(vm.DtcInsEcu, "P0100", null, vm.DtcInsVeicolo));
        Assert.Equal(new[] { PassoDtc.Validazione, PassoDtc.Devalidazione }, vm.RevisioneCorrente.Condizioni.Select(c => c.DtcPasso!.Value).ToArray());
        Assert.All(vm.RevisioneCorrente.Condizioni, c => { Assert.True(c.DtcDaStrumento); Assert.Equal("", c.Funzionalita); });
    }

    [Fact]
    public void SoloDtcInjection_ConOperazioneVuotaGiaPresente_VieneRimossaSelezionandoLoStrumento()
    {
        var state = CreateState();
        var vm = Wizard(state, "ValidationTool");
        var vuota = Assert.Single(vm.RevisioneCorrente!.Condizioni);   // senza guasto Injection l'operazione libera iniziale esiste
        Assert.True(vm.MostraAggiungiOperazioneLibera);

        vm.StrumentiOptions.First(o => o.Valore == "Guasto Injection").Selezionato = true;

        Assert.True(vm.SoloOperazioniDtc);
        Assert.DoesNotContain(vuota, vm.RevisioneCorrente.Condizioni);
        Assert.Empty(vm.RevisioneCorrente.Condizioni);
        Assert.Empty(vm.CondizioniVisual);
    }

    [Fact]
    public void OperazioneLiberaCompilata_NonVieneMaiRimossa_SelezionandoDtcInjection()
    {
        var state = CreateState();
        var vm = Wizard(state, "ValidationTool");
        vm.RevisioneCorrente!.Condizioni[0].Nota = "scritta dall'utente";

        vm.StrumentiOptions.First(o => o.Valore == "Guasto Injection").Selezionato = true;

        Assert.Single(vm.RevisioneCorrente.Condizioni);
    }

    [Fact]
    public void ConStrumentoDaTestare_LaFunzionalitaCompareSoloSulleSueOperazioni_NonSuiDtc()
    {
        var state = CreateState();
        var vm = Wizard(state, "ValidationTool", "Guasto Injection");
        ImpostaVeicoloEEcu(state, vm, "Componente-TEST-BCM");
        vm.NuovaFunzionalita = vm.FunzionalitaDisponibili[0];
        vm.AggiungiFunzionalitaCommand.Execute(null);
        vm.NuovaFunzionalita = vm.FunzionalitaDisponibili[1];
        vm.AggiungiFunzionalitaCommand.Execute(null);
        Assert.True(vm.SoloOperazioniDtc);                            // ancora nessun Da testare: Funzionalita nascosta

        vm.StrumentiOptions.First(o => o.Valore == "UnitLogger").Selezionato = true;
        Assert.True(vm.HaStrumentoDaTestare);
        Assert.False(vm.SoloOperazioniDtc);
        Assert.True(vm.MostraAggiungiOperazioneLibera);
        Assert.True(vm.MostraCampoFunzionalita);
        Assert.Null(vm.AggiungiDtcInjection("Componente-TEST-BCM", "P0100", null, vm.DtcInsVeicolo));

        var unitlogger = vm.RevisioneCorrente!.Condizioni.Where(c => c.Strumento == "UnitLogger").ToList();
        Assert.Equal(vm.TestCase.Funzionalita.OrderBy(x => x), unitlogger.Select(c => c.Funzionalita).OrderBy(x => x));   // una per funzionalita
        var guasto = vm.RevisioneCorrente.Condizioni.Where(c => c.Strumento == "Guasto Injection").ToList();
        Assert.Equal(2, guasto.Count);
        Assert.All(guasto, c => { Assert.True(c.DtcDaStrumento); Assert.True(string.IsNullOrWhiteSpace(c.Funzionalita)); });
        Assert.False(vm.FunzionalitaOperazioneObbligatoria);          // il guasto non e mai bloccato dalla Funzionalita
    }

}

/// <summary>6.5.2, test gia salvati: preselezione Veicolo/componente, cascata sulle coppie senza guasto, copertura come avviso/blocco.</summary>
[Collection("Localization")]
public sealed class DtcInjectionCoperturaVeicoloEcuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_dtccop_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    private static TestCase Test(string[] veicoli, string[] componente, bool dtcInjection = true)
    {
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(componente), Veicolo = veicoli[0] };
        foreach (var v in veicoli) tc.Veicoli.Add(v);
        if (dtcInjection) tc.StrumentiUsati.Add("Guasto Injection");
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "x" });
        return tc;
    }

    private TestCaseEditorViewModel Apri(TestCase tc)
    {
        var state = CreaStato();
        state.TestCases.Save(tc, true);
        return new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { }) { ModoEditorRapido = false };
    }

    private static void Aggiungi(TestCaseEditorViewModel vm, string codice)
    {
        vm.DtcInsCodiceManuale = codice;
        vm.AggiungiDtcCommand.Execute(null);
        Assert.Null(vm.DtcInsErrore);
    }

    [Fact]
    public void UnSoloVeicoloEUnaEcu_SonoPreselezionati_ECoperturaRichiedeAlmenoUnDtc()
    {
        var vm = Apri(Test(new[] { "V" }, new[] { "ECM" }));
        Assert.Equal(("V", "ECM"), (vm.DtcInsVeicolo, vm.DtcInsEcu));
        Assert.Equal(new[] { "V / ECM" }, vm.VeicoloEcuSenzaDtc);
        Aggiungi(vm, "P0100");
        Assert.Empty(vm.VeicoloEcuSenzaDtc);
        Assert.Equal(("V", "ECM"), (vm.DtcInsVeicolo, vm.DtcInsEcu));   // tutto coperto: la selezione resta (si possono aggiungere altri guasto)
    }

    [Fact]
    public void PiuVeicoliEEcu_PreselezionaLaPrimaCoppiaSenzaDtc_EAvanzaDopoOgniAggiunta()
    {
        var vm = Apri(Test(new[] { "V", "W" }, new[] { "ECM", "BCM" }));
        Assert.Equal(4, vm.VeicoloEcuSenzaDtc.Count);
        Assert.Equal(("V", "ECM"), (vm.DtcInsVeicolo, vm.DtcInsEcu));
        Aggiungi(vm, "P0001");
        Assert.Equal(("V", "BCM"), (vm.DtcInsVeicolo, vm.DtcInsEcu));
        Assert.Equal(new[] { "V / BCM", "W / ECM", "W / BCM" }, vm.VeicoloEcuSenzaDtc);
        Aggiungi(vm, "P0002");
        Assert.Equal(("W", "ECM"), (vm.DtcInsVeicolo, vm.DtcInsEcu));
        Aggiungi(vm, "P0003");
        Assert.Equal(("W", "BCM"), (vm.DtcInsVeicolo, vm.DtcInsEcu));
        Assert.True(vm.HaCoperturaIncompleta);
        Aggiungi(vm, "P0004");
        Assert.Empty(vm.VeicoloEcuSenzaDtc);
        Assert.False(vm.HaCoperturaIncompleta);
        Assert.Equal(("W", "BCM"), (vm.DtcInsVeicolo, vm.DtcInsEcu));   // tutte coperte: nessun salto
        Assert.Equal(8, vm.RevisioneCorrente!.Condizioni.Count);
    }

    [Fact]
    public void ScelteDellUtente_ValideRestano_ELaCascataVeicoloEcuSiRiallinea()
    {
        var vm = Apri(Test(new[] { "V", "W" }, new[] { "ECM", "BCM" }));
        vm.DtcInsVeicolo = "W";
        vm.DtcInsEcu = "BCM";
        Aggiungi(vm, "P0009");                                           // copre (W,BCM): le altre coppie restano scoperte
        Assert.Equal(("V", "ECM"), (vm.DtcInsVeicolo, vm.DtcInsEcu));    // avanza alla prima ancora scoperta
        vm.DtcInsVeicolo = "W";                                          // cambio veicolo: componente ancora senza guasto per quel veicolo
        Assert.Equal("ECM", vm.DtcInsEcu);
    }

    [Fact]
    public async Task TestGiaSalvato_SenzaDtc_AvvisoNonBloccante_AncheQuandoConcluso()
    {
        var vm = Apri(Test(new[] { "V", "W" }, new[] { "ECM" }));
        Assert.True(vm.HaCoperturaIncompleta);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.ErroriValidazione, e => e.Contains("V / ECM") || e.Contains("W / ECM"));   // solo avviso nel banner di copertura

        vm.TestCase.StatoLavoro = StatoLavoroTest.Concluso;
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.ErroriValidazione, e => e.Contains("V / ECM") || e.Contains("W / ECM"));
        Assert.Equal(new[] { "V / ECM", "W / ECM" }, vm.VeicoloEcuSenzaDtc);
    }

    [Fact]
    public void CoppieVeicoloEcu_SonoIlProdottoCompletoDelTest_ENonAssociateSegnalateSenzaInferireCopertura()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "ECM", VeicoliAssociati = { "V" } }, new() { Nome = "BCM", VeicoliAssociati = { "W" } } });
        state.TestCases.Save(Test(new[] { "V", "W" }, new[] { "ECM", "BCM" }), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { }) { ModoEditorRapido = false };
        var senza = vm.VeicoloEcuSenzaDtc;
        Assert.Equal(4, senza.Count);                                    // tutte le coppie, non solo quelle del catalogo
        Assert.Equal(2, senza.Count(x => x.Contains("V / BCM") || x.Contains("W / ECM")));
        Assert.Equal(2, senza.Count(x => x == "V / ECM" || x == "W / BCM"));
        Aggiungi(vm, "P0001");                                           // copre (V,ECM)
        vm.DtcInsVeicolo = "W"; vm.DtcInsEcu = "BCM";
        Aggiungi(vm, "P0002");                                           // copre (W,BCM)
        Assert.Equal(2, vm.VeicoloEcuSenzaDtc.Count);                    // le escluse restano scoperte: nessuna copertura inferita
        Assert.All(vm.VeicoloEcuSenzaDtc, x => Assert.True(x.StartsWith("V / BCM") || x.StartsWith("W / ECM")));
    }

    [Fact]
    public async Task TestGiaSalvato_SenzaDtc_RimossaUltimaOperazioneVuota_SalvaDavvero()
    {
        var vm = Apri(Test(new[] { "V" }, new[] { "ECM" }));
        var stato = vm.TestCase.RevisioneDati;
        vm.RevisioneCorrente!.Condizioni.Clear();                        // come RimuoviOperazioniVuoteSeSoloDtc dopo aver scelto guasto Injection
        Assert.True(vm.SoloOperazioniDtc);
        vm.ModificheNonSalvate = true;
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);                              // nessun hardfail (ne copertura componente ne guasto)
        Assert.True(vm.HaCoperturaIncompleta);                           // resta un avviso nel banner
        var su = vm.TestCase.RevisioneDati;
        Assert.NotEqual(stato, su);                                      // salvato davvero: revisione dati incrementata
    }

    [Fact]
    public void FunzionalitaRiga_VisibileSoloPerProprietarioDaTestare_ELegacyFuoriDalloSoloDtc()
    {
        var daTestare = new[] { "UnitLogger", "Easy" };
        Assert.True(OperazioniStrumentoRegole.FunzionalitaRigaVisibile("unitlogger", false, daTestare));
        Assert.True(OperazioniStrumentoRegole.FunzionalitaRigaVisibile("Easy", true, daTestare));
        Assert.False(OperazioniStrumentoRegole.FunzionalitaRigaVisibile("ValidationTool", false, daTestare));
        Assert.False(OperazioniStrumentoRegole.FunzionalitaRigaVisibile("Guasto Injection", false, daTestare));
        Assert.True(OperazioniStrumentoRegole.FunzionalitaRigaVisibile(null, false, daTestare));
        Assert.False(OperazioniStrumentoRegole.FunzionalitaRigaVisibile(null, true, daTestare));
    }

    [Fact]
    public void Copertura_RichiedeEntrambiIPassiDelloStessoDtcEDelloStrumento()
    {
        var vm = Apri(Test(new[] { "V" }, new[] { "ECM" }));
        Aggiungi(vm, "P0001");
        var devalidazione = vm.RevisioneCorrente!.Condizioni.Single(c => c.DtcPasso == PassoDtc.Devalidazione);
        devalidazione.Guasto = "P0002";
        Assert.Single(vm.VeicoloEcuSenzaDtc);
        devalidazione.Guasto = "P0001";
        devalidazione.Strumento = "UnitLogger";
        Assert.Single(vm.VeicoloEcuSenzaDtc);
        devalidazione.Strumento = "Guasto Injection";
        Assert.Empty(vm.VeicoloEcuSenzaDtc);
    }

    [Fact]
    public void Copertura_LegacySenzaVeicoloNonCoprePiuVeicoli_ESenzaEcuNonCoprePiuEcu()
    {
        var vm = Apri(Test(new[] { "V", "W" }, new[] { "ECM", "BCM" }));
        Aggiungi(vm, "P0001");
        foreach (var c in vm.RevisioneCorrente!.Condizioni) c.DtcVeicolo = null;
        Assert.Equal(4, vm.VeicoloEcuSenzaDtc.Count);
        foreach (var c in vm.RevisioneCorrente.Condizioni) { c.DtcVeicolo = "V"; c.Componente.Clear(); }
        Assert.Equal(4, vm.VeicoloEcuSenzaDtc.Count);
    }

    [Fact]
    public void Copertura_LegacySenzaVeicoloConUnSoloVeicoloRestaRisolvibile()
    {
        var vm = Apri(Test(new[] { "V" }, new[] { "ECM" }));
        Aggiungi(vm, "P0001");
        foreach (var c in vm.RevisioneCorrente!.Condizioni) c.DtcVeicolo = null;
        Assert.Empty(vm.VeicoloEcuSenzaDtc);
    }

    [Fact]
    public void SenzaDtcInjection_NessunaCoperturaDtcRichiesta()
    {
        var vm = Apri(Test(new[] { "V", "W" }, new[] { "ECM" }, dtcInjection: false));
        Assert.Empty(vm.VeicoloEcuSenzaDtc);
        Assert.False(vm.SoloOperazioniDtc);
        Assert.True(vm.MostraAggiungiOperazioneLibera);
    }
}
