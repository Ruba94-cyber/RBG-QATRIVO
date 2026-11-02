using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1): politica condivisa "durata obbligatoria". Default facoltativa, modifica solo Master, scrittura
/// concorrente che fallisce senza sovrascrivere un valore piu' recente, file illeggibile che blocca il salvataggio.
/// Ogni test isola la propria cartella condivisa temporanea.</summary>
public sealed class PolicyDurataMasterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_PolicyDurata_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        return state;
    }

    [Fact]
    public void Default_EFacoltativa_QuandoIlFileManca()
    {
        var state = CreateState();
        Assert.False(File.Exists(state.Paths.PoliticaDurataFile));

        var politica = PoliticaDurataService.Load(state.Paths);

        Assert.False(politica.DurataObbligatoria);
        Assert.Null(politica.ErroreCaricamento);
        Assert.False(PoliticaDurataService.RichiedeDurata(politica));
        Assert.False(new PoliticaDurataConfig().DurataObbligatoria);
    }

    [Fact]
    public void Save_RifiutaSeNonEMasterEffettivo_NessunFileScritto()
    {
        var state = CreateState();
        var politica = new PoliticaDurataConfig { DurataObbligatoria = true };

        Assert.Throws<UnauthorizedAccessException>(() => PoliticaDurataService.Save(state.Paths, politica, false));
        Assert.False(File.Exists(state.Paths.PoliticaDurataFile));
    }

    [Fact]
    public void Save_DaMaster_PersisteERicaricaLaRichiesta()
    {
        var state = CreateState();
        var politica = PoliticaDurataService.Load(state.Paths);
        politica.DurataObbligatoria = true;

        PoliticaDurataService.Save(state.Paths, politica, true);

        var riletta = PoliticaDurataService.Load(state.Paths);
        Assert.True(riletta.DurataObbligatoria);
        Assert.Equal(1, riletta.RevisioneDati);
        Assert.True(PoliticaDurataService.RichiedeDurata(riletta));
    }

    [Fact]
    public void ScritturaConcorrente_DaUnaRevisioneVecchia_FalliscePerSicurezzaSenzaSovrascrivere()
    {
        var state = CreateState();
        var primo = PoliticaDurataService.Load(state.Paths);
        primo.DurataObbligatoria = true;
        PoliticaDurataService.Save(state.Paths, primo, true); // rev 1, obbligatoria

        // Due Master aprono Impostazioni sulla stessa revisione 1.
        var masterA = PoliticaDurataService.Load(state.Paths);
        var masterB = PoliticaDurataService.Load(state.Paths);

        masterA.DurataObbligatoria = false;
        PoliticaDurataService.Save(state.Paths, masterA, true); // rev 2, facoltativa

        masterB.DurataObbligatoria = true;
        Assert.Throws<ConflittoRevisioneException>(() => PoliticaDurataService.Save(state.Paths, masterB, true));

        var finale = PoliticaDurataService.Load(state.Paths);
        Assert.False(finale.DurataObbligatoria); // il valore piu' recente (rev 2) non e' stato sovrascritto
        Assert.Equal(2, finale.RevisioneDati);
    }

    [Fact]
    public void ScritturaConcorrente_FileModificatoFuoriApp_ConFingerprintDiversa_Rifiutata()
    {
        var state = CreateState();
        var base0 = PoliticaDurataService.Load(state.Paths);
        base0.DurataObbligatoria = true;
        PoliticaDurataService.Save(state.Paths, base0, true); // rev 1

        var stale = PoliticaDurataService.Load(state.Paths);
        // Modifica manuale del file che non tocca la revisione: la fingerprint della sorgente non deve coincidere.
        File.WriteAllText(state.Paths.PoliticaDurataFile,
            "{\n  \"RevisioneDati\": 1,\n  \"DurataObbligatoria\": false\n}");

        stale.DurataObbligatoria = true;
        Assert.Throws<ConflittoRevisioneException>(() => PoliticaDurataService.Save(state.Paths, stale, true));
        Assert.Contains("\"DurataObbligatoria\": false", File.ReadAllText(state.Paths.PoliticaDurataFile));
    }

    [Fact]
    public void FileIlleggibile_BloccaIlSalvataggio_ENonCambiaLaRichiesta()
    {
        var state = CreateState();
        File.WriteAllText(state.Paths.PoliticaDurataFile, "{ questo non e' JSON");

        var letta = PoliticaDurataService.Load(state.Paths);
        Assert.NotNull(letta.ErroreCaricamento);
        Assert.False(PoliticaDurataService.RichiedeDurata(letta)); // fallback facoltativo, mai obbligatorio da un file corrotto

        letta.DurataObbligatoria = true;
        Assert.Throws<InvalidDataException>(() => PoliticaDurataService.Save(state.Paths, letta, true));
        Assert.Equal("{ questo non e' JSON", File.ReadAllText(state.Paths.PoliticaDurataFile));
    }

    [Fact]
    public void RevisioniSuccessive_CrescononoSenzaSalti()
    {
        var state = CreateState();
        for (var i = 0; i < 3; i++)
        {
            var politica = PoliticaDurataService.Load(state.Paths);
            politica.DurataObbligatoria = i % 2 == 0;
            PoliticaDurataService.Save(state.Paths, politica, true);
        }

        var finale = PoliticaDurataService.Load(state.Paths);
        Assert.Equal(3, finale.RevisioneDati);
        Assert.True(finale.DurataObbligatoria);
    }

    [Fact]
    public void ImpostazioniViewModel_SalvaLaPoliticaSoloDaMaster()
    {
        var state = CreateState();
        var master = new Operatore { Nome = "Master Uno", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), PinChiaro = "1234", ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(master);
        var operatore = new Operatore { Nome = "Operatore Uno", Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("5678"), PinChiaro = "5678", ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(operatore);
        var operatori = state.Master.Operatori.Load().ToList();
        operatori.Add(master);
        operatori.Add(operatore);
        state.Master.Operatori.Save(operatori);

        state.ImpostaOperatore("Operatore Uno", "5678");
        var vmOperatore = new ImpostazioniViewModel(state, () => { });
        vmOperatore.DurataObbligatoriaSelezionata = true;
        vmOperatore.SalvaPoliticaDurataCommand.Execute(null);
        Assert.False(File.Exists(state.Paths.PoliticaDurataFile)); // un Operatore non puo' modificare la politica

        state.ImpostaOperatore("Master Uno", "1234");
        var vmMaster = new ImpostazioniViewModel(state, () => { });
        vmMaster.DurataObbligatoriaSelezionata = true;
        vmMaster.SalvaPoliticaDurataCommand.Execute(null);
        Assert.True(PoliticaDurataService.Load(state.Paths).DurataObbligatoria);
    }

    [Fact]
    public void Save_ConcorrentiDallaStessaRevisione_UnSoloVinceSenzaPerdereAggiornamenti()
    {
        var state = CreateState();
        PoliticaDurataService.Save(state.Paths, new PoliticaDurataConfig { DurataObbligatoria = true }, true); // revisione 1

        // Otto Master caricano la stessa revisione e salvano insieme: solo il primo puo' riuscire, gli altri ricevono conflitto.
        var copie = Enumerable.Range(0, 8).Select(_ => PoliticaDurataService.Load(state.Paths)).ToList();
        var successi = 0;
        var conflitti = 0;
        var partenza = new ManualResetEventSlim(false);
        var attivita = copie.Select(copia => Task.Run(() =>
        {
            partenza.Wait();
            copia.DurataObbligatoria = !copia.DurataObbligatoria;
            try
            {
                PoliticaDurataService.Save(state.Paths, copia, true);
                Interlocked.Increment(ref successi);
            }
            catch (ConflittoRevisioneException)
            {
                Interlocked.Increment(ref conflitti);
            }
        })).ToArray();
        partenza.Set();
        Assert.True(Task.WaitAll(attivita, TimeSpan.FromMinutes(2)), "Salvataggi concorrenti della politica bloccati.");

        Assert.Equal(1, successi);
        Assert.Equal(copie.Count - 1, conflitti);
        Assert.Equal(2, PoliticaDurataService.Load(state.Paths).RevisioneDati);
    }
}
