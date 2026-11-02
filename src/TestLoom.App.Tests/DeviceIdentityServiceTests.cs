using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class DeviceIdentityServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_DeviceIdentity_" + Guid.NewGuid().ToString("N"));

    public DeviceIdentityServiceTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void DeviceIdCorrotto_VieneRiparatoEPersistitoPrimaDellUso()
    {
        var path = Path.Combine(_dir, "device-id.txt");
        File.WriteAllText(path, "non-un-guid");

        var first = DeviceIdentityService.GetOrCreateInDirectory(_dir);
        var second = DeviceIdentityService.GetOrCreateInDirectory(_dir);

        Assert.True(Guid.TryParse(first, out var guid));
        Assert.NotEqual(Guid.Empty, guid);
        Assert.Equal(first, second);
        Assert.Equal(first, Guid.Parse(File.ReadAllText(path).Trim()).ToString("N"));
        Assert.True(File.Exists(Path.Combine(_dir, "device-id.lock")));
    }

    [Fact]
    public async Task RiparazioneConcorrente_ConvergeSuUnSoloGuidPersistente()
    {
        var path = Path.Combine(_dir, "device-id.txt");
        File.WriteAllText(path, "corrotto");

        var tasks = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => DeviceIdentityService.GetOrCreateInDirectory(_dir)))
            .ToArray();
        var values = await Task.WhenAll(tasks);

        var winner = Assert.Single(values.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.True(Guid.TryParse(winner, out var guid));
        Assert.NotEqual(Guid.Empty, guid);
        Assert.Equal(winner, Guid.Parse(File.ReadAllText(path).Trim()).ToString("N"));
        Assert.Equal(winner, DeviceIdentityService.GetOrCreateInDirectory(_dir));
        Assert.Empty(Directory.GetFiles(_dir, "device-id.txt.*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void DeviceIdValidoEsistente_NonVieneRiscritto()
    {
        var path = Path.Combine(_dir, "device-id.txt");
        var guid = Guid.NewGuid();
        File.WriteAllText(path, guid.ToString("D"));
        var before = File.GetLastWriteTimeUtc(path);

        var resolved = DeviceIdentityService.GetOrCreateInDirectory(_dir);

        Assert.Equal(guid.ToString("N"), resolved);
        Assert.Equal(guid.ToString("D"), File.ReadAllText(path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
