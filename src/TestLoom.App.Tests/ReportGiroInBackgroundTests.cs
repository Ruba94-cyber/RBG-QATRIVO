using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Report con calcolo a giri (vedi ReportViewModel.Giro): i risultati coincidono con il calcolo in linea, un giro
/// superato non applica nulla, tre cambi di filtro ravvicinati producono un solo calcolo, un errore in background viene
/// registrato senza far crashare la pagina, e l'esportazione con un giro in corso e' quella del filtro visibile.
/// Le letture bloccate si controllano con i ganci di test (<see cref="ReportViewModel.GateLetturaPerTest"/>,
/// <see cref="ReportViewModel.GuastoLetturaPerTest"/>). Fixture come HomeAggiornamentoInBackgroundTests;
/// xunit.runner.json disattiva il parallelismo.
/// </summary>
[Trait("Category", "Lento")]
public sealed class ReportGiroInBackgroundTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public ReportGiroInBackgroundTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportBackground_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        foreach (var path in Directory.GetFiles(_dir, "testcase_CORROTTO-TEST-99.json", SearchOption.AllDirectories))
            File.Delete(path);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
        _state.TestCases.RefreshIndex();
    }

    public void Dispose()
    {
        _state.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Tutto cio' che il Report mostra: anteprima, liste dei filtri, finestra della matrice e totali.</summary>
    private static string Firma(ReportViewModel vm)
    {
        var sb = new StringBuilder();
        sb.Append($"anteprima={vm.TotaleRisultatiAnteprima};avviso={vm.AvvisoVolumeGrande};");
        foreach (var r in vm.Anteprima)
            sb.Append($"[{r.Id}|{r.Componente}|{r.Veicolo}|{r.Template}|{r.Esito}|{r.Data}]");
        sb.Append($";componente={string.Join(',', vm.EcuDisponibili)};veicoli={string.Join(',', vm.VeicoliDisponibili)};");
        sb.Append($"template={string.Join(',', vm.FiltroTemplate.Opzioni.Select(o => o.Etichetta))};");
        sb.Append($"custom={string.Join(',', vm.FiltriCampiCustomReport.Select(f => f.Filtro.Opzioni.Count))};");
        sb.Append($"dimensioni={string.Join(',', vm.FiltriMatrice.Select(f => f.Filtro.Opzioni.Count))};");
        sb.Append($"colonne={string.Join('|', vm.ColonneMatrice)};scorrimento={vm.OffsetRigheMatrice}/{vm.OffsetColonneMatrice};finestra={vm.RigheVisibiliMatrice}/{vm.ColonneVisibiliMatrice};");
        foreach (var riga in vm.RigheMatrice)
            foreach (var cella in riga.Celle)
                sb.Append($"[{riga.Riga}:{cella.Colonna}:{cella.Conformi}/{cella.Valutati}/{cella.Totali}]");
        sb.Append($";totaliColonna={JsonSerializer.Serialize(vm.TotaliColonnaMatrice)};");
        sb.Append($"totaleGenerale={JsonSerializer.Serialize(vm.TotaleGeneraleMatrice)}");
        return sb.ToString();
    }

    /// <summary>Due componente con Test, non annidate e senza Test in comune: ogni filtro seleziona Test diversi dall'altro.</summary>
    private (string Primo, string Secondo) DueEcu()
    {
        var tutti = _state.TestCases.GetAll();
        var componente = tutti.SelectMany(tc => tc.EcuEffettive).Where(e => !string.IsNullOrWhiteSpace(e))
            .GroupBy(e => e, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).ToList();
        foreach (var a in componente)
            foreach (var b in componente)
                if (a != b && !a.Contains(b, StringComparison.Ordinal) && !b.Contains(a, StringComparison.Ordinal) &&
                    !tutti.Any(tc => tc.EcuEffettive.Contains(a) && tc.EcuEffettive.Contains(b)))
                    return (a, b);
        throw new InvalidOperationException("Il seed demo deve avere due componente con Test distinti e non annidate.");
    }

    private static bool RegistratoNelLogCrash(string identificativo)
    {
        var cartella = CrashLogService.LogDirectory;
        if (!Directory.Exists(cartella)) return false;
        return Directory.GetFiles(cartella).Any(file =>
        {
            try { return File.ReadAllText(file).Contains(identificativo, StringComparison.Ordinal); }
            catch { return false; }
        });
    }

    private static string LeggiTestoZip(string percorso)
    {
        using var archivio = ZipFile.OpenRead(percorso);
        var sb = new StringBuilder();
        foreach (var voce in archivio.Entries)
        {
            using var lettore = new StreamReader(voce.Open());
            sb.Append(lettore.ReadToEnd());
        }
        return sb.ToString();
    }

    /// <summary>Testi delle celle dell'export, senza orari e date (cambiano a ogni esportazione): due export dello stesso
    /// filtro devono coincidere su tutto il resto.</summary>
    private static HashSet<string> TestiDelleCelle(string percorso) =>
        Regex.Matches(LeggiTestoZip(percorso), "<x:t[^>]*>([^<]*)</x:t>").Select(m => m.Groups[1].Value)
            .Where(v => !v.Contains(':') && !v.Contains('/')).ToHashSet(StringComparer.Ordinal);

    /// <summary>Il calcolo in background, applicato sul thread UI, da' gli stessi valori del calcolo in linea: dopo il
    /// caricamento iniziale, dopo un cambio di filtro e dopo un cambio di asse della matrice.</summary>
    [Fact]
    public async Task Background_RisultatiUgualiAlCalcoloInLinea()
    {
        var (ecuA, _) = DueEcu();
        var inLinea = new ReportViewModel(_state);
        var background = new ReportViewModel(_state, aggiornaInBackground: true);
        await background.AttendiAggiornamentoAsync();
        Assert.False(background.CaricamentoInCorso);
        Assert.Equal(Firma(inLinea), Firma(background));

        inLinea.FiltroEcu = ecuA;
        background.FiltroEcu = ecuA;
        await background.AttendiAggiornamentoAsync();
        Assert.False(background.CaricamentoInCorso);
        Assert.True(background.TotaleRisultatiAnteprima > 0, "Il filtro di prova deve selezionare dei Test.");
        Assert.Equal(Firma(inLinea), Firma(background));

        inLinea.AsseYMatrice = inLinea.OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Esito);
        background.AsseYMatrice = background.OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Esito);
        await background.AttendiAggiornamentoAsync();
        Assert.Equal(Firma(inLinea), Firma(background));
    }

    [Fact]
    public void ScorrimentoMatriceSpostaLaFinestraPerRigaEColonnaEArrestaAlFondo()
    {
        var vm = new ReportViewModel(_state);
        vm.AsseXMatrice = vm.OpzioniDimensioniMatrice.Single(d => d.Id == DimensioniMatrice.Componente);
        vm.AsseYMatrice = vm.OpzioniDimensioniMatrice.Single(d => d.Id == DimensioniMatrice.Funzionalita);
        vm.AggiornaDimensioniViewportMatrice(larghezza: 300, altezza: 140);

        var righeComplete = vm.ValoriOrdinabili(righe: true);
        var colonneComplete = vm.ValoriOrdinabili(righe: false);
        Assert.True(righeComplete.Count > vm.RigheVisibiliMatrice, "Il seed deve coprire più righe della finestra visibile.");
        Assert.True(colonneComplete.Count > vm.ColonneVisibiliMatrice, "Il seed deve coprire più colonne della finestra visibile.");
        Assert.Equal(1, vm.RigheVisibiliMatrice);
        Assert.Equal(1, vm.ColonneVisibiliMatrice);

        vm.OffsetRigheMatrice = 1;
        Assert.Equal(righeComplete[1], Assert.Single(vm.RigheMatrice).Riga);
        vm.OffsetColonneMatrice = 1;
        Assert.Equal(colonneComplete[1], Assert.Single(vm.ColonneMatrice));

        Assert.True(vm.ScorriMatriceDaRotella(0, -1), "la rotella verso il basso scorre la matrice verticalmente");
        Assert.Equal(4, vm.OffsetRigheMatrice);
        Assert.True(vm.ScorriMatriceDaRotella(-1, 0), "il gesto orizzontale verso destra scorre la matrice");
        Assert.Equal(4, vm.OffsetColonneMatrice);
        Assert.True(vm.ScorriMatriceDaRotella(0, -0.1), "i movimenti frazionari del trackpad vengono consumati");
        Assert.Equal(4, vm.OffsetRigheMatrice);

        vm.OffsetRigheMatrice = double.MaxValue;
        vm.OffsetColonneMatrice = double.MaxValue;
        Assert.Equal(righeComplete[^1], Assert.Single(vm.RigheMatrice).Riga);
        Assert.Equal(colonneComplete[^1], Assert.Single(vm.ColonneMatrice));
        Assert.Equal(vm.MassimoOffsetRigheMatrice, vm.OffsetRigheMatrice);
        Assert.Equal(vm.MassimoOffsetColonneMatrice, vm.OffsetColonneMatrice);
        Assert.False(vm.ScorriMatriceDaRotella(0, -1), "al limite inferiore la rotella può proseguire sulla pagina esterna");

        vm.AggiornaDimensioniViewportMatrice(larghezza: 4_000, altezza: 2_000);
        Assert.Equal(0, vm.OffsetRigheMatrice);
        Assert.Equal(0, vm.OffsetColonneMatrice);
        Assert.Equal(righeComplete[0], vm.RigheMatrice[0].Riga);
        Assert.Equal(colonneComplete[0], vm.ColonneMatrice[0]);
    }

    /// <summary>Un giro superato da un cambio piu' recente non applica i propri risultati: restano quelli dell'ultimo filtro.</summary>
    [Fact]
    public async Task GiroSuperato_RisultatoScartato_NonApplicato()
    {
        var (ecuA, ecuB) = DueEcu();
        var riferimento = new ReportViewModel(_state);
        var vm = new ReportViewModel(_state, aggiornaInBackground: true);
        await vm.AttendiAggiornamentoAsync();
        var entrato = new ManualResetEventSlim(false);
        var rilascio = new ManualResetEventSlim(false);
        var letture = 0;
        vm.GateLetturaPerTest = () =>
        {
            if (Interlocked.Increment(ref letture) == 1)
            {
                entrato.Set();
                rilascio.Wait(TimeSpan.FromSeconds(30));
            }
        };

        vm.FiltroEcu = ecuA;
        Assert.True(entrato.Wait(TimeSpan.FromSeconds(30)), "Il primo giro in background non e' partito.");
        vm.FiltroEcu = ecuB;
        rilascio.Set();
        await vm.AttendiAggiornamentoAsync();

        Assert.Equal(1, vm.GiriScartati);
        Assert.False(vm.CaricamentoInCorso);
        riferimento.FiltroEcu = ecuB;
        Assert.Equal(Firma(riferimento), Firma(vm));
    }

    /// <summary>Revisione F1: un giro in volo viene superato da un cambio di asse dopo un cambio di filtro. Il giro superato aveva
    /// consumato anche l'anteprima, mentre il cambio di asse rifa' solo la matrice: senza il riarmo di tutte le richieste l'anteprima
    /// resterebbe quella senza filtro. Il filtro deve essere riapplicato.</summary>
    [Fact]
    public async Task GiroSuperatoDaCambioAsse_RiapplicaLAnteprimaDelFiltro()
    {
        var (ecuA, _) = DueEcu();
        var riferimento = new ReportViewModel(_state);
        var vm = new ReportViewModel(_state, aggiornaInBackground: true);
        await vm.AttendiAggiornamentoAsync();
        var entrato = new ManualResetEventSlim(false);
        var rilascio = new ManualResetEventSlim(false);
        var letture = 0;
        vm.GateLetturaPerTest = () =>
        {
            if (Interlocked.Increment(ref letture) == 1)
            {
                entrato.Set();
                rilascio.Wait(TimeSpan.FromSeconds(30));
            }
        };

        vm.FiltroEcu = ecuA;
        Assert.True(entrato.Wait(TimeSpan.FromSeconds(30)), "Il primo giro in background non e' partito.");
        vm.AggiungiCampoMatrice(righe: false);
        rilascio.Set();
        await vm.AttendiAggiornamentoAsync();

        Assert.False(vm.CaricamentoInCorso);
        riferimento.FiltroEcu = ecuA;
        riferimento.AggiungiCampoMatrice(righe: false);
        Assert.Equal(Firma(riferimento), Firma(vm));
    }

    /// <summary>Revisione F1: un giro che fallisce mentre e' arrivato un cambio di asse. Il giro fallito aveva consumato anche
    /// l'anteprima: le richieste riarmate devono produrre un giro che la riapplica, senza attendere un'altra azione dell'utente.</summary>
    [Fact]
    public async Task GiroFallitoConCambioPendente_RiapplicaLAnteprimaDelFiltro()
    {
        var (ecuA, _) = DueEcu();
        var riferimento = new ReportViewModel(_state);
        var vm = new ReportViewModel(_state, aggiornaInBackground: true);
        await vm.AttendiAggiornamentoAsync();
        var entrato = new ManualResetEventSlim(false);
        var rilascio = new ManualResetEventSlim(false);
        var letture = 0;
        vm.GateLetturaPerTest = () =>
        {
            if (Interlocked.Increment(ref letture) == 1)
            {
                entrato.Set();
                rilascio.Wait(TimeSpan.FromSeconds(30));
            }
        };
        vm.GuastoLetturaPerTest = () =>
        {
            if (Volatile.Read(ref letture) == 1) throw new InvalidOperationException("Guasto simulato del primo giro.");
        };

        vm.FiltroEcu = ecuA;
        Assert.True(entrato.Wait(TimeSpan.FromSeconds(30)), "Il primo giro in background non e' partito.");
        vm.AggiungiCampoMatrice(righe: false);
        rilascio.Set();
        await vm.AttendiAggiornamentoAsync();

        Assert.NotNull(vm.UltimaEccezioneAggiornamento);
        Assert.False(vm.CaricamentoInCorso);
        riferimento.FiltroEcu = ecuA;
        riferimento.AggiungiCampoMatrice(righe: false);
        Assert.Equal(Firma(riferimento), Firma(vm));
    }

    /// <summary>Revisione (medio 1): un giro superato da un cambio di filtro che NON chiede le liste non fa perdere il cambio di catalogo
    /// che le liste le chiedeva: il giro che supera il primo le riporta, e il campo ha i suoi valori.</summary>
    [Fact]
    public async Task GiroSuperatoConCatalogoCambiato_RileggeLeListeNonLePerde()
    {
        var (ecuA, _) = DueEcu();
        await ModificaUsabileComeFiltro(_state, "progetto-demo", usabile: false);
        var vm = new ReportViewModel(_state, aggiornaInBackground: true);
        await vm.AttendiAggiornamentoAsync();
        Assert.DoesNotContain(vm.FiltriCampiCustomReport, f => f.Chiave == "progetto-demo");

        var entrato = new ManualResetEventSlim(false);
        var rilascio = new ManualResetEventSlim(false);
        var letture = 0;
        vm.GateLetturaPerTest = () =>
        {
            if (Interlocked.Increment(ref letture) == 1)
            {
                entrato.Set();
                rilascio.Wait(TimeSpan.FromSeconds(30));
            }
        };

        await ModificaUsabileComeFiltro(_state, "progetto-demo", usabile: true);
        vm.Aggiorna();
        Assert.True(entrato.Wait(TimeSpan.FromSeconds(30)), "Il giro con il catalogo cambiato non e' partito.");
        vm.FiltroEcu = ecuA;
        rilascio.Set();
        await vm.AttendiAggiornamentoAsync();

        Assert.True(vm.GiriScartati >= 1, "Il giro in corso avrebbe dovuto essere superato.");
        var progetto = vm.FiltriCampiCustomReport.Single(f => f.Chiave == "progetto-demo");
        Assert.NotEmpty(progetto.Filtro.Opzioni);
    }

    /// <summary>Un giro fallito con il catalogo cambiato non consuma la richiesta di liste: il giro successivo la soddisfa.</summary>
    [Fact]
    public async Task GiroFallitoConCatalogoCambiato_RileggeLeListeAlGiroSuccessivo()
    {
        var (ecuA, _) = DueEcu();
        await ModificaUsabileComeFiltro(_state, "progetto-demo", usabile: false);
        var vm = new ReportViewModel(_state, aggiornaInBackground: true);
        await vm.AttendiAggiornamentoAsync();

        var entrato = new ManualResetEventSlim(false);
        var rilascio = new ManualResetEventSlim(false);
        var letture = 0;
        vm.GateLetturaPerTest = () =>
        {
            if (Interlocked.Increment(ref letture) == 1)
            {
                entrato.Set();
                rilascio.Wait(TimeSpan.FromSeconds(30));
            }
        };
        vm.GuastoLetturaPerTest = () =>
        {
            if (Volatile.Read(ref letture) == 1) throw new InvalidOperationException("Guasto di prova del giro.");
        };

        await ModificaUsabileComeFiltro(_state, "progetto-demo", usabile: true);
        vm.Aggiorna();
        Assert.True(entrato.Wait(TimeSpan.FromSeconds(30)), "Il giro con il catalogo cambiato non e' partito.");
        vm.FiltroEcu = ecuA;
        rilascio.Set();
        await vm.AttendiAggiornamentoAsync();

        Assert.NotNull(vm.UltimaEccezioneAggiornamento);
        var progetto = vm.FiltriCampiCustomReport.Single(f => f.Chiave == "progetto-demo");
        Assert.NotEmpty(progetto.Filtro.Opzioni);
    }

    /// <summary>Cambio reale dal catalogo (come l'editor): accesso del Master di prova, modifica, salvataggio. Senza accesso la modifica
    /// non parte e il test non verificherebbe nulla: per questo si controllano l'accesso e l'inizio della modifica.</summary>
    private static async Task ModificaUsabileComeFiltro(AppState state, string chiave, bool usabile)
    {
        var master = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"), "Accesso del Master di prova non riuscito.");
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica, vm.LockInfoTesto);
        vm.Items.Single(c => c.Chiave == chiave).UsabileComeFiltro = usabile;
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
    }

    /// <summary>Tre cambi di filtro ravvicinati producono UN solo calcolo (con il calcolo in linea sarebbero tre) e
    /// il risultato e' quello dell'ultimo filtro.</summary>
    [Fact]
    public async Task TreCambiRavvicinati_UnSoloCalcolo_RisultatoDellUltimoFiltro()
    {
        var (ecuA, ecuB) = DueEcu();
        var riferimento = new ReportViewModel(_state);
        var vm = new ReportViewModel(_state, aggiornaInBackground: true) { RitardoCoalescenza = TimeSpan.FromMilliseconds(800) };
        await vm.AttendiAggiornamentoAsync();
        var calcoli = 0;
        vm.GateLetturaPerTest = () => { Interlocked.Increment(ref calcoli); };

        vm.FiltroEcu = ecuB;
        vm.FiltroEcu = ecuA;
        vm.FiltroEcu = ecuB;
        await vm.AttendiAggiornamentoAsync();

        Assert.Equal(1, calcoli);
        Assert.False(vm.CaricamentoInCorso);
        riferimento.FiltroEcu = ecuB;
        Assert.Equal(Firma(riferimento), Firma(vm));
    }

    /// <summary>Un guasto nel calcolo in background non esce dal Report: viene registrato nel log dei crash, l'indicatore si
    /// spegne e un cambio successivo riporta i valori corretti.</summary>
    [Fact]
    public async Task ErroreInBackground_RegistratoSenzaCrash_LaPaginaRestaUtilizzabile()
    {
        var (ecuA, ecuB) = DueEcu();
        var riferimento = new ReportViewModel(_state);
        var vm = new ReportViewModel(_state, aggiornaInBackground: true);
        await vm.AttendiAggiornamentoAsync();
        var identificativo = Guid.NewGuid().ToString("N");
        vm.GuastoLetturaPerTest = () => throw new InvalidOperationException("guasto di prova " + identificativo);

        vm.FiltroEcu = ecuA;
        await vm.AttendiAggiornamentoAsync();

        Assert.NotNull(vm.UltimaEccezioneAggiornamento);
        Assert.Contains(identificativo, vm.UltimaEccezioneAggiornamento!.Message, StringComparison.Ordinal);
        Assert.False(vm.CaricamentoInCorso);
        Assert.True(RegistratoNelLogCrash(identificativo), "L'eccezione in background non e' finita nel log dei crash.");

        vm.GuastoLetturaPerTest = null;
        vm.FiltroEcu = ecuB;
        await vm.AttendiAggiornamentoAsync();
        riferimento.FiltroEcu = ecuB;
        Assert.Equal(Firma(riferimento), Firma(vm));
    }

    /// <summary>Con un giro in corso (filtro A superato da B) l'export e' quello del filtro visibile B: stessi testi di
    /// cella di un export calcolato in linea con lo stesso filtro, e nessun dato del filtro A nelle righe filtrate.</summary>
    [Fact]
    public async Task Esportazione_ConGiroInCorso_UsaIlFiltroVisibile()
    {
        var (ecuA, ecuB) = DueEcu();
        var vm = new ReportViewModel(_state, aggiornaInBackground: true);
        await vm.AttendiAggiornamentoAsync();
        var entrato = new ManualResetEventSlim(false);
        var rilascio = new ManualResetEventSlim(false);
        var letture = 0;
        vm.GateLetturaPerTest = () =>
        {
            if (Interlocked.Increment(ref letture) == 1)
            {
                entrato.Set();
                rilascio.Wait(TimeSpan.FromSeconds(30));
            }
        };

        vm.FiltroEcu = ecuA;
        Assert.True(entrato.Wait(TimeSpan.FromSeconds(30)), "Il giro per il filtro A non e' partito.");
        vm.FiltroEcu = ecuB;
        var percorsoInCorso = Path.Combine(_dir, "esportazione-in-corso-" + Guid.NewGuid().ToString("N") + ".xlsx");
        var esportazione = vm.EsportaExcelVerso(percorsoInCorso);
        rilascio.Set();
        await esportazione;
        await vm.AttendiAggiornamentoAsync();

        // Riferimento: stesso filtro visibile (B) calcolato in linea su questo stesso stato.
        var riferimento = new ReportViewModel(_state);
        var percorsoRiferimento = Path.Combine(_dir, "esportazione-riferimento-" + Guid.NewGuid().ToString("N") + ".xlsx");
        await riferimento.EsportaExcelVerso(percorsoRiferimento);

        Assert.True(File.Exists(percorsoInCorso), "L'esportazione con giro in corso non ha scritto il file.");
        var inCorso = TestiDelleCelle(percorsoInCorso);
        var attesi = TestiDelleCelle(percorsoRiferimento);
        var differenze = attesi.Except(inCorso).Concat(inCorso.Except(attesi)).Take(10).ToList();
        Assert.Empty(differenze);
    }

    /// <summary>Selezione persistita di un filtro della matrice (liste dei filtri): con il primo giro in background viene ripristinata
    /// DOPO il popolamento delle opzioni, come nel calcolo in linea.</summary>
    [Fact]
    public async Task Background_SelezionePersistitaSiRipristinaDopoIlPopolamento()
    {
        var primo = new ReportViewModel(_state);
        // componente è un filtro globale unico e non compare più nei filtri della matrice: usa una dimensione
        // della matrice che conserva il proprio filtro di valore.
        var filtroFunzionalita = primo.FiltriMatrice.First(f => f.Dimensione.Id == DimensioniMatrice.Funzionalita).Filtro;
        var funzionalitaSelezionata = filtroFunzionalita.Opzioni.First().Valore;
        filtroFunzionalita.Opzioni.First(o => o.Valore == funzionalitaSelezionata).Selezionato = true;
        await primo.AttendiAggiornamentoAsync();
        Assert.Equal(new[] { funzionalitaSelezionata }, filtroFunzionalita.Opzioni.Where(o => o.Selezionato).Select(o => o.Valore).ToList());

        var inLinea = new ReportViewModel(_state);
        var background = new ReportViewModel(_state, aggiornaInBackground: true);
        await background.AttendiAggiornamentoAsync();

        static IReadOnlyList<string> SelezionateFunzionalita(ReportViewModel vm) =>
            vm.FiltriMatrice.First(f => f.Dimensione.Id == DimensioniMatrice.Funzionalita).Filtro.Opzioni
                .Where(o => o.Selezionato).Select(o => o.Valore).ToList();
        Assert.Equal(new[] { funzionalitaSelezionata }, SelezionateFunzionalita(background));
        Assert.Equal(SelezionateFunzionalita(inLinea), SelezionateFunzionalita(background));
        Assert.Equal(Firma(inLinea), Firma(background));
    }

    [Fact]
    public async Task Dispose_AnnullaDebounceEPulisceIlRitardo()
    {
        var (ecuA, _) = DueEcu();
        var vm = new ReportViewModel(_state, aggiornaInBackground: true)
        {
            RitardoCoalescenza = TimeSpan.FromSeconds(5)
        };
        await vm.AttendiAggiornamentoAsync();
        var calcoli = 0;
        var notificheDopoDispose = 0;
        vm.GateLetturaPerTest = () => Interlocked.Increment(ref calcoli);

        vm.FiltroEcu = ecuA;
        vm.PropertyChanged += (_, _) => Interlocked.Increment(ref notificheDopoDispose);
        vm.Dispose();
        await vm.AttendiAggiornamentoAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, calcoli);
        Assert.Equal(0, notificheDopoDispose);
    }

    [Fact]
    public async Task Dispose_InvalidaIlGiroInVoloPrimaCheApplichiRisultati()
    {
        var (ecuA, _) = DueEcu();
        var vm = new ReportViewModel(_state, aggiornaInBackground: true);
        await vm.AttendiAggiornamentoAsync();
        var totalePrima = vm.TotaleRisultatiAnteprima;
        var entrato = new ManualResetEventSlim(false);
        var rilascio = new ManualResetEventSlim(false);
        var notificheDopoDispose = 0;
        vm.RitardoCoalescenza = TimeSpan.Zero;
        vm.GateLetturaPerTest = () =>
        {
            entrato.Set();
            rilascio.Wait(TimeSpan.FromSeconds(30));
        };

        try
        {
            vm.FiltroEcu = ecuA;
            Assert.True(entrato.Wait(TimeSpan.FromSeconds(10)), "Il giro non è partito.");
            vm.PropertyChanged += (_, _) => Interlocked.Increment(ref notificheDopoDispose);
            vm.Dispose();
        }
        finally
        {
            rilascio.Set();
        }

        await vm.AttendiAggiornamentoAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(totalePrima, vm.TotaleRisultatiAnteprima);
        Assert.Equal(0, notificheDopoDispose);
    }
}
