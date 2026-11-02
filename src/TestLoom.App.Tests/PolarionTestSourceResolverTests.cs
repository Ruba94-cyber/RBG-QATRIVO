using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionTestSourceResolverTests
{
    [Fact]
    public void FindsNcByImmutableTestUidAndCrByValidationTestId()
    {
        var test = new TestCase { Id = "TEST-77" };
        var nc = new NonConformita { TestUid = test.Uid, TestCaseId = "OLD-RENAME", Id = "NC-1" };
        var cr = new ChangeRequest
        {
            Id = "CR-1", TestValidazione = [new CollegamentoValidazioneCr { TestId = "TEST-77" }]
        };
        var sources = PolarionTestSourceResolver.Find(test, [nc], [cr]);
        Assert.Equal(2, sources.Count);
        Assert.Equal("NC-1", sources[0].Id);
        Assert.Equal("CR-1", sources[1].Id);
    }

    [Fact]
    public void DoesNotMisinterpretUnrelatedRequirementOrTestAsEstaSource()
    {
        var test = new TestCase { Id = "TEST-88" };
        var other = new NonConformita { Id = "NC-9", TestCaseId = "TEST-888" };
        Assert.Empty(PolarionTestSourceResolver.Find(test, [other], []));
    }


    [Fact]
    public void RequirementOnlyTestDoesNotCreateAnEstaSource()
    {
        var test = new TestCase { Id = "TEST-REQ", RequisitoId = ["POL-REQ-112"] };

        Assert.Empty(PolarionTestSourceResolver.Find(test, [], []));
    }

    [Fact]
    public void ArchivedChangeRequestIsNotOfferedAsCreationSource()
    {
        var test = new TestCase { Id = "TEST-ARCHIVE" };
        var cr = new ChangeRequest
        {
            Id = "CR-ARCHIVED", Archiviata = true,
            TestValidazione = [new CollegamentoValidazioneCr { TestId = test.Id }]
        };

        Assert.Empty(PolarionTestSourceResolver.Find(test, [], [cr]));
    }

    [Fact]
    public void DoesNotUseOldTestIdWhenImmutableUidContradicts()
    {
        var test = new TestCase { Id = "TEST-10" };
        var other = new NonConformita { Id = "NC-1", TestUid = Guid.NewGuid(), TestCaseId = test.Id };
        Assert.Empty(PolarionTestSourceResolver.Find(test, [other], []));
    }
}
