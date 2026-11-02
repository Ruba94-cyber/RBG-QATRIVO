using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Collisione di ID Test tra postazioni con indice locale non aggiornato (P2.8): il claim esclusivo
/// dell'ID sulla cartella condivisa impedisce che due Test nuovi ricevano lo stesso codice. Coperti anche il layout a
/// blocchi delle cartelle, la pulizia dei claim scaduti (solo quelli idonei) e la pulizia una volta al giorno per PC.</summary>
public sealed class TestCaseIdClaimTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_IdClaim_" + Guid.NewGuid().ToString("N"));
    private readonly List<AppState> _postazioni = new();

    public void Dispose()
    {
        foreach (var postazione in _postazioni)
        {
            try { postazione.Dispose(); } catch { }
        }
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreaPostazione(string nome)
    {
        var stato = TestPostazioniCondivise.Apri(Path.Combine(_root, "data"), Path.Combine(_root, "local-" + nome));
        _postazioni.Add(stato);
        return stato;
    }

    private DataPaths Percorsi => new(Path.Combine(_root, "data"));

    private static TestCase NuovoTest(string id, string componente) => new()
    {
        Id = id,
        Componente = componente,
        Descrizione = "collisione",
        DescrizioneBreve = "collisione",
        StatoLavoro = StatoLavoroTest.Pianificato,
    };

    [Fact]
    public void DuePostazioniConLoStessoIdNuovo_NonProduconoDueTestConLoStessoCodice()
    {
        var postazioneA = CreaPostazione("A");
        var postazioneB = CreaPostazione("B"); // indice aperto prima del Test di A: non lo conosce
        var primo = NuovoTest("T-AB-0001", "BCM");
        postazioneA.TestCases.Save(primo, isNew: true);

        // Stesso ID generato da B; componente diversa, quindi il controllo sul file del percorso gerarchico non lo vede.
        var secondo = NuovoTest("T-AB-0001", "Componente-X");
        postazioneB.TestCases.Save(secondo, isNew: true);

        Assert.NotEqual(primo.Id, secondo.Id);
        postazioneA.TestCases.RefreshIndex();
        var ids = postazioneA.TestCases.GetAll(includeEliminati: true).Select(t => t.Id).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ClaimLasciatoDaUnSalvataggioInterrotto_NonBloccaMaSaltaSoloLId()
    {
        var postazione = CreaPostazione("A");
        var claimOrfano = TestCaseIdClaim.PercorsoClaim(postazione.Paths, "T-AB-0002");
        Directory.CreateDirectory(Path.GetDirectoryName(claimOrfano)!);
        File.WriteAllText(claimOrfano, "{ \"uid\": \"00000000-0000-0000-0000-000000000000\" }");

        var tc = NuovoTest("T-AB-0002", "BCM");
        postazione.TestCases.Save(tc, isNew: true);

        Assert.Equal("T-AB-0003", tc.Id);
        Assert.Contains(postazione.TestCases.GetAll(), t => t.Id == "T-AB-0003");
    }

    [Fact]
    public void SalvataggioNuovoFallitoPrimaDellaScrittura_RilasciaIlClaimDellId()
    {
        var postazioneA = CreaPostazione("A");
        var postazioneB = CreaPostazione("B");
        var fallito = NuovoTest("T-AB-0005", "BCM");
        var percorso = postazioneA.TestCases.PercorsoPer(fallito);
        using var lockTenuto = TestCaseCommitLock.AcquireAll(fallito.Id, new[] { percorso }); // il percorso è occupato: il commit va in timeout

        Assert.Throws<TestCaseSaveLockTimeoutException>(() =>
            postazioneA.TestCases.Save(fallito, isNew: true, null, TimeSpan.FromMilliseconds(200)));

        // Nessun file creato e claim rilasciato: un altro PC può usare lo stesso ID senza che venga saltato.
        var riprova = NuovoTest("T-AB-0005", "Componente-X");
        postazioneB.TestCases.Save(riprova, isNew: true);
        Assert.Equal("T-AB-0005", riprova.Id);
    }

    [Fact]
    public void PercorsoClaim_RaggruppaPerBloccoDiProgressivi_NessunaCartellaSuperaLaDimensioneShard()
    {
        var perCartella = Enumerable.Range(1, 1200)
            .Select(n => TestCaseIdClaim.PercorsoClaim(Percorsi, $"T-AB-{n:D4}"))
            .GroupBy(percorso => Path.GetDirectoryName(percorso)!)
            .ToList();

        Assert.Equal(3, perCartella.Count); // blocchi 0000 (1-499), 0001 (500-999), 0002 (1000-1200)
        Assert.All(perCartella, gruppo => Assert.True(gruppo.Count() <= TestCaseIdClaim.DimensioneShard));
        Assert.EndsWith(Path.Combine("testcase", "0001", "T-AB-0500.claim"), TestCaseIdClaim.PercorsoClaim(Percorsi, "T-AB-0500"));
        Assert.Equal("_altri", TestCaseIdClaim.BloccoPer("CUSTOM-1"));
    }

    [Fact]
    public void TryPrenota_CreaIlClaimNelBloccoDelLID()
    {
        var claim = TestCaseIdClaim.TryPrenota(Percorsi, "T-AB-0600", Guid.NewGuid());
        Assert.NotNull(claim);
        claim!.Conferma();

        var percorso = TestCaseIdClaim.PercorsoClaim(Percorsi, "T-AB-0600");
        Assert.True(File.Exists(percorso));
        Assert.Equal("0001", Path.GetFileName(Path.GetDirectoryName(percorso)));
    }

    [Fact]
    public void PulisciScaduti_RimuoveSoloIClaimScadutiConTestEsistente()
    {
        var adesso = DateTime.UtcNow;
        var scadutoConTest = CreaClaimConEta("T-AB-0100", adesso.AddDays(-8));
        var scadutoSenzaTest = CreaClaimConEta("T-AB-0101", adesso.AddDays(-8));
        var recenteConTest = CreaClaimConEta("T-AB-0102", adesso.AddDays(-6));
        var esistenti = new HashSet<string>(StringComparer.Ordinal) { "T-AB-0100", "T-AB-0102" };

        var rimossi = TestCaseIdClaim.PulisciScaduti(Percorsi, esistenti.Contains, adesso);

        Assert.Equal(1, rimossi);
        Assert.False(File.Exists(scadutoConTest));
        Assert.True(File.Exists(scadutoSenzaTest)); // nessun Test: possibile salvataggio interrotto, il claim si tiene
        Assert.True(File.Exists(recenteConTest));   // più recente di sette giorni: si tiene
    }

    [Fact]
    public void ClaimRecente_NonVieneRimossoEUnaCollisioneRestaRilevata()
    {
        var adesso = DateTime.UtcNow;
        CreaClaimConEta("T-AB-0200", adesso.AddDays(-1));

        // Anche con il Test esistente conta solo l'età: un claim recente resta, e un secondo PC non ottiene lo stesso ID.
        var rimossi = TestCaseIdClaim.PulisciScaduti(Percorsi, _ => true, adesso);

        Assert.Equal(0, rimossi);
        Assert.Null(TestCaseIdClaim.TryPrenota(Percorsi, "T-AB-0200", Guid.NewGuid()));
    }

    [Fact]
    public void PuliziaDovuta_AlPiuUnaVoltaAlGiornoPerPc()
    {
        var adesso = DateTime.UtcNow;
        Assert.True(TestCaseIdClaim.PuliziaDovuta(null, adesso));
        Assert.False(TestCaseIdClaim.PuliziaDovuta(adesso.AddHours(-23), adesso));
        Assert.True(TestCaseIdClaim.PuliziaDovuta(adesso.AddHours(-25), adesso));
    }

    [Fact]
    public async Task SalvataggioDiUnNuovoTest_RimuoveIClaimScadutiUnaSolaVoltaAlGiorno()
    {
        var postazioneA = CreaPostazione("A");
        var postazioneB = CreaPostazione("B");

        // Il Test X lo crea B e il suo claim invecchia; A, dopo il refresh, vede X e fa la pulizia di oggi.
        postazioneB.TestCases.Save(NuovoTest("T-AB-0400", "BCM"), isNew: true);
        await AttendiPulizia(postazioneB);
        var claimX = TestCaseIdClaim.PercorsoClaim(postazioneA.Paths, "T-AB-0400");
        File.SetLastWriteTimeUtc(claimX, DateTime.UtcNow.AddDays(-8));
        postazioneA.TestCases.RefreshIndex();
        // L'ultima pulizia del PC risale a due giorni fa: la pulizia è dovuta.
        TestCaseIdClaim.RegistraUltimaPulizia(postazioneA.TestCases.LocalWorkspaceDirectory, DateTime.UtcNow.AddDays(-2));

        postazioneA.TestCases.Save(NuovoTest("T-AB-0401", "BCM"), isNew: true);
        await AttendiPulizia(postazioneA);
        Assert.False(File.Exists(claimX));
        var claimY = TestCaseIdClaim.PercorsoClaim(postazioneA.Paths, "T-AB-0401");
        Assert.True(File.Exists(claimY)); // il claim appena creato resta

        // Stessa giornata: anche con un claim scaduto la pulizia non riparte.
        File.SetLastWriteTimeUtc(claimY, DateTime.UtcNow.AddDays(-8));
        var taskDiOggi = postazioneA.TestCases.PuliziaClaimTask;
        postazioneA.TestCases.Save(NuovoTest("T-AB-0402", "BCM"), isNew: true);
        Assert.Same(taskDiOggi, postazioneA.TestCases.PuliziaClaimTask);
        Assert.True(File.Exists(claimY));
    }

    /// <summary>Crea un claim confermato e ne imposta la data di ultima scrittura (cioè l'età del claim).</summary>
    private string CreaClaimConEta(string id, DateTime ultimaScritturaUtc)
    {
        using (var claim = TestCaseIdClaim.TryPrenota(Percorsi, id, Guid.NewGuid()))
        {
            Assert.NotNull(claim);
            claim!.Conferma();
        }
        var percorso = TestCaseIdClaim.PercorsoClaim(Percorsi, id);
        File.SetLastWriteTimeUtc(percorso, ultimaScritturaUtc);
        return percorso;
    }

    private static Task AttendiPulizia(AppState postazione) => postazione.TestCases.PuliziaClaimTask ?? Task.CompletedTask;
}
