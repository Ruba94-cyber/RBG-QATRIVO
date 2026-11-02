using System.Linq;
using TestLoom.App.Models;

namespace TestLoom.App.Tests;

public class TestTemplateRiferimentiTests
{
    [Fact]
    public void ChiaviRiferimenti_ContieneITreLinkEsistenti()
    {
        Assert.Equal(new[] { CampiSistema.RequisitiCollegati, CampiSistema.TestCorrelati, CampiSistema.TicketCollegati }, CampiSistema.ChiaviRiferimenti);
    }

    [Fact]
    public void TestTemplate_CampiRiferimenti_DefaultVuoto()
    {
        var t = new TestTemplate();
        Assert.Empty(t.CampiRiferimenti);
    }
}
