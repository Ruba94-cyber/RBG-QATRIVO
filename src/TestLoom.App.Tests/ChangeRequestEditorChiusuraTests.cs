using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Chiusura/riapertura con motivo, marcatura "senza validazione", Salva rapido senza validazione completa e
/// conflitto di revisione nell'editor CR (stesso VM della Modifica rapida).</summary>
public sealed class ChangeRequestEditorChiusuraTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CrChiusura_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    /// <summary>Master attivo; CR-0001 coperta (si valida), CR-0002 con veicolo non coperto (resta DaValidare),
    /// CR-0003 senza ambito ne responsabile (incompleta, nata prima delle regole attuali).</summary>
    private (AppState State, TestTemplate Template) CreaStato(bool master = true)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var template = new TestTemplate { Nome = "Change Request", IsChangeRequest = true };
        var tutti = state.Master.TestTemplate.Load().ToList();
        tutti.Add(template);
        state.Master.TestTemplate.Save(tutti);
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = "Daily 24", GammaNome = "MY24" },
            new() { Nome = "Alpha 24", GammaNome = "Alpha" },
        });
        state.Master.Operatori.Save(new List<Operatore>
        {
            TestOperatori.Crea("Mario Rossi", master ? RuoloOperatore.Master : RuoloOperatore.Standard),
            TestOperatori.Crea("Anna Bianchi", RuoloOperatore.Standard),
        });
        state.TestCases.Save(new TestCase
        {
            Id = "TEST-001", Componente = "BCM", DescrizioneBreve = "Verifica copertura", DataCreazione = new DateTime(2026, 1, 1),
            Esecuzioni = new() { new Esecuzione { Iterazione = 1, Data = new DateTime(2026, 2, 1), Esito = Esito.Conforme } },
        }, isNew: true);
        state.ChangeRequests.Save(Cr("CR-2026-0001", template, "Daily 24", "Daily 24"));
        state.ChangeRequests.Save(Cr("CR-2026-0002", template, "Alpha 24", "Daily 24"));
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-2026-0003", Titolo = "CR incompleta", TemplateUid = template.Uid, CreatoDa = "Mario Rossi",
            Stato = StatoChangeRequest.Aperta,
        });
        Assert.True(state.ImpostaOperatore("Mario Rossi", "1234"));
        state.RicaricaTutto();
        return (state, template);
    }

    private static ChangeRequest Cr(string id, TestTemplate template, string veicolo, string coperto) => new()
    {
        Id = id, Titolo = "CR " + id, Descrizione = "d", Responsabile = "Mario Rossi", CreatoDa = "Mario Rossi",
        TemplateUid = template.Uid, Stato = StatoChangeRequest.DaValidare,
        VeicoliImpattati = new List<string> { veicolo },
        TestValidazione = new List<CollegamentoValidazioneCr>
        {
            new() { TestId = "TEST-001", Obbligatorio = true, VeicoliCoperti = new List<string> { coperto } },
        },
    };

    private static ChangeRequestEditorViewModel Apri(AppState state, string id) =>
        new(state, id, () => { }, _ => { }, forzaBackground: false, gateCaricamentoTest: null);

    [Fact]
    public void Chiudi_DaDaValidareNonCoperta_ConMotivo_ChiudeEMarcaSenzaValidazione()
    {
        var (state, _) = CreaStato();
        var vm = Apri(state, "CR-2026-0002");
        vm.MotivoTransizione = "Richiesta annullata dal cliente";

        vm.ChiudiCommand.Execute(null);

        Assert.True(string.IsNullOrEmpty(vm.Errore), vm.Errore);
        var salvata = state.ChangeRequests.GetById("CR-2026-0002")!;
        Assert.Equal(StatoChangeRequest.Chiusa, salvata.Stato);
        Assert.True(salvata.ChiusaSenzaValidazione);
        Assert.Equal("Richiesta annullata dal cliente", salvata.MotivoChiusura);
        Assert.Equal("Mario Rossi", salvata.ChiusaDa);
        Assert.Contains(salvata.Changelog, c => c.Campo == "stato" && (c.ValoreNuovo ?? "").Contains("senza validazione"));
    }

    [Fact]
    public void Chiudi_DaValidata_ConMotivo_ChiudeSenzaMarcatura()
    {
        var (state, _) = CreaStato();
        var vm = Apri(state, "CR-2026-0001");
        vm.MotivoTransizione = "Validata e rilasciata";

        vm.ChiudiCommand.Execute(null);

        Assert.True(string.IsNullOrEmpty(vm.Errore), vm.Errore);
        var salvata = state.ChangeRequests.GetById("CR-2026-0001")!;
        Assert.Equal(StatoChangeRequest.Chiusa, salvata.Stato);
        Assert.False(salvata.ChiusaSenzaValidazione);
        Assert.Equal("Validata e rilasciata", salvata.MotivoChiusura);
    }

    [Fact]
    public void Chiudi_SenzaMotivo_NonChiudeEMostraErrore()
    {
        var (state, _) = CreaStato();
        var vm = Apri(state, "CR-2026-0002");
        vm.MotivoTransizione = "  ";

        vm.ChiudiCommand.Execute(null);

        Assert.False(string.IsNullOrEmpty(vm.Errore));
        Assert.Equal(StatoChangeRequest.DaValidare, state.ChangeRequests.GetById("CR-2026-0002")!.Stato);
    }

    [Fact]
    public void Chiudi_ComeNonMaster_NonChiudeNemmenoChiamandoIlComando()
    {
        var (state, _) = CreaStato(master: false);
        var vm = Apri(state, "CR-2026-0002");
        vm.MotivoTransizione = "motivo";

        vm.ChiudiCommand.Execute(null);

        Assert.False(vm.PuoChiudere);
        Assert.False(string.IsNullOrEmpty(vm.Errore));
        Assert.Equal(StatoChangeRequest.DaValidare, state.ChangeRequests.GetById("CR-2026-0002")!.Stato);
    }

    [Fact]
    public void Riapri_RichiedeMotivo_EAzzeraLaMarcatura()
    {
        var (state, _) = CreaStato();
        var chiusura = Apri(state, "CR-2026-0002");
        chiusura.MotivoTransizione = "motivo chiusura";
        chiusura.ChiudiCommand.Execute(null);

        var vm = Apri(state, "CR-2026-0002");
        Assert.True(vm.PuoRiaprire);
        vm.MotivoTransizione = "";
        vm.RiapriCommand.Execute(null);
        Assert.False(string.IsNullOrEmpty(vm.Errore));
        Assert.Equal(StatoChangeRequest.Chiusa, state.ChangeRequests.GetById("CR-2026-0002")!.Stato);

        vm.MotivoTransizione = "serve ancora";
        vm.RiapriCommand.Execute(null);

        var riaperta = state.ChangeRequests.GetById("CR-2026-0002")!;
        Assert.Equal(StatoChangeRequest.Aperta, riaperta.Stato);
        Assert.False(riaperta.ChiusaSenzaValidazione);
        Assert.Equal("", riaperta.MotivoChiusura);
        Assert.Contains(riaperta.Changelog, c => (c.ValoreNuovo ?? "").Contains("serve ancora"));
    }

    [Fact]
    public void SalvaRapida_CrSenzaAmbito_RiassegnaIlResponsabileSenzaValidazioneCompleta()
    {
        var (state, _) = CreaStato();
        var vm = Apri(state, "CR-2026-0003");
        vm.Responsabile = "Anna Bianchi";

        vm.SalvaRapidaCommand.Execute(null);

        Assert.True(string.IsNullOrEmpty(vm.Errore), vm.Errore);
        Assert.Equal("Anna Bianchi", state.ChangeRequests.GetById("CR-2026-0003")!.Responsabile);
    }

    [Fact]
    public void Salva_CompletoSuCrSenzaAmbito_RestaBloccatoDallaValidazione()
    {
        var (state, _) = CreaStato();
        var vm = Apri(state, "CR-2026-0003");
        vm.Responsabile = "Anna Bianchi";

        vm.SalvaCommand.Execute(null);

        Assert.False(string.IsNullOrEmpty(vm.Errore));
        Assert.Equal("", state.ChangeRequests.GetById("CR-2026-0003")!.Responsabile);
    }

    [Fact]
    public void SalvaRapida_ConConflittoDiRevisione_NonSovrascriveESegnalaIlConflitto()
    {
        var (state, _) = CreaStato();
        var vm = Apri(state, "CR-2026-0003");
        var altroPc = EntityStateFingerprint.Clone(state.ChangeRequests.GetById("CR-2026-0003")!);
        altroPc.Titolo = "Modificata da un altro PC";
        state.ChangeRequests.Save(altroPc, altroPc.RevisioneDati);
        vm.Responsabile = "Anna Bianchi";

        vm.SalvaRapidaCommand.Execute(null);

        Assert.True(vm.ConflittoRevisione);
        Assert.False(string.IsNullOrEmpty(vm.Errore));
        var suDisco = state.ChangeRequests.GetById("CR-2026-0003")!;
        Assert.Equal("Modificata da un altro PC", suDisco.Titolo);
        Assert.Equal("", suDisco.Responsabile);
    }

    [Fact]
    public void Chiudi_ConConflittoDiRevisione_NonChiudeMaiSuDatiSuperati()
    {
        var (state, _) = CreaStato();
        var vm = Apri(state, "CR-2026-0002");
        var altroPc = EntityStateFingerprint.Clone(state.ChangeRequests.GetById("CR-2026-0002")!);
        altroPc.Descrizione = "Descrizione aggiornata altrove";
        state.ChangeRequests.Save(altroPc, altroPc.RevisioneDati);
        vm.MotivoTransizione = "chiusura su dati vecchi";

        vm.ChiudiCommand.Execute(null);

        Assert.True(vm.ConflittoRevisione);
        var suDisco = state.ChangeRequests.GetById("CR-2026-0002")!;
        Assert.Equal(StatoChangeRequest.DaValidare, suDisco.Stato);
        Assert.False(suDisco.ChiusaSenzaValidazione);
        Assert.Equal("", suDisco.MotivoChiusura);
    }

    [Fact]
    public void ApertaInSolaLettura_NonPermetteModificheNeChiusuraFinoAModifica()
    {
        var (state, _) = CreaStato();
        var vm = Apri(state, "CR-2026-0002");
        vm.ImpostaSolaLetturaIniziale(null);

        Assert.False(vm.InModifica);
        Assert.False(vm.PuoModificare);
        Assert.False(vm.PuoChiudere);
        Assert.False(vm.PuoRiaprire);
        Assert.False(vm.PuoSalvare);
        Assert.True(vm.PuoEntrareInModifica);
        vm.MotivoTransizione = "motivo";
        vm.ChiudiCommand.Execute(null);
        Assert.Equal(StatoChangeRequest.DaValidare, state.ChangeRequests.GetById("CR-2026-0002")!.Stato);

        vm.EntraInModificaCommand.Execute(null);

        Assert.True(vm.InModifica);
        Assert.True(vm.PuoModificare);
        Assert.True(vm.PuoChiudere);
        Assert.False(vm.PuoEntrareInModifica);
    }

    [Fact]
    public void EntraInModifica_ConCallback_DelegaLaNavigazioneAUnEditorNuovo()
    {
        var (state, _) = CreaStato();
        var vm = Apri(state, "CR-2026-0002");
        var richieste = 0;
        vm.ImpostaSolaLetturaIniziale(() => richieste++);

        vm.EntraInModificaCommand.Execute(null);

        Assert.Equal(1, richieste);
        Assert.False(vm.PuoModificare);
    }

    [Fact]
    public void CrChiusa_MasterPuoEntrareInModificaPerRiaprire_NonMasterNo()
    {
        var (statoMaster, _) = CreaStato();
        var chiusura = Apri(statoMaster, "CR-2026-0002");
        chiusura.MotivoTransizione = "chiusa";
        chiusura.ChiudiCommand.Execute(null);
        var vmMaster = Apri(statoMaster, "CR-2026-0002");
        vmMaster.ImpostaSolaLetturaIniziale(null);
        Assert.True(vmMaster.PuoEntrareInModifica);
        vmMaster.EntraInModificaCommand.Execute(null);
        Assert.True(vmMaster.PuoRiaprire);

        var chiusa = EntityStateFingerprint.Clone(statoMaster.ChangeRequests.GetById("CR-2026-0001")!);
        chiusa.Stato = StatoChangeRequest.Chiusa;
        chiusa.ChiusaIl = DateTime.Now;
        statoMaster.ChangeRequests.Save(chiusa, chiusa.RevisioneDati);
        Assert.True(statoMaster.ImpostaOperatore("Anna Bianchi", "1234"));
        var vmStandard = Apri(statoMaster, "CR-2026-0001");
        vmStandard.ImpostaSolaLetturaIniziale(null);

        Assert.False(vmStandard.PuoEntrareInModifica);
    }
}
