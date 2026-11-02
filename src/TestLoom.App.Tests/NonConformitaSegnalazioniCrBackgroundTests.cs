using System.Threading;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Pagina Anomalie (ciclo prestazioni 6.6.1): segnalazioni da classificare calcolate una volta per giro e paginate nel
/// ViewModel; Change Request filtrate e ordinate fuori dal thread UI; gamma letto una volta per giro. Il riferimento dei
/// test e' il calcolo diretto della regola precedente (LINQ sugli stessi dati): stessi elementi, stesso ordine, stesse pagine.
/// </summary>
public sealed class NonConformitaSegnalazioniCrBackgroundTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NcSegCr_" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Responsabili = { "Anna", "Bruno", "Carla" };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState Stato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Gamma.Save([new Gamma { Nome = "GAMMA-A", ModificatoDa = "Seed" }, new Gamma { Nome = "GAMMA-B", ModificatoDa = "Seed" }]);
        state.Master.Veicoli.Save([
            new Veicolo { Nome = "VEI-1", GammaNome = "GAMMA-A", ModificatoDa = "Seed" },
            new Veicolo { Nome = "VEI-2", GammaNome = "GAMMA-B", ModificatoDa = "Seed" },
            new Veicolo { Nome = "VEI-3", GammaNome = "GAMMA-A", ModificatoDa = "Seed" },
        ]);
        state.Master.Componente.Save([new Componente { Nome = "Componente-0", ModificatoDa = "Seed" }, new Componente { Nome = "Componente-1", ModificatoDa = "Seed" },
            new Componente { Nome = "Componente-2", ModificatoDa = "Seed" }, new Componente { Nome = "Componente-3", ModificatoDa = "Seed" }]);
        state.Master.Operatori.Save([TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master NcSeg", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Seed"
        })]);
        state.RicaricaTutto();
        Assert.True(state.ImpostaOperatore("Master NcSeg", "1234"));
        return state;
    }

    /// <summary>Test con due esecuzioni da tre condizioni. Alcune condizioni non hanno funzionalita' (numero di reiterazione).</summary>
    private static TestCase CreaTest(string id, int indice)
    {
        var esecuzioni = new List<Esecuzione>();
        for (var e = 0; e < 2; e++)
        {
            var condizioni = new List<Condizione>();
            for (var c = 0; c < 3; c++)
            {
                var numero = indice * 6 + e * 3 + c;
                condizioni.Add(new Condizione
                {
                    Funzionalita = numero % 4 == 0 ? "" : $"FUNZ-{numero}",
                    Componente = $"Componente-{numero % 4}",
                    DescrizioneProblema = $"Problema {numero}",
                    Nota = $"Nota {numero}",
                });
            }
            esecuzioni.Add(new Esecuzione { Iterazione = e + 1, Matricola = $"Matricola-{id}-{e}", Condizioni = condizioni });
        }
        return new TestCase
        {
            Id = id, Componente = $"Componente-{indice % 4}", Veicolo = "VEI-1",
            Severita = indice % 2 == 0 ? SeveritaAnomalia.High : null,
            Esecuzioni = esecuzioni,
        };
    }

    /// <summary>Segnalazioni da classificare su 12 Test: alcune puntano a un Test o a una condizione inesistenti, alcune sono gia'
    /// classificate (escluse dall'elenco).</summary>
    private static void CreaSegnalazioni(AppState s, int quante)
    {
        var tests = Enumerable.Range(0, 12).Select(i => CreaTest($"TEST-{i:D2}", i)).ToList();
        foreach (var t in tests) s.TestCases.Save(t, isNew: true);
        for (var i = 0; i < quante; i++)
        {
            var test = tests[i % tests.Count];
            var condizioni = test.Esecuzioni.SelectMany(e => e.Condizioni).ToList();
            var condizioneUid = i % 7 == 0 ? Guid.NewGuid() : condizioni[i % condizioni.Count].Uid;
            s.Segnalazioni.Save(new SegnalazioneDaClassificare
            {
                Uid = Guid.NewGuid(),
                TestId = i % 19 == 0 ? "TEST-INESISTENTE" : test.Id,
                CondizioneUid = condizioneUid,
                Stato = i % 11 == 0 ? StatoSegnalazione.Classificata : StatoSegnalazione.DaClassificare,
                CreataIl = new DateTime(2026, 3, 1).AddMinutes(i),
                CreataDa = "Seed",
            });
        }
    }

    /// <summary>Riferimento: la regola di calcolo precedente, applicata direttamente ai dati (stesso ordine di GetAll).</summary>
    private static List<(Guid Uid, string Reiterazione, string Risultato, string Titolo, string Contesto, SeveritaAnomalia? Severita)> RiferimentoSegnalazioni(AppState s)
    {
        var righe = new List<(Guid, string, string, string, string, SeveritaAnomalia?)>();
        foreach (var segnalazione in s.Segnalazioni.GetAll().Where(x => x.Stato == StatoSegnalazione.DaClassificare))
        {
            var test = s.TestCases.GetById(segnalazione.TestId);
            var esecuzione = test?.Esecuzioni.FirstOrDefault(e => e.Condizioni.Any(c => c.Uid == segnalazione.CondizioneUid));
            var condizione = esecuzione?.Condizioni.FirstOrDefault(c => c.Uid == segnalazione.CondizioneUid);
            var numero = condizione == null ? 0 : esecuzione!.Condizioni.IndexOf(condizione) + 1;
            righe.Add((segnalazione.Uid,
                condizione == null ? LocalizationService.Instance.Traduci("ReiterazioneNonDisponibile")
                    : string.IsNullOrEmpty(condizione.Funzionalita) ? string.Format(LocalizationService.Instance.Traduci("ReiterazioneNumeroFmt"), numero) : condizione.Funzionalita,
                condizione?.Nota ?? "",
                condizione == null ? "" : NonConformitaRulesService.TitoloDaOperazione(condizione.Funzionalita, condizione.Componente, condizione.DescrizioneProblema,
                    string.Format(LocalizationService.Instance.Traduci("ReiterazioneNumeroFmt"), numero)),
                test == null || esecuzione == null ? "" : $"{s.NomeTestTemplate(test.TestTemplateUid)} · {test.Componente} · matricola {esecuzione.Matricola}",
                test?.Severita));
        }
        return righe;
    }

    private static (Guid Uid, string Reiterazione, string Risultato, string Titolo, string Contesto, SeveritaAnomalia? Severita) Firma(SegnalazioneRiga r) =>
        (r.Segnalazione.Uid, r.Reiterazione, r.Risultato, r.TitoloAnomalia, r.Contesto, r.Severita);

    private static void CreaChangeRequest(AppState s, int quante)
    {
        for (var i = 0; i < quante; i++)
        {
            s.ChangeRequests.Save(new ChangeRequest
            {
                Id = $"CR-{i:D4}",
                Titolo = $"Titolo {i}",
                Descrizione = i % 3 == 0 ? "Descrizione con ticket" : "Descrizione generica",
                Responsabile = Responsabili[i % Responsabili.Length],
                Stato = (StatoChangeRequest)(i % 5),
                DataCreazione = new DateTime(2026, 1, 1).AddDays(i % 40),
                ChiusaIl = i % 4 == 3 ? new DateTime(2026, 2, 1).AddDays(i % 9) : null,
                VeicoliImpattati = new List<string> { $"VEI-{i % 3 + 1}" },
                EcuImpattate = new List<string> { $"Componente-{i % 4}" },
                FunzionalitaImpattate = new List<string> { $"FUNZ-{i % 5}" },
                TicketCollegati = i % 7 == 0 ? new List<TicketRif> { new() { Riferimento = $"TICKET-{i}" } } : new List<TicketRif>(),
            });
        }
    }

    /// <summary>Riferimento per le CR: stesso filtro e stesso ordine della regola precedente, sugli stessi dati.</summary>
    private static List<string> RiferimentoCr(AppState s, Func<ChangeRequest, bool> filtro, int pagina = 1)
    {
        var tutte = s.ChangeRequests.GetAll().Where(filtro).OrderByDescending(c => c.DataCreazione).ToList();
        return tutte.Skip((pagina - 1) * 250).Take(250).Select(c => c.Id).ToList();
    }

    [Fact]
    public void Segnalazioni_PagineEOrdineUgualiAlRiferimento()
    {
        using var s = Stato();
        CreaSegnalazioni(s, 230);
        var attese = RiferimentoSegnalazioni(s);
        var vm = new NonConformitaViewModel(s, _ => { });

        Assert.Equal(attese.Count, vm.TotaleSegnalazioni);
        Assert.Equal(3, vm.PagineTotaliSegnalazioni);
        Assert.Equal(attese.Take(100).Select(x => x.Uid), vm.Segnalazioni.Select(r => r.Segnalazione.Uid));
        Assert.Equal(attese.Take(100), vm.Segnalazioni.Select(Firma));

        vm.PaginaSegnalazioniSuccessivaCommand.Execute(null);
        Assert.Equal(attese.Skip(100).Take(100), vm.Segnalazioni.Select(Firma));
        Assert.True(vm.PuoPaginaSegnalazioniSuccessiva);
        vm.PaginaSegnalazioniSuccessivaCommand.Execute(null);
        Assert.Equal(attese.Skip(200), vm.Segnalazioni.Select(Firma));
        Assert.False(vm.PuoPaginaSegnalazioniSuccessiva);
        Assert.True(vm.HaSegnalazioni);
    }

    [Fact]
    public void Segnalazioni_ModificaNonSalvataSopravvieCambioPagina()
    {
        using var s = Stato();
        CreaSegnalazioni(s, 150);
        var vm = new NonConformitaViewModel(s, _ => { });
        var primaRiga = vm.Segnalazioni[0];
        primaRiga.Severita = SeveritaAnomalia.Low;

        vm.PaginaSegnalazioniSuccessivaCommand.Execute(null);
        vm.PrimaPaginaSegnalazioniCommand.Execute(null);

        Assert.Same(primaRiga, vm.Segnalazioni[0]);
        Assert.Equal(SeveritaAnomalia.Low, vm.Segnalazioni[0].Severita);
    }

    [Fact]
    public void Segnalazioni_SenzaElementi_ElencoVuotoENessunaPagina()
    {
        using var s = Stato();
        var vm = new NonConformitaViewModel(s, _ => { });

        Assert.Equal(0, vm.TotaleSegnalazioni);
        Assert.Equal(1, vm.PagineTotaliSegnalazioni);
        Assert.False(vm.HaSegnalazioni);
        Assert.Empty(vm.Segnalazioni);
    }

    [Fact]
    public void ChangeRequest_FiltriEOrdineUgualiAlRiferimento()
    {
        using var s = Stato();
        CreaChangeRequest(s, 60);
        var vm = new NonConformitaViewModel(s, _ => { });
        Assert.Equal(RiferimentoCr(s, _ => true), vm.ChangeRequests.Select(c => c.Id));
        Assert.Equal(60, vm.TotaleCr);

        vm.FiltroStatoCr = StatoChangeRequest.Validata;
        Assert.Equal(RiferimentoCr(s, c => c.Stato == StatoChangeRequest.Validata), vm.ChangeRequests.Select(c => c.Id));

        vm.FiltroStatoCr = null;
        vm.FiltroTesto = "Titolo 1";
        Assert.Equal(RiferimentoCr(s, c => c.Titolo.Contains("Titolo 1", StringComparison.OrdinalIgnoreCase)
            || c.Id.Contains("Titolo 1", StringComparison.OrdinalIgnoreCase) || c.Descrizione.Contains("Titolo 1", StringComparison.OrdinalIgnoreCase)),
            vm.ChangeRequests.Select(c => c.Id));

        vm.FiltroTesto = "";
        vm.FiltroTicket = "TICKET-14";
        Assert.Equal(RiferimentoCr(s, c => c.TicketCollegati.Any(t => t.Riferimento.Contains("TICKET-14", StringComparison.OrdinalIgnoreCase))),
            vm.ChangeRequests.Select(c => c.Id));

        vm.FiltroTicket = "";
        vm.FiltroVeicolo.Opzioni.Single(o => o.Valore == "VEI-2").Selezionato = true;
        Assert.Equal(RiferimentoCr(s, c => c.VeicoliImpattati.Any(v => string.Equals(v, "VEI-2", StringComparison.OrdinalIgnoreCase))),
            vm.ChangeRequests.Select(c => c.Id));

        vm.FiltroVeicolo.Opzioni.Single(o => o.Valore == "VEI-2").Selezionato = false;
        vm.FiltroEcu.Opzioni.Single(o => o.Valore == "Componente-1").Selezionato = true;
        Assert.Equal(RiferimentoCr(s, c => c.EcuImpattate.Any(e => string.Equals(e, "Componente-1", StringComparison.OrdinalIgnoreCase))),
            vm.ChangeRequests.Select(c => c.Id));
    }

    [Fact]
    public void ChangeRequest_FiltroGammaUgualeAlRiferimentoELetturaUnaVoltaPerGiro()
    {
        using var s = Stato();
        CreaChangeRequest(s, 60);
        var vm = new NonConformitaViewModel(s, _ => { });
        var letture = vm.GammaDiVeicoloLetti;

        vm.FiltroGamma.Opzioni.Single(o => o.Valore == "GAMMA-B").Selezionato = true;

        var gammaB = s.Master.Veicoli.Load().Where(v => v.GammaNome == "GAMMA-B").Select(v => v.Nome).ToHashSet();
        Assert.Equal(RiferimentoCr(s, c => c.VeicoliImpattati.Any(gammaB.Contains)), vm.ChangeRequests.Select(c => c.Id));
        // Una sola lettura del gamma per il giro (Anomalie e Change Request la condividono).
        Assert.Equal(letture + 1, vm.GammaDiVeicoloLetti);
    }

    [Fact]
    public void ChangeRequest_SecondaPaginaUgualeAlRiferimento()
    {
        using var s = Stato();
        CreaChangeRequest(s, 260);
        var vm = new NonConformitaViewModel(s, _ => { });
        Assert.Equal(2, vm.PagineTotaliCr);
        Assert.Equal(RiferimentoCr(s, _ => true, 1), vm.ChangeRequests.Select(c => c.Id));

        vm.PaginaCrSuccessivaCommand.Execute(null);
        Assert.Equal(2, vm.PaginaCr);
        Assert.Equal(RiferimentoCr(s, _ => true, 2), vm.ChangeRequests.Select(c => c.Id));
    }

    [Fact]
    public void ChangeRequest_QueryCompletaFiltraUnaVoltaEPermetteRiusoDiTutteLePagine()
    {
        using var s = Stato();
        CreaChangeRequest(s, 725);
        var tutte = s.ChangeRequests.PeekAllReadOnly();
        var filtro = new NonConformitaViewModel.FiltroChangeRequest(
            null, "Titolo", null, null, null, null,
            false, null, null, null, null, null, null, 1);

        var ordinate = NonConformitaViewModel.CalcolaChangeRequestsFiltrate(tutte, filtro, null);
        var riferimento = s.ChangeRequests.GetAll().Where(cr => cr.Titolo.Contains("Titolo", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(cr => cr.DataCreazione).Select(cr => cr.Id).ToList();
        Assert.Equal(riferimento.Count, ordinate.Count);
        Assert.Equal(riferimento.Take(250), ordinate.Take(250).Select(cr => cr.Id));

        var pagine = new List<string>();
        for (var offset = 0; offset < ordinate.Count; offset += 250)
            pagine.AddRange(ordinate.Skip(offset).Take(250).Select(cr => cr.Id));
        Assert.Equal(riferimento, pagine);

        using var annullamento = new CancellationTokenSource();
        annullamento.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            NonConformitaViewModel.CalcolaChangeRequestsFiltrate(tutte, filtro, null, annullamento.Token));
    }

    [Fact]
    public async Task Segnalazioni_CalcoliInVolo_SiApplicaSoloIlPiuRecente()
    {
        using var s = Stato();
        CreaSegnalazioni(s, 40);
        var vm = new NonConformitaViewModel(s, _ => { });
        var base0 = vm.TotaleSegnalazioni;
        vm.ForzaBackground = true;
        using var rilascio = new ManualResetEventSlim(false);
        vm.GateCalcoloPerTest = () => rilascio.Wait(TimeSpan.FromSeconds(30));

        s.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = "TEST-00", CondizioneUid = Guid.NewGuid(), Stato = StatoSegnalazione.DaClassificare });
        vm.Aggiorna();                                       // primo calcolo in volo (bloccato dal gate)
        s.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = "TEST-01", CondizioneUid = Guid.NewGuid(), Stato = StatoSegnalazione.DaClassificare });
        vm.Aggiorna();                                       // secondo calcolo, piu' recente
        rilascio.Set();
        await vm.AttendiAggiornamentoAsync();

        Assert.Equal(base0 + 2, vm.TotaleSegnalazioni);
        Assert.Equal(RiferimentoSegnalazioni(s).Take(100).Select(x => x.Uid), vm.Segnalazioni.Select(r => r.Segnalazione.Uid));
        Assert.True(vm.CalcoliScartati >= 1);
    }

    [Fact]
    public async Task ChangeRequest_FiltroCambiatoMentreCalcola_SiApplicaSoloIlFiltroAttuale()
    {
        using var s = Stato();
        CreaChangeRequest(s, 60);
        var vm = new NonConformitaViewModel(s, _ => { });
        vm.ForzaBackground = true;
        using var rilascio = new ManualResetEventSlim(false);
        vm.GateCalcoloPerTest = () => rilascio.Wait(TimeSpan.FromSeconds(30));

        vm.FiltroStatoCr = StatoChangeRequest.Chiusa;        // calcolo in volo con questo filtro
        vm.FiltroStatoCr = StatoChangeRequest.Validata;      // calcolo piu' recente
        rilascio.Set();
        await vm.AttendiAggiornamentoAsync();

        Assert.Equal(RiferimentoCr(s, c => c.Stato == StatoChangeRequest.Validata), vm.ChangeRequests.Select(c => c.Id));
        Assert.Equal(RiferimentoCr(s, c => c.Stato == StatoChangeRequest.Validata).Count, vm.TotaleCr);
        Assert.True(vm.CalcoliScartati >= 1);
    }
}
