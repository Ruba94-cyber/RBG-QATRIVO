using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class JsonIoAtomicWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_JsonIoAtomic_" + Guid.NewGuid().ToString("N"));

    public JsonIoAtomicWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch { }
    }

    [Fact]
    public void WriteAtomic_FileCreatoDuranteSerializzazione_NonVieneSovrascritto()
    {
        var path = Path.Combine(_dir, "race.json");
        Assert.False(File.Exists(path));

        var payload = new CreaDestinazioneDuranteSerializzazione(path);

        // WriteAtomic fotografa l'assenza del target PRIMA di serializzare. Il getter qui sotto simula un altro
        // PC/processo che crea proprio quel file mentre il payload locale è ancora in serializzazione: il rename
        // finale deve diventare create-only e fallire, mai fare last-writer-wins sul contenuto appena comparso.
        Assert.Throws<IOException>(() => JsonIo.WriteAtomic(path, payload));

        Assert.Equal("contenuto-remoto", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "race.json.tmp*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void WriteAtomic_FileGiaEsistenteAllIngresso_ConservaLaNormaleSemanticaOverwrite()
    {
        var path = Path.Combine(_dir, "existing.json");
        File.WriteAllText(path, "vecchio");

        JsonIo.WriteAtomic(path, new PayloadSemplice { Valore = "nuovo" });

        var letto = JsonIo.TryRead<PayloadSemplice>(path, out var errore);
        Assert.Null(errore);
        Assert.NotNull(letto);
        Assert.Equal("nuovo", letto!.Valore);
    }

    private sealed class CreaDestinazioneDuranteSerializzazione
    {
        private readonly string _path;

        public CreaDestinazioneDuranteSerializzazione(string path) => _path = path;

        public string Valore
        {
            get
            {
                File.WriteAllText(_path, "contenuto-remoto");
                return "contenuto-locale";
            }
        }
    }

    public sealed class PayloadSemplice
    {
        public string Valore { get; set; } = "";
    }
}
