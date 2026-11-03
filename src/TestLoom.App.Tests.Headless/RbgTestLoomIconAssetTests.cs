using System.Buffers.Binary;

namespace TestLoom.App.Tests;

/// <summary>
/// The Windows .ico must preserve the approved truck/checklist/traceability identity
/// across shell, taskbar and high-DPI sizes. Static gate: no Windows runner required.
/// </summary>
public sealed class RbgDitmIconAssetTests
{
    private static string IconPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "TestLoom.App", "TestLoom.App.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "TestLoom.App", "Assets", "rbg-qatrivo.ico");
    }

    [Fact]
    public void IconaContieneLeDimensioniWindowsEDpiAlti()
    {
        var ico = File.ReadAllBytes(IconPath());
        Assert.True(ico.Length > 1000, "Windows icon is empty or truncated.");
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2, 2)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4, 2));
        var frames = new HashSet<int>();
        byte[]? png256 = null;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            Assert.True(entry + 16 <= ico.Length);
            var size = ico[entry] == 0 ? 256 : ico[entry];
            var height = ico[entry + 1] == 0 ? 256 : ico[entry + 1];
            Assert.Equal(size, height);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(entry + 8, 4));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(entry + 12, 4));
            Assert.True((long)offset + length <= ico.Length, $"{size}px icon frame extends outside file.");
            Assert.True(length > 50);
            var png = ico.AsSpan(checked((int)offset), checked((int)length));
            Assert.True(png.Length >= 24 && png[0] == 137 && png[1] == 80 &&
                png[2] == 78 && png[3] == 71, $"{size}px frame is not PNG.");
            Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)));
            Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)));
            if (size == 256) png256 = png.ToArray();
            frames.Add(size);
        }
        foreach (var size in new[] { 16, 24, 32, 48, 64, 96, 128, 256 })
            Assert.Contains(size, frames);
        var asset256 = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(IconPath())!, "rbg-qatrivo-icon-256.png"));
        Assert.Equal(png256, asset256); // PNG external asset must be exactly the ICO 256px frame.
    }
}