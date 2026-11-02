using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Regressione per il passaggio di CaricaPannelloAnomalie/"candidate" da GetAll() (clona l'intero
/// archivio NC) a PeekAllReadOnly() (nessuna clonazione, istanze effettive della cache del repository).
/// Copre esattamente i tre rischi del cambiamento: (a) stesso elenco di candidati con NC di vari stati,
/// incluse DaClassificare/Esclusa; (b) nessuna mutazione della cache prima del Save, perché AnomaliaSelezionata
/// ora può essere l'istanza live del repository e CollegaSegnalazioneAdAnomalia la clona esplicitamente prima
/// di mutarla; (c) una scrittura di un'altra postazione resta visibile alla successiva apertura dell'editor.</summary>
[Collection("Localization")]
public sealed class TestCaseEditorAnomaliaCandidatePeekTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AnomaliaCandidatePeek_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private (AppState State, TestCase Test) CreaTestConSegnalazioniVarie()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        var condizione = new Condizione { Esito = Esito.NonConforme, Nota = "Risposta errata", Componente = "BCM" };
        var tc = new TestCase
        {
            Id = "TC-CAND-1",
            Componente = "BCM",
            Assegnatario = "Tester",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
            StatoLavoro = StatoLavoroTest.InCorso,
            Esecuzioni = new()
            {
                new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.NonConforme, Condizioni = new() { condizione } }
            }
        };
        state.TestCases.Save(tc, isNew: true);

        // Anomalie aperte NON collegate a questo Test: sono le candidate che il picker deve proporre.
        state.NonConformita.Save(new NonConformita { Id = "NC-0001", TestCaseId = "TC-ALTRO-1", DataApertura = DateTime.Now.AddDays(-2), StatoWorkflow = StatoAnomalia.Confermata, Descrizione = "Anomalia aperta 1" });
        state.NonConformita.Save(new NonConformita { Id = "NC-0002", TestCaseId = "TC-ALTRO-2", DataApertura = DateTime.Now.AddDays(-1), StatoWorkflow = StatoAnomalia.InVerifica, Descrizione = "Anomalia aperta 2" });
        // Anomalia chiusa: non deve comparire tra le candidate (solo le Aperta contano).
        state.NonConformita.Save(new NonConformita { Id = "NC-0003", TestCaseId = "TC-ALTRO-3", DataApertura = DateTime.Now.AddDays(-3), StatoWorkflow = StatoAnomalia.Conclusa, Descrizione = "Anomalia chiusa" });

        return (state, tc);
    }

    /// <summary>(a) Stesso elenco di candidati con una segnalazione DaClassificare, e con una seconda riga
    /// portata a Esclusa (che deve restare visibile e proporre comunque i candidati per un'eventuale
    /// riclassificazione, come da commento su EscludiSegnalazioneDaTest/candidate).</summary>
    [Fact]
    public void CandidatiAnomalieAperte_UgualiPerSegnalazioniDaClassificareEEscluse()
    {
        var (state, _) = CreaTestConSegnalazioniVarie();
        var vm = new TestCaseEditorViewModel(state, "TC-CAND-1", () => { }, _ => { });

        var riga = Assert.Single(vm.SegnalazioniTest);
        var candidatiAttesi = new[] { "NC-0001", "NC-0002" };
        Assert.Equal(candidatiAttesi.OrderBy(x => x), riga.AnomalieCandidate.Select(n => n.Id).OrderBy(x => x));

        riga.MotivazioneEsclusione = "Falso positivo, da rivedere";
        vm.EscludiSegnalazioneDaTestCommand.Execute(riga);

        var rigaEsclusa = Assert.Single(vm.SegnalazioniTest);
        Assert.Equal(StatoSegnalazione.Esclusa, rigaEsclusa.Segnalazione.Stato);
        Assert.Equal(candidatiAttesi.OrderBy(x => x), rigaEsclusa.AnomalieCandidate.Select(n => n.Id).OrderBy(x => x));
    }

    /// <summary>Review 2026-09-25: con uno scope di working-copy attivo (ripristino del form dopo un rollback in
    /// SalvaSicuro) il Peek restituirebbe le working-copy interne dello scope, che la UI tratterrebbe fra i
    /// candidati. In quel caso l'editor deve tornare al percorso che clona.</summary>
    [Fact]
    public void CandidatiConScopeWorkingCopyAttivo_NonEspongonoLeWorkingCopyDelloScope()
    {
        var (state, _) = CreaTestConSegnalazioniVarie();
        var vm = new TestCaseEditorViewModel(state, "TC-CAND-1", () => { }, _ => { });
        var carica = typeof(TestCaseEditorViewModel).GetMethod("CaricaPannelloAnomalie",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, new[] { typeof(bool) })!;

        using (WorkflowWorkingCopyScope.Begin(nonConformita: state.NonConformita.GetAll()))
        {
            Assert.True(WorkflowWorkingCopyScope.Attivo);
            carica.Invoke(vm, new object[] { false });
            var workingCopies = state.NonConformita.PeekAllReadOnly();
            var riga = Assert.Single(vm.SegnalazioniTest);
            Assert.NotEmpty(riga.AnomalieCandidate);
            foreach (var candidato in riga.AnomalieCandidate)
                Assert.DoesNotContain(workingCopies, w => ReferenceEquals(w, candidato));
        }
        Assert.False(WorkflowWorkingCopyScope.Attivo);
    }

    /// <summary>(b) Selezionare e collegare un candidato non deve mutare l'istanza nella cache del repository:
    /// una lettura indipendente fatta PRIMA del Salva del Test deve ancora restituire la NC senza il nuovo
    /// collegamento. Con PeekAllReadOnly() (nessuna clonazione) questo dipende interamente dal fatto che
    /// CollegaSegnalazioneAdAnomalia clona esplicitamente prima di mutare (vedi TestCaseEditorViewModel.cs).</summary>
    [Fact]
    public async Task CollegaSegnalazioneAdAnomalia_NonMutaLaCacheDelRepositoryPrimaDelSalvataggio()
    {
        var (state, tc) = CreaTestConSegnalazioniVarie();
        var master = TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Peek",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234")
        });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));

        using var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);
        var riga = Assert.Single(vm.SegnalazioniTest);
        var candidato = riga.AnomalieCandidate.Single(n => n.Id == "NC-0001");
        riga.AnomaliaSelezionata = candidato;

        vm.CollegaSegnalazioneAdAnomaliaCommand.Execute(riga);

        // Lettura indipendente della cache, prima di qualunque Salva: deve essere ancora priva del
        // collegamento appena fatto in memoria dal comando sulla propria copia.
        var dallaCachePrimaDelSalvataggio = state.NonConformita.GetById("NC-0001")!;
        Assert.DoesNotContain(dallaCachePrimaDelSalvataggio.ProveCollegate, p => p.TestId == tc.Id);
        var dallaCachePeek = state.NonConformita.PeekAllReadOnly().Single(n => n.Id == "NC-0001");
        Assert.DoesNotContain(dallaCachePeek.ProveCollegate, p => p.TestId == tc.Id);

        // La segnalazione risulta comunque classificata lato editor (comportamento verso l'utente invariato).
        Assert.Equal(StatoSegnalazione.Classificata, riga.Segnalazione.Stato);

        // NOTA: il comportamento a questo punto (se il collegamento sopravviva o meno al Salva del Test per
        // una NC che NON faceva già parte del pannello di QUESTO test) è identico, byte per byte, prima e dopo
        // questo cambiamento — verificato eseguendo questo stesso test sul codice pre-esistente (GetAll(),
        // candidate sempre clonato). Non è quindi una regressione introdotta qui: resta fuori dallo scope di
        // questo task, che riguarda solo sola-lettura vs mutazione, non la logica di collegamento esistente.
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
    }

    /// <summary>(c) Una scrittura fatta da una SECONDA postazione sulla stessa cartella condivisa (stesso
    /// schema "seconda postazione" usato altrove nel repository, es. WuStatusRicaricaCacheTests) deve comparire
    /// tra i candidati alla successiva apertura dell'editor sulla prima postazione, dopo un refresh.</summary>
    [Fact]
    public void ScritturaDiSecondaPostazione_CompareTraICandidatiAllaSuccessivaAperturaDellEditor()
    {
        var (state, tc) = CreaTestConSegnalazioniVarie();

        var dirLocale2 = Path.Combine(Path.GetTempPath(), "TestLoom_AnomaliaCandidatePeek_Postazione2_" + Guid.NewGuid().ToString("N"));
        var configService2 = new AppConfigService(Path.Combine(dirLocale2, "config.json"));
        var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        try
        {
            stato2.InizializzaCartella(_dir);
            stato2.NonConformita.Save(new NonConformita
            {
                Id = "NC-0009", TestCaseId = "TC-ALTRO-9", DataApertura = DateTime.Now,
                StatoWorkflow = StatoAnomalia.Confermata, Descrizione = "Aperta dalla seconda postazione"
            });
        }
        finally { stato2.Dispose(); }

        state.RicaricaTutto();
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);

        Assert.Contains(riga.AnomalieCandidate, n => n.Id == "NC-0009");
    }
}
