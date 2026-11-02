using System.Text.RegularExpressions;

namespace TestLoom.App.Tests;

public sealed class ValidationRunnerSourceTests
{
    [Fact]
    public void PilotTransactionOutcomeTrace_RestaBestEffortEUsaCallbackFinali()
    {
        var root = FindRepositoryRoot();
        var servicePath = Path.Combine(root, "src", "TestLoom.App", "Services", "PilotTransactionOutcomeTraceService.cs");
        var jsonIoPath = Path.Combine(root, "src", "TestLoom.App", "Services", "JsonIo.cs");
        Assert.True(File.Exists(servicePath), $"Servizio trace transazionale non trovato: {servicePath}");
        Assert.True(File.Exists(jsonIoPath), $"JsonIo non trovato: {jsonIoPath}");

        var service = File.ReadAllText(servicePath);
        var jsonIo = File.ReadAllText(jsonIoPath);
        Assert.True(service.Contains("DeferAfterCommit", StringComparison.Ordinal));
        Assert.True(service.Contains("DeferAfterRollback", StringComparison.Ordinal));
        Assert.True(service.Contains("outcome: \"committed\"", StringComparison.Ordinal));
        Assert.True(service.Contains("outcome: \"rolled-back\"", StringComparison.Ordinal));
        Assert.True(service.Contains("catch (Exception ex)", StringComparison.Ordinal));
        Assert.True(service.Contains("Non usare mai il trace come motivo per far fallire", StringComparison.Ordinal));
        Assert.True(jsonIo.Contains("PilotTransactionOutcomeTraceService.RegisterForSuccessfulWrite", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Impossibile risalire alla root del repository TestLoom dal test host.");
    }
}
