using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class TestTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_template_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Seed_ProduceITemplateAutomotiveStandardERasUdt()
    {
        var templates = TestTemplateDefaults.Seed();
        Assert.Equal(5, templates.Count);
        Assert.Contains(templates, t => t.Nome == "Validazione Banco");
        Assert.Contains(templates, t => t.Nome == "Validazione Veicolo");
        Assert.Contains(templates, t => t.Nome == "UnitLogger");
        Assert.Contains(templates, t => t.Nome == "UnitLogger Performance");
    }

    [Fact]
    public void Seed_OgniTemplateHaUnaSezioneDettagliConTuttiICampiSistema()
    {
        foreach (var t in TestTemplateDefaults.Seed())
        {
            var dettagli = Assert.Single(t.Sezioni);
            Assert.Equal("Dettagli", dettagli.Nome);
            var chiavi = dettagli.Campi.Select(c => c.CampoSistemaChiave).ToList();
            Assert.Equal(CampiSistema.Chiavi.Count, chiavi.Count);
            foreach (var chiave in CampiSistema.Chiavi) Assert.Contains(chiave, chiavi);
            Assert.All(dettagli.Campi, c => Assert.Equal(TipoCampoTemplate.CampoSistema, c.Tipo));
        }
    }

    [Fact]
    public void Seed_OgniTemplateHaITreCampiRiferimentiVisibiliENonObbligatori()
    {
        var templates = TestTemplateDefaults.Seed();
        foreach (var t in templates)
        {
            Assert.Equal(3, t.CampiRiferimenti.Count);
            Assert.All(t.CampiRiferimenti, c => Assert.False(c.Obbligatorio));
            var chiavi = t.CampiRiferimenti.Select(c => c.CampoSistemaChiave).ToList();
            foreach (var chiave in CampiSistema.ChiaviRiferimenti) Assert.Contains(chiave, chiavi);
        }
    }

    // La vecchia AzioneCollegataTemplate preconfigurata dal Master (Banco -> Veicolo) è stata rimossa (feedback
    // utente 2026-08-20, punto 17): "Crea test collegato" è ora un menu interattivo runtime disponibile per
    // qualunque template verso qualunque altro, vedi TestCaseEditorViewModel.CreaTestCollegati e
    // Scenarios51ExecutionTests.Test_22.

    [Fact]
    public void Seed_VincoloUdtNonEObbligatorioDiDefault()
    {
        var templates = TestTemplateDefaults.Seed();
        foreach (var nome in new[] { "UnitLogger", "UnitLogger Performance" })
        {
            var t = templates.Single(x => x.Nome == nome);
            Assert.NotNull(t.VincoloCollegamento);
            Assert.False(t.VincoloCollegamento!.Obbligatorio);
        }
    }

    [Fact]
    public void Seed_FogliReportRicalcanoIFogliOriginali()
    {
        var templates = TestTemplateDefaults.Seed();
        var banco = templates.Single(t => t.Nome == "Validazione Banco");
        Assert.Equal(new[] { "Test On Bench", "Guasto Injection" }, banco.FogliReport.Select(f => f.NomeFoglio));

        var veicolo = templates.Single(t => t.Nome == "Validazione Veicolo");
        Assert.Equal(new[] { "Test a Veicolo" }, veicolo.FogliReport.Select(f => f.NomeFoglio));

        var unitlogger = templates.Single(t => t.Nome == "UnitLogger");
        Assert.Equal(new[] { "UnitLogger" }, unitlogger.FogliReport.Select(f => f.NomeFoglio));
    }

    [Fact]
    public void Repository_SalvaERicaricaTemplateConSezioniECampi()
    {
        Directory.CreateDirectory(_root);
        var paths = new DataPaths(_root);
        var repo = new MasterDataRepository<TestTemplate>(paths.TestTemplateFile);

        var originali = TestTemplateDefaults.Seed();
        repo.Save(originali);

        var ricaricati = repo.Load();
        Assert.Equal(originali.Count, ricaricati.Count);
        var banco = ricaricati.Single(t => t.Nome == "Validazione Banco");
        Assert.Equal(2, banco.FogliReport.Count);
        Assert.NotEmpty(banco.Sezioni.Single().Campi);
    }

    [Fact]
    public void AppState_PrimaAperturaCartellaSeedaITemplateDiDefault()
    {
        Directory.CreateDirectory(_root);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(_root);

        var templates = state.Master.TestTemplate.Load();
        Assert.Equal(5, templates.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
