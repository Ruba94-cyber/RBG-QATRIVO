using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed class NcEstaLinksTests
{
    private static NonConformita Nc(params string[] codici)
    {
        var nc = new NonConformita { Id = "NC-1" };
        foreach (var c in codici) nc.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = c, Url = "https://x/" + c });
        return nc;
    }

    private static TestCase Test(params string[] riferimenti)
    {
        var t = new TestCase { Id = "T-1" };
        foreach (var r in riferimenti) t.TicketCollegati.Add(new TicketRif { Riferimento = r, Url = "https://t/" + r });
        return t;
    }

    [Fact]
    public void NcConEstaProprioETestConAltro_MostraEntrambi_PrimaIPropri()
    {
        var link = NcEstaLinks.Effettivi(Nc("TICKET-1"), Test("TICKET-2"));

        Assert.Equal(new[] { "TICKET-1", "TICKET-2" }, link.Select(l => l.Codice));
        Assert.False(link[0].DalTest);
        Assert.True(link[1].DalTest);
        Assert.NotEmpty(link[1].Tooltip);
    }

    [Fact]
    public void NcSenzaEstaETestConEsta_ComparisceComeLinkApribile()
    {
        var link = NcEstaLinks.Effettivi(Nc(), Test("TICKET-9"));

        var unico = Assert.Single(link);
        Assert.Equal("TICKET-9", unico.Codice);
        Assert.Equal("https://t/TICKET-9", unico.Url);
        Assert.True(unico.DalTest);
    }

    [Fact]
    public void StessoEstaSuNcETest_NonSiDuplica_ENonDistingueMaiuscole()
    {
        var link = NcEstaLinks.Effettivi(Nc("ticket-5"), Test("TICKET-5", " TICKET-6 "));

        Assert.Equal(new[] { "ticket-5", "TICKET-6" }, link.Select(l => l.Codice));
        Assert.False(link[0].DalTest);
    }

    [Fact]
    public void CodiciVuotiOSoloSpazi_VengonoIgnorati()
    {
        Assert.Empty(NcEstaLinks.Effettivi(Nc("", "  "), Test(" ")));
        Assert.Equal("—", NcEstaLinks.Testo(Nc(), null));
    }

    [Fact]
    public void SenzaTestCollegato_ValgonoSoloIPropri()
    {
        Assert.Equal("A, B", NcEstaLinks.Testo(Nc("A", "B"), null));
    }

    [Fact]
    public void RigaDellaTabellaNc_UsaLaStessaRegolaPerTestoEPresenza()
    {
        var riga = new NcRiga { Nc = Nc("TICKET-1"), TestCase = Test("TICKET-2") };

        Assert.Equal("TICKET-1, TICKET-2", riga.TicketTesto);
        Assert.True(riga.HaTicketPolarionNc);
        Assert.Equal(2, riga.LinkEstaEffettivi.Count);
    }

    [Fact]
    public void RigaSenzaNessunEsta_MostraTrattinoENessunLink()
    {
        var riga = new NcRiga { Nc = Nc(), TestCase = Test() };

        Assert.Equal("—", riga.TicketTesto);
        Assert.False(riga.HaTicketPolarionNc);
    }

    [Fact]
    public void RigaDellElencoDentroIlTest_ConIlTestPassato_VedeGliEstaDelTest()
    {
        var senzaTest = new NcRiga { Nc = Nc() };
        var conTest = new NcRiga { Nc = Nc(), TestCase = Test("TICKET-7") };

        Assert.Equal("—", senzaTest.TicketTesto);
        Assert.Equal("TICKET-7", conTest.TicketTesto);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("https://ticketing.example/TICKET-1", true)]
    [InlineData("http://ticketing.example/TICKET-1", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("TICKET-55555", false)]
    public void LinkApribileSoloSeSenzaUrlOConUrlWeb(string url, bool atteso)
    {
        var nc = new NonConformita { Id = "NC-1" };
        nc.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = "TICKET-1", Url = url });

        Assert.Equal(atteso, NcEstaLinks.Effettivi(nc, null).Single().Apribile);
    }

    [Fact]
    public void LaRigaNonEInCache_UnLinkAggiuntoDopoAppareAlProssimoAccesso()
    {
        var nc = Nc("A");
        var riga = new NcRiga { Nc = nc };
        Assert.Equal("A", riga.TicketTesto);

        nc.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = "B" });

        Assert.Equal("A, B", riga.TicketTesto);
    }
}
