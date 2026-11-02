using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copre le checkpoint del grill-me 2026-08-21 sulla CR: raggruppamento veicoli per gamma
/// (punto 3/4), allegati (punto 6), pacchetto export (punto 7) e popup campi mancanti (punto 8).</summary>
public sealed class ChangeRequestEditorViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ChangeRequestEditor_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private (AppState State, TestTemplate TemplateCr) CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var templateCr = new TestTemplate { Nome = "Change Request", IsChangeRequest = true };
        var tutti = state.Master.TestTemplate.Load().ToList();
        tutti.Add(templateCr);
        state.Master.TestTemplate.Save(tutti);

        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = "Daily 24", GammaNome = "MY24" },
            new() { Nome = "eDaily 24", GammaNome = "MY24" },
            new() { Nome = "Alpha 24", GammaNome = "Alpha" },
            new() { Nome = "Banco singolo", GammaNome = "" },
        });
        state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Crea("Mario Rossi", RuoloOperatore.Standard) });
        Assert.True(state.ImpostaOperatore("Mario Rossi", "1234"));
        return (state, templateCr);
    }

    [Fact]
    public void AggiungiVeicoliDaGamme_AggiungeTuttiIVeicoliDelleGammeScelte_SenzaDuplicati()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };

        vm.AggiungiVeicoliDaGamme(new[] { "MY24" });
        Assert.Equal(2, vm.Veicoli.Count);
        Assert.Contains("Daily 24", vm.Veicoli);
        Assert.Contains("eDaily 24", vm.Veicoli);

        // Riselezionare la stessa gamma non deve duplicare i badge (stesso helper Aggiungi usato dal resto della pagina).
        vm.AggiungiVeicoliDaGamme(new[] { "MY24" });
        Assert.Equal(2, vm.Veicoli.Count);

        vm.AggiungiVeicoliDaGamme(new[] { "Alpha" });
        Assert.Equal(3, vm.Veicoli.Count);
        Assert.Contains("Alpha 24", vm.Veicoli);
    }

    /// <summary>Copertura del bug segnalato 2026-08-24: "Seleziona tutto" nel picker componente proponeva l'intero
    /// catalogo invece delle sole componente montate sui veicoli già scelti per la CR — EcuDisponibili non filtrava
    /// affatto (a differenza di AuditMatriceService.EcuDelSottoveicolo, già usato altrove per lo stesso scopo).</summary>
    [Fact]
    public void EcuDisponibili_NessunVeicoloScelto_TornaCatalogoIntero()
    {
        var (state, templateCr) = CreaStato();
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = new List<string> { "Daily 24" } },
            new() { Nome = "EDC", VeicoliAssociati = new List<string> { "Alpha 24" } },
        });
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };

        Assert.Equal(new[] { "BCM", "EDC" }, vm.EcuDisponibili);
    }

    [Fact]
    public void EcuDisponibili_ConVeicoloScelto_SoloLeEcuDiQuelVeicolo()
    {
        var (state, templateCr) = CreaStato();
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = new List<string> { "Daily 24" } },
            new() { Nome = "EDC", VeicoliAssociati = new List<string> { "Alpha 24" } },
            new() { Nome = "SGW", VeicoliAssociati = new List<string> { "Daily 24", "Alpha 24" } },
        });
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };

        vm.AggiungiVeicoli(new[] { "Daily 24" });

        Assert.Equal(new[] { "BCM", "SGW" }, vm.EcuDisponibili);
    }

    [Fact]
    public void EcuDisponibili_PiuVeicoliScelti_UnioneDelleEcuDiCiascuno()
    {
        var (state, templateCr) = CreaStato();
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = new List<string> { "Daily 24" } },
            new() { Nome = "EDC", VeicoliAssociati = new List<string> { "Alpha 24" } },
            new() { Nome = "SGW", VeicoliAssociati = new List<string> { "Daily 24", "Alpha 24" } },
        });
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };

        vm.AggiungiVeicoli(new[] { "Daily 24", "Alpha 24" });

        Assert.Equal(new[] { "BCM", "EDC", "SGW" }, vm.EcuDisponibili);
    }

    [Fact]
    public void EcuDisponibili_RimuovereIlVeicolo_TornaAlCatalogoIntero()
    {
        var (state, templateCr) = CreaStato();
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = new List<string> { "Daily 24" } },
            new() { Nome = "EDC", VeicoliAssociati = new List<string> { "Alpha 24" } },
        });
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };

        vm.AggiungiVeicoli(new[] { "Daily 24" });
        Assert.Single(vm.EcuDisponibili);
        vm.RimuoviVeicoloCommand.Execute("Daily 24");

        Assert.Equal(new[] { "BCM", "EDC" }, vm.EcuDisponibili);
    }

    [Fact]
    public void VeicoliRaggruppati_GruppoPiccolo_PartePreEspanso_GruppoGrande_PartCollassato()
    {
        var (state, templateCr) = CreaStato();
        state.Master.Veicoli.Save(Enumerable.Range(1, 6).Select(i => new Veicolo { Nome = $"V{i}", GammaNome = "Grande" })
            .Append(new Veicolo { Nome = "Piccolo1", GammaNome = "Piccola" }).Append(new Veicolo { Nome = "Piccolo2", GammaNome = "Piccola" }).ToList());
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };

        vm.AggiungiVeicoliDaGamme(new[] { "Grande", "Piccola" });

        var grande = vm.VeicoliRaggruppati.Single(g => g.Etichetta == "Grande");
        var piccola = vm.VeicoliRaggruppati.Single(g => g.Etichetta == "Piccola");
        Assert.False(grande.Espanso);
        Assert.True(piccola.Espanso);

        vm.ToggleGruppoVeicoliCommand.Execute("Grande");
        Assert.True(vm.VeicoliRaggruppati.Single(g => g.Etichetta == "Grande").Espanso);
    }

    [Fact]
    public void Valida_AmbitoMancante_PuntaAllaCardAmbitoImpattato_EAlzaIlPopup()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr, Titolo = "Titolo valido" };
        var popupAlzato = false;
        vm.RichiestaMostraCampiMancanti += () => popupAlzato = true;

        vm.SalvaCommand.Execute(null);

        Assert.True(popupAlzato);
        Assert.Contains(vm.CampiMancanti, c => c.Chiave == "AmbitoImpattato");
        Assert.True(vm.IsNew); // il salvataggio deve essere stato bloccato, non deve aver generato un Id
    }

    [Fact]
    public void Valida_TitoloMancante_PuntaAllaCardDatiCr()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };
        vm.AggiungiVeicoli(new[] { "Daily 24" });

        vm.SalvaCommand.Execute(null);

        Assert.Contains(vm.CampiMancanti, c => c.Chiave == "DatiCr" && c.Messaggio.Contains("titolo"));
    }

    [Fact]
    public void Salva_ConAmbitoETitoloValidi_Riesce_ENonAlzaIlPopup()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr, Titolo = "CR valida" };
        vm.AggiungiVeicoli(new[] { "Daily 24" });
        var popupAlzato = false;
        vm.RichiestaMostraCampiMancanti += () => popupAlzato = true;

        vm.SalvaCommand.Execute(null);

        Assert.False(popupAlzato);
        Assert.False(vm.IsNew);
        Assert.Empty(vm.CampiMancanti);
    }

    [Fact]
    public async Task AggiungiAllegato_SuCrNonAncoraSalvata_VieneBloccato()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };
        var file = Path.Combine(_dir, "nota.txt");
        File.WriteAllText(file, "contenuto");

        Assert.True(vm.IsNew);
        await vm.AggiungiAllegatoCommand.ExecuteAsync(file);

        Assert.Empty(vm.AllegatiVisual);
        Assert.Contains("Salva la Change Request", vm.Errore);
    }

    [Fact]
    public async Task AggiungiERimuoviAllegato_SuCrSalvata_AggiornaListaEFile()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr, Titolo = "CR con allegati" };
        vm.AggiungiVeicoli(new[] { "Daily 24" });
        vm.SalvaCommand.Execute(null);
        Assert.False(vm.IsNew);

        var file = Path.Combine(_dir, "evidenza.txt");
        File.WriteAllText(file, "contenuto");
        await vm.AggiungiAllegatoCommand.ExecuteAsync(file);

        Assert.Single(vm.AllegatiVisual);
        var allegato = vm.AllegatiVisual[0];
        Assert.Equal("evidenza.txt", allegato.NomeFile);
        var percorsoAllegato = AttachmentService.PercorsoCompleto(state.Paths, vm.ChangeRequest, allegato);
        Assert.True(File.Exists(percorsoAllegato));

        vm.RimuoviAllegatoCommand.Execute(allegato);

        Assert.Empty(vm.AllegatiVisual);
        // Il binario resta nel percorso attivo finche' il Salva non persiste la CR senza l'allegato.
        Assert.True(File.Exists(percorsoAllegato));

        vm.SalvaCommand.Execute(null);

        Assert.Empty(vm.Errore);
        Assert.False(File.Exists(percorsoAllegato));
        Assert.True(File.Exists(AttachmentService.PercorsoCompleto(state.Paths, vm.ChangeRequest, allegato)));
    }

    [Fact]
    public async Task RimozioneAllegato_AbbandonoSenzaSalvataggio_LasciaFileERecordInvariati()
    {
        var (state, templateCr) = CreaStato();
        var vm = CreaCrSalvata(state, templateCr, out var id);
        var file = Path.Combine(_dir, "abbandono.txt");
        File.WriteAllText(file, "evidenza da non perdere");
        await vm.AggiungiAllegatoCommand.ExecuteAsync(file);
        vm.SalvaCommand.Execute(null); // l'allegato entra nel record persistito
        Assert.Empty(vm.Errore);
        var allegato = vm.AllegatiVisual.Single();
        var attivo = AttachmentService.PercorsoCompleto(state.Paths, vm.ChangeRequest, allegato);

        vm.RimuoviAllegatoCommand.Execute(allegato);
        ((IConfirmedAbandonmentParticipant)vm).PrepareConfirmedAbandonmentCleanup(); // abbandono confermato
        await AttachmentRemovalTestSupport.AttendiCallbackAbbandono();

        Assert.True(File.Exists(attivo));
        Assert.Equal("evidenza da non perdere", File.ReadAllText(attivo));
        var persistita = state.ChangeRequests.GetById(id)!;
        Assert.Single(persistita.Allegati);
        Assert.Equal(allegato.Uid, persistita.Allegati[0].Uid);
    }

    [Fact]
    public async Task RimozioneAllegato_SalvataggioRiuscito_SpostaIlBinarioInRecoveryEAggiornaIlRecord()
    {
        var (state, templateCr) = CreaStato();
        var vm = CreaCrSalvata(state, templateCr, out var id);
        var file = Path.Combine(_dir, "successo.txt");
        File.WriteAllText(file, "contenuto");
        await vm.AggiungiAllegatoCommand.ExecuteAsync(file);
        vm.SalvaCommand.Execute(null);
        var allegato = vm.AllegatiVisual.Single();
        var attivo = AttachmentService.PercorsoCompleto(state.Paths, vm.ChangeRequest, allegato);

        vm.RimuoviAllegatoCommand.Execute(allegato);
        vm.SalvaCommand.Execute(null);

        Assert.Empty(vm.Errore);
        Assert.False(File.Exists(attivo));
        Assert.Equal("contenuto", File.ReadAllText(AttachmentService.PercorsoCompleto(state.Paths, vm.ChangeRequest, allegato)));
        Assert.Empty(state.ChangeRequests.GetById(id)!.Allegati);
    }

    [Fact]
    public async Task RimozioneAllegato_SalvataggioFallitoPerConflitto_ConservaFileEAllegatoNelRecord()
    {
        var (state, templateCr) = CreaStato();
        var mio = CreaCrSalvata(state, templateCr, out var id);
        var file = Path.Combine(_dir, "conflitto.txt");
        File.WriteAllText(file, "contenuto conflitto");
        await mio.AggiungiAllegatoCommand.ExecuteAsync(file);
        mio.SalvaCommand.Execute(null);
        Assert.Empty(mio.Errore);
        var allegato = mio.AllegatiVisual.Single();
        var attivo = AttachmentService.PercorsoCompleto(state.Paths, mio.ChangeRequest, allegato);

        var altro = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        altro.Titolo = "Modificata da un altro PC";
        altro.SalvaCommand.Execute(null);
        Assert.Empty(altro.Errore);
        Assert.Equal("Modificata da un altro PC", state.ChangeRequests.GetById(id)!.Titolo);
        Assert.NotEqual(mio.ChangeRequest.RevisioneDati, state.ChangeRequests.GetById(id)!.RevisioneDati);
        Assert.Equal(mio.ChangeRequest.RevisioneDati, (long)typeof(ChangeRequestEditorViewModel)
            .GetField("_revisioneAttesa", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(mio)!);

        mio.RimuoviAllegatoCommand.Execute(allegato);
        mio.SalvaCommand.Execute(null); // revisione superata: il Salva fallisce e non deve toccare il binario

        Assert.NotEmpty(mio.Errore);
        Assert.True(mio.ModificheNonSalvate);
        Assert.True(File.Exists(attivo));
        Assert.Equal("contenuto conflitto", File.ReadAllText(attivo));
        Assert.Single(state.ChangeRequests.GetById(id)!.Allegati);
    }

    [Fact]
    public async Task AggiuntaAllegato_AbbandonoConfermato_RimuoveLaCopiaMaiPersistita()
    {
        var (state, templateCr) = CreaStato();
        var vm = CreaCrSalvata(state, templateCr, out var id);
        var file = Path.Combine(_dir, "bozza.txt");
        File.WriteAllText(file, "solo in bozza");
        await vm.AggiungiAllegatoCommand.ExecuteAsync(file);
        var allegato = vm.AllegatiVisual.Single();
        var attivo = AttachmentService.PercorsoCompleto(state.Paths, vm.ChangeRequest, allegato);
        Assert.True(File.Exists(attivo)); // la copia avviene subito, come nel Test (crash = orfano accettato)

        ((IConfirmedAbandonmentParticipant)vm).PrepareConfirmedAbandonmentCleanup();
        await AttachmentRemovalTestSupport.AttendiCallbackAbbandono();

        Assert.False(File.Exists(attivo));
        Assert.Empty(state.ChangeRequests.GetById(id)!.Allegati);
    }

    [Fact]
    public async Task AggiuntaERimozioneNellaSessione_AbbandonoConfermato_RimuoveLaCopiaMaiPersistita()
    {
        var (state, templateCr) = CreaStato();
        var vm = CreaCrSalvata(state, templateCr, out var id);
        var file = Path.Combine(_dir, "bozza-rimossa.txt");
        File.WriteAllText(file, "aggiunto e tolto nella sessione");
        await vm.AggiungiAllegatoCommand.ExecuteAsync(file);
        var allegato = vm.AllegatiVisual.Single();
        var attivo = AttachmentService.PercorsoCompleto(state.Paths, vm.ChangeRequest, allegato);
        vm.RimuoviAllegatoCommand.Execute(allegato);

        ((IConfirmedAbandonmentParticipant)vm).PrepareConfirmedAbandonmentCleanup();
        await AttachmentRemovalTestSupport.AttendiCallbackAbbandono();

        Assert.False(File.Exists(attivo));
        Assert.Empty(state.ChangeRequests.GetById(id)!.Allegati);
    }

    [Fact]
    public async Task EsportaPacchetto_ConCrSalvata_GeneraUnoZipReale()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr, Titolo = "CR da esportare" };
        vm.AggiungiVeicoli(new[] { "Daily 24" });
        vm.SalvaCommand.Execute(null);

        var destinazione = Path.Combine(_dir, "export");
        vm.EsportaPacchettoCommand.Execute(new RichiestaPacchettoPolarion(destinazione, new HashSet<string>(vm.SezioniPacchettoDisponibili())));
        // Il pacchetto si genera su thread di background: l'esito e pronto solo a comando concluso.
        await (vm.EsportaPacchettoCommand.ExecutionTask ?? Task.CompletedTask);

        Assert.Empty(vm.Errore);
        Assert.Contains("Pacchetto CR esportato", vm.Messaggio);
        Assert.True(Directory.Exists(destinazione));
        Assert.Single(Directory.GetFiles(destinazione, "*.zip"));
    }

    [Fact]
    public void EsportaPacchetto_SuCrNonAncoraSalvata_VieneBloccato()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };

        vm.EsportaPacchettoCommand.Execute(new RichiestaPacchettoPolarion(Path.Combine(_dir, "export"), new HashSet<string>(CrSezioni.Tutte)));

        Assert.Contains("Salva la Change Request", vm.Errore);
    }

    /// <summary>Feedback utente 2026-09-15: "il creatore dovrebbe essere comunque sempre presente" — a
    /// differenza di Responsabile (modificabile in qualunque momento), CreatoDa si imposta una sola volta alla
    /// creazione e resta invariato anche dopo un salvataggio e una modifica successiva del Responsabile.</summary>
    [Fact]
    public void CreatoDa_ImpostatoAllaCreazioneEMaiSovrascrittoDaSalvataggiSuccessivi()
    {
        var (state, templateCr) = CreaStato();
        var operatori = state.Master.Operatori.Load().ToList();
        operatori.Add(TestOperatori.Crea("Altro Operatore", RuoloOperatore.Standard));
        state.Master.Operatori.Save(operatori);
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };
        vm.Titolo = "Prima CR";
        vm.Descrizione = "Descrizione";
        vm.AggiungiVeicoli(new[] { "Daily 24" });

        Assert.Equal("Mario Rossi", vm.CreatoDa);

        vm.SalvaCommand.Execute(null);
        Assert.Empty(vm.Errore);
        var id = vm.ChangeRequest.Id;

        Assert.True(state.ImpostaOperatore("Altro Operatore", "1234"));
        var riaperto = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        riaperto.Responsabile = "Altro Operatore";
        riaperto.SalvaCommand.Execute(null);

        var salvata = state.ChangeRequests.GetById(id)!;
        Assert.Equal("Mario Rossi", salvata.CreatoDa);
        Assert.Equal("Altro Operatore", salvata.Responsabile);
    }

    /// <summary>Feedback utente 2026-09-15: "nelle Cr dovrebbe poi anche essere possibile collegare il ticket
    /// TICKET come avviene per le non conformità" — il campo ChangeRequest.TicketCollegati esisteva già (usato
    /// solo dall'export), ma l'editor non aveva alcuna UI/comando per popolarlo.</summary>
    [Fact]
    public void AggiungiTicket_FormatoValido_LoAggiungeEPersisteAlSalvataggio()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };
        vm.Titolo = "CR con ticket";
        vm.Descrizione = "Descrizione";
        vm.AggiungiVeicoli(new[] { "Daily 24" });
        vm.NuovoTicketRiferimento = "ticket-1234";
        vm.NuovoTicketUrl = "https://ticketing.example/ticket-1234";

        vm.AggiungiTicketCommand.Execute(null);

        Assert.Empty(vm.Errore);
        var ticket = Assert.Single(vm.TicketCollegatiVisual);
        Assert.Equal("TICKET-1234", ticket.Riferimento);
        Assert.Equal("", vm.NuovoTicketRiferimento);

        vm.SalvaCommand.Execute(null);
        var salvata = state.ChangeRequests.GetById(vm.ChangeRequest.Id)!;
        Assert.Contains(salvata.TicketCollegati, t => t.Riferimento == "TICKET-1234");
    }

    [Fact]
    public void AggiungiTicket_FormatoNonValido_MostraErroreENonAggiungeNulla()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };
        vm.NuovoTicketRiferimento = "non-ticket";

        vm.AggiungiTicketCommand.Execute(null);

        Assert.NotEmpty(vm.Errore);
        Assert.Empty(vm.TicketCollegatiVisual);
    }

    [Fact]
    public void RimuoviTicket_LoRimuoveDallaListaVisualEDalModello()
    {
        var (state, templateCr) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };
        vm.NuovoTicketRiferimento = "TICKET-9999";
        vm.AggiungiTicketCommand.Execute(null);
        var ticket = Assert.Single(vm.TicketCollegatiVisual);

        vm.RimuoviTicketCommand.Execute(ticket);

        Assert.Empty(vm.TicketCollegatiVisual);
        Assert.Empty(vm.ChangeRequest.TicketCollegati);
    }

    /// <summary>Review 2026-09-30: Salva/Implementa mutavano la CR live (Id, revisioni, stato) PRIMA del Save; un
    /// conflitto di revisione lasciava la UI in uno stato mai persistito e un retry duplicava la revisione.</summary>
    private static ChangeRequestEditorViewModel CreaCrSalvata(AppState state, TestTemplate templateCr, out string id)
    {
        Assert.True(state.ImpostaOperatore("Mario Rossi", "1234"));
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };
        vm.Titolo = "CR conflitto"; vm.Descrizione = "Descrizione"; vm.Responsabile = "Mario Rossi";
        vm.AggiungiVeicoli(new[] { "Daily 24" });
        vm.SalvaCommand.Execute(null);
        Assert.Empty(vm.Errore);
        id = vm.ChangeRequest.Id;
        return vm;
    }

    [Fact]
    public void Salva_ConConflittoDiRevisione_NonLanciaNonMutaLaCrENonDuplicaLaRevisioneAlRetry()
    {
        var (state, templateCr) = CreaStato();
        CreaCrSalvata(state, templateCr, out var id);
        var mio = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        var altro = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        altro.Titolo = "Modificata da un altro PC";
        altro.SalvaCommand.Execute(null);
        Assert.Empty(altro.Errore);

        var revisioniPrima = mio.ChangeRequest.Revisioni.Count;
        var revisioneDatiPrima = mio.ChangeRequest.RevisioneDati;
        mio.Titolo = "La mia modifica";
        mio.SalvaCommand.Execute(null); // non deve lanciare
        mio.SalvaCommand.Execute(null); // retry: stesso esito, nessuna revisione in piu'

        Assert.NotEmpty(mio.Errore);
        Assert.True(mio.ModificheNonSalvate);
        Assert.Equal(revisioniPrima, mio.ChangeRequest.Revisioni.Count);
        Assert.Equal(revisioneDatiPrima, mio.ChangeRequest.RevisioneDati);
        Assert.Equal("Modificata da un altro PC", state.ChangeRequests.GetById(id)!.Titolo);
    }

    [Fact]
    public void Salva_SenzaModificheNonScriveENonAggiungeRevisioni()
    {
        var (state, templateCr) = CreaStato();
        CreaCrSalvata(state, templateCr, out var id);
        var prima = state.ChangeRequests.GetById(id)!;
        var revisioni = prima.Revisioni.Count;
        var revisioneDati = prima.RevisioneDati;
        var ultimaModifica = prima.UltimaModifica;
        var vm = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });

        vm.SalvaCommand.Execute(null);

        var dopo = state.ChangeRequests.GetById(id)!;
        Assert.Empty(vm.Errore);
        Assert.False(vm.ModificheNonSalvate);
        Assert.Equal(revisioni, dopo.Revisioni.Count);
        Assert.Equal(revisioneDati, dopo.RevisioneDati);
        Assert.Equal(ultimaModifica, dopo.UltimaModifica);
    }

    [Fact]
    public void Salva_RilevaUnCampoAnnidatoModificatoAncheSeIlFlagNonEImpostato()
    {
        var (state, templateCr) = CreaStato();
        CreaCrSalvata(state, templateCr, out var id);
        var prima = state.ChangeRequests.GetById(id)!;
        var revisioni = prima.Revisioni.Count;
        var vm = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        vm.ChangeRequest.TicketCollegati.Add(new TicketRif { Riferimento = "TICKET-9912", Url = "https://ticket.example/ticket/9912" });
        Assert.False(vm.ModificheNonSalvate); // Mutazione annidata diretta: la decisione non si basa sul dirty flag.

        vm.SalvaCommand.Execute(null);

        Assert.Empty(vm.Errore);
        var dopo = state.ChangeRequests.GetById(id)!;
        Assert.Contains(dopo.TicketCollegati, t => t.Riferimento == "TICKET-9912");
        Assert.Equal(revisioni + 1, dopo.Revisioni.Count);
    }

    [Fact]
    public void RecuperaBozzaCr_ApplicaSoloCampiSelezionatiEMantieneLaRevisioneCanonica()
    {
        var (state, templateCr) = CreaStato();
        CreaCrSalvata(state, templateCr, out var id);
        var vm = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        var baselineTitle = vm.ChangeRequest.Titolo;
        var baselineDescription = vm.ChangeRequest.Descrizione;
        var baselineRevision = vm.ChangeRequest.RevisioneDati;
        var draft = System.Text.Json.JsonSerializer.Deserialize<ChangeRequest>(
            System.Text.Json.JsonSerializer.Serialize(vm.ChangeRequest, JsonIo.Options), JsonIo.Options)!;
        draft.Titolo = "Titolo recuperato";
        draft.Descrizione = "Descrizione recuperata";

        vm.ApplicaBozzaRecuperata(draft, [nameof(ChangeRequest.Titolo)]);

        Assert.Equal("Titolo recuperato", vm.ChangeRequest.Titolo);
        Assert.Equal(baselineDescription, vm.ChangeRequest.Descrizione);
        Assert.NotEqual("Titolo recuperato", baselineTitle);
        Assert.Equal(baselineRevision, vm.ChangeRequest.RevisioneDati);
        Assert.True(vm.ModificheNonSalvate);
        Assert.Contains("Titolo", vm.Messaggio);
        Assert.Equal(baselineTitle, state.ChangeRequests.GetById(id)!.Titolo);
    }

    [Fact]
    public void Implementa_ConConflittoDiRevisione_NonCambiaStatoNeImplementazioneInMemoria()
    {
        var (state, templateCr) = CreaStato();
        CreaCrSalvata(state, templateCr, out var id);
        var mio = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        var altro = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        altro.Titolo = "Modificata da un altro PC";
        altro.SalvaCommand.Execute(null);
        var statoPrima = mio.ChangeRequest.Stato;
        var revisioniPrima = mio.ChangeRequest.Revisioni.Count;

        mio.ImplementaCommand.Execute(null);

        Assert.NotEmpty(mio.Errore);
        Assert.Equal(statoPrima, mio.ChangeRequest.Stato);
        Assert.Null(mio.ChangeRequest.ImplementataIl);
        Assert.Equal(revisioniPrima, mio.ChangeRequest.Revisioni.Count);
        Assert.Equal(StatoChangeRequest.Aperta, state.ChangeRequests.GetById(id)!.Stato);
    }

    /// <summary>Review 2026-09-30 (HIGH): dopo un Salva riuscito la CR live viene sostituita da una copia; le righe
    /// dei campi custom devono essere ri-legate al dizionario della nuova istanza, altrimenti una modifica
    /// successiva finisce sul dizionario vecchio e il secondo Salva la perde in silenzio.</summary>
    [Fact]
    public void Salva_PoiModificaCampoCustomESalvaDiNuovo_PersisteIlValore()
    {
        var (state, templateCr) = CreaStato();
        var campo = new CampoTemplate { Tipo = TipoCampoTemplate.CampoLibero, Nome = "Nota", TipoLibero = TipoCampoStrumento.Testo };
        var salvati = state.Master.TestTemplate.Load().ToList();
        salvati.Single(t => t.Uid == templateCr.Uid).CampiPianificazione.Add(campo);
        state.Master.TestTemplate.Save(salvati);
        templateCr = state.Master.TestTemplate.Load().Single(t => t.Uid == templateCr.Uid);
        CreaCrSalvata(state, templateCr, out var id);
        var vm = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        vm.CampiCr.Single().Valore = "primo";
        vm.SalvaCommand.Execute(null);
        Assert.Empty(vm.Errore);

        vm.CampiCr.Single().Valore = "secondo"; // dopo il primo Salva: la riga deve puntare alla CR corrente
        vm.SalvaCommand.Execute(null);
        Assert.Empty(vm.Errore);

        Assert.Equal("secondo", state.ChangeRequests.GetById(id)!.CampiCustom[campo.Uid.ToString()]);
    }

    [Fact]
    public void Implementa_DopoModificaCampoCustom_PersisteIlValore()
    {
        var (state, templateCr) = CreaStato();
        var campo = new CampoTemplate { Tipo = TipoCampoTemplate.CampoLibero, Nome = "Nota", TipoLibero = TipoCampoStrumento.Testo };
        var salvati = state.Master.TestTemplate.Load().ToList();
        salvati.Single(t => t.Uid == templateCr.Uid).CampiPianificazione.Add(campo);
        state.Master.TestTemplate.Save(salvati);
        templateCr = state.Master.TestTemplate.Load().Single(t => t.Uid == templateCr.Uid);
        CreaCrSalvata(state, templateCr, out var id);
        var vm = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        vm.CampiCr.Single().Valore = "a";
        vm.SalvaCommand.Execute(null);
        vm.CampiCr.Single().Valore = "b";

        vm.ImplementaCommand.Execute(null);
        Assert.Empty(vm.Errore);
        Assert.Equal("b", state.ChangeRequests.GetById(id)!.CampiCustom[campo.Uid.ToString()]);

        vm.CampiCr.Single().Valore = "c"; // anche dopo l'Implementa la riga resta legata alla CR corrente
        vm.SalvaCommand.Execute(null);
        Assert.Equal("c", state.ChangeRequests.GetById(id)!.CampiCustom[campo.Uid.ToString()]);
    }

    [Fact]
    public void Implementa_Riuscita_PersisteStatoESostituisceLaCrLiveConQuellaSalvata()
    {
        var (state, templateCr) = CreaStato();
        CreaCrSalvata(state, templateCr, out var id);
        var vm = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });

        vm.ImplementaCommand.Execute(null);

        Assert.Empty(vm.Errore);
        Assert.NotEqual(StatoChangeRequest.Aperta, vm.ChangeRequest.Stato);
        var salvata = state.ChangeRequests.GetById(id)!;
        Assert.NotEqual(StatoChangeRequest.Aperta, salvata.Stato); // lo Stato in memoria puo' avanzare dopo per la copertura Test
        Assert.NotNull(salvata.ImplementataIl);
        Assert.Equal(vm.ChangeRequest.Revisioni.Count, salvata.Revisioni.Count);
        Assert.Equal(salvata.Revisioni.Count, salvata.RevisioneImplementata);
        Assert.Equal(StatoChangeRequest.Implementata, vm.ChangeRequest.Stato);
        Assert.Equal(StatoChangeRequest.Implementata, salvata.Stato);
        Assert.False(vm.ModificheNonSalvate);

        vm.SalvaCommand.Execute(null);

        Assert.Empty(vm.Errore);
        Assert.Equal(StatoChangeRequest.Implementata, state.ChangeRequests.GetById(id)!.Stato);
    }

    [Fact]
    public void CreaTestValidazione_SenzaTitolo_NonPersisteNeCrNeTest()
    {
        var (state, templateCr) = CreaStato();
        var templateTest = new TestTemplate { Nome = "Test standard" };
        var tutti = state.Master.TestTemplate.Load().ToList(); tutti.Add(templateTest); state.Master.TestTemplate.Save(tutti);
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { }) { TemplateSelezionato = templateCr };
        vm.AggiungiVeicoli(new[] { "Daily 24" }); // ambito valido, titolo assente

        vm.CreaTestValidazioneCommand.Execute(null);

        Assert.NotEmpty(vm.Errore);
        Assert.True(vm.IsNew);
        Assert.Empty(state.ChangeRequests.GetAll());
        Assert.Empty(state.TestCases.GetAll(true));
    }

    [Fact]
    public void ModificaAmbito_DiCrImplementata_TornaAApertaEMostraAvviso()
    {
        var (state, templateCr) = CreaStato();
        CreaCrSalvata(state, templateCr, out var id);
        var vm = new ChangeRequestEditorViewModel(state, id, torna: () => { }, apriTest: _ => { });
        vm.ImplementaCommand.Execute(null);
        Assert.NotEqual(StatoChangeRequest.Aperta, vm.ChangeRequest.Stato);

        vm.AggiungiVeicoli(new[] { "eDaily 24" });

        Assert.Equal(StatoChangeRequest.Aperta, vm.ChangeRequest.Stato);
        Assert.Equal(LocalizationService.Instance.Traduci("CrEdStatoRiportatoAperta"), vm.Messaggio);
    }
}
