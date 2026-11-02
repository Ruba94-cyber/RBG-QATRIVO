using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class ReportCronologiaServiceTests
{
    [Fact]
    public void Aggiungi_InserisceInTesta()
    {
        var cronologia = new List<ReportSalvato> { new() { Percorso = "vecchio.xlsx", Data = new DateTime(2026, 1, 1) } };

        var risultato = ReportCronologiaService.Aggiungi(cronologia, "nuovo.xlsx", new DateTime(2026, 6, 1));

        Assert.Equal("nuovo.xlsx", risultato[0].Percorso);
        Assert.Equal("vecchio.xlsx", risultato[1].Percorso);
    }

    [Fact]
    public void Aggiungi_LimitaA50Voci()
    {
        var cronologia = Enumerable.Range(1, 50).Select(i => new ReportSalvato { Percorso = $"file{i}.xlsx", Data = DateTime.Now }).ToList();

        var risultato = ReportCronologiaService.Aggiungi(cronologia, "nuovo.xlsx", DateTime.Now);

        Assert.Equal(50, risultato.Count);
        Assert.Equal("nuovo.xlsx", risultato[0].Percorso);
        Assert.DoesNotContain(risultato, r => r.Percorso == "file50.xlsx");
    }
}
