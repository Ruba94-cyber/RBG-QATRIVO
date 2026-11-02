using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Difesa in profondità (review 6.4.0, wip/6.4.0-perf-condiviso): <see cref="MasterDataRepository{T}.Save"/>
/// sospende sempre MasterLetturaScope prima di scrivere ("il percorso di scrittura non deve MAI vedere una
/// fotografia accorpata da uno scope di lettura aperto"), ma <see cref="MasterDataPagedReadSource{T}.SavePatch"/>
/// — il writer bounded usato dagli editor paginati (Requisiti/Funzionalità/Componente) — non lo faceva. Aggiunto lo
/// stesso <c>MasterLetturaScope.Sospendi()</c> per coerenza con l'unico altro percorso di scrittura master.
///
/// NOTA (dichiarazione richiesta quando un test di regressione non e' costruibile): il primo test sotto
/// riproduce lo scenario a rischio (scope aperto e gia' "usato" da un Refresh, poi una modifica concorrente
/// scritta da un'altra istanza sulla stessa voce, poi SavePatch) ma NON fallisce senza il fix — l'ho verificato
/// eseguendolo contro il codice pre-fix. PhaseDMasterRepositoryEngine.SavePatch rileva comunque il conflitto
/// reale (MasterDataConflictException) perche' la scrittura finale e' comunque convalidata contro lo stato
/// fisico del journal (vettori di versione), non solo contro la fotografia accorpata letta in pre-check: quella
/// fotografia stantia puo' al piu' fare arrivare SavePatch un passo piu' avanti prima di accorgersi del
/// conflitto, non fargli perdere silenziosamente la modifica concorrente. Il test resta quindi un test di
/// NON-REGRESSIONE (nessun conflitto reale deve mai passare inosservato, con o senza scope aperto), non una
/// riproduzione del difetto.
/// </summary>
public sealed class MasterDataPagedReadSourceSavePatchScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ScopePatch_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DataPaths PreparaPhaseD()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        JsonIo.WriteAtomic(paths.RequisitiFile, new List<Requisito>
        {
            new() { Id = "REQ-SCOPE-PATCH-001", Testo = "baseline", ModificatoDa = "test" }
        });
        var health = CondivisioneHealthService.Verifica(_root);
        CompatibilitaVersioneService.Applica(paths, health, new Version(6, 1, 0));
        Assert.True(PhaseDMasterBootstrapService.IsReady(_root));
        return paths;
    }

    /// <summary>Non-regressione (vedi nota in cima al file: non fallisce senza il fix, verificato). Con lo scope
    /// aperto e già "usato" (un Refresh già fatto, come farebbe una pagina che ha appena interrogato altri
    /// cataloghi), una modifica concorrente scritta da un'altra istanza sulla STESSA voce deve comunque risultare
    /// in un conflitto rilevato da SavePatch, mai in una sovrascrittura silenziosa dello stato condiviso più
    /// recente.</summary>
    [Fact]
    public void SavePatch_DentroUnoScopeGiaRinfrescato_VedeUnaModificaConcorrenteEDoNonLaSovrascrive()
    {
        var paths = PreparaPhaseD();
        var pagedSource = MasterDataPagedReadSources.Requisiti(paths);

        // Finestra "caricata dall'editor" prima di qualunque scrittura concorrente.
        var repoBaseline = new MasterDataRepository<Requisito>(paths.RequisitiFile);
        var caricati = repoBaseline.Load();
        Assert.Single(caricati);
        var baselineWindow = EntityStateFingerprint.Clone(caricati);
        var desiredWindow = EntityStateFingerprint.Clone(caricati);
        desiredWindow[0].Testo = "modificata da SavePatch paginato";
        desiredWindow[0].ModificatoDa = "Test SavePatch";

        using var scope = MasterLetturaScope.Apri();

        // Un Refresh già fatto in QUESTA lettura logica (esattamente come farebbe una pagina che ha appena
        // interrogato altri cataloghi prima di arrivare a questo SavePatch): mette in cache la fotografia
        // corrente per questo scope (vedi PhaseDMasterMaterializer.RefreshPerLettura).
        pagedSource.Refresh();

        // "Un'altra istanza" (thread indipendente: stessa cartella condivisa, nessuno scope ThreadStatic aperto
        // su quel thread, quindi il suo MasterDataRepository.Save vede sempre lo stato più aggiornato) modifica
        // la STESSA voce nel frattempo.
        Exception? erroreThreadConcorrente = null;
        var worker = new Thread(() =>
        {
            try
            {
                var repoConcorrente = new MasterDataRepository<Requisito>(paths.RequisitiFile);
                var lista = repoConcorrente.Load();
                lista.Single().Testo = "modificata da un'altra istanza";
                repoConcorrente.Save(lista);
            }
            catch (Exception ex) { erroreThreadConcorrente = ex; }
        });
        worker.Start();
        worker.Join();
        Assert.Null(erroreThreadConcorrente);

        // SavePatch, sulla stessa voce, con una baseline ormai superata dalla modifica concorrente sopra: deve
        // sempre rilevare il conflitto reale, mai sovrascrivere silenziosamente — con il fix perché il
        // pre-check vede subito lo stato aggiornato, senza perché comunque la scrittura finale e' convalidata
        // contro il journal fisico (vedi nota in cima al file).
        Assert.Throws<MasterDataConflictException>(() => pagedSource.SavePatch(desiredWindow, baselineWindow));

        // Nessuna sovrascrittura silenziosa: la modifica dell'altra istanza deve restare quella visibile.
        var dopo = new MasterDataRepository<Requisito>(paths.RequisitiFile).Load();
        Assert.Equal("modificata da un'altra istanza", dopo.Single().Testo);
    }

    /// <summary>Non-regressione: fuori da qualunque scope (il caso normale, editor paginato aperto da solo),
    /// SavePatch deve continuare a funzionare esattamente come prima — un salvataggio senza alcun conflitto
    /// reale deve andare a buon fine e il contenuto salvato deve essere quello desiderato.</summary>
    [Fact]
    public void SavePatch_FuoriDaUnoScope_SalvaSenzaConflitti()
    {
        var paths = PreparaPhaseD();
        var pagedSource = MasterDataPagedReadSources.Requisiti(paths);

        var repo = new MasterDataRepository<Requisito>(paths.RequisitiFile);
        var caricati = repo.Load();
        var baselineWindow = EntityStateFingerprint.Clone(caricati);
        var desiredWindow = EntityStateFingerprint.Clone(caricati);
        desiredWindow[0].Testo = "modificata senza conflitti";
        desiredWindow[0].ModificatoDa = "Test SavePatch";

        pagedSource.SavePatch(desiredWindow, baselineWindow);

        var dopo = repo.Load();
        Assert.Equal("modificata senza conflitti", dopo.Single().Testo);
    }
}
