using TestLoom.App.Models;
using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

public class PolarionTicketLinkServiceTests
{
    [Fact]
    public void DerivaUrl_UsaHostEProgettoConfigurati()
    {
        var url = PolarionTicketLinkService.DerivaUrl(
            new PolarionApiSettings { ApiRoot = "https://ticketing.example.com/ticketing/rest/v1", ProjectId = "TICKET_Tickets" }, "TICKET-123");
        Assert.Equal("https://ticketing.example.com/ticketing/redirect/project/TICKET_Tickets/workitem?id=TICKET-123", url);
    }

    [Theory]
    [InlineData("", "TICKET_Tickets", "TICKET-1")]
    [InlineData("http://ticketing.example.com/ticketing/rest/v1", "TICKET_Tickets", "TICKET-1")]
    [InlineData("https://ticketing.example.com/ticketing/rest/v1", "", "TICKET-1")]
    [InlineData("https://ticketing.example.com/ticketing/rest/v1", "TICKET_Tickets", " ")]
    public void DerivaUrl_SenzaConfigurazioneValidaRestaVuoto(string root, string project, string codice) =>
        Assert.Equal("", PolarionTicketLinkService.DerivaUrl(new PolarionApiSettings { ApiRoot = root, ProjectId = project }, codice));

    [Fact]
    public void DerivaUrl_SenzaPercorsiRestaVuoto() => Assert.Equal("", PolarionTicketLinkService.DerivaUrl((DataPaths?)null, "TICKET-1"));
}
