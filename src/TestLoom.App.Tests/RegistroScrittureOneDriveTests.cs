using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

public sealed class RegistroScrittureOneDriveTests
{
    private sealed class StoreMemoria : IOneDrivePendenteStore
    {
        public OneDrivePendente? Valore;
        public int Salvataggi;
        public OneDrivePendente? Leggi(string radice) => Valore;
        public void Salva(string radice, OneDrivePendente pendente) { Valore = pendente; Salvataggi++; }
        public void Cancella(string radice) => Valore = null;
    }

    private static readonly string Radice = Path.Combine(Path.GetTempPath(), "ditm_registro_radice");

    private static RegistroScrittureOneDrive Nuovo(StoreMemoria store, int massimo = 200) =>
        new(Radice, store, massimo, TimeSpan.FromMinutes(10));

    [Fact]
    public void ScrittureSottoLaRadice_VengonoRegistrateRelativeEDeduplicate()
    {
        var store = new StoreMemoria();
        using var registro = Nuovo(store);
        registro.Registra(Path.Combine(Radice, "test", "T-1.json"));
        registro.Registra(Path.Combine(Radice, "test", "T-1.json"));
        registro.Registra(Path.Combine(Radice, "nc", "NC-1.json"));
        registro.Svuota();

        Assert.NotNull(store.Valore);
        Assert.Equal(2, store.Valore!.Percorsi.Count);
        Assert.Contains(Path.Combine("test", "T-1.json"), store.Valore.Percorsi);
    }

    [Fact]
    public void FuoriDallaRadiceETemporanei_VengonoIgnorati()
    {
        var store = new StoreMemoria();
        using var registro = Nuovo(store);
        registro.Registra(Path.Combine(Path.GetTempPath(), "altra", "x.json"));
        registro.Registra(Path.Combine(Radice, "test", "T-1.json.tmp1a2b3c4d"));
        registro.Registra(Path.Combine(Radice, "test", "T-1.json.tmp"));
        registro.Registra(Path.Combine(Radice, ".testloom-write-probe-1"));
        registro.Registra(Path.Combine(Radice, "~$lock.docx"));
        registro.Svuota();

        Assert.Null(store.Valore);
        Assert.Equal(0, store.Salvataggi);
    }

    [Fact]
    public void SvuotaUnisceAiPercorsiGiaRegistratiEMantieneIPiuRecentiOltreIlTetto()
    {
        var store = new StoreMemoria { Valore = new OneDrivePendente { Percorsi = new List<string> { "a.json", "b.json" }, NumeroFile = 2 } };
        using var registro = Nuovo(store, massimo: 3);
        registro.Registra(Path.Combine(Radice, "c.json"));
        registro.Registra(Path.Combine(Radice, "d.json"));
        registro.Svuota();

        Assert.Equal(new[] { "b.json", "c.json", "d.json" }, store.Valore!.Percorsi);
        Assert.Equal(4, store.Valore.NumeroFile);
    }

    [Fact]
    public void DisposeScriveLeVociInCodaESuccessiveRegistrazioniSonoIgnorate()
    {
        var store = new StoreMemoria();
        var registro = Nuovo(store);
        registro.Registra(Path.Combine(Radice, "a.json"));
        registro.Dispose();
        registro.Registra(Path.Combine(Radice, "b.json"));
        registro.Svuota();

        Assert.Single(store.Valore!.Percorsi);
    }

    [Fact]
    public void ErroreDelloStore_NonSollevaEccezioni()
    {
        var store = new StoreMemoriaChePiega();
        using var registro = new RegistroScrittureOneDrive(Radice, store, 200, TimeSpan.FromMinutes(10));
        registro.Registra(Path.Combine(Radice, "a.json"));
        registro.Svuota();
    }

    private sealed class StoreMemoriaChePiega : IOneDrivePendenteStore
    {
        public OneDrivePendente? Leggi(string radice) => throw new IOException("disco pieno");
        public void Salva(string radice, OneDrivePendente pendente) => throw new IOException("disco pieno");
        public void Cancella(string radice) { }
    }
}
