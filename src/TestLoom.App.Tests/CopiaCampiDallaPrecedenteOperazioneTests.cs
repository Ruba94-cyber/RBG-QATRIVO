using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>"Copia dalla precedente" sulla singola operazione (6.6.1): propaga i campi strumento (PerOperazione, degli
/// strumenti selezionati nel Test) dall'operazione subito precedente nella stessa REV. Riusa l'anteprima, la conferma e
/// l'annullamento della copia verso una nuova operazione. Non copia esito, note, guasto, funzionalita', componente, allegati,
/// ripetizioni o campi File. Nessun effetto sulle altre operazioni.</summary>
/// <remarks>[Collection("Localization")]: questi test mutano ConfirmService.Handler, statico e condiviso dal processo.</remarks>
[Collection("Localization")]
public sealed class CopiaCampiDallaPrecedenteOperazioneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CopiaCampi_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    /// <summary>Editor con almeno tre operazioni nella REV corrente; solo lo strumento UnitLogger e' selezionato nel Test.
    /// <paramref name="campiReali"/> null = configurazione demo del seed, altrimenti lo strumento UnitLogger reale.</summary>
    private TestCaseEditorViewModel CreaVm(List<DefinizioneCampoStrumento>? campiReali = null)
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        if (campiReali != null)
        {
            var strumenti = state.Master.Strumenti.Load();
            strumenti.Single(s => s.Nome == "UnitLogger").Campi = campiReali;
            state.Master.Strumenti.Save(strumenti);
        }
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        foreach (var strumento in vm.StrumentiOptions) strumento.Selezionato = strumento.Valore == "UnitLogger";
        while (vm.RevisioneCorrente!.Condizioni.Count < 3) vm.AggiungiCondizioneCommand.Execute(null);
        return vm;
    }

    private static Condizione Op(TestCaseEditorViewModel vm, int indice) => vm.RevisioneCorrente!.Condizioni[indice];

    [Fact]
    public void CopiaCampi_OperazioneConFunzionalitaDiversa_CopiaSoloICampiStrumento()
    {
        var vm = CreaVm();
        var origine = Op(vm, 0);
        var destinazione = Op(vm, 1);
        origine.Funzionalita = "F1";
        origine.Nota = "nota origine";
        origine.ValoriStrumenti["UnitLogger::Pacchetto"] = "WU-A";
        origine.ValoriStrumenti["UnitLogger::Versione pacchetto"] = "2663";
        destinazione.Funzionalita = "F2";
        destinazione.Nota = "nota destinazione";
        destinazione.Esito = Esito.NonConforme;
        var ecuPrima = destinazione.Componente.ToArray();

        vm.CopiaCampiDallaPrecedenteOperazioneCommand.Execute(destinazione);
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.Equal("WU-A", destinazione.ValoriStrumenti["UnitLogger::Pacchetto"]);
        Assert.Equal("2663", destinazione.ValoriStrumenti["UnitLogger::Versione pacchetto"]);
        Assert.Equal("F2", destinazione.Funzionalita);
        Assert.Equal("nota destinazione", destinazione.Nota);
        Assert.Equal(Esito.NonConforme, destinazione.Esito);
        Assert.Equal(ecuPrima, destinazione.Componente.ToArray());
        Assert.Equal("F1", origine.Funzionalita);
        Assert.True(vm.ModificheNonSalvate);
    }

    [Fact]
    public void CopiaCampi_CampiVuotiNellaPrecedente_NonSovrascrivonoLaDestinazione()
    {
        var vm = CreaVm();
        var origine = Op(vm, 0);
        var destinazione = Op(vm, 1);
        origine.ValoriStrumenti["UnitLogger::Lingua"] = "   ";
        origine.ValoriStrumenti["UnitLogger::Pacchetto"] = "WU-A";
        destinazione.ValoriStrumenti["UnitLogger::Lingua"] = "it-IT";

        vm.CopiaCampiDallaPrecedenteOperazioneCommand.Execute(destinazione);

        Assert.DoesNotContain(vm.AnteprimaCopiaPrecedente, riga => riga.Chiave == "UnitLogger::Lingua");
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);
        Assert.Equal("it-IT", destinazione.ValoriStrumenti["UnitLogger::Lingua"]);
        Assert.Equal("WU-A", destinazione.ValoriStrumenti["UnitLogger::Pacchetto"]);
    }

    [Fact]
    public void CopiaCampi_StrumentoNonSelezionatoNellaDestinazione_NonCopiaIlCampo()
    {
        var vm = CreaVm();
        Op(vm, 0).ValoriStrumenti["UnitLogger::Pacchetto"] = "WU-A";
        var destinazione = Op(vm, 1);
        vm.StrumentiOptions.Single(s => s.Valore == "UnitLogger").Selezionato = false;

        vm.CopiaCampiDallaPrecedenteOperazioneCommand.Execute(destinazione);

        Assert.False(vm.MostraAnteprimaCopiaPrecedente);
        Assert.DoesNotContain(vm.AnteprimaCopiaPrecedente, riga => riga.Chiave == "UnitLogger::Pacchetto");
        Assert.False(destinazione.ValoriStrumenti.ContainsKey("UnitLogger::Pacchetto"));
    }

    [Fact]
    public void CopiaCampi_SpuntaTolta_NonCopiaIlCampo()
    {
        var vm = CreaVm();
        var origine = Op(vm, 0);
        var destinazione = Op(vm, 1);
        origine.ValoriStrumenti["UnitLogger::Pacchetto"] = "WU-A";
        origine.ValoriStrumenti["UnitLogger::Versione pacchetto"] = "2663";

        vm.CopiaCampiDallaPrecedenteOperazioneCommand.Execute(destinazione);
        vm.AnteprimaCopiaPrecedente.Single(riga => riga.Chiave == "UnitLogger::Pacchetto").Selezionato = false;
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.False(destinazione.ValoriStrumenti.ContainsKey("UnitLogger::Pacchetto"));
        Assert.Equal("2663", destinazione.ValoriStrumenti["UnitLogger::Versione pacchetto"]);
    }

    [Fact]
    public void CopiaCampi_SolaLettura_ComandoNonEseguibile()
    {
        var vm = CreaVm();
        Op(vm, 0).ValoriStrumenti["UnitLogger::Pacchetto"] = "WU-A";
        var destinazione = Op(vm, 1);
        vm.InModifica = false;

        Assert.False(vm.CopiaCampiDallaPrecedenteOperazioneCommand.CanExecute(destinazione));
        vm.CopiaCampiDallaPrecedenteOperazioneCommand.Execute(destinazione);

        Assert.False(vm.MostraAnteprimaCopiaPrecedente);
        Assert.False(destinazione.ValoriStrumenti.ContainsKey("UnitLogger::Pacchetto"));
    }

    [Fact]
    public void CopiaCampi_PrimaOperazione_ComandoNonDisponibile()
    {
        var vm = CreaVm();
        var prima = Op(vm, 0);

        Assert.False(vm.CopiaCampiDallaPrecedenteOperazioneCommand.CanExecute(prima));
        vm.CopiaCampiDallaPrecedenteOperazioneCommand.Execute(prima);

        Assert.False(vm.MostraAnteprimaCopiaPrecedente);
    }

    [Fact]
    public void CopiaCampi_NonTocca_LeAltreOperazioni()
    {
        var vm = CreaVm();
        Op(vm, 0).ValoriStrumenti["UnitLogger::Pacchetto"] = "WU-A";
        var destinazione = Op(vm, 1);
        var terza = Op(vm, 2);
        terza.ValoriStrumenti["UnitLogger::Pacchetto"] = "WU-C";
        terza.Funzionalita = "F3";

        vm.CopiaCampiDallaPrecedenteOperazioneCommand.Execute(destinazione);
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.Equal("WU-A", destinazione.ValoriStrumenti["UnitLogger::Pacchetto"]);
        Assert.Equal("WU-C", terza.ValoriStrumenti["UnitLogger::Pacchetto"]);
        Assert.Equal("F3", terza.Funzionalita);
    }

    [Fact]
    public void CopiaCampi_SostituzioneDiValoriDiversi_RichiedeConfermaEsplicita()
    {
        var richieste = 0;
        var risposta = false;
        ConfirmService.Handler = (_, _, _, _) =>
        {
            richieste++;
            return Task.FromResult(risposta);
        };
        var vm = CreaVm();
        var origine = Op(vm, 0);
        var destinazione = Op(vm, 1);
        origine.ValoriStrumenti["UnitLogger::Versione pacchetto"] = "2663";
        destinazione.ValoriStrumenti["UnitLogger::Versione pacchetto"] = "1000";

        vm.CopiaCampiDallaPrecedenteOperazioneCommand.Execute(destinazione);
        Assert.True(vm.AnteprimaCopiaPrecedente.Single(riga => riga.Chiave == "UnitLogger::Versione pacchetto").Sostituisce);
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);

        // Senza conferma nulla cambia e l'anteprima resta aperta per una nuova decisione.
        Assert.Equal(1, richieste);
        Assert.Equal("1000", destinazione.ValoriStrumenti["UnitLogger::Versione pacchetto"]);
        Assert.True(vm.MostraAnteprimaCopiaPrecedente);

        risposta = true;
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.Equal(2, richieste);
        Assert.Equal("2663", destinazione.ValoriStrumenti["UnitLogger::Versione pacchetto"]);
    }

    [Fact]
    public void CopiaCampi_Annulla_RiportaIValoriPrecedenti()
    {
        var vm = CreaVm();
        Op(vm, 0).ValoriStrumenti["UnitLogger::Pacchetto"] = "WU-A";
        var destinazione = Op(vm, 1);
        destinazione.Funzionalita = "F2";

        vm.CopiaCampiDallaPrecedenteOperazioneCommand.Execute(destinazione);
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);
        Assert.True(vm.PuoAnnullareCopiaPrecedente);

        vm.AnnullaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.False(destinazione.ValoriStrumenti.ContainsKey("UnitLogger::Pacchetto"));
        Assert.Equal("F2", destinazione.Funzionalita);
        Assert.False(vm.PuoAnnullareCopiaPrecedente);
    }

}
