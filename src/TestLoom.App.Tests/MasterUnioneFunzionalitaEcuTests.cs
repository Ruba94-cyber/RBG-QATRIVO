using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Integrazione fra Unisci/Rinomina/Dividi componente e il campo Funzionalita.TutteLeEcu/EcuAssociate
/// (release/6.3.0, feat/funzionalita-componente 04adbae): fusione della lista al merge di due Funzionalita, copia
/// alla divisione, e la rete di sicurezza che impedisce una Funzionalita "senza componente applicabili" (richiesta
/// esplicita del coordinatore 2026-09-23, terzo giro).</summary>
public sealed class MasterUnioneFunzionalitaEcuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterUnioneEcuFunz_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } } }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    [Fact]
    public void UnisciEDividiRequisitiD4d_ConservaAmbitoEAssociazioni()
    {
        var state = CreaStato();
        Requisito Crea(string id, string veicolo, string componente)
        {
            var r = new Requisito { Id = id, VeicoliEcuD4D = new() { new() { Veicolo = veicolo, Componente = componente } } };
            RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
            RequisitoApplicabilita.ImpostaSottoveicoli(r, new[] { veicolo });
            return r;
        }
        state.Master.Requisiti.Save(new List<Requisito> { Crea("R1", "V1", "E1"), Crea("R2", "V2", "E2") });
        MasterUnioneService.Unisci(state, state.Master.Requisiti, "Requisito", "R1", "R2", r => r.Id, new());
        var unito = Assert.Single(state.Master.Requisiti.Load());
        Assert.Equal(new[] { "V1", "V2" }, RequisitoApplicabilita.SottoveicoliDi(unito));
        Assert.Equal(2, unito.VeicoliEcuD4D!.Count);
        MasterUnioneService.Dividi(state, state.Master.Requisiti, "Requisito", "R2", new[] { "R3" }, r => r.Id, new());
        var copia = state.Master.Requisiti.Load().Single(r => r.Id == "R3");
        Assert.True(RequisitoApplicabilita.ED4D(copia));
        Assert.Equal(new[] { "V1", "V2" }, RequisitoApplicabilita.SottoveicoliDi(copia));
        Assert.Equal(2, copia.VeicoliEcuD4D!.Count);
    }

    [Fact]
    public void UnisciD4dInStandard_ConAssociazioni_RifiutaSenzaPerdereDati()
    {
        var state = CreaStato();
        var extra = new Requisito { Id = "R1", VeicoliEcuD4D = new() { new() { Veicolo = "V1", Componente = "E1" } } };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        state.Master.Requisiti.Save(new List<Requisito> { extra, new() { Id = "R2" } });
        Assert.Throws<InvalidOperationException>(() => MasterUnioneService.Unisci(state, state.Master.Requisiti, "Requisito", "R1", "R2", r => r.Id, new()));
        Assert.Equal(2, state.Master.Requisiti.Load().Count);
        Assert.Single(state.Master.Requisiti.Load().Single(r => r.Id == "R1").VeicoliEcuD4D!);
    }

    [Fact]
    public void UnisciEcu_AggiornaEcuAssociateDiUnaFunzionalitaSenzaSvuotarla()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Diagnosi", TutteLeEcu = false, EcuAssociate = { "BCM" } },
        });

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, avvisi);

        var f = state.Master.Funzionalita.Load().Single();
        Assert.False(f.TutteLeEcu);
        Assert.Equal(new[] { "ECM" }, f.EcuAssociate);
    }

    [Fact]
    public void UnisciFunzionalita_FondeEcuAssociate_QuandoEntrambeLimitate()
    {
        var state = CreaStato();
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Diagnosi UnitLogger", TutteLeEcu = false, EcuAssociate = { "BCM" } },
            new() { Nome = "Diagnosi", TutteLeEcu = false, EcuAssociate = { "ECM" } },
        });

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi UnitLogger", "Diagnosi", f => f.Nome, avvisi);

        var risultato = state.Master.Funzionalita.Load().Single();
        Assert.False(risultato.TutteLeEcu);
        Assert.Contains("BCM", risultato.EcuAssociate);
        Assert.Contains("ECM", risultato.EcuAssociate);
    }

    [Fact]
    public void UnisciFunzionalita_TutteLeEcuVinceSempreEnonRestringeMai()
    {
        var state = CreaStato();
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Diagnosi UnitLogger", TutteLeEcu = true },
            new() { Nome = "Diagnosi", TutteLeEcu = false, EcuAssociate = { "ECM" } },
        });

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi UnitLogger", "Diagnosi", f => f.Nome, avvisi);

        var risultato = state.Master.Funzionalita.Load().Single();
        Assert.True(risultato.TutteLeEcu);
        Assert.Empty(risultato.EcuAssociate);
    }

    [Fact]
    public void DividiFunzionalita_CopiaTutteLeEcuEEcuAssociateSulleNuoveVoci()
    {
        var state = CreaStato();
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Diagnosi", TutteLeEcu = false, EcuAssociate = { "BCM" } },
        });

        var avvisi = new List<string>();
        MasterUnioneService.Dividi(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi",
            new[] { "Diagnosi-Base", "Diagnosi-Avanzata" }, f => f.Nome, avvisi);

        var catalogo = state.Master.Funzionalita.Load();
        Assert.Equal(3, catalogo.Count); // originale mantenuta + 2 nuove
        Assert.All(catalogo, f => Assert.False(f.TutteLeEcu));
        Assert.All(catalogo, f => Assert.Equal(new[] { "BCM" }, f.EcuAssociate));
    }

    /// <summary>Rete di sicurezza: se una Funzionalita risultasse con EcuAssociate vuota e TutteLeEcu=false
    /// (stato che la sola sostituzione per lista non produce mai da sola, ma che va comunque protetto — vedi
    /// MasterUnioneService.RipristinaInvarianteEcuAssociate), la prossima operazione sul catalogo componente la
    /// ripristina su "Tutti i componenti" invece di lasciarla invisibile in ogni picker filtrato per componente.</summary>
    [Fact]
    public void OperazioneSulCatalogoEcu_RipristinaTutteLeEcu_SuFunzionalitaRimastaSenzaEcu()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        // Stato altrimenti irraggiungibile tramite Unisci/Rinomina/Dividi (che sostituiscono, mai svuotano):
        // simula qui una Funzionalita rimasta orfana per un motivo esterno al motore (dato storico corrotto,
        // import, ecc.) per verificare che la rete di sicurezza si attivi comunque.
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi", TutteLeEcu = false } });

        var avvisi = new List<string>();
        MasterUnioneService.Rinomina(state, state.Master.Componente, "Componente", "BCM", "BCM2", e => e.Nome, avvisi);

        var f = state.Master.Funzionalita.Load().Single();
        Assert.True(f.TutteLeEcu);
        Assert.Contains(avvisi, a => a.Contains("Tutti i componenti"));
    }

    [Fact]
    public void UnisciEcu_ConTutteLeEcu_EcuSenzaRestrizioneRestaSuTuttiIVeicoli()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Diagnosi", TutteLeEcu = true, VeicoliPerEcu = [new VeicoliFunzionalitaEcu { Componente = "BCM", Veicoli = ["V1"] }] },
        });

        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, new List<string>());

        // ECM era gia' applicabile su tutti i veicoli: unirle BCM (solo V1) non la restringe.
        var f = state.Master.Funzionalita.Load().Single();
        Assert.True(f.TutteLeEcu);
        Assert.Empty(f.VeicoliPerEcu);
        Assert.True(f.ValeSu("ECM", "V9"));
    }

    [Fact]
    public void UnisciEcu_ConTutteLeEcu_UnisceLeRestrizioniDelleDueEcu()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new()
            {
                Nome = "Diagnosi",
                TutteLeEcu = true,
                VeicoliPerEcu = [new VeicoliFunzionalitaEcu { Componente = "BCM", Veicoli = ["V1"] }, new VeicoliFunzionalitaEcu { Componente = "ECM", Veicoli = ["V2"] }],
            },
        });

        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, new List<string>());

        var f = state.Master.Funzionalita.Load().Single();
        var voce = Assert.Single(f.VeicoliPerEcu);
        Assert.Equal("ECM", voce.Componente);
        Assert.Equal(new[] { "V1", "V2" }, voce.Veicoli.OrderBy(v => v, StringComparer.Ordinal));
    }
}
