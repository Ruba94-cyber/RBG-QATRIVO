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

    private static readonly PolarionApiSettings Configurato =
        new() { ApiRoot = "https://ticketing.example.com/ticketing/rest/v1", ProjectId = "TICKET_Tickets" };

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("non-un-url")]
    [InlineData("https://vecchio.example.com/altro?id=TICKET-999")]
    public void RisolviUrl_CostruisceSempreDalNumeroDelTicket_IndipendenteDall_UrlSalvato(string? salvato) =>
        Assert.Equal("https://ticketing.example.com/ticketing/redirect/project/TICKET_Tickets/workitem?id=TICKET-123",
            PolarionTicketLinkService.RisolviUrl(Configurato, "TICKET-123", salvato));

    [Fact]
    public void RisolviUrl_HostNonConfigurato_RipieganSulUrlSalvatoSeWebValido() =>
        Assert.Equal("https://p.example.com/x?id=TICKET-1",
            PolarionTicketLinkService.RisolviUrl(new PolarionApiSettings(), "TICKET-1", " https://p.example.com/x?id=TICKET-1 "));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ftp://p.example.com/x")]
    public void RisolviUrl_HostNonConfiguratoESenzaUrlSalvatoValido_RestaVuoto(string? salvato) =>
        Assert.Equal("", PolarionTicketLinkService.RisolviUrl(new PolarionApiSettings(), "TICKET-1", salvato));

    [Fact]
    public void RisolviUrl_CodiceVuoto_RestaVuoto() =>
        Assert.Equal("", PolarionTicketLinkService.RisolviUrl(Configurato, " ", null));

    [Theory]
    [InlineData("TICKET-12345", "TICKET-12345")]
    [InlineData("  ticket-12345	", "TICKET-12345")]
    [InlineData("TICKET 12345", "TICKET-12345")]
    [InlineData("TICKET12345", "TICKET-12345")]
    [InlineData("esta_12345", "TICKET-12345")]
    [InlineData("TICKET–12345", "TICKET-12345")]
    [InlineData("TICKET—12345", "TICKET-12345")]
    [InlineData("TICKET−12345", "TICKET-12345")]
    [InlineData("ＥＳＴＡ-12345", "TICKET-12345")]
    [InlineData("TICKET-​12345", "TICKET-12345")]
    [InlineData("TICKET-12345 ", "TICKET-12345")]
    [InlineData("#TICKET-12345", "TICKET-12345")]
    [InlineData("[TICKET-12345]", "TICKET-12345")]
    [InlineData("(TICKET-12345).", "TICKET-12345")]
    [InlineData("TICKET-12345 - Titolo incollato", "TICKET-12345")]
    [InlineData("TICKET-12345: titolo", "TICKET-12345")]
    [InlineData("https://p.example.com/ticketing/redirect/project/TICKET_Tickets/workitem?id=TICKET-12345", "TICKET-12345")]
    [InlineData("TICKET-DEMO-4100", "TICKET-DEMO-4100")]
    public void TryNormalizzaCodice_AccettaIFormatiRealiDigitatiOIncollati(string input, string atteso)
    {
        Assert.True(PolarionTicketLinkService.TryNormalizzaCodice(input, out var codice));
        Assert.Equal(atteso, codice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XYZ-12")]
    [InlineData("12345")]
    [InlineData("TICKET")]
    [InlineData("TICKET-")]
    [InlineData("non-ticket")]
    [InlineData("ESTAX-123")]
    [InlineData("TICKET Titolo senza numero")]
    public void TryNormalizzaCodice_RifiutaCio_CheNonEUnTicketEsta(string input) =>
        Assert.False(PolarionTicketLinkService.TryNormalizzaCodice(input, out _));
}
