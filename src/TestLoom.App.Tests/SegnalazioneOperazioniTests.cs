using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Richiesta utente (2026-09-18): "nella creazione delle segnalazione i campi stabili per operazioni
/// dovrebbero essere quindi per operazioni com'avviene durante il test e quindi legate a funzionalità e componente
/// in modo da poter avere nel campo strumenti la versione SW/HW legata al componente nel caso... dovrebbe essere il
/// più simile possibile all'inserimento presente nei test". Granularità confermata dall'utente: "una per ogni
/// coppia [Componente×Funzionalità] deve essere esattamente come in test"; nessuna retrocompatibilità richiesta
/// ("non sono presenti vecchie segnalazioni").
/// </summary>
[Collection("Localization")]
public sealed class SegnalazioneOperazioniTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SegnalazioneOperazioni_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi accensione" }, new() { Nome = "Diagnosi iniezione" } });
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new()
            {
                Nome = "UnitLogger",
                Campi = new List<DefinizioneCampoStrumento>
                {
                    // Nessun Ambito esplicito: il default (PerOperazione) e' esattamente il caso da testare.
                    new() { Nome = "Versione SW/HW", Tipo = TipoCampoStrumento.Testo },
                    new() { Nome = "Numero seriale banco", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
                }
            }
        });
        var operatori = state.Master.Operatori.Load();
        operatori.Add(TestOperatori.Sigilla(new Operatore { Nome = "Operatore Rete", Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234") }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Operatore Rete", "1234"));
        return state;
    }

    private static SegnalazioneReteEditorViewModel CreaVm(AppState state)
    {
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.AggiungiEcuCommand.Execute("ECM");
        vm.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
        vm.AggiungiFunzionalitaCommand.Execute("Diagnosi iniezione");
        vm.StrumentoSelezionato = "UnitLogger";
        return vm;
    }

    [Fact]
    public void CampoAmbitoPerOperazione_NonComparePiuNellaListaFlat()
    {
        var vm = CreaVm(CreaStato());
        // "Versione SW/HW" (default PerOperazione) va nelle operazioni, non nella lista flat: solo "Numero
        // seriale banco" (PerTest) resta lì. "Licenza" non e' un buon campo di controllo qui: viene comunque
        // filtrato dalla dedup con "Origine segnalazione" (ECampoOrigineSegnalazione), indipendentemente
        // dall'Ambito — vedi SegnalazioneReteEditorViewModelTests per quella logica.
        var campo = Assert.Single(vm.CampiStrumento);
        Assert.Equal("Numero seriale banco", campo.Nome);
    }

    [Fact]
    public void AggiungiOperazione_ContieneIlCampoPerOperazioneEEredidaEcuFunzionalitaSeUnaSola()
    {
        var state = CreaStato(); // gia' una sola componente/Funzionalita' selezionabile in questo scenario
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
        vm.StrumentoSelezionato = "UnitLogger";

        vm.AggiungiOperazioneSegnalazioneCommand.Execute(null);

        var operazione = Assert.Single(vm.Operazioni);
        Assert.Equal("BCM", operazione.Componente); // ereditato: una sola componente disponibile
        Assert.Equal("Diagnosi accensione", operazione.Funzionalita);
        var campo = Assert.Single(operazione.CampiVisual!);
        Assert.Equal("Versione SW/HW", campo.Nome);
        Assert.True(vm.HaOperazioni);
    }

    [Fact]
    public void DueOperazioni_ValoriDelCampoPerOperazioneRestanoIndipendentiPerEcu()
    {
        var vm = CreaVm(CreaStato());

        vm.AggiungiOperazioneSegnalazioneCommand.Execute(null);
        vm.AggiungiOperazioneSegnalazioneCommand.Execute(null);
        var opBcm = vm.Operazioni[0];
        var opEcm = vm.Operazioni[1];
        opBcm.Componente = "BCM";
        opEcm.Componente = "ECM";

        opBcm.CampiVisual!.Single(c => c.Nome == "Versione SW/HW").Valore = "SW 1.2.0";
        opEcm.CampiVisual!.Single(c => c.Nome == "Versione SW/HW").Valore = "SW 3.4.1";

        Assert.Equal("SW 1.2.0", opBcm.CampiVisual!.Single(c => c.Nome == "Versione SW/HW").Valore);
        Assert.Equal("SW 3.4.1", opEcm.CampiVisual!.Single(c => c.Nome == "Versione SW/HW").Valore);
        // I due dizionari di sfondo sono realmente distinti, non lo stesso oggetto condiviso.
        Assert.NotSame(opBcm.ValoriStrumenti, opEcm.ValoriStrumenti);
    }

    [Fact]
    public void RimuoviOperazione_LaTogliaDallaListaEDalModello()
    {
        var vm = CreaVm(CreaStato());
        vm.AggiungiOperazioneSegnalazioneCommand.Execute(null);
        var operazione = Assert.Single(vm.Operazioni);

        vm.RimuoviOperazioneSegnalazioneCommand.Execute(operazione);

        Assert.Empty(vm.Operazioni);
        Assert.Empty(vm.Nc.OperazioniSegnalate);
        Assert.False(vm.HaOperazioni);
    }

    private static void CompilaCampiObbligatoriComuni(SegnalazioneReteEditorViewModel vm)
    {
        vm.NuovoNumeroPratica = "PRATICA-1"; vm.AggiungiNumeroPraticaCommand.Execute(null);
        vm.NuovoVin = "ZCFC1234500098765"; vm.AggiungiVinCommand.Execute(null);
        vm.NuovaLicenza = "LIC-1"; vm.AggiungiLicenzaCommand.Execute(null);
        vm.DescrizioneBreve = "Test operazioni";
        vm.Descrizione = "Verifica persistenza operazioni per componente.";
        vm.Severita = SeveritaAnomalia.High;
        vm.PrioritaScelta = PrioritaOperativa.Normal;
        vm.StatoScelta = StatoAnomalia.Confermata;
    }

    /// <summary>Percorso di salvataggio semplice (una sola Funzionalità, nessuno split): due operazioni sulla
    /// STESSA funzionalità ma componente diverse, i valori del campo PerOperazione restano distinti per componente.</summary>
    [Fact]
    public void Salva_PersisteLeOperazioniConIValoriPerOperazioneDistintiPerEcu()
    {
        var state = CreaStato();
        var vm = new SegnalazioneReteEditorViewModel(state, null, () => { });
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.AggiungiEcuCommand.Execute("ECM");
        vm.AggiungiFunzionalitaCommand.Execute("Diagnosi accensione");
        vm.StrumentoSelezionato = "UnitLogger";
        vm.AggiungiOperazioneSegnalazioneCommand.Execute(null);
        vm.AggiungiOperazioneSegnalazioneCommand.Execute(null);
        vm.Operazioni[0].Componente = "BCM";
        vm.Operazioni[0].CampiVisual!.Single(c => c.Nome == "Versione SW/HW").Valore = "SW 1.2.0";
        vm.Operazioni[1].Componente = "ECM";
        vm.Operazioni[1].CampiVisual!.Single(c => c.Nome == "Versione SW/HW").Valore = "SW 3.4.1";
        CompilaCampiObbligatoriComuni(vm);

        vm.SalvaCommand.Execute(null);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);

        var salvata = Assert.Single(state.NonConformita.GetAll());
        Assert.Equal(2, salvata.OperazioniSegnalate.Count);
        var opBcm = salvata.OperazioniSegnalate.Single(o => o.Componente == "BCM");
        var opEcm = salvata.OperazioniSegnalate.Single(o => o.Componente == "ECM");
        Assert.Equal("SW 1.2.0", opBcm.ValoriStrumenti["UnitLogger::Versione SW/HW"]);
        Assert.Equal("SW 3.4.1", opEcm.ValoriStrumenti["UnitLogger::Versione SW/HW"]);

        // Ricaricata da zero (rilettura reale, non lo stesso oggetto in memoria): la persistenza e' vera.
        var ricaricata = state.NonConformita.GetById(salvata.Id);
        Assert.NotNull(ricaricata);
        Assert.Equal(2, ricaricata!.OperazioniSegnalate.Count);
    }

    /// <summary>Split in NC "sorelle" per funzionalità (SalvaComeSegnalazioniMultiple, feature preesistente):
    /// ogni operazione appartiene a UNA funzionalità, quindi lo split deve restringere anche
    /// OperazioniSegnalate come già fa con FunzionalitaSegnalate — altrimenti ogni NC sorella erediterebbe
    /// anche le operazioni delle altre funzionalità.</summary>
    [Fact]
    public async Task Salva_ConPiuFunzionalita_OgniNcSorellaRicevesoloLeProprieOperazioni()
    {
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true); // conferma sempre lo split
        try
        {
            var state = CreaStato();
            var vm = CreaVm(state); // 2 componente, 2 Funzionalita', strumento UnitLogger
            vm.AggiungiOperazioneSegnalazioneCommand.Execute(null);
            vm.AggiungiOperazioneSegnalazioneCommand.Execute(null);
            vm.Operazioni[0].Componente = "BCM";
            vm.Operazioni[0].Funzionalita = "Diagnosi accensione";
            vm.Operazioni[0].CampiVisual!.Single(c => c.Nome == "Versione SW/HW").Valore = "SW 1.2.0";
            vm.Operazioni[1].Componente = "ECM";
            vm.Operazioni[1].Funzionalita = "Diagnosi iniezione";
            vm.Operazioni[1].CampiVisual!.Single(c => c.Nome == "Versione SW/HW").Valore = "SW 3.4.1";
            CompilaCampiObbligatoriComuni(vm);

            await vm.SalvaCommand.ExecuteAsync(null);
            Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);

            var tutte = state.NonConformita.GetAll();
            Assert.Equal(2, tutte.Count);
            var ncAccensione = Assert.Single(tutte, n => n.FunzionalitaSegnalate.Contains("Diagnosi accensione"));
            var ncIniezione = Assert.Single(tutte, n => n.FunzionalitaSegnalate.Contains("Diagnosi iniezione"));

            var opAccensione = Assert.Single(ncAccensione.OperazioniSegnalate);
            Assert.Equal("BCM", opAccensione.Componente);
            var opIniezione = Assert.Single(ncIniezione.OperazioniSegnalate);
            Assert.Equal("ECM", opIniezione.Componente);
        }
        finally { ConfirmService.Handler = null; }
    }

    [Fact]
    public async Task Salva_ConAllegatiPrimaDelPrimoSalvataggio_LoSplitCopiaGliAllegatiInOgniSorella()
    {
        var handler = ConfirmService.Handler;
        try
        {
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            var state = CreaStato();
            var vm = CreaVm(state);
            CompilaCampiObbligatoriComuni(vm);
            var sorgente = Path.Combine(_dir, "evidenza-split.txt");
            File.WriteAllText(sorgente, "evidenza per tutte le funzionalità");
            await vm.AggiungiAllegatoCommand.ExecuteAsync(sorgente);
            var allegatoBozza = Assert.Single(vm.Nc.Allegati);
            var percorsoBozza = AttachmentService.PercorsoCompleto(state.Paths, vm.Nc, allegatoBozza);

            await vm.SalvaCommand.ExecuteAsync(null);

            Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
            var sorelle = state.NonConformita.GetAll();
            Assert.Equal(2, sorelle.Count);
            Assert.All(sorelle, sorella =>
            {
                var allegato = Assert.Single(sorella.Allegati);
                var percorso = AttachmentService.PercorsoCompleto(state.Paths, sorella, allegato);
                Assert.True(File.Exists(percorso));
                Assert.Equal("evidenza per tutte le funzionalità", File.ReadAllText(percorso));
            });
            Assert.False(File.Exists(percorsoBozza));
        }
        finally { ConfirmService.Handler = handler; }
    }

    [Fact]
    public void Clona_CopiaLeOperazioniInModoIndipendenteDaLoriginale()
    {
        var nc = new NonConformita
        {
            OperazioniSegnalate = new List<OperazioneSegnalazione>
            {
                new() { Componente = "BCM", Funzionalita = "Diagnosi accensione", ValoriStrumenti = { ["UnitLogger::Versione SW/HW"] = "1.0" } }
            }
        };

        var copia = nc.Clona();
        copia.OperazioniSegnalate[0].Componente = "ECM";
        copia.OperazioniSegnalate[0].ValoriStrumenti["UnitLogger::Versione SW/HW"] = "2.0";

        Assert.Equal("BCM", nc.OperazioniSegnalate[0].Componente);
        Assert.Equal("1.0", nc.OperazioniSegnalate[0].ValoriStrumenti["UnitLogger::Versione SW/HW"]);
    }
}
