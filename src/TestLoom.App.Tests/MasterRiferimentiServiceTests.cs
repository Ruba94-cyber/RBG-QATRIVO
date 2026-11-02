using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Copertura del motore generico di ricerca/sostituzione riferimenti (spec 2026-09-23,
/// spec_unisci_master.md) indipendente dal ViewModel/transazione: inventario, campi top-level, campi annidati
/// (Condizioni/OperazioniSegnalate/TestValidazione), dedup nelle liste e collisioni informative.</summary>
public sealed class MasterRiferimentiServiceTests
{
    [Fact]
    public void Registro_ContieneICataloghiPrincipaliRichiestiDallaSpec()
    {
        var cataloghi = MasterRiferimentiService.Registro.Select(r => r.Catalogo).ToHashSet();
        foreach (var atteso in new[] { "Componente", "Veicolo", "Funzionalita", "StrumentoDiagnostico", "Operatore", "Requisito", "Gamma", "Categoria" })
            Assert.Contains(atteso, cataloghi);
    }

    [Fact]
    public void Registro_ContieneLaRigaAttivaPerFunzionalitaEcuAssociate()
    {
        var riga = MasterRiferimentiService.Registro.SingleOrDefault(r => r.TipoNodo == typeof(Funzionalita) && r.NomeProprieta == "EcuAssociate");
        Assert.NotNull(riga);
        Assert.Equal("Componente", riga!.Catalogo);
        // Il campo Funzionalita.EcuAssociate esiste dalla release/6.3.0 (04adbae): la riga e' ora attiva
        // (Proprieta non nulla), il motore la raggiunge senza bisogno di modifiche a questo file.
        Assert.NotNull(riga.Proprieta);
    }

    private static TestCase NuovoTest(string id, string componente, string funzionalita) => new()
    {
        Id = id,
        Componente = { componente },
        Funzionalita = { funzionalita },
        Esecuzioni = new()
        {
            new Esecuzione
            {
                Iterazione = 1,
                Condizioni = new() { new Condizione { Componente = { componente }, Funzionalita = funzionalita, Esito = Esito.Conforme } }
            }
        }
    };

    [Fact]
    public void ApplicaSuTestCase_SostituisceCampoTopLevelEListaCondizioni()
    {
        var tc = NuovoTest("T-1", "BCM", "Letture guasti");
        var avvisi = new List<string>();

        var modificato = MasterRiferimentiService.ApplicaSuTestCase(tc, "Componente", "BCM", new[] { "BCM2" }, avvisi);

        Assert.True(modificato);
        Assert.Contains("BCM2", tc.Componente.ToList());
        Assert.DoesNotContain("BCM", tc.Componente.ToList());
        Assert.Contains("BCM2", tc.Esecuzioni[0].Condizioni[0].Componente.ToList());
        Assert.Empty(avvisi);
    }

    [Fact]
    public void ApplicaSuTestCase_ListaDedupNonCreaDoppioniDopoLaSostituzione()
    {
        var tc = NuovoTest("T-1", "BCM", "X");
        tc.Componente.Add("ECM"); // il test copre già anche la voce corretta: dopo l'unione BCM->ECM non deve duplicare
        var avvisi = new List<string>();

        MasterRiferimentiService.ApplicaSuTestCase(tc, "Componente", "BCM", new[] { "ECM" }, avvisi);

        Assert.Single(tc.Componente, e => e == "ECM");
    }

    [Fact]
    public void ApplicaSuTestCase_Dividi_AggiungeEntrambeLeNuoveVociInLista()
    {
        var tc = NuovoTest("T-1", "BCM", "X");
        var avvisi = new List<string>();

        var modificato = MasterRiferimentiService.ApplicaSuTestCase(tc, "Componente", "BCM", new[] { "BCM-HW", "BCM-SW" }, avvisi);

        Assert.True(modificato);
        Assert.Contains("BCM-HW", tc.Componente.ToList());
        Assert.Contains("BCM-SW", tc.Componente.ToList());
        Assert.DoesNotContain("BCM", tc.Componente.ToList());
        Assert.Contains("BCM-HW", tc.Esecuzioni[0].Condizioni[0].Componente.ToList());
        Assert.Contains("BCM-SW", tc.Esecuzioni[0].Condizioni[0].Componente.ToList());
    }

    [Fact]
    public void ApplicaSuTestCase_DividiConOriginaleTraINuovi_ListaTieneOriginaleEAggiungeNuove()
    {
        var tc = NuovoTest("T-1", "BCM", "X");
        var avvisi = new List<string>();

        var modificato = MasterRiferimentiService.ApplicaSuTestCase(tc, "Componente", "BCM", new[] { "BCM", "BCM-Nuova" }, avvisi);

        Assert.True(modificato);
        Assert.Equal(new[] { "BCM", "BCM-Nuova" }, tc.Componente.ToArray());
        Assert.Empty(avvisi);
        // idempotente: rieseguita non produce altre modifiche
        Assert.False(MasterRiferimentiService.ApplicaSuTestCase(tc, "Componente", "BCM", new[] { "BCM", "BCM-Nuova" }, avvisi));
    }

    [Fact]
    public void ApplicaSuNonConformita_DividiConOriginaleTraINuovi_CampoSingoloNonToccatoNeSegnalato()
    {
        var nc = new NonConformita { Id = "NC-1", StrumentoSegnalato = "UnitLogger" };
        var avvisi = new List<string>();

        var modificato = MasterRiferimentiService.ApplicaSuNonConformita(nc, "StrumentoDiagnostico", "UnitLogger", new[] { "UnitLogger", "UnitLogger-2" }, avvisi);

        Assert.False(modificato);
        Assert.Equal("UnitLogger", nc.StrumentoSegnalato);
        Assert.Empty(avvisi);
    }

    [Fact]
    public void ApplicaSuTestCase_CampoSingoloConPiuValoriNuovi_TieneIlPrimoESegnalaLimite()
    {
        var nc = new NonConformita { Id = "NC-1", StrumentoSegnalato = "UnitLogger" };
        var avvisi = new List<string>();

        var modificato = MasterRiferimentiService.ApplicaSuNonConformita(nc, "StrumentoDiagnostico", "UnitLogger", new[] { "UnitLogger-1", "UnitLogger-2" }, avvisi);

        Assert.True(modificato);
        Assert.Equal("UnitLogger-1", nc.StrumentoSegnalato);
        Assert.Single(avvisi);
    }

    [Fact]
    public void ApplicaSuTestCase_CollisioneCondizioni_NonRimuoveNulla_SoloAvviso()
    {
        // Due condizioni della stessa esecuzione, componente diverse ma stessa Funzionalita: dopo l'unione componente-A->componente-B
        // diventano identiche (componente+Funzionalita) pur con esiti diversi — la spec vieta di fonderle silenziosamente.
        var tc = new TestCase
        {
            Id = "T-1",
            Componente = { "Componente-A", "Componente-B" },
            Funzionalita = { "Diagnosi" },
            Esecuzioni = new()
            {
                new Esecuzione
                {
                    Iterazione = 1,
                    Condizioni = new()
                    {
                        new Condizione { Componente = { "Componente-A" }, Funzionalita = "Diagnosi", Esito = Esito.Conforme },
                        new Condizione { Componente = { "Componente-B" }, Funzionalita = "Diagnosi", Esito = Esito.NonConforme },
                    }
                }
            }
        };
        var avvisi = new List<string>();

        MasterRiferimentiService.ApplicaSuTestCase(tc, "Componente", "Componente-A", new[] { "Componente-B" }, avvisi);

        Assert.Equal(2, tc.Esecuzioni[0].Condizioni.Count); // nessuna rimossa
        Assert.Equal(Esito.Conforme, tc.Esecuzioni[0].Condizioni[0].Esito);
        Assert.Equal(Esito.NonConforme, tc.Esecuzioni[0].Condizioni[1].Esito);
        Assert.Contains(avvisi, a => a.Contains("più condizioni condividono"));
    }

    [Fact]
    public void ApplicaSuNonConformita_SostituisceOperazioniSegnalateAnnidate()
    {
        var nc = new NonConformita
        {
            Id = "NC-1",
            OperazioniSegnalate = new() { new OperazioneSegnalazione { Componente = "BCM", Funzionalita = "X" } }
        };
        var avvisi = new List<string>();

        var modificato = MasterRiferimentiService.ApplicaSuNonConformita(nc, "Componente", "BCM", new[] { "BCM2" }, avvisi);

        Assert.True(modificato);
        Assert.Equal("BCM2", nc.OperazioniSegnalate[0].Componente);
    }

    [Fact]
    public void ApplicaSuChangeRequest_SostituisceTestValidazioneAnnidato()
    {
        var cr = new ChangeRequest
        {
            Id = "CR-1",
            EcuImpattate = { "BCM" },
            TestValidazione = new() { new CollegamentoValidazioneCr { EcuCoperte = { "BCM" } } }
        };
        var avvisi = new List<string>();

        var modificato = MasterRiferimentiService.ApplicaSuChangeRequest(cr, "Componente", "BCM", new[] { "BCM2" }, avvisi);

        Assert.True(modificato);
        Assert.Contains("BCM2", cr.EcuImpattate);
        Assert.Contains("BCM2", cr.TestValidazione[0].EcuCoperte);
    }

    [Fact]
    public void ContaImpatto_ContaCorrettamenteTestNcCr()
    {
        var tc1 = NuovoTest("T-1", "BCM", "X");
        var tc2 = NuovoTest("T-2", "ECM", "X");
        var nc = new NonConformita { Id = "NC-1", EcuSegnalate = new EcuSelection(new[] { "BCM" }) };
        var cr = new ChangeRequest { Id = "CR-1", EcuImpattate = { "BCM" } };

        var risultato = MasterRiferimentiService.ContaImpatto("Componente", "BCM",
            new[] { tc1, tc2 }, new[] { nc }, new[] { cr });

        Assert.Equal(1, risultato.TestAggiornati);
        Assert.Equal(1, risultato.NonConformitaAggiornate);
        Assert.Equal(1, risultato.ChangeRequestAggiornate);
    }
}
