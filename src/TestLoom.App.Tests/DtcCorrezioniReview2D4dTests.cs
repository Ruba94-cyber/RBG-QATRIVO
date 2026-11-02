using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Extra senza veicoli dichiarati: coerente con RequisitoApplicabilita.SiApplicaAlSottoveicolo (lista vuota = tutti i veicoli).</summary>
public sealed class DtcCorrezioniReview2D4dTests
{
    [Fact]
    public void D4dSenzaVeicoliDichiarati_AccettaQualunqueVeicoloDellAnagrafica_ENonSegnalaNonAllineata()
    {
        var anagrafica = new[] { "A", "B" };
        var nessuno = Array.Empty<string>();
        var chiavi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Null(DtcAlberoRegole.ValidaNuovaVoce("a", "ECM", "P0001", nessuno, null, chiavi, anagrafica));
        Assert.Equal("DtcVeicoloNonDichiarato", DtcAlberoRegole.ValidaNuovaVoce("Z", "ECM", "P0001", nessuno, null, chiavi, anagrafica));
        Assert.Null(DtcAlberoRegole.ValidaNodo("B", "ECM", nessuno, null, anagrafica));
        Assert.Equal("DtcVeicoloNonDichiarato", DtcAlberoRegole.ValidaNodo("Z", "ECM", nessuno, null, anagrafica));
        var blocco = DtcAlberoRegole.ValidaBlocco("A", "ECM", "P1 P2", nessuno, null, chiavi, anagrafica);
        Assert.Null(blocco.ErroreNodo);
        Assert.Equal(2, blocco.Nuove.Count);
        Assert.Equal("DtcVeicoloNonDichiarato", DtcAlberoRegole.ValidaBlocco("Z", "ECM", "P1", nessuno, null, chiavi, anagrafica).ErroreNodo);
        // con veicoli dichiarati l'anagrafica non allarga: restano ammessi solo i dichiarati
        Assert.Equal("DtcVeicoloNonDichiarato", DtcAlberoRegole.ValidaNuovaVoce("B", "ECM", "P0001", new[] { "A" }, null, chiavi, anagrafica));
        var voci = new List<DtcRequisito> { new() { Codice = "P1", Componente = "ECM", Veicolo = "A" }, new() { Codice = "P1", Componente = "ECM", Veicolo = "Z" } };
        var righe = DtcAlberoRegole.Costruisci(voci, nessuno, null, _ => false, anagrafica);
        Assert.False(righe.Single(r => r.Veicolo == "A").NonAllineata);
        Assert.True(righe.Single(r => r.Veicolo == "Z").NonAllineata);                          // veicolo fuori anagrafica: ancora da riallineare
    }
}
