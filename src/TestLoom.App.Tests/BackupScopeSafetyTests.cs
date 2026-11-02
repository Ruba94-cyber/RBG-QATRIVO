using System.IO.Compression;
using System.Reflection;
using System.Threading;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

[Collection("BackupWriteGate")] // BackupService.WriteGate è statico: niente esecuzione parallela fra queste classi
public sealed class BackupScopeSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_BackupScope_" + Guid.NewGuid().ToString("N"));
    // Radice locale di test (mai il vero %LOCALAPPDATA%): stesso meccanismo di override già usato da
    // LocalTestIndexStore per i suoi test, passato qui direttamente a BackupService.CreaOra.
    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "TestLoom_BackupScope_LocalAppData_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Backup_IncludeJsonDitmEdEscludeJsonEstraneoAllaRootCondivisa()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();

        File.WriteAllText(paths.OperatoriFile, "[]");
        var foreign = Path.Combine(_root, "third-party.json");
        File.WriteAllText(foreign, "{\"secret\":true}");

        var recovery = Path.Combine(paths.RecoveryBinDir, "recovery_test.json");
        File.WriteAllText(recovery, "{}");

        var zipPath = BackupService.CreaOra(paths, new DateTime(2026, 9, 5, 12, 34, 56), _localAppData);

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("operatori.json", entries);
        Assert.Contains("recovery-bin/recovery_test.json", entries);
        Assert.DoesNotContain("third-party.json", entries);
        Assert.Contains("snapshot-manifest.txt", entries);
    }

    [Fact]
    public void Backup_LayoutTestGerarchicoIncluso_SottoAlberiTerziNonScansionati()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var nested = Path.Combine(paths.TestCasesDir, "Gamma", "Sottoveicolo", "Componente");
        Directory.CreateDirectory(nested);
        var tc = Path.Combine(nested, "testcase_TC-HIERARCHICAL.json");
        File.WriteAllText(tc, "{\"Id\":\"TC-HIERARCHICAL\"}");

        var foreignRoot = Path.Combine(_root, "ThirdPartyApplication", "testcases");
        Directory.CreateDirectory(foreignRoot);
        File.WriteAllText(Path.Combine(foreignRoot, "testcase_SAME_PREFIX.json"), "{\"Private\":true}");

        var zipPath = BackupService.CreaOra(paths,
            new DateTime(2026, 10, 10, 12, 0, 0), _localAppData);
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("testcases/Gamma/Sottoveicolo/componente/testcase_TC-HIERARCHICAL.json", names);
        Assert.DoesNotContain(names, name => name.Contains("ThirdPartyApplication",
            StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(foreignRoot, "testcase_SAME_PREFIX.json")));
    }

    [Fact]
    public void BackupSnapshot_ContenutoDiversoConStessaLunghezzaEMtime_NonRisultaUguale()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var stamp = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);

        // Due JSON diversi ma della stessa lunghezza. Il timestamp viene deliberatamente riportato allo stesso
        // valore per simulare una sostituzione/sincronizzazione che i soli metadati non saprebbero distinguere.
        File.WriteAllText(paths.OperatoriFile, "{\"a\":1}");
        File.SetLastWriteTimeUtc(paths.OperatoriFile, stamp);

        var capture = typeof(BackupService).GetMethod("CaptureSourceSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
        var same = typeof(BackupService).GetMethod("SameSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(capture);
        Assert.NotNull(same);
        var before = capture!.Invoke(null, [paths, CancellationToken.None]);
        Assert.NotNull(before);
        var length = new FileInfo(paths.OperatoriFile).Length;

        File.WriteAllText(paths.OperatoriFile, "{\"b\":2}");
        File.SetLastWriteTimeUtc(paths.OperatoriFile, stamp);
        Assert.Equal(length, new FileInfo(paths.OperatoriFile).Length);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(paths.OperatoriFile));

        var after = capture.Invoke(null, [paths, CancellationToken.None]);
        Assert.NotNull(after);
        var identical = (bool)same!.Invoke(null, [before!, after!])!;

        Assert.False(identical);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        try { if (Directory.Exists(_localAppData)) Directory.Delete(_localAppData, recursive: true); } catch { }
    }
}
