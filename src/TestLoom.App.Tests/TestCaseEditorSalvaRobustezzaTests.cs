using System.Reflection;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Review 6.4.3 sul Salva dell'editor Test: nota di pianificazione obbligatoria ma invisibile (vicolo cieco),
/// salvataggio parziale che confligge con la propria scrittura, esclusione segnalazioni per Test annullato
/// applicata prima della validazione, conferma alla rimozione di operazioni collegate.
/// </summary>
[Collection("Localization")]
public sealed class TestCaseEditorSalvaRobustezzaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SalvaRobustezza_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState(string operatoreAttivo = "Mario Rossi")
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Crea("Mario Rossi", RuoloOperatore.Master));
        ops.Add(TestOperatori.Crea("Luigi Bianchi", RuoloOperatore.Standard));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore(operatoreAttivo, "1234");
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        return state;
    }

    private static async Task<string> CreaTestValido(AppState state, string? assegnatario = "Mario Rossi")
    {
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Daily 24";
        vm.TestCase.Descrizione = "Descrizione completa";
        vm.AssegnatarioSelezionato = assegnatario;
        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
        if (vm.RevisioneCorrente != null) vm.RevisioneCorrente.Matricola = "";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.CampiObbligatoriMancanti);
        var id = vm.TestCase.Id;
        vm.Dispose();
        return id;
    }

    private static T Campo<T>(object o, string nome) =>
        (T)o.GetType().GetField(nome, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    [Fact]
    public async Task Standard_TestSenzaAssegnatario_SalvaAutoAssegnaSenzaRichiedereLaNota()
    {
        var state = CreateState();
        var id = await CreaTestValido(state);
        var tc = state.TestCases.GetById(id)!;
        tc.Assegnatario = null;
        state.TestCases.Save(tc, isNew: false, tc.RevisioneDati);

        state.ImpostaOperatore("Luigi Bianchi", "1234");
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        Assert.False(vm.MostraNotaPianificazione);
        vm.IniziaModificaCommand.AttendiModifica();
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.DoesNotContain(LocalizationService.Instance.Traduci("ValNotaPianificazioneObbl"), vm.ErroriValidazione);
        Assert.Equal("Luigi Bianchi", state.TestCases.GetById(id)!.Assegnatario);
        vm.Dispose();
    }

    [Fact]
    public async Task TestNuovoConSnapshotDaAllegato_MasterCambiaAssegnatario_NotaVisibileERichiesta()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Daily 24";
        vm.TestCase.Descrizione = "Descrizione completa";
        vm.AssegnatarioSelezionato = "Mario Rossi";
        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
        if (vm.RevisioneCorrente != null) vm.RevisioneCorrente.Matricola = "";
        Assert.False(vm.MostraNotaPianificazione); // ancora nessuno snapshot

        typeof(TestCaseEditorViewModel).GetMethod("AssicuraTestSalvato", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!.Invoke(vm, null);
        Assert.True(vm.MostraNotaPianificazione);

        vm.AssegnatarioSelezionato = "Luigi Bianchi";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Contains(LocalizationService.Instance.Traduci("ValNotaPianificazioneObbl"), vm.ErroriValidazione);

        vm.NotaPianificazioneTemp = "Riassegnato";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        Assert.Equal("Luigi Bianchi", state.TestCases.GetById(vm.TestCase.Id)!.Assegnatario);
        vm.Dispose();
    }

    private (AppState State, string Id) CreaTestConNcDaAggiornare()
    {
        var state = CreateState("Luigi Bianchi");
        var condizione = new Condizione { Esito = Esito.NonConforme, Nota = "Risposta errata", Componente = "BCM" };
        var tc = new TestCase
        {
            Id = "TC-PARZ-1", Componente = "BCM", Assegnatario = "Luigi Bianchi", Descrizione = "d",
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, StatoLavoro = StatoLavoroTest.InCorso,
            Esecuzioni = new() { new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.NonConforme, Condizioni = new() { condizione } } }
        };
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(new NonConformita { Id = "NC-0001", TestCaseId = "TC-ALTRO", DataApertura = DateTime.Now.AddDays(-1), StatoWorkflow = StatoAnomalia.Confermata, Descrizione = "Aperta" });
        return (state, tc.Id);
    }

    [Fact]
    public async Task SalvataggioParziale_SnapshotRiallineatoAllaRevisioneScritta()
    {
        var (state, id) = CreaTestConNcDaAggiornare();
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.AnomaliaSelezionata = riga.AnomalieCandidate.Single(n => n.Id == "NC-0001");
        vm.CollegaSegnalazioneAdAnomaliaCommand.Execute(riga);

        // Un'altra postazione aggiorna la NC dopo il collegamento: il salvataggio della NC confligge DOPO il Test.
        var nc = state.NonConformita.GetById("NC-0001")!;
        nc.Descrizione = "Aperta (modificata altrove)";
        state.NonConformita.Save(nc, nc.RevisioneDati);

        var revPrima = state.TestCases.GetById(id)!.RevisioneDati;
        await vm.SalvaCommand.ExecuteAsync(null);
        var revDopo = state.TestCases.GetById(id)!.RevisioneDati;
        Assert.True(revDopo > revPrima, "Il Test deve essere stato scritto prima del fallimento sulla NC");
        Assert.Equal(revDopo, Campo<TestCase>(vm, "_snapshot").RevisioneDati);

        // Retry: il Test non deve confliggere con la propria scrittura precedente.
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.ErroriValidazione, e => e.Contains(id));
        vm.Dispose();
    }

    [Fact]
    public async Task SalvataggioParziale_RetryNonDuplicaLaNuovaAnomalia()
    {
        var (state, id) = CreaTestConNcDaAggiornare();
        var t = state.TestCases.GetById(id)!;
        t.Esecuzioni[0].Condizioni.Add(new Condizione { Esito = Esito.NonConforme, Nota = "Seconda", Componente = "BCM" });
        state.TestCases.Save(t, isNew: false, t.RevisioneDati);

        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        await vm.SalvaCommand.ExecuteAsync(null); // materializza le segnalazioni mancanti
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.Equal(2, vm.SegnalazioniTest.Count);
        var riga1 = vm.SegnalazioniTest[0];
        var riga2 = vm.SegnalazioniTest[1];
        riga1.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga1);
        riga2.AnomaliaSelezionata = riga2.AnomalieCandidate.Single(n => n.Id == "NC-0001");
        vm.CollegaSegnalazioneAdAnomaliaCommand.Execute(riga2);

        // NC-0001 aggiornata altrove: il suo salvataggio fallisce DOPO Test e nuova NC.
        var nc = state.NonConformita.GetById("NC-0001")!;
        nc.Descrizione = "modificata altrove";
        state.NonConformita.Save(nc, nc.RevisioneDati);

        await vm.SalvaCommand.ExecuteAsync(null);
        var nDopoPrimo = state.NonConformita.GetAll().Count(n => n.TestCaseId == id);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Equal(1, nDopoPrimo);
        Assert.Equal(1, state.NonConformita.GetAll().Count(n => n.TestCaseId == id));
        vm.Dispose();
    }

    /// <summary>Review 2026-09-30: una NC nuova gia' scritta da un salvataggio parziale e poi modificata prima del
    /// retry non deve essere saltata (modifica persa); a salvataggio completato il tracciamento va azzerato.</summary>
    [Fact]
    public async Task SalvataggioParziale_NcNuovaScrittaPoiModificata_RetryLaRiscriveConRevisioneCorrente()
    {
        var (state, id) = CreaTestConNcDaAggiornare();
        var t = state.TestCases.GetById(id)!;
        t.Esecuzioni[0].Condizioni.Add(new Condizione { Esito = Esito.NonConforme, Nota = "Seconda", Componente = "BCM" });
        state.TestCases.Save(t, isNew: false, t.RevisioneDati);

        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        await vm.SalvaCommand.ExecuteAsync(null); // materializza le segnalazioni mancanti
        vm.IniziaModificaCommand.AttendiModifica();
        var riga1 = vm.SegnalazioniTest[0];
        var riga2 = vm.SegnalazioniTest[1];
        riga1.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga1);
        riga2.AnomaliaSelezionata = riga2.AnomalieCandidate.Single(n => n.Id == "NC-0001");
        vm.CollegaSegnalazioneAdAnomaliaCommand.Execute(riga2);
        var nc0 = state.NonConformita.GetById("NC-0001")!;
        nc0.Descrizione = "modificata altrove";
        state.NonConformita.Save(nc0, nc0.RevisioneDati);

        await vm.SalvaCommand.ExecuteAsync(null); // parziale: la nuova NC e' scritta, NC-0001 confligge
        var uidNuova = state.NonConformita.GetAll().Single(n => n.TestCaseId == id && n.Uid != nc0.Uid).Uid;
        Assert.Contains(uidNuova, Campo<HashSet<Guid>>(vm, "_salvatiInQuestoSalva"));
        Assert.Empty(Campo<List<NonConformita>>(vm, "_anomalieNuove")); // ormai e' una NC esistente
        Assert.NotNull(state.NonConformita.GetByUid(uidNuova));
        Assert.Equal(1, state.NonConformita.GetByUid(uidNuova)!.RevisioneDati);

        // L'utente modifica la NC gia' scritta (sulla riga del pannello, come la UI) e sistema il conflitto.
        var ncPannello = vm.AnomalieTestAperte.Concat(vm.AnomalieTestChiuse).Select(r => r.Nc).Single(n => n.Uid == uidNuova);
        ncPannello.MotivoChiusura = "modifica dopo salvataggio parziale";
        typeof(TestCaseEditorViewModel).GetMethod("TracciaAnomaliaDaAggiornare", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, new object[] { ncPannello });
        var ncAttuale = state.NonConformita.GetById("NC-0001")!;
        Campo<Dictionary<Guid, long>>(vm, "_anomalieDaAggiornare")[ncAttuale.Uid] = ncAttuale.RevisioneDati;
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        var salvata = state.NonConformita.GetByUid(uidNuova)!;
        Assert.Equal("modifica dopo salvataggio parziale", salvata.MotivoChiusura);
        Assert.Equal(2, salvata.RevisioneDati);
        Assert.Equal(1, state.NonConformita.GetAll().Count(n => n.Uid == uidNuova));
        Assert.Empty(Campo<HashSet<Guid>>(vm, "_salvatiInQuestoSalva"));
        vm.Dispose();
    }

    [Fact]
    public async Task TestAnnullato_ValidazioneFallita_NonEscludeLeSegnalazioni()
    {
        var (state, id) = CreaTestConNcDaAggiornare();
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        vm.StatoLavoroSelezionato = StatoLavoroTest.Annullato;
        vm.TestCase.MotivoAnnullamento = "Non piu necessario";
        // Validazione fallita per un motivo indipendente: descrizione problema vuota su Non Conforme.
        vm.RevisioneCorrente!.Condizioni[0].Nota = "";
        vm.RevisioneCorrente.Condizioni[0].DescrizioneProblema = "";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.ErroriValidazione.Concat(vm.CampiObbligatoriMancanti.Select(c => c.Messaggio)));
        Assert.All(vm.SegnalazioniTest, r => Assert.NotEqual(StatoSegnalazione.Esclusa, r.Segnalazione.Stato));
        vm.Dispose();
    }

    [Fact]
    public async Task RimuoviOperazioneCollegata_ChiedeConferma()
    {
        var (state, id) = CreaTestConNcDaAggiornare();
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        vm.AggiungiCondizioneCommand.Execute(null);
        var daRimuovere = vm.RevisioneCorrente!.Condizioni[0];
        var chieste = 0;
        ConfirmService.Handler = (_, _, _, _) => { chieste++; return Task.FromResult(false); };
        await vm.RimuoviCondizioneCommand.ExecuteAsync(daRimuovere);
        Assert.Equal(1, chieste);
        Assert.Contains(daRimuovere, vm.RevisioneCorrente.Condizioni);
        vm.Dispose();
    }
}
