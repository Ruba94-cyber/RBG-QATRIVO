using System.Threading;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Editor di Change Request (ciclo prestazioni 6.6.1, wt-p661-opt-cr): cataloghi e copertura si calcolano in un giro, in linea
/// nei test e in background nella UI. Il risultato in background e' quello in linea; l'editor e' utilizzabile mentre il giro
/// e' in attesa; il salvataggio completa in linea un giro in attesa e non scrive mai una copertura superata.
/// </summary>
public sealed class ChangeRequestEditorGiroTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CrGiro_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    /// <summary>Workspace con un template CR, due veicoli, un operatore Master attivo e un Test conforme sul veicolo "Daily 24"
    /// (TEST-001), e due CR: CR-2026-0001 coperta (diventa Validata), CR-2026-0002 con un veicolo non coperto (resta DaValidare).</summary>
    private (AppState State, TestTemplate TemplateCr) CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        var templateCr = new TestTemplate { Nome = "Change Request", IsChangeRequest = true };
        var tutti = state.Master.TestTemplate.Load().ToList();
        tutti.Add(templateCr);
        state.Master.TestTemplate.Save(tutti);
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = "Daily 24", GammaNome = "MY24" },
            new() { Nome = "Alpha 24", GammaNome = "Alpha" },
        });
        state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Crea("Mario Rossi", RuoloOperatore.Master) });

        state.TestCases.Save(new TestCase
        {
            Id = "TEST-001", Componente = "BCM", DescrizioneBreve = "Verifica copertura", DataCreazione = new DateTime(2026, 1, 1),
            Esecuzioni = new() { new Esecuzione { Iterazione = 1, Data = new DateTime(2026, 2, 1), Esito = Esito.Conforme } },
        }, isNew: true);

        state.ChangeRequests.Save(CrDiProva("CR-2026-0001", templateCr, new[] { "Daily 24" }, new[] { "Daily 24" }));
        state.ChangeRequests.Save(CrDiProva("CR-2026-0002", templateCr, new[] { "Alpha 24" }, new[] { "Daily 24" }));

        Assert.True(state.ImpostaOperatore("Mario Rossi", "1234"));
        state.RicaricaTutto(); // aggiorna le cache dopo il seed e riallinea l'operatore autenticato.
        return (state, templateCr);
    }

    private static ChangeRequest CrDiProva(string id, TestTemplate template, string[] veicoliImpattati, string[] veicoliCoperti) => new()
    {
        Id = id,
        Titolo = $"CR di prova {id}",
        Descrizione = "Descrizione della CR di prova",
        Responsabile = "Mario Rossi",
        CreatoDa = "Mario Rossi",
        TemplateUid = template.Uid,
        Stato = StatoChangeRequest.DaValidare,
        VeicoliImpattati = veicoliImpattati.ToList(),
        TestValidazione = new List<CollegamentoValidazioneCr>
        {
            new() { TestId = "TEST-001", Obbligatorio = true, VeicoliCoperti = veicoliCoperti.ToList() },
        },
    };

    private static ChangeRequestEditorViewModel Apri(AppState state, string id, bool? forzaBackground, Action? gate = null) =>
        new(state, id, () => { }, _ => { }, forzaBackground, gate);

    [Theory]
    [InlineData("CR-2026-0001")]
    [InlineData("CR-2026-0002")]
    public async Task Editor_RisultatoInBackgroundUgualeAQuelloInLinea(string id)
    {
        var (state, _) = CreaStato();
        var inLinea = Apri(state, id, forzaBackground: false);
        var inBackground = Apri(state, id, forzaBackground: true);
        await inBackground.AttendiGiroAsync();
        await inBackground.AttendiCaricamentoTestDisponibiliAsync();

        Assert.False(inBackground.CaricamentoInCorso);
        Assert.Equal(inLinea.Messaggio, inBackground.Messaggio);
        Assert.Equal(inLinea.ChangeRequest.Stato, inBackground.ChangeRequest.Stato);
        Assert.Equal(inLinea.ModificheNonSalvate, inBackground.ModificheNonSalvate);
        Assert.Equal(inLinea.TemplateCr.Select(t => t.Uid), inBackground.TemplateCr.Select(t => t.Uid));
        Assert.Equal(inLinea.TemplateSelezionato?.Uid, inBackground.TemplateSelezionato?.Uid);
        Assert.Equal(inLinea.VeicoliDisponibili, inBackground.VeicoliDisponibili);
        Assert.Equal(inLinea.EcuDisponibili, inBackground.EcuDisponibili);
        Assert.Equal(inLinea.OperatoriDisponibili, inBackground.OperatoriDisponibili);

        var linkLinea = inLinea.ChangeRequest.TestValidazione.Single();
        var linkBg = inBackground.ChangeRequest.TestValidazione.Single();
        Assert.Equal(linkLinea.EsecuzioneValidante, linkBg.EsecuzioneValidante);
        Assert.Equal(linkLinea.SnapshotValidazione?.Esito, linkBg.SnapshotValidazione?.Esito);
        Assert.Equal(linkLinea.SnapshotValidazione?.EseguitaIl, linkBg.SnapshotValidazione?.EseguitaIl);
    }

    [Fact]
    public async Task ImplementaInBackground_MantieneIlFeedbackDopoIlRicalcolo()
    {
        var (state, _) = CreaStato();
        var vm = Apri(state, "CR-2026-0001", forzaBackground: true);
        await vm.AttendiGiroAsync();
        await vm.AttendiCaricamentoTestDisponibiliAsync();

        vm.ImplementaCommand.Execute(null);
        await vm.AttendiGiroAsync();

        Assert.Equal(LocalizationService.Instance.Traduci("CrEdNotImplementata"), vm.Messaggio);
    }

    [Fact]
    public async Task Editor_CataloghiENonPronti_MaUtilizzabileEPoiApplicati()
    {
        var (state, templateCr) = CreaStato();
        using var rilascio = new ManualResetEventSlim(false);
        var vm = Apri(state, "CR-2026-0001", forzaBackground: true, gate: () => rilascio.Wait(TimeSpan.FromSeconds(30)));

        // Il costruttore torna subito: cataloghi e copertura sono in attesa, l'indicatore e' acceso.
        Assert.True(vm.CaricamentoInCorso);
        Assert.Empty(vm.TemplateCr);
        Assert.Null(vm.TemplateSelezionato);

        // L'editor si modifica mentre il giro e' in attesa.
        vm.Titolo = "Titolo modificato durante il caricamento";
        Assert.True(vm.ModificheNonSalvate);

        rilascio.Set();
        await vm.AttendiGiroAsync();
        await vm.AttendiCaricamentoTestDisponibiliAsync();

        Assert.False(vm.CaricamentoInCorso);
        Assert.Contains(vm.TemplateCr, t => t.Uid == templateCr.Uid);
        Assert.Equal(templateCr.Uid, vm.TemplateSelezionato?.Uid);
        Assert.Equal(StatoChangeRequest.Validata, vm.ChangeRequest.Stato);
        Assert.Equal("Titolo modificato durante il caricamento", vm.ChangeRequest.Titolo);
        Assert.True(vm.ModificheNonSalvate); // la modifica dell'utente resta, il solo caricamento non la conta
        Assert.Equal(0, vm.GiriScartati);
    }

    [Fact]
    public async Task Salva_CompletaIlGiroInAttesaPrimaDiScrivere_EIlGiroSospesoEScartato()
    {
        var (state, _) = CreaStato();
        using var rilascio = new ManualResetEventSlim(false);
        var vm = Apri(state, "CR-2026-0001", forzaBackground: true, gate: () => rilascio.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(vm.CaricamentoInCorso);

        // Salvataggio mentre il giro e' ancora in attesa: la copertura e i cataloghi si completano in linea prima di scrivere.
        vm.SalvaCommand.Execute(null);
        Assert.Empty(vm.Errore);
        var salvata = state.ChangeRequests.GetById("CR-2026-0001")!;
        Assert.Equal(StatoChangeRequest.Validata, salvata.Stato);
        Assert.NotNull(salvata.TestValidazione.Single().SnapshotValidazione);
        var revisioneSalvata = salvata.RevisioneDati;

        // Il giro in background, rilasciato dopo, e' superato: non riscrive nulla e non cambia la CR salvata.
        rilascio.Set();
        await vm.AttendiGiroAsync();
        Assert.True(vm.GiriScartati >= 1);
        Assert.Equal(revisioneSalvata, state.ChangeRequests.GetById("CR-2026-0001")!.RevisioneDati);
        Assert.Equal(StatoChangeRequest.Validata, state.ChangeRequests.GetById("CR-2026-0001")!.Stato);
    }

    [Fact]
    public async Task Implementa_CompletaIlGiroInAttesaPrimaDiValidare()
    {
        var (state, _) = CreaStato();
        using var rilascio = new ManualResetEventSlim(false);
        var vm = Apri(state, "CR-2026-0001", forzaBackground: true, gate: () => rilascio.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(vm.CaricamentoInCorso);
        Assert.Null(vm.TemplateSelezionato);

        // Simula il clic immediato su Cambia stato > Implementa prima del completamento dei cataloghi in background.
        vm.ImplementaCommand.Execute(null);

        Assert.Empty(vm.Errore);
        var salvata = state.ChangeRequests.GetById("CR-2026-0001")!;
        Assert.NotNull(salvata.ImplementataIl);
        Assert.Equal("Mario Rossi", salvata.ImplementataDa);
        var revisioneSalvata = salvata.RevisioneDati;

        // Dopo il salvataggio parte una nuova rivalutazione asincrona, quindi il caricamento termina
        // solo quando il gate viene rilasciato e il giro più recente è completato.
        rilascio.Set();
        await vm.AttendiGiroAsync();
        Assert.False(vm.CaricamentoInCorso);
        Assert.Equal(revisioneSalvata, state.ChangeRequests.GetById("CR-2026-0001")!.RevisioneDati);
        // Con test di validazione obbligatori il salvataggio applica DaValidare e conserva la data/operatore di Implementa.
        Assert.Equal(StatoChangeRequest.DaValidare, state.ChangeRequests.GetById("CR-2026-0001")!.Stato);
    }

    [Fact]
    public async Task Chiudi_RicalcolaLaValidazioneInAttesaENonChiudeSeIlTestNonEConforme()
    {
        var (state, _) = CreaStato();
        var cr = state.ChangeRequests.GetById("CR-2026-0001")!;
        cr.Stato = StatoChangeRequest.Validata;
        state.ChangeRequests.Save(cr, cr.RevisioneDati);

        var test = state.TestCases.GetById("TEST-001")!;
        test.Esecuzioni.Single().Esito = Esito.NonConforme;
        state.TestCases.Save(test, isNew: false, revisioneAttesa: test.RevisioneDati);

        using var rilascio = new ManualResetEventSlim(false);
        var vm = Apri(state, "CR-2026-0001", forzaBackground: true, gate: () => rilascio.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(vm.CaricamentoInCorso);
        Assert.True(vm.PuoChiudere); // la schermata ha ancora lo stato Validata letto prima del ricalcolo
        var revisionePersistita = state.ChangeRequests.GetById("CR-2026-0001")!.RevisioneDati;

        vm.ChiudiCommand.Execute(null);

        Assert.False(vm.CaricamentoInCorso);
        Assert.Equal(StatoChangeRequest.DaValidare, vm.ChangeRequest.Stato);
        var persistita = state.ChangeRequests.GetById("CR-2026-0001")!;
        Assert.Equal(StatoChangeRequest.Validata, persistita.Stato);
        Assert.Equal(revisionePersistita, persistita.RevisioneDati);

        rilascio.Set();
        await vm.AttendiGiroAsync();
        Assert.True(vm.GiriScartati >= 1);
        Assert.Equal(StatoChangeRequest.Validata, state.ChangeRequests.GetById("CR-2026-0001")!.Stato);
    }

    [Fact]
    public async Task ModificaDeiDatiDellaCopertura_DopoLaCattura_LaCoperturaSiRicalcolaSulValoreAttuale()
    {
        var (state, _) = CreaStato();
        using var rilascio = new ManualResetEventSlim(false);
        var vm = Apri(state, "CR-2026-0001", forzaBackground: true, gate: () => rilascio.Wait(TimeSpan.FromSeconds(30)));

        // Mentre il giro e' in attesa, il veicolo impattato cambia (dato che entra nella copertura): il risultato catturato non vale piu'.
        vm.ChangeRequest.VeicoliImpattati.Add("Alpha 24");

        rilascio.Set();
        await vm.AttendiGiroAsync();

        Assert.False(vm.CaricamentoInCorso);
        Assert.Equal(StatoChangeRequest.DaValidare, vm.ChangeRequest.Stato);
        Assert.Contains("Alpha 24", vm.Messaggio);
        Assert.Equal(0, vm.GiriScartati);
    }
}
