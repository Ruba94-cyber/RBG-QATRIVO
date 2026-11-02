using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed class TestTemplateViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_testtemplatevm_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CampiLiberi_IncludeSoloCampiRuoloNessunoNonArchiviati()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        var catalogo = state.Master.CampiSistemaDettagli.Load();
        catalogo.Add(new CampoSistemaDefinizione { Chiave = "custom-1", Etichetta = "Custom 1", Ruolo = RuoloCampoSistema.Nessuno });
        catalogo.Add(new CampoSistemaDefinizione { Chiave = "custom-2-deprecato", Etichetta = "Custom deprecato", Ruolo = RuoloCampoSistema.Nessuno, Archiviata = true });
        state.Master.CampiSistemaDettagli.Save(catalogo);

        var liberi = CampoSistemaCatalogoService.CampiLiberi(state, GruppoCampoSistema.Dettagli);

        Assert.Contains(liberi, c => c.Chiave == "custom-1");
        Assert.DoesNotContain(liberi, c => c.Chiave == "custom-2-deprecato");
        Assert.DoesNotContain(liberi, c => c.Ruolo == RuoloCampoSistema.Componente);
    }

    [Fact]
    public void Stato_EspostoPubblicamentePerLApriDelEditorDiTemplate()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        var vm = new TestTemplateViewModel(state);

        Assert.Same(state, vm.Stato);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
