using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>SegnalazioneRepository.GetByTestIdOrSenzaTest deve restituire lo stesso sovrainsieme che
/// TestCaseEditorViewModel.CaricaPannelloAnomalie otteneva prima filtrando DOPO GetAll() (Addendum prestazioni
/// 2026-09-27, editor Test a 100k): le Segnalazioni del Test richiesto, più quelle senza alcun Test associato,
/// e nessun'altra — filtrate PRIMA di clonare, come NonConformitaRepository.GetByTestIds.</summary>
public sealed class SegnalazioneRepositoryGetByTestIdScalabileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SegnalazioneGetByTestId_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateStateConSegnalazioni()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        state.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = "TC-A" });
        state.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = "TC-B" });
        state.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = "" }); // senza Test associato: deve comparire per qualunque Test richiesto
        state.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = "TC-C" });
        state.Segnalazioni.RefreshIndex();
        return state;
    }

    [Fact]
    public void GetByTestIdOrSenzaTest_TrovaSoloLeSegnalazioniDelTestPiuQuelleSenzaTest()
    {
        var state = CreateStateConSegnalazioni();

        var risultato = state.Segnalazioni.GetByTestIdOrSenzaTest("TC-A");

        Assert.Contains(risultato, s => s.TestId == "TC-A");
        Assert.Contains(risultato, s => s.TestId == "");
        Assert.DoesNotContain(risultato, s => s.TestId == "TC-B");
        Assert.DoesNotContain(risultato, s => s.TestId == "TC-C");
        Assert.Equal(2, risultato.Count);
    }

    [Fact]
    public void GetByTestIdOrSenzaTest_ConTestSenzaSegnalazioni_RestituisceSoloQuelleSenzaTest()
    {
        var state = CreateStateConSegnalazioni();

        var risultato = state.Segnalazioni.GetByTestIdOrSenzaTest("TC-INESISTENTE");

        Assert.Single(risultato);
        Assert.Equal("", risultato[0].TestId);
    }

    [Fact]
    public void GetByTestIdOrSenzaTest_RestituisceCopieIndipendentiDallaCache()
    {
        var state = CreateStateConSegnalazioni();

        var prima = state.Segnalazioni.GetByTestIdOrSenzaTest("TC-A").Single(s => s.TestId == "TC-A");
        prima.Stato = StatoSegnalazione.Esclusa;

        var dopo = state.Segnalazioni.GetByTestIdOrSenzaTest("TC-A").Single(s => s.TestId == "TC-A");
        Assert.Equal(StatoSegnalazione.DaClassificare, dopo.Stato);
    }

    [Fact]
    public void GetByTestIdOrSenzaTest_StessoOrdineDiGetAll_PiuRecentiPrima()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var t0 = new DateTime(2026, 9, 1, 10, 0, 0);
        state.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = "TC-A", CreataIl = t0 });
        state.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = "TC-A", CreataIl = t0.AddDays(2) });
        state.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = "", CreataIl = t0.AddDays(1) });
        state.Segnalazioni.RefreshIndex();

        var atteso = state.Segnalazioni.GetAll()
            .Where(s => s.TestId == "TC-A" || string.IsNullOrEmpty(s.TestId)).Select(s => s.Uid).ToList();
        var ottenuto = state.Segnalazioni.GetByTestIdOrSenzaTest("TC-A").Select(s => s.Uid).ToList();

        Assert.Equal(atteso, ottenuto);
    }

    [Fact]
    public void GetByTestIdOrSenzaTest_ConCopiaDiLavoroAttiva_ComeGetAllFiltrato()
    {
        var state = CreateStateConSegnalazioni();
        // Nella Unit of Work la Segnalazione di TC-B viene riassegnata a TC-A: filtrare sul valore
        // autoritativo prima dell'overlay la perderebbe.
        var working = state.Segnalazioni.GetAll().Single(s => s.TestId == "TC-B");
        working.TestId = "TC-A";

        using (WorkflowWorkingCopyScope.Begin(segnalazioni: new[] { working }))
        {
            var atteso = state.Segnalazioni.GetAll()
                .Where(s => s.TestId == "TC-A" || string.IsNullOrEmpty(s.TestId)).Select(s => s.Uid).ToList();
            var ottenuto = state.Segnalazioni.GetByTestIdOrSenzaTest("TC-A").Select(s => s.Uid).ToList();

            Assert.Equal(atteso, ottenuto);
            Assert.Contains(working.Uid, ottenuto);
        }
    }
}
