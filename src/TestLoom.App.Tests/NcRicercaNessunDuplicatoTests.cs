using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Regressione: cercando una NC per Id la lista non deve mai mostrare la stessa NC due volte.</summary>
public sealed class NcRicercaNessunDuplicatoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NcRicerca_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task RicercaPerId_ConDueNc_MostraUnaSolaRigaSenzaDuplicati()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.NonConformita.Save(new NonConformita { Id = "NC-QA-0001", TestCaseId = "", DataApertura = DateTime.UtcNow, Severita = SeveritaAnomalia.Medium, StatoWorkflow = StatoAnomalia.InVerifica, Descrizione = "NC seminata" });
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-QA-0002", TestCaseId = "", DataApertura = DateTime.UtcNow.AddMinutes(-1), Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica, Descrizione = "NC con ticket", DaSegnalazioneRete = true,
            CollegamentiPolarion = { new CollegamentoPolarion { Codice = "TICKET-12345", Url = "https://ticketing.example/TICKET-12345" } },
        });

        var vm = new NonConformitaViewModel(state, _ => { });
        Assert.Equal(new[] { "NC-QA-0001", "NC-QA-0002" }, vm.Righe.Select(r => r.Nc.Id).OrderBy(x => x));

        vm.FiltroTesto = "NC-QA-0001";
        for (var i = 0; i < 40 && vm.Righe.Count != 1; i++) await Task.Delay(100);
        Assert.Equal(new[] { "NC-QA-0001" }, vm.Righe.Select(r => r.Nc.Id));
    }
}
