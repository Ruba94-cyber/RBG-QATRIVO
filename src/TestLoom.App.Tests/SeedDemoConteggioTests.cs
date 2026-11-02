using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Il dataset dimostrativo (--seed-demo) deve contare 170 Test anche dopo l'aggiunta del tipo RAS UnitLogger: il tipo è un
/// template in più, non un Test in più.</summary>
public sealed class SeedDemoConteggioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SeedDemo_Conteggio_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
