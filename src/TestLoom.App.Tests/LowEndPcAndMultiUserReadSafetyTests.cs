using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class LowEndPcAndMultiUserReadSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_LowEndPcSafety_" + Guid.NewGuid().ToString("N"));

    public LowEndPcAndMultiUserReadSafetyTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, true); } catch { }
        }
    }

    [Fact]
    public void JsonIo_ConcurrentWriterHoldingShareReadWrite_IsReadableWithoutSharingViolation()
    {
        var testPath = Path.Combine(_dir, "test-concurrent.json");
        var sample = new TestCase { Id = "T-9999", Descrizione = "Test Concurrent Reader" };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(sample, JsonIo.Options);

        // Scriviamo il file inizialmente
        File.WriteAllBytes(testPath, jsonBytes);

        // Simuliamo un altro PC / sync client di OneDrive che tiene aperto il file in scrittura con FileShare.ReadWrite
        using (var writerStream = new FileStream(testPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            // TestLoom tenta di leggere il file mentre il writer è attivo
            var read = JsonIo.TryRead<TestCase>(testPath, out var error);

            Assert.NotNull(read);
            Assert.Equal("T-9999", read.Id);
            Assert.Null(error);
        }
    }

    [Fact]
    public void FileRepairService_TrailingComma_RepairsSuccessfully()
    {
        var testPath = Path.Combine(_dir, "testcases", "Componente-TEST", "testcase_T-0001.json");
        Directory.CreateDirectory(Path.GetDirectoryName(testPath)!);

        // Creiamo la struttura minima di workspace affinché sia riconosciuto come TestLoom
        File.WriteAllText(Path.Combine(_dir, "version-manifest.json"), "{\"manifestSchema\":1}");
        Directory.CreateDirectory(Path.Combine(_dir, "_ditm"));

        // File con virgola finale (errore tipico di merge/modifica concorrente manuale)
        File.WriteAllText(testPath, "{\n  \"Id\": \"T-0001\",\n  \"Descrizione\": \"Test Case con difetto\",\n}");

        // ProvaRipara deve rimuovere la virgola finale e riparare il file
        var res = FileRepairService.ProvaRipara(testPath);

        Assert.True(res.Riuscita, $"Riparazione fallita: {res.Messaggio}");
        var riparato = JsonIo.TryRead<TestCase>(testPath, out var err);
        Assert.NotNull(riparato);
        Assert.Equal("T-0001", riparato.Id);
    }

    [Fact]
    public void LowEndPcSimulation_PagingAndFilterLatency_Under15ms()
    {
        // Simulazione di una macchina a basse risorse con 5.000 elementi filtrati
        var items = new List<TestCase>();
        for (int i = 0; i < 5000; i++)
        {
            items.Add(new TestCase
            {
                Id = $"T-{i:D5}",
                Descrizione = $"Test case number {i}",
                Veicolo = i % 2 == 0 ? "Daily" : "Eurocargo",
                Componente = i % 3 == 0 ? "BCM" : "EDC"
            });
        }

        var filter = new DashboardFilter
        {
            Veicolo = new List<string> { "Daily" },
            Componente = new List<string> { "BCM" }
        };

        // Misura tempo di applicazione filtro
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var filtrati = filter.Applica(items, _ => null).ToList();
        sw.Stop();

        Assert.NotEmpty(filtrati);
        // Su qualunque CPU deve completare in meno di 15 millisecondi
        Assert.True(sw.ElapsedMilliseconds < 15, $"Filtro troppo lento su hardware debole: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void LanguageSwitch_ResponsiveAndInstant()
    {
        var loc = LocalizationService.Instance;
        var linguaIniziale = loc.Lingua;

        try
        {
            loc.Lingua = "it";
            Assert.False(loc.EInglese);
            var itTitolo = loc.Traduci("FileConProblemiTitolo");
            Assert.Equal("File con problemi", itTitolo);

            loc.Lingua = "en";
            Assert.True(loc.EInglese);
            var enTitolo = loc.Traduci("FileConProblemiTitolo");
            Assert.Equal("Files with problems", enTitolo);
        }
        finally
        {
            loc.Lingua = linguaIniziale;
        }
    }
}
