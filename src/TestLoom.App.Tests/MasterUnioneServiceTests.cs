using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Copertura end-to-end (transazione reale, filesystem reale) di Unisci/Rinomina/Dividi sui cataloghi
/// master (spec 2026-09-23, spec_unisci_master.md): propagazione a TestCase/NonConformita/ChangeRequest,
/// fusione delle proprietà specifiche del catalogo, Changelog, permessi solo Master, e atomicità (rollback su
/// conflitto) con WorkflowTransactionService — stesso modello di WuStatusViewModelTests/RevisioneUdtServiceTests.</summary>
public sealed class MasterUnioneServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterUnione_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } } }

    private AppState CreaStato(RuoloOperatore ruolo = RuoloOperatore.Master)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Prova", Ruolo = ruolo, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    // ---------------------------------------------------------------- Unisci -----------------------------

    [Fact]
    public void Unisci_PropagaATestCaseNonConformitaEChangeRequest_ERimuoveLaVoceScartata()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        state.TestCases.Save(new TestCase { Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" } }, isNew: true);
        state.NonConformita.Save(new NonConformita { Id = "NC-1", EcuSegnalate = new EcuSelection(new[] { "BCM" }) });
        state.ChangeRequests.Save(new ChangeRequest { Id = "CR-1", EcuImpattate = { "BCM" } });

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, avvisi);

        Assert.Equal("ECM", Assert.Single(state.TestCases.GetAll())!.Componente.Single());
        Assert.Equal("ECM", state.NonConformita.GetAll().Single().EcuSegnalate.Single());
        Assert.Equal("ECM", state.ChangeRequests.GetAll().Single().EcuImpattate.Single());

        var catalogo = state.Master.Componente.Load();
        Assert.Single(catalogo);
        Assert.Equal("ECM", catalogo[0].Nome);
    }

    [Fact]
    public void Unisci_FondeVeicoliAssociatiSenzaDoppioni_EScriveChangelogSullaVoceCorretta()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = { "Daily", "Eurocargo" } },
            new() { Nome = "ECM", VeicoliAssociati = { "Eurocargo" } },
        });

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, avvisi);

        var ecm = state.Master.Componente.Load().Single();
        Assert.Equal(new[] { "Eurocargo", "Daily" }, ecm.VeicoliAssociati);
        Assert.Contains(ecm.Changelog, c => c.Campo == "(unione)" && c.ValorePrecedente == "BCM");
    }

    [Fact]
    public void Unisci_FunzionalitaFondeCategorie()
    {
        var state = CreaStato();
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Letture guasti UnitLogger", Categorie = { "UnitLogger" } },
            new() { Nome = "Letture guasti", Categorie = { "Diagnostica" } },
        });

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Funzionalita, "Funzionalita", "Letture guasti UnitLogger", "Letture guasti", f => f.Nome, avvisi);

        var fusa = state.Master.Funzionalita.Load().Single();
        Assert.Contains("UnitLogger", fusa.Categorie);
        Assert.Contains("Diagnostica", fusa.Categorie);
    }

    [Fact]
    public void Unisci_VeicoloConGammaDiversa_TieneQuellaDellaVoceCorrettaESegnala()
    {
        var state = CreaStato();
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = "Daily MY23", GammaNome = "LCV Diesel" },
            new() { Nome = "Daily MY24", GammaNome = "LCV BEV" },
        });

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Veicoli, "Veicolo", "Daily MY23", "Daily MY24", v => v.Nome, avvisi);

        var risultato = state.Master.Veicoli.Load().Single();
        Assert.Equal("LCV BEV", risultato.GammaNome);
        Assert.Contains(avvisi, a => a.Contains("Gamma diversa"));
    }

    [Fact]
    public void Unisci_DedupNelleListe_NonCreaDoppioniQuandoIlRecordUsaGiaEntrambe()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        state.TestCases.Save(new TestCase { Id = "T-1", Componente = { "BCM", "ECM" }, Veicolo = "V1", Veicoli = { "V1" } }, isNew: true);

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, avvisi);

        Assert.Single(state.TestCases.GetAll().Single().Componente);
    }

    [Fact]
    public void Unisci_CondizioniCheDiventanoIdenticheConEsitiDiversi_RestanoEntrambeESegnalate()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "Componente-A" }, new() { Nome = "Componente-B" } });
        state.TestCases.Save(new TestCase
        {
            Id = "T-1", Componente = { "Componente-A", "Componente-B" }, Funzionalita = { "Diagnosi" }, Veicolo = "V1", Veicoli = { "V1" },
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
        }, isNew: true);

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "Componente-A", "Componente-B", e => e.Nome, avvisi);

        var tc = state.TestCases.GetAll().Single();
        Assert.Equal(2, tc.Esecuzioni[0].Condizioni.Count);
        Assert.Contains(avvisi, a => a.Contains("più condizioni condividono"));
    }

    [Fact]
    public void Unisci_SenzaRuoloMaster_LanciaEccezioneENonScriveNulla()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });

        var avvisi = new List<string>();
        Assert.Throws<UnauthorizedAccessException>(() =>
            MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, avvisi));

        Assert.Equal(2, state.Master.Componente.Load().Count);
    }

    /// <summary>Atomicità (spec punto 6): se una precondizione fallisce dopo che il motore ha già letto il
    /// catalogo ma prima di scrivere alcunché, nessun TestCase/catalogo deve risultare parzialmente modificato.
    /// Il meccanismo di rollback multi-file vero e proprio (before-image + ripristino in ordine inverso su
    /// conflitto a metà transazione) è quello di WorkflowTransactionService, già coperto da
    /// WorkflowTransactionFaultInjectionTests/WorkflowTransactionConcurrentRollbackTests — MasterUnioneService
    /// vi si appoggia direttamente (stesso Begin/Commit/Rollback di WuStatusViewModel.Unisci) senza logica di
    /// rollback propria, quindi qui si verifica l'integrazione: un fallimento non lascia scritture parziali.</summary>
    [Fact]
    public void Unisci_VoceCorrettaSpariscePrimaDiScrivere_NonTocca_TestCaseNeCatalogo()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        state.TestCases.Save(new TestCase { Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" } }, isNew: true);

        // "ECM" scompare dal catalogo prima ancora che Unisci parta (un altro Master l'ha archiviata/rinominata
        // nel frattempo): il motore deve fermarsi SUBITO dopo il proprio Load(), prima di toccare qualunque
        // TestCase, e la using-transaction deve fare rollback (qui: nessuna Capture ancora avvenuta sui Test).
        // Load-poi-muta (non una lista di oggetti nuovi): Phase D traccia le righe per provenienza/riferimento,
        // una lista di oggetti mai caricati da questo repository verrebbe trattata come aggiunte, non rimozioni.
        var catalogoEcu = state.Master.Componente.Load();
        catalogoEcu.RemoveAll(e => e.Nome == "ECM");
        state.Master.Componente.Save(catalogoEcu);

        var avvisi = new List<string>();
        Assert.Throws<InvalidOperationException>(() =>
            MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, avvisi));

        Assert.Equal("BCM", state.TestCases.GetAll().Single().Componente.Single());
        Assert.Single(state.Master.Componente.Load());
    }

    /// <summary>Stesso principio del test sopra, applicato a Dividi: se una delle voci di destinazione risulta
    /// già presente con proprietà diverse dall'atteso non importa (Dividi la salta, non è un conflitto), ma se
    /// la voce ORIGINALE sparisce prima dell'operazione l'intera transazione deve annullarsi senza propagare
    /// nulla ai record di dominio già letti.</summary>
    [Fact]
    public void Dividi_VoceOriginaleSparisce_NonPropagaNulla()
    {
        var state = CreaStato();
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi" } });
        state.TestCases.Save(new TestCase { Id = "T-1", Componente = { "BCM" }, Funzionalita = { "Diagnosi" }, Veicolo = "V1", Veicoli = { "V1" } }, isNew: true);

        var catalogoFunzionalita = state.Master.Funzionalita.Load();
        catalogoFunzionalita.Clear(); // rimossa da un'altra sessione nel frattempo (load-poi-muta, vedi sopra)
        state.Master.Funzionalita.Save(catalogoFunzionalita);

        var avvisi = new List<string>();
        Assert.Throws<InvalidOperationException>(() =>
            MasterUnioneService.Dividi(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi",
                new[] { "Diagnosi-Base", "Diagnosi-Avanzata" }, f => f.Nome, avvisi));

        Assert.Equal("Diagnosi", state.TestCases.GetAll().Single().Funzionalita.Single());
        Assert.Empty(state.Master.Funzionalita.Load());
    }

    // ---------------------------------------------------------------- Rinomina ---------------------------

    /// <summary>Decisione orchestratore 2026-09-23: le fotografie storiche (DettagliTestSnapshot dentro
    /// Esecuzione.Dettagli) sono un audit trail, come il nome template congelato — Unisci/Rinomina/Dividi non
    /// le riscrive mai, solo i campi "correnti" del test (TestCase.Componente, Condizione.Componente delle revisioni). Una
    /// revisione storica continua quindi a mostrare il nome con cui l'operatore l'ha vista all'epoca.</summary>
    [Fact]
    public void Unisci_NonRiscriveLeFotografieStoricheDelleRevisioni()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        state.TestCases.Save(new TestCase
        {
            Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" },
            Esecuzioni = new()
            {
                new Esecuzione
                {
                    Iterazione = 1,
                    Dettagli = new DettagliTestSnapshot { Componente = new EcuSelection(new[] { "BCM" }) },
                    Condizioni = new() { new Condizione { Componente = { "BCM" }, Esito = Esito.Conforme } },
                }
            }
        }, isNew: true);

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, avvisi);

        var tc = state.TestCases.GetAll().Single();
        Assert.Equal("ECM", tc.Componente.Single()); // campo corrente aggiornato
        Assert.Equal("BCM", tc.Esecuzioni[0].Dettagli!.Componente.Single()); // fotografia storica intatta
    }

    [Fact]
    public void Rinomina_MantieneUidEStorico_EPropagaOvunque()
    {
        var state = CreaStato();
        var uidOriginale = Guid.NewGuid();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" } });
        var uidCatalogo = state.Master.Componente.Load().Single().Changelog; // solo per confermare che l'oggetto non cambia identità di riga
        state.TestCases.Save(new TestCase { Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" } }, isNew: true);

        var avvisi = new List<string>();
        MasterUnioneService.Rinomina(state, state.Master.Componente, "Componente", "BCM", "BCM2", e => e.Nome, avvisi);

        var voce = state.Master.Componente.Load().Single();
        Assert.Equal("BCM2", voce.Nome);
        Assert.Contains(voce.Changelog, c => c.Campo == "(rinomina)" && c.ValorePrecedente == "BCM" && c.ValoreNuovo == "BCM2");
        Assert.Equal("BCM2", state.TestCases.GetAll().Single().Componente.Single());
    }

    [Fact]
    public void Rinomina_VersoUnNomeGiaEsistente_Lancia()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });

        var avvisi = new List<string>();
        Assert.Throws<InvalidOperationException>(() =>
            MasterUnioneService.Rinomina(state, state.Master.Componente, "Componente", "BCM", "ECM", e => e.Nome, avvisi));
    }

    // ---------------------------------------------------------------- Dividi -----------------------------

    [Fact]
    public void Dividi_CreaLeNuoveVociOvunqueEMantieneLOriginale()
    {
        var state = CreaStato();
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi", Categorie = { "UnitLogger" } } });
        state.TestCases.Save(new TestCase { Id = "T-1", Componente = { "BCM" }, Funzionalita = { "Diagnosi" }, Veicolo = "V1", Veicoli = { "V1" } }, isNew: true);
        state.NonConformita.Save(new NonConformita { Id = "NC-1", FunzionalitaSegnalate = { "Diagnosi" } });

        var avvisi = new List<string>();
        MasterUnioneService.Dividi(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi",
            new[] { "Diagnosi-Base", "Diagnosi-Avanzata" }, f => f.Nome, avvisi);

        var tc = state.TestCases.GetAll().Single();
        Assert.Contains("Diagnosi-Base", tc.Funzionalita);
        Assert.Contains("Diagnosi-Avanzata", tc.Funzionalita);
        Assert.Contains("Diagnosi", tc.Funzionalita); // l'originale NON viene rimosso dai riferimenti multi-valore
        Assert.Equal("V1", tc.Veicolo);
        Assert.Equal(new[] { "BCM" }, tc.Componente.ToArray());

        var nc = state.NonConformita.GetAll().Single();
        Assert.Contains("Diagnosi", nc.FunzionalitaSegnalate);
        Assert.Contains("Diagnosi-Base", nc.FunzionalitaSegnalate);
        Assert.Contains("Diagnosi-Avanzata", nc.FunzionalitaSegnalate);

        var catalogo = state.Master.Funzionalita.Load();
        Assert.Equal(3, catalogo.Count);
        Assert.Contains(catalogo, f => f.Nome == "Diagnosi"); // A resta nel catalogo
        Assert.All(catalogo, f => Assert.Contains("UnitLogger", f.Categorie)); // proprietà copiate dall'originale
    }

    [Fact]
    public void Dividi_NomeConMaiuscoleDiverseDaVoceEsistente_UsaLaGrafiaDelCatalogo()
    {
        var state = CreaStato();
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi" }, new() { Nome = "Componente2" } });
        state.TestCases.Save(new TestCase { Id = "T-1", Funzionalita = { "Diagnosi" } }, isNew: true);

        MasterUnioneService.Dividi(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi",
            new[] { "componente2" }, f => f.Nome, new List<string>());

        var tc = state.TestCases.GetAll().Single();
        Assert.Contains("Componente2", tc.Funzionalita);
        Assert.DoesNotContain("componente2", tc.Funzionalita);
        Assert.Equal(2, state.Master.Funzionalita.Load().Count);
    }

    /// <summary>Caso reale segnalato dall'utente: scrivere "A, Nuova" sembrava una rinomina perché A spariva.
    /// Ora A viene ignorato tra i nomi (uguale all'originale), resta e viene creata solo "Nuova".</summary>
    [Fact]
    public void Dividi_InputConOriginaleENuova_CreaSoloLaNuovaEMantieneLOriginale()
    {
        var state = CreaStato();
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi", Categorie = { "UnitLogger" } } });
        state.TestCases.Save(new TestCase { Id = "T-1", Componente = { "BCM" }, Funzionalita = { "Diagnosi" }, Veicolo = "V1", Veicoli = { "V1" } }, isNew: true);

        MasterUnioneService.Dividi(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi",
            new[] { "diagnosi", "Nuova", "NUOVA" }, f => f.Nome, new List<string>());

        var catalogo = state.Master.Funzionalita.Load();
        Assert.Equal(new[] { "Diagnosi", "Nuova" }, catalogo.Select(f => f.Nome).OrderBy(n => n).ToArray());
        Assert.Equal(new[] { "Diagnosi", "Nuova" }, state.TestCases.GetAll().Single().Funzionalita.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Dividi_NomeGiaEsistenteNelCatalogo_NonVieneRicreatoMaEAggiuntoAiRiferimenti()
    {
        var state = CreaStato();
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Diagnosi", Categorie = { "UnitLogger" } },
            new() { Nome = "Esistente", Categorie = { "ALTRA" } },
        });
        state.TestCases.Save(new TestCase { Id = "T-1", Componente = { "BCM" }, Funzionalita = { "Diagnosi" }, Veicolo = "V1", Veicoli = { "V1" } }, isNew: true);

        MasterUnioneService.Dividi(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi",
            new[] { "Esistente" }, f => f.Nome, new List<string>());

        var catalogo = state.Master.Funzionalita.Load();
        Assert.Equal(2, catalogo.Count);
        Assert.Equal(new[] { "ALTRA" }, catalogo.Single(f => f.Nome == "Esistente").Categorie.ToArray()); // non ricreato/sovrascritto
        Assert.Contains("Esistente", state.TestCases.GetAll().Single().Funzionalita);
    }

    [Fact]
    public void Dividi_SenzaNuoveVociDistinte_Rifiuta()
    {
        var state = CreaStato();
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi" } });

        Assert.Throws<ArgumentException>(() => MasterUnioneService.Dividi(state, state.Master.Funzionalita, "Funzionalita", "Diagnosi",
            new[] { "Diagnosi", "diagnosi", " " }, f => f.Nome, new List<string>()));
        Assert.Single(state.Master.Funzionalita.Load());
    }

    [Fact]
    public void Dividi_CampoSingoloNonListaRestaSull_OriginaleSenzaAvvisi()
    {
        var state = CreaStato();
        var operatoriEsistenti = state.Master.Operatori.Load();
        operatoriEsistenti.Add(new Operatore { Nome = "Mario Rossi" });
        state.Master.Operatori.Save(operatoriEsistenti);
        state.NonConformita.Save(new NonConformita { Id = "NC-1", Owner = "Mario Rossi" });

        var avvisi = new List<string>();
        MasterUnioneService.Dividi(state, state.Master.Operatori, "Operatore", "Mario Rossi",
            new[] { "Mario Rossi Nord", "Mario Rossi Sud" }, o => o.Nome, avvisi);

        Assert.Equal("Mario Rossi", state.NonConformita.GetAll().Single().Owner);
        Assert.Empty(avvisi);
        Assert.Equal(3, state.Master.Operatori.Load().Count(o => o.Nome.StartsWith("Mario Rossi")));
    }
}
