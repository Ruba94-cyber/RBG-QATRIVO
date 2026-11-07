using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed class TestCaseExportFiltroTests
{
    private static TestCase Crea(string id, string componente, string veicolo, string funzione)
    {
        return new TestCase
        {
            Id = id, Componente = componente, Veicolo = veicolo, Funzionalita = [funzione],
            DescrizioneBreve = "Verifica diagnostica " + id,
            Assegnatario = "Mario", CreatoDa = "QA",
            DataCreazione = new DateTime(2026, 11, 6), StatoLavoro = StatoLavoroTest.InCorso
        };
    }

    [Fact]
    public void FiltriExport_CombinanoAndFraCampiEOrFraValori_SenzaLimitarsiAllaPaginaCorrente()
    {
        var tests = Enumerable.Range(0, 300)
            .Select(i => Crea($"TEST-{i:D4}", i % 2 == 0 ? "BCM" : "ECM",
                i % 3 == 0 ? "Daily" : "Eurocargo", "Guasto")).ToList();
        var filtro = new TestCaseExportFiltro
        {
            Multi = new Dictionary<string, IReadOnlySet<string>>
            {
                ["Componente"] = new HashSet<string>(["BCM"], StringComparer.OrdinalIgnoreCase),
                ["Veicolo"] = new HashSet<string>(["Daily", "Eurocargo"], StringComparer.OrdinalIgnoreCase),
                ["Assegnatario"] = new HashSet<string>(["mario"], StringComparer.OrdinalIgnoreCase)
            },
            Dal = new DateTime(2026, 11, 1),
            Al = new DateTime(2026, 11, 30)
        };
        var risultato = filtro.Applica(tests).ToList();
        Assert.Equal(150, risultato.Count);
        Assert.Contains(risultato, t => t.Id == "TEST-0298");
        Assert.DoesNotContain(risultato, t => t.Id == "TEST-0299");
    }

    [Fact]
    public void FiltriExport_StatoAnomaliaEPolarionUsanoUIDDeiTestNonLeProiezioniDiPagina()
    {
        var a = Crea("TEST-A", "BCM", "Daily", "Guasto");
        var b = Crea("TEST-B", "BCM", "Daily", "Guasto");
        b.TicketCollegati.Add(new TicketRif { Riferimento = "TICKET-22" });
        var c = Crea("TEST-C", "BCM", "Daily", "Guasto");
        var filtro = new TestCaseExportFiltro
        {
            Multi = new Dictionary<string, IReadOnlySet<string>>
            {
                ["StatoAnomalia"] = new HashSet<string>(["DaVerificare"], StringComparer.OrdinalIgnoreCase)
            },
            UidTestConNcStato = new HashSet<Guid>([a.Uid, b.Uid]),
            UidTestConNcPolarion = new HashSet<Guid>([a.Uid]),
            Ticketing = FiltroPolarioneOpzione.ConPolarion
        };
        var risultato = filtro.Applica([a, b, c]).Select(x => x.Id).ToList();
        Assert.Equal(new[] { "TEST-A", "TEST-B" }, risultato);

        var senza = new TestCaseExportFiltro
        {
            UidTestConNcPolarion = new HashSet<Guid>([a.Uid]),
            Ticketing = FiltroPolarioneOpzione.SenzaPolarion
        };
        Assert.Equal(new[] { "TEST-C" }, senza.Applica([a, b, c]).Select(x => x.Id).ToList());
    }

    [Fact]
    public void FiltriExport_CombinaTestoRequisitiTemplateEdEcuEReimpostaConSnapshotSenzaSelezioni()
    {
        var template = Guid.NewGuid();
        var tc = Crea("TEST-TARGET", "BCM", "Daily", "Diagnosi");
        tc.TestTemplateUid = template;
        tc.RequisitoId.Add("REQ-001");
        var filtro = new TestCaseExportFiltro
        {
            Ricerca = "req-001",
            Multi = new Dictionary<string, IReadOnlySet<string>>
            {
                ["TemplateUid"] = new HashSet<string>([template.ToString()], StringComparer.OrdinalIgnoreCase),
                ["Funzionalita"] = new HashSet<string>(["diagnosi"], StringComparer.OrdinalIgnoreCase),
                ["Componente"] = new HashSet<string>(["BCM"], StringComparer.OrdinalIgnoreCase)
            }
        };
        Assert.True(filtro.Corrisponde(tc));
        Assert.True(new TestCaseExportFiltro().Corrisponde(tc));
        tc.TestTemplateUid = Guid.NewGuid();
        Assert.False(filtro.Corrisponde(tc));
    }

    [Fact]
    public void FiltriExport_AnnullamentoInterrompeEnumerazione()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new TestCaseExportFiltro().Applica([Crea("TEST-1", "BCM", "Daily", "Guasto")], cts.Token).ToList());
    }
}
