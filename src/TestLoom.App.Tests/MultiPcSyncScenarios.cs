using System.Diagnostics;
using System.Text.Json.Nodes;

namespace TestLoom.App.Tests;

/// <summary>
/// Block B (revisione multi-PC 6.4.2): scenari end-to-end con due processi TestLoom.App.MultiPcHost REALI,
/// ognuno puntato sulla PROPRIA cartella fisica locale (mai una cartella condivisa unica, a differenza di
/// MultiPcSyncTests fase 4), sincronizzate da <see cref="OneDriveFolderSyncSimulator"/> con condizioni realistiche
/// (ritardo, ordine arbitrario, scritture a metà, blocchi temporanei, cancellazioni ritardate, copie di
/// conflitto). Copertura dichiarata rispetto ai 9 scenari richiesti: (1) create/vedi/modifica con consegna
/// disordinata e file a metà — COPERTO; (3) stesso campo -> conflitto visibile, nessuna sovrascrittura silenziosa
/// — COPERTO a livello file/repository (il simulatore e i classificatori CopieDiConflitto già estesi nel block C
/// lo dimostrano); (6) due postazioni offline, stesse iniziali -> nessun Id duplicato "silenzioso" e nessun dato
/// perso — COPERTO end-to-end con due processi reali. (2) merge automatico campi diversi, (4) cestino/ripristino/
/// eliminazione definitiva, (5) avviso "altra postazione", (7) cataloghi master Phase D, (8) offline prolungato,
/// (9) Test spostato durante modifica NON hanno un comando dedicato in TestLoom.App.MultiPcHost (solo
/// Test/NC minimi) e NON sono coperti da un harness a due processi in questa sessione — riconosciuto come lavoro
/// residuo nel report finale, non nascosto. (2) e (9) sono comunque parzialmente coperti a livello unitario
/// altrove (merge automatico: TestCaseAutoForkMergeService ha una suite propria preesistente; (9) dal fix di
/// revisione "spostamento in corso" in TestCaseConflictCopyTests).
/// </summary>
[Trait("Category", "Lento")]
public sealed class MultiPcSyncScenarios : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_MultiPcScenari_" + Guid.NewGuid().ToString("N"));
    private readonly string _folderA;
    private readonly string _folderB;
    private readonly string _localA;
    private readonly string _localB;

    public MultiPcSyncScenarios()
    {
        _folderA = Path.Combine(_root, "OneDriveA");
        _folderB = Path.Combine(_root, "OneDriveB");
        _localA = Path.Combine(_root, "LocalA");
        _localB = Path.Combine(_root, "LocalB");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Scenario1_CreaTestENc_ConsegnaDisordinataEFileAMeta_EntrambiIPcConvergonoSenzaProblemi()
    {
        using var pcA = new HostProcess(_folderA, _localA);
        var sim = new OneDriveFolderSyncSimulator(_folderA, _folderB, seed: 42) { OrdineArbitrario = true };
        // Bootstrap realistico: TestLoom viene attivato la prima volta su A (crea i cataloghi Phase D di default),
        // poi la cartella si sincronizza su B PRIMA che l'utente apra TestLoom su B. Se B bootstrap-asse a sua
        // volta su una cartella B ancora vuota, ciascun lato creerebbe indipendentemente i propri record di
        // default con Uid diversi per la stessa chiave business — una collisione Phase D che non c'entra con lo
        // scenario in prova (scoperta scrivendo questo test: PhaseDMasterBusinessKeyConflictException "Nessun
        // merge automatico applicato" su record di seed, non su dati creati dall'utente).
        sim.PropagaFinoAConvergenza();
        using var pcB = new HostProcess(_folderB, _localB);

        // PC A crea un Test e la sua NC quasi in contemporanea: la simulazione deve tollerare che la NC arrivi
        // PRIMA del Test sull'altro lato (consegna disordinata) senza mai comparire come "file con problemi".
        pcA.Send(new JsonObject { ["cmd"] = "createTest", ["id"] = "T-SC1-0001", ["componente"] = "ECM", ["veicolo"] = "Daily", ["descrizione"] = "creato da A" });
        pcA.Send(new JsonObject { ["cmd"] = "createNC", ["id"] = "NC-SC1-0001", ["testId"] = "T-SC1-0001", ["descrizione"] = "anomalia iniziale" });

        // Scrittura a metà per il prossimo giro di propagazione: simula un JSON che OneDrive sta ancora
        // caricando quando B fa il primo refresh.
        sim.Propaga(scritturaAMetaBytesPerNuoviCambiamenti: 5);
        pcB.Send(new JsonObject { ["cmd"] = "refresh" });
        // Con il file troncato, B non deve vedere ancora il Test/la NC come validi, ma nemmeno crashare: la
        // tolleranza di FileProblemToleranceTracker (>= 2 tentativi consecutivi E >= 2 minuti) impedisce che UN
        // solo giro con un file a metà scrittura generi già un "file con problemi" visibile.
        AssertNessunProblema(pcB, "PC B durante la scrittura a metà (entro la tolleranza)");

        sim.CompletaScrittureAMeta();
        sim.PropagaFinoAConvergenza();
        pcB.Send(new JsonObject { ["cmd"] = "refresh" });

        var testSuB = pcB.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-SC1-0001" });
        Assert.True(testSuB["trovato"]!.GetValue<bool>(), "PC B non vede il Test dopo la convergenza.");
        var ncSuB = pcB.Send(new JsonObject { ["cmd"] = "getNC", ["id"] = "NC-SC1-0001" });
        Assert.True(ncSuB["trovato"]!.GetValue<bool>(), "PC B non vede la NC dopo la convergenza.");
        Assert.Equal("T-SC1-0001", ncSuB["testCaseId"]!.GetValue<string>());
        AssertNessunProblema(pcB, "PC B a convergenza raggiunta");

        // B modifica il Test, A deve vederlo dopo la propagazione.
        pcB.Send(new JsonObject { ["cmd"] = "editTest", ["id"] = "T-SC1-0001", ["descrizione"] = "modificato da B" });
        sim.PropagaFinoAConvergenza();
        pcA.Send(new JsonObject { ["cmd"] = "refresh" });
        var testSuA = pcA.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-SC1-0001" });
        Assert.Equal("modificato da B", testSuA["descrizione"]!.GetValue<string>());
        AssertNessunProblema(pcA, "PC A a fine scenario");
        AssertNessunProblema(pcB, "PC B a fine scenario");
    }

    [Fact]
    public void Scenario3_EntrambiIPcModificanoLoStessoCampoDelloStessoTest_ConflittoVisibileNessunaSovrascritturaSilenziosa()
    {
        using var pcA = new HostProcess(_folderA, _localA);
        var sim = new OneDriveFolderSyncSimulator(_folderA, _folderB, seed: 7);
        sim.PropagaFinoAConvergenza(); // bootstrap dei cataloghi Phase D di default, vedi Scenario1
        using var pcB = new HostProcess(_folderB, _localB);

        pcA.Send(new JsonObject { ["cmd"] = "createTest", ["id"] = "T-SC3-0001", ["componente"] = "ECM", ["veicolo"] = "Daily", ["descrizione"] = "originale" });
        sim.PropagaFinoAConvergenza();
        pcB.Send(new JsonObject { ["cmd"] = "refresh" });
        Assert.True(pcB.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-SC3-0001" })["trovato"]!.GetValue<bool>());

        // Entrambi modificano lo STESSO campo (descrizione) sulla propria copia locale PRIMA che la
        // sincronizzazione abbia il tempo di propagare l'uno o l'altro cambiamento.
        pcA.Send(new JsonObject { ["cmd"] = "editTest", ["id"] = "T-SC3-0001", ["descrizione"] = "modificato da A" });
        pcB.Send(new JsonObject { ["cmd"] = "editTest", ["id"] = "T-SC3-0001", ["descrizione"] = "modificato da B" });

        sim.PropagaFinoAConvergenza();
        pcA.Send(new JsonObject { ["cmd"] = "refresh" });
        pcB.Send(new JsonObject { ["cmd"] = "refresh" });

        InvecchiaCopieDiConflittoERileggi(pcA, pcB);
        // Nessuna sovrascrittura silenziosa: uno dei due valori resta come Test canonico, l'ALTRO deve comparire
        // come copia di conflitto visibile (mai perso, mai un file "con problemi" indistinguibile da un errore).
        var visto = pcA.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-SC3-0001" });
        Assert.True(visto["trovato"]!.GetValue<bool>());
        var descrizioneCanonica = visto["descrizione"]!.GetValue<string>();
        Assert.Contains(descrizioneCanonica, new[] { "modificato da A", "modificato da B" });

        var problemiA = pcA.Send(new JsonObject { ["cmd"] = "listProblemi" });
        var conflittiA = problemiA["copieDiConflitto"]!.AsArray();
        Assert.True(conflittiA.Count >= 1,
            "Il valore perdente deve comparire come copia di conflitto visibile, non sparire silenziosamente.");
        Assert.Empty(problemiA["fileConProblemi"]!.AsArray()); // mai indistinguibile da un errore di lettura
    }

    [Fact]
    public void Scenario6_DuePostazioniOfflineCreanoTestConStessoIdEStesseIniziali_DopoSyncNessunDatoPersoEConflittoVisibile()
    {
        using var pcA = new HostProcess(_folderA, _localA);
        var bootstrap = new OneDriveFolderSyncSimulator(_folderA, _folderB, seed: 99);
        bootstrap.PropagaFinoAConvergenza(); // bootstrap dei cataloghi Phase D di default, vedi Scenario1
        using var pcB = new HostProcess(_folderB, _localB);

        // "Offline" = nessuna propagazione ancora avvenuta DA QUI IN AVANTI (il workspace era già sincronizzato
        // prima che le due postazioni si scollegassero, come da bootstrap sopra): ciascuna postazione genera
        // localmente lo stesso Id
        // (qui assegnato esplicitamente, riproducendo l'esito di IdGenerator.Genera su due storici locali
        // identici e le stesse iniziali — la collisione stessa è già validata in isolamento da
        // IdCollisionMultiPcTests.Genera_DuePostazioniOfflineStessoStoricoEStesseIniziali_ProduceLoStessoId).
        pcA.Send(new JsonObject { ["cmd"] = "createTest", ["id"] = "T-OPA-0001", ["componente"] = "ECM", ["veicolo"] = "Daily", ["descrizione"] = "creato da A offline" });
        pcB.Send(new JsonObject { ["cmd"] = "createTest", ["id"] = "T-OPA-0001", ["componente"] = "ECM", ["veicolo"] = "Daily", ["descrizione"] = "creato da B offline" });

        bootstrap.PropagaFinoAConvergenza();
        pcA.Send(new JsonObject { ["cmd"] = "refresh" });
        pcB.Send(new JsonObject { ["cmd"] = "refresh" });

        // Nessun dato perso: uno dei due contenuti resta il Test canonico, l'altro compare come conflitto
        // (copia di conflitto o ID duplicato ambiguo, mai perso/sovrascritto senza traccia) su entrambi i lati
        // dopo la convergenza.
        InvecchiaCopieDiConflittoERileggi(pcA, pcB);
        var listaA = pcA.Send(new JsonObject { ["cmd"] = "listProblemi" });
        var listaB = pcB.Send(new JsonObject { ["cmd"] = "listProblemi" });
        var haConflittoA = listaA["copieDiConflitto"]!.AsArray().Count > 0 || listaA["duplicatiId"]!.AsArray().Count > 0;
        var haConflittoB = listaB["copieDiConflitto"]!.AsArray().Count > 0 || listaB["duplicatiId"]!.AsArray().Count > 0;
        Assert.True(haConflittoA || haConflittoB,
            "La collisione di Id deve restare visibile (copia di conflitto o ID duplicato), mai silenziosa.");

        var testSuA = pcA.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-OPA-0001" });
        Assert.True(testSuA["trovato"]!.GetValue<bool>(), "Il Test deve restare indirizzabile con l'Id originale su A.");
        var testSuB = pcB.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-OPA-0001" });
        Assert.True(testSuB["trovato"]!.GetValue<bool>(), "Il Test deve restare indirizzabile con l'Id originale su B.");

        // Il contenuto fisico di ENTRAMBE le versioni deve sopravvivere da qualche parte sul disco condiviso,
        // anche se solo una e' quella "canonica" indirizzabile per Id in questo momento.
        var contenutiA = Directory.GetFiles(_folderA, "testcase_T-OPA-0001*.json", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
        var contenutiB = Directory.GetFiles(_folderB, "testcase_T-OPA-0001*.json", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
        var tuttiIContenuti = contenutiA.Concat(contenutiB);
        Assert.Contains(tuttiIContenuti, c => c.Contains("creato da A offline"));
        Assert.Contains(tuttiIContenuti, c => c.Contains("creato da B offline"));
    }

    [Fact]
    public void Scenario2_EntrambiIPcModificanoCampiDiversiDelloStessoTest_MergeAutomaticoSenzaPerditeNeAvvisi()
    {
        using var pcA = new HostProcess(_folderA, _localA);
        var sim = new OneDriveFolderSyncSimulator(_folderA, _folderB, seed: 11) { OrdineArbitrario = true };
        sim.PropagaFinoAConvergenza(); // bootstrap dei cataloghi Phase D di default, vedi Scenario1
        using var pcB = new HostProcess(_folderB, _localB);

        pcA.Send(new JsonObject { ["cmd"] = "createTest", ["id"] = "T-SC2-0001", ["componente"] = "ECM", ["veicolo"] = "Daily", ["descrizione"] = "base" });
        sim.PropagaFinoAConvergenza();
        pcB.Send(new JsonObject { ["cmd"] = "refresh" });
        Assert.True(pcB.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-SC2-0001" })["trovato"]!.GetValue<bool>());

        // Stessa revisione di partenza, campi diversi, prima di qualunque propagazione.
        pcA.Send(new JsonObject { ["cmd"] = "editTest", ["id"] = "T-SC2-0001", ["descrizione"] = "breve da A" });
        pcB.Send(new JsonObject { ["cmd"] = "editTest", ["id"] = "T-SC2-0001", ["assegnatario"] = "Operatore B" });

        // Più giri: il PC incaricato del merge (DeviceId minore) lo applica, poi il risultato si propaga.
        for (var giro = 0; giro < 4; giro++)
        {
            sim.PropagaFinoAConvergenza();
            pcA.Send(new JsonObject { ["cmd"] = "refresh" });
            pcB.Send(new JsonObject { ["cmd"] = "refresh" });
        }

        InvecchiaCopieDiConflittoERileggi(pcA, pcB);
        var diagnostica = string.Join("\n", new[] { (pcA, "A"), (pcB, "B") }.Select(x =>
            $"PC {x.Item2}: test={x.Item1.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-SC2-0001" }).ToJsonString()} " +
            $"forks={x.Item1.Send(new JsonObject { ["cmd"] = "listForks" }).ToJsonString()} " +
            $"problemi={x.Item1.Send(new JsonObject { ["cmd"] = "listProblemi" }).ToJsonString()}"))
            + "\nfile A: " + string.Join(", ", Directory.GetFiles(_folderA, "testcase_T-SC2*", SearchOption.AllDirectories).Select(Path.GetFileName))
            + "\nfile B: " + string.Join(", ", Directory.GetFiles(_folderB, "testcase_T-SC2*", SearchOption.AllDirectories).Select(Path.GetFileName));

        foreach (var (pc, nome) in new[] { (pcA, "A"), (pcB, "B") })
        {
            var t = pc.Send(new JsonObject { ["cmd"] = "getTest", ["id"] = "T-SC2-0001" });
            Assert.True(t["descrizione"]!.GetValue<string>() == "breve da A" && t["assegnatario"]!.GetValue<string>() == "Operatore B",
                $"PC {nome}: merge incompleto.\n{diagnostica}");
            Assert.Empty(pc.Send(new JsonObject { ["cmd"] = "listForks" })["forks"]!.AsArray());
            AssertNessunProblema(pc, $"PC {nome} dopo il merge automatico");
            var esito = pc.Send(new JsonObject { ["cmd"] = "listProblemi" });
            Assert.True(esito["copieDiConflitto"]!.AsArray().Count == 0,
                $"PC {nome}: una copia di conflitto già assorbita dal merge automatico non deve restare come avviso: " +
                esito["copieDiConflitto"]!.ToJsonString());
            Assert.Empty(esito["duplicatiId"]!.AsArray());
        }
    }

    private (HostProcess A, HostProcess B, OneDriveFolderSyncSimulator Sim) AvviaDuePc(int seed)
    {
        var pcA = new HostProcess(_folderA, _localA);
        var sim = new OneDriveFolderSyncSimulator(_folderA, _folderB, seed) { OrdineArbitrario = true };
        sim.PropagaFinoAConvergenza(); // bootstrap dei cataloghi Phase D di default, vedi Scenario1
        return (pcA, new HostProcess(_folderB, _localB), sim);
    }

    private static void Sincronizza(OneDriveFolderSyncSimulator sim, params HostProcess[] pcs)
    {
        for (var giro = 0; giro < 3; giro++)
        {
            sim.PropagaFinoAConvergenza();
            foreach (var pc in pcs) pc.Send(new JsonObject { ["cmd"] = "refresh" });
        }
    }

    private void AssertTuttoPulito(string contesto, params (HostProcess Pc, string Nome)[] pcs)
    {
        InvecchiaCopieDiConflittoERileggi(pcs.Select(p => p.Pc).ToArray());
        foreach (var (pc, nome) in pcs)
        {
            AssertNessunProblema(pc, $"{contesto}, PC {nome}");
            var esito = pc.Send(new JsonObject { ["cmd"] = "listProblemi" });
            Assert.True(esito["copieDiConflitto"]!.AsArray().Count == 0, $"{contesto}, PC {nome}: copie di conflitto visibili: {esito["copieDiConflitto"]!.ToJsonString()}");
            Assert.True(esito["duplicatiId"]!.AsArray().Count == 0, $"{contesto}, PC {nome}: ID duplicati: {esito["duplicatiId"]!.ToJsonString()}");
        }
    }

    private static JsonObject Cmd(string cmd, params (string K, string V)[] campi)
    {
        var o = new JsonObject { ["cmd"] = cmd };
        foreach (var (k, v) in campi) o[k] = v;
        return o;
    }

    [Fact]
    public void Scenario4_EliminaCestinoRipristinoEliminazioneDefinitivaFraPc_StatoCoerenteSenzaResurrezioni()
    {
        var (pcA, pcB, sim) = AvviaDuePc(seed: 21);
        using (pcA) using (pcB)
        {
            pcA.Send(Cmd("createTest", ("id", "T-SC4-0001"), ("descrizione", "da eliminare")));
            Sincronizza(sim, pcA, pcB);
            Assert.True(pcB.Send(Cmd("getTest", ("id", "T-SC4-0001")))["trovato"]!.GetValue<bool>());

            // A lo mette nel cestino: la cancellazione arriva a B in ritardo e in ordine sparso.
            pcA.Send(Cmd("deleteTest", ("id", "T-SC4-0001")));
            sim.Propaga(ritardoTickPerNuoviCambiamenti: 2);
            pcB.Send(Cmd("refresh"));
            Sincronizza(sim, pcA, pcB);
            var suB = pcB.Send(Cmd("getTestAnche", ("id", "T-SC4-0001")));
            Assert.True(suB["trovato"]!.GetValue<bool>() && suB["eliminato"]!.GetValue<bool>(), "B deve vederlo nel cestino: " + suB.ToJsonString());

            // B lo ripristina: A deve rivederlo attivo.
            pcB.Send(Cmd("restoreTest", ("id", "T-SC4-0001")));
            Sincronizza(sim, pcA, pcB);
            var suA = pcA.Send(Cmd("getTestAnche", ("id", "T-SC4-0001")));
            Assert.True(suA["trovato"]!.GetValue<bool>() && !suA["eliminato"]!.GetValue<bool>(), "A deve vederlo ripristinato: " + suA.ToJsonString());

            // A lo elimina definitivamente: sparisce ovunque e non ricompare con altri giri.
            pcA.Send(Cmd("deleteTest", ("id", "T-SC4-0001")));
            pcA.Send(Cmd("purgeTest", ("id", "T-SC4-0001")));
            Sincronizza(sim, pcA, pcB);
            Sincronizza(sim, pcA, pcB);
            foreach (var (pc, nome) in new[] { (pcA, "A"), (pcB, "B") })
            {
                var t = pc.Send(Cmd("getTestAnche", ("id", "T-SC4-0001")));
                Assert.False(t["trovato"]!.GetValue<bool>(), $"PC {nome}: il Test eliminato definitivamente è ricomparso: {t.ToJsonString()}");
            }
            AssertTuttoPulito("Scenario 4", (pcA, "A"), (pcB, "B"));
        }
    }

    [Fact]
    public void Scenario5_AvvisoAltraPostazione_ConDuePostazioniReali()
    {
        var (pcA, pcB, sim) = AvviaDuePc(seed: 31);
        using (pcA) using (pcB)
        {
            pcA.Send(Cmd("createTest", ("id", "T-SC5-0001"), ("descrizione", "condiviso")));
            Sincronizza(sim, pcA, pcB);

            pcA.Send(Cmd("lockTest", ("id", "T-SC5-0001"), ("operatore", "Mario")));
            Sincronizza(sim, pcA, pcB);
            var avvisoB = pcB.Send(Cmd("avvisoAltraPostazione", ("id", "T-SC5-0001"), ("operatore", "Luigi")));
            Assert.True(avvisoB["avviso"]!.GetValue<bool>(), "B deve vedere che A sta modificando il Test.");
            Assert.Equal("Mario", avvisoB["operatore"]!.GetValue<string>());
            // Stesso nome operatore ma altra postazione: l'avviso deve comparire lo stesso.
            Assert.True(pcB.Send(Cmd("avvisoAltraPostazione", ("id", "T-SC5-0001"), ("operatore", "Mario")))["avviso"]!.GetValue<bool>());
            // Mai un avviso per il proprio lock.
            Assert.False(pcA.Send(Cmd("avvisoAltraPostazione", ("id", "T-SC5-0001"), ("operatore", "Mario")))["avviso"]!.GetValue<bool>());

            pcA.Send(Cmd("releaseLock", ("id", "T-SC5-0001")));
            Sincronizza(sim, pcA, pcB);
            Assert.False(pcB.Send(Cmd("avvisoAltraPostazione", ("id", "T-SC5-0001"), ("operatore", "Luigi")))["avviso"]!.GetValue<bool>(),
                "Rilasciato il lock, l'avviso deve sparire.");
            AssertTuttoPulito("Scenario 5", (pcA, "A"), (pcB, "B"));
        }
    }

    [Fact]
    public void Scenario7_CatalogoEcuModificatoDaiDuePc_Converge()
    {
        var (pcA, pcB, sim) = AvviaDuePc(seed: 41);
        using (pcA) using (pcB)
        {
            // Voci diverse aggiunte insieme sui due PC.
            pcA.Send(Cmd("addEcu", ("nome", "Componente-DA-A")));
            pcB.Send(Cmd("addEcu", ("nome", "Componente-DA-B")));
            Sincronizza(sim, pcA, pcB);
            foreach (var (pc, nome) in new[] { (pcA, "A"), (pcB, "B") })
            {
                var componente = pc.Send(Cmd("listEcu"))["componente"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
                Assert.Contains("Componente-DA-A", componente);
                Assert.Contains("Componente-DA-B", componente);
            }
            AssertTuttoPulito("Scenario 7", (pcA, "A"), (pcB, "B"));
        }
    }

    [Fact]
    public void Scenario7b_StessaEcuAggiuntaInsiemeDaiDuePc_CatalogoResteLeggibileConUnaSolaVoce()
    {
        var (pcA, pcB, sim) = AvviaDuePc(seed: 43);
        using (pcA) using (pcB)
        {
            pcA.Send(Cmd("addEcu", ("nome", "Componente-COMUNE")));
            pcB.Send(Cmd("addEcu", ("nome", "Componente-COMUNE")));
            Sincronizza(sim, pcA, pcB);
            foreach (var (pc, nome) in new[] { (pcA, "A"), (pcB, "B") })
            {
                var componente = pc.Send(Cmd("listEcu"))["componente"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
                Assert.True(componente.Count(e => e == "Componente-COMUNE") == 1, $"PC {nome}: attesa una sola componente-COMUNE, trovate: {string.Join(", ", componente)}");
            }
            AssertTuttoPulito("Scenario 7b", (pcA, "A"), (pcB, "B"));
        }
    }

    [Fact]
    public void Scenario7c_StessaEcuModificataInsiemeDaiDuePc_CatalogoResteLeggibile()
    {
        var (pcA, pcB, sim) = AvviaDuePc(seed: 47);
        using (pcA) using (pcB)
        {
            pcA.Send(Cmd("addEcu", ("nome", "Componente-CONDIVISA")));
            Sincronizza(sim, pcA, pcB);
            pcA.Send(Cmd("editEcu", ("nome", "Componente-CONDIVISA"), ("archiviata", "true")));
            pcB.Send(Cmd("editEcu", ("nome", "Componente-CONDIVISA"), ("archiviata", "false")));
            pcB.Send(Cmd("addEcu", ("nome", "Componente-SOLO-B")));
            Sincronizza(sim, pcA, pcB);
            foreach (var (pc, nome) in new[] { (pcA, "A"), (pcB, "B") })
            {
                var componente = pc.Send(Cmd("listEcu"))["componente"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
                Assert.True(componente.Count(e => e == "Componente-CONDIVISA") == 1 && componente.Contains("Componente-SOLO-B"),
                    $"PC {nome}: catalogo inatteso: {string.Join(", ", componente)}");
            }
            AssertTuttoPulito("Scenario 7c", (pcA, "A"), (pcB, "B"));
        }
    }

    [Fact]
    public void Scenario8_PcOfflineALungoConModificheLocali_SiRiallineaSenzaPerdite()
    {
        var (pcA, pcB, sim) = AvviaDuePc(seed: 51);
        using (pcA) using (pcB)
        {
            pcA.Send(Cmd("createTest", ("id", "T-SC8-0001"), ("descrizione", "base 1")));
            pcA.Send(Cmd("createTest", ("id", "T-SC8-0002"), ("descrizione", "base 2")));
            Sincronizza(sim, pcA, pcB);

            // B resta offline (nessuna sincronizzazione) mentre entrambi lavorano per molti giri.
            for (var i = 0; i < 5; i++)
            {
                pcA.Send(Cmd("editTest", ("id", "T-SC8-0001"), ("descrizione", $"A giro {i}")));
                pcA.Send(Cmd("refresh"));
                pcB.Send(Cmd("editTest", ("id", "T-SC8-0002"), ("assegnatario", $"B giro {i}")));
                pcB.Send(Cmd("refresh"));
            }
            pcA.Send(Cmd("createTest", ("id", "T-SC8-0003"), ("descrizione", "nuovo da A offline")));
            pcB.Send(Cmd("createNC", ("id", "NC-SC8-0001"), ("testId", "T-SC8-0002"), ("descrizione", "NC da B offline")));

            // B torna online.
            Sincronizza(sim, pcA, pcB);
            Sincronizza(sim, pcA, pcB);
            foreach (var (pc, nome) in new[] { (pcA, "A"), (pcB, "B") })
            {
                Assert.Equal("A giro 4", pc.Send(Cmd("getTest", ("id", "T-SC8-0001")))["descrizione"]!.GetValue<string>());
                Assert.Equal("B giro 4", pc.Send(Cmd("getTest", ("id", "T-SC8-0002")))["assegnatario"]!.GetValue<string>());
                Assert.True(pc.Send(Cmd("getTest", ("id", "T-SC8-0003")))["trovato"]!.GetValue<bool>(), $"PC {nome}: manca il Test creato da A.");
                Assert.True(pc.Send(Cmd("getNC", ("id", "NC-SC8-0001")))["trovato"]!.GetValue<bool>(), $"PC {nome}: manca la NC creata da B.");
            }
            AssertTuttoPulito("Scenario 8", (pcA, "A"), (pcB, "B"));
        }
    }

    [Fact]
    public void Scenario9_TestSpostatoSuAMentreBLoModifica_NessunDoppioneNessunaPerdita()
    {
        var (pcA, pcB, sim) = AvviaDuePc(seed: 61);
        using (pcA) using (pcB)
        {
            pcA.Send(Cmd("createTest", ("id", "T-SC9-0001"), ("componente", "ECM"), ("descrizione", "prima")));
            Sincronizza(sim, pcA, pcB);

            pcA.Send(Cmd("moveTest", ("id", "T-SC9-0001"), ("componente", "BCM")));
            pcB.Send(Cmd("editTest", ("id", "T-SC9-0001"), ("assegnatario", "Operatore B")));
            Sincronizza(sim, pcA, pcB);
            Sincronizza(sim, pcA, pcB);

            foreach (var (pc, nome) in new[] { (pcA, "A"), (pcB, "B") })
            {
                var t = pc.Send(Cmd("getTest", ("id", "T-SC9-0001")));
                var tutto = pc.Send(Cmd("getTestAnche", ("id", "T-SC9-0001")));
                Assert.True(t["trovato"]!.GetValue<bool>(), $"PC {nome}: Test sparito: {tutto.ToJsonString()}");
                Assert.True(t["assegnatario"]!.GetValue<string>() == "Operatore B" && tutto["componente"]!.GetValue<string>().Contains("BCM"),
                    $"PC {nome}: spostamento o modifica persi: {t.ToJsonString()} {tutto.ToJsonString()}");
            }
            AssertTuttoPulito("Scenario 9", (pcA, "A"), (pcB, "B"));
        }
    }

    [Fact]
    public void ScenarioCrESegnalazioni_CreateSuAModificateSuB_VisteSuA()
    {
        var (pcA, pcB, sim) = AvviaDuePc(seed: 71);
        using (pcA) using (pcB)
        {
            var uidSeg = Guid.NewGuid().ToString();
            pcA.Send(Cmd("createTest", ("id", "T-SCX-0001"), ("descrizione", "per segnalazione")));
            pcA.Send(Cmd("createCR", ("id", "CR-SCX-0001"), ("titolo", "titolo da A")));
            pcA.Send(Cmd("createSeg", ("uid", uidSeg), ("testId", "T-SCX-0001")));
            Sincronizza(sim, pcA, pcB);

            pcB.Send(Cmd("editCR", ("id", "CR-SCX-0001"), ("titolo", "titolo da B")));
            pcB.Send(Cmd("editSeg", ("uid", uidSeg), ("motivazione", "motivazione da B")));
            Sincronizza(sim, pcA, pcB);

            Assert.Equal("titolo da B", pcA.Send(Cmd("getCR", ("id", "CR-SCX-0001")))["titolo"]!.GetValue<string>());
            Assert.Equal("motivazione da B", pcA.Send(Cmd("getSeg", ("uid", uidSeg)))["motivazione"]!.GetValue<string>());
            AssertTuttoPulito("Scenario CR/Segnalazioni", (pcA, "A"), (pcB, "B"));
        }
    }

    /// <summary>Simula il passare del tempo per le copie di conflitto (data del file indietro oltre la
    /// tolleranza di 2 minuti) senza cambiarne il contenuto, poi rilegge su entrambi i PC: l'app le valuta come
    /// farebbe qualche minuto dopo la sincronizzazione reale.</summary>
    private void InvecchiaCopieDiConflittoERileggi(params HostProcess[] pcs)
    {
        foreach (var cartella in new[] { _folderA, _folderB })
            foreach (var file in Directory.GetFiles(cartella, "*.json", SearchOption.AllDirectories)
                         .Where(f => Path.GetFileNameWithoutExtension(f).Contains("-OneDrive", StringComparison.OrdinalIgnoreCase)))
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));
        foreach (var pc in pcs) pc.Send(new JsonObject { ["cmd"] = "refresh" });
    }

    private static void AssertNessunProblema(HostProcess pc, string contesto)
    {
        var esito = pc.Send(new JsonObject { ["cmd"] = "listProblemi" });
        var fileConProblemi = esito["fileConProblemi"]!.AsArray();
        Assert.True(fileConProblemi.Count == 0,
            $"{contesto}: {fileConProblemi.Count} 'File con problemi' inattesi: " +
            string.Join(" | ", fileConProblemi.Select(p => p!["errore"]!.GetValue<string>())));
    }

    /// <summary>Copia adattata dell'HostProcess di MultiPcSyncTests: qui ogni PC punta sulla PROPRIA cartella
    /// fisica (mai una condivisa unica), sincronizzata da OneDriveFolderSyncSimulator.</summary>
    private sealed class HostProcess : IDisposable
    {
        private readonly Process _process;

        public HostProcess(string ownFolder, string local)
        {
            Directory.CreateDirectory(ownFolder);
            Directory.CreateDirectory(local);
            var exe = LocateHostExecutable();
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--shared");
            psi.ArgumentList.Add(ownFolder);
            psi.ArgumentList.Add("--local");
            psi.ArgumentList.Add(local);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Impossibile avviare TestLoom.App.MultiPcHost.");

            var readyLine = ReadLineWithTimeout(TimeSpan.FromSeconds(30))
                ?? throw new TimeoutException("TestLoom.App.MultiPcHost non ha risposto 'ready' entro il timeout." + LeggiStderr());
            var ready = JsonNode.Parse(readyLine)!.AsObject();
            if (ready["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Avvio host fallito: " + readyLine);
        }

        public JsonObject Send(JsonObject comando)
        {
            _process.StandardInput.WriteLine(comando.ToJsonString());
            _process.StandardInput.Flush();
            var riga = ReadLineWithTimeout(TimeSpan.FromSeconds(30))
                ?? throw new TimeoutException($"Nessuna risposta dall'host per il comando '{comando["cmd"]}'." + LeggiStderr());
            var risposta = JsonNode.Parse(riga)!.AsObject();
            if (risposta["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"Comando '{comando["cmd"]}' fallito: {riga}");
            return risposta;
        }

        private string? ReadLineWithTimeout(TimeSpan timeout)
        {
            var task = _process.StandardOutput.ReadLineAsync();
            return task.Wait(timeout) ? task.Result : null;
        }

        private string LeggiStderr()
        {
            try
            {
                var err = _process.StandardError.ReadToEnd();
                return string.IsNullOrWhiteSpace(err) ? "" : $" stderr: {err}";
            }
            catch { return ""; }
        }

        private static string LocateHostExecutable()
        {
            var nomeEseguibile = OperatingSystem.IsWindows() ? "TestLoom.App.MultiPcHost.exe" : "TestLoom.App.MultiPcHost";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            var cursore = dir;
            while (cursore != null && !string.Equals(cursore.Name, "src", StringComparison.OrdinalIgnoreCase))
                cursore = cursore.Parent;
            if (cursore == null)
                throw new InvalidOperationException($"Impossibile risalire alla cartella 'src' da {AppContext.BaseDirectory}.");

            var hostDir = Path.Combine(cursore.FullName, "TestLoom.App.MultiPcHost");
            var trovato = Directory.Exists(hostDir)
                ? Directory.EnumerateFiles(hostDir, nomeEseguibile, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            return trovato ?? throw new InvalidOperationException(
                $"TestLoom.App.MultiPcHost non trovato sotto {hostDir}. Compila TestLoom.slnx (include il progetto host) prima di eseguire MultiPcSyncScenarios.");
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            _process.Dispose();
        }
    }
}
