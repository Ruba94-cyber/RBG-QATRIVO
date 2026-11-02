using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressioni per i cataloghi master condivisi. Il requisito operativo non e' soltanto avere 100k Test:
/// Funzionalita/Requisiti/componente devono poter crescere allo stesso ordine di grandezza senza perdere aggiornamenti
/// quando due repliche OneDrive vedono il file in momenti diversi.
/// </summary>
public sealed class MasterDataScaleConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterScale_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, true); }
            catch { }
        }
    }

    private AppState CreateState(RuoloOperatore ruolo)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var operatori = state.Master.Operatori.Load().ToList();
        var op = new Operatore
        {
            Nome = "Op Master Scale",
            Ruolo = ruolo,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        };
        RuoloSigilloService.Sigilla(op); // migrazione 6.4.2: un ruolo non sigillato vale come Viewer.
        operatori.Add(op);
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Op Master Scale", "1234"));
        return state;
    }

    [Fact]
    public async Task EditorFunzionalita_MasterNonSovrascriveUnaReplicaArrivataDopoApertura()
    {
        using var state = CreateState(RuoloOperatore.Master);
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Base", ModificatoDa = "Seed" }
        });

        using var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "Aggiunta locale";

        // Un Master puo' anche cambiare righe esistenti: non tentiamo alcun merge implicito. La replica remota
        // arrivata dopo l'apertura deve quindi provocare conflitto e nessun dato viene sovrascritto.
        var remoto = state.Master.Funzionalita.Load();
        remoto.Add(new Funzionalita { Nome = "Aggiunta remota", ModificatoDa = "PC-B" });
        state.Master.Funzionalita.Save(remoto);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.True(vm.InModifica);
        var autorevole = state.Master.Funzionalita.Load();
        Assert.Contains(autorevole, f => f.Nome == "Aggiunta remota");
        Assert.DoesNotContain(autorevole, f => f.Nome == "Aggiunta locale");
    }

    [Fact]
    public async Task EditorFunzionalita_StandardFondeDueAggiunteConcorrenzialiDistinte()
    {
        using var state = CreateState(RuoloOperatore.Standard);
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Base", ModificatoDa = "Seed" }
        });

        using var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "Aggiunta PC-A";

        // PC-B pubblica una voce diversa mentre PC-A e' ancora aperto. Siccome lo Standard non puo' aver
        // modificato/rimosso righe preesistenti, il merge additivo e' dimostrabilmente sicuro.
        var remoto = state.Master.Funzionalita.Load();
        remoto.Add(new Funzionalita { Nome = "Aggiunta PC-B", ModificatoDa = "PC-B" });
        state.Master.Funzionalita.Save(remoto);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.False(vm.InModifica);
        var autorevole = state.Master.Funzionalita.Load();
        Assert.Contains(autorevole, f => f.Nome == "Aggiunta PC-A");
        Assert.Contains(autorevole, f => f.Nome == "Aggiunta PC-B");
        Assert.Equal(3, autorevole.Count);
        Assert.Equal(3, vm.Items.Count); // anche la UI viene riallineata al risultato autorevole del merge.
    }

    [Fact]
    public void RepositoryMergeAdditivo_RifiutaCollisioneDiChiaveConContenutoDiverso()
    {
        using var state = CreateState(RuoloOperatore.Standard);
        var baseSnapshot = new List<Funzionalita>
        {
            new() { Nome = "Base", ModificatoDa = "Seed" }
        };
        state.Master.Funzionalita.Save(baseSnapshot);

        var locale = state.Master.Funzionalita.Load();
        locale.Add(new Funzionalita { Nome = "Stessa", Categorie = new() { "Locale" }, ModificatoDa = "PC-A" });

        var remoto = state.Master.Funzionalita.Load();
        remoto.Add(new Funzionalita { Nome = "Stessa", Categorie = new() { "Remoto" }, ModificatoDa = "PC-B" });
        state.Master.Funzionalita.Save(remoto);

        // Phase D distingue una collisione di chiave business tra due UID diverse (PhaseDMasterBusinessKeyConflictException,
        // integrità del dato) da uno scostamento generico di revisione/base (MasterDataConflictException):
        // qui il caso è il primo, più specifico dei due.
        Assert.Throws<PhaseDMasterBusinessKeyConflictException>(() =>
            state.Master.Funzionalita.SaveAdditionsPreservingRemote(locale, baseSnapshot, f => f.Nome));

        var autorevole = state.Master.Funzionalita.Load();
        Assert.Single(autorevole, f => f.Nome == "Stessa");
        Assert.Equal("Remoto", autorevole.Single(f => f.Nome == "Stessa").Categorie.Single());
    }

    [Fact]
    public void RepositorySaveLegacy_ListaDirettaDaLoad_RilevaUnaScritturaRemota()
    {
        using var state = CreateState(RuoloOperatore.Master);
        var repo = state.Master.Funzionalita;
        repo.Save(new List<Funzionalita> { new() { Nome = "Base", ModificatoDa = "Seed" } });

        var pcA = repo.Load();
        var pcB = repo.Load();
        pcB.Add(new Funzionalita { Nome = "PC-B", ModificatoDa = "PC-B" });
        repo.Save(pcB); // overload legacy, ma la List mantiene il fingerprint della propria Load().

        // A differenza di una pura aggiunta concorrenziale (vedi LegacyInstrumentAdditiveMergeTests, dove due
        // Load->Add->Save su chiavi distinte si riconciliano automaticamente), qui pcA MODIFICA la riga "Base"
        // gia' presente nella fotografia di apertura: HaEditARigaPreesistente(pcA) rileva l'edit e forza
        // allowSilentRemoteMerge a false, quindi la scrittura remota di PC-B (avvenuta dopo il Load() di pcA)
        // deve bloccare il salvataggio invece di essere fusa silenziosamente.
        pcA[0].Categorie.Add("Modifica locale");
        Assert.Throws<MasterDataConflictException>(() => repo.Save(pcA));

        var autorevole = repo.Load();
        Assert.Contains(autorevole, f => f.Nome == "PC-B");
        Assert.Empty(autorevole.Single(f => f.Nome == "Base").Categorie);
    }

    [Fact]
    public void RepositorySaveLegacy_DueLoadSuFileAssente_NonCreanoEntrambiIlPrimoWriter()
    {
        using var state = CreateState(RuoloOperatore.Master);
        var path = Path.Combine(_dir, "bootstrap-operatori-concorrenza.json");
        var pcA = new MasterDataRepository<Operatore>(path);
        var pcB = new MasterDataRepository<Operatore>(path);

        var listaA = pcA.Load();
        var listaB = pcB.Load();
        Assert.Empty(listaA);
        Assert.Empty(listaB);

        listaA.Add(new Operatore { Nome = "Primo A", Ruolo = RuoloOperatore.Master, ModificatoDa = "PC-A" });
        pcA.Save(listaA);

        listaB.Add(new Operatore { Nome = "Primo B", Ruolo = RuoloOperatore.Master, ModificatoDa = "PC-B" });
        Assert.Throws<MasterDataConflictException>(() => pcB.Save(listaB));

        var autorevole = pcA.Load();
        Assert.Single(autorevole);
        Assert.Equal("Primo A", autorevole[0].Nome);
    }

    [Fact]
    public void RepositorySeedCostruitoSuFileAssente_DueWriterIdenticiSonoIdempotenti()
    {
        using var state = CreateState(RuoloOperatore.Master);
        var path = Path.Combine(_dir, "bootstrap-seed-identico.json");
        var pcA = new MasterDataRepository<Funzionalita>(path);
        var pcB = new MasterDataRepository<Funzionalita>(path);

        var seedA = new List<Funzionalita> { new() { Nome = "Default", ModificatoDa = "Setup" } };
        var seedB = EntityStateFingerprint.Clone(seedA);
        pcA.Save(seedA);
        pcB.Save(seedB); // deve essere no-op, non una seconda riscrittura.

        var autorevole = pcA.Load();
        Assert.Single(autorevole);
        Assert.Equal("Default", autorevole[0].Nome);
        Assert.False(File.Exists(path + ".prev")); // una seconda write avrebbe creato la safety snapshot.
    }

    [Fact]
    public void RepositorySeedCostruitoSuFileAssente_WriterDiversoNonSovrascriveIlPrimo()
    {
        using var state = CreateState(RuoloOperatore.Master);
        var path = Path.Combine(_dir, "bootstrap-seed-divergente.json");
        var pcA = new MasterDataRepository<Funzionalita>(path);
        var pcB = new MasterDataRepository<Funzionalita>(path);

        pcA.Save(new List<Funzionalita> { new() { Nome = "Default A", ModificatoDa = "PC-A" } });
        Assert.Throws<MasterDataConflictException>(() =>
            pcB.Save(new List<Funzionalita> { new() { Nome = "Default B", ModificatoDa = "PC-B" } }));

        var autorevole = pcA.Load();
        Assert.Single(autorevole);
        Assert.Equal("Default A", autorevole[0].Nome);
        Assert.False(File.Exists(path + ".prev"));
    }

    [Fact]
    public void RepositoryStrumenti_SaveDiListaFiltrata_PreservaLeRigheArchiviate()
    {
        using var state = CreateState(RuoloOperatore.Master);
        var repo = state.Master.Strumenti;
        repo.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "Attivo", Archiviata = false, ModificatoDa = "Seed" },
            new() { Nome = "Storico archiviato", Archiviata = true, ModificatoDa = "Seed" }
        });

        // Riproduce esattamente il percorso legacy del Test editor: Load -> filtro UI -> ToList -> Save.
        var visibili = repo.Load().Where(s => !s.Archiviata).ToList();
        Assert.Single(visibili);
        visibili[0].ModificatoDa = "PC-A";
        repo.Save(visibili);

        var autorevole = repo.Load();
        Assert.Equal(2, autorevole.Count);
        Assert.Contains(autorevole, s => s.Nome == "Storico archiviato" && s.Archiviata);
        Assert.Equal("PC-A", autorevole.Single(s => s.Nome == "Attivo").ModificatoDa);
    }

    [Fact]
    public void RepositoryStrumenti_ThreeWayFondeModificheSuStrumentiDiversiEPreservaArchiviati()
    {
        using var state = CreateState(RuoloOperatore.Master);
        var repo = state.Master.Strumenti;
        repo.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "Tool A", Campi = new() { new() { Nome = "Campo A seed" } }, ModificatoDa = "Seed" },
            new() { Nome = "Tool B", Campi = new() { new() { Nome = "Campo B seed" } }, ModificatoDa = "Seed" },
            new() { Nome = "Storico archiviato", Archiviata = true, ModificatoDa = "Seed" }
        });

        // PC-A lavora sulla stessa lista filtrata usata dal Test editor. PC-B modifica nel frattempo Tool B:
        // siccome PC-A tocca soltanto Tool A, il three-way può conservare entrambe le modifiche senza scegliere
        // arbitrariamente un vincitore e senza reintrodurre la cancellazione delle righe archiviate.
        var pcAVisibili = repo.Load().Where(s => !s.Archiviata).ToList();
        var pcB = repo.Load();
        var toolB = pcB.Single(s => s.Nome == "Tool B");
        toolB.Campi = new() { new() { Nome = "Campo B remoto" } };
        toolB.ModificatoDa = "PC-B";
        repo.Save(pcB);

        var toolA = pcAVisibili.Single(s => s.Nome == "Tool A");
        toolA.Campi = new() { new() { Nome = "Campo A locale" } };
        toolA.ModificatoDa = "PC-A";
        repo.Save(pcAVisibili);

        var autorevole = repo.Load();
        Assert.Equal("Campo A locale", autorevole.Single(s => s.Nome == "Tool A").Campi.Single().Nome);
        Assert.Equal("Campo B remoto", autorevole.Single(s => s.Nome == "Tool B").Campi.Single().Nome);
        Assert.Contains(autorevole, s => s.Nome == "Storico archiviato" && s.Archiviata);
    }

    [Fact]
    public void RepositoryStrumenti_ThreeWayRifiutaModificheDivergentiSulloStessoStrumento()
    {
        using var state = CreateState(RuoloOperatore.Master);
        var repo = state.Master.Strumenti;
        repo.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "Tool A", Campi = new() { new() { Nome = "Campo seed" } }, ModificatoDa = "Seed" },
            new() { Nome = "Storico archiviato", Archiviata = true, ModificatoDa = "Seed" }
        });

        var pcAVisibili = repo.Load().Where(s => !s.Archiviata).ToList();
        var pcB = repo.Load();
        var toolB = pcB.Single(s => s.Nome == "Tool A");
        toolB.Campi = new() { new() { Nome = "Campo remoto" } };
        toolB.ModificatoDa = "PC-B";
        repo.Save(pcB);

        var toolA = pcAVisibili.Single();
        toolA.Campi = new() { new() { Nome = "Campo locale" } };
        toolA.ModificatoDa = "PC-A";
        Assert.Throws<MasterDataConflictException>(() => repo.Save(pcAVisibili));

        var autorevole = repo.Load();
        Assert.Equal("Campo remoto", autorevole.Single(s => s.Nome == "Tool A").Campi.Single().Nome);
        Assert.Equal("PC-B", autorevole.Single(s => s.Nome == "Tool A").ModificatoDa);
        Assert.Contains(autorevole, s => s.Nome == "Storico archiviato" && s.Archiviata);
    }

    [Fact]
    public async Task EditorMaster_ConflittoNonSporcaChangelogETimestampDegliOggettiLive()
    {
        using var state = CreateState(RuoloOperatore.Master);
        var timestampSeed = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "Componente Base", Archiviata = false, UltimaModifica = timestampSeed, ModificatoDa = "Seed" }
        });

        using var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        var locale = Assert.Single(vm.Items);
        var changelogPrima = locale.Changelog.Count;
        locale.Archiviata = true;

        // Una replica remota cambia il file dopo l'apertura. Il tentativo di commit deve fallire senza
        // aggiungere ai dati live metadati che non sono mai stati realmente persistiti.
        var remoto = state.Master.Componente.Load();
        remoto.Add(new Componente { Nome = "Componente PC-B", ModificatoDa = "PC-B" });
        state.Master.Componente.Save(remoto);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.True(vm.InModifica);
        Assert.True(locale.Archiviata); // la modifica utente resta disponibile per essere risolta/riprovata.
        Assert.Equal(changelogPrima, locale.Changelog.Count);
        Assert.Equal(timestampSeed, locale.UltimaModifica);
        Assert.Equal("Seed", locale.ModificatoDa);
    }

    [Fact]
    public async Task EditorMaster_RevocaRuoloDuranteModifica_BloccaIlSalvataggioFailClosed()
    {
        using var state = CreateState(RuoloOperatore.Master);
        using var vm = new OperatoriViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        var locale = vm.Items.Single(o => o.Nome == "Op Master Scale");
        var inizialiPrima = locale.Iniziali;
        locale.Iniziali = "ZZ";

        // Simula la replica amministrativa che declassa l'operatore mentre la pagina e' gia' aperta.
        var remoto = state.Master.Operatori.Load();
        remoto.Single(o => o.Nome == "Op Master Scale").Ruolo = RuoloOperatore.Standard;
        state.Master.Operatori.Save(remoto);
        state.RicaricaTutto();
        Assert.False(vm.PuoModificare);
        Assert.False(vm.PuoCreare);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.True(vm.InModifica);
        var autorevole = state.Master.Operatori.Load().Single(o => o.Nome == "Op Master Scale");
        Assert.Equal(RuoloOperatore.Standard, autorevole.Ruolo);
        Assert.Equal(inizialiPrima, autorevole.Iniziali);
    }

    [Fact]
    [Trait("Category", "Scale")]
    public async Task Funzionalita100k_StandardPuoAprireAggiungereESalvareSenzaPercorsiQuadratici()
    {
        using var state = CreateState(RuoloOperatore.Standard);
        const int count = 100_000;
        state.Master.Funzionalita.Save(Enumerable.Range(0, count)
            .Select(i => new Funzionalita
            {
                Nome = $"Funzione-{i:D6}",
                Categorie = new List<string> { $"Categoria-{i % 100:D3}" },
                ModificatoDa = "Seed"
            })
            .ToList());

        var watch = Stopwatch.StartNew();
        using var vm = new FunzionalitaViewModel(state);
        var apertura = watch.Elapsed;
        Assert.True(vm.UsaPaginazione);
        Assert.Equal(vm.DimensionePagina, vm.Items.Count);
        Assert.Equal(vm.DimensionePagina, vm.ItemsAttivi.Count);
        Assert.True(vm.PaginaSuccessivaDisponibile);
        Assert.True(vm.PuoCreare);
        Assert.False(vm.PuoModificare);

        // Anche la navigazione deve cambiare soltanto la pagina locale, non caricare il master completo.
        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(2, vm.PaginaCorrente);
        Assert.Equal(vm.DimensionePagina, vm.Items.Count);
        vm.PaginaPrecedenteCommand.Execute(null);
        Assert.Equal(1, vm.PaginaCorrente);

        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        Assert.True(vm.UsaPaginazione);
        // Dal refactor "Edit Phase D master pages without full-catalog promotion" (91fc21f) l'editing di un
        // catalogo a 100k righe resta O(page-size) in RAM invece di promuovere all'intera fotografia: senza
        // filtro attivo la finestra caricata e' semplicemente la prima pagina.
        Assert.Equal(vm.DimensionePagina, vm.Items.Count);
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "Funzione-100000";
        vm.Items.Last().Categorie = new List<string> { "Categoria-000" };

        watch.Restart();
        await vm.SalvaCommand.ExecuteAsync(null);
        var salvataggio = watch.Elapsed;

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.False(vm.InModifica);
        Assert.True(vm.UsaPaginazione);
        Assert.True(vm.Items.Count <= vm.DimensionePagina);
        Assert.Equal(count + 1, state.Master.Funzionalita.Load().Count);

        // Limiti volutamente larghi: sono guardrail anti-regressione quadratica, non micro-benchmark.
        // Un percorso O(n^2) a 100k non termina con margine pratico; il percorso lineare deve invece restare
        // usabile anche su una macchina Windows non dedicata e con antivirus attivo.
        Assert.True(apertura < TimeSpan.FromSeconds(60), $"Apertura 100k Funzionalita troppo lenta: {apertura}");
        Assert.True(salvataggio < TimeSpan.FromSeconds(120), $"Salvataggio 100k Funzionalita troppo lento: {salvataggio}");
    }
}
