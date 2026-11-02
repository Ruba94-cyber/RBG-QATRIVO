using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// MasterLetturaScope accorpa in una sola lettura logica i Refresh Phase D che Home e Report farebbero una
/// volta per catalogo (feedback utente 2026-09-18: "le prestazioni generali sono molto pessime"). Il rischio
/// da presidiare non e' la velocita' ma la FRESCHEZZA: se l'accorpamento sopravvivesse allo scope, una pagina
/// continuerebbe a mostrare master vecchi anche dopo un salvataggio o una consegna remota. Questi test
/// bloccano esattamente quello.
/// </summary>
public sealed class MasterLetturaScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_Scope_Shared_" + Guid.NewGuid().ToString("N"));

    public MasterLetturaScopeTests() => Directory.CreateDirectory(_root);

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
            new() { Id = "REQ-SCOPE-001", Testo = "baseline", ModificatoDa = "test" }
        });
        var health = CondivisioneHealthService.Verifica(_root);
        CompatibilitaVersioneService.Applica(paths, health, new Version(6, 1, 0));
        Assert.True(PhaseDMasterBootstrapService.IsReady(_root));
        return paths;
    }

    [Fact]
    public void FuoriDalloScope_NonCEnessunaLetturaInCorso()
    {
        Assert.Null(MasterLetturaScope.IdCorrente);
        using (MasterLetturaScope.Apri()) Assert.NotNull(MasterLetturaScope.IdCorrente);
        Assert.Null(MasterLetturaScope.IdCorrente);
    }

    [Fact]
    public void ScopeAnnidati_CondividonoLaStessaLetturaLogica()
    {
        using var esterno = MasterLetturaScope.Apri();
        var idEsterno = MasterLetturaScope.IdCorrente;
        using (MasterLetturaScope.Apri())
            Assert.Equal(idEsterno, MasterLetturaScope.IdCorrente);

        // Chiudere quello interno non deve chiudere quello esterno.
        Assert.Equal(idEsterno, MasterLetturaScope.IdCorrente);
    }

    [Fact]
    public void DueLettureSuccessive_SonoLettureLogicheDiverse()
    {
        long? prima, seconda;
        using (MasterLetturaScope.Apri()) prima = MasterLetturaScope.IdCorrente;
        using (MasterLetturaScope.Apri()) seconda = MasterLetturaScope.IdCorrente;
        Assert.NotEqual(prima, seconda);
    }

    [Fact]
    public void UnAltroThread_NonVedeLoScopeApertoQui()
    {
        // Apri()/Dispose() sono [ThreadStatic] e vanno usati entro un singolo metodo SINCRONO sullo stesso
        // thread OS, esattamente come in HomeViewModel.Aggiorna/ReportViewModel.Aggiorna (nessun await al
        // loro interno) — un thread del ThreadPool, invece, PUO' essere stato riusato da un Apri()/Dispose()
        // precedente eseguito su thread diversi (es. un test scritto male con un "await" in mezzo) e quindi
        // partire con uno stato ThreadStatic non vergine: non e' un buon doppio cieco per questa verifica.
        // Un System.Threading.Thread appena creato garantisce storage ThreadStatic vergine.
        using var _ = MasterLetturaScope.Apri();
        Assert.NotNull(MasterLetturaScope.IdCorrente);

        // Il refresh periodico e la manutenzione girano su thread di background: non devono MAI ereditare
        // l'accorpamento deciso dal thread UI, altrimenti salterebbero un refresh che invece devono fare.
        long? idAltroThread = -1;
        var worker = new Thread(() => idAltroThread = MasterLetturaScope.IdCorrente);
        worker.Start();
        worker.Join();
        Assert.Null(idAltroThread);
    }

    [Fact]
    public void DentroLoScope_ILetturiRipetutiRestituisconoGliStessiDati()
    {
        var paths = PreparaPhaseD();
        var requisiti = new MasterDataRepository<Requisito>(paths.RequisitiFile);
        var funzionalita = new MasterDataRepository<Funzionalita>(paths.FunzionalitaFile);

        using var _ = MasterLetturaScope.Apri();
        var primaLettura = requisiti.Load();
        Assert.Single(primaLettura);
        Assert.Equal("REQ-SCOPE-001", primaLettura[0].Id);

        // Un secondo catalogo nella stessa lettura logica riusa il refresh del primo: deve comunque
        // restituire i propri dati corretti, non quelli del catalogo precedente.
        Assert.Empty(funzionalita.Load());
        Assert.Single(requisiti.Load());
    }

    [Fact]
    public void ChiusoLoScope_UnSalvataggioSuccessivoEImmediatamenteVisibile()
    {
        var paths = PreparaPhaseD();
        var funzionalita = new MasterDataRepository<Funzionalita>(paths.FunzionalitaFile);

        using (MasterLetturaScope.Apri()) Assert.Empty(funzionalita.Load());

        var lista = funzionalita.Load();
        lista.Add(new Funzionalita { Uid = Guid.NewGuid(), Nome = "FUN-NUOVA" });
        funzionalita.Save(lista);

        // Sia fuori sia dentro una NUOVA lettura logica: l'accorpamento non deve sopravvivere allo scope.
        Assert.Contains(funzionalita.Load(), f => f.Nome == "FUN-NUOVA");
        using (MasterLetturaScope.Apri())
            Assert.Contains(funzionalita.Load(), f => f.Nome == "FUN-NUOVA");
    }

    /// <summary>Il salvataggio non deve mai finire dentro una lettura accorpata: se cosi' fosse, scriverebbe
    /// partendo da una fotografia potenzialmente non aggiornata dello stato condiviso. Qui si verifica che
    /// anche nel caso limite (Save eseguito mentre uno scope e' aperto) il dato salvato resti corretto.</summary>
    [Fact]
    public void UnSalvataggioDentroUnoScope_NonPerdeIlDato()
    {
        var paths = PreparaPhaseD();
        var funzionalita = new MasterDataRepository<Funzionalita>(paths.FunzionalitaFile);

        using (MasterLetturaScope.Apri())
        {
            var lista = funzionalita.Load();
            lista.Add(new Funzionalita { Uid = Guid.NewGuid(), Nome = "FUN-DENTRO-SCOPE" });
            funzionalita.Save(lista);
        }

        Assert.Contains(funzionalita.Load(), f => f.Nome == "FUN-DENTRO-SCOPE");
    }
}
