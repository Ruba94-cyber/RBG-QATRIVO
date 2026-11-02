using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Ricerca globale per dominio (ciclo prestazioni 6.6.1, wt-p661-opt-ricerca). Le letture senza copia di NC e Segnalazioni
/// danno gli stessi elementi, nello stesso ordine, della lettura con copia; il perimetro dell'operatore Standard vale per i Test
/// e per le segnalazioni, anche con ruolo grezzo o effettivo diverso; il catalogo Veicoli si legge una volta per ricerca, non
/// per segnalazione; una ricerca superata non legge piu' i domini successivi.
///
/// Riferimento: la regola precedente, applicata con GetAll() (copia) e con la verifica per Test di AppState.
/// Stessa collezione dei banchi: i contatori PerfContatori sono globali e la collezione non gira in parallelo con le altre.
/// </summary>
[Collection("Scale performance")]
public sealed class GlobalSearchPerDominioTests : IDisposable
{
    private const string GammaA = "Gamma A";
    private const string GammaB = "Gamma B";
    private const string Pin = "1234";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_GlobalSearchDominio_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    /// <summary>Workspace con 12 Test (pari su Truck Alpha di Gamma A, dispari su Truck Beta di Gamma B), NC e segnalazioni
    /// da classificare (una su tre esclusa, una su cinque gia' classificata: non devono comparire).</summary>
    private AppState Stato(int segnalazioni = 24)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Veicoli.Save([
            new() { Nome = "Truck Alpha", GammaNome = GammaA },
            new() { Nome = "Truck Beta", GammaNome = GammaB },
        ]);
        state.Master.Operatori.Save([
            TestOperatori.Sigilla(new Operatore { Nome = "Master Dominio", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash(Pin) }),
            TestOperatori.Sigilla(new Operatore
            {
                Nome = "Standard Dominio", Ruolo = RuoloOperatore.Standard, GammeAssegnate = [GammaA], PinHash = PinService.Hash(Pin),
            }),
            // Ruolo grezzo Standard senza sigillo: il ruolo effettivo e' Viewer, il grezzo resta Standard.
            new() { Nome = "Standard Grezzo", Ruolo = RuoloOperatore.Standard, GammeAssegnate = [GammaA], PinHash = PinService.Hash(Pin) },
        ]);

        var tests = Enumerable.Range(0, 12).Select(CreaTest).ToList();
        foreach (var t in tests) state.TestCases.Save(t, isNew: true);
        for (var k = 0; k < 24; k++)
        {
            var nc = new NonConformita
            {
                Id = $"NC-{k:D2}", TestCaseId = tests[k % tests.Count].Id,
                Descrizione = k % 3 == 0 ? $"Anomalia banco {k}" : $"Difetto {k}", Owner = k % 2 == 0 ? "Mario" : "Luca",
            };
            state.NonConformita.Save(nc);
        }
        for (var k = 0; k < segnalazioni; k++)
        {
            var test = tests[k % tests.Count];
            var condizione = test.Esecuzioni[0].Condizioni[k % 3];
            state.Segnalazioni.Save(new SegnalazioneDaClassificare
            {
                Uid = Guid.NewGuid(),
                TestId = test.Id,
                CondizioneUid = condizione.Uid,
                EsecuzioneIterazione = 1,
                Stato = k % 5 == 4 ? StatoSegnalazione.Classificata : k % 3 == 2 ? StatoSegnalazione.Esclusa : StatoSegnalazione.DaClassificare,
                CreataIl = new DateTime(2026, 3, 1).AddMinutes(k),
                CreataDa = "Seed",
            });
        }
        state.RicaricaTutto();
        return state;
    }

    private static TestCase CreaTest(int i)
    {
        var condizioni = Enumerable.Range(0, 3).Select(c => new Condizione
        {
            Funzionalita = $"FUNZ-{i}-{c}", Componente = $"Componente-{(i + c) % 4}",
            DescrizioneProblema = $"Problema {i}-{c}", Nota = $"Nota {i}-{c}",
        }).ToList();
        return new TestCase
        {
            Id = $"TEST-{i:D2}", Veicolo = i % 2 == 0 ? "Truck Alpha" : "Truck Beta", Componente = $"Componente-{i % 4}",
            Descrizione = "Banco ricerca",
            Esecuzioni = new List<Esecuzione> { new() { Iterazione = 1, Condizioni = condizioni } },
        };
    }

    private static bool Match(string q, params string?[] valori) =>
        valori.Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Riferimento NC: lettura con copia (GetAll), come prima della modifica.</summary>
    private static List<string> RiferimentoNc(AppState s, string q) =>
        s.NonConformita.GetAll().Where(n => Match(q, n.Id, n.TestCaseId, n.Descrizione, n.Owner)).Take(8).Select(n => n.Id).ToList();

    /// <summary>Riferimento segnalazioni: GetAll (copia) e verifica di visibilita' per Test di AppState, come prima della modifica.</summary>
    private static List<Guid> RiferimentoSegnalazioni(AppState s, string q)
    {
        var uids = new List<Guid>();
        foreach (var segnalazione in s.Segnalazioni.GetAll().Where(x => x.Stato is StatoSegnalazione.DaClassificare or StatoSegnalazione.Esclusa))
        {
            var test = segnalazione.TestUid is { } testUid ? s.TestCases.GetByUid(testUid) : s.TestCases.GetById(segnalazione.TestId);
            if (test == null || test.Eliminato || !s.TestCaseVisibilePerOperatore(test)) continue;
            var esecuzione = test.Esecuzioni.FirstOrDefault(e => e.Iterazione == segnalazione.EsecuzioneIterazione);
            var condizione = esecuzione?.CondizioniEffettive.FirstOrDefault(c => c.Uid == segnalazione.CondizioneUid);
            var componente = condizione?.Componente.ToString() ?? "";
            if (!Match(q, test.Id, segnalazione.TestId, segnalazione.Uid.ToString("D"), segnalazione.Uid.ToString("N"),
                    segnalazione.Stato.ToString(), segnalazione.MotivazioneEsclusione, condizione?.Funzionalita,
                    condizione?.DescrizioneProblema, condizione?.Nota, componente)) continue;
            uids.Add(segnalazione.Uid);
            if (uids.Count >= 8) break;
        }
        return uids;
    }

    private static readonly string[] Termini = { "Anomalia banco", "NC-0", "Luca", "Problema", "FUNZ-1", "Nota 3", "Banco ricerca", "inesistente" };

    [Fact]
    public void NonConformita_SenzaCopia_StessoElencoEStessoOrdineDellaLetturaConCopia()
    {
        var state = Stato();
        Assert.True(state.ImpostaOperatore("Master Dominio", Pin));
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);

        var trovate = 0;
        foreach (var q in Termini)
        {
            vm.Query = q;
            var attesi = RiferimentoNc(state, q);
            // Le NC mostrano l'id nel titolo (la riga non ha ElementoId).
            var ottenuti = vm.Risultati.Where(r => r.Destinazione == "anomalie").Select(r => r.Titolo).ToList();
            Assert.Equal(attesi, ottenuti);
            trovate += ottenuti.Count;
        }
        Assert.True(trovate > 0, "Il confronto delle NC deve avere risultati, altrimenti non prova nulla.");
    }

    [Theory]
    [InlineData("Master Dominio")]
    [InlineData("Standard Dominio")]
    public void Segnalazioni_SenzaCopia_StessoElencoEStessoOrdineDellaVerificaPerTest(string operatore)
    {
        var state = Stato();
        Assert.True(state.ImpostaOperatore(operatore, Pin));
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);

        var trovate = 0;
        foreach (var q in Termini)
        {
            vm.Query = q;
            var attesi = RiferimentoSegnalazioni(state, q);
            var ottenuti = vm.Risultati.Where(r => r.Destinazione == "segnalazione").Select(r => r.ElementoUid!.Value).ToList();
            Assert.Equal(attesi, ottenuti);
            trovate += ottenuti.Count;
            if (operatore == "Standard Dominio")
            {
                // Perimetro: nessun risultato di segnalazione su un Test fuori da Gamma A.
                foreach (var id in vm.Risultati.Where(r => r.Destinazione == "segnalazione").Select(r => r.ElementoId!))
                    Assert.Contains("Truck Alpha", state.TestCases.GetById(id)!.VeicoliEffettivi);
            }
        }
        Assert.True(trovate > 0, "Il confronto delle segnalazioni deve avere risultati, altrimenti non prova nulla.");
    }

    [Fact]
    public void Standard_RuoloGrezzoSenzaSigillo_LaRicercaRestaNelPerimetroDiGammaA()
    {
        var state = Stato();
        Assert.True(state.ImpostaOperatore("Standard Grezzo", Pin));
        // Il ruolo effettivo e' Viewer (senza sigillo): la lettura grezza Standard deve comunque restringere la ricerca.
        Assert.Equal(RuoloOperatore.Viewer, state.OperatoreCorrente!.RuoloEffettivo);
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);

        vm.Query = "Banco ricerca";
        var testTrovati = vm.Risultati.Where(r => r.Destinazione == "test").Select(r => r.ElementoId!).ToList();
        var segnalazioniTrovate = vm.Risultati.Where(r => r.Destinazione == "segnalazione").Select(r => r.ElementoId!).ToList();

        Assert.NotEmpty(testTrovati);
        Assert.All(testTrovati, id => Assert.Contains("Truck Alpha", state.TestCases.GetById(id)!.VeicoliEffettivi));
        Assert.All(segnalazioniTrovate, id => Assert.Contains("Truck Alpha", state.TestCases.GetById(id)!.VeicoliEffettivi));
    }

    [Fact]
    public async Task Standard_IlCatalogoVeicoliSiLeggeUnaVoltaPerRicercaNonPerSegnalazione()
    {
        // Query senza risultati: il ciclo sulle segnalazioni aperte non si ferma mai presto (visita tutte le aperte).
        const string senzaRisultati = "zzz-nessun-risultato";
        var state = Stato(segnalazioni: 60);
        Assert.True(state.ImpostaOperatore("Standard Dominio", Pin));
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);
        await vm.AttendiRefreshInCorsoAsync(); // il refresh dell'indice avviato dal costruttore non deve sommarsi alla misura
        vm.Query = "Banco ricerca"; // primo giro: cache pronte

        PerfContatori.Reset();
        vm.Query = senzaRisultati;
        var caricamentiMaster = PerfContatori.LeggiSnapshot().MasterLoad;

        Assert.Empty(vm.Risultati);
        // Con 60 segnalazioni aperte, una lettura per segnalazione darebbe decine di caricamenti. Il conteggio atteso e' una
        // costante piccola: Test (perimetro), Segnalazioni (perimetro), Veicoli e Strumenti (fallback).
        Assert.True(caricamentiMaster < 10, $"Caricamenti master per ricerca: {caricamentiMaster}.");
    }

    [Fact]
    public async Task RicercaSuperata_NonLeggeIDominiSuccessivi()
    {
        var state = Stato();
        Assert.True(state.ImpostaOperatore("Standard Dominio", Pin));
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);
        await vm.AttendiRefreshInCorsoAsync();
        using var annullata = new CancellationTokenSource();
        annullata.Cancel();

        PerfContatori.Reset();
        var risultatoAnnullato = vm.CalcolaRisultatiPerTest("Problema", annullata.Token);
        var caricamentiAnnullata = PerfContatori.LeggiSnapshot().MasterLoad;

        PerfContatori.Reset();
        var risultatoCompleto = vm.CalcolaRisultatiPerTest("Problema", CancellationToken.None);
        var caricamentiCompleta = PerfContatori.LeggiSnapshot().MasterLoad;

        Assert.Empty(risultatoAnnullato);
        Assert.Equal(0, caricamentiAnnullata);
        Assert.NotEmpty(risultatoCompleto);
        Assert.True(caricamentiCompleta > 0);
    }
}
