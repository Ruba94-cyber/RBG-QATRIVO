using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class SessioneOperatoreStaleHeadlessTests
{
    [Fact]
    public void OperatoreLocaleNonPiuPresente_ApreAccessoInveceDiLasciareLaShellInSolaLettura()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var cartella = Path.Combine(Path.GetTempPath(), "TestLoom_operatore_stale_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cartella);
            var state = TestStateFactory.Create();
            MainViewModel? main = null;
            try
            {
                state.Config.OperatoreAttivo = "Master rinominato";
                state.InizializzaCartella(cartella);

                Assert.True(state.CartellaValida);
                Assert.Null(state.OperatoreCorrente);
                Assert.False(state.PuoScrivere);

                main = new MainViewModel(state);

                Assert.True(main.MostraPrimoAvvio);
                Assert.Equal(
                    LocalizationService.Instance.Traduci("OperatoreMemorizzatoNonDisponibile"),
                    main.FirstRun.MessaggioErrore);
            }
            finally
            {
                if (main is not null) main.Dispose();
                else state.Dispose();
                if (Directory.Exists(cartella)) Directory.Delete(cartella, recursive: true);
            }
        });
    }

    [Fact]
    public void OperatoreDisattivatoDuranteLaSessione_RiapreLaSchermataDiAccesso()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var cartella = Path.Combine(Path.GetTempPath(), "TestLoom_operatore_disattivato_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cartella);
            var state = TestStateFactory.Create();
            MainViewModel? main = null;
            try
            {
                state.InizializzaCartella(cartella);
                var master = TestOperatori.Sigilla(new Operatore
                {
                    Nome = "Master attivo",
                    Ruolo = RuoloOperatore.Master,
                    PinHash = PinService.Hash("1234")
                });
                state.Master.Operatori.Save(new List<Operatore> { master });
                Assert.True(state.ImpostaOperatore(master.Nome, "1234"));

                main = new MainViewModel(state);
                Assert.False(main.MostraPrimoAvvio);

                var operatori = state.Master.Operatori.Load();
                operatori.Single().Disattivo = true;
                state.Master.Operatori.Save(operatori);
                state.RicaricaTutto();

                Assert.Null(state.OperatoreCorrente);
                Assert.True(main.MostraPrimoAvvio);
                Assert.Equal(
                    LocalizationService.Instance.Traduci("OperatoreMemorizzatoNonDisponibile"),
                    main.FirstRun.MessaggioErrore);
            }
            finally
            {
                if (main is not null) main.Dispose();
                else state.Dispose();
                if (Directory.Exists(cartella)) Directory.Delete(cartella, recursive: true);
            }
        });
    }
}
