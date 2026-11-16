using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

[Collection("OneDriveStatico")]
public sealed class OneDriveMonitorContinuoTests : IDisposable
{
    private sealed class AccessoFinto : IOneDriveAccesso
    {
        public bool Client = true;
        public bool Internet = true;
        public bool InternetOnline() => Internet;
        public bool DentroOneDrive = true;
        public List<FileCloud> DaAllineare = new();
        public Exception? ErroreScansione;
        public bool EDentroOneDrive(string percorso) => DentroOneDrive;
        public bool ClientInEsecuzione() => Client;
        public bool AvviaClient() => false;
        public void Scarica(string percorso, CancellationToken ct) { }
        public ScansioneCloud Scansiona(string radice, IReadOnlyCollection<string> percorsiSospetti, CancellationToken ct)
        {
            if (ErroreScansione != null) throw ErroreScansione;
            return new ScansioneCloud(DaAllineare.Count, DaAllineare.ToList());
        }
    }

    private sealed class StoreFinto : IOneDrivePendenteStore
    {
        public OneDrivePendente? Leggi(string radice) => null;
        public void Salva(string radice, OneDrivePendente pendente) { }
        public void Cancella(string radice) { }
    }

    private static readonly string Radice = Path.Combine(Path.GetTempPath(), "ditm_monitor_onedrive");
    private DateTime _ora = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private readonly AccessoFinto _accesso = new();
    private readonly OneDriveSyncService _servizioPrecedente = OneDriveSync.Servizio;

    public OneDriveMonitorContinuoTests()
    {
        OneDriveSyncGate.Sblocca(Radice);
        OneDriveSync.Servizio = new OneDriveSyncService(_accesso, new StoreFinto());
        OneDriveSync.ImpostaMonitorPerTest(NuovoMonitor(), Radice);
    }

    public void Dispose()
    {
        OneDriveSyncGate.Sblocca(Radice);
        OneDriveSync.ImpostaMonitorPerTest(null, null);
        OneDriveSync.Servizio = _servizioPrecedente;
    }

    private OneDriveMonitorContinuo NuovoMonitor() => new(_accesso, new OneDriveMonitorOptions(), () => _ora);

    private FileCloud InAttesa(string nome) => new(Path.Combine(Radice, "test", nome), StatoFileCloud.InAttesaCaricamento, 10);

    [Fact]
    public void ClientAttivoENessunFile_Ok()
    {
        var r = NuovoMonitor().Controlla(Radice);
        Assert.True(r.Sicuro);
    }

    [Fact]
    public void ClientAssente_BloccaSoloDopoLaSogliaESiSbloccaQuandoTorna()
    {
        var monitor = NuovoMonitor();
        _accesso.Client = false;
        Assert.True(monitor.Controlla(Radice).Sicuro);                       // appena sparito: tolleranza (riavvio del client)
        _ora += TimeSpan.FromSeconds(61);
        Assert.Equal(EsitoMonitorOneDrive.ClientNonAttivo, monitor.Controlla(Radice).Esito);
        _accesso.Client = true;
        Assert.True(monitor.Controlla(Radice).Sicuro);
    }

    [Fact]
    public void FileInAttesaOltreLaSoglia_SegnalaCaricamentoFermo()
    {
        var monitor = NuovoMonitor();
        _accesso.DaAllineare.Add(InAttesa("T-1.json"));
        Assert.True(monitor.Controlla(Radice).Sicuro);
        _ora += TimeSpan.FromMinutes(9);
        Assert.True(monitor.Controlla(Radice).Sicuro);
        _ora += TimeSpan.FromMinutes(2);
        var r = monitor.Controlla(Radice);
        Assert.Equal(EsitoMonitorOneDrive.CaricamentoFermo, r.Esito);
        Assert.Equal(1, r.FileFermi);
    }

    [Fact]
    public void FileCheSiAllineaORientra_AzzeraIlConteggioDelTempo()
    {
        var monitor = NuovoMonitor();
        var f = InAttesa("T-1.json");
        _accesso.DaAllineare.Add(f);
        monitor.Controlla(Radice);
        _ora += TimeSpan.FromMinutes(8);
        _accesso.DaAllineare.Clear();                  // caricato
        monitor.Controlla(Radice);
        _ora += TimeSpan.FromMinutes(5);
        _accesso.DaAllineare.Add(f);                   // di nuovo in attesa: riparte da zero
        Assert.True(monitor.Controlla(Radice).Sicuro);
        _ora += TimeSpan.FromMinutes(9);
        Assert.True(monitor.Controlla(Radice).Sicuro);
    }

    [Fact]
    public void TemporaneiEAllegatiSoloOnline_NonContano()
    {
        var monitor = NuovoMonitor();
        _accesso.DaAllineare.Add(new FileCloud(Path.Combine(Radice, "test", "a.json.tmp1a2b3c4d"), StatoFileCloud.InAttesaCaricamento, 1));
        _accesso.DaAllineare.Add(new FileCloud(Path.Combine(Radice, "allegati", "x.pdf"), StatoFileCloud.SoloOnline, 1));
        monitor.Controlla(Radice);
        _ora += TimeSpan.FromHours(1);
        Assert.True(monitor.Controlla(Radice).Sicuro);
    }

    [Fact]
    public void ErroreDiLetturaDuranteIlControllo_NonBloccaNulla()
    {
        var monitor = NuovoMonitor();
        _accesso.ErroreScansione = new IOException("disco momentaneamente non leggibile");
        Assert.True(monitor.Controlla(Radice).Sicuro);
    }

    [Fact]
    public void ControlloContinuo_ClientPersoMettePoiToglieLaSolaLettura()
    {
        _accesso.Client = false;
        Assert.False(OneDriveSync.ControlloContinuo(Radice));            // tolleranza iniziale
        Assert.False(OneDriveSyncGate.Attivo(Radice));
        _ora += TimeSpan.FromSeconds(61);
        Assert.True(OneDriveSync.ControlloContinuo(Radice));             // blocco applicato
        Assert.True(OneDriveSyncGate.Attivo(Radice));
        Assert.False(OneDriveSync.ControlloContinuo(Radice));            // gia' bloccato: nessun cambiamento

        _accesso.Client = true;
        Assert.True(OneDriveSync.ControlloContinuo(Radice));             // torna a sincronizzare: sblocco
        Assert.False(OneDriveSyncGate.Attivo(Radice));
    }

    [Fact]
    public void ControlloContinuo_SenzaInternet_MettePoiToglieLaSolaLettura()
    {
        _accesso.Internet = false;
        Assert.False(OneDriveSync.ControlloContinuo(Radice));            // tolleranza iniziale
        _ora += TimeSpan.FromSeconds(61);
        Assert.True(OneDriveSync.ControlloContinuo(Radice));
        Assert.True(OneDriveSyncGate.Attivo(Radice));
        Assert.Contains("Internet", OneDriveSyncGate.Motivo(Radice));
        _accesso.Internet = true;
        Assert.True(OneDriveSync.ControlloContinuo(Radice));
        Assert.False(OneDriveSyncGate.Attivo(Radice));
    }

    [Fact]
    public void ControlloContinuo_CaricamentoFermo_NonMetteInSolaLettura()
    {
        _accesso.DaAllineare.Add(InAttesa("T-1.json"));
        Assert.False(OneDriveSync.ControlloContinuo(Radice));
        _ora += TimeSpan.FromMinutes(11);
        Assert.False(OneDriveSync.ControlloContinuo(Radice));
        Assert.False(OneDriveSyncGate.Attivo(Radice));
    }

    [Fact]
    public void ControlloContinuo_NonSciogliMaIlBloccoDellAvvio()
    {
        OneDriveSyncGate.Blocca(Radice, "bloccato all'avvio");
        Assert.False(OneDriveSync.ControlloContinuo(Radice));            // client ok, ma il blocco non e' del monitor
        Assert.True(OneDriveSyncGate.Attivo(Radice));
    }

    [Fact]
    public void ControlloContinuo_CartellaNonOneDrive_NonFaNulla()
    {
        _accesso.DentroOneDrive = false;
        _accesso.Client = false;
        _ora += TimeSpan.FromMinutes(30);
        Assert.False(OneDriveSync.ControlloContinuo(Radice));
        Assert.False(OneDriveSyncGate.Attivo(Radice));
    }
}
