using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Piano 6.4.0 — Propagazione massiva multi-PC: <see cref="ValutazioneRequisitoEcuVeicoloPropagazioneMassivaTests"/>
/// copre già la sequenza deterministica "anteprima calcolata da PC1, poi PC2 scrive a mano, poi PC1 conferma"
/// (simulazione sequenziale della finestra di race). Questo file aggiunge scenari con VERA concorrenza a livello
/// di thread — due postazioni (due <see cref="MasterDataRepository{T}"/> distinti sullo stesso file
/// <c>audit-valutazioni-requisito-componente-veicolo.json</c>) che chiamano <see cref="ValutazioneRequisitoEcuVeicoloService.PropagaMassiva"/>
/// / <see cref="ValutazioneRequisitoEcuVeicoloService.ImpostaManuale"/> quasi in contemporanea, sincronizzate da
/// un <see cref="Barrier"/>. PropagaMassiva NON ha retry automatico (fail-closed sulla singola chiamata: vedi
/// commento sul metodo), quindi il test stesso ritenta — esattamente come dovrebbe fare l'operatore davanti a un
/// messaggio di conflitto — finché la propria azione ha successo o scade il budget di tentativi.
///
/// Fix 6.4.0 (collisione di chiave business permanente): due postazioni che propagano/impostano quasi in
/// contemporanea la STESSA cella NUOVA possono ancora produrre due Upsert Phase D con la stessa chiave business
/// RequisitoId|Componente|Veicolo e UID diverse — ma da questo fix la collisione non è più permanente né blocca la
/// lettura per nessuno: <see cref="PhaseDMasterKeyCollisionTolerance"/> tollera la collisione in lettura per
/// QUESTO catalogo, <see cref="ValutazioneRequisitoEcuVeicoloService.RisolviCella"/> la risolve
/// deterministicamente (vince l'UltimaModifica più recente, a parità l'UID ordinale minore) e la marca come
/// conflitto quando i valori differiscono, e ogni successivo Propaga/ImpostaManuale/PropagaMassiva la ripara nel
/// journal condiviso applicando <see cref="ValutazioneRequisitoEcuVeicoloService.ConsolidaCollisioni"/> prima di
/// salvare. I due test che qui documentavano il bug (<c>[Trait("Category","BugAperto")]</c>) sono stati
/// trasformati in test normali che verificano la correzione.
/// </summary>
public sealed class ValutazioneRequisitoEcuVeicoloPropagazioneMassivaConcorrenzaMultiPcTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PropagaMassivaConcorrenza_" + Guid.NewGuid().ToString("N"));
    private readonly DataPaths _paths;

    public ValutazioneRequisitoEcuVeicoloPropagazioneMassivaConcorrenzaMultiPcTests()
    {
        _paths = new DataPaths(_root);
        _paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Componente EcuDi(string nome, params string[] veicoli) => new() { Nome = nome, VeicoliAssociati = veicoli.ToList() };

    private static TestCase TestDiretto(string id, string componente, string requisitoId, string veicolo, PunteggioConformita punteggio, DateTime data)
    {
        var tc = new TestCase { Id = id, Componente = componente, RequisitoId = { requisitoId }, Veicolo = veicolo };
        tc.Esecuzioni.Add(new Esecuzione { Data = data, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = requisitoId, Valore = punteggio } } });
        return tc;
    }

    private static Requisito ReqAttivo(string id) => new() { Id = id };

    /// <summary>Ritenta come farebbe l'operatore davanti a "un'altra postazione ha scritto nel frattempo,
    /// ripeti l'operazione": ricalcola l'anteprima sui dati freschi ad ogni tentativo (mai su uno snapshot
    /// stantio) e richiama PropagaMassiva. Nessun sleep arbitrario: il retry è immediato, limitato da un
    /// contatore di tentativi, non da un timeout basato sul tempo. Dopo il fix 6.4.0 la collisione di chiave
    /// business (PhaseDMasterBusinessKeyConflictException) non è più permanente per questo catalogo: un
    /// retry successivo la trova già riparata (dal proprio Load/ConsolidaCollisioni o da quello della
    /// postazione concorrente) e converge normalmente, esattamente come un comune conflitto di revisione.</summary>
    private static RisultatoPropagazioneMassiva PropagaConRetryOperatore(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo, string veicolo, IReadOnlyList<Componente> componente,
        IReadOnlyList<Requisito> requisiti, IReadOnlyList<TestCase> testCases, string nota, string operatore,
        out string? errore, int maxTentativi = 50)
    {
        errore = null;
        for (var tentativo = 0; tentativo < maxTentativi; tentativo++)
        {
            try
            {
                var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
                    veicolo, componente, requisiti, testCases, repo.Load());
                if (anteprima.DaPropagare.Count == 0) { errore = null; return new RisultatoPropagazioneMassiva(Array.Empty<VocePropagazioneMassiva>(), anteprima.Conflitti, Array.Empty<(string, string)>()); }

                var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
                    repo, veicolo, componente, requisiti, testCases, null, anteprima.DaPropagare, nota, operatore, out errore);
                if (errore == null) return risultato;
                // Conflitto di scrittura concorrente (fail-closed, nessun dato perso): ritenta sui dati freschi.
            }
            catch (PhaseDMasterBusinessKeyConflictException ex)
            {
                // Collisione di chiave business Phase D su questo catalogo (tollerata in lettura, riparata al
                // primo Save successivo — vedi PhaseDMasterKeyCollisionTolerance): equivalente a un normale
                // conflitto di scrittura concorrente, il chiamante ricarica e ritenta.
                errore = ex.Message;
            }
        }
        Assert.Fail($"Retry esaurito senza successo dopo {maxTentativi} tentativi. Ultimo errore: {errore ?? "(nessuno)"}");
        return null!; // irraggiungibile
    }

    [Fact]
    public async Task DuePostazioni_PropaganoCelleDiverseQuasiInContemporanea_NessunaPersaEntrambeScritte()
    {
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var requisiti = new[] { ReqAttivo("R1"), ReqAttivo("R2") };
        var srcR1 = TestDiretto("T-R1", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var srcR2 = TestDiretto("T-R2", "BCM", "R2", "My28", PunteggioConformita.NonConforme, new DateTime(2026, 1, 1));
        var testCases = new[] { srcR1, srcR2 };

        var repoA = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var repoB = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);

        var barrier = new Barrier(2);
        RisultatoPropagazioneMassiva? risultatoA = null;
        RisultatoPropagazioneMassiva? risultatoB = null;

        var taskA = Task.Run(() =>
        {
            barrier.SignalAndWait();
            // Solo R1 nel perimetro di A: due postazioni con vista/filtro diverso della stessa Audit.
            risultatoA = PropagaConRetryOperatore(repoA, "My24", new[] { componente }, new[] { ReqAttivo("R1") },
                testCases, "confermo R1 da PC-A", "PC-A", out _);
        });
        var taskB = Task.Run(() =>
        {
            barrier.SignalAndWait();
            risultatoB = PropagaConRetryOperatore(repoB, "My24", new[] { componente }, new[] { ReqAttivo("R2") },
                testCases, "confermo R2 da PC-B", "PC-B", out _);
        });
        await Task.WhenAll(taskA, taskB);

        Assert.NotNull(risultatoA);
        Assert.NotNull(risultatoB);
        Assert.Single(risultatoA!.Propagate);
        Assert.Single(risultatoB!.Propagate);

        // Convergenza: dopo un refresh, ENTRAMBE le postazioni vedono lo stesso stato consolidato con le due
        // celle scritte, nessuna perduta e nessuna sovrascrittura silenziosa dell'altra.
        var statoA = repoA.Load();
        var statoB = repoB.Load();
        Assert.Equal(2, statoA.Count);
        Assert.Equal(2, statoB.Count);
        Assert.Equal("R1", statoA.Single(v => v.Componente == "BCM" && v.Veicolo == "My24" && v.RequisitoId == "R1").RequisitoId);
        Assert.Contains(statoA, v => v.RequisitoId == "R2");
        Assert.Equal(statoA.Select(v => (v.Componente, v.Veicolo, v.RequisitoId, v.Punteggio)).OrderBy(x => x.RequisitoId).ToList(),
                     statoB.Select(v => (v.Componente, v.Veicolo, v.RequisitoId, v.Punteggio)).OrderBy(x => x.RequisitoId).ToList());
    }

    /// <summary>
    /// Fix 6.4.0 (ex BugAperto). Due postazioni che propagano in massa la STESSA cella "!" (nessuna riga
    /// preesistente, quindi entrambe creano una riga NUOVA con UID casuale) con la stessa sorgente/punteggio:
    /// una collisione di chiave business Phase D può ancora verificarsi (PropagaMassiva fa sempre RemoveAll+Add,
    /// mai un update in-place), ma non è più permanente né blocca la lettura per nessuno: il retry converge
    /// SEMPRE entro il budget di tentativi (nessuna eccezione esce dal retry di nessuna delle due), e una terza
    /// postazione (Load() con un repository nuovo) legge SUBITO il catalogo senza eccezioni — anche mentre la
    /// collisione non è ancora stata riparata (righe duplicate ancora presenti, tollerate in lettura). La
    /// riparazione a UNA sola riga per cella avviene invece al PRIMO Save successivo di QUALUNQUE postazione
    /// (vedi PhaseDMasterKeyCollisionTolerance/ConsolidaCollisioni — non è automatica sulla sola lettura), qui
    /// dimostrata con una scrittura indipendente su un'altra cella, stesso schema di
    /// PrimaScritturaSuccessivaAllaCollisione_RiparaIlJournalCondiviso.
    /// </summary>
    [Fact]
    public async Task DuePostazioni_PropaganoLaStessaCellaNuovaQuasiInContemporanea_CollisioneRiparataNonBloccaNessuno()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var requisiti = new[] { ReqAttivo("R1") };
        var sorgente = TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var testCases = new[] { sorgente };

        var repoA = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var repoB = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);

        var barrier = new Barrier(2);
        Exception? eccezioneA = null, eccezioneB = null;

        var taskA = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { PropagaConRetryOperatore(repoA, "My24", new[] { componente }, requisiti, testCases, "confermo da PC-A", "PC-A", out _); }
            catch (Exception ex) { eccezioneA = ex; }
        });
        var taskB = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { PropagaConRetryOperatore(repoB, "My24", new[] { componente }, requisiti, testCases, "confermo da PC-B", "PC-B", out _); }
            catch (Exception ex) { eccezioneB = ex; }
        });
        await Task.WhenAll(taskA, taskB);

        // Comportamento CORRETTO dopo il fix: nessuna eccezione esce dal retry di nessuna delle due postazioni
        // (una collisione di chiave business su questo catalogo è sempre riparabile con retry, mai permanente).
        Assert.Null(eccezioneA);
        Assert.Null(eccezioneB);

        // Prova decisiva e robusta della correzione (indipendente da quale dei due thread abbia "vinto" per
        // primo): un Load() con un repository DEL TUTTO NUOVO (una terza "postazione" che riapre semplicemente
        // la pagina Audit in un momento successivo) NON lancia più, subito — anche se la collisione fosse
        // ancora presente (righe duplicate tollerate in lettura, vedi PhaseDMasterKeyCollisionTolerance).
        var repoTerzaPostazione = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var exLoad = Record.Exception(() => repoTerzaPostazione.Load());
        Assert.Null(exLoad);

        // La riparazione a una sola riga per cella non è automatica sulla sola lettura (vedi doc di classe):
        // avviene al PRIMO Save successivo di QUALUNQUE postazione. Una scrittura indipendente su un'altra
        // cella (My26, mai toccata dalla race sopra) la ripara come effetto collaterale — stesso schema di
        // PrimaScritturaSuccessivaAllaCollisione_RiparaIlJournalCondiviso.
        var ok = ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repoTerzaPostazione, "R1", "BCM", new[] { "My26" }, PunteggioConformita.Conforme,
            "valutazione indipendente che ripara il journal", "PC-C", null, out var erroreRiparazione);
        Assert.True(ok, erroreRiparazione);

        var statoFinale = repoTerzaPostazione.Load();
        var righeCella = statoFinale.Where(v => v.Componente == "BCM" && v.Veicolo == "My24" && v.RequisitoId == "R1").ToList();
        Assert.Single(righeCella);
        Assert.Equal(PunteggioConformita.Conforme, righeCella[0].Punteggio);
    }

    /// <summary>
    /// Fix 6.4.0 (ex BugAperto) — stessa causa radice del test precedente. Una postazione propaga in massa
    /// mentre l'altra imposta un valore Manuale sulla STESSA cella nuova, quasi in contemporanea: entrambe
    /// creano — quando la cella non ha ancora nessuna riga — un ValutazioneRequisitoEcuVeicolo NUOVO con UID
    /// casuale, possibile collisione di chiave business Phase D. Dopo il fix nessuna delle due resta bloccata e
    /// il catalogo resta SUBITO leggibile per nessun lettore successivo (nessuna eccezione); la convergenza a
    /// una sola riga per cella avviene invece al primo Save successivo di una qualunque postazione (vedi
    /// PhaseDMasterKeyCollisionTolerance/ConsolidaCollisioni), qui dimostrata esplicitamente.
    /// </summary>
    [Fact]
    public async Task UnaPostazionePropagaMentreLAltraImpostaManualeSullaStessaCellaNuova_QuasiInContemporanea_CollisioneRiparataNonBloccaNessuno()
    {
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var requisiti = new[] { ReqAttivo("R1") };
        var sorgente = TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var testCases = new[] { sorgente };

        var repoPropaga = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var repoManuale = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);

        var barrier = new Barrier(2);
        Exception? eccezionePropaga = null;
        var manualeRiuscito = false;

        var taskPropaga = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                PropagaConRetryOperatore(repoPropaga, "My24", new[] { componente }, requisiti, testCases,
                    "propagazione massiva da PC-A", "PC-A", out _);
            }
            catch (Exception ex) { eccezionePropaga = ex; }
        });
        Exception? eccezioneManualeInattesa = null;
        var taskManuale = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var tentativo = 0; tentativo < 50 && !manualeRiuscito; tentativo++)
            {
                try
                {
                    manualeRiuscito = ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
                        repoManuale, "R1", "BCM", new[] { "My24" }, PunteggioConformita.NonApplicabile,
                        "override manuale da PC-B, non applicabile su questo banco", "PC-B", null, out _);
                    if (!manualeRiuscito) return; // ProvaAggiorna ha già gestito il conflitto internamente: nessuna eccezione, semplicemente non ha scritto.
                }
                catch (PhaseDMasterBusinessKeyConflictException) { /* collisione riparabile: ritenta */ }
                catch (Exception ex) { eccezioneManualeInattesa = ex; return; }
            }
        });
        await Task.WhenAll(taskPropaga, taskManuale);

        // Comportamento CORRETTO dopo il fix: nessuna eccezione esce dal retry di PropagaMassiva, e il ramo
        // Manuale non incontra mai nulla di diverso da un normale esito gestito (successo, o nessuna scrittura
        // perché l'altra parte ha già vinto la cella — mai un'eccezione tecnica non gestita).
        Assert.Null(eccezionePropaga);
        Assert.Null(eccezioneManualeInattesa);

        var repoTerzaPostazione = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var exLoad = Record.Exception(() => repoTerzaPostazione.Load());
        Assert.Null(exLoad); // il catalogo non resta mai illeggibile, per nessun lettore, subito dopo la race.

        // La riparazione a una sola riga per cella avviene al PRIMO Save successivo (non è automatica sulla
        // sola lettura, vedi doc di classe) — qui una scrittura indipendente su My28, mai toccata dalla race.
        var okRiparazione = ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repoTerzaPostazione, "R1", "BCM", new[] { "My28" }, PunteggioConformita.Conforme,
            "valutazione indipendente che ripara il journal", "PC-C", null, out var erroreRiparazione);
        Assert.True(okRiparazione, erroreRiparazione);

        var righeCella = repoTerzaPostazione.Load().Where(v => v.Componente == "BCM" && v.Veicolo == "My24" && v.RequisitoId == "R1").ToList();
        Assert.Single(righeCella); // mai due righe attive sulla stessa cella dopo la riparazione.
    }

    /// <summary>Tre postazioni (tre <see cref="MasterDataRepository{T}"/> nuovi, mai usati per scrivere) che
    /// leggono lo STESSO journal condiviso contenente una collisione di chiave business non ancora riparata
    /// (prodotta con la stessa race delle due prove sopra): devono vedere EATTAMENTE lo stesso risultato
    /// consolidato — stesso punteggio/origine vincenti, stesso flag di conflitto — perché la scelta della riga
    /// vincente (UltimaModifica più recente, a parità Uid) è deterministica sui dati del journal, non
    /// sull'ordine di lettura di ciascuna postazione.</summary>
    [Fact]
    public async Task TrePostazioni_LegonoLoStessoJournalConCollisione_VedonoLoStessoRisultatoConsolidato()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var requisiti = new[] { ReqAttivo("R1") };
        var sorgente = TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var testCases = new[] { sorgente };

        var repoA = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var repoB = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var barrier = new Barrier(2);
        var taskA = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { PropagaConRetryOperatore(repoA, "My24", new[] { componente }, requisiti, testCases, "da PC-A", "PC-A", out _); }
            catch (PhaseDMasterBusinessKeyConflictException) { }
        });
        var taskB = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { PropagaConRetryOperatore(repoB, "My24", new[] { componente }, requisiti, testCases, "da PC-B", "PC-B", out _); }
            catch (PhaseDMasterBusinessKeyConflictException) { }
        });
        await Task.WhenAll(taskA, taskB);

        var repo1 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var repo2 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var repo3 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);

        var righe1 = repo1.Load();
        var righe2 = repo2.Load();
        var righe3 = repo3.Load();

        var risoluzione1 = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", testCases, righe1);
        var risoluzione2 = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", testCases, righe2);
        var risoluzione3 = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", testCases, righe3);

        Assert.Equal(risoluzione1.Punteggio, risoluzione2.Punteggio);
        Assert.Equal(risoluzione1.Punteggio, risoluzione3.Punteggio);
        Assert.Equal(risoluzione1.Origine, risoluzione2.Origine);
        Assert.Equal(risoluzione1.Origine, risoluzione3.Origine);
        Assert.Equal(risoluzione1.Data, risoluzione2.Data);
        Assert.Equal(risoluzione1.Data, risoluzione3.Data);
        Assert.Equal(risoluzione1.ConflittoCollisioneCella, risoluzione2.ConflittoCollisioneCella);
        Assert.Equal(risoluzione1.ConflittoCollisioneCella, risoluzione3.ConflittoCollisioneCella);
    }

    /// <summary>Due righe collise sulla stessa cella con GLI STESSI valori significativi (stesso Punteggio,
    /// Origine, sorgente — es. entrambe le postazioni hanno propagato la stessa identica sorgente) NON sono un
    /// conflitto da mostrare: consolidamento silenzioso, ConflittoCollisioneCella deve restare false.</summary>
    [Fact]
    public void CollisioneConValoriUguali_NonEConflittoVisibile()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var comune = new DateTime(2026, 3, 1, 10, 0, 0);
        var righe = new List<ValutazioneRequisitoEcuVeicolo>
        {
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
                    Origine = OrigineValutazioneRequisito.Propagata, SorgenteVeicolo = "My26", SorgenteTestId = "T-SRC",
                    SorgentePunteggio = PunteggioConformita.Conforme, UltimaModifica = comune, Operatore = "PC-A" },
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
                    Origine = OrigineValutazioneRequisito.Propagata, SorgenteVeicolo = "My26", SorgenteTestId = "T-SRC",
                    SorgentePunteggio = PunteggioConformita.Conforme, UltimaModifica = comune.AddSeconds(1), Operatore = "PC-B" },
        };

        var risoluzione = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", Array.Empty<TestCase>(), righe);

        Assert.False(risoluzione.ConflittoCollisioneCella);
        Assert.Equal(PunteggioConformita.Conforme, risoluzione.Punteggio);
    }

    /// <summary>Due righe collise sulla stessa cella con valori DIVERSI (es. una postazione ha impostato
    /// Manuale Conforme, l'altra Manuale NonConforme quasi in contemporanea): il valore mostrato resta
    /// deterministico (vince l'UltimaModifica più recente), ma ConflittoCollisioneCella deve essere true — la
    /// pagina Audit deve poter segnalare che c'è un conflitto da rivedere, non un valore ordinario.</summary>
    [Fact]
    public void CollisioneConValoriDiversi_EConflittoVisibileMaValoreDeterministico()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var righe = new List<ValutazioneRequisitoEcuVeicolo>
        {
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
                    Origine = OrigineValutazioneRequisito.Manuale, UltimaModifica = new DateTime(2026, 3, 1, 10, 0, 0), Operatore = "PC-A" },
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme,
                    Origine = OrigineValutazioneRequisito.Manuale, UltimaModifica = new DateTime(2026, 3, 1, 10, 0, 5), Operatore = "PC-B" },
        };

        var risoluzione = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", Array.Empty<TestCase>(), righe);

        Assert.True(risoluzione.ConflittoCollisioneCella);
        // Deterministico: vince la riga con UltimaModifica più recente (PC-B, NonConforme).
        Assert.Equal(PunteggioConformita.NonConforme, risoluzione.Punteggio);
    }

    /// <summary>Fix 6.4.0 — perché la riparazione concorrente (due postazioni che riparano indipendentemente
    /// la STESSA collisione preesistente, vedi RiparazioneConcorrenteDiDuePostazioni_EIdempotenteNessunaDuplicazione)
    /// non trasformi una collisione di CHIAVE BUSINESS (tollerata) in una collisione di REVISIONE sulla stessa
    /// UID (PhaseDMasterConflictException, "teste concorrenti non risolte", che renderebbe l'intero catalogo
    /// illeggibile per chiunque — mai tollerata, per nessun catalogo), la mutazione che ConsolidaCollisioni
    /// applica alla riga vincente deve essere interamente derivata dai DATI delle righe, non da DateTime.Now né
    /// dall'operatore chiamante: due postazioni diverse che riparano la stessa collisione devono produrre
    /// esattamente lo stesso payload byte-per-byte.</summary>
    [Fact]
    public void ConsolidaCollisioni_ChiamatoDaDuePostazioniSullaStessaCollisionePreesistente_ProduceLoStessoPayload()
    {
        var uidVincitrice = Guid.NewGuid();
        var uidPersa = Guid.NewGuid();
        var comune = new DateTime(2026, 3, 1, 10, 0, 0);

        List<ValutazioneRequisitoEcuVeicolo> Snapshot() => new()
        {
            new() { Uid = uidVincitrice, RequisitoId = "R1", Componente = "BCM", Veicolo = "My24",
                    Punteggio = PunteggioConformita.Conforme, Origine = OrigineValutazioneRequisito.Manuale,
                    Operatore = "PC-A", UltimaModifica = comune.AddSeconds(5) },
            new() { Uid = uidPersa, RequisitoId = "R1", Componente = "BCM", Veicolo = "My24",
                    Punteggio = PunteggioConformita.NonConforme, Origine = OrigineValutazioneRequisito.Manuale,
                    Operatore = "PC-B", UltimaModifica = comune },
        };

        // Due postazioni diverse ("PC-1" e "PC-2") ripristinano indipendentemente la STESSA collisione
        // preesistente, ognuna con il proprio snapshot (mai lo stesso oggetto in memoria — simula due processi).
        var copiaRipartaDaPC1 = Snapshot();
        var copiaRipartaDaPC2 = Snapshot();
        ValutazioneRequisitoEcuVeicoloService.ConsolidaCollisioni(copiaRipartaDaPC1, "PC-1");
        ValutazioneRequisitoEcuVeicoloService.ConsolidaCollisioni(copiaRipartaDaPC2, "PC-2");

        var vincitriceDaPC1 = Assert.Single(copiaRipartaDaPC1);
        var vincitriceDaPC2 = Assert.Single(copiaRipartaDaPC2);
        // Stesso payload canonico: e' esattamente cio' che serve perche' Phase D tratti i due commit
        // concorrenti come "stesso valore" (nessuna testa in conflitto), anche se arrivano da due mutation-id
        // diverse — vedi PhaseDMasterMaterializer.ResolveHeads (sameValue) e il commento di ConsolidaCollisioni.
        Assert.Equal(JsonSerializer.Serialize(vincitriceDaPC1), JsonSerializer.Serialize(vincitriceDaPC2));
    }

    /// <summary>Fix 6.4.0 (R2) — dopo che ConsolidaCollisioni ha riparato una collisione con valori DISCORDANTI
    /// (una sola riga rimasta), il conflitto deve restare visibile in Audit finché un operatore non reimposta
    /// esplicitamente la cella: RisolviCella deve continuare a segnalare ConflittoCollisioneCella=true anche
    /// quando la cella ha già una sola riga, riconoscendo il marcatore lasciato nel Changelog della riparazione
    /// (nessun campo nuovo persistito). Un successivo ImpostaManuale sulla STESSA cella sostituisce la riga
    /// (RemoveAll+Add, Changelog azzerato) e chiude il conflitto.</summary>
    [Fact]
    public void ConsolidaCollisioni_ConValoriDiscordanti_ConflittoRestaVisibileFinoARipristinoCella()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var righe = new List<ValutazioneRequisitoEcuVeicolo>
        {
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
                    Origine = OrigineValutazioneRequisito.Manuale, Operatore = "PC-A",
                    UltimaModifica = new DateTime(2026, 3, 1, 10, 0, 5) },
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme,
                    Origine = OrigineValutazioneRequisito.Manuale, Operatore = "PC-B",
                    UltimaModifica = new DateTime(2026, 3, 1, 10, 0, 0) },
        };

        var rimosse = ValutazioneRequisitoEcuVeicoloService.ConsolidaCollisioni(righe, "PC-C");
        Assert.Equal(1, rimosse);
        var vincitrice = Assert.Single(righe);
        Assert.Equal(PunteggioConformita.Conforme, vincitrice.Punteggio); // UltimaModifica più recente (PC-A)

        // Con una sola riga rimasta, RisolviCella deve comunque segnalare il conflitto (marcatore nel
        // Changelog), non trattarlo come un valore ordinario.
        var risoluzioneDopoRiparazione = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", Array.Empty<TestCase>(), righe);
        Assert.True(risoluzioneDopoRiparazione.ConflittoCollisioneCella);
        Assert.Equal(PunteggioConformita.Conforme, risoluzioneDopoRiparazione.Punteggio);

        // Una seconda riparazione (nessuna collisione residua) è un no-op: il conflitto resta visibile finché
        // l'operatore non reimposta la cella esplicitamente.
        Assert.Equal(0, ValutazioneRequisitoEcuVeicoloService.ConsolidaCollisioni(righe, "PC-C"));
        var risoluzioneDopoSecondaRiparazione = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", Array.Empty<TestCase>(), righe);
        Assert.True(risoluzioneDopoSecondaRiparazione.ConflittoCollisioneCella);

        // ImpostaManuale sulla stessa cella sostituisce la riga (RemoveAll+Add) e chiude il conflitto. Persiste
        // prima lo stato di partenza (la riga vincente, già consolidata e ancora in conflitto) sul catalogo
        // condiviso, come farebbe un vero Save precedente.
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        repo.Save(righe);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.Conforme,
            "cella rivista e reimpostata dall'operatore", "PC-D", null, out var errore));
        Assert.Null(errore);
        var righeDopoReset = repo.Load();
        var risoluzioneDopoReset = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", Array.Empty<TestCase>(), righeDopoReset);
        Assert.False(risoluzioneDopoReset.ConflittoCollisioneCella);
    }

    /// <summary>Riparazione con il primo salvataggio: dopo che due postazioni hanno prodotto una collisione di
    /// chiave business su una cella (race riprodotta come sopra), la PRIMA chiamata successiva a ImpostaManuale
    /// su una cella DIVERSA (qualunque scrittura di questo servizio, non necessariamente sulla cella collisa)
    /// deve riparare il journal condiviso: dopo quella chiamata, ogni lettore vede esattamente una riga per la
    /// cella originariamente collisa, e la riga vincente porta traccia del consolidamento in Nota/Changelog.</summary>
    [Fact]
    public async Task PrimaScritturaSuccessivaAllaCollisione_RiparaIlJournalCondiviso()
    {
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var requisiti = new[] { ReqAttivo("R1") };
        var sorgente = TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var testCases = new[] { sorgente };

        var repoA = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var repoB = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var barrier = new Barrier(2);
        var taskA = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { PropagaConRetryOperatore(repoA, "My24", new[] { componente }, requisiti, testCases, "da PC-A", "PC-A", out _); }
            catch (PhaseDMasterBusinessKeyConflictException) { }
        });
        var taskB = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { PropagaConRetryOperatore(repoB, "My24", new[] { componente }, requisiti, testCases, "da PC-B", "PC-B", out _); }
            catch (PhaseDMasterBusinessKeyConflictException) { }
        });
        await Task.WhenAll(taskA, taskB);

        // A questo punto potrebbe già essere riparato (se la corsa non ha prodotto collisione questa volta) o
        // no: entrambi i casi devono portare allo stesso esito dopo la scrittura successiva.
        var repoTerza = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);

        // Una scrittura NORMALE su una cella diversa (My28) deve riparare, come effetto collaterale, l'eventuale
        // collisione su My24 lasciata dalla race sopra.
        var ok = ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repoTerza, "R1", "BCM", new[] { "My28" }, PunteggioConformita.Conforme,
            "valutazione indipendente che ripara il journal", "PC-C", null, out var errore);
        Assert.True(ok, errore);

        var repoQuarta = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var exLoad = Record.Exception(() => repoQuarta.Load());
        Assert.Null(exLoad);

        var righeCella = repoQuarta.Load().Where(v => v.Componente == "BCM" && v.Veicolo == "My24" && v.RequisitoId == "R1").ToList();
        Assert.Single(righeCella);
    }

    /// <summary>Riparazione concorrente idempotente: due postazioni chiamano entrambe ImpostaManuale sulla
    /// STESSA cella nuova (My28, con valori diversi — la stessa race di
    /// DuePostazioni_PropaganoLaStessaCellaNuovaQuasiInContemporanea_CollisioneRiparataNonBloccaNessuno, qui
    /// combinata con una collisione PREESISTENTE prodotta prima su una cella diversa, My24, che nessuna delle
    /// due tocca direttamente) quasi in contemporanea. Entrambe provano a riparare la collisione preesistente
    /// come effetto collaterale (ConsolidaCollisioni prima del proprio Save): nessuna eccezione tecnica non
    /// gestita deve uscire da nessuna delle due (una collisione di chiave business Phase D su questo catalogo è
    /// sempre riparabile con retry, mai permanente), e il catalogo finale deve restare leggibile per chiunque —
    /// la convergenza a una riga per cella (incluso My28, che può a sua volta restare in collisione tollerata
    /// finché un Save successivo non la ripara) è verificata esplicitamente più sotto con una scrittura di
    /// riparazione dedicata, stesso schema di PrimaScritturaSuccessivaAllaCollisione_RiparaIlJournalCondiviso.</summary>
    [Fact]
    public async Task RiparazioneConcorrenteDiDuePostazioni_EIdempotenteNessunaDuplicazione()
    {
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var requisiti = new[] { ReqAttivo("R1") };
        var sorgente = TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var testCases = new[] { sorgente };

        // Produce la collisione preesistente su My24 con la stessa race delle prove sopra.
        var repoRaceA = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var repoRaceB = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var barrierRace = new Barrier(2);
        var taskRaceA = Task.Run(() =>
        {
            barrierRace.SignalAndWait();
            try { PropagaConRetryOperatore(repoRaceA, "My24", new[] { componente }, requisiti, testCases, "da PC-A", "PC-A", out _); }
            catch (PhaseDMasterBusinessKeyConflictException) { }
        });
        var taskRaceB = Task.Run(() =>
        {
            barrierRace.SignalAndWait();
            try { PropagaConRetryOperatore(repoRaceB, "My24", new[] { componente }, requisiti, testCases, "da PC-B", "PC-B", out _); }
            catch (PhaseDMasterBusinessKeyConflictException) { }
        });
        await Task.WhenAll(taskRaceA, taskRaceB);

        // Due postazioni riparano concorrentemente scrivendo su celle diverse (nessuna delle due tocca My24).
        var repoRipara1 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var repoRipara2 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var barrierRipara = new Barrier(2);
        Exception? erroreRipara1 = null, erroreRipara2 = null;

        var taskRipara1 = Task.Run(() =>
        {
            barrierRipara.SignalAndWait();
            for (var t = 0; t < 20; t++)
            {
                var ok = ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
                    repoRipara1, "R1", "BCM", new[] { "My28" }, PunteggioConformita.Conforme, "riparo1", "PC-1", null, out var err);
                if (ok) { erroreRipara1 = null; return; }
                erroreRipara1 = err == null ? null : new InvalidOperationException(err);
            }
        });
        var taskRipara2 = Task.Run(() =>
        {
            barrierRipara.SignalAndWait();
            for (var t = 0; t < 20; t++)
            {
                var ok = ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
                    repoRipara2, "R1", "BCM", new[] { "My28" }, PunteggioConformita.NonConforme, "riparo2", "PC-2", null, out var err);
                if (ok) { erroreRipara2 = null; return; }
                erroreRipara2 = err == null ? null : new InvalidOperationException(err);
            }
        });
        await Task.WhenAll(taskRipara1, taskRipara2);

        // Entrambe le postazioni convergono entro il budget di retry (nessun blocco permanente): l'ultimo
        // errore osservato da ciascuna, se presente, è sempre un normale conflitto di scrittura concorrente
        // gestito da ProvaAggiorna — mai un'eccezione tecnica non gestita che avrebbe fatto fallire il Task.
        Assert.Null(erroreRipara1);
        Assert.Null(erroreRipara2);

        // Nessuna eccezione tecnica non gestita: entrambe le postazioni scrivono senza mai incontrare un
        // blocco permanente — ma su My28 (cella NUOVA per entrambe, nessuna riga preesistente) una vera
        // sovrapposizione può produrre la STESSA collisione di chiave business del resto della classe (entrambe
        // creano una riga nuova con UID diversa prima di vedere l'altra): tollerata in lettura, ma non ancora
        // riparata finché non arriva un Save successivo (vedi doc di classe).
        var repoFinale = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(_paths.ValutazioniRequisitoEcuVeicoloFile);
        var exLoad = Record.Exception(() => repoFinale.Load());
        Assert.Null(exLoad);

        // La riparazione a una sola riga per cella avviene al PRIMO Save successivo di qualunque postazione
        // (non è automatica sulla sola lettura) — qui una scrittura indipendente su una cella mai toccata sopra.
        var okRiparazioneFinale = ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repoFinale, "R1", "BCM", new[] { "My26" }, PunteggioConformita.Conforme,
            "valutazione indipendente che ripara il journal", "PC-VERIFICA", null, out var erroreRiparazioneFinale);
        Assert.True(okRiparazioneFinale, erroreRiparazioneFinale);

        var statoFinale = repoFinale.Load();
        // Nessuna cella con più di una riga attiva: la riparazione ha rimosso ogni duplicato residuo.
        var gruppi = statoFinale.GroupBy(v => (v.RequisitoId, v.Componente, v.Veicolo)).ToList();
        Assert.All(gruppi, g => Assert.Single(g));

        // Idempotenza (requisito R2): una riparazione SUCCESSIVA sullo stesso stato, già consolidato, non deve
        // rimuovere/alterare nulla — ConsolidaCollisioni è un no-op quando non c'è più nessuna collisione.
        Assert.Equal(0, ValutazioneRequisitoEcuVeicoloService.ConsolidaCollisioni(statoFinale, "PC-VERIFICA"));
    }

    /// <summary>6.4.2: due postazioni che creano insieme la stessa voce di un catalogo NON tollerato (qui Componente,
    /// chiave business "Nome") non devono più rendere il catalogo illeggibile. Prima della 6.4.2 la collisione era
    /// fail-closed per ogni lettore, senza recupero dall'interfaccia; ora il read model locale mostra una sola voce
    /// (vince l'Uid ordinale minore, PhaseDMasterMaterializer.OscuraDuplicatiDiChiave) e il journal resta intatto.
    /// Verificato su tutti i tentativi della race, con o senza sovrapposizione reale; il caso deterministico con
    /// due processi reali è MultiPcSyncScenarios.Scenario7b.</summary>
    [Fact]
    public async Task AltroCatalogoPhaseD_Componente_CollisioneDiChiaveBusiness_CatalogoRestaLeggibileConUnaSolaVoce()
    {
        for (var tentativo = 0; tentativo < 30; tentativo++)
        {
            var repoA = new MasterDataRepository<Componente>(_paths.EcuFile);
            var repoB = new MasterDataRepository<Componente>(_paths.EcuFile);
            var nomeUnico = $"BCM-COLLISIONE-{tentativo}";
            var barrier = new Barrier(2);

            // Una delle due Save può ancora rilevare la collisione alla verifica post-commit (esito legittimo
            // per chi scrive in quell'istante): il requisito riguarda lo stato che vedono i lettori dopo.
            var taskA = Task.Run(() =>
            {
                barrier.SignalAndWait();
                try { repoA.Save(new List<Componente> { new() { Nome = nomeUnico } }); }
                catch (PhaseDMasterBusinessKeyConflictException) { }
                catch (IOException) { } // stesso processo: il journal del device è occupato dall'altra Save
            });
            var taskB = Task.Run(() =>
            {
                barrier.SignalAndWait();
                try { repoB.Save(new List<Componente> { new() { Nome = nomeUnico } }); }
                catch (PhaseDMasterBusinessKeyConflictException) { }
                catch (IOException) { }
            });
            await Task.WhenAll(taskA, taskB);

            // Qualunque sia l'esito della race per chi scrive, i lettori vedono una sola voce.
            var righe = new MasterDataRepository<Componente>(_paths.EcuFile).Load();
            Assert.Equal(1, righe.Count(e => e.Nome.Equals(nomeUnico, StringComparison.OrdinalIgnoreCase)));
        }
    }
}