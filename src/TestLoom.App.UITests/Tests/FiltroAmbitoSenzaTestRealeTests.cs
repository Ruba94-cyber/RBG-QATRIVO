using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Prova end-to-end sull'eseguibile reale contro l'archivio di benchmark da 100k (richiesta utente
/// 2026-09-22: "fai anche la prova con l'app reale e usando i dati demo veri... in modo da avere la prova
/// provata"). La cartella condivisa e' una COPIA dell'archivio demo, mai l'originale, e nella copia sono state
/// seminate tre segnalazioni di rete senza Test collegato: due su componente-TEST-BCM e una su componente-TEST-SGW.
///
/// Prima della correzione i filtri componente/Veicolo scartavano ogni NC con tc == null e la ricerca libera non
/// guardava l'ambito proprio della NC: entrambe le asserzioni qui sotto tornavano 0.
///
/// Il percorso richiede TestLoom_UI_WORKSPACE_100K impostata sulla copia: senza, i test si auto-escludono invece
/// di fallire, perche' l'archivio da 616 MB non e' un fixture che la suite possa creare da sola.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Scale")]
public sealed class FiltroAmbitoSenzaTestRealeTests : IDisposable
{
    private const string EcuSeminata = "Componente-TEST-BCM";
    private const string TestoSeminato = "PROVA FIX";
    private const string Operatore = "Operatore Prova A";

    private readonly TestWorkspace? _workspace;
    private readonly TestLoomApplication? _app;
    private readonly string? _motivoSkip;

    public FiltroAmbitoSenzaTestRealeTests()
    {
        var cartella = Environment.GetEnvironmentVariable("TestLoom_UI_WORKSPACE_100K");
        if (string.IsNullOrWhiteSpace(cartella) || !Directory.Exists(cartella))
        {
            _motivoSkip = "TestLoom_UI_WORKSPACE_100K non impostata o inesistente.";
            return;
        }

        _workspace = TestWorkspace.AdottaCartellaEsistente(nameof(FiltroAmbitoSenzaTestRealeTests), cartella, Operatore);

        // Il filtro componente e' persistito fra sessioni in AppConfig.UltimaSelezionePagine: impostandolo qui l'app
        // si apre gia' filtrata, senza dover pilotare la tendina multi-selezione (un popup, il punto piu'
        // fragile da automatizzare). Si scrive direttamente sul config, NON aprendo un AppState: quello
        // caricherebbe e deserializzerebbe gli oltre 100.000 file JSON dell'archivio dentro il processo di
        // test, per poi farlo ancora una volta nel processo dell'app.
        var configService = new AppConfigService(_workspace.ConfigPath);
        var config = configService.Load();
        config.UltimaSelezionePagine["AnomalieCr.FiltroEcu"] =
            System.Text.Json.JsonSerializer.Serialize(new List<string> { EcuSeminata });
        configService.Save(config);

        _app = TestLoomApplication.Launch(_workspace);
    }

    [Fact]
    public void FiltroEcu_SullArchivioReale_TrovaLeSegnalazioniDiReteSenzaTest()
    {
        if (_motivoSkip != null) return;

        var window = _app!.WaitForMainWindow();
        var shell = new MainShellPage(window);
        shell.DismissNotificationsIfShown();
        var pagina = shell.GoToAnomalie();

        Assert.Contains(EcuSeminata, pagina.TestoFiltroEcu());

        pagina.Cerca(TestoSeminato);

        Assert.Equal(2, pagina.TotaleFiltratoStabile());
    }

    [Fact]
    public void RicercaLibera_SullArchivioReale_TrovaPerNomeEcu()
    {
        if (_motivoSkip != null) return;

        var window = _app!.WaitForMainWindow();
        var shell = new MainShellPage(window);
        shell.DismissNotificationsIfShown();
        var pagina = shell.GoToAnomalie();

        pagina.Cerca(EcuSeminata);

        // L'archivio di benchmark contiene gia' di suo migliaia di segnalazioni di rete con l'ambito
        // valorizzato, quindi un numero esatto sarebbe legato ai dati e non alla correzione: qui conta che il
        // nome della centralina sia diventato cercabile, cosa che prima non era (il vecchio codice dava 0).
        var totale = pagina.TotaleFiltratoStabile();
        Assert.True(totale >= 2, $"Attese almeno le 2 segnalazioni seminate su {EcuSeminata}, trovate {totale}.");
    }

    public void Dispose()
    {
        _app?.Dispose();
        _workspace?.Dispose();
    }
}
