using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Accesso di test sopra <see cref="OneDriveFolderSyncSimulator"/>: un file della cartella A è "in attesa di caricamento"
/// finché non è arrivato nella cartella B. L'attesa del servizio fa avanzare il simulatore di un tick.</summary>
internal sealed class AccessoSuSimulatore : IOneDriveAccesso
{
    private readonly string _a;
    private readonly string _b;
    public AccessoSuSimulatore(string a, string b) { _a = a; _b = b; }

    public bool EDentroOneDrive(string percorso) => true;
    public bool ClientInEsecuzione() => true;
    public bool AvviaClient() => true;
    public void Scarica(string percorso, CancellationToken ct) { }

    public ScansioneCloud Scansiona(string radice, IReadOnlyCollection<string> percorsiSospetti, CancellationToken ct)
    {
        var tutti = Directory.EnumerateFiles(_a, "*", SearchOption.AllDirectories).ToList();
        var inAttesa = tutti
            .Where(f => !File.Exists(Path.Combine(_b, Path.GetRelativePath(_a, f))))
            .Select(f => new FileCloud(f, StatoFileCloud.InAttesaCaricamento, new FileInfo(f).Length))
            .ToList();
        return new ScansioneCloud(tutti.Count, inAttesa);
    }
}

public sealed class OneDriveAccessoETestSimulatoreTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "TestLoom_OneDriveSim_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_base, true); } catch (IOException) { } }

    [Fact]
    public async Task Simulatore_ChiusuraAttendeLArrivoSulCloud_PoiCompleta()
    {
        var a = Path.Combine(_base, "A");
        var b = Path.Combine(_base, "B");
        var sim = new OneDriveFolderSyncSimulator(a, b) { RitardoDiDefaultTick = 2 };
        File.WriteAllText(Path.Combine(a, "requisiti.json"), "{}");
        Directory.CreateDirectory(Path.Combine(a, "testcases"));
        File.WriteAllText(Path.Combine(a, "testcases", "t1.json"), "{}");

        var ora = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var servizio = new OneDriveSyncService(new AccessoSuSimulatore(a, b), new PendenteStoreInMemoria(),
            attesa: (t, ct) => { ora += t; sim.Propaga(); return Task.CompletedTask; }, oraUtc: () => ora);

        var r = await servizio.AttendiCaricamentoChiusuraAsync(a);

        Assert.Equal(EsitoSyncOneDrive.Completata, r.Esito);
        Assert.True(File.Exists(Path.Combine(b, "requisiti.json")));
        Assert.True(File.Exists(Path.Combine(b, "testcases", "t1.json")));
    }

    [Fact]
    public async Task Simulatore_FileBloccatoDaAltroProcesso_OltreIlTimeout_RestaPendente()
    {
        var a = Path.Combine(_base, "A");
        var b = Path.Combine(_base, "B");
        var sim = new OneDriveFolderSyncSimulator(a, b);
        File.WriteAllText(Path.Combine(a, "componente.json"), "{}");
        sim.BloccaTemporaneamente("componente.json", 1000);

        var ora = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new PendenteStoreInMemoria();
        var servizio = new OneDriveSyncService(new AccessoSuSimulatore(a, b), store,
            attesa: (t, ct) => { ora += t; sim.Propaga(); return Task.CompletedTask; }, oraUtc: () => ora);

        var r = await servizio.AttendiCaricamentoChiusuraAsync(a);

        Assert.Equal(EsitoSyncOneDrive.Timeout, r.Esito);
        Assert.Equal(1, r.DaCaricare);
        Assert.Equal(new[] { "componente.json" }, store.Valore!.Percorsi);
    }

    // ---- implementazione Windows reale su cartella temporanea (non OneDrive) ----------------------------------

    [Fact]
    public void AccessoWindows_CartellaTemporanea_NonEOneDrive_ENessunFileDaAllineare()
    {
        Directory.CreateDirectory(Path.Combine(_base, "d", "sotto"));
        File.WriteAllText(Path.Combine(_base, "d", "a.json"), "{}");
        File.WriteAllText(Path.Combine(_base, "d", "sotto", "b.json"), "{}");
        var acc = new OneDriveAccessoWindows();

        Assert.False(acc.EDentroOneDrive(Path.Combine(_base, "d")));
        var s = acc.Scansiona(Path.Combine(_base, "d"), Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(2, s.TotaleFile);
        // Nessun segnaposto Cloud Files: i file recenti non sono "in attesa" (non si può saperlo senza Files On-Demand).
        Assert.Empty(s.DaAllineare);
    }

    [Fact]
    public void AccessoWindows_FileNotoComePendenteDallaChiusura_RestaInAttesa()
    {
        Directory.CreateDirectory(Path.Combine(_base, "d"));
        File.WriteAllText(Path.Combine(_base, "d", "a.json"), "{}");
        File.WriteAllText(Path.Combine(_base, "d", "b.json"), "{}");

        var s = new OneDriveAccessoWindows().Scansiona(Path.Combine(_base, "d"), new[] { "a.json" }, CancellationToken.None);

        var unico = Assert.Single(s.DaAllineare);
        Assert.Equal(StatoFileCloud.InAttesaCaricamento, unico.Stato);
        Assert.EndsWith("a.json", unico.Percorso);
    }

    [Fact]
    public void AccessoWindows_PendenteVecchioNonSegnaposto_NonEPiuInAttesa_ILRecenteSi()
    {
        var d = Path.Combine(_base, "d");
        Directory.CreateDirectory(d);
        var vecchio = Path.Combine(d, "vecchio.json");
        var recente = Path.Combine(d, "recente.json");
        File.WriteAllText(vecchio, "{}");
        File.WriteAllText(recente, "{}");
        File.SetLastWriteTimeUtc(vecchio, DateTime.UtcNow - TimeSpan.FromDays(3));
        File.SetLastWriteTimeUtc(recente, DateTime.UtcNow - TimeSpan.FromHours(5));

        var s = new OneDriveAccessoWindows().Scansiona(d, new[] { "vecchio.json", "recente.json" }, CancellationToken.None);

        var unico = Assert.Single(s.DaAllineare);
        Assert.EndsWith("recente.json", unico.Percorso);
    }

    [Fact]
    public void Rilevante_FileDiHandshake_NonConta()
    {
        var r = Path.Combine(_base, "d");
        var f = new FileCloud(Path.Combine(r, "_ditm", "sync-handshake", "abc.json"), StatoFileCloud.InAttesaCaricamento, 1);
        Assert.False(OneDriveSyncService.Rilevante(r, f, avvio: true));
        Assert.True(OneDriveSyncService.Rilevante(r, f with { Percorso = Path.Combine(r, "testcases", "x.json") }, avvio: true));
    }

    [Fact]
    public void AccessoWindows_Scansione_Annullabile_ERadiceInesistenteNonLancia()
    {
        var acc = new OneDriveAccessoWindows();
        var s = acc.Scansiona(Path.Combine(_base, "non-esiste"), Array.Empty<string>(), CancellationToken.None);
        Assert.Equal(0, s.TotaleFile);
        Directory.CreateDirectory(Path.Combine(_base, "d"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => acc.Scansiona(Path.Combine(_base, "d"), Array.Empty<string>(), cts.Token));
    }

    [Fact]
    public void AccessoWindows_Scarica_LeggeIlFile_ENonLoModifica()
    {
        Directory.CreateDirectory(_base);
        var f = Path.Combine(_base, "x.json");
        File.WriteAllText(f, new string('x', 200_000));
        var prima = File.GetLastWriteTimeUtc(f);
        new OneDriveAccessoWindows().Scarica(f, CancellationToken.None);
        Assert.Equal(prima, File.GetLastWriteTimeUtc(f));
        Assert.Equal(200_000, new FileInfo(f).Length);
    }

    [Fact]
    public void AccessoWindows_RadiceOneDriveDaVariabileDAmbiente_Riconosciuta()
    {
        var originale = Environment.GetEnvironmentVariable("OneDriveCommercial");
        try
        {
            Environment.SetEnvironmentVariable("OneDriveCommercial", Path.Combine(_base, "OD"));
            var acc = new OneDriveAccessoWindows();
            Assert.True(acc.EDentroOneDrive(Path.Combine(_base, "OD", "TestLoom", "dati")));
            Assert.False(acc.EDentroOneDrive(Path.Combine(_base, "ODaltro", "dati"))); // prefisso non a confine di cartella
        }
        finally { Environment.SetEnvironmentVariable("OneDriveCommercial", originale); }
    }

    /// <summary>Smoke manuale su una cartella OneDrive reale: TestLoom_ONEDRIVE_SMOKE=<cartella>. Senza la variabile non fa nulla.</summary>
    [Fact]
    public void AccessoWindows_SmokeSuOneDriveReale_SeRichiesto()
    {
        var cartella = Environment.GetEnvironmentVariable("TestLoom_ONEDRIVE_SMOKE");
        if (string.IsNullOrWhiteSpace(cartella) || !Directory.Exists(cartella)) return;
        var acc = new OneDriveAccessoWindows();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = acc.Scansiona(cartella, Array.Empty<string>(), CancellationToken.None);
        Console.WriteLine($"SMOKE dentro={acc.EDentroOneDrive(cartella)} client={acc.ClientInEsecuzione()} totale={s.TotaleFile} " +
            $"soloOnline={s.DaAllineare.Count(f => f.Stato == StatoFileCloud.SoloOnline)} " +
            $"inAttesa={s.DaAllineare.Count(f => f.Stato == StatoFileCloud.InAttesaCaricamento)} ms={sw.ElapsedMilliseconds}");
        Assert.True(s.TotaleFile >= 0);
    }

    [Fact]
    public void PendenteStore_RoundTrip_ECancella()
    {
        var store = new OneDrivePendenteStore(Path.Combine(_base, "pend"));
        var radice = Path.Combine(_base, "shared");
        Assert.Null(store.Leggi(radice));
        store.Salva(radice, new OneDrivePendente { NumeroFile = 3, Percorsi = { "a.json", @"x\b.json" }, RegistratoIlUtc = DateTime.UtcNow });
        var letto = store.Leggi(radice);
        Assert.Equal(3, letto!.NumeroFile);
        Assert.Equal(2, letto.Percorsi.Count);
        store.Cancella(radice);
        Assert.Null(store.Leggi(radice));
    }
}
