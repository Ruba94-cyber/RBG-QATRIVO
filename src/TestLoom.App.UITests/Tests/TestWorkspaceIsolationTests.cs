using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// These checks run only with synthetic disposable test folders.
/// No production TestLoom folder is ever opened from the development machine.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Isolation")]
public sealed class TestWorkspaceIsolationTests
{
    [Fact]
    public void RefusesExistingFolderThatIsNotExplicitlyDisposable()
    {
        var realLooking = Path.Combine(Path.GetTempPath(), "ProductionLookingDITM_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(realLooking);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                TestWorkspace.AdottaCartellaEsistente("NeverTouchProduction", realLooking, "QA"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(realLooking));
        }
        finally { Directory.Delete(realLooking); }
    }

    [Fact]
    public void PermitsSyntheticFixtureOnlyInsideMarkedRoot()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "TestLoom_6_TEST",
            "Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            using var adopted = TestWorkspace.AdottaCartellaEsistente("QA_Disposable", fixture, "QA");
            Assert.Equal(fixture, adopted.WorkspaceDir);
            Assert.True(File.Exists(adopted.ConfigPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture));
        }
        finally { Directory.Delete(fixture); }
    }

    [Fact]
    public void RejectsMarkedTestFolderNestedInsideAnExistingWorkspace()
    {
        var parent = Path.Combine(Path.GetTempPath(),
            "ExistingWorkspace_" + Guid.NewGuid().ToString("N"));
        var nestedTest = Path.Combine(parent, "TestLoom_6_TEST", "Pilot");
        Directory.CreateDirectory(nestedTest);
        File.WriteAllText(Path.Combine(parent, "operatori.json"), "[]");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                TestWorkspace.AdottaCartellaEsistente("NeverProductionChild", nestedTest, "QA"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(nestedTest));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void RejectsOnlySimilarNameNotAnExactTestPathSegment()
    {
        var deceptive = Path.Combine(Path.GetTempPath(), "TestLoom_6_TEST_suffix",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(deceptive);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                TestWorkspace.AdottaCartellaEsistente("QA_Disposable", deceptive, "QA"));
        }
        finally { Directory.Delete(deceptive); }
    }
}
