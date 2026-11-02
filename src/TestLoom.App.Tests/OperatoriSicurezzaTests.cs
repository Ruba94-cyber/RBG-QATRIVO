using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata per il round grill-me 2026-08-20, punti 1/2/3/7/9/20: cifratura reversibile di
/// PIN/chiave di recupero sempre visibili al Master, ricerca generica sui 6 master data, aggiornamento live dei
/// permessi mentre una pagina è già aperta, e guard "modifiche non salvate" esteso all'uscita da Impostazioni.</summary>
public sealed class OperatoriSicurezzaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_OperatoriSicurezza_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateMasterState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(op); // migrazione 6.4.2: un ruolo non sigillato vale come Viewer.
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    // Punto 2: cifratura reversibile — mai testo in chiaro sul blob persistito, ma sempre recuperabile.
    [Fact]
    public void PinService_CifraDecifra_Roundtrip_NonMemorizzaTestoInChiaro()
    {
        var cifrato = PinService.Cifra("7391");
        Assert.NotNull(cifrato);
        Assert.DoesNotContain("7391", cifrato);
        Assert.Equal("7391", PinService.Decifra(cifrato));
    }

    [Fact]
    public void PinService_Decifra_ValoreNonValido_RitornaNullSenzaEccezioni()
    {
        Assert.Null(PinService.Decifra("non-e-base64-valido!!!"));
        Assert.Null(PinService.Decifra(null));
        Assert.Null(PinService.Decifra(""));
    }

    [Fact]
    public void PinService_Cifra_StessoTesto_ProduceBlobDiversi_PerIvCasuale()
    {
        var a = PinService.Cifra("1234");
        var b = PinService.Cifra("1234");
        Assert.NotEqual(a, b);
        Assert.Equal("1234", PinService.Decifra(a));
        Assert.Equal("1234", PinService.Decifra(b));
    }

    [Fact]
    public void Operatore_PinChiaro_RoundtripTramiteCifrato_NonEspostoInChiaroSulCampoPersistito()
    {
        var op = new Operatore { Nome = "X" };
        op.PinChiaro = "5678";
        Assert.Equal("5678", op.PinChiaro);
        Assert.NotNull(op.PinCifrato);
        Assert.DoesNotContain("5678", op.PinCifrato);
    }

    [Fact]
    public void Operatore_RecoveryCodeChiaro_RoundtripTramiteCifrato()
    {
        var op = new Operatore { Nome = "X" };
        op.RecoveryCodeChiaro = "ABCD-EFGH-JKLM-NPQR";
        Assert.Equal("ABCD-EFGH-JKLM-NPQR", op.RecoveryCodeChiaro);
        Assert.NotNull(op.RecoveryCodeCifrato);
        Assert.DoesNotContain("ABCD-EFGH-JKLM-NPQR", op.RecoveryCodeCifrato);
    }

    // Punto 2: il Master può scegliere il PIN (sia in creazione che nei reset successivi); se lascia vuoto
    // NuovoPinTemp ne genera uno casuale. Sempre visibile in chiaro dopo, non solo mostrato una tantum.
    [Fact]
    public void ImpostaPin_ConValoreDigitato_UsaQuelValore_SempreVisibileInChiaro()
    {
        var state = CreateMasterState();
        var vm = new OperatoriViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var nuovo = vm.Items.First(o => o.Nome == "Master Test");
        nuovo.NuovoPinTemp = "9012";

        vm.ImpostaPinCommand.Execute(nuovo);

        Assert.Equal("9012", nuovo.PinChiaro);
        Assert.True(PinService.Verifica("9012", nuovo.PinHash));
        Assert.Equal("", nuovo.NuovoPinTemp);
    }

    [Fact]
    public void ImpostaPin_SenzaValoreDigitato_GeneraPinCasualeA4Cifre()
    {
        var state = CreateMasterState();
        var vm = new OperatoriViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var nuovo = vm.Items.First(o => o.Nome == "Master Test");
        nuovo.NuovoPinTemp = "";

        vm.ImpostaPinCommand.Execute(nuovo);

        Assert.NotNull(nuovo.PinChiaro);
        Assert.Equal(4, nuovo.PinChiaro!.Length);
        Assert.True(nuovo.PinChiaro.All(char.IsDigit));
        Assert.True(PinService.Verifica(nuovo.PinChiaro, nuovo.PinHash));
    }

    [Fact]
    public void ImpostaPin_PrimaVolta_GeneraAncheLaChiaveDiRecupero()
    {
        var state = CreateMasterState();
        var vm = new OperatoriViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var nuovo = vm.Items.First(o => o.Nome == "Master Test");
        Assert.Null(nuovo.RecoveryCodeChiaro);

        vm.ImpostaPinCommand.Execute(nuovo);

        Assert.NotNull(nuovo.RecoveryCodeChiaro);
        Assert.True(PinService.Verifica(nuovo.RecoveryCodeChiaro!, nuovo.RecoveryCodeHash));
    }

    // Cuore del punto 2: la chiave NON si rigenera più ad ogni reset del PIN, resta fissa finché non la si
    // rigenera esplicitamente con RigeneraChiaveRecupero.
    [Fact]
    public void ImpostaPin_ResetSuccessivi_NonCambianoLaChiaveDiRecuperoGiaEsistente()
    {
        var state = CreateMasterState();
        var vm = new OperatoriViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var nuovo = vm.Items.First(o => o.Nome == "Master Test");

        vm.ImpostaPinCommand.Execute(nuovo);
        var chiavePrimaVolta = nuovo.RecoveryCodeChiaro;

        nuovo.NuovoPinTemp = "4321";
        vm.ImpostaPinCommand.Execute(nuovo);

        Assert.Equal("4321", nuovo.PinChiaro);
        Assert.Equal(chiavePrimaVolta, nuovo.RecoveryCodeChiaro);
    }

    [Fact]
    public void RigeneraChiaveRecupero_CambiaLaChiaveEsistente()
    {
        var state = CreateMasterState();
        var vm = new OperatoriViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var nuovo = vm.Items.First(o => o.Nome == "Master Test");
        vm.ImpostaPinCommand.Execute(nuovo);
        var chiavePrima = nuovo.RecoveryCodeChiaro;

        vm.RigeneraChiaveRecuperoCommand.Execute(nuovo);

        Assert.NotNull(nuovo.RecoveryCodeChiaro);
        Assert.NotEqual(chiavePrima, nuovo.RecoveryCodeChiaro);
        Assert.True(PinService.Verifica(nuovo.RecoveryCodeChiaro!, nuovo.RecoveryCodeHash));
    }

    // Punto 3/9/20: ricerca generica sui master data, inclusa la chiave di recupero, con reset.
    [Fact]
    public void Operatori_FiltroTesto_TrovaAncheDallaChiaveDiRecupero()
    {
        var state = CreateMasterState();
        var vm = new OperatoriViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var nuovo = vm.Items.First(o => o.Nome == "Master Test");
        vm.ImpostaPinCommand.Execute(nuovo);
        var frammentoChiave = nuovo.RecoveryCodeChiaro!.Substring(0, 4);

        vm.FiltroTesto = frammentoChiave;

        Assert.Contains(nuovo, vm.ItemsAttivi);
        Assert.True(vm.HaFiltriAttivi);

        vm.FiltroTesto = "STRINGA-CHE-NON-CORRISPONDE-A-NULLA";
        Assert.DoesNotContain(nuovo, vm.ItemsAttivi);

        vm.ReimpostaFiltriCommand.Execute(null);
        Assert.Equal("", vm.FiltroTesto);
        Assert.False(vm.HaFiltriAttivi);
        Assert.Contains(nuovo, vm.ItemsAttivi);
    }

    [Fact]
    public void Componente_FiltroTesto_FiltraPerNome_GenericoSuOgniMasterData()
    {
        var state = CreateMasterState();
        var componente = state.Master.Componente.Load().ToList();
        componente.Add(new Componente { Nome = "BCM Ricerca Unica" });
        state.Master.Componente.Save(componente);
        var vm = new EcuViewModel(state);

        vm.FiltroTesto = "Ricerca Unica";

        Assert.Contains(vm.ItemsAttivi, e => e.Nome == "BCM Ricerca Unica");
        Assert.All(vm.ItemsAttivi, e => Assert.Contains("Ricerca Unica", e.Nome));
    }

    // Punto 1: aggiornamento live dei permessi mentre la pagina è già aperta, senza logout forzato.
    [Fact]
    public void PuoModificare_SiAggiornaLive_QuandoIlRuoloCambiaConPaginaGiaAperta()
    {
        var state = CreateMasterState();
        var vm = new OperatoriViewModel(state);
        Assert.True(vm.PuoModificare);

        var notificato = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.PuoModificare)) notificato = true; };

        var ops = state.Master.Operatori.Load().ToList();
        var corrente = ops.First(o => o.Nome == "Master Test");
        corrente.Ruolo = RuoloOperatore.Standard;
        state.Master.Operatori.Save(ops);

        state.RicaricaTutto();

        Assert.True(notificato);
        Assert.False(vm.PuoModificare);
    }

    [Fact]
    public void Dispose_FermaLAggiornamentoLive_NessunaNotificaDopo()
    {
        var state = CreateMasterState();
        var vm = new OperatoriViewModel(state);
        vm.Dispose();

        var notificato = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.PuoModificare)) notificato = true; };

        var ops = state.Master.Operatori.Load().ToList();
        ops.First(o => o.Nome == "Master Test").Ruolo = RuoloOperatore.Standard;
        state.Master.Operatori.Save(ops);
        state.RicaricaTutto();

        Assert.False(notificato);
    }

    // Punto 7: uscire dall'intero hub Impostazioni (non solo cambiare sotto-sezione) con modifiche non salvate
    // deve essere intercettato da MainViewModel.HaModificheNonSalvate, non solo da ImpostazioniViewModel.
    [Fact]
    public void MainViewModel_HaModificheNonSalvate_RilevaEditorMasterDataApertoDentroImpostazioni()
    {
        var state = CreateMasterState();
        var main = new MainViewModel(state);
        var impostazioni = new ImpostazioniViewModel(state, () => { }, main.NavItems);
        var operatori = new OperatoriViewModel(state);
        impostazioni.PaginaCorrente = operatori;

        operatori.IniziaModificaCommand.AttendiModifica();
        operatori.Items.First(o => o.Nome == "Master Test").Iniziali = "ZZ";
        main.CurrentPage = impostazioni;

        Assert.True(main.HaModificheNonSalvate());
    }
}
