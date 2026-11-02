using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.6.1 (correzione 1): la sonda della radice ha un tetto di tempo e non blocca il thread UI. Il test usa una sonda
/// sostituita e lenta (si ferma finché il test la rilascia): dimostra che il tetto vale davvero e che scaduto il tetto non si
/// crea né si semina nulla. La sonda originale viene sempre ripristinata.</summary>
[Collection("CondivisioneRete")]
public class RadiceCondivisaSondaTests : IDisposable
{
    private readonly string _radice = TestRete.NuovaDirectoria();
    private readonly Func<string, RadiceCondivisaSonda.Esito> _sondaOriginale = RadiceCondivisaSonda.SondaSistema;
    private readonly TimeSpan _etaOriginale = RadiceCondivisaSonda.EtaMassimaUi;
    private readonly ManualResetEventSlim _rilascio = new(false);

    /// <summary>Le verifiche con tetto valgono per i percorsi di rete: questi test usano una radice in modalità Rete, limitata a
    /// questa radice e quindi isolata dagli altri test in parallelo.</summary>
    private readonly OverrideModalitaScope _modalita;

    public RadiceCondivisaSondaTests()
    {
        RadiceCondivisaSonda.AzzeraEsitiNoti();
        _modalita = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, _radice);
    }

    public void Dispose()
    {
        _modalita.Dispose();
        _rilascio.Set(); // libera un'eventuale sonda ferma, così nessun thread di test resta appeso
        RadiceCondivisaSonda.SondaSistema = _sondaOriginale;
        RadiceCondivisaSonda.EtaMassimaUi = _etaOriginale;
        RadiceCondivisaSonda.AzzeraEsitiNoti();
        try { Directory.Delete(_radice, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Sonda che non risponde finché il test non la rilascia (simula una cartella di rete in stallo).</summary>
    private RadiceCondivisaSonda.Esito SondaBloccata(string _)
    {
        _rilascio.Wait(TimeSpan.FromSeconds(60));
        return RadiceCondivisaSonda.Esito.Presente;
    }

    [Fact]
    public void Verifica_SondaBloccata_RestituisceLentaAlTettoSenzaAttendere()
    {
        RadiceCondivisaSonda.SondaSistema = SondaBloccata;
        var sw = Stopwatch.StartNew();
        var esito = RadiceCondivisaSonda.Verifica(_radice, TimeSpan.FromMilliseconds(300));
        sw.Stop();
        Assert.Equal(RadiceCondivisaSonda.Esito.Lenta, esito);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"la sonda ha trattenuto il chiamante per {sw.Elapsed}");
    }

    [Fact]
    public void VerificaDisponibilita_SondaBloccata_NonDisponibileESenzaStrutture()
    {
        RadiceCondivisaSonda.SondaSistema = SondaBloccata;
        var sw = Stopwatch.StartNew();
        // Radice locale esistente e vuota: il tetto della modalità locale (3 s) deve scadere e nulla deve esservi creato.
        var esito = CondivisioneHealthService.VerificaDisponibilitaSenzaScrivere(_radice);
        sw.Stop();
        Assert.Equal(Models.StatoCondivisione.NonDisponibile, esito.Stato);
        Assert.Contains("non risponde", esito.Messaggio);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"preflight bloccato per {sw.Elapsed}");
        Assert.Empty(Directory.GetFileSystemEntries(_radice));
    }

    [Fact]
    public void Inizializzazione_SondaBloccata_NonApreIlWorkspaceENonCreaStrutture()
    {
        var configService = new AppConfigService(Path.Combine(TestRete.NuovaDirectoria(), "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = _radice, Lingua = "it", ModalitaCartellaCondivisa = "Rete" });
        RadiceCondivisaSonda.SondaSistema = SondaBloccata;
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        Assert.False(state.InizializzazioneWorkspaceCompletata);
        Assert.Equal(Models.StatoCondivisione.NonDisponibile, state.StatoCondivisione.Stato);
        Assert.False(state.CartellaValida);
        Assert.Empty(Directory.GetFileSystemEntries(_radice));
    }

    [Fact]
    public void CartellaValida_NonAttendeLaSondaELaLetturaSiAggiornaDaSola()
    {
        // Workspace aperto con la sonda normale; poi la cartella diventa un'unità che non risponde.
        var configService = new AppConfigService(Path.Combine(TestRete.NuovaDirectoria(), "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = _radice, Lingua = "it", ModalitaCartellaCondivisa = "Rete" });
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        Assert.True(state.CartellaValida);

        RadiceCondivisaSonda.SondaSistema = SondaBloccata;
        RadiceCondivisaSonda.EtaMassimaUi = TimeSpan.Zero;   // ogni lettura chiede una verifica di fondo

        // La lettura UI (chiamata da decine di binding) non deve attendere la sonda bloccata.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) _ = state.CartellaValida;
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"CartellaValida ha atteso la rete per {sw.Elapsed}");

        // Alla scadenza del tetto della verifica di fondo l'esito diventa "non risponde": la UI vede la cartella non valida.
        var diventataNonValida = WaitUntil(() => !state.CartellaValida, TimeSpan.FromSeconds(20));
        Assert.True(diventataNonValida, "la verifica di fondo non ha aggiornato la lettura UI dopo lo scadere del tetto");
    }

    [Fact]
    public void EsitoPerSottocartella_NonSovrascriveLEsitoDellaRadice()
    {
        var sottocartella = Path.Combine(_radice, "allegati");
        Directory.CreateDirectory(sottocartella);
        Assert.Equal(RadiceCondivisaSonda.Esito.Presente, RadiceCondivisaSonda.Verifica(_radice));
        Assert.Equal(RadiceCondivisaSonda.Esito.Presente, RadiceCondivisaSonda.Verifica(sottocartella));
        RadiceCondivisaSonda.SondaSistema = _ => RadiceCondivisaSonda.Esito.Assente;
        RadiceCondivisaSonda.Verifica(sottocartella);
        Assert.Equal(RadiceCondivisaSonda.Esito.Presente, RadiceCondivisaSonda.EsitoRecenteUi(_radice));
    }

    private static bool WaitUntil(Func<bool> condizione, TimeSpan durata)
    {
        var scadenza = DateTime.UtcNow + durata;
        while (DateTime.UtcNow < scadenza)
        {
            if (condizione()) return true;
            Thread.Sleep(50);
        }
        return condizione();
    }
}
