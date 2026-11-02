using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Il marcatore delle migrazioni del catalogo non viene mai riparato automaticamente: la riparazione dei JSON non
/// lo tocca e un marcatore corrotto resta fail-closed (la migrazione non scrive nulla).</summary>
public sealed class CatalogMigrationRiparazioneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_rasudt_riparazione_" + Guid.NewGuid().ToString("N"));

    // Virgola finale prima della chiusura: il riparatore generico la rimuoverebbe e riscriverebbe il file.
    private const string MarcatoreCorrotto = "{\"RevisioneDati\":1,\"Applicate\":[\"ras-unitlogger-template-v1\"],}";

    [Fact]
    public void Riparazione_NonRiscriveIlMarcatoreCorrotto()
    {
        Directory.CreateDirectory(_root);
        // Radice di un workspace reale (con il manifest di versione): la risoluzione del root riuscirebbe, quindi
        // l'unico motivo di rifiuto e' il divieto esplicito sul marcatore.
        File.WriteAllText(Path.Combine(_root, "version-manifest.json"), "{}");
        var paths = new DataPaths(_root);
        File.WriteAllText(paths.CatalogMigrationsFile, MarcatoreCorrotto);
        var prima = File.ReadAllBytes(paths.CatalogMigrationsFile);

        var esito = FileRepairService.ProvaRipara(paths.CatalogMigrationsFile);

        Assert.False(esito.Riuscita);
        Assert.Contains("marcatore delle migrazioni", esito.Messaggio);
        Assert.Equal(prima, File.ReadAllBytes(paths.CatalogMigrationsFile));
        Assert.Empty(Directory.GetFiles(_root, "catalog-migrations.json.corrotto-*"));
    }

    [Fact]
    public void Migrazione_MarcatoreCorrottoRestaBloccataSenzaScritture()
    {
        Directory.CreateDirectory(_root);
        var paths = new DataPaths(_root);
        var repo = new MasterDataRepository<TestTemplate>(paths.TestTemplateFile);
        repo.Save(TestTemplateDefaults.Seed().Where(t => t.Uid != TestTemplateDefaults.RasUdtUid).ToList());
        File.WriteAllText(paths.CatalogMigrationsFile, MarcatoreCorrotto);
        var catalogoPrima = File.ReadAllBytes(paths.TestTemplateFile);
        var marcatorePrima = File.ReadAllBytes(paths.CatalogMigrationsFile);

        var locks = new LockService("dispositivo-test-riparazione");
        var esito = CatalogMigrationService.EseguiRasUdt(paths, repo, locks, "Test Master");

        Assert.Equal(EsitoMigrazioneCatalogo.Bloccata, esito.Esito);
        Assert.Equal(catalogoPrima, File.ReadAllBytes(paths.TestTemplateFile));
        Assert.Equal(marcatorePrima, File.ReadAllBytes(paths.CatalogMigrationsFile));

        // Anche il percorso di riparazione lo rifiuta e lascia i byte invariati.
        Assert.False(FileRepairService.ProvaRipara(paths.CatalogMigrationsFile).Riuscita);
        Assert.Equal(marcatorePrima, File.ReadAllBytes(paths.CatalogMigrationsFile));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
