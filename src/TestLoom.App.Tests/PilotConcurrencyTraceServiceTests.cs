using System.Security.Cryptography;
using System.Text;
using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

public sealed class JsonIoAtomicWriteTests_PilotConcurrencyTrace
{
    [Fact]
    public void Trace_RestaLocaleEConservaSoloMetadatiTecnici()
    {
        var root = NewTemp("TestLoom_6_TEST-trace-workspace");
        var local = NewTemp("TestLoom-local-trace");
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "testcases", "ECM", "testcase_T1.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{\"Id\":\"T1\"}");

            PilotConcurrencyTraceService.WriteEventForTesting(root, new PilotConcurrencyTraceEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Operation = "json-write",
                Stage = "atomic-replace",
                Outcome = "succeeded",
                RelativePath = "testcases/ECM/testcase_T1.json",
                TransactionId = Guid.NewGuid().ToString("N"),
                ObservedBeforeSha256 = Sha("before"),
                CandidateAfterSha256 = Sha("after"),
                ObservedAfterSha256 = Sha("after"),
                Detail = "exception=none"
            }, local);

            var traceDir = PilotConcurrencyTraceService.WorkspaceDirectoryForTesting(root, local);
            var tracePath = Path.Combine(traceDir, "concurrency.ndjson");
            Assert.True(File.Exists(tracePath));
            Assert.False(File.Exists(Path.Combine(root, "concurrency.ndjson")));
            Assert.False(Directory.Exists(Path.Combine(root, "pilot-trace")));

            var raw = File.ReadAllText(tracePath);
            Assert.True(raw.Contains("testcases/ECM/testcase_T1.json", StringComparison.Ordinal));
            Assert.False(raw.Contains("{\"Id\":\"T1\"}", StringComparison.Ordinal));

            var events = PilotConcurrencyTraceService.ReadRecentForTesting(root, 10, local);
            var evt = Assert.Single(events);
            Assert.Equal("json-write", evt.Operation);
            Assert.Equal("succeeded", evt.Outcome);
            Assert.Equal(Sha("before"), evt.ObservedBeforeSha256);
            Assert.Equal(Sha("after"), evt.ObservedAfterSha256);
        }
        finally
        {
            TryDelete(root);
            TryDelete(local);
        }
    }

    [Fact]
    [Trait("Category", "Lento")]
    public void Trace_RuotaERimaneBounded()
    {
        var root = NewTemp("TestLoom_6_TEST-trace-bounded");
        var local = NewTemp("TestLoom-local-trace-bounded");
        try
        {
            Directory.CreateDirectory(root);
            var largeDetail = new string('x', 600); // viene limitato dal servizio, ma genera comunque righe realistiche.
            // 5.000 righe sono sufficienti a superare 2 MiB con questo payload tecnico e mantengono il test
            // sensibilmente più leggero rispetto a una stress suite da 9.000 write-through.
            for (var i = 0; i < 5000; i++)
            {
                PilotConcurrencyTraceService.WriteEventForTesting(root, new PilotConcurrencyTraceEvent
                {
                    TimestampUtc = DateTime.UtcNow.AddTicks(i),
                    Operation = "race-matrix",
                    Stage = "iteration",
                    Outcome = "observed",
                    RelativePath = $"testcases/ECM/testcase_{i:D6}.json",
                    TransactionId = Guid.NewGuid().ToString("N"),
                    ObservedBeforeSha256 = Sha("a" + i),
                    CandidateAfterSha256 = Sha("b" + i),
                    ObservedAfterSha256 = Sha("b" + i),
                    Detail = largeDetail
                }, local);
            }

            var traceDir = PilotConcurrencyTraceService.WorkspaceDirectoryForTesting(root, local);
            var files = Directory.GetFiles(traceDir, "concurrency*.ndjson", SearchOption.TopDirectoryOnly);
            Assert.InRange(files.Length, 2, 4); // almeno una rotazione deve essere realmente avvenuta.
            Assert.All(files, file => Assert.True(new FileInfo(file).Length <= 2_300_000));
            Assert.True(PilotConcurrencyTraceService.ReadRecentForTesting(root, 25, local).Count <= 25);
        }
        finally
        {
            TryDelete(root);
            TryDelete(local);
        }
    }

    [Fact]
    public void TryHashFile_DistingueContenutoConStessaLunghezzaEStessoTimestamp()
    {
        var dir = NewTemp("TestLoom_6_TEST-trace-hash");
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "operatori.json");
            var stamp = new DateTime(2026, 9, 5, 5, 0, 0, DateTimeKind.Utc);
            File.WriteAllText(path, "AAAA");
            File.SetLastWriteTimeUtc(path, stamp);
            var first = PilotConcurrencyTraceService.TryHashFile(path);

            File.WriteAllText(path, "BBBB");
            File.SetLastWriteTimeUtc(path, stamp);
            var second = PilotConcurrencyTraceService.TryHashFile(path);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first, second);
            Assert.Equal(4, new FileInfo(path).Length);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        }
        finally { TryDelete(dir); }
    }

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string NewTemp(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
