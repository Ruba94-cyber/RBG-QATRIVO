using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressione mirata dell'Addendum prestazioni 2026-09-25 (Home a 100k Test/NC/WU): <see cref="HomeViewModel.Aggiorna"/>
/// consolida componente, KPI testata, copertura requisiti, filtri custom e Da fare/Fatti in UNA sola lettura indicizzata
/// (<see cref="LocalTestArchiveQueryService.GetHomeAggiornaBatch"/>) invece di quattro-cinque connessioni SQLite
/// separate. Questi test verificano che il consolidamento non abbia cambiato la semantica di recovery (fallback
/// identico al percorso non indicizzato quando l'indice e' corrotto o non disponibile, stessa invalidazione per
/// generazione) ne' la visibilita' delle scritture di un'altra postazione sulla stessa cartella condivisa.
/// </summary>
[Trait("Category", "Lento")]
public sealed class HomeAggiornaScalabileTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public HomeAggiornaScalabileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeScalabile_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
    }

    public void Dispose()
    {
        _state.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Il seed generale include intenzionalmente un JSON corrotto per altri test di sicurezza: qui va
    /// rimosso perche' il recupero deve poter offrire una sorgente autorevole COMPLETA, altrimenti il fail-closed
    /// corretto (nessun indice parziale marcato pronto) impedirebbe per contratto la ricostruzione.</summary>
    private void RimuoviJsonCorrottoDelSeed()
    {
        foreach (var path in Directory.GetFiles(_dir, "testcase_CORROTTO-TEST-99.json", SearchOption.AllDirectories))
            File.Delete(path);
        _state.TestCases.RefreshIndex();
    }

    [Fact]
    public void Aggiorna_IndiceSano_UsaIlBatchUnificatoEDaRisultatiIdenticiAlPercorsoNonIndicizzato()
    {
        RimuoviJsonCorrottoDelSeed();
        Assert.True(_state.TestCases.HasPersistentSnapshot);

        var attesi = LegacyKpi(_state);

        var home = new HomeViewModel(_state, _ => { });

        Assert.Equal(attesi.TotaleTestCase, home.TotaleTestCase);
        Assert.Equal(attesi.DaRitestare, home.DaRitestare);
        Assert.Equal(attesi.TestSenzaEcu, home.TestSenzaEcu);
        Assert.Equal(attesi.DaClassificare, home.DaClassificare);
        Assert.Equal(attesi.TotaleNcAperte, home.TotaleNcAperte);
        Assert.Equal(attesi.CoperturaRequisitiPercentuale, home.CoperturaRequisitiPercentuale, 6);
    }

    /// <summary>Corruzione dell'indice DOPO l'apertura: la stessa rivisita deve cadere per intero sul percorso
    /// JSON (nessun ibrido fra le quattro sezioni del batch) e produrre lo stesso risultato del calcolo legacy;
    /// il coordinatore deve comunque ricostruire l'indice da solo, come sul percorso a query separate.</summary>
    [Fact]
    public void Aggiorna_IndiceCorrottoDuranteLaLettura_RicadeSulPercorsoJsonERicostruisce()
    {
        RimuoviJsonCorrottoDelSeed();
        var home = new HomeViewModel(_state, _ => { });
        var attesi = LegacyKpi(_state);

        var coordinatore = Assert.IsType<LocalIndexRecoveryCoordinator>(_state.TestCases.IndexRecovery);
        var ricostruzioniPrima = coordinatore.RicostruzioniEseguite;
        var database = Assert.IsType<string>(_state.TestCases.LocalIndexDatabasePath);

        IndiceSqliteCorruzioneTestHelper.CheckpointWalEChiudiPool(database);
        using (var file = new FileStream(database, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            file.Position = 0;
            file.Write(new byte[100]);
            file.Flush(flushToDisk: true);
        }

        home.Aggiorna();

        Assert.True(coordinatore.AttendiRecupero(TimeSpan.FromSeconds(15)), coordinatore.Motivo);
        Assert.Equal(LocalIndexHealth.Healthy, coordinatore.Salute);
        Assert.Equal(ricostruzioniPrima + 1, coordinatore.RicostruzioniEseguite);

        Assert.Equal(attesi.TotaleTestCase, home.TotaleTestCase);
        Assert.Equal(attesi.DaRitestare, home.DaRitestare);
        Assert.Equal(attesi.TestSenzaEcu, home.TestSenzaEcu);
        Assert.Equal(attesi.CoperturaRequisitiPercentuale, home.CoperturaRequisitiPercentuale, 6);
    }

    /// <summary>Una scrittura da una SECONDA postazione (stessa cartella condivisa, AppState indipendente) deve
    /// comparire alla rivisita successiva della prima: il batch unificato non deve introdurre alcuna cache
    /// aggiuntiva basata su data/dimensione file oltre a quella gia' invalidata per generazione dal coordinatore.</summary>
    [Fact]
    public void Aggiorna_DopoScritturaDiSecondaPostazione_MostraIlNuovoTestEIlNuovoEcu()
    {
        RimuoviJsonCorrottoDelSeed();
        var home = new HomeViewModel(_state, _ => { });
        var totalePrima = home.TotaleTestCase;
        Assert.DoesNotContain(home.FiltroEcuDashboard.Opzioni, o => o.Valore == "Componente-SECONDA-POSTAZIONE");

        var configService2 = new AppConfigService(Path.Combine(_dir + "_local_B", "config.json"));
        var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        stato2.InizializzaCartella(_dir);
        stato2.TestCases.Save(new TestCase
        {
            Id = "T-HOME-SECONDA-POSTAZIONE",
            Componente = { "Componente-SECONDA-POSTAZIONE" },
            ModificatoDa = "Seconda Postazione",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);
        stato2.Dispose();

        _state.TestCases.RefreshIndex();
        home.Aggiorna();

        Assert.Equal(totalePrima + 1, home.TotaleTestCase);
        Assert.Contains(home.FiltroEcuDashboard.Opzioni, o => o.Valore == "Componente-SECONDA-POSTAZIONE");
    }

    /// <summary>Addendum prestazioni 2026-09-26: GetRequirementCoverageCore ora cache il bucket per
    /// (Generazione, PRAGMA data_version, requisiti, soloBanco, soloVeicolo). Una scrittura da una seconda
    /// postazione che collega un Test conforme a un requisito prima non testato deve cambiare la copertura
    /// mostrata alla rivisita successiva della prima, non restituire il bucket cachato prima della scrittura —
    /// stesso invariante del test componente sopra, ma sul percorso che introduce la cache.</summary>
    [Fact]
    public void Aggiorna_DopoScritturaDiSecondaPostazioneConNuovoRequisitoCoperto_AggiornaLaCoperturaRequisiti()
    {
        RimuoviJsonCorrottoDelSeed();

        // Requisito NUOVO (mai referenziato da alcun Test del seed): a differenza di cercarne uno "libero" nel
        // seed generato casualmente, questo garantisce deterministicamente la transizione NonTestati -> Conforme
        // usata sotto, senza dipendere da quanti requisiti il seed lascia scoperti in questa esecuzione.
        var nuovoRequisito = new Requisito { Id = "REQ-CACHE-HOME-SECONDA-POSTAZIONE", ApplicabileBanco = true, ApplicabileVeicolo = true };
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(nuovoRequisito).ToList());

        var home = new HomeViewModel(_state, _ => { });
        var coperturaPrima = home.CoperturaRequisitiPercentuale;

        var configService2 = new AppConfigService(Path.Combine(_dir + "_local_C", "config.json"));
        var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        stato2.InizializzaCartella(_dir);
        stato2.TestCases.Save(new TestCase
        {
            Id = "T-HOME-SECONDA-POSTAZIONE-REQ",
            Componente = { "Componente-SECONDA-POSTAZIONE-REQ" },
            ModificatoDa = "Seconda Postazione",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
            RequisitoId = new List<string> { nuovoRequisito.Id },
            Esecuzioni = new()
            {
                new Esecuzione
                {
                    Iterazione = 1,
                    Data = DateTime.Now,
                    Operatore = "Seconda Postazione",
                    Esito = Esito.Conforme,
                    Condizioni = new() { new Condizione { Esito = Esito.Conforme } }
                }
            }
        }, isNew: true);
        stato2.Dispose();

        _state.TestCases.RefreshIndex();
        home.Aggiorna();

        var attesi = LegacyKpi(_state);
        Assert.NotEqual(coperturaPrima, home.CoperturaRequisitiPercentuale);
        Assert.Equal(attesi.CoperturaRequisitiPercentuale, home.CoperturaRequisitiPercentuale, 6);
    }

    /// <summary>Stesso identico calcolo del ramo non indicizzato di AggiornaKpiTestata/Aggiorna, usato come
    /// riferimento indipendente dal percorso scalabile per verificare che il batch unificato non abbia cambiato
    /// alcun risultato.</summary>
    private static (long TotaleTestCase, long DaRitestare, long TestSenzaEcu, long DaClassificare, long TotaleNcAperte, double CoperturaRequisitiPercentuale) LegacyKpi(AppState state)
    {
        var tutti = state.TestCases.GetAll();
        var contati = tutti.Where(tc => tc.Origine != OrigineTest.ImportStatoUdt).ToList();
        long testati = 0, conformi = 0, senzaEcu = 0, daRitestare = 0;
        foreach (var tc in contati)
        {
            if (tc.StatoLavoro == StatoLavoroTest.DaRitestare) daRitestare++;
            if (tc.Componente.Count == 0) senzaEcu++;
            if (tc.StatoCorrente.HasValue) { testati++; if (tc.StatoCorrente == Esito.Conforme) conformi++; }
        }
        var coperturaPct = CoverageService.PerRequisito(state.Master.Requisiti.Load(), tutti, soloBanco: false, soloVeicolo: false).PercentualeCompletamento;
        var totaleNcAperte = state.NonConformita.CountByState(StatoNonConformita.Aperta);
        var daClassificare = state.Segnalazioni.CountByState(StatoSegnalazione.DaClassificare);
        return (contati.LongCount(), daRitestare, senzaEcu, daClassificare, totaleNcAperte, coperturaPct);
    }
}
