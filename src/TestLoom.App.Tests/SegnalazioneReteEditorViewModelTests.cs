using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copre il feedback utente 2026-09-08/2026-09-15: una NC può nascere senza un Test, tipicamente da
/// una segnalazione diretta della rete assistenziale — porta i campi di un Test (componente/Veicolo/Funzionalità/
/// Strumento, MAI il Requisito, esclusivo dei Test) senza un TestCaseId reale, con più ticket/matricola possibili, e
/// deve poter essere collegata a un Test vero in un secondo momento.</summary>
[Collection("Localization")]
public sealed class SegnalazioneReteEditorViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SegnalazioneRete_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", ModificatoDa = "Seed" } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily MY24", GammaNome = "Daily", ModificatoDa = "Seed" } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi accensione", ModificatoDa = "Seed" } });
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new()
            {
                Nome = "UnitLogger",
                // Ambito=PerTest esplicito su tutti i campi di questo file (feedback utente 2026-09-18): il
                // default di DefinizioneCampoStrumento e' PerOperazione, che dal redesign delle Operazioni
                // (vedi OperazioniSegnalateTests) finisce nella riga Componente×Funzionalita, non in CampiStrumento.
                // Questi test riguardano il filtro dedup della lista FLAT (Origine segnalazione), non le
                // Operazioni: PerTest ne preserva l'intento originale invariato.
                Campi = new List<DefinizioneCampoStrumento>
                {
                    new() { Nome = "Versione firmware", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
                    // "Licenza" deve essere filtrato dalla UI (dedup con Origine segnalazione), non deve
                    // comparire tra i campi tecnici mostrati per lo strumento.
                    new() { Nome = "Licenza", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
                }
            }
        });
        var operatori = state.Master.Operatori.Load();
        var operatoreRete = new Operatore
        {
            Nome = "Operatore Rete",
            Ruolo = RuoloOperatore.Standard,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        };
        RuoloSigilloService.Sigilla(operatoreRete); // migrazione 6.4.2: un ruolo non sigillato vale come Viewer.
        operatori.Add(operatoreRete);
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Operatore Rete", "1234"));
        return state;
    }

    private static void CompilaCampiObbligatori(SegnalazioneReteEditorViewModel vm)
    {
        vm.NuovoNumeroPratica = "PRATICA-2026-4471";
        vm.AggiungiNumeroPraticaCommand.Execute(null);
        vm.NuovoVin = "ZCFC1234500098765";
        vm.AggiungiVinCommand.Execute(null);
        vm.NuovaLicenza = "LIC-2026-001";
        vm.AggiungiLicenzaCommand.Execute(null);
        vm.DescrizioneBreve = "Quadro strumenti spento";
        vm.Descrizione = "Il quadro strumenti non si accende dopo il KL15 ON.";
        vm.Severita = SeveritaAnomalia.High;
        vm.PrioritaScelta = PrioritaOperativa.Normal;
        vm.StatoScelta = StatoAnomalia.Confermata;
        // Ambito minimo (feedback utente 2026-09-16): un componente basta a soddisfare "almeno uno tra componente/Veicolo/
        // Funzionalità/Strumento" per i test che non ne aggiungono già una propria dopo questa chiamata.
        vm.AggiungiEcuCommand.Execute("BCM");
    }

    [Fact]
    public void Salva_NuovaSegnalazioneRete_PersisteTuttiICampiSenzaAlcunTestCollegato()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        vm.ConcessionariaOfficina = "Officina Rossi SRL";
        vm.Owner = "Operatore Rete";
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.AggiungiVeicoloCommand.Execute("Daily MY24");
        vm.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
        vm.StrumentoSelezionato = "UnitLogger";
        Assert.Single(vm.CampiStrumento).Valore = "1.4.2"; // "Licenza" e' filtrato, resta solo "Versione firmware"

        vm.SalvaCommand.Execute(null);

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        var salvata = Assert.Single(state.NonConformita.GetAll());
        Assert.True(salvata.DaSegnalazioneRete);
        Assert.True(string.IsNullOrEmpty(salvata.TestCaseId));
        Assert.Empty(salvata.ProveCollegate);
        Assert.Equal("Quadro strumenti spento", salvata.DescrizioneBreveSegnalata);
        Assert.Equal("Il quadro strumenti non si accende dopo il KL15 ON.", salvata.Descrizione);
        Assert.Contains("PRATICA-2026-4471", salvata.NumeriPraticaRete);
        Assert.Contains("ZCFC1234500098765", salvata.VinCliente);
        Assert.Equal("Officina Rossi SRL", salvata.ConcessionariaOfficina);
        Assert.Equal(PrioritaOperativa.Normal, salvata.Priorita);
        Assert.Contains("BCM", salvata.EcuSegnalate.ToList());
        Assert.Contains("Daily MY24", salvata.VeicoliSegnalati);
        Assert.Contains("Diagnosi accensione", salvata.FunzionalitaSegnalate);
        Assert.Equal("UnitLogger", salvata.StrumentoSegnalato);
        Assert.Equal("1.4.2", salvata.ValoriStrumentoSegnalato["Versione firmware"]);

        // Senza alcuna ProveCollegate la chiusura automatica per conformità non si applica mai: una
        // segnalazione di rete resta sempre una decisione umana finché non viene verificata da un Test vero.
        Assert.False(AnomaliaWorkflowService.PuoChiudersiPerConformita(salvata, state.TestCases.GetAll()));
    }

    [Fact]
    public void RecuperaBozzaNc_ApplicaSoloCampiSelezionatiEMantieneIlCanonicoInvariato()
    {
        var originale = ConfirmService.Handler;
        try
        {
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            var state = CreateState();
            var crea = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(crea);
            crea.SalvaCommand.Execute(null);
            var id = Assert.Single(state.NonConformita.GetAll()).Id;
            var vm = new SegnalazioneReteEditorViewModel(state, id, () => { });
            var baseline = EntityStateFingerprint.Clone(vm.Nc);
            var draft = EntityStateFingerprint.Clone(vm.Nc);
            draft.Descrizione = "Descrizione recuperata";
            draft.DescrizioneBreveSegnalata = "Da non importare";

            vm.ApplicaBozzaRecuperata(draft, [nameof(NonConformita.Descrizione)]);

            Assert.Equal("Descrizione recuperata", vm.Nc.Descrizione);
            Assert.Equal(baseline.DescrizioneBreveSegnalata, vm.Nc.DescrizioneBreveSegnalata);
            Assert.Equal(baseline.RevisioneDati, vm.Nc.RevisioneDati);
            Assert.True(vm.ModificheNonSalvate);
            Assert.Equal(baseline.Descrizione, state.NonConformita.GetById(id)!.Descrizione);
        }
        finally { ConfirmService.Handler = originale; }
    }

    [Theory]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, false)]
    public void Salva_ConUnCampoObbligatorioMancante_BloccaEMostraIPopupCampiMancanti(
        bool haTicket, bool haVin, bool haDescrizioneBreve, bool haSeverita, bool haPriorita)
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        var richiestoPopup = false;
        vm.RichiestaMostraCampiMancanti += () => richiestoPopup = true;

        if (haTicket) { vm.NuovoNumeroPratica = "PRATICA-1"; vm.AggiungiNumeroPraticaCommand.Execute(null); }
        if (haVin) { vm.NuovoVin = "Matricola1"; vm.AggiungiVinCommand.Execute(null); }
        if (haDescrizioneBreve) vm.DescrizioneBreve = "Sintesi";
        vm.Descrizione = "Descrizione dettagliata";
        if (haSeverita) vm.Severita = SeveritaAnomalia.Low;
        if (haPriorita) vm.PrioritaScelta = PrioritaOperativa.Normal;

        vm.SalvaCommand.Execute(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.NotEmpty(vm.CampiMancanti);
        Assert.True(richiestoPopup);
        Assert.Empty(state.NonConformita.GetAll());
    }

    [Fact]
    public void RicostruisciCampiStrumento_FiltraICampiGiaRaccoltiInOrigineSegnalazione()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });

        vm.StrumentoSelezionato = "UnitLogger";

        var campo = Assert.Single(vm.CampiStrumento);
        Assert.Equal("Versione firmware", campo.Nome);
        Assert.DoesNotContain(vm.CampiStrumento, c => c.Nome.Equals("Licenza", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Regressione del bug reale segnalato dall'utente 2026-09-16: sullo strumento UnitLogger il campo si
    /// chiama "Asist", non uno dei 4 nomi esatti che il vecchio filtro cercava ("numero ticket"/"ticket"/
    /// "matricola"/"licenza") — restava quindi duplicato rispetto al numero ticket/pratica già raccolto in "Origine
    /// segnalazione". Usa lo strumento e i nomi campo REALI del catalogo dell'utente (strumenti.json), non
    /// un'approssimazione.</summary>
    [Fact]
    public void RicostruisciCampiStrumento_FiltraIlCampoAsistDelloStrumentoUdtReale()
    {
        var state = CreateState();
        var strumenti = state.Master.Strumenti.Load();
        var unitlogger = strumenti.Single(s => s.Nome == "UnitLogger");
        unitlogger.Campi = new List<DefinizioneCampoStrumento>
        {
            new() { Nome = "Licence ", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest }, // spazio finale, come nel file reale
            new() { Nome = "UnitLogger Version", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
            new() { Nome = "UnitLogger Language", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
            new() { Nome = "PC Language", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
            new() { Nome = "Pacchetto + rev.", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
            new() { Nome = "Asist", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
            new() { Nome = "Other Vehicle Involved", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
        };
        state.Master.Strumenti.Save(strumenti);
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });

        vm.StrumentoSelezionato = "UnitLogger";

        Assert.DoesNotContain(vm.CampiStrumento, c => c.Nome == "Asist");
        Assert.DoesNotContain(vm.CampiStrumento, c => c.Nome == "Licence ");
        // I campi non equivalenti a ticket/matricola/licenza restano visibili, incluso un secondo campo lingua
        // distinto: nessun filtro troppo aggressivo che ne nasconda anche uno di questi per errore.
        Assert.Contains(vm.CampiStrumento, c => c.Nome == "UnitLogger Version");
        Assert.Contains(vm.CampiStrumento, c => c.Nome == "UnitLogger Language");
        Assert.Contains(vm.CampiStrumento, c => c.Nome == "PC Language");
        Assert.Contains(vm.CampiStrumento, c => c.Nome == "Pacchetto + rev.");
        Assert.Contains(vm.CampiStrumento, c => c.Nome == "Other Vehicle Involved");
    }

    /// <summary>Il confronto per parola non deve "sparare nel mucchio": un campo come "Vincolo" contiene "matricola"
    /// come prefisso ma è una parola completamente diversa — deve restare visibile.</summary>
    [Theory]
    [InlineData("Asist", true)]
    [InlineData("N. Ticket", true)]
    [InlineData("Ticket TICKET", true)]
    [InlineData("Licence", true)]
    [InlineData("Matricola", true)]
    [InlineData("Vincolo", false)]
    [InlineData("Vincolo di progetto", false)]
    [InlineData("PC Language", false)]
    [InlineData("Pacchetto", false)]
    public void ECampoOrigineSegnalazione_ConfrontoPerParolaNonPerSottostringaCieca(string nomeCampo, bool attesoFiltrato)
    {
        Assert.Equal(attesoFiltrato, SegnalazioneReteEditorViewModel.ECampoOrigineSegnalazione(nomeCampo));
    }

    [Fact]
    public void CollegaATestSicuro_ImpostaTestCaseIdMaPreservaICampiOriginaliDellaSegnalazione()
    {
        var state = CreateState();
        var editor = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(editor);
        editor.AggiungiEcuCommand.Execute("BCM");
        editor.SalvaCommand.Execute(null);
        Assert.False(editor.HaMessaggioErrore, editor.MessaggioErrore);

        var testIndagine = new TestCase { Id = "TC-INDAGINE-1", Componente = "BCM", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
        state.TestCases.Save(testIndagine, isNew: true);

        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = Assert.Single(vm.Righe);
        Assert.True(riga.SenzaTestCollegato);
        riga.TestIdDaCollegare = "TC-INDAGINE-1";

        vm.CollegaATestSicuroCommand.Execute(riga);

        Assert.Null(vm.MessaggioErrore);
        var salvata = state.NonConformita.GetById(riga.Nc.Id)!;
        Assert.Equal("TC-INDAGINE-1", salvata.TestCaseId);
        Assert.True(salvata.DaSegnalazioneRete);
        Assert.Contains("BCM", salvata.EcuSegnalate.ToList());
    }

    [Fact]
    public void CollegaATestSicuro_IdInesistente_MostraErroreENonModificaNulla()
    {
        var state = CreateState();
        var editor = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(editor);
        editor.SalvaCommand.Execute(null);

        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = Assert.Single(vm.Righe);
        var revisionePrima = riga.Nc.RevisioneDati;
        riga.TestIdDaCollegare = "TC-NON-ESISTE";

        vm.CollegaATestSicuroCommand.Execute(riga);

        Assert.NotNull(vm.MessaggioErrore);
        Assert.Equal(revisionePrima, state.NonConformita.GetById(riga.Nc.Id)!.RevisioneDati);
        Assert.True(string.IsNullOrEmpty(state.NonConformita.GetById(riga.Nc.Id)!.TestCaseId));
    }

    [Fact]
    public void Salva_ModificaUnaSegnalazioneEsistente_AggiornaICampiConRevisioneCorretta()
    {
        var state = CreateState();
        var creazione = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(creazione);
        creazione.SalvaCommand.Execute(null);
        var id = Assert.Single(state.NonConformita.GetAll()).Id;

        var modifica = new SegnalazioneReteEditorViewModel(state, id, () => { });
        modifica.Descrizione = "Descrizione aggiornata dopo ulteriori informazioni dal cliente";
        modifica.AggiungiEcuCommand.Execute("BCM");

        modifica.SalvaCommand.Execute(null);

        Assert.False(modifica.HaMessaggioErrore, modifica.MessaggioErrore);
        var salvata = state.NonConformita.GetById(id)!;
        Assert.Equal("Descrizione aggiornata dopo ulteriori informazioni dal cliente", salvata.Descrizione);
        Assert.Contains("BCM", salvata.EcuSegnalate.ToList());
        Assert.True(salvata.DaSegnalazioneRete);
    }

    /// <summary>Gli allegati della segnalazione sono caricabili prima del primo salvataggio e persistono con la NC.</summary>
    [Fact]
    public async Task AggiungiAllegato_SuSegnalazioneNuova_PuoUsareMsgEPersisteAlPrimoSalvataggio()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        var file = Path.Combine(_dir, "nota.txt");
        File.WriteAllText(file, "contenuto messaggio Outlook");
        var msg = Path.Combine(_dir, "email.msg");
        File.WriteAllBytes(msg, "messaggio Outlook"u8.ToArray());

        Assert.True(vm.IsNew);
        await vm.AggiungiAllegatoCommand.ExecuteAsync(file);
        await vm.AggiungiAllegatoCommand.ExecuteAsync(msg);

        Assert.Equal(2, vm.AllegatiVisual.Count);
        Assert.Null(vm.MessaggioErrore);
        Assert.All(vm.AllegatiVisual, allegato => Assert.True(File.Exists(AttachmentService.PercorsoCompleto(state.Paths, vm.Nc, allegato))));

        await vm.SalvaCommand.ExecuteAsync(null);

        var salvata = state.NonConformita.GetById(vm.Nc.Id)!;
        Assert.Equal(new[] { "email.msg", "nota.txt" }, salvata.Allegati.Select(a => a.NomeFile).OrderBy(n => n));
        Assert.All(salvata.Allegati, allegato => Assert.True(File.Exists(AttachmentService.PercorsoCompleto(state.Paths, salvata, allegato))));
    }

    [Fact]
    public async Task AggiungiAllegato_MostraIlNomeDelFileDuranteLaCopia()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        var file = Path.Combine(_dir, "evidenza-lunga.txt");
        File.WriteAllText(file, "contenuto");
        var avviato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continua = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.GateCaricamentoAllegatoPerTest = async () =>
        {
            avviato.TrySetResult();
            await continua.Task;
        };

        var caricamento = vm.AggiungiAllegatoCommand.ExecuteAsync(file);
        try
        {
            await avviato.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(vm.CaricamentoAllegatoInCorso);
            Assert.Equal("evidenza-lunga.txt", vm.NomeAllegatoInCaricamento);
            Assert.False(vm.PuoAggiungereAllegati);
        }
        finally { continua.TrySetResult(); }

        await caricamento.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(vm.CaricamentoAllegatoInCorso);
        Assert.Null(vm.NomeAllegatoInCaricamento);
        Assert.True(vm.PuoAggiungereAllegati);
    }

    [Fact]
    public async Task SalvaEAnnulla_DuranteCopiaSonoBloccatiFinoAlCompletamento()
    {
        var state = CreateState();
        var ritorni = 0;
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => ritorni++);
        CompilaCampiObbligatori(vm);
        var file = Path.Combine(_dir, "upload-in-corso.txt");
        File.WriteAllText(file, "allegato da salvare");
        var avviato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continua = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.GateCaricamentoAllegatoPerTest = async () =>
        {
            avviato.TrySetResult();
            await continua.Task;
        };

        var caricamento = vm.AggiungiAllegatoCommand.ExecuteAsync(file);
        try
        {
            await avviato.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(vm.PuoSalvare);
            Assert.False(vm.PuoAnnullare);
            await vm.SalvaCommand.ExecuteAsync(null);
            vm.AnnullaCommand.Execute(null);

            Assert.True(vm.IsNew);
            Assert.Empty(state.NonConformita.PeekAllReadOnly());
            Assert.Equal(0, ritorni);
        }
        finally { continua.TrySetResult(); }

        await caricamento.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.PuoSalvare);
        Assert.True(vm.PuoAnnullare);
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Equal(1, ritorni);
        var salvata = Assert.Single(state.NonConformita.GetById(vm.Nc.Id)!.Allegati);
        Assert.Equal("upload-in-corso.txt", salvata.NomeFile);
        Assert.True(File.Exists(AttachmentService.PercorsoCompleto(state.Paths, vm.Nc, salvata)));
    }

    [Fact]
    public void Salva_SenzaTicketAsist_NonBloccaLaSegnalazione()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        vm.NumeriPratica.Clear();

        vm.SalvaCommand.Execute(null);

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.Empty(state.NonConformita.GetById(vm.Nc.Id)!.NumeriPraticaRete);
    }

    [Fact]
    public async Task AllegatoDiSegnalazioneNuova_AbbandonoConfermatoPulisceIlBucketTemporaneo()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        var file = Path.Combine(_dir, "da-scartare.txt");
        File.WriteAllText(file, "allegato non persistito");
        await vm.AggiungiAllegatoCommand.ExecuteAsync(file);
        var allegato = Assert.Single(vm.Nc.Allegati);
        var destinazione = AttachmentService.PercorsoCompleto(state.Paths, vm.Nc, allegato);
        Assert.True(File.Exists(destinazione));

        ((IConfirmedAbandonmentParticipant)vm).PrepareConfirmedAbandonmentCleanup();
        await AttachmentRemovalTestSupport.AttendiCallbackAbbandono();

        Assert.False(File.Exists(destinazione));
    }

    [Fact]
    public async Task AggiungiERimuoviAllegato_SuSegnalazioneSalvata_AggiornaListaEFile()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        vm.SalvaCommand.Execute(null);
        Assert.False(vm.IsNew);
        var id = vm.Nc.Id;

        // Come nell'app: ogni Salva parte da un editor aperto sulla revisione corrente (poi si torna alla lista).
        var file = Path.Combine(_dir, "evidenza.txt");
        File.WriteAllText(file, "contenuto");
        var aggiunta = new SegnalazioneReteEditorViewModel(state, id, () => { });
        await aggiunta.AggiungiAllegatoCommand.ExecuteAsync(file);
        aggiunta.SalvaCommand.Execute(null);
        Assert.Null(aggiunta.MessaggioErrore);

        var rimozione = new SegnalazioneReteEditorViewModel(state, id, () => { });
        Assert.Single(rimozione.AllegatiVisual);
        var allegato = rimozione.AllegatiVisual[0];
        Assert.Equal("evidenza.txt", allegato.NomeFile);
        var percorsoAllegato = AttachmentService.PercorsoCompleto(state.Paths, rimozione.Nc, allegato);
        Assert.True(File.Exists(percorsoAllegato));

        rimozione.RimuoviAllegatoCommand.Execute(allegato);

        Assert.Empty(rimozione.AllegatiVisual);
        // Il binario resta nel percorso attivo finche' il Salva non persiste la segnalazione senza l'allegato.
        Assert.True(File.Exists(percorsoAllegato));

        rimozione.SalvaCommand.Execute(null);

        Assert.Null(rimozione.MessaggioErrore);
        Assert.False(File.Exists(percorsoAllegato));
        Assert.True(File.Exists(AttachmentService.PercorsoCompleto(state.Paths, rimozione.Nc, allegato)));
    }

    [Fact]
    public async Task RimozioneAllegato_AbbandonoSenzaSalvataggio_LasciaFileERecordInvariati()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        vm.SalvaCommand.Execute(null);
        var id = vm.Nc.Id;
        var file = Path.Combine(_dir, "abbandono.txt");
        File.WriteAllText(file, "evidenza segnalazione");
        var aggiunta = new SegnalazioneReteEditorViewModel(state, id, () => { });
        await aggiunta.AggiungiAllegatoCommand.ExecuteAsync(file);
        aggiunta.SalvaCommand.Execute(null); // l'allegato entra nel record persistito
        Assert.Null(aggiunta.MessaggioErrore);

        var abbandonata = new SegnalazioneReteEditorViewModel(state, id, () => { });
        var allegato = abbandonata.AllegatiVisual.Single();
        var attivo = AttachmentService.PercorsoCompleto(state.Paths, abbandonata.Nc, allegato);
        abbandonata.RimuoviAllegatoCommand.Execute(allegato);
        ((IConfirmedAbandonmentParticipant)abbandonata).PrepareConfirmedAbandonmentCleanup(); // abbandono confermato
        await AttachmentRemovalTestSupport.AttendiCallbackAbbandono();

        Assert.True(File.Exists(attivo));
        Assert.Equal("evidenza segnalazione", File.ReadAllText(attivo));
        Assert.Single(state.NonConformita.GetById(id)!.Allegati);
    }

    [Fact]
    public async Task RimozioneAllegato_SalvataggioFallitoPerConflitto_ConservaFileEAllegatoNelRecord()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        vm.SalvaCommand.Execute(null);
        var id = vm.Nc.Id;
        var file = Path.Combine(_dir, "conflitto.txt");
        File.WriteAllText(file, "contenuto conflitto");
        var aggiunta = new SegnalazioneReteEditorViewModel(state, id, () => { });
        await aggiunta.AggiungiAllegatoCommand.ExecuteAsync(file);
        aggiunta.SalvaCommand.Execute(null);
        Assert.Null(aggiunta.MessaggioErrore);

        var mio = new SegnalazioneReteEditorViewModel(state, id, () => { }); // revisione letta da qui
        var allegato = mio.AllegatiVisual.Single();
        var attivo = AttachmentService.PercorsoCompleto(state.Paths, mio.Nc, allegato);

        var altro = new SegnalazioneReteEditorViewModel(state, id, () => { });
        altro.DescrizioneBreve = "Modificata da un altro PC";
        altro.SalvaCommand.Execute(null);
        Assert.Null(altro.MessaggioErrore);

        mio.RimuoviAllegatoCommand.Execute(allegato);
        mio.SalvaCommand.Execute(null); // revisione superata: il Salva fallisce e non deve toccare il binario

        Assert.NotNull(mio.MessaggioErrore);
        Assert.True(mio.ModificheNonSalvate);
        Assert.True(File.Exists(attivo));
        Assert.Equal("contenuto conflitto", File.ReadAllText(attivo));
        Assert.Single(state.NonConformita.GetById(id)!.Allegati);
    }

    [Fact]
    public async Task PreparaEsta_SuSegnalazioneNuova_LaSalvaPrimaERestituisceLaNcPersistita()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        Assert.True(vm.PuoEsportarePacchetto);

        var nc = await vm.SalvaPerPreparareEstaAsync();

        Assert.NotNull(nc);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.False(string.IsNullOrWhiteSpace(nc!.Id));
        Assert.NotNull(state.NonConformita.GetById(nc.Id));
        Assert.True(vm.PuoEsportarePacchetto);
    }

    [Fact]
    public async Task PreparaEsta_SuSegnalazioneNuovaNonValida_NonRestituisceNessunaNc()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });

        var nc = await vm.SalvaPerPreparareEstaAsync();

        Assert.Null(nc);
        Assert.Empty(state.NonConformita.GetAll());
    }

    // ----- Nuovi campi obbligatori (feedback utente 2026-09-16): Licenza, Ambito minimo, Stato iniziale.

    [Fact]
    public void Salva_SenzaLicenza_BloccaESegnalaCampoMancante()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        vm.RimuoviLicenzaCommand.Execute(vm.Licenze[0]);

        vm.SalvaCommand.Execute(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains("licenza", vm.MessaggioErrore, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(state.NonConformita.GetAll());
    }

    [Fact]
    public void Salva_SenzaAlcunElementoDiAmbito_BloccaESegnalaCampoMancante()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        vm.RimuoviEcuCommand.Execute("BCM"); // CompilaCampiObbligatori aggiunge BCM: lo togliamo per lasciare l'ambito vuoto

        vm.SalvaCommand.Execute(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains("ambito", vm.MessaggioErrore, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(state.NonConformita.GetAll());
    }

    [Fact]
    public void Salva_ConSoloVeicoloComeAmbito_NonBlocca()
    {
        // Basta UNO tra componente/Veicolo/Funzionalità/Strumento (feedback utente 2026-09-16), non tutti insieme.
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        vm.RimuoviEcuCommand.Execute("BCM");
        vm.AggiungiVeicoloCommand.Execute("Daily MY24");

        vm.SalvaCommand.Execute(null);

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.Single(state.NonConformita.GetAll());
    }

    [Fact]
    public void Salva_SenzaStatoScelto_BloccaESegnalaCampoMancante()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        vm.StatoScelta = null;

        vm.SalvaCommand.Execute(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains("stato", vm.MessaggioErrore, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(state.NonConformita.GetAll());
    }

    [Fact]
    public void Salva_ConLicenzeMultiple_LePersisteTutte()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        CompilaCampiObbligatori(vm);
        vm.NuovaLicenza = "LIC-2026-002";
        vm.AggiungiLicenzaCommand.Execute(null);

        vm.SalvaCommand.Execute(null);

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        var salvata = Assert.Single(state.NonConformita.GetAll());
        Assert.Equal(new[] { "LIC-2026-001", "LIC-2026-002" }, salvata.Licenze);
        Assert.Equal(StatoAnomalia.Confermata, salvata.StatoWorkflow);
    }

    // ----- Ticket TICKET/Ticketing (feedback utente 2026-09-16): stessa UI add/apri/rimuovi già presente su
    // Test e Change Request, prima assente su questo editor.

    [Fact]
    public void AggiungiTicket_ConCodiceValido_LoAggiungeAllaListaEAllaNc()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        vm.NuovoTicketRiferimento = "ticket-12345";
        vm.NuovoTicketUrl = "https://ticket.example.com/12345";

        vm.AggiungiTicketCommand.Execute(null);

        var ticket = Assert.Single(vm.TicketCollegatiVisual);
        Assert.Equal("TICKET-12345", ticket.Codice);
        Assert.Same(ticket, Assert.Single(vm.Nc.CollegamentiPolarion));
        Assert.Equal("", vm.NuovoTicketRiferimento);
    }

    [Fact]
    public void AggiungiTicket_ConFormatoNonValido_MostraErroreENonAggiungeNulla()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        vm.NuovoTicketRiferimento = "12345";

        vm.AggiungiTicketCommand.Execute(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.Empty(vm.TicketCollegatiVisual);
    }

    [Fact]
    public void RimuoviTicket_LoRimuoveDallaListaEDallaNc()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        vm.NuovoTicketRiferimento = "TICKET-999";
        vm.AggiungiTicketCommand.Execute(null);
        var ticket = Assert.Single(vm.TicketCollegatiVisual);

        vm.RimuoviTicketCommand.Execute(ticket);

        Assert.Empty(vm.TicketCollegatiVisual);
        Assert.Empty(vm.Nc.CollegamentiPolarion);
    }

    // ----- Split in NC separate, una per funzionalità (feedback utente 2026-09-16).

    private AppState CreateStateConDueFunzionalita()
    {
        var state = CreateState();
        var funzionalita = state.Master.Funzionalita.Load();
        funzionalita.Add(new Funzionalita { Nome = "Diagnosi climatizzazione", ModificatoDa = "Seed" });
        state.Master.Funzionalita.Save(funzionalita);
        return state;
    }

    [Fact]
    public void Salva_ConPiuFunzionalitaEConfermaAccettata_CreaUnaNcSeparataPerCiascuna()
    {
        var originale = ConfirmService.Handler;
        try
        {
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            var state = CreateStateConDueFunzionalita();
            var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(vm);
            vm.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            vm.AggiungiFunzionalitaCommand.Execute("Diagnosi climatizzazione");

            vm.SalvaCommand.Execute(null);

            Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
            var tutte = state.NonConformita.GetAll();
            Assert.Equal(2, tutte.Count);
            Assert.All(tutte, n => Assert.True(n.DaSegnalazioneRete));
            Assert.All(tutte, n => Assert.Contains("PRATICA-2026-4471", n.NumeriPraticaRete));
            Assert.All(tutte, n => Assert.Contains("LIC-2026-001", n.Licenze));
            Assert.Equal(new[] { "Diagnosi accensione" }, tutte.Single(n => n.FunzionalitaSegnalate.Contains("Diagnosi accensione")).FunzionalitaSegnalate);
            Assert.Equal(new[] { "Diagnosi climatizzazione" }, tutte.Single(n => n.FunzionalitaSegnalate.Contains("Diagnosi climatizzazione")).FunzionalitaSegnalate);
            // Stesso gruppo su entrambe, per il riferimento incrociato "sorelle".
            Assert.NotNull(tutte[0].GruppoSegnalazioneUid);
            Assert.Equal(tutte[0].GruppoSegnalazioneUid, tutte[1].GruppoSegnalazioneUid);
            Assert.NotEqual(tutte[0].Id, tutte[1].Id);
        }
        finally { ConfirmService.Handler = originale; }
    }

    [Fact]
    public void Salva_SplitInterrottoDopoLaPrimaNc_IlRetryNonDuplicaQuellaGiaCreata()
    {
        var originale = ConfirmService.Handler;
        try
        {
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            var state = CreateStateConDueFunzionalita();
            var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(vm);
            vm.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            vm.AggiungiFunzionalitaCommand.Execute("Diagnosi climatizzazione");
            // La seconda NC non può essere scritta: una cartella occupa il percorso del suo file.
            var primoId = state.NonConformita.NextId();
            var secondoId = $"NC-{int.Parse(primoId[3..]) + 1:D4}";
            var ostacolo = state.Paths.NonConformitaFile(secondoId);
            Directory.CreateDirectory(ostacolo);

            vm.SalvaCommand.Execute(null);

            Assert.True(vm.HaMessaggioErrore);
            var dopoErrore = Assert.Single(state.NonConformita.GetAll());
            Assert.Equal(new[] { "Diagnosi accensione" }, dopoErrore.FunzionalitaSegnalate);
            Assert.Equal(new[] { "Diagnosi climatizzazione" }, vm.Funzionalita);

            Directory.Delete(ostacolo);
            vm.SalvaCommand.Execute(null);

            Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
            var tutte = state.NonConformita.GetAll();
            Assert.Equal(2, tutte.Count);
            Assert.Single(tutte, n => n.FunzionalitaSegnalate.Contains("Diagnosi accensione"));
            Assert.Single(tutte, n => n.FunzionalitaSegnalate.Contains("Diagnosi climatizzazione"));
        }
        finally { ConfirmService.Handler = originale; }
    }

    [Fact]
    public void Salva_ConPiuFunzionalitaEConfermaRifiutata_NonCreaNulla()
    {
        var originale = ConfirmService.Handler;
        try
        {
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);
            var state = CreateStateConDueFunzionalita();
            var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(vm);
            vm.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            vm.AggiungiFunzionalitaCommand.Execute("Diagnosi climatizzazione");

            vm.SalvaCommand.Execute(null);

            Assert.Empty(state.NonConformita.GetAll());
        }
        finally { ConfirmService.Handler = originale; }
    }

    [Fact]
    public void SegnalazioniSorelle_DopoUnoSplit_TrovaLAltraNcDelloStessoGruppo()
    {
        var originale = ConfirmService.Handler;
        try
        {
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            var state = CreateStateConDueFunzionalita();
            var creazione = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(creazione);
            creazione.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            creazione.AggiungiFunzionalitaCommand.Execute("Diagnosi climatizzazione");
            creazione.SalvaCommand.Execute(null);
            var tutte = state.NonConformita.GetAll();
            var prima = tutte[0];

            var apertura = new SegnalazioneReteEditorViewModel(state, prima.Id, () => { });

            Assert.True(apertura.HaSegnalazioniSorelle);
            Assert.Equal(new[] { tutte[1].Id }, apertura.SegnalazioniSorelle);
        }
        finally { ConfirmService.Handler = originale; }
    }

    /// <summary>Feedback utente 2026-09-17: la scelta collega/crea nello split deve essere per funzionalità,
    /// non un'unica scelta valida per tutte insieme. Una delle due funzionalità ha già una NC aperta con la
    /// stessa componente, l'altra no: l'operatore accetta il collegamento solo per quella con un candidato.</summary>
    [Fact]
    public void Salva_ConPiuFunzionalitaEUnSoloCandidatoAccettato_CollegaSoloQuellaECreaUnaNuovaPerLAltra()
    {
        var originale = ConfirmService.Handler;
        try
        {
            var state = CreateStateConDueFunzionalita();
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            var prima = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(prima);
            prima.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            prima.SalvaCommand.Execute(null);
            var idEsistente = Assert.Single(state.NonConformita.GetAll()).Id;

            // Split con le due funzionalità: "Diagnosi accensione" ha un candidato (quella appena creata),
            // "Diagnosi climatizzazione" no. Accetta il collegamento solo per la prima.
            ConfirmService.Handler = (titolo, messaggio, _, _) => Task.FromResult(
                titolo != "Possibile non conformità già aperta" || messaggio.Contains("Diagnosi accensione"));
            var seconda = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(seconda);
            seconda.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            seconda.AggiungiFunzionalitaCommand.Execute("Diagnosi climatizzazione");

            seconda.SalvaCommand.Execute(null);

            Assert.False(seconda.HaMessaggioErrore, seconda.MessaggioErrore);
            var tutte = state.NonConformita.GetAll();
            // Nessuna NUOVA NC per "Diagnosi accensione" (collegata a quella esistente): solo la NC originale
            // più UNA nuova per "Diagnosi climatizzazione".
            Assert.Equal(2, tutte.Count);
            var originaleAggiornata = tutte.Single(n => n.Id == idEsistente);
            Assert.Contains("Diagnosi accensione", originaleAggiornata.FunzionalitaSegnalate);
            var nuova = tutte.Single(n => n.Id != idEsistente);
            Assert.Equal(new[] { "Diagnosi climatizzazione" }, nuova.FunzionalitaSegnalate);
        }
        finally { ConfirmService.Handler = originale; }
    }

    [Fact]
    public void ApriSorellaCommand_ConCallbackIniettato_InvocaLaNavigazioneConLIdCorretto()
    {
        var state = CreateState();
        var idAperto = new List<string>();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { }, id => idAperto.Add(id));

        vm.ApriSorellaCommand.Execute("NC-9999");

        Assert.Equal(new[] { "NC-9999" }, idAperto);
    }

    [Fact]
    public void ApriSorellaCommand_SenzaCallbackIniettato_NonLanciaEccezioni()
    {
        var state = CreateState();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });

        vm.ApriSorellaCommand.Execute("NC-9999"); // nessun callback passato: non deve lanciare nulla
    }

    // ----- Controllo duplicati (feedback utente 2026-09-16): "non devo creare una non conformità per errore
    // se già presente in uno dei nostri test, piuttosto aggiungo il caso/segnalazione a quella non conformità".

    [Fact]
    public void Salva_ConNcApertaSuStessaEcuEFunzionalita_SeConfermatoCollegaInveceDiCreareUnaNuova()
    {
        var originale = ConfirmService.Handler;
        try
        {
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true); // conferma sempre "collega"/"procedi"
            var state = CreateState();
            var prima = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(prima);
            prima.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            prima.SalvaCommand.Execute(null);
            Assert.False(prima.HaMessaggioErrore, prima.MessaggioErrore);
            var idEsistente = Assert.Single(state.NonConformita.GetAll()).Id;

            var seconda = new SegnalazioneReteEditorViewModel(state, null, () => { });
            seconda.NuovoNumeroPratica = "PRATICA-2026-9999";
            seconda.AggiungiNumeroPraticaCommand.Execute(null);
            seconda.NuovoVin = "ZCFC000000000000X";
            seconda.AggiungiVinCommand.Execute(null);
            seconda.NuovaLicenza = "LIC-2026-999";
            seconda.AggiungiLicenzaCommand.Execute(null);
            seconda.DescrizioneBreve = "Stesso guasto riportato da un altro concessionario";
            seconda.Descrizione = "Anche qui il quadro strumenti resta spento dopo il KL15 ON.";
            seconda.Severita = SeveritaAnomalia.High;
            seconda.PrioritaScelta = PrioritaOperativa.Normal;
            seconda.StatoScelta = StatoAnomalia.Confermata;
            seconda.AggiungiEcuCommand.Execute("BCM");
            seconda.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");

            seconda.SalvaCommand.Execute(null);

            Assert.False(seconda.HaMessaggioErrore, seconda.MessaggioErrore);
            // Nessuna NUOVA NC: solo quella originale, ora con i dati della seconda segnalazione uniti.
            var unica = Assert.Single(state.NonConformita.GetAll());
            Assert.Equal(idEsistente, unica.Id);
            Assert.Contains("PRATICA-2026-9999", unica.NumeriPraticaRete);
            Assert.Contains("ZCFC000000000000X", unica.VinCliente);
            Assert.Contains("LIC-2026-999", unica.Licenze);
            Assert.Contains("Stesso guasto riportato da un altro concessionario", unica.Descrizione);
            Assert.Contains(idEsistente, seconda.Messaggio ?? "");
        }
        finally { ConfirmService.Handler = originale; }
    }

    [Fact]
    public async Task Salva_CollegaNcEsistente_CopiaGliAllegatiCaricatiPrimaDelPrimoSalvataggio()
    {
        var originale = ConfirmService.Handler;
        try
        {
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            var state = CreateState();
            var prima = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(prima);
            prima.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            await prima.SalvaCommand.ExecuteAsync(null);
            var esistente = Assert.Single(state.NonConformita.GetAll());
            var copiaArchiviataSorgente = Path.Combine(_dir, "mail-archiviata.txt");
            File.WriteAllText(copiaArchiviataSorgente, "messaggio Outlook collegato");
            var archiviato = await AttachmentService.SalvaAsync(state.Paths, esistente, copiaArchiviataSorgente,
                state.Config.OperatoreAttivo);
            archiviato.Archiviata = true;
            esistente.Allegati.Add(archiviato);
            state.NonConformita.Save(esistente, esistente.RevisioneDati);

            var seconda = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(seconda);
            seconda.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            var daRimuovere = Path.Combine(_dir, "rimosso-prima-del-salvataggio.txt");
            File.WriteAllText(daRimuovere, "allegato tolto dal form");
            await seconda.AggiungiAllegatoCommand.ExecuteAsync(daRimuovere);
            var allegatoRimosso = Assert.Single(seconda.Nc.Allegati);
            var percorsoRimosso = AttachmentService.PercorsoCompleto(state.Paths, seconda.Nc, allegatoRimosso);
            seconda.RimuoviAllegatoCommand.Execute(allegatoRimosso);
            Assert.True(File.Exists(percorsoRimosso)); // la rimozione fisica attende un'operazione riuscita
            var file = Path.Combine(_dir, "mail-collegata.msg");
            File.WriteAllBytes(file, "messaggio Outlook collegato"u8.ToArray());
            await seconda.AggiungiAllegatoCommand.ExecuteAsync(file);
            var allegatoBozza = Assert.Single(seconda.Nc.Allegati);
            var percorsoBozza = AttachmentService.PercorsoCompleto(state.Paths, seconda.Nc, allegatoBozza);

            await seconda.SalvaCommand.ExecuteAsync(null);

            Assert.False(seconda.HaMessaggioErrore, seconda.MessaggioErrore);
            var salvata = state.NonConformita.GetById(esistente.Id)!;
            Assert.Single(salvata.Allegati, a => a.Archiviata);
            var allegatoPersistito = Assert.Single(salvata.Allegati, a => !a.Archiviata);
            var percorsoPersistito = AttachmentService.PercorsoCompleto(state.Paths, salvata, allegatoPersistito);
            Assert.True(File.Exists(percorsoPersistito));
            Assert.Equal("messaggio Outlook collegato", File.ReadAllText(percorsoPersistito));
            Assert.False(File.Exists(percorsoBozza));
            Assert.False(File.Exists(percorsoRimosso));
        }
        finally { ConfirmService.Handler = originale; }
    }

    [Fact]
    public async Task Salva_CollegaNcEsistente_ConservaBucketReferenziatoDaBozzaDiRecupero()
    {
        var originale = ConfirmService.Handler;
        try
        {
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            var state = CreateState();
            var prima = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(prima);
            prima.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            await prima.SalvaCommand.ExecuteAsync(null);

            var seconda = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(seconda);
            seconda.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            var sorgente = Path.Combine(_dir, "evidenza-bozza.txt");
            File.WriteAllText(sorgente, "contenuto recuperabile");
            await seconda.AggiungiAllegatoCommand.ExecuteAsync(sorgente);
            var allegato = Assert.Single(seconda.Nc.Allegati);
            var percorsoBozza = AttachmentService.PercorsoCompleto(state.Paths, seconda.Nc, allegato);
            FailedSaveDraftService.Save(state.Paths.Root, "nonconformita", seconda.Nc.Uid, 0,
                state.Config.OperatoreAttivo, EntityStateFingerprint.Clone(seconda.Nc), "Salvataggio iniziale fallito");

            await seconda.SalvaCommand.ExecuteAsync(null);

            Assert.False(seconda.HaMessaggioErrore, seconda.MessaggioErrore);
            Assert.True(File.Exists(percorsoBozza)); // la bozza recuperabile continua a referenziare byte presenti
            Assert.Single(FailedSaveDraftInspector.ListPage(state.Paths.Root, 0, 10,
                domain: "nonconformita", entityUid: seconda.Nc.Uid));
            var destinazione = Assert.Single(state.NonConformita.GetAll().Single().Allegati);
            Assert.True(File.Exists(AttachmentService.PercorsoCompleto(state.Paths,
                state.NonConformita.GetAll().Single(), destinazione)));
        }
        finally { ConfirmService.Handler = originale; }
    }

    [Fact]
    public void Salva_ConNcApertaSuStessaEcuEFunzionalitaMaOperatoreRifiutaIlCollegamento_CreaComunqueUnaNuovaNc()
    {
        var originale = ConfirmService.Handler;
        try
        {
            var state = CreateState();
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            var prima = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(prima);
            prima.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            prima.SalvaCommand.Execute(null);

            // Questa volta l'operatore rifiuta il collegamento proposto ("crea comunque una nuova NC").
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);
            var seconda = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(seconda);
            seconda.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");

            seconda.SalvaCommand.Execute(null);

            Assert.False(seconda.HaMessaggioErrore, seconda.MessaggioErrore);
            Assert.Equal(2, state.NonConformita.GetAll().Count);
        }
        finally { ConfirmService.Handler = originale; }
    }

    [Fact]
    public void Salva_ConNcCandidataMaGiaChiusa_NonProponeIlCollegamentoECreaUnaNuovaNc()
    {
        var originale = ConfirmService.Handler;
        try
        {
            var state = CreateState();
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            var prima = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(prima);
            prima.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
            prima.SalvaCommand.Execute(null);
            var esistente = Assert.Single(state.NonConformita.GetAll());
            esistente.StatoWorkflow = StatoAnomalia.Conclusa; // chiusa: non più un candidato da collegare
            state.NonConformita.Save(esistente, esistente.RevisioneDati);

            // Se il popup di collegamento scattasse per errore anche su una NC chiusa, questo handler che
            // rifiuta SEMPRE farebbe fallire il test creando comunque una NC in più solo per lo split — qui
            // non c'è split (una sola funzionalità), quindi qualunque richiesta di conferma sarebbe indebita.
            ConfirmService.Handler = (_, _, _, _) => throw new InvalidOperationException("Non doveva chiedere conferma: la NC candidata è già chiusa.");
            var seconda = new SegnalazioneReteEditorViewModel(state, null, () => { });
            CompilaCampiObbligatori(seconda);
            seconda.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");

            seconda.SalvaCommand.Execute(null);

            Assert.False(seconda.HaMessaggioErrore, seconda.MessaggioErrore);
            Assert.Equal(2, state.NonConformita.GetAll().Count);
        }
        finally { ConfirmService.Handler = originale; }
    }
}
