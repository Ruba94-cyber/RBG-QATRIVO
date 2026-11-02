using System.Collections.Concurrent;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Copre il canale di avanzamento del bootstrap di AppState (feedback utente: "anche la prima apertura se
/// pur lenta di 5/7 min dovrebbe mostrarmi l'indicazione che sta ricostruendo il db... e dovrebbe dirmi
/// anche quanto tempo manca alla fine"). I file JSON sono scritti direttamente su disco (bypassando i
/// repository) così il primo AppState su questa cartella trova sempre indice locale assente e deve fare la
/// riconciliazione completa: è esattamente lo scenario "prima apertura lenta" del bug report, solo in scala
/// ridotta per restare un test veloce.
/// </summary>
[Trait("Category", "Lento")]
public sealed class AppStateBootstrapProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_BootstrapProgress_" + Guid.NewGuid().ToString("N"));
    private const int NumeroTest = 1500;
    private const int NumeroNc = 400;

    private sealed class SinkConcorrente : IProgress<BootstrapProgress>
    {
        public readonly ConcurrentQueue<BootstrapProgress> Ricevuti = new();
        public void Report(BootstrapProgress value) => Ricevuti.Enqueue(value);
    }

    private string CreaWorkspaceConDatiGrezzi()
    {
        var dataRoot = Path.Combine(_root, "data");
        var paths = new DataPaths(dataRoot);
        paths.EnsureStructure();
        for (var i = 0; i < NumeroTest; i++)
            JsonIo.WriteAtomic(paths.TestCaseFile("BCM", $"TC-{i:D5}"), new TestCase { Id = $"TC-{i:D5}", Componente = "BCM" });
        for (var i = 0; i < NumeroNc; i++)
            JsonIo.WriteAtomic(paths.NonConformitaFile($"NC-{i:D5}"), new NonConformita
            {
                Id = $"NC-{i:D5}",
                TestCaseId = $"TC-{i % NumeroTest:D5}",
                Descrizione = "generata dal test",
                DataApertura = DateTime.Now,
                Severita = SeveritaAnomalia.Low
            });
        return dataRoot;
    }

    private AppConfigService CreaConfigService(string dataRoot)
    {
        var configPath = Path.Combine(_root, "local", "config.json");
        var configService = new AppConfigService(configPath);
        // CartellaCondivisa già impostata PRIMA della costruzione di AppState: il bootstrap innescato dal
        // costruttore (lo stesso percorso usato da App.axaml.cs all'avvio reale) parte subito, non serve un
        // ImpostaCartella successivo.
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it" });
        return configService;
    }

    [Fact]
    public void PrimoAvvio_IndiceAssente_RiportaLeFasiAtteseEIConteggiCorretti()
    {
        var dataRoot = CreaWorkspaceConDatiGrezzi();
        var configService = CreaConfigService(dataRoot);
        var spia = new SinkConcorrente();

        using var state = new AppState(configService, runBackgroundMaintenance: false, progress: spia);

        Assert.True(state.InizializzazioneWorkspaceCompletata);
        Assert.Equal(NumeroTest, state.TestCases.GetAll().Count);
        Assert.Equal(NumeroNc, state.NonConformita.GetAll().Count);

        var eventi = spia.Ricevuti.ToList();
        Assert.NotEmpty(eventi);

        // Le fasi in parole comprensibili richieste dal feedback utente: devono comparire tutte, nell'ordine
        // in cui il bootstrap le attraversa davvero (AppState.InizializzaCartella).
        var fasiViste = eventi.Select(e => e.Fase).Distinct().ToList();
        foreach (var faseAttesa in new[]
                 {
                     FaseBootstrap.Avvio, FaseBootstrap.VerificaWorkspace, FaseBootstrap.RipristinoTransazioni,
                     FaseBootstrap.CaricamentoTest, FaseBootstrap.CaricamentoNonConformita,
                     FaseBootstrap.CaricamentoDatiCorrelati, FaseBootstrap.Completato
                 })
            Assert.Contains(faseAttesa, fasiViste);

        // Nessun messaggio vuoto: la schermata di avvio mostra SEMPRE una frase comprensibile, mai un vuoto.
        Assert.All(eventi, e => Assert.False(string.IsNullOrWhiteSpace(e.Messaggio)));

        // Il totale per i Test deve comparire ed essere quello reale (mai una percentuale finta prima che il
        // totale sia conosciuto: la primissima notifica di CaricamentoTest con Totale valorizzato arriva con
        // Elaborati=0, poi cresce verso NumeroTest).
        var progressoTest = eventi.Where(e => e.Fase == FaseBootstrap.CaricamentoTest && e.Totale.HasValue).ToList();
        Assert.NotEmpty(progressoTest);
        Assert.All(progressoTest, e => Assert.Equal(NumeroTest, e.Totale));
        Assert.All(progressoTest, e => Assert.InRange(e.Elaborati!.Value, 0, NumeroTest));

        var progressoNc = eventi.Where(e => e.Fase == FaseBootstrap.CaricamentoNonConformita && e.Totale.HasValue).ToList();
        Assert.NotEmpty(progressoNc);
        Assert.All(progressoNc, e => Assert.Equal(NumeroNc, e.Totale));

        // Coerenza della stima: se un evento riporta un ritmo (elaborati tra 0 e totale escluso), la stima
        // del tempo rimanente non deve mai essere negativa, e questo evento arriva assieme al trascorso reale.
        foreach (var e in eventi.Where(e => e.StimaRimanente.HasValue))
        {
            Assert.True(e.StimaRimanente!.Value >= TimeSpan.Zero);
            Assert.True(e.Trascorso >= TimeSpan.Zero);
        }
    }

    [Fact]
    public void SecondoAvvio_IndiceGiaPresente_NonRiportaUnaRicostruzioneCompleta()
    {
        var dataRoot = CreaWorkspaceConDatiGrezzi();

        // Primo avvio: costruisce l'indice locale persistente (senza spiare l'avanzamento, non è l'oggetto
        // di questo test).
        using (var primoStato = new AppState(CreaConfigService(dataRoot), runBackgroundMaintenance: false))
            Assert.True(primoStato.InizializzazioneWorkspaceCompletata);

        // Secondo avvio sulla STESSA cartella: indice locale già presente, il bootstrap deve restare rapido
        // (requisito esplicito: "quando l'indice esiste già... la schermata non deve comparire o sfarfallare").
        // Qui verifichiamo il presupposto di quel requisito: nessuna riconciliazione completa con un totale
        // grande viene riportata, perché TestCaseRepository.HasPersistentSnapshot è vero e RileggiTestCases
        // prende il percorso incrementale, non quello di ricostruzione.
        var spia = new SinkConcorrente();
        using var secondoStato = new AppState(CreaConfigService(dataRoot), runBackgroundMaintenance: false, progress: spia);

        Assert.True(secondoStato.InizializzazioneWorkspaceCompletata);
        Assert.Equal(NumeroTest, secondoStato.TestCases.GetAll().Count);

        var eventi = spia.Ricevuti.ToList();
        Assert.DoesNotContain(eventi, e => e.Fase == FaseBootstrap.CaricamentoTest && e.Totale == NumeroTest);
        Assert.DoesNotContain(eventi, e => e.Fase == FaseBootstrap.CaricamentoNonConformita && e.Totale == NumeroNc);
    }

    [Fact]
    public void CostruttoreSenzaCanaleDiAvanzamento_ContinuaAFunzionareIdentico()
    {
        // Firma storica usata da moltissimi altri test (`new AppState(configService, runBackgroundMaintenance: false)`):
        // deve restare compilabile ed eseguibile senza alcun cambiamento di comportamento visibile.
        var dataRoot = CreaWorkspaceConDatiGrezzi();
        using var state = new AppState(CreaConfigService(dataRoot), runBackgroundMaintenance: false);

        Assert.True(state.InizializzazioneWorkspaceCompletata);
        Assert.Equal(NumeroTest, state.TestCases.GetAll().Count);
        Assert.Equal(NumeroNc, state.NonConformita.GetAll().Count);
    }

    [Fact]
    public void CostruttoreConSoloConfigService_OmettendoGliAltriParametriOpzionali_ContinuaAFunzionare()
    {
        // Firma minimale `new AppState(configService)`, senza runBackgroundMaintenance né progress: entrambi
        // restano ai valori di default (true / null) esattamente come prima dell'aggiunta del parametro
        // progress. Config isolata dedicata, mai il config.json reale dell'utente (regola del progetto).
        // Nessuna cartella condivisa configurata: il bootstrap non ha nulla da aprire, quindi resta rapido
        // anche con la manutenzione in background abilitata di default.
        using var state = new AppState(new AppConfigService(Path.Combine(_root, "isolato", "config.json")));
        Assert.False(state.InizializzazioneWorkspaceCompletata);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
