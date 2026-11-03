using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

public class PolarionTicketTitleTests
{
    [Fact]
    public void BuildsFiveBracketedPartsInFixedOrder()
    {
        var title = PolarionTicketTitle.Build(new[] { "Daily" }, new[] { "My22" }, new[] { "MD1-CE101" },
            new[] { "EGR valve test" }, "Errore al termine");
        Assert.Equal("[Daily] [My22] [MD1-CE101] [EGR valve test] [Errore al termine]", title);
    }

    [Fact]
    public void OmitsEmptyGroupsJoinsMultipleValuesAndNeutralizesBrackets()
    {
        var title = PolarionTicketTitle.Build(new[] { "", "" }, new[] { "My22", "My24", "my22" },
            new string[0], new[] { "A [x]" }, " breve ");
        Assert.Equal("[My22/My24] [A (x)] [breve]", title);
    }

    [Fact]
    public void NeverExceedsPolarionTitleLimitAndKeepsClosingBracket()
    {
        var title = PolarionTicketTitle.Build(new[] { "G" }, new[] { "V" }, new[] { "E" }, new[] { "F" },
            new string('x', 600));
        Assert.True(title.Length <= 512);
        Assert.StartsWith("[G] [V] [E] [F] [x", title);
        Assert.EndsWith("]", title);
    }

    [Theory]
    [InlineData("BCM - Body Control Module", "BCM")]
    [InlineData("MD1-CE101 (Motore diesel)", "MD1-CE101")]
    [InlineData("EDC17", "EDC17")]
    [InlineData("  ", "")]
    public void EcuInTitleIsOnlyTheAcronym(string name, string expected) =>
        Assert.Equal(expected, PolarionTicketTitle.SiglaEcu(name));

    [Fact]
    public void TitleUsesEcuAcronymNotFullName()
    {
        var title = PolarionTicketTitle.Build(new[] { "G" }, new[] { "V" },
            new[] { "BCM - Body Control Module", "ABS Sistema frenante" }, new[] { "F" }, "d");
        Assert.Equal("[G] [V] [BCM/ABS] [F] [d]", title);
    }

    [Fact]
    public void InstrumentIsTheFirstGroupOfTheTitle()
    {
        var title = PolarionTicketTitle.Build(new[] { "UnitLogger" }, new[] { "Daily" }, new[] { "My22" },
            new[] { "BCM - Body" }, new[] { "F" }, "d");
        Assert.Equal("[UnitLogger] [Daily] [My22] [BCM] [F] [d]", title);
    }
}
