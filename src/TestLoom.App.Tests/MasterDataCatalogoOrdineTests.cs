using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Ordine di presentazione dei cataloghi master dopo Phase D (6.6.0): il materializer legge le teste ORDER BY entity_uid
/// (UUID casuali), quindi template, componente, veicoli e funzionalita comparivano in ordine casuale. Il fix ordina in lettura
/// (nessun dato persistito cambia): prima l'ordine esplicito (Ordine) quando la riga ne ha uno, poi il nome
/// (chiave business, confronto ordinale senza maiuscole), infine l'UID come ultimo spareggio. L'ordine dipende solo dal
/// contenuto condiviso, quindi e' identico fra riavvii e fra due postazioni che leggono la stessa cartella.
/// </summary>
public sealed class MasterDataCatalogoOrdineTests : IDisposable
{
    // UID scelti in ordine INVERSO rispetto ai nomi: con l'ordinamento per UID il test fallisce in modo deterministico.
    private static readonly Guid UidPrimo = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
    private static readonly Guid UidMezzo = Guid.Parse("88888888-0000-0000-0000-000000000002");
    private static readonly Guid UidUltimo = Guid.Parse("00000000-0000-0000-0000-000000000003");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_CatalogoOrdine_" + Guid.NewGuid().ToString("N"));
    private readonly string _copia = Path.Combine(Path.GetTempPath(), "TestLoom_CatalogoOrdineCopia_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _copia })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private DataPaths NuovaCartellaPhaseD(string radice)
    {
        var paths = new DataPaths(radice);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);
        return paths;
    }

    private static List<string> Nomi<T>(IEnumerable<T> righe, Func<T, string> nome) => righe.Select(nome).ToList();

    [Fact]
    public void Funzionalita_SenzaOrdineEsplicito_SonoOrdinatePerNome_NonPerUid()
    {
        var paths = NuovaCartellaPhaseD(_root);
        var repo = new MasterDataRepository<Funzionalita>(paths.FunzionalitaFile);
        repo.Save(new List<Funzionalita>
        {
            new() { Uid = UidPrimo, Nome = "Accensione" },
            new() { Uid = UidMezzo, Nome = "Misura" },
            new() { Uid = UidUltimo, Nome = "Diagnosi" }
        });

        var letto = repo.Load();

        Assert.Equal(new[] { "Accensione", "Diagnosi", "Misura" }, Nomi(letto, f => f.Nome));
    }

    [Fact]
    public void Componente_StessoOrdineDopoRiavvioEDopoCopiaSuSecondaPostazione()
    {
        var paths = NuovaCartellaPhaseD(_root);
        new MasterDataRepository<Componente>(paths.EcuFile).Save(new List<Componente>
        {
            new() { Nome = "TCU" },
            new() { Nome = "BCM" },
            new() { Nome = "ECM" }
        });
        var primaLettura = Nomi(new MasterDataRepository<Componente>(paths.EcuFile).Load(), e => e.Nome);

        // Stessi dati in una cartella copiata (seconda postazione, cache locale distinta): stesso ordine.
        CopiaDirectory(_root, _copia);
        var copiaPaths = new DataPaths(_copia);
        var secondaPostazione = Nomi(new MasterDataRepository<Componente>(copiaPaths.EcuFile).Load(), e => e.Nome);
        var riavvio = Nomi(new MasterDataRepository<Componente>(paths.EcuFile).Load(), e => e.Nome);

        Assert.Equal(new[] { "BCM", "ECM", "TCU" }, primaLettura);
        Assert.Equal(primaLettura, riavvio);
        Assert.Equal(primaLettura, secondaPostazione);
    }

    [Fact]
    public void TestTemplate_OrdineEsplicito_PrevaleSulNomeESulleUid()
    {
        var paths = NuovaCartellaPhaseD(_root);
        var repo = new MasterDataRepository<TestTemplate>(paths.TestTemplateFile);
        repo.Save(new List<TestTemplate>
        {
            new() { Uid = UidPrimo, Nome = "Alfa", Ordine = 3 },
            new() { Uid = UidMezzo, Nome = "Beta", Ordine = 1 },
            new() { Uid = UidUltimo, Nome = "Gamma", Ordine = 2 }
        });

        var letto = repo.Load();

        Assert.Equal(new[] { "Beta", "Gamma", "Alfa" }, Nomi(letto, t => t.Nome));
    }

    [Fact]
    public void TestTemplate_OrdineUguale_SpareggioPerNomeEStabileFraLetture()
    {
        var paths = NuovaCartellaPhaseD(_root);
        var repo = new MasterDataRepository<TestTemplate>(paths.TestTemplateFile);
        repo.Save(new List<TestTemplate>
        {
            new() { Uid = UidPrimo, Nome = "Validazione Banco", Ordine = 0 },
            new() { Uid = UidMezzo, Nome = "Calibrazione", Ordine = 0 },
            new() { Uid = UidUltimo, Nome = "Collaudo", Ordine = 0 }
        });

        var prima = Nomi(repo.Load(), t => t.Nome);
        var seconda = Nomi(new MasterDataRepository<TestTemplate>(paths.TestTemplateFile).Load(), t => t.Nome);

        Assert.Equal(new[] { "Calibrazione", "Collaudo", "Validazione Banco" }, prima);
        Assert.Equal(prima, seconda);
    }

    private static void CopiaDirectory(string origine, string destinazione)
    {
        foreach (var dir in Directory.EnumerateDirectories(origine, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destinazione, Path.GetRelativePath(origine, dir)));
        foreach (var file in Directory.EnumerateFiles(origine, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destinazione, Path.GetRelativePath(origine, file)), overwrite: true);
    }
}
