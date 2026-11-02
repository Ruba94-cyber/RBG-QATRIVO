using TestLoom.App.Models;
using TestLoom.App.Services;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>Punto 0 del piano Codex (handoff 2026-09-18): "TestTemplateDisplay conserva una mappa statica
/// globale, potenzialmente sovrascritta da AppState di workspace diversi; manca ancora un difetto riprodotto
/// e una correzione scoped".
///
/// Indagine: TestTemplateDisplay._nomi è davvero un campo statico condiviso e AppState.RicaricaTutto() lo
/// sovrascrive incondizionatamente (AppState.cs). In produzione esiste un solo AppState per processo
/// (App.axaml.cs), quindi il rischio reale oggi è confinato ai test - due AppState indipendenti creati da
/// classi di test diverse (collection separate, quindi eseguite in parallelo da xunit di default) possono
/// sovrascriversi a vicenda. Prima di questi test non c'erano asserzioni dirette su NomeDi: questa race
/// non spiega da sola i 10-22 fallimenti sporadici della suite completa.
///
/// Correzione scoped: i 6 punti di lettura che avevano già un AppState/lista Master.TestTemplate a
/// disposizione (HomeViewModel, ReportViewModel, NonConformitaViewModel, TestCaseEditorViewModel,
/// ReportService, TicketExportService) sono stati spostati su AppState.NomeTestTemplate, scoped alla propria
/// istanza. Resta solo TestTemplateLabelConverter (XAML, nessun AppState raggiungibile dal binding) sul
/// singleton statico - stesso principio, stesso rischio residuo, già accettato per LocalizationService.Instance.
/// </summary>
[Collection("Localization")]
public sealed class TestTemplateDisplayRaceTests : IDisposable
{
    private readonly List<AppState> _stati = new();
    private readonly List<string> _cartelle = new();
    private readonly ITestOutputHelper _output;

    public TestTemplateDisplayRaceTests(ITestOutputHelper output) => _output = output;

    private AppState CreaStatoConTemplate(string nomeTemplate, out Guid uid)
    {
        var state = TestStateFactory.Create();
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_TestTemplateRace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _cartelle.Add(dir);
        _stati.Add(state);
        state.InizializzaCartella(dir);
        uid = Guid.NewGuid();
        state.Master.TestTemplate.Save(new List<TestTemplate> { new() { Uid = uid, Nome = nomeTemplate } });
        state.RicaricaTutto();
        return state;
    }

    /// <summary>Riproduce il difetto: il singleton statico TestTemplateDisplay._nomi, letto/scritto da due
    /// AppState indipendenti in thread paralleli, produce nomi "sbagliati" (di un altro AppState) - fallisce
    /// se eseguito senza le sezioni critiche protette, cosa che qui NON facciamo apposta per dimostrare la
    /// race reale, non simulata.</summary>
    [Fact]
    public void VecchioSingletonStatico_ConDueAppStateInParallelo_RestituisceOccasionalmenteIlNomeAltrui()
    {
        var stateA = CreaStatoConTemplate("Template-A", out var uidA);
        var stateB = CreaStatoConTemplate("Template-B", out var uidB);

        var visteSbagliate = 0;
        using var entrambiHannoAggiornato = new Barrier(2);

        Parallel.Invoke(
            () =>
            {
                stateA.RicaricaTutto();
                entrambiHannoAggiornato.SignalAndWait();
                if (TestTemplateDisplay.NomeDi(uidA) != "Template-A") Interlocked.Increment(ref visteSbagliate);
            },
            () =>
            {
                stateB.RicaricaTutto();
                entrambiHannoAggiornato.SignalAndWait();
                if (TestTemplateDisplay.NomeDi(uidB) != "Template-B") Interlocked.Increment(ref visteSbagliate);
            });

        Assert.Equal(1, visteSbagliate);
    }

    /// <summary>Stessa concorrenza della race riprodotta sopra, ma leggendo tramite la correzione scoped
    /// (AppState.NomeTestTemplate): ogni AppState deve vedere SEMPRE e SOLO il proprio nome, indipendentemente
    /// da quante volte l'altro thread sovrascrive il singleton statico in parallelo.</summary>
    [Fact]
    public void CorrezioneScoped_ConDueAppStateInParallelo_VedeSempreIlProprioNome()
    {
        var stateA = CreaStatoConTemplate("Template-A", out var uidA);
        var stateB = CreaStatoConTemplate("Template-B", out var uidB);

        var visteSbagliate = 0;
        using var entrambiHannoAggiornato = new Barrier(2);

        Parallel.Invoke(
            () =>
            {
                stateA.RicaricaTutto();
                entrambiHannoAggiornato.SignalAndWait();
                if (stateA.NomeTestTemplate(uidA) != "Template-A") Interlocked.Increment(ref visteSbagliate);
            },
            () =>
            {
                stateB.RicaricaTutto();
                entrambiHannoAggiornato.SignalAndWait();
                if (stateB.NomeTestTemplate(uidB) != "Template-B") Interlocked.Increment(ref visteSbagliate);
            });

        Assert.Equal(0, visteSbagliate);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void MilleNomiTemplate_ConfrontaLoadPerRigaConMappaScoped()
    {
        var state = CreaStatoConTemplate("Template-A", out var uid);
        const int chiamate = 1000;
        var prima = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < chiamate; i++)
            Assert.Equal("Template-A", state.Master.TestTemplate.Load().First(t => t.Uid == uid).Nome);
        prima.Stop();
        var dopo = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < chiamate; i++)
            Assert.Equal("Template-A", state.NomeTestTemplate(uid));
        dopo.Stop();
        _output.WriteLine($"TEMPLATE_LOOKUP|chiamate={chiamate}|prima_ms={prima.Elapsed.TotalMilliseconds:F1}|dopo_ms={dopo.Elapsed.TotalMilliseconds:F1}");
    }

    public void Dispose()
    {
        foreach (var state in _stati) state.Dispose();
        foreach (var dir in _cartelle)
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { }
    }
}
