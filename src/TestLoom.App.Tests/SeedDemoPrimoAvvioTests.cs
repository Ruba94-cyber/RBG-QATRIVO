using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class SeedDemoPrimoAvvioTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "TestLoom_first_run_demo_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConfigurazioneDemo_CreaTuttiIMasterMaNessunTestONessunRequisito()
    {
        var root = NuovaCartellaVuota("config");
        SeedDemoDataService.GeneraConfigurazionePerPrimoAvvio(root);
        var paths = new DataPaths(root);

        Assert.NotEmpty(new MasterDataRepository<StrumentoDiagnostico>(paths.StrumentiFile).Load());
        Assert.Contains(new MasterDataRepository<StrumentoDiagnostico>(paths.StrumentiFile).Load(), s => s.Campi.Count > 0);
        Assert.NotEmpty(new MasterDataRepository<Funzionalita>(paths.FunzionalitaFile).Load());
        Assert.NotEmpty(new MasterDataRepository<Categoria>(paths.CategorieFile).Load());
        Assert.NotEmpty(new MasterDataRepository<Componente>(paths.EcuFile).Load());
        Assert.NotEmpty(new MasterDataRepository<Veicolo>(paths.VeicoliFile).Load());
        Assert.NotEmpty(new MasterDataRepository<Gamma>(paths.GammaFile).Load());
        Assert.False(File.Exists(paths.OperatoriFile));
        Assert.NotEmpty(new MasterDataRepository<TestTemplate>(paths.TestTemplateFile).Load());
        Assert.NotEmpty(new MasterDataRepository<CampoSistemaDefinizione>(paths.CampiSistemaPianificazioneFile).Load());
        Assert.NotEmpty(new MasterDataRepository<CampoSistemaDefinizione>(paths.CampiSistemaDettagliFile).Load());
        Assert.False(File.Exists(paths.RequisitiFile));
        Assert.Empty(Directory.EnumerateFiles(paths.TestCasesDir, "testcase_*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void ConfigurazioneDemo_NonCreaNessunCampoDemoProgettoERelease()
    {
        var root = NuovaCartellaVuota("config-campi-demo");
        SeedDemoDataService.GeneraConfigurazionePerPrimoAvvio(root);

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var contenuto = File.ReadAllText(file);
            Assert.DoesNotContain("progetto-demo", contenuto);
            Assert.DoesNotContain("release-demo", contenuto);
        }
    }

    [Fact]
    [Trait("Category", "Lento")]
    public void DemoCompletaPrimoAvvio_CreaCampiDemoProgettoERelease()
    {
        var root = NuovaCartellaVuota("full-campi-demo");
        SeedDemoDataService.GeneraCompletoPerPrimoAvvio(root);
        var paths = new DataPaths(root);

        var dettagli = new MasterDataRepository<CampoSistemaDefinizione>(paths.CampiSistemaDettagliFile).Load();
        Assert.Contains(dettagli, c => c.Chiave == "progetto-demo");
        Assert.Contains(dettagli, c => c.Chiave == "release-demo");

        var templates = new MasterDataRepository<TestTemplate>(paths.TestTemplateFile).Load();
        Assert.All(templates.Where(t => !t.IsChangeRequest), t =>
        {
            Assert.Contains(t.Sezioni[0].Campi, c => c.CampoSistemaChiave == "progetto-demo");
            Assert.Contains(t.Sezioni[0].Campi, c => c.CampoSistemaChiave == "release-demo");
        });
    }

    [Fact]
    [Trait("Category", "Lento")]
    public void DemoCompletaPrimoAvvio_CreaTestERequisitiSenzaArtefattiQa()
    {
        var root = NuovaCartellaVuota("full");
        SeedDemoDataService.GeneraCompletoPerPrimoAvvio(root);
        var paths = new DataPaths(root);
        var repo = new TestCaseRepository(paths);
        repo.RefreshIndex();

        Assert.True(repo.GetAll().Count > 100);
        Assert.NotEmpty(new MasterDataRepository<Requisito>(paths.RequisitiFile).Load());
        var operatori = new MasterDataRepository<Operatore>(paths.OperatoriFile).Load();
        Assert.Equal(4, operatori.Count);
        Assert.All(operatori, o => Assert.True(o.RichiedeCambioPin));
        Assert.Empty(repo.FileConProblemi);
        Assert.False(File.Exists(paths.RequisitiFile + ".lock"));
    }

    [Fact]
    public void InizializzazioneDemo_CartellaNonVuotaVieneRifiutataSenzaToccareContenuti()
    {
        var root = NuovaCartellaVuota("protected");
        var sentinel = Path.Combine(root, "dati-reali.txt");
        File.WriteAllText(sentinel, "non modificare");

        Assert.Throws<InvalidOperationException>(() => SeedDemoDataService.GeneraCompletoPerPrimoAvvio(root));
        Assert.Throws<InvalidOperationException>(() => SeedDemoDataService.GeneraConfigurazionePerPrimoAvvio(root));
        Assert.Equal("non modificare", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void SeedQa_CartellaNonMarcataVieneRifiutataSenzaToccareContenuti()
    {
        var root = NuovaCartellaVuota("protected-qa");
        var sentinel = Path.Combine(root, "dati-reali.txt");
        File.WriteAllText(sentinel, "non modificare");

        Assert.Throws<InvalidOperationException>(() => SeedDemoDataService.Genera(root));
        Assert.Equal("non modificare", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(root));
    }

    private string NuovaCartellaVuota(string nome)
    {
        var root = Path.Combine(_base, nome);
        Directory.CreateDirectory(root);
        return root;
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, true);
    }
}
