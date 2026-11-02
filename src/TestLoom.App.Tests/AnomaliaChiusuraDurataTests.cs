using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1), review finding 7: la chiusura di un'anomalia che crea una nuova REV con prove eseguite dichiara la durata
/// (proposta dalla regola di copia nel dialogo) e, con la politica Master "durata obbligatoria", non salva una prova eseguita senza durata.</summary>
[Collection("Localization")]
public sealed class AnomaliaChiusuraDurataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AnomDurata_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Giorno = new(2026, 9, 1);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    private static void AttivaPoliticaObbligatoria(AppState state)
    {
        var politica = PoliticaDurataService.Load(state.Paths);
        politica.DurataObbligatoria = true;
        PoliticaDurataService.Save(state.Paths, politica, true);
        state.RicaricaTutto();
    }

    private static Condizione Op(string funzionalita, string componente, Esito esito, int? minuti) => new()
    {
        Funzionalita = funzionalita,
        Esito = esito,
        Componente = new EcuSelection([componente]),
        DurataMinuti = minuti,
    };

    /// <summary>Test T-1 con Diagnosi/BCM non conforme (anomalia NC-0001) e Lettura/BCM conforme, come nel dialogo di chiusura.</summary>
    private static (Guid C1, Guid C2) CreaTestENc(AppState state, string ncId = "NC-0001")
    {
        var c1 = Op("Diagnosi", "BCM", Esito.NonConforme, null);
        var c2 = Op("Lettura", "BCM", Esito.Conforme, null);
        var rev = new Esecuzione { Iterazione = 1, Data = Giorno, Operatore = "Mario", Matricola = "Matricola1", Condizioni = { c1, c2 } };
        rev.SincronizzaEsito();
        state.TestCases.Save(new TestCase
        {
            Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" }, Assegnatario = "Luigi",
            StatoLavoro = StatoLavoroTest.InCorso, Esecuzioni = new List<Esecuzione> { rev },
        }, isNew: true);
        state.NonConformita.Save(new NonConformita
        {
            Id = ncId, TestCaseId = "T-1", EsecuzioneIterazione = 1, Owner = "Mario", Descrizione = "KO",
            Severita = SeveritaAnomalia.Medium, StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now,
            ProveCollegate = new() { new RiferimentoProva { TestId = "T-1", EsecuzioneIterazione = 1, CondizioneUid = c1.Uid } },
        });
        return (c1.Uid, c2.Uid);
    }

    /// <summary>Test e anomalia in memoria (nessuna persistenza) per la regola della chiusura.</summary>
    private static (TestCase Tc, NonConformita Nc, Guid C1) TestEAnomaliaInMemoria()
    {
        var c1 = Op("Diagnosi", "BCM", Esito.NonConforme, null);
        var rev = new Esecuzione { Iterazione = 1, Data = Giorno, Operatore = "Mario", Matricola = "Matricola1", Condizioni = { c1 } };
        rev.SincronizzaEsito();
        var tc = new TestCase
        {
            Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" }, Assegnatario = "Luigi",
            StatoLavoro = StatoLavoroTest.InCorso, Esecuzioni = new List<Esecuzione> { rev },
        };
        var nc = new NonConformita
        {
            Id = "NC-0001", TestCaseId = "T-1", EsecuzioneIterazione = 1, Owner = "Mario", Descrizione = "KO",
            Severita = SeveritaAnomalia.Medium, StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now,
        };
        return (tc, nc, c1.Uid);
    }

    private static AnomaliaDettaglioViewModel Apri(AppState state) =>
        new(state, state.NonConformita.GetById("NC-0001")!);

    // ----- Servizio: durate dichiarate e guardia della politica -----

    [Fact]
    public void ChiudiConNuovaRev_DurataDichiarata_SiRiportaSullaNuovaProva()
    {
        var (tc, nc, c1) = TestEAnomaliaInMemoria();
        var durate = new Dictionary<Guid, int?> { [c1] = 12 };

        var risultato = AnomaliaChiusuraService.ChiudiConNuovaRev(tc, nc, new Dictionary<Guid, Esito> { [c1] = Esito.Conforme },
            null, OrigineRevisione.Rete, "Luigi", DateTime.Now, "Risolta", durate, durataObbligatoria: true);

        var prova = risultato.NuovaRev!.Condizioni.Single(c => c.Esito == Esito.Conforme);
        Assert.Equal(12, prova.DurataMinuti);
        Assert.Equal(2, tc.Esecuzioni.Count);
    }

    [Fact]
    public void ChiudiConNuovaRev_PoliticaObbligatoria_ProvaEseguitaSenzaDurata_NonSalvaNulla()
    {
        var (tc, nc, c1) = TestEAnomaliaInMemoria();

        Assert.Throws<InvalidOperationException>(() => AnomaliaChiusuraService.ChiudiConNuovaRev(tc, nc,
            new Dictionary<Guid, Esito> { [c1] = Esito.Conforme }, null, OrigineRevisione.Rete, "Luigi", DateTime.Now, "Risolta",
            null, durataObbligatoria: true));

        Assert.Single(tc.Esecuzioni);                                    // nessuna REV aggiunta
        Assert.NotEqual(StatoAnomalia.Conclusa, nc.StatoWorkflow);        // nessuna chiusura dell'anomalia
    }

    [Fact]
    public void ChiudiConNuovaRev_PoliticaObbligatoria_EsitoNonEseguito_NonRichiedeLaDurata()
    {
        var (tc, nc, c1) = TestEAnomaliaInMemoria();

        var risultato = AnomaliaChiusuraService.ChiudiConNuovaRev(tc, nc,
            new Dictionary<Guid, Esito> { [c1] = Esito.NonApplicabile }, null, OrigineRevisione.Rete, "Luigi", DateTime.Now, "Risolta",
            null, durataObbligatoria: true);

        Assert.NotNull(risultato.NuovaRev);
        Assert.Null(risultato.NuovaRev!.Condizioni.Single(c => c.Esito == Esito.NonApplicabile).DurataMinuti);
    }

    [Fact]
    public void ChiudiConNuovaRev_SenzaPolitica_LaDurataRestaFacoltativa()
    {
        var (tc, nc, c1) = TestEAnomaliaInMemoria();

        var risultato = AnomaliaChiusuraService.ChiudiConNuovaRev(tc, nc,
            new Dictionary<Guid, Esito> { [c1] = Esito.Conforme }, null, OrigineRevisione.Rete, "Luigi", DateTime.Now, "Risolta");

        Assert.Null(risultato.NuovaRev!.Condizioni.Single(c => c.Esito == Esito.Conforme).DurataMinuti);
    }

    [Fact]
    public void ChiudiESalva_PoliticaObbligatoria_SenzaDurata_NonScriveNeTestNeAnomalia()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        AttivaPoliticaObbligatoria(state);

        Assert.ThrowsAny<Exception>(() => AnomaliaChiusuraService.ChiudiESalva(state, "T-1", "NC-0001",
            new Dictionary<Guid, Esito> { [c1] = Esito.Conforme }, null, OrigineRevisione.Rete, "Luigi", DateTime.Now, "Risolta"));

        Assert.Single(state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!.Esecuzioni);
        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public void ChiudiESalva_PoliticaObbligatoria_ConDurata_SalvaLaNuovaProva()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        AttivaPoliticaObbligatoria(state);

        AnomaliaChiusuraService.ChiudiESalva(state, "T-1", "NC-0001",
            new Dictionary<Guid, Esito> { [c1] = Esito.Conforme }, null, OrigineRevisione.Rete, "Luigi", DateTime.Now, "Risolta",
            durataPerCondizione: new Dictionary<Guid, int?> { [c1] = 31 });

        var riletto = state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!;
        Assert.Equal(2, riletto.Esecuzioni.Count);
        Assert.Equal(31, riletto.Esecuzioni[^1].Condizioni.Single(c => c.Funzionalita == "Diagnosi" && c.Esito == Esito.Conforme).DurataMinuti);
    }

    // ----- Dialogo: proposta dalla regola di copia, blocco con messaggio -----

    [Fact]
    public async Task Chiudi_NuovaProvaEseguita_ProponeLaDurataDallaRegolaDiCopia()
    {
        var state = CreaStato();
        // Un Test precedente con Diagnosi/BCM conforme in 25 minuti: e' la proposta per la nuova prova.
        var precedente = new TestCase
        {
            Id = "T-PRECEDENTE", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" }, Funzionalita = ["Diagnosi"],
            DataCreazione = Giorno.AddDays(-3), UltimaModifica = Giorno.AddDays(-3), Assegnatario = "Luigi",
        };
        precedente.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = Giorno.AddDays(-3), Operatore = "Mario", Condizioni = { Op("Diagnosi", "BCM", Esito.Conforme, 25) } });
        state.TestCases.Save(precedente, isNew: true);
        var (c1, _) = CreaTestENc(state);
        var vm = Apri(state);
        vm.PubblicaRisultatoCopia = azione => azione();
        var riga = vm.Condizioni.Single(c => c.Uid == c1);

        riga.EsitoNuovo = Esito.Conforme;
        await vm.AttendiCopieDurataInCorso();

        Assert.True(riga.MostraDurata);
        Assert.Equal("25", riga.DurataTesto);            // proposta, non una misura: resta modificabile
        vm.OrigineSelezionata = vm.OpzioniOrigine.Single(o => o.Valore == OrigineRevisione.Rete);
        vm.MotivoChiusura = "Risolta in rete";
        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Null(vm.Errore);
        var riletto = state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!;
        Assert.Equal(25, riletto.Esecuzioni[^1].Condizioni.Single(c => c.Funzionalita == "Diagnosi" && c.Esito == Esito.Conforme).DurataMinuti);
    }

    [Fact]
    public async Task Chiudi_PoliticaObbligatoria_SenzaDurata_BloccaConMessaggioESenzaChiusura()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        AttivaPoliticaObbligatoria(state);
        var vm = Apri(state);
        vm.PubblicaRisultatoCopia = azione => azione();   // nessun precedente: nessuna proposta
        vm.Condizioni.Single(c => c.Uid == c1).EsitoNuovo = Esito.Conforme;
        vm.OrigineSelezionata = vm.OpzioniOrigine.Single(o => o.Valore == OrigineRevisione.Rete);
        vm.MotivoChiusura = "Risolta";
        var chiusure = 0;
        vm.ChiusuraRichiesta += () => chiusure++;

        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationService.Instance.Traduci("ValDurataOperazioneObbl"), vm.Errore);
        Assert.Equal(0, chiusure);
        Assert.Single(state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!.Esecuzioni);
        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public async Task Chiudi_DurataNonValida_BloccaSempreConMessaggio()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        var vm = Apri(state);
        vm.PubblicaRisultatoCopia = azione => azione();
        var riga = vm.Condizioni.Single(c => c.Uid == c1);
        riga.EsitoNuovo = Esito.Conforme;
        riga.DurataTesto = "abc";
        vm.OrigineSelezionata = vm.OpzioniOrigine.Single(o => o.Valore == OrigineRevisione.Rete);
        vm.MotivoChiusura = "Risolta";

        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationService.Instance.Traduci("DurataNonValidaMsg"), vm.Errore);
        Assert.Single(state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!.Esecuzioni);
    }

    [Fact]
    public async Task Proposta_NonSovrascriveUnaDurataDigitataDallOperatore()
    {
        var state = CreaStato();
        var precedente = new TestCase
        {
            Id = "T-PRECEDENTE", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" }, Funzionalita = ["Diagnosi"],
            DataCreazione = Giorno.AddDays(-3), UltimaModifica = Giorno.AddDays(-3), Assegnatario = "Luigi",
        };
        precedente.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = Giorno.AddDays(-3), Operatore = "Mario", Condizioni = { Op("Diagnosi", "BCM", Esito.Conforme, 25) } });
        state.TestCases.Save(precedente, isNew: true);
        var (c1, _) = CreaTestENc(state);
        var vm = Apri(state);
        vm.PubblicaRisultatoCopia = azione => azione();
        var riga = vm.Condizioni.Single(c => c.Uid == c1);

        riga.DurataTesto = "7";                            // l'operatore scrive prima che la proposta arrivi
        riga.EsitoNuovo = Esito.Conforme;
        await vm.AttendiCopieDurataInCorso();

        Assert.Equal("7", riga.DurataTesto);
    }
}
