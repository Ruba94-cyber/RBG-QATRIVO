using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("CondivisioneRete")]
public sealed class FirstRunSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_pin_demo_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AccountDemo_NonEntraFincheNonCambiaIlPinPubblico()
    {
        var state = PreparaStatoDemo();
        var completamenti = 0;
        var vm = new FirstRunViewModel(state, () => completamenti++);
        vm.OperatoreSelezionato = "Demo Master";
        vm.PinInserito = "1234";

        await vm.ConfermaCommand.ExecuteAsync(null);

        Assert.True(vm.RichiedeCambioPin);
        Assert.Equal(0, completamenti);
        vm.NuovoPinObbligatorio = "1234";
        vm.ConfermaNuovoPinObbligatorio = "1234";
        vm.ConfermaCambioPinObbligatorioCommand.Execute(null);
        Assert.Equal(0, completamenti);
        Assert.True(vm.MostraErrore);

        vm.NuovoPinObbligatorio = "9087";
        vm.ConfermaNuovoPinObbligatorio = "9087";
        vm.ConfermaCambioPinObbligatorioCommand.Execute(null);

        Assert.Equal(0, completamenti);
        Assert.False(vm.RichiedeCambioPin);
        Assert.True(vm.AttendeConfermaCodiceRecupero);
        Assert.True(vm.MostraCodiceRecupero);
        var salvato = state.Master.Operatori.Load().Single();
        Assert.False(salvato.RichiedeCambioPin);
        Assert.True(PinService.Verifica("9087", salvato.PinHash));
        Assert.False(PinService.Verifica("1234", salvato.PinHash));

        vm.ContinuaDopoCodiceRecuperoCommand.Execute(null);
        Assert.Equal(1, completamenti);
        Assert.False(vm.MostraCodiceRecupero);
    }

    [Fact]
    public async Task Conferma_SegnalaAccessoInCorsoEChiudeIlPopupAFineLavoro()
    {
        // Regressione (piano 6.4.0, punto 2): "Accedi" deve mostrare lo stesso popup di avanzamento
        // dell'apertura con cartella già definita, non bloccare la UI in silenzio. AccessoInCorso è la
        // proprietà che il code-behind della view osserva per mostrare/nascondere AvvioWindow: qui verifichiamo
        // che diventi vera durante il lavoro e torni falsa a fine comando, senza toccare Avalonia.
        var state = PreparaStatoDemo();
        var vm = new FirstRunViewModel(state, () => { });
        vm.OperatoreSelezionato = "Demo Master";
        vm.PinInserito = "1234";
        var statiAccessoInCorso = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirstRunViewModel.AccessoInCorso)) statiAccessoInCorso.Add(vm.AccessoInCorso);
        };

        await vm.ConfermaCommand.ExecuteAsync(null);

        Assert.Contains(true, statiAccessoInCorso);
        Assert.False(vm.AccessoInCorso);
        Assert.False(statiAccessoInCorso[^1]);
        Assert.NotNull(vm.AvvioProgress);
    }

    [Fact]
    public async Task CambioPinObbligatorio_PopolaPinChiaroERecoveryCodeChiaro()
    {
        // Regressione (feedback utente 2026-08-20): il Master deve vedere sempre il PIN e la chiave di
        // recupero in chiaro (punto 2) — questo valeva già per OperatoriViewModel.ImpostaPin, ma non per il
        // cambio PIN obbligatorio al primo accesso: l'operatore restava senza PinChiaro/RecoveryCodeChiaro
        // finché qualcuno non lo reimpostava manualmente da Operatori.
        var state = PreparaStatoDemo();
        var vm = new FirstRunViewModel(state, () => { });
        vm.OperatoreSelezionato = "Demo Master";
        vm.PinInserito = "1234";
        await vm.ConfermaCommand.ExecuteAsync(null);

        vm.NuovoPinObbligatorio = "9087";
        vm.ConfermaNuovoPinObbligatorio = "9087";
        vm.ConfermaCambioPinObbligatorioCommand.Execute(null);

        var salvato = state.Master.Operatori.Load().Single();
        Assert.Equal("9087", salvato.PinChiaro);
        Assert.False(string.IsNullOrEmpty(salvato.RecoveryCodeChiaro));
    }

    [Fact]
    public void AggiungiOperatore_PopolaPinChiaroERecoveryCodeChiaro()
    {
        // Regressione (feedback utente 2026-08-20, punto 2): l'operatore creato al bootstrap di una cartella
        // vuota funzionava per il login (PinHash) ma il Master non vedeva mai il PIN in chiaro nella tabella
        // Operatori, perché la creazione qui non impostava PinChiaro/RecoveryCodeChiaro.
        Directory.CreateDirectory(_root);
        var state = TestStateFactory.Create();
        var vm = new FirstRunViewModel(state, () => { });
        vm.Cartella = _root;
        vm.NuovoOperatoreNome = "Primo Master";
        vm.NuovoOperatorePin = "5566";

        vm.AggiungiOperatoreCommand.Execute(null);

        var salvato = new MasterDataRepository<Operatore>(new DataPaths(_root).OperatoriFile).Load().Single();
        Assert.Equal("5566", salvato.PinChiaro);
        Assert.False(string.IsNullOrEmpty(salvato.RecoveryCodeChiaro));
        Assert.Equal(RuoloOperatore.Master, salvato.Ruolo);
    }

    [Fact]
    public void ConfermaRecuperoPin_PopolaPinChiaroERecoveryCodeChiaro()
    {
        // Stessa regressione del test sopra, per il percorso di auto-recupero PIN (Dimenticato il PIN).
        var state = PreparaStatoDemo();
        var vm = new FirstRunViewModel(state, () => { });
        vm.Cartella = _root;
        vm.RecuperoOperatoreSelezionato = "Demo Master";
        vm.RecuperoCodiceInserito = "RECOVERY";
        vm.RecuperoNuovoPin = "4433";

        vm.ConfermaRecuperoPinCommand.Execute(null);

        var salvato = state.Master.Operatori.Load().Single();
        Assert.Equal("4433", salvato.PinChiaro);
        Assert.False(string.IsNullOrEmpty(salvato.RecoveryCodeChiaro));
    }

    [Fact]
    public void ConfermaRecuperoPin_CartellaGiaNotaComeAssente_NonSalvaNulla()
    {
        // 6.7.1 (fix rete): la verifica della cartella passa dalla sonda non bloccante. Una radice nota come Assente blocca il
        // recupero con il messaggio dei campi obbligatori, senza alcun salvataggio.
        var state = PreparaStatoDemo();
        var originale = RadiceCondivisaSonda.SondaSistema;
        try
        {
            RadiceCondivisaSonda.SondaSistema = _ => RadiceCondivisaSonda.Esito.Assente;
            RadiceCondivisaSonda.Verifica(_root);
            var vm = new FirstRunViewModel(state, () => { });
            vm.Cartella = _root;
            vm.RecuperoOperatoreSelezionato = "Demo Master";
            vm.RecuperoCodiceInserito = "RECOVERY";
            vm.RecuperoNuovoPin = "4433";

            vm.ConfermaRecuperoPinCommand.Execute(null);

            Assert.NotNull(vm.RecuperoMessaggioErrore);
            Assert.NotEqual("4433", state.Master.Operatori.Load().Single().PinChiaro);
        }
        finally
        {
            RadiceCondivisaSonda.SondaSistema = originale;
            RadiceCondivisaSonda.AzzeraEsitiNoti();
        }
    }

    [Fact]
    public void ResetNuovoAccesso_CancellaSegretiEStatiDellaSessionePrecedente()
    {
        var state = PreparaStatoDemo();
        var vm = new FirstRunViewModel(state, () => { });
        vm.CodiceRecuperoMostrato = "SEGRETO";

        vm.ResetPerNuovoAccesso();

        Assert.False(vm.MostraCodiceRecupero);
        Assert.False(vm.AttendeConfermaCodiceRecupero);
        Assert.False(vm.MostraRecuperoPin);
    }

    [Theory]
    [InlineData("12", "12")]
    [InlineData("abcd", "abcd")]
    [InlineData("5678", "5679")]
    public async Task CambioPinObbligatorio_RifiutaValoriNonValidi(string pin, string conferma)
    {
        var state = PreparaStatoDemo();
        var vm = new FirstRunViewModel(state, () => { });
        vm.OperatoreSelezionato = "Demo Master";
        vm.PinInserito = "1234";
        await vm.ConfermaCommand.ExecuteAsync(null);
        vm.NuovoPinObbligatorio = pin;
        vm.ConfermaNuovoPinObbligatorio = conferma;

        vm.ConfermaCambioPinObbligatorioCommand.Execute(null);

        Assert.True(vm.RichiedeCambioPin);
        Assert.True(state.Master.Operatori.Load().Single().RichiedeCambioPin);
    }

    private AppState PreparaStatoDemo()
    {
        Directory.CreateDirectory(_root);
        var state = TestStateFactory.Create();
        state.ImpostaCartella(_root);
        var demoMaster = new Operatore
        {
            Nome = "Demo Master", Iniziali = "DM", Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"), RecoveryCodeHash = PinService.Hash("RECOVERY"),
            RichiedeCambioPin = true, UltimaModifica = DateTime.Now, ModificatoDa = "Setup"
        };
        RuoloSigilloService.Sigilla(demoMaster); // migrazione 6.4.2: un ruolo non sigillato vale come Viewer.
        state.Master.Operatori.Save(new List<Operatore> { demoMaster });
        return state;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
