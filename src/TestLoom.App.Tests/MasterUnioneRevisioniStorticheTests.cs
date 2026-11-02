using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Rinomina/Unisci/Dividi di un elemento di catalogo usato da un Test con più REV: il nome nuovo vale in
/// TUTTE le REV (anche quelle storiche), senza creare REV e senza il blocco "REV storica non modificabile"
/// (feedback utente 2026-10-06, punto 1).</summary>
public sealed class MasterUnioneRevisioniStorticheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterUnioneRev_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } } }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    private static Esecuzione Rev(int n, string componente) => new()
    {
        Iterazione = n,
        Condizioni = { new Condizione { Componente = new EcuSelection(new[] { componente }), Funzionalita = "Diagnosi" } }
    };

    private static void CreaTestConTreRev(AppState state)
    {
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        var tc = new TestCase { Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" } };
        tc.Esecuzioni = new List<Esecuzione> { Rev(1, "BCM"), Rev(2, "BCM"), Rev(3, "BCM") };
        state.TestCases.Save(tc, isNew: true);
    }

    [Fact]
    public void Rinomina_Componente_RiscriveIlNomeInTutteLeRevStoricheSenzaCrearneDiNuove()
    {
        var state = CreaStato();
        CreaTestConTreRev(state);

        MasterUnioneService.Rinomina(state, state.Master.Componente, "Componente", "BCM", "BCM2", e => e.Nome, new List<string>());

        var tc = state.TestCases.GetAll().Single()!;
        Assert.Equal(3, tc.Esecuzioni.Count);
        Assert.Equal("BCM2", tc.Componente.Single());
        Assert.All(tc.Esecuzioni, e => Assert.Equal("BCM2", e.Condizioni.Single().Componente.Single()));
    }

    [Fact]
    public void Unisci_Componente_RiscriveIlNomeInTutteLeRevStoriche()
    {
        var state = CreaStato();
        CreaTestConTreRev(state);

        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, new List<string>());

        var tc = state.TestCases.GetAll().Single()!;
        Assert.Equal(3, tc.Esecuzioni.Count);
        Assert.All(tc.Esecuzioni, e => Assert.Equal("ECM", e.Condizioni.Single().Componente.Single()));
    }

    [Fact]
    public void FuoriDalMotoreCataloghi_LaRevStoricaRestaProtetta()
    {
        var state = CreaStato();
        CreaTestConTreRev(state);

        var copia = state.TestCases.GetAll().Single()!;
        copia = System.Text.Json.JsonSerializer.Deserialize<TestCase>(System.Text.Json.JsonSerializer.Serialize(copia, JsonIo.Options), JsonIo.Options)!;
        copia.Esecuzioni[0].Condizioni[0].Componente = new EcuSelection(new[] { "ALTRA" });
        copia.Esecuzioni.Add(Rev(4, "BCM"));

        Assert.Throws<InvalidDataException>(() => state.TestCases.Save(copia, isNew: false, revisioneAttesa: copia.RevisioneDati));
    }
}
