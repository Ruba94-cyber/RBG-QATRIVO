using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Valutazione non mutante della cartella condivisa: radice, file chiave, copie di conflitto, campione limitato e avviso
/// di barra. Tutto sotto Path.GetTempPath(). Il comando Visualizzatore non viene mai eseguito qui perché scrive il marker
/// nel vero %LOCALAPPDATA%: lo stato di avviso si verifica solo tramite RileggiDaDisco e NotificaDatiRicaricati.
/// </summary>
public sealed class SharedFolderHealthMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SaluteCartella_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_SaluteCartellaLocal_" + Guid.NewGuid().ToString("N"));

    private DataPaths Paths => new(_root);

    /// <summary>Cartella con i file chiave: version-manifest.json e operatori.json alla radice, workspace-id.json in _ditm/.</summary>
    private void CreaWorkspaceSano()
    {
        Directory.CreateDirectory(Path.Combine(_root, "_ditm"));
        File.WriteAllText(Paths.VersionManifestFile, "{}");
        File.WriteAllText(Path.Combine(_root, "_ditm", "workspace-id.json"), "{}");
        File.WriteAllText(Paths.OperatoriFile, "[]");
    }

    private static string ScriviJson(string percorso)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(percorso)!);
        File.WriteAllText(percorso, "{}");
        return percorso;
    }

    /// <summary>Fotografia di nomi, date e dimensioni: serve a dimostrare che la valutazione non tocca nulla.</summary>
    private string[] Istantanea() =>
        Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Select(p => $"{p}|{(File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks + "|" + new FileInfo(p).Length : "dir")}")
            .Order()
            .ToArray();

    [Fact]
    public void WorkspaceSanoSenzaSegnali_RisultaDisponibile()
    {
        CreaWorkspaceSano();

        var esito = SharedFolderHealthMonitor.Valuta(Paths);

        Assert.Equal(SharedFolderHealthState.Disponibile, esito.Stato);
        Assert.Empty(esito.Motivi);
        // Campione: i due JSON alla radice. workspace-id.json sta in _ditm/, fuori dal campione.
        Assert.Equal(2, esito.FileEsaminati);
    }

    [Fact]
    public void RadiceInesistente_RisultaNonRaggiungibile_ELeScrittureRestanoBloccate()
    {
        var esito = SharedFolderHealthMonitor.Valuta(Paths);

        Assert.Equal(SharedFolderHealthState.NonRaggiungibile, esito.Stato);
        Assert.Single(esito.Motivi);
        Assert.Equal(0, esito.FileEsaminati);
        // Il blocco resta quello del gate esistente, che il monitor non sostituisce: verifichiamo che sia ancora attivo.
        Assert.False(WorkspaceWritePolicy.CanWrite(Paths));
        Assert.Throws<IOException>(() => WorkspaceWritePolicy.EnsureCanWrite(Paths, "Test salute cartella"));
    }

    [Fact]
    public void RadiceSoloOffline_RisultaNonRaggiungibile()
    {
        CreaWorkspaceSano();
        File.SetAttributes(_root, File.GetAttributes(_root) | FileAttributes.Offline);
        try
        {
            var esito = SharedFolderHealthMonitor.Valuta(Paths);

            Assert.Equal(SharedFolderHealthState.NonRaggiungibile, esito.Stato);
            Assert.Contains(esito.Motivi, m => m.Contains("offline", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.SetAttributes(_root, File.GetAttributes(_root) & ~FileAttributes.Offline);
        }
    }

    [Fact]
    public void FileChiaveMancante_RisultaIncertaMaNonBloccaLeScritture()
    {
        CreaWorkspaceSano();
        File.Delete(Paths.OperatoriFile);

        var esito = SharedFolderHealthMonitor.Valuta(Paths);

        Assert.Equal(SharedFolderHealthState.Incerta, esito.Stato);
        Assert.Contains(esito.Motivi, m => m.Contains("operatori.json", StringComparison.Ordinal));
    }

    [Fact]
    public void FileChiaveSoloOnline_RisultaIncertaSenzaAprirlo()
    {
        CreaWorkspaceSano();
        File.SetAttributes(Paths.OperatoriFile, File.GetAttributes(Paths.OperatoriFile) | FileAttributes.Offline);

        var esito = SharedFolderHealthMonitor.Valuta(Paths);

        Assert.Equal(SharedFolderHealthState.Incerta, esito.Stato);
        Assert.Contains(esito.Motivi, m => m.Contains("solo online", StringComparison.Ordinal) && m.Contains("operatori.json", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspacePhaseD_UsaReadyJsonComeChiaveDegliOperatori()
    {
        CreaWorkspaceSano();
        // Dopo il cutover Phase D il monolite operatori.json non viene più riscritto: non deve essere richiesto.
        File.Delete(Paths.OperatoriFile);
        var ready = PhaseDMasterBootstrapService.ReadyPath(_root);
        ScriviJson(PhaseDMasterJournalService.ManifestPath(_root));
        ScriviJson(ready);

        Assert.Equal(SharedFolderHealthState.Disponibile, SharedFolderHealthMonitor.Valuta(Paths).Stato);

        File.Delete(ready);
        var esito = SharedFolderHealthMonitor.Valuta(Paths);

        Assert.Equal(SharedFolderHealthState.Incerta, esito.Stato);
        Assert.Contains(esito.Motivi, m => m.Contains("ready.json", StringComparison.Ordinal));
    }

    [Fact]
    public void CopiaDiConflittoRecente_ConCanonicoAccanto_RisultaIncerta()
    {
        CreaWorkspaceSano();
        ScriviJson(Path.Combine(_root, "nonconformita", "nc_1.json"));
        ScriviJson(Path.Combine(_root, "nonconformita", "nc_1 (Laptop).json"));

        var esito = SharedFolderHealthMonitor.Valuta(Paths);

        Assert.Equal(SharedFolderHealthState.Incerta, esito.Stato);
        Assert.Contains(esito.Motivi, m => m.Contains("conflitto", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(" (Laptop)")]
    [InlineData("-PC2")]
    [InlineData("_LAPTOP-1")]
    public void CopiaDiConflittoConSuffissoEsplicito_RisultaIncerta(string suffisso)
    {
        CreaWorkspaceSano();
        ScriviJson(Path.Combine(_root, "nonconformita", "nc_5.json"));
        ScriviJson(Path.Combine(_root, "nonconformita", "nc_5" + suffisso + ".json"));

        Assert.Equal(SharedFolderHealthState.Incerta, SharedFolderHealthMonitor.Valuta(Paths).Stato);
    }

    [Fact]
    public void CopiaDiConflittoVecchiaOSenzaCanonico_NonSegnala()
    {
        CreaWorkspaceSano();
        ScriviJson(Path.Combine(_root, "nonconformita", "nc_2.json"));
        var vecchia = ScriviJson(Path.Combine(_root, "nonconformita", "nc_2 (Laptop).json"));
        File.SetLastWriteTime(vecchia, DateTime.Now.AddDays(-10));
        Assert.Equal(SharedFolderHealthState.Disponibile, SharedFolderHealthMonitor.Valuta(Paths).Stato);

        // Una copia recente senza il canonico accanto non è una copia di conflitto riconosciuta: nessun falso allarme.
        ScriviJson(Path.Combine(_root, "nonconformita", "nc_3 (Laptop).json"));
        Assert.Equal(SharedFolderHealthState.Disponibile, SharedFolderHealthMonitor.Valuta(Paths).Stato);
    }

    [Fact]
    public void IdAmbiguoSimileAlCanonico_NonEUnaCopiaDiConflitto()
    {
        CreaWorkspaceSano();
        // Stessa regola della guardia di scrittura: "CR-2-TEST" può essere un record diverso da "CR-2".
        ScriviJson(Path.Combine(_root, "change-requests", "cr_CR-2.json"));
        ScriviJson(Path.Combine(_root, "change-requests", "cr_CR-2-TEST.json"));

        Assert.Equal(SharedFolderHealthState.Disponibile, SharedFolderHealthMonitor.Valuta(Paths).Stato);
    }

    [Fact]
    public void JsonOfflineNelCampione_RispettaIlLimiteDiFile()
    {
        CreaWorkspaceSano();
        for (var i = 0; i < 10; i++)
        {
            var percorso = ScriviJson(Path.Combine(_root, "nonconformita", $"nc_{i:000}.json"));
            File.SetAttributes(percorso, File.GetAttributes(percorso) | FileAttributes.Offline);
        }

        var esito = SharedFolderHealthMonitor.Valuta(Paths, new SharedFolderHealthOptions { MaxFileCampionati = 3 });

        Assert.Equal(SharedFolderHealthState.Incerta, esito.Stato);
        Assert.Equal(3, esito.FileEsaminati);
        Assert.Contains(esito.Motivi, m => m.Contains("solo online", StringComparison.Ordinal));
    }

    [Fact]
    public void CampioneDiDefault_NonSuperaDuecentoFile()
    {
        CreaWorkspaceSano();
        for (var i = 0; i < 250; i++)
            ScriviJson(Path.Combine(_root, "nonconformita", $"nc_{i:000}.json"));

        var esito = SharedFolderHealthMonitor.Valuta(Paths);

        Assert.Equal(SharedFolderHealthMonitor.DefaultMaxFileCampionati, esito.FileEsaminati);
        Assert.Equal(SharedFolderHealthState.Disponibile, esito.Stato);
    }

    [Fact]
    public void ValutazioneDiDefault_NonMutaLaCartella()
    {
        CreaWorkspaceSano();
        ScriviJson(Path.Combine(_root, "nonconformita", "nc_1 (Laptop).json"));
        var prima = Istantanea();

        var esito = SharedFolderHealthMonitor.Valuta(Paths);

        Assert.Equal(SharedFolderHealthState.Disponibile, esito.Stato);
        Assert.Equal(prima, Istantanea());
        Assert.False(File.Exists(Path.Combine(_root, ".testloom-write-probe")));
    }

    [Fact]
    public void ProbeEsplicito_SuCartellaScrivibile_RisultaDisponibileERimuoveIlProbe()
    {
        CreaWorkspaceSano();

        var esito = SharedFolderHealthMonitor.Valuta(Paths, new SharedFolderHealthOptions { EseguiProbeScrittura = true });

        Assert.Equal(SharedFolderHealthState.Disponibile, esito.Stato);
        Assert.False(File.Exists(Path.Combine(_root, ".testloom-write-probe")));
    }

    [Fact]
    public void ProbeEsplicito_SuScritturaFallita_RisultaNonRaggiungibile()
    {
        CreaWorkspaceSano();
        // Una directory con lo stesso nome del probe impedisce la creazione del file: la scrittura reale fallisce.
        Directory.CreateDirectory(Path.Combine(_root, ".testloom-write-probe"));

        var esito = SharedFolderHealthMonitor.Valuta(Paths, new SharedFolderHealthOptions { EseguiProbeScrittura = true });

        Assert.Equal(SharedFolderHealthState.NonRaggiungibile, esito.Stato);
        Assert.Contains(esito.Motivi, m => m.Contains("Prova di scrittura", StringComparison.Ordinal));
    }

    /// <summary>AppState con un Master attivo, stessa impostazione di AppScaldamentoMasterPagineTests: la valutazione
    /// periodica richiede un operatore attivo, altrimenti non produce alcun avviso (primo avvio).</summary>
    private AppState CreaStatoConOperatore()
    {
        var dataRoot = Path.Combine(_root, "cartella");
        Directory.CreateDirectory(dataRoot);
        var configService = new AppConfigService(Path.Combine(_local, "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Master Test" });

        var state = new AppState(configService, runBackgroundMaintenance: false);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    private static void ScriviNcConflittoRecente(DataPaths paths, string id)
    {
        var canonico = paths.NonConformitaFile(id);
        JsonIo.WriteAtomic(canonico, new NonConformita { Uid = Guid.NewGuid(), Id = id, RevisioneDati = 1 });
        File.Copy(canonico, Path.Combine(paths.NonConformitaDir, $"nc_{id} (Laptop).json"));
    }

    [Fact]
    public void RileggiDaDisco_AggiornaLaSalutePeriodica_SenzaBloccareLeScritture()
    {
        using var state = CreaStatoConOperatore();
        Assert.True(state.InizializzazioneWorkspaceCompletata);

        state.RileggiDaDisco();
        var baseline = state.SaluteCartellaCondivisa;
        Assert.True(baseline?.Stato == SharedFolderHealthState.Disponibile,
            "Motivi baseline: " + string.Join(" | ", baseline?.Motivi ?? Array.Empty<string>()));

        ScriviNcConflittoRecente(state.Paths, "NC-SAL-1");
        state.RileggiDaDisco();

        Assert.Equal(SharedFolderHealthState.Incerta, state.SaluteCartellaCondivisa?.Stato);
        // Incerta è un avviso: il gate di scrittura (PuoScrivere) non cambia.
        Assert.True(state.PuoScrivere);
    }

    [Fact]
    public void AvvisoCartellaIncerta_CompareNellaBarraDiStato()
    {
        using var state = CreaStatoConOperatore();
        using var main = new MainViewModel(state);
        state.RileggiDaDisco();
        state.NotificaDatiRicaricati();
        Assert.False(main.MostraAvvisoCartellaIncerta);

        ScriviNcConflittoRecente(state.Paths, "NC-SAL-2");
        state.RileggiDaDisco();
        state.NotificaDatiRicaricati();

        Assert.True(main.MostraAvvisoCartellaIncerta);
        Assert.Contains("conflitto", main.TestoAvvisoCartellaIncerta, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        try { if (Directory.Exists(_local)) Directory.Delete(_local, recursive: true); } catch { }
    }
}
