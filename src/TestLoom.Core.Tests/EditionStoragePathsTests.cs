using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class EditionStoragePathsTests
{
    [Fact]
    public void ConfigDirectoryUsesRequestedEditionName()
    {
        var root = Path.Combine(Path.GetTempPath(), "edition-paths");
        var actual = EditionStoragePaths.ConfigDirectory(root, "InternalProduct");
        Assert.Equal(Path.Combine(root, "InternalProduct"), actual);
        Assert.Equal(Path.Combine(root, "InternalProduct", "config.json"),
            EditionStoragePaths.ConfigFile(root, "InternalProduct"));
    }

    [Fact]
    public void IndependentEditionsHaveDifferentConfigurationAndCacheLocations()
    {
        var appData = Path.Combine(Path.GetTempPath(), "edition-appdata");
        var localData = Path.Combine(Path.GetTempPath(), "edition-localdata");
        Assert.NotEqual(
            EditionStoragePaths.ConfigFile(appData, "InternalProduct"),
            EditionStoragePaths.ConfigFile(appData, "TestLoom"));
        Assert.NotEqual(
            EditionStoragePaths.WorkspaceCacheDirectory(localData, "InternalProduct"),
            EditionStoragePaths.WorkspaceCacheDirectory(localData, "TestLoom"));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("other/child")]
    [InlineData("other\\child")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not allowed")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    public void EditionFolderRejectsUnsafeNames(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => EditionStoragePaths.ConfigDirectory("root", name));
        Assert.ThrowsAny<ArgumentException>(() => EditionStoragePaths.WorkspaceCacheDirectory("root", name));
    }

    [Fact]
    public void EditionFolderAllowsPortableNames()
    {
        Assert.EndsWith("Product_1", EditionStoragePaths.ConfigDirectory("root", "Product_1"));
        Assert.EndsWith("Product-2", EditionStoragePaths.ConfigDirectory("root", "Product-2"));
    }
}
