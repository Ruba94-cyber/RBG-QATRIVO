using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Le NC archiviate compaiono nell'Archivio (non nelle liste Anomalie) e da li' si possono solo ELIMINARE
/// definitivamente (Master, con conferma): stesso percorso di Anomalie (journal, cestino di recupero, indice).</summary>
[Collection("Localization")]
public sealed class ArchivioNcArchiviateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ArchivioNc_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _originalConfirm = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _originalConfirm;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato(RuoloOperatore ruolo = RuoloOperatore.Master)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Crea("Mario Rossi", RuoloOperatore.Master));
        if (ruolo != RuoloOperatore.Master) ops.Add(TestOperatori.Crea("Luigi Verdi", ruolo));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore(ruolo == RuoloOperatore.Master ? "Mario Rossi" : "Luigi Verdi", "1234");
        return state;
    }

    private static NonConformita SalvaNc(AppState state, string id, bool archiviata)
    {
        var nc = new NonConformita
        {
            Id = id, TestCaseId = "TC-1", DataApertura = DateTime.Now.AddDays(-2), Descrizione = "Riga uno\nRiga due",
            Severita = SeveritaAnomalia.High
        };
        state.NonConformita.Save(nc);
        if (archiviata) state.NonConformita.Archivia(state.NonConformita.GetById(id)!, "Mario Rossi");
        return state.NonConformita.GetById(id)!;
    }

    [Fact]
    public void NcArchiviata_CompareInArchivioENonNelleListe()
    {
        using var state = CreaStato();
        SalvaNc(state, "NC-0001", archiviata: true);
        SalvaNc(state, "NC-0002", archiviata: false);

        using var archivio = new ArchivioViewModel(state);
        var vm = new NonConformitaViewModel(state, _ => { });

        var voce = Assert.Single(archivio.Voci);
        Assert.Equal("Non Conformità", voce.TipoEntita);
        Assert.StartsWith("NC-0001 - Riga uno", voce.Etichetta);
        Assert.False(string.IsNullOrEmpty(voce.Stato));
        Assert.False(voce.RipristinoDisponibile);
        Assert.NotEqual(default, voce.UltimaModifica);
        Assert.DoesNotContain(vm.Righe, r => r.Nc.Id == "NC-0001");
        Assert.Contains(vm.Righe, r => r.Nc.Id == "NC-0002");
    }

    [Fact]
    public async Task EliminaDefinitivamente_RimuoveDatiIndiceEPassaDalCestino()
    {
        using var state = CreaStato();
        var nc = SalvaNc(state, "NC-0001", archiviata: true);
        var path = state.NonConformita.FilePathOf(nc);
        var richieste = 0;
        ConfirmService.Handler = (_, _, _, _) => { richieste++; return Task.FromResult(true); };
        using var archivio = new ArchivioViewModel(state);

        await archivio.EliminaVoceDefinitivamenteCommand.ExecuteAsync(Assert.Single(archivio.Voci));

        Assert.Equal(1, richieste); // conferma esplicita
        Assert.Null(archivio.MessaggioErrore);
        Assert.Empty(archivio.Voci);
        // Dentro la transazione di workflow il file diventa una lapide (non una NC leggibile): la rilettura da disco lo conferma sotto.
        Assert.Null(JsonIo.TryRead<NonConformita>(path, out _)?.Id);
        Assert.Null(state.NonConformita.GetById("NC-0001"));
        Assert.Equal(0, state.NonConformita.ContaArchiviate());
        // L'indice locale e' coerente: nemmeno dopo una rilettura completa la NC riappare.
        state.NonConformita.RefreshIndex();
        Assert.Empty(state.NonConformita.GetAll());
        // Recuperabile solo dal cestino tecnico.
        Assert.NotEmpty(Directory.EnumerateFiles(state.Paths.RecoveryBinDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EliminaDefinitivamente_ConfermaRifiutata_NonCancellaNulla()
    {
        using var state = CreaStato();
        SalvaNc(state, "NC-0001", archiviata: true);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);
        using var archivio = new ArchivioViewModel(state);

        await archivio.EliminaVoceDefinitivamenteCommand.ExecuteAsync(Assert.Single(archivio.Voci));

        Assert.NotNull(state.NonConformita.GetById("NC-0001"));
        Assert.Single(archivio.Voci);
    }

    [Fact]
    public void NonMaster_NonPuoEliminare()
    {
        using var state = CreaStato(RuoloOperatore.Standard);
        SalvaNc(state, "NC-0001", archiviata: true);
        using var archivio = new ArchivioViewModel(state);

        var voce = Assert.Single(archivio.Voci);

        Assert.False(archivio.PuoModificare);
        Assert.False(archivio.EliminaVoceDefinitivamenteCommand.CanExecute(voce));
        Assert.NotNull(state.NonConformita.GetById("NC-0001"));
    }

    [Fact]
    public async Task EliminaDefinitivamente_ConflittoDiRevisione_NonCorrompeLoStato()
    {
        using var state = CreaStato();
        var nc = SalvaNc(state, "NC-0001", archiviata: true);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        using var archivio = new ArchivioViewModel(state);
        var voce = Assert.Single(archivio.Voci);

        // Un'altra postazione modifica la NC dopo il caricamento dell'Archivio.
        var remoto = EntityStateFingerprint.Clone(state.NonConformita.GetById(nc.Id)!);
        remoto.Descrizione = "modifica concorrente";
        state.NonConformita.Save(remoto, remoto.RevisioneDati);

        await archivio.EliminaVoceDefinitivamenteCommand.ExecuteAsync(voce);

        Assert.NotNull(archivio.MessaggioErrore);
        state.NonConformita.RefreshIndex();
        var sopravvissuta = Assert.Single(state.NonConformita.GetAll());
        Assert.Equal("modifica concorrente", sopravvissuta.Descrizione);
        Assert.True(sopravvissuta.Archiviata);
        Assert.True(File.Exists(state.NonConformita.FilePathOf(sopravvissuta)));
        Assert.Single(archivio.Voci);
    }

    [Fact]
    public async Task SvuotaArchivio_NonEliminaLeNcArchiviate()
    {
        using var state = CreaStato();
        SalvaNc(state, "NC-0001", archiviata: true);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        using var archivio = new ArchivioViewModel(state);

        await archivio.SvuotaArchivioCommand.ExecuteAsync(null);

        Assert.NotNull(state.NonConformita.GetById("NC-0001"));
    }

    [Fact]
    public async Task EliminaDefinitivamente_RiportaLeSegnalazioniClassificateADaClassificare()
    {
        using var state = CreaStato();
        var nc = SalvaNc(state, "NC-0001", archiviata: true);
        var seg = new SegnalazioneDaClassificare
        {
            TestId = "TC-1", CondizioneUid = Guid.NewGuid(), EsecuzioneIterazione = 1, CreataIl = DateTime.Now,
            Stato = StatoSegnalazione.Classificata, AnomaliaUid = nc.Uid
        };
        state.Segnalazioni.Save(seg);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        using var archivio = new ArchivioViewModel(state);

        await archivio.EliminaVoceDefinitivamenteCommand.ExecuteAsync(Assert.Single(archivio.Voci));

        var autorevole = state.Segnalazioni.Get(seg.Uid)!;
        Assert.Equal(StatoSegnalazione.DaClassificare, autorevole.Stato);
        Assert.Null(autorevole.AnomaliaUid);
    }
}
