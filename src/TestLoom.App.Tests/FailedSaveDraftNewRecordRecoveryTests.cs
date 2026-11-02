using System.Security.Cryptography;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Recupero delle bozze di record MAI salvati (nuova CR, nuova segnalazione di rete) e delle bozze delle segnalazioni
/// da classificare. Il recupero deve solo preparare l'editor: nessuna scrittura nel repository condiviso prima del
/// Salva esplicito dell'utente, che passa dalle normali verifiche.
/// </summary>
[Collection("Localization")]
public sealed class FailedSaveDraftNewRecordRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NewRecordRecovery_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var operatori = state.Master.Operatori.Load();
        var operatore = new Operatore
        {
            Nome = "Operatore Rete",
            Ruolo = RuoloOperatore.Standard,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        };
        RuoloSigilloService.Sigilla(operatore);
        operatori.Add(operatore);
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Operatore Rete", "1234"));
        Assert.True(state.PuoScrivere);
        return state;
    }

    private static TestTemplate AggiungiTemplateCr(AppState state)
    {
        var template = new TestTemplate { Nome = "Change Request", IsChangeRequest = true };
        var tutti = state.Master.TestTemplate.Load().ToList();
        tutti.Add(template);
        state.Master.TestTemplate.Save(tutti);
        return template;
    }

    private AppState CreaStatoNc()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", ModificatoDa = "Seed" } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily MY24", GammaNome = "Daily", ModificatoDa = "Seed" } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi accensione", ModificatoDa = "Seed" } });
        return state;
    }

    private static NonConformita BozzaNc(Guid uid, string id) => new()
    {
        Id = id,
        Uid = uid,
        DaSegnalazioneRete = true,
        Owner = "Operatore Rete",
        Severita = SeveritaAnomalia.High,
        Priorita = PrioritaOperativa.Critical,
        StatoWorkflow = StatoAnomalia.Confermata,
        Descrizione = "Descrizione recuperata",
        DescrizioneBreveSegnalata = "Breve recuperata",
        EcuSegnalate = new EcuSelection(new[] { "BCM" }),
        VeicoliSegnalati = new List<string> { "Daily MY24" },
        FunzionalitaSegnalate = new List<string> { "Diagnosi accensione" },
        NumeriPraticaRete = new List<string> { "PRATICA-2026-4471" },
        VinCliente = new List<string> { "ZCFC1234500098765" },
        Licenze = new List<string> { "LIC-2026-001" }
    };

    /// <summary>Fotografia del contenuto della cartella condivisa: qualunque scrittura cambia il risultato.</summary>
    private static string FotografiaCondivisa(string root) =>
        string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(root, p) + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));

    private static SegnalazioneDaClassificare CreaSegnalazione(AppState state, string testId)
    {
        var tc = new TestCase
        {
            Id = testId,
            Componente = "BCM",
            Esecuzioni = new List<Esecuzione>
            {
                new() { Iterazione = 1, Condizioni = new List<Condizione> { new() { Esito = Esito.NonConforme } } }
            }
        };
        state.TestCases.Save(tc, isNew: true);
        var seg = new SegnalazioneDaClassificare
        {
            TestId = tc.Id,
            CondizioneUid = tc.Esecuzioni[0].Condizioni[0].Uid,
            Stato = StatoSegnalazione.DaClassificare
        };
        state.Segnalazioni.Save(seg);
        return state.Segnalazioni.Get(seg.Uid) ?? throw new InvalidOperationException("Segnalazione non salvata.");
    }

    // ---------------------------------------------------------------- Change Request nuova

    [Fact]
    public void CrNuova_BozzaSenzaBase_SiCaricaNelNuovoEditorSenzaScrivereCondiviso()
    {
        var state = CreaStato();
        var template = AggiungiTemplateCr(state);
        var uid = Guid.NewGuid();
        FailedSaveDraftService.Save(_dir, "change-request", uid, 0, "QA", new ChangeRequest
        {
            Uid = uid,
            TemplateUid = template.Uid,
            Titolo = "CR da bozza",
            Descrizione = "Descrizione recuperata",
            Responsabile = "Mario Rossi",
            VeicoliImpattati = new List<string> { "Daily 24" }
        }, "Save fallito");

        var vm = new ChangeRequestEditorViewModel(state, null, () => { }, _ => { });
        Assert.True(vm.PuoRecuperareBozzaNuova);
        Assert.True(vm.PuoRecuperareBozzaLocale);
        Assert.False(vm.PuoRecuperareBozza);
        var prima = FotografiaCondivisa(_dir);

        var row = Assert.Single(FailedSaveDraftInspector.ListNewRecordPage(_dir, "change-request", vm.UidCrPresenti().Contains, 0, 10));
        vm.ApplicaBozzaNuovaRecuperata(vm.CaricaBozzaNuovaRecuperabile(row));

        Assert.Equal("CR da bozza", vm.ChangeRequest.Titolo);
        Assert.Equal("Descrizione recuperata", vm.ChangeRequest.Descrizione);
        Assert.Equal("Mario Rossi", vm.ChangeRequest.Responsabile);
        Assert.Contains("Daily 24", vm.ChangeRequest.VeicoliImpattati);
        Assert.Equal(template.Uid, vm.TemplateSelezionato?.Uid);
        Assert.Equal(uid, vm.ChangeRequest.Uid);
        Assert.True(vm.IsNew);
        Assert.True(vm.ModificheNonSalvate);
        Assert.Equal(prima, FotografiaCondivisa(_dir)); // nessuna scrittura condivisa prima del Salva
    }

    [Fact]
    public void CrNuova_SalvaDopoIlRecupero_CreaIlRecordConLUidDellaBozzaEElaBozzaNonEPiuOfferta()
    {
        var state = CreaStato();
        var template = AggiungiTemplateCr(state);
        var uid = Guid.NewGuid();
        FailedSaveDraftService.Save(_dir, "change-request", uid, 0, "QA", new ChangeRequest
        {
            Uid = uid,
            TemplateUid = template.Uid,
            Titolo = "CR salvata dopo recupero",
            VeicoliImpattati = new List<string> { "Daily 24" }
        }, "Save fallito");
        var vm = new ChangeRequestEditorViewModel(state, null, () => { }, _ => { });
        var row = Assert.Single(FailedSaveDraftInspector.ListNewRecordPage(_dir, "change-request", vm.UidCrPresenti().Contains, 0, 10));
        vm.ApplicaBozzaNuovaRecuperata(vm.CaricaBozzaNuovaRecuperabile(row));
        var prima = FotografiaCondivisa(_dir);

        vm.SalvaCommand.Execute(null);

        Assert.Equal("", vm.Errore);
        Assert.False(vm.IsNew);
        Assert.NotEqual(prima, FotografiaCondivisa(_dir));
        var salvata = Assert.Single(state.ChangeRequests.GetAll(includeArchiviate: true).Where(c => c.Uid == uid));
        Assert.StartsWith("CR-", salvata.Id);
        Assert.Equal("CR salvata dopo recupero", salvata.Titolo);
        // Ora l'UID esiste nel repository: la bozza non è più un "nuovo record".
        Assert.Empty(FailedSaveDraftInspector.ListNewRecordPage(_dir, "change-request", vm.UidCrPresenti().Contains, 0, 10));
    }

    [Fact]
    public void CrNuova_IdDellaBozzaGiaUsatoDaUnAltraCr_NonRiusaLIdEIlRecordEsistenteResta()
    {
        var state = CreaStato();
        var template = AggiungiTemplateCr(state);
        var altroUid = Guid.NewGuid();
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-2026-0007",
            Uid = altroUid,
            TemplateUid = template.Uid,
            Titolo = "Altra CR",
            VeicoliImpattati = new List<string> { "Daily 24" }
        });
        var uid = Guid.NewGuid();
        FailedSaveDraftService.Save(_dir, "change-request", uid, 0, "QA", new ChangeRequest
        {
            Id = "CR-2026-0007",
            Uid = uid,
            TemplateUid = template.Uid,
            Titolo = "CR bozza con ID occupato",
            VeicoliImpattati = new List<string> { "Daily 24" }
        }, "Save fallito");
        var vm = new ChangeRequestEditorViewModel(state, null, () => { }, _ => { });
        var row = Assert.Single(FailedSaveDraftInspector.ListNewRecordPage(_dir, "change-request", vm.UidCrPresenti().Contains, 0, 10));

        vm.ApplicaBozzaNuovaRecuperata(vm.CaricaBozzaNuovaRecuperabile(row));
        Assert.Equal("", vm.ChangeRequest.Id); // nessun ID della bozza riusato: il Save assegna il prossimo
        vm.SalvaCommand.Execute(null);

        Assert.False(vm.IsNew);
        var nuova = Assert.Single(state.ChangeRequests.GetAll(includeArchiviate: true).Where(c => c.Uid == uid));
        Assert.NotEqual("CR-2026-0007", nuova.Id);
        Assert.Equal(altroUid, state.ChangeRequests.GetById("CR-2026-0007")!.Uid); // l'altra CR è intatta
    }

    [Fact]
    public void CrNuova_UidGiaNelRepository_NonEOfferta_EAlCaricamentoIlNuovoRecordNeRiceveUnAltro()
    {
        var state = CreaStato();
        var template = AggiungiTemplateCr(state);
        var uid = Guid.NewGuid();
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-2026-0001",
            Uid = uid,
            TemplateUid = template.Uid,
            Titolo = "Gia salvata",
            VeicoliImpattati = new List<string> { "Daily 24" }
        });
        FailedSaveDraftService.Save(_dir, "change-request", uid, 0, "QA",
            new ChangeRequest { Uid = uid, Titolo = "Bozza con lo stesso UID" }, "Save fallito");
        var vm = new ChangeRequestEditorViewModel(state, null, () => { }, _ => { });

        Assert.Empty(FailedSaveDraftInspector.ListNewRecordPage(_dir, "change-request", vm.UidCrPresenti().Contains, 0, 10));

        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_dir, 0, 10, domain: "change-request"));
        var candidato = vm.CaricaBozzaNuovaRecuperabile(row);
        Assert.NotEqual(uid, candidato.Uid); // l'UID già usato non viene riutilizzato
        Assert.Equal("", candidato.Id);
    }

    [Fact]
    public void CrNuova_UidDiUnaCrEliminata_NonEOffertaPerNonResuscitarla()
    {
        var state = CreaStato();
        var template = AggiungiTemplateCr(state);
        var uid = Guid.NewGuid();
        var cr = new ChangeRequest
        {
            Id = "CR-2026-0009", Uid = uid, TemplateUid = template.Uid, Titolo = "Da eliminare",
            VeicoliImpattati = new List<string> { "Daily 24" }
        };
        state.ChangeRequests.Save(cr);
        state.ChangeRequests.EliminaDefinitivamente(state.ChangeRequests.GetById("CR-2026-0009")!, "QA");
        FailedSaveDraftService.Save(_dir, "change-request", uid, 0, "QA",
            new ChangeRequest { Uid = uid, Titolo = "Bozza della CR poi eliminata" }, "Save fallito");
        var vm = new ChangeRequestEditorViewModel(state, null, () => { }, _ => { });

        // UID non più nel repository, ma presente nel recovery-bin: va ripristinata da lì, non ricreata.
        Assert.DoesNotContain(uid, vm.UidCrPresenti());
        Assert.Empty(FailedSaveDraftInspector.ListNewRecordPage(_dir, "change-request", vm.UidCrPresenti().Contains, 0, 10));
    }

    [Fact]
    public void BozzaCrConBase_NonEOfferta_ComeNuovoRecord_MaRestaNelElencoOrdinario()
    {
        var state = CreaStato();
        var uid = Guid.NewGuid();
        var baseCr = new ChangeRequest { Id = "CR-2026-0002", Uid = uid, RevisioneDati = 1, Titolo = "Base" };
        FailedSaveDraftService.Save(_dir, "change-request", uid, 1, "QA",
            new ChangeRequest { Id = "CR-2026-0002", Uid = uid, RevisioneDati = 1, Titolo = "Modificata" },
            "Save fallito", baseSnapshot: baseCr);
        var vm = new ChangeRequestEditorViewModel(state, null, () => { }, _ => { });

        Assert.Empty(FailedSaveDraftInspector.ListNewRecordPage(_dir, "change-request", vm.UidCrPresenti().Contains, 0, 10));
        Assert.Single(FailedSaveDraftInspector.ListPage(_dir, 0, 10, domain: "change-request"));
    }

    // ---------------------------------------------------------------- Segnalazione di rete nuova

    [Fact]
    public void NcNuova_BozzaSenzaBase_SiCaricaNelNuovoEditorSenzaScrivereCondiviso_ESalvaConIlProssimoId()
    {
        var state = CreaStatoNc();
        var uid = Guid.NewGuid();
        FailedSaveDraftService.Save(_dir, "nonconformita", uid, 0, "Operatore Rete", BozzaNc(uid, "NC-0001"), "Save fallito");
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        Assert.True(vm.PuoRecuperareBozzaNuova);
        Assert.True(vm.PuoRecuperareBozzaLocale);
        var prima = FotografiaCondivisa(_dir);

        var row = Assert.Single(FailedSaveDraftInspector.ListNewRecordPage(_dir, "nonconformita", vm.UidNcPresenti().Contains, 0, 10));
        vm.ApplicaBozzaNuovaRecuperata(vm.CaricaBozzaNuovaRecuperabile(row));

        Assert.Equal("Descrizione recuperata", vm.Nc.Descrizione);
        Assert.Equal("Breve recuperata", vm.Nc.DescrizioneBreveSegnalata);
        Assert.Equal(SeveritaAnomalia.High, vm.Nc.Severita);
        Assert.Equal(StatoAnomalia.Confermata, vm.Nc.StatoWorkflow);
        Assert.Contains("Diagnosi accensione", vm.Nc.FunzionalitaSegnalate);
        Assert.Contains("PRATICA-2026-4471", vm.Nc.NumeriPraticaRete);
        Assert.Equal(uid, vm.Nc.Uid);
        Assert.True(vm.IsNew);
        Assert.Equal(prima, FotografiaCondivisa(_dir)); // nessuna scrittura condivisa prima del Salva

        vm.SalvaCommand.Execute(null);

        Assert.False(vm.IsNew);
        var salvata = Assert.Single(state.NonConformita.PeekAllReadOnly().Where(n => n.Uid == uid));
        Assert.Equal("NC-0001", salvata.Id);
        Assert.Empty(FailedSaveDraftInspector.ListNewRecordPage(_dir, "nonconformita", vm.UidNcPresenti().Contains, 0, 10));
    }

    [Fact]
    public void NcNuova_IdDellaBozzaGiaUsatoDaUnAltraNc_NonRiusaLIdEIlRecordEsistenteResta()
    {
        var state = CreaStatoNc();
        var altroUid = Guid.NewGuid();
        var altra = BozzaNc(altroUid, "NC-0001");
        altra.FunzionalitaSegnalate = new List<string> { "Altra funzionalità" };
        altra.NumeriPraticaRete = new List<string> { "PRATICA-ALTRA" };
        altra.VinCliente = new List<string> { "Matricola-ALTRO" };
        altra.Licenze = new List<string> { "LIC-ALTRA" };
        state.NonConformita.Save(altra);
        var uid = Guid.NewGuid();
        FailedSaveDraftService.Save(_dir, "nonconformita", uid, 0, "Operatore Rete", BozzaNc(uid, "NC-0001"), "Save fallito");
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        var row = Assert.Single(FailedSaveDraftInspector.ListNewRecordPage(_dir, "nonconformita", vm.UidNcPresenti().Contains, 0, 10));

        vm.ApplicaBozzaNuovaRecuperata(vm.CaricaBozzaNuovaRecuperabile(row));
        Assert.Equal("", vm.Nc.Id);
        vm.SalvaCommand.Execute(null);

        Assert.False(vm.IsNew);
        Assert.Equal(altroUid, state.NonConformita.GetById("NC-0001")!.Uid); // la NC esistente è intatta
        var nuova = Assert.Single(state.NonConformita.PeekAllReadOnly().Where(n => n.Uid == uid));
        Assert.NotEqual("NC-0001", nuova.Id);
    }

    [Fact]
    public void NcNuova_BozzaDiSplitMultiFunzionalita_NonEOfferta_PerNonDuplicareLeSorelle()
    {
        var state = CreaStatoNc();
        var uid = Guid.NewGuid();
        var bozza = BozzaNc(uid, "");
        bozza.FunzionalitaSegnalate = new List<string> { "Diagnosi accensione", "Altra funzionalità" };
        FailedSaveDraftService.Save(_dir, "nonconformita", uid, 0, "Operatore Rete", bozza, "Save fallito");
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });

        Assert.Empty(FailedSaveDraftInspector.ListNewRecordPage(_dir, "nonconformita", vm.UidNcPresenti().Contains, 0, 10));
        Assert.Single(FailedSaveDraftInspector.ListPage(_dir, 0, 10, domain: "nonconformita"));
    }

    [Fact]
    public void NcNuova_BozzaNonDiSegnalazioneDiRete_NonEOfferta()
    {
        var state = CreaStatoNc();
        var uid = Guid.NewGuid();
        var bozza = BozzaNc(uid, "NC-0002");
        bozza.DaSegnalazioneRete = false; // NC creata dalla classificazione, non dall'editor di rete
        FailedSaveDraftService.Save(_dir, "nonconformita", uid, 0, "Operatore Rete", bozza, "Save fallito");
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });

        Assert.Empty(FailedSaveDraftInspector.ListNewRecordPage(_dir, "nonconformita", vm.UidNcPresenti().Contains, 0, 10));
    }

    // ---------------------------------------------------------------- Segnalazioni da classificare

    [Fact]
    public void SegnalazioneEsclusioneFallita_SalvaBozzaSegnalazioneConBaseFotografica()
    {
        var state = CreaStatoNc();
        var seg = CreaSegnalazione(state, "TC-SEG-1");
        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = vm.Segnalazioni.First(s => s.Segnalazione.TestId == "TC-SEG-1");
        riga.MotivazioneEsclusione = "Duplicata";
        // Un'altra postazione modifica la segnalazione dopo l'apertura: il salvataggio locale deve fallire.
        var altraVersione = state.Segnalazioni.Get(seg.Uid)!;
        altraVersione.RevisioneDati += 1;
        JsonIo.WriteAtomic(state.Paths.SegnalazioneFile(seg.Uid), altraVersione, overwrite: true);

        vm.EscludiSegnalazioneSicuroCommand.Execute(riga);

        Assert.False(string.IsNullOrEmpty(vm.MessaggioErrore));
        var bozza = Assert.Single(FailedSaveDraftInspector.ListPage(_dir, 0, 10, domain: "segnalazione"));
        var envelope = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(File.ReadAllBytes(bozza.FilePath), out var error);
        Assert.Null(error);
        Assert.NotNull(envelope);
        Assert.Equal(seg.Uid, envelope!.EntityUid);
        Assert.Equal("segnalazione", envelope.Domain);
        Assert.False(string.IsNullOrEmpty(envelope.BasePayloadJson));  // fotografia iniziale presente
        Assert.Contains("Duplicata", envelope.PayloadJson);
    }

    [Fact]
    public void SegnalazioneClassificazioneFallita_SalvaBozzaSegnalazioneOltreAllaNc()
    {
        var state = CreaStatoNc();
        var seg = CreaSegnalazione(state, "TC-SEG-2");
        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = vm.Segnalazioni.First(s => s.Segnalazione.TestId == "TC-SEG-2");
        riga.Severita = SeveritaAnomalia.Low;
        var altraVersione = state.Segnalazioni.Get(seg.Uid)!;
        altraVersione.RevisioneDati += 1;
        JsonIo.WriteAtomic(state.Paths.SegnalazioneFile(seg.Uid), altraVersione, overwrite: true);

        vm.ClassificaSegnalazioneSicuroCommand.Execute(riga);

        Assert.False(string.IsNullOrEmpty(vm.MessaggioErrore));
        var bozza = Assert.Single(FailedSaveDraftInspector.ListPage(_dir, 0, 10, domain: "segnalazione"));
        var envelope = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(File.ReadAllBytes(bozza.FilePath), out _);
        Assert.NotNull(envelope);
        Assert.Equal(seg.Uid, envelope!.EntityUid);
        Assert.False(string.IsNullOrEmpty(envelope.BasePayloadJson));
        // La bozza NC della classificazione resta come prima; la transazione ha annullato la NC creata.
        Assert.Single(FailedSaveDraftInspector.ListPage(_dir, 0, 10, domain: "nonconformita"));
        Assert.Empty(state.NonConformita.PeekAllReadOnly());
    }

    // ---------------------------------------------------------------- Salvataggio fallito di un record nuovo

    [Fact]
    public void CrNuova_SalvataggioFallito_CreaUnaBozzaSenzaBaseEIlSalvataggioRestaNuovo()
    {
        var state = CreaStato();
        var template = AggiungiTemplateCr(state);
        var uid = Guid.NewGuid();
        FailedSaveDraftService.Save(_dir, "change-request", uid, 0, "QA", new ChangeRequest
        {
            Uid = uid,
            TemplateUid = template.Uid,
            Titolo = "CR da salvare",
            VeicoliImpattati = new List<string> { "Daily 24" }
        }, "Save precedente");
        var vm = new ChangeRequestEditorViewModel(state, null, () => { }, _ => { });
        var row = Assert.Single(FailedSaveDraftInspector.ListNewRecordPage(_dir, "change-request", vm.UidCrPresenti().Contains, 0, 10));
        vm.ApplicaBozzaNuovaRecuperata(vm.CaricaBozzaNuovaRecuperabile(row));

        // Modalità visualizzatore: il Save della CR nuova fallisce davvero, attraverso la guardia di scrittura.
        WorkspaceReadOnlyService.SetEnabled(_dir, true);
        try { vm.SalvaCommand.Execute(null); }
        finally { WorkspaceReadOnlyService.SetEnabled(_dir, false); }

        Assert.True(vm.IsNew);
        Assert.Contains("Bozza locale conservata", vm.Errore);
        var bozze = FailedSaveDraftInspector.ListPage(_dir, 0, 10, domain: "change-request");
        Assert.Equal(2, bozze.Count); // la bozza precedente e quella del salvataggio fallito
        Assert.All(bozze, b => Assert.Equal(uid, b.EntityUid));
        Assert.Equal(2, FailedSaveDraftInspector.ListNewRecordPage(_dir, "change-request", vm.UidCrPresenti().Contains, 0, 10).Count);
    }

    [Fact]
    public void NcNuova_SalvataggioFallito_CreaUnaBozzaConIdTentatoEIlEditorTornaNuovo()
    {
        var state = CreaStatoNc();
        var uid = Guid.NewGuid();
        FailedSaveDraftService.Save(_dir, "nonconformita", uid, 0, "Operatore Rete", BozzaNc(uid, ""), "Save precedente");
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        var row = Assert.Single(FailedSaveDraftInspector.ListNewRecordPage(_dir, "nonconformita", vm.UidNcPresenti().Contains, 0, 10));
        vm.ApplicaBozzaNuovaRecuperata(vm.CaricaBozzaNuovaRecuperabile(row));

        // In modalità visualizzatore Salva() esce subito (PuoModificare falso): si esercita direttamente il percorso
        // di salvataggio privato, dove la guardia di scrittura del repository fa fallire il Save.
        var salvaSingola = typeof(SegnalazioneReteEditorViewModel).GetMethod("SalvaSingola",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        WorkspaceReadOnlyService.SetEnabled(_dir, true);
        try { salvaSingola.Invoke(vm, null); }
        finally { WorkspaceReadOnlyService.SetEnabled(_dir, false); }

        Assert.True(vm.IsNew);                 // l'ID tentato non resta sulla NC aperta
        Assert.Equal("", vm.Nc.Id);
        Assert.False(string.IsNullOrEmpty(vm.MessaggioErrore));
        var bozze = FailedSaveDraftInspector.ListPage(_dir, 0, 10, domain: "nonconformita");
        Assert.Equal(2, bozze.Count);
        var emergenza = Assert.Single(bozze
            .Select(b => JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(File.ReadAllBytes(b.FilePath), out _)!)
            .Where(e => IdDelPayload(e.PayloadJson) == "NC-0001"));
        Assert.Equal(uid, emergenza.EntityUid);
        Assert.True(string.IsNullOrEmpty(emergenza.BasePayloadJson)); // nuovo record: nessuna fotografia iniziale
    }

    private static string? IdDelPayload(string payloadJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
        return doc.RootElement.TryGetProperty("Id", out var id) ? id.GetString() : null;
    }
}
