using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Salvataggio atomico di Componente e Funzionalità (associazioni dal veicolo): su Phase D un solo batch nel journal con le
/// mutation di entrambi i cataloghi; su workspace legacy una transazione sui due file. Conflitti ed errori di pianificazione
/// non devono lasciare scritture parziali.</summary>
public sealed class MasterDataSalvataggioAtomicoTests : IDisposable
{
    private const string V1 = "VEI-1";
    private const string V2 = "VEI-2";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SalvataggioAtomico_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        PhaseDMasterRepositoryEngine<Funzionalita>.FaultInjectionPerUid = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    /// <summary>Workspace nuovo con i cataloghi legacy (JSON) seminati: due componente (E1 e E2 montate su V1) e una funzionalità
    /// valida su tutti i componenti. Il passaggio a Phase D (come fa l'app) si fa a parte, dopo il seme.</summary>
    private (DataPaths Paths, MasterDataStore Master) CreaWorkspaceSeminato()
    {
        Directory.CreateDirectory(_dir);
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var master = new MasterDataStore(paths);
        master.Componente.Save(new List<Componente>
        {
            new() { Nome = "E1", VeicoliAssociati = new List<string> { V1 } },
            new() { Nome = "E2", VeicoliAssociati = new List<string> { V1 } },
        });
        master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "F1", TutteLeEcu = true } });
        return (paths, master);
    }

    /// <summary>Copia di lavoro e baseline: la baseline è la fotografia letta con l'impronta di catalogo, come fa un editor.</summary>
    private static (List<T> Lavoro, List<T> Attese, string? Impronta) Apri<T>(MasterDataRepository<T> repo) where T : MasterEntity
    {
        var impronta = repo.CatturaImpronteCatalogoCorrente();
        var letti = repo.Load();
        var attese = JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(letti, JsonIo.Options), JsonIo.Options)!;
        var lavoro = JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(letti, JsonIo.Options), JsonIo.Options)!;
        return (lavoro, attese, impronta);
    }

    private static int RigheJournal(DataPaths paths)
    {
        var percorso = PhaseDMasterJournalService.CurrentPath(paths.Root, DeviceIdentityService.GetOrCreate());
        return File.Exists(percorso) ? File.ReadLines(percorso).Count(r => r.Trim().Length > 0) : 0;
    }

    /// <summary>Cataloghi coinvolti nell'ultimo batch del journal (campo Catalog delle sue mutation), in ordine.</summary>
    private static string[] CataloghiUltimoBatch(DataPaths paths)
    {
        var percorso = PhaseDMasterJournalService.CurrentPath(paths.Root, DeviceIdentityService.GetOrCreate());
        var ultima = File.ReadLines(percorso).Where(r => r.Trim().Length > 0).Last();
        using var documento = JsonDocument.Parse(ultima);
        var cataloghi = new List<string>();
        foreach (var proprieta in documento.RootElement.EnumerateObject())
        {
            if (!proprieta.NameEquals("Mutations") && !proprieta.NameEquals("mutations")) continue;
            foreach (var mutazione in proprieta.Value.EnumerateArray())
                foreach (var campo in mutazione.EnumerateObject())
                    if (campo.Name.Equals("Catalog", StringComparison.OrdinalIgnoreCase)) cataloghi.Add(campo.Value.GetString() ?? "");
        }
        return cataloghi.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Modifiche tipiche delle associazioni: monta V2 su E1 e disattiva F1 per V1 su E1 (restrizione E1 => V2).</summary>
    private static void ApplicaModificheAssociazioni(List<Componente> componente, List<Funzionalita> funzioni)
    {
        AssociazioniVeicoloService.ImpostaEcuMontata(componente.Single(e => e.Nome == "E1"), V2, true, funzioni);
        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(funzioni.Single(), componente.Single(e => e.Nome == "E1"), V1, applicata: false);
    }

    [Fact]
    public void SalvataggioPhaseD_ScriveUnSoloBatchConEntrambiICataloghi()
    {
        var (paths, master) = CreaWorkspaceSeminato();
        PhaseDMasterBootstrapService.EnsureReady(paths);
        Assert.True(PhaseDMasterBootstrapService.IsReady(paths.Root));
        var (ecuLavoro, ecuAttese, fpEcu) = Apri(master.Componente);
        var (funzLavoro, funzAttese, fpFunz) = Apri(master.Funzionalita);
        ApplicaModificheAssociazioni(ecuLavoro, funzLavoro);
        var righePrima = RigheJournal(paths);

        MasterDataSalvataggioAtomico.Salva(paths, master,
            new(ecuLavoro, ecuAttese, fpEcu),
            new(funzLavoro, funzAttese, fpFunz),
            allowSilentRemoteMerge: false);

        Assert.Equal(righePrima + 1, RigheJournal(paths));
        Assert.Equal(new[] { "componente", "funzionalita" }, CataloghiUltimoBatch(paths));
        Assert.Contains(V2, master.Componente.Load().Single(e => e.Nome == "E1").VeicoliAssociati);
        var funzLetta = master.Funzionalita.Load().Single();
        Assert.Equal(new[] { V2 }, funzLetta.VeicoliRistretti("E1"));
        Assert.True(funzLetta.ValeSu("E1", V2));
        Assert.False(funzLetta.ValeSu("E1", V1));
        Assert.True(funzLetta.ValeSu("E2", V1)); // l'altra componente non cambia
    }

    [Fact]
    public void SalvataggioPhaseD_ConflittoSuUnSoloCatalogo_NonScriveNulla()
    {
        var (paths, master) = CreaWorkspaceSeminato();
        PhaseDMasterBootstrapService.EnsureReady(paths);
        var (ecuLavoro, ecuAttese, fpEcu) = Apri(master.Componente);
        var (funzLavoro, funzAttese, fpFunz) = Apri(master.Funzionalita);
        ApplicaModificheAssociazioni(ecuLavoro, funzLavoro);

        // Modifica concorrente sulle funzionalità (dopo l'apertura, da un altro PC): il salvataggio deve fallire senza scrivere nulla.
        var altraPostazione = master.Funzionalita.Load();
        var baseAltra = JsonSerializer.Deserialize<List<Funzionalita>>(JsonSerializer.Serialize(altraPostazione, JsonIo.Options), JsonIo.Options)!;
        altraPostazione.Single().Categorie = new List<string> { "Modificata altrove" };
        master.Funzionalita.Save(altraPostazione, baseAltra);
        var righePrima = RigheJournal(paths);

        Assert.Throws<MasterDataConflictException>(() => MasterDataSalvataggioAtomico.Salva(paths, master,
            new(ecuLavoro, ecuAttese, fpEcu),
            new(funzLavoro, funzAttese, fpFunz),
            allowSilentRemoteMerge: false));

        Assert.Equal(righePrima, RigheJournal(paths));
        Assert.Equal(new[] { V1 }, master.Componente.Load().Single(e => e.Nome == "E1").VeicoliAssociati); // componente non scritta
    }

    [Fact]
    public void SalvataggioPhaseD_ErrorePianificazioneDiUnCatalogo_NonScriveNulla()
    {
        var (paths, master) = CreaWorkspaceSeminato();
        PhaseDMasterBootstrapService.EnsureReady(paths);
        var (ecuLavoro, ecuAttese, fpEcu) = Apri(master.Componente);
        var (funzLavoro, funzAttese, fpFunz) = Apri(master.Funzionalita);
        ApplicaModificheAssociazioni(ecuLavoro, funzLavoro);
        var righePrima = RigheJournal(paths);

        // Guasto iniettato nella pianificazione delle funzionalità: il componente è già pianificata, ma nulla deve essere pubblicato.
        PhaseDMasterRepositoryEngine<Funzionalita>.FaultInjectionPerUid = (_, _) => throw new InvalidOperationException("guasto iniettato");
        try
        {
            Assert.Throws<InvalidOperationException>(() => MasterDataSalvataggioAtomico.Salva(paths, master,
                new(ecuLavoro, ecuAttese, fpEcu),
                new(funzLavoro, funzAttese, fpFunz),
                allowSilentRemoteMerge: false));
        }
        finally
        {
            PhaseDMasterRepositoryEngine<Funzionalita>.FaultInjectionPerUid = null;
        }

        Assert.Equal(righePrima, RigheJournal(paths));
        Assert.Equal(new[] { V1 }, master.Componente.Load().Single(e => e.Nome == "E1").VeicoliAssociati);
        Assert.True(master.Funzionalita.Load().Single().ValeSu("E1", V1));
    }

    [Fact]
    public void SalvataggioPhaseD_SenzaModifiche_NonScriveAlcunBatch()
    {
        var (paths, master) = CreaWorkspaceSeminato();
        PhaseDMasterBootstrapService.EnsureReady(paths);
        var (ecuLavoro, ecuAttese, fpEcu) = Apri(master.Componente);
        var (funzLavoro, funzAttese, fpFunz) = Apri(master.Funzionalita);
        var righePrima = RigheJournal(paths);

        MasterDataSalvataggioAtomico.Salva(paths, master,
            new(ecuLavoro, ecuAttese, fpEcu),
            new(funzLavoro, funzAttese, fpFunz),
            allowSilentRemoteMerge: false);

        Assert.Equal(righePrima, RigheJournal(paths));
    }

    [Fact]
    public void SalvataggioLegacy_SenzaPhaseD_AggiornaEntrambiIFileNellaStessaTransazione()
    {
        var (paths, master) = CreaWorkspaceSeminato();
        Assert.False(PhaseDMasterBootstrapService.IsReady(paths.Root));
        var (ecuLavoro, ecuAttese, fpEcu) = Apri(master.Componente);
        var (funzLavoro, funzAttese, fpFunz) = Apri(master.Funzionalita);
        ApplicaModificheAssociazioni(ecuLavoro, funzLavoro);

        MasterDataSalvataggioAtomico.Salva(paths, master,
            new(ecuLavoro, ecuAttese, fpEcu),
            new(funzLavoro, funzAttese, fpFunz),
            allowSilentRemoteMerge: false);

        Assert.Contains(V2, master.Componente.Load().Single(e => e.Nome == "E1").VeicoliAssociati);
        Assert.Equal(new[] { V2 }, master.Funzionalita.Load().Single().VeicoliRistretti("E1"));
    }
}
