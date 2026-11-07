using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Gestione delle anomalie collegate dalla pagina Test tramite il dialogo UNICO di dettaglio
/// (<see cref="TestCaseEditorViewModel.CreaDettaglioAnomalia"/> / <see cref="TestCaseEditorViewModel.DopoDettaglioAnomalia"/>):
/// stesse regole di chiusura (motivazione obbligatoria), stesso optimistic lock della pagina Anomalie, workflow
/// di conclusione del Test invariato. Caso d'uso: segnalazione dalla rete su cui sono stati fatti test, nessun
/// altro test necessario: si chiude l'anomalia dal test e poi si conclude il test.</summary>
[Collection("Localization")]
public sealed class AnomalieDallaPaginaTestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AnomDaTest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private (AppState State, string TestId) CreaTestConAnomalia(int numero = 1)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));

        var condizioni = new List<Condizione>();
        var segnalazioni = new List<SegnalazioneDaClassificare>();
        for (var i = 1; i <= numero; i++)
        {
            var condizione = new Condizione { Esito = Esito.NonConforme, Nota = "Risposta errata", Componente = "BCM", DescrizioneProblema = "KO" };
            var seg = new SegnalazioneDaClassificare
            {
                TestId = "TC-ANOM-1", CondizioneUid = condizione.Uid, EsecuzioneIterazione = 1,
                Stato = StatoSegnalazione.Classificata, AnomaliaUid = Guid.NewGuid()
            };
            condizione.SegnalazioneUid = seg.Uid;
            condizioni.Add(condizione);
            segnalazioni.Add(seg);
        }
        state.TestCases.Save(new TestCase
        {
            Id = "TC-ANOM-1", Componente = "BCM", Assegnatario = "Tester", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
            StatoLavoro = StatoLavoroTest.InCorso,
            Esecuzioni = new() { new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.NonConforme, Condizioni = condizioni } }
        }, isNew: true);
        for (var i = 0; i < numero; i++)
        {
            state.Segnalazioni.Save(segnalazioni[i]);
            state.NonConformita.Save(new NonConformita
            {
                Uid = segnalazioni[i].AnomaliaUid!.Value, Id = $"NC-{i + 1:0000}", TestCaseId = "TC-ANOM-1", DataApertura = DateTime.Now.AddMinutes(i),
                Severita = SeveritaAnomalia.High, Owner = "Mario", StatoWorkflow = StatoAnomalia.Confermata,
                ProveCollegate = new() { new RiferimentoProva { TestId = "TC-ANOM-1", EsecuzioneIterazione = 1, CondizioneUid = condizioni[i].Uid } }
            });
        }
        return (state, "TC-ANOM-1");
    }

    [Fact]
    public async Task ChiusuraInline_NcModificataAltrove_NonFingeChiusuraNeConcludeIlTest()
    {
        var (state, testId) = CreaTestConAnomalia();
        var editor = new TestCaseEditorViewModel(state, testId, () => { }, _ => { });
        var riga = Assert.Single(editor.AnomalieTestAperte);
        riga.Nc.MotivoChiusura = "Controllo completato sulla REV corrente";

        // Conflitto successivo all'apertura dell'editor: il suo optimistic lock e' vecchio.
        var concorrente = state.NonConformita.GetById("NC-0001")!;
        concorrente.Owner = "Altro operatore";
        state.NonConformita.Save(concorrente, concorrente.RevisioneDati);

        await editor.ChiudiAnomaliaDalTestCommand.ExecuteAsync(riga);

        Assert.NotEmpty(editor.ErroriValidazione);
        Assert.Equal(StatoAnomalia.Confermata, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
        Assert.Contains(editor.AnomalieTestAperte, x => x.Nc.Id == "NC-0001"
            && x.Nc.StatoWorkflow != StatoAnomalia.Conclusa);
        Assert.NotEqual(StatoLavoroTest.Concluso, editor.StatoLavoroSelezionato);
    }

    [Fact]
    public async Task ChiusuraDalDettaglio_SenzaMotivazione_Rifiutata_ConMotivazione_ChiudeEIlTestSiConclude()
    {
        var (state, testId) = CreaTestConAnomalia();
        string? navigatoA = null;
        var editor = new TestCaseEditorViewModel(state, testId, () => { }, id => navigatoA = id);
        var riga = Assert.Single(editor.AnomalieTestAperte);

        var dettaglio = editor.CreaDettaglioAnomalia(riga);
        Assert.True(dettaglio.Persistita);
        await dettaglio.ChiudiCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance.Traduci("ChiudereAnomaliaMotivo"), dettaglio.Errore);
        Assert.False(dettaglio.Modificato);
        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);

        dettaglio.MotivoChiusura = "Segnalazione dalla rete verificata, nessun altro test necessario";
        await dettaglio.ChiudiCommand.ExecuteAsync(null);
        Assert.Null(dettaglio.Errore);
        Assert.True(dettaglio.Modificato);
        var chiusa = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal(StatoAnomalia.Conclusa, chiusa.StatoWorkflow);
        Assert.Contains("nessun altro test", chiusa.MotivoChiusura);

        // Dopo il dialogo il test viene ricaricato (navigazione allo stesso Id) e puo' concludersi.
        editor.DopoDettaglioAnomalia(dettaglio);
        Assert.Equal(testId, navigatoA);
        var ricaricato = new TestCaseEditorViewModel(state, testId, () => { }, _ => { });
        Assert.Empty(ricaricato.AnomalieTestAperte);
        Assert.Single(ricaricato.AnomalieTestChiuse);
        ricaricato.StatoLavoroSelezionato = StatoLavoroTest.Concluso;
        await ricaricato.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(ricaricato.ErroriValidazione);
        Assert.Equal(StatoLavoroTest.Concluso, state.TestCases.GetById(testId)!.StatoLavoro);
    }

    [Fact]
    public async Task ConclusioneTest_RestaBloccataFinche_L_AnomaliaNonEChiusa()
    {
        var (state, testId) = CreaTestConAnomalia();
        var editor = new TestCaseEditorViewModel(state, testId, () => { }, _ => { });
        editor.StatoLavoroSelezionato = StatoLavoroTest.Concluso;

        await editor.SalvaCommand.ExecuteAsync(null);

        Assert.Contains(editor.ErroriValidazione, e => e.Contains("anomalie", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(StatoLavoroTest.Concluso, state.TestCases.GetById(testId)!.StatoLavoro);
    }

    [Fact]
    public void ModificaDalDettaglio_ConConflittoConAltroOperatore_RicaricaERestaAperto_StessoMeccanismoDellaPaginaAnomalie()
    {
        var (state, testId) = CreaTestConAnomalia();
        var editor = new TestCaseEditorViewModel(state, testId, () => { }, _ => { });
        var mio = editor.CreaDettaglioAnomalia(Assert.Single(editor.AnomalieTestAperte));
        using var writer = new AppState(
            new AppConfigService(Path.Combine(state.ConfigService.CartellaLocale, "config.json")),
            runBackgroundMaintenance: false);
        var altro = new AnomaliaDettaglioViewModel(writer, writer.NonConformita.GetById("NC-0001")!);
        var chiusureMio = 0;
        mio.ChiusuraRichiesta += () => chiusureMio++;

        altro.Nc.Owner = "Da altro operatore";
        altro.SalvaCommand.Execute(null);
        Assert.True(altro.Modificato);

        mio.Nc.Owner = "Dal test";
        mio.SalvaCommand.Execute(null);

        // Post 6.5.3: l'input resta nel dialogo ed è conservato in bozza; la ricarica è una scelta esplicita.
        Assert.StartsWith(LocalizationService.Instance.Traduci("AnomaliaConflittoInputPreservato"), mio.Errore);
        Assert.Contains("Bozza locale conservata", mio.Errore);
        Assert.True(mio.ConflittoInAttesa);
        Assert.Equal("Dal test", mio.Nc.Owner);
        var bozza = Assert.Single(FailedSaveDraftInspector.ListPage(state.Paths.Root, 0, 10),
            r => r.EntityUid == state.NonConformita.GetById("NC-0001")!.Uid);
        Assert.Contains("Dal test", File.ReadAllText(bozza.FilePath));
        Assert.Equal(0, chiusureMio);
        Assert.False(mio.Modificato);
        Assert.Equal("Da altro operatore", writer.NonConformita.GetById("NC-0001")!.Owner);

        mio.RicaricaDopoConflittoCommand.Execute(null);

        Assert.StartsWith(LocalizationService.Instance.Traduci("AnomaliaConflittoRicaricata"), mio.Errore);
        Assert.False(mio.ConflittoInAttesa);
        Assert.Equal("Da altro operatore", mio.Nc.Owner);
        Assert.Equal(0, chiusureMio);
        Assert.True(File.Exists(bozza.FilePath));
    }

    [Fact]
    public void ModificaDalDettaglio_Salva_PersisteEIlTestRicaricatoLaMostra()
    {
        var (state, testId) = CreaTestConAnomalia();
        string? navigatoA = null;
        var editor = new TestCaseEditorViewModel(state, testId, () => { }, id => navigatoA = id);
        var dettaglio = editor.CreaDettaglioAnomalia(Assert.Single(editor.AnomalieTestAperte));

        dettaglio.Nc.Owner = "Giulia";
        dettaglio.Nc.Priorita = PrioritaOperativa.Major;
        dettaglio.SalvaCommand.Execute(null);
        editor.DopoDettaglioAnomalia(dettaglio);

        Assert.Equal(testId, navigatoA);
        var ricaricato = new TestCaseEditorViewModel(state, testId, () => { }, _ => { });
        var riga = Assert.Single(ricaricato.AnomalieTestAperte);
        Assert.Equal("Giulia", riga.Nc.Owner);
        Assert.Equal(PrioritaOperativa.Major, riga.Nc.Priorita);
    }

    [Fact]
    public async Task DopoPopup_ChiusuraDellUltimaAnomalia_InModifica_ProponeAvanzamento_NonSeResteUnaAperta()
    {
        var originale = ConfirmService.Handler;
        var richieste = new List<string>();
        ConfirmService.Handler = (titolo, _, _, _) => { richieste.Add(titolo); return Task.FromResult(true); };
        try
        {
            var (state, testId) = CreaTestConAnomalia(2);
            var editor = new TestCaseEditorViewModel(state, testId, () => { }, _ => { });
            editor.InModifica = true;
            Assert.Equal(2, editor.AnomalieTestAperte.Count);
            Assert.False(editor.ModificheNonSalvate);

            // Prima chiusura: ne resta una aperta, nessuna proposta di avanzamento.
            var primo = editor.CreaDettaglioAnomalia(editor.AnomalieTestAperte.First(r => r.Nc.Id == "NC-0002"));
            primo.MotivoChiusura = "Prima verificata";
            await primo.ChiudiCommand.ExecuteAsync(null);
            richieste.Clear();
            await editor.DopoDettaglioAnomaliaAsync(primo);
            Assert.Empty(richieste);
            Assert.False(editor.ModificheNonSalvate, "il pop-up ha gia' salvato: nessuna modifica pendente da perdere alla navigazione");
            Assert.Single(editor.AnomalieTestAperte);
            Assert.NotEqual(StatoLavoroTest.Concluso, editor.StatoLavoroSelezionato);

            // Ultima chiusura: stessa conferma di avanzamento della chiusura inline, lo stato passa a Concluso (da salvare).
            var ultimo = editor.CreaDettaglioAnomalia(editor.AnomalieTestAperte[0]);
            ultimo.MotivoChiusura = "Ultima verificata";
            await ultimo.ChiudiCommand.ExecuteAsync(null);
            richieste.Clear();
            await editor.DopoDettaglioAnomaliaAsync(ultimo);
            Assert.Single(richieste);
            Assert.Equal(StatoLavoroTest.Concluso, editor.StatoLavoroSelezionato);
            Assert.Empty(editor.AnomalieTestAperte);
        }
        finally { ConfirmService.Handler = originale; }
    }
}
