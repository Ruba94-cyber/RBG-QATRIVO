using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>Punto 1 (feedback 2026-10): la Home conta il lavoro reale, cioe' le operazioni eseguite per componente, non le REV.</summary>
public sealed class ConteggioLavoroPerOperatoreTests
{
    private static Condizione Op(Esito esito, params string[] componente) => new() { Esito = esito, Componente = new EcuSelection(componente) };

    private static TestCase Test(params Esecuzione[] revisioni)
    {
        var tc = new TestCase { Id = "T", Uid = Guid.NewGuid(), Componente = new EcuSelection(new[] { "BCM" }) };
        tc.Esecuzioni.AddRange(revisioni);
        return tc;
    }

    [Fact]
    public void DieciOperazioniNellaStessaRev_ValgonoDieciEsecuzioni()
    {
        var rev = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = Enumerable.Range(0, 10).Select(_ => Op(Esito.Conforme, "BCM")).ToList() };
        Assert.Equal(10, HomeViewModel.ContaEsecuzioniPerOperatore([Test(rev)])["Mario"]);
    }

    [Fact]
    public void CelleEreditateDaEseguire_NonContano_RetestUgualeSi()
    {
        var rev1 = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM"), Op(Esito.Conforme, "BCM")] };
        // Nuova REV: le condizioni clonate partono Da eseguire; una sola viene rieseguita con lo STESSO esito.
        var rev2 = new Esecuzione { Iterazione = 2, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM"), Op(Esito.DaEseguire, "BCM")] };
        Assert.Equal(3, HomeViewModel.ContaEsecuzioniPerOperatore([Test(rev1, rev2)])["Mario"]);
    }

    [Fact]
    public void OperazioneSuDueEcu_ValeDue_NaENonTestataZero_ReteEsclusa()
    {
        var rev = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM", "VMU"), Op(Esito.NonApplicabile, "BCM"), Op(Esito.DaEseguire, "BCM")] };
        var rete = new Esecuzione { Iterazione = 2, Operatore = "Mario", Origine = OrigineRevisione.Rete, Condizioni = [Op(Esito.Conforme, "BCM")] };
        Assert.Equal(2, HomeViewModel.ContaEsecuzioniPerOperatore([Test(rev, rete)])["Mario"]);
    }

    [Fact]
    public void OperazioneSenzaEcuRisolvibile_ContaComunqueUno()
    {
        var tc = new TestCase { Id = "T", Uid = Guid.NewGuid() };
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme)] });
        Assert.Equal(1, HomeViewModel.ContaEsecuzioniPerOperatore([tc])["Mario"]);
    }
}
