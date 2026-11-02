using System.Linq;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>La matrice Requisito×componente vista sparsa (solo coppie testate) prima viveva dentro ReportViewModel
/// (Addendum: grill-me 2026-08-18, punto 3); è stata sostituita dalla matrice completa e dedicata di
/// AuditViewModel (Addendum: grill-me 2026-08-18, follow-up, punto 5), che mostra ogni coppia del
/// sottoveicolo scelto invece delle sole coppie con almeno un test.</summary>
[Trait("Category", "Lento")]
public sealed class AuditViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_auditvm_" + Guid.NewGuid().ToString("N"));

    private AppState CreaStatoComeMaster()
    {
        SeedDemoDataService.Genera(_dir);
        // Il seed QA include un JSON intenzionalmente illeggibile per i test di tolleranza. I flussi Audit
        // che scrivono ora richiedono una fotografia Test autorevole e completa, quindi questa fixture usa
        // il resto del seed valido; i test di refresh incompleto aggiungono il file guasto esplicitamente.
        foreach (var path in Directory.GetFiles(_dir, "testcase_CORROTTO-TEST-99.json", SearchOption.AllDirectories))
            File.Delete(path);

        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = state.Master.Operatori.Load().Single(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Righe_ContengonoUnaCellaValutataPerLaCoppiaEcuRequisitoTestata()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        var veicolo = new Veicolo { Nome = "Sottoveicolo-Test" };
        state.Master.Veicoli.Save(new List<Veicolo> { veicolo });
        var componente = new Componente { Nome = "ECM", VeicoliAssociati = { veicolo.Nome } };
        state.Master.Componente.Save(new List<Componente> { componente });
        var requisito = new Requisito { Id = "R1", Testo = "Il sistema deve..." };
        state.Master.Requisiti.Save(new List<Requisito> { requisito });

        var tc = new TestCase { Id = "TC-001", Componente = "ECM", RequisitoId = new List<string> { "R1" }, Veicolo = veicolo.Nome };
        var esecuzione = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V" };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, tc.RequisitoId);
        tc.Esecuzioni.Add(esecuzione);
        state.TestCases.Save(tc, true);

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };

        var riga = Assert.Single(vm.Righe, r => r.RequisitoId == "R1");
        var cella = riga.Celle["ECM"];
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio);
    }

    /// <summary>Fix 6.4.0 (R4) — unit sul ViewModel, senza FlaUI: una collisione di chiave business Phase D
    /// con valori discordanti sul catalogo audit-valutazioni-requisito-componente-veicolo (due righe sulla stessa
    /// cella RequisitoId|Componente|Veicolo, es. da due postazioni che hanno scritto quasi in contemporanea — vedi
    /// PhaseDMasterKeyCollisionTolerance) deve arrivare fino a CellaMatriceAuditVisual.ConflittoCollisioneCella
    /// e farsi notare in Testo/Colore/ToolTip esattamente come lo stato Conflitto esistente, senza però
    /// perdere il valore deterministico mostrato (UltimaModifica più recente).</summary>
    [Fact]
    public void Righe_CollisioneDiCellaConValoriDiscordanti_CellaVisualeSegnalaIlConflitto()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        var veicolo = new Veicolo { Nome = "Sottoveicolo-Test" };
        state.Master.Veicoli.Save(new List<Veicolo> { veicolo });
        var componente = new Componente { Nome = "ECM", VeicoliAssociati = { veicolo.Nome } };
        state.Master.Componente.Save(new List<Componente> { componente });
        var requisito = new Requisito { Id = "R1", Testo = "Il sistema deve..." };
        state.Master.Requisiti.Save(new List<Requisito> { requisito });

        // Due righe sulla STESSA cella (R1|ECM|Sottoveicolo-Test) con valori Manuale discordanti — la stessa
        // situazione che due postazioni produrrebbero scrivendo quasi in contemporanea la stessa cella nuova
        // (vedi ValutazioneRequisitoEcuVeicoloPropagazioneMassivaConcorrenzaMultiPcTests per la riproduzione
        // con vera concorrenza di thread). Qui il dato è costruito con DUE Save separate, ENTRAMBE con baseline
        // vuota (nessuna delle due ha "visto" l'altra, esattamente come due postazioni realmente concorrenti):
        // una singola Save con entrambe le righe nella STESSA lista verrebbe invece rifiutata subito
        // (ValidateBusinessKeys su 'localItems' — un chiamante che sottomette due righe duplicate in una sola
        // chiamata è sempre un bug, mai la race multi-postazione che questo catalogo tollera — vedi
        // PhaseDMasterRepositoryEngine.Save/SavePatch).
        var repoPcA = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(state.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var repoPcB = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(state.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var rigaA = new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "ECM", Veicolo = veicolo.Nome, Punteggio = PunteggioConformita.Conforme,
            Origine = OrigineValutazioneRequisito.Manuale, Nota = "da PC-A", Operatore = "PC-A",
            UltimaModifica = new DateTime(2026, 3, 1, 10, 0, 0),
        };
        var rigaB = new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "ECM", Veicolo = veicolo.Nome, Punteggio = PunteggioConformita.NonConforme,
            Origine = OrigineValutazioneRequisito.Manuale, Nota = "da PC-B", Operatore = "PC-B",
            UltimaModifica = new DateTime(2026, 3, 1, 10, 0, 5),
        };
        repoPcA.Save(new List<ValutazioneRequisitoEcuVeicolo> { rigaA }, new List<ValutazioneRequisitoEcuVeicolo>());
        repoPcB.Save(new List<ValutazioneRequisitoEcuVeicolo> { rigaB }, new List<ValutazioneRequisitoEcuVeicolo>());

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };

        var cella = vm.Righe.Single(r => r.RequisitoId == "R1").Celle["ECM"];
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.True(cella.ConflittoCollisioneCella);
        // Valore deterministico: vince PC-B (UltimaModifica più recente), non fuso/mediato con PC-A.
        Assert.Equal(PunteggioConformita.NonConforme, cella.Punteggio);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Manuale, cella.Origine);
        // Testo/Colore devono segnalare il conflitto in modo distinto da un Manuale ordinario, con lo stesso
        // colore già usato per StatoCellaAudit.Conflitto — non un valore silenzioso.
        Assert.StartsWith("⚠", cella.Testo);
        Assert.Equal("#FCA5A5", cella.Colore);
        Assert.Contains("due postazioni", cella.ToolTip, StringComparison.OrdinalIgnoreCase);

        // Una scelta Manuale successiva chiude il conflitto per la vista, mantenendo il marcatore storico e
        // gli snapshot della riga scartata invece di cancellare la traccia della collisione.
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(state.Paths.ValutazioniRequisitoEcuVeicoloFile),
            "R1", "ECM", new[] { veicolo.Nome }, PunteggioConformita.Conforme,
            "cella rivista e reimpostata dall'operatore", "PC-C", null, out var errore));
        Assert.Null(errore);

        vm.Aggiorna();
        var cellaDopo = vm.Righe.Single(r => r.RequisitoId == "R1").Celle["ECM"];
        Assert.False(cellaDopo.ConflittoCollisioneCella);
        Assert.DoesNotContain("⚠", cellaDopo.Testo);

        var salvata = Assert.Single(state.Master.ValutazioniRequisitoEcuVeicolo.Load());
        Assert.Contains(salvata.Changelog, c => c.Campo == "(consolidamento collisione cella)" &&
            c.ValoreNuovo?.Contains("valori discordanti", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(salvata.Changelog, c => c.Campo == "(risoluzione collisione cella)" && c.Operatore == "PC-C");
        Assert.Contains(salvata.Storico, e => e.Operatore == "PC-A" && e.Punteggio == PunteggioConformita.Conforme);
        Assert.Contains(salvata.Storico, e => e.Operatore == "PC-B" && e.Punteggio == PunteggioConformita.NonConforme);
    }

    [Fact]
    public void Righe_CoppiaMaiTestata_CellaDaTestare()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        var veicolo = new Veicolo { Nome = "Sottoveicolo-Test" };
        state.Master.Veicoli.Save(new List<Veicolo> { veicolo });
        var componente = new Componente { Nome = "ECM", VeicoliAssociati = { veicolo.Nome } };
        state.Master.Componente.Save(new List<Componente> { componente });
        var requisito = new Requisito { Id = "R1", Testo = "Il sistema deve..." };
        state.Master.Requisiti.Save(new List<Requisito> { requisito });

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };

        var riga = Assert.Single(vm.Righe);
        Assert.Equal(StatoCellaAudit.DaTestare, riga.Celle["ECM"].Stato);
    }

    [Fact]
    public void CambiareSottoveicoloSelezionato_RicaricaLeColonneEcu()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        var veicoloA = new Veicolo { Nome = "Sottoveicolo-A" };
        var veicoloB = new Veicolo { Nome = "Sottoveicolo-B" };
        state.Master.Veicoli.Save(new List<Veicolo> { veicoloA, veicoloB });
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "Componente-A", VeicoliAssociati = { veicoloA.Nome } },
            new() { Nome = "Componente-B", VeicoliAssociati = { veicoloB.Nome } },
        });

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicoloA.Nome };
        Assert.Equal(new[] { "Componente-A" }, vm.EcuColonne);

        vm.SottoveicoloSelezionato = veicoloB.Nome;
        Assert.Equal(new[] { "Componente-B" }, vm.EcuColonne);
    }

    [Fact]
    public void Righe_DueTestDiversiConPunteggiDiscordanti_CellaInConflitto()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        var veicolo = new Veicolo { Nome = "Sottoveicolo-Test" };
        state.Master.Veicoli.Save(new List<Veicolo> { veicolo });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "ECM", VeicoliAssociati = { veicolo.Nome } } });
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1" } });

        var tcA = new TestCase { Id = "TC-A", Componente = "ECM", RequisitoId = { "R1" }, Veicolo = veicolo.Nome };
        tcA.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.Conforme } } });
        state.TestCases.Save(tcA, true);
        var tcB = new TestCase { Id = "TC-B", Componente = "ECM", RequisitoId = { "R1" }, Veicolo = veicolo.Nome };
        tcB.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Esito = Esito.NonConforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.NonConforme } } });
        state.TestCases.Save(tcB, true);

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };

        var cella = vm.Righe.Single(r => r.RequisitoId == "R1").Celle["ECM"];
        Assert.True(cella.InConflitto);
        Assert.Equal(2, cella.Candidati.Count);
        Assert.True(vm.HaConflitti);
        Assert.Equal(1, vm.TotaleCoppieInConflitto);
    }

    [Fact]
    public void RisolviConflitto_ComeMaster_LaCellaDiventaValutataConIlTestIndicato()
    {
        var state = CreaStatoComeMaster();

        var veicolo = new Veicolo { Nome = "Sottoveicolo-Risoluzione" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "Componente-RISOLUZIONE", VeicoliAssociati = { veicolo.Nome } }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-RISOLUZIONE" }).ToList());

        var tcA = new TestCase { Id = "TC-RIS-A", Componente = "Componente-RISOLUZIONE", RequisitoId = { "R-RISOLUZIONE" }, Veicolo = veicolo.Nome };
        tcA.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-RISOLUZIONE", Valore = PunteggioConformita.Conforme } } });
        state.TestCases.Save(tcA, true);
        var tcB = new TestCase { Id = "TC-RIS-B", Componente = "Componente-RISOLUZIONE", RequisitoId = { "R-RISOLUZIONE" }, Veicolo = veicolo.Nome };
        tcB.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Esito = Esito.NonConforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-RISOLUZIONE", Valore = PunteggioConformita.NonConforme } } });
        state.TestCases.Save(tcB, true);

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };
        Assert.True(vm.Righe.Single(r => r.RequisitoId == "R-RISOLUZIONE").Celle["Componente-RISOLUZIONE"].InConflitto);
        // Il dataset di semina (SeedDemoDataService, Addendum: grill-me 2026-08-18, seconda follow-up) include
        // ora un proprio scenario di conflitto deliberato altrove: il conteggio globale non parte da zero,
        // quindi si verifica solo che risolvere QUESTA coppia lo faccia scendere di uno, non che azzeri tutto.
        var conflittiPrima = vm.TotaleCoppieInConflitto;

        vm.RisolviConflitto("Componente-RISOLUZIONE", "R-RISOLUZIONE", "TC-RIS-A", "Il test B era su una configurazione non rappresentativa.");

        var cella = vm.Righe.Single(r => r.RequisitoId == "R-RISOLUZIONE").Celle["Componente-RISOLUZIONE"];
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio);
        Assert.Equal(conflittiPrima - 1, vm.TotaleCoppieInConflitto);
    }

    // Feedback utente 2026-08-26: inserimento/correzione manuale di una cella direttamente dalla matrice.

    [Fact]
    public void SalvaValutazioneManuale_ComeMaster_CompilaUnaCellaAncoraDaTestare()
    {
        var state = CreaStatoComeMaster();

        var veicolo = new Veicolo { Nome = "Sottoveicolo-Manuale" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "Componente-MANUALE", VeicoliAssociati = { veicolo.Nome } }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-MANUALE" }).ToList());

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };
        Assert.Equal(StatoCellaAudit.DaTestare, vm.Righe.Single(r => r.RequisitoId == "R-MANUALE").Celle["Componente-MANUALE"].Stato);

        vm.SalvaValutazioneManuale("Componente-MANUALE", "R-MANUALE", PunteggioConformita.NonApplicabile, "Non applicabile su questa configurazione");

        var cella = vm.Righe.Single(r => r.RequisitoId == "R-MANUALE").Celle["Componente-MANUALE"];
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.NonApplicabile, cella.Punteggio);
        Assert.True(cella.InserimentoManuale);
    }

    [Fact]
    public void SalvaValutazioneManuale_SenzaMotivazione_NonVieneSalvata()
    {
        var state = CreaStatoComeMaster();
        var veicolo = new Veicolo { Nome = "Sottoveicolo-ManualeNoMotivo" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "Componente-NM", VeicoliAssociati = { veicolo.Nome } }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-NM" }).ToList());
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };

        vm.SalvaValutazioneManuale("Componente-NM", "R-NM", PunteggioConformita.Conforme, "  ");

        Assert.Equal(StatoCellaAudit.DaTestare, vm.Righe.Single(r => r.RequisitoId == "R-NM").Celle["Componente-NM"].Stato);
    }

    [Fact]
    public void SalvaValutazioneManuale_NonMaster_NonVieneSalvata()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var veicolo = new Veicolo { Nome = "Sottoveicolo-Viewer" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "Componente-VIEWER", VeicoliAssociati = { veicolo.Nome } }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-VIEWER" }).ToList());
        var viewer = new Operatore { Nome = "Viewer Test", Ruolo = RuoloOperatore.Viewer, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" };
        state.Master.Operatori.Save(state.Master.Operatori.Load().Append(viewer).ToList());
        Assert.True(state.ImpostaOperatore("Viewer Test", "1234"));

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };
        vm.SalvaValutazioneManuale("Componente-VIEWER", "R-VIEWER", PunteggioConformita.Conforme, "Tentativo non autorizzato");

        Assert.Equal(StatoCellaAudit.DaTestare, vm.Righe.Single(r => r.RequisitoId == "R-VIEWER").Celle["Componente-VIEWER"].Stato);
    }

    [Fact]
    public void RimuoviValutazioneManuale_ComeMaster_LaCellaTornaAllEsitoAutomatico()
    {
        var state = CreaStatoComeMaster();
        var veicolo = new Veicolo { Nome = "Sottoveicolo-Rimozione" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "Componente-RIMOZIONE", VeicoliAssociati = { veicolo.Nome } }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-RIMOZIONE" }).ToList());

        var tc = new TestCase { Id = "TC-RIMOZIONE", Componente = "Componente-RIMOZIONE", RequisitoId = { "R-RIMOZIONE" }, Veicolo = veicolo.Nome };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-RIMOZIONE", Valore = PunteggioConformita.Conforme } } });
        state.TestCases.Save(tc, true);
        state.Master.RisoluzioniConflittoAudit.Save(new List<RisoluzioneConflittoAudit>
        {
            new()
            {
                Componente = "Componente-RIMOZIONE", RequisitoId = "R-RIMOZIONE", TestCaseIdValido = tc.Id,
                PunteggioManuale = PunteggioConformita.NonConforme, Motivazione = "valore legacy da rimuovere",
                UltimaModifica = DateTime.Now.AddSeconds(1), ModificatoDa = "Master test",
            },
        });

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };
        vm.SalvaValutazioneManuale("Componente-RIMOZIONE", "R-RIMOZIONE", PunteggioConformita.NonConforme, "Correzione temporanea");
        Assert.True(vm.Righe.Single(r => r.RequisitoId == "R-RIMOZIONE").Celle["Componente-RIMOZIONE"].InserimentoManuale);

        // Legacy storico: un punteggio manuale e una scelta Test sono decisioni distinte. Il vecchio
        // comando d'inserimento li rende mutuamente esclusivi, quindi simula un catalogo che contiene entrambe
        // le decisioni prima di verificare che la rimozione del punteggio non cancelli la scelta Test.
        var risoluzioni = state.Master.RisoluzioniConflittoAudit.Load();
        var atteseRisoluzioni = EntityStateFingerprint.Clone(risoluzioni);
        var risoluzione = Assert.Single(risoluzioni);
        risoluzione.TestCaseIdValido = tc.Id;
        state.Master.RisoluzioniConflittoAudit.Save(risoluzioni, atteseRisoluzioni);

        vm.RimuoviValutazioneManuale("Componente-RIMOZIONE", "R-RIMOZIONE");

        var cella = vm.Righe.Single(r => r.RequisitoId == "R-RIMOZIONE").Celle["Componente-RIMOZIONE"];
        Assert.False(cella.InserimentoManuale);
        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio); // torna all'esito calcolato dal Test collegato
        var legacy = Assert.Single(state.Master.RisoluzioniConflittoAudit.Load());
        Assert.Null(legacy.PunteggioManuale);
        Assert.Equal(tc.Id, legacy.TestCaseIdValido); // archiviare il manuale non cancella la decisione Test separata
        Assert.Contains(legacy.StoricoPunteggiManuali, e => e.Nota == "valore legacy da rimuovere" &&
            e.Punteggio == PunteggioConformita.NonConforme);
        Assert.Contains(legacy.StoricoPunteggiManuali, e => e.Nota == "Correzione temporanea" &&
            e.Punteggio == PunteggioConformita.NonConforme);
    }

    // Piano 6.3.1 §1 UI: propagazione esplicita "!" e inserimento manuale con dimensione veicolo.

    private static (AppState State, Veicolo Origine, Veicolo Destinazione, Componente Componente) PreparaScenarioPropagazione(string dir)
    {
        SeedDemoDataService.Genera(dir);
        // Il seed QA include un JSON intenzionalmente corrotto per i test di tolleranza/diagnostica.
        // Questa fixture richiede invece una fotografia Test completa per verificare la propagazione fail-closed.
        foreach (var path in Directory.GetFiles(dir, "testcase_CORROTTO-TEST-99.json", SearchOption.AllDirectories))
            File.Delete(path);

        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        var master = state.Master.Operatori.Load().Single(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));

        var origine = new Veicolo { Nome = "My24-Prop" };
        var destinazione = new Veicolo { Nome = "My26-Prop" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Concat(new[] { origine, destinazione }).ToList());
        var componente = new Componente { Nome = "Componente-PROP", VeicoliAssociati = { origine.Nome, destinazione.Nome } };
        state.Master.Componente.Save(state.Master.Componente.Load().Append(componente).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-PROP" }).ToList());

        var tc = new TestCase { Id = "TC-PROP", Componente = "Componente-PROP", RequisitoId = { "R-PROP" }, Veicolo = origine.Nome };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-PROP", Valore = PunteggioConformita.Conforme } } });
        state.TestCases.Save(tc, true);

        return (state, origine, destinazione, componente);
    }

    [Fact]
    public void CellaConVeicoloSenzaTestDiretto_MostraPropagabileNonConfermato_NonIlNumero()
    {
        var (state, _, destinazione, _) = PreparaScenarioPropagazione(_dir);

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };

        var cella = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"];
        Assert.Equal(StatoCellaAudit.PropagabileNonConfermato, cella.Stato);
        Assert.Equal("!", cella.Testo);
        Assert.Null(cella.Punteggio);
        Assert.Single(cella.CandidatiPropagazione);
    }

    [Fact]
    public void Propaga_ComeMaster_LaCellaDiventaValutataConOrigineEIconaDistinta()
    {
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };
        var candidato = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"].CandidatiPropagazione[origine.Nome];
        var attesa = Assert.Single(vm.VeicoliPropagabiliPer(componente.Nome, "R-PROP", candidato));

        vm.Propaga(componente.Nome, "R-PROP", new[] { attesa }, "confermata identica su banco");

        var cella = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"];
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Propagata, cella.Origine);
        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio);
        Assert.StartsWith("↳", cella.Testo);
        Assert.Contains("propagazione", cella.ToolTip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Propaga_SenzaNota_NonVieneSalvata()
    {
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };
        var candidato = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"].CandidatiPropagazione[origine.Nome];
        var attesa = Assert.Single(vm.VeicoliPropagabiliPer(componente.Nome, "R-PROP", candidato));

        vm.Propaga(componente.Nome, "R-PROP", new[] { attesa }, "  ");

        var cella = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"];
        Assert.Equal(StatoCellaAudit.PropagabileNonConfermato, cella.Stato);
    }

    [Fact]
    public void Propaga_SorgenteCambiataDopoAnteprima_FallisceSenzaScrivere()
    {
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };
        var cella = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"];
        var attesa = Assert.Single(vm.VeicoliPropagabiliPer(componente.Nome, "R-PROP", cella.CandidatiPropagazione[origine.Nome]));

        var test = state.TestCases.GetAll().Single(tc => tc.Id == "TC-PROP");
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 2, Data = DateTime.Now.AddMinutes(1), Esito = Esito.NonConforme, Matricola = "V2",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-PROP", Valore = PunteggioConformita.NonConforme } },
        });
        state.TestCases.Save(test, false);

        vm.Propaga(componente.Nome, "R-PROP", new[] { attesa }, "conferma vecchia sorgente");

        Assert.Empty(state.Master.ValutazioniRequisitoEcuVeicolo.Load());
        Assert.NotNull(vm.MessaggioErrore);
        Assert.Equal(StatoCellaAudit.PropagabileNonConfermato,
            vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"].Stato);
    }

    [Fact]
    public void Propaga_RefreshTestConFileIlleggibile_FallisceChiusaSenzaScrivere()
    {
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };
        var cella = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"];
        var attesa = Assert.Single(vm.VeicoliPropagabiliPer(componente.Nome, "R-PROP", cella.CandidatiPropagazione[origine.Nome]));
        var testSorgente = state.TestCases.GetAll().Single(tc => tc.Id == "TC-PROP");
        File.WriteAllText(state.TestCases.PercorsoPer(testSorgente), "{ json incompleto");

        vm.Propaga(componente.Nome, "R-PROP", new[] { attesa }, "conferma con archivio incompleto");

        Assert.Empty(state.Master.ValutazioniRequisitoEcuVeicolo.Load());
        Assert.NotNull(vm.MessaggioErrore);
        Assert.Contains("fotografia", vm.MessaggioErrore, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Propaga_LegacyCompareSulTargetDopoAnteprima_FallisceSenzaSovrascriverlo()
    {
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };
        var cella = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"];
        var attesa = Assert.Single(vm.VeicoliPropagabiliPer(componente.Nome, "R-PROP", cella.CandidatiPropagazione[origine.Nome]));
        var legacy = new RisoluzioneConflittoAudit
        {
            Componente = componente.Nome, RequisitoId = "R-PROP", PunteggioManuale = PunteggioConformita.NonConforme,
            Motivazione = "valore legacy concorrente", UltimaModifica = DateTime.Now.AddMinutes(1), ModificatoDa = "CD",
        };
        state.Master.RisoluzioniConflittoAudit.Save(new List<RisoluzioneConflittoAudit> { legacy });

        vm.Propaga(componente.Nome, "R-PROP", new[] { attesa }, "conferma vecchia destinazione");

        Assert.Empty(state.Master.ValutazioniRequisitoEcuVeicolo.Load());
        Assert.NotNull(vm.MessaggioErrore);
        Assert.Equal(PunteggioConformita.NonConforme,
            Assert.Single(state.Master.RisoluzioniConflittoAudit.Load()).PunteggioManuale);
    }

    [Fact]
    public void VeicoliPropagabiliPer_RestituisceIVeicoliCheCondividonoLaStessaSorgente()
    {
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };
        var candidato = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"].CandidatiPropagazione[origine.Nome];

        var propagabili = vm.VeicoliPropagabiliPer(componente.Nome, "R-PROP", candidato);

        var voce = Assert.Single(propagabili);
        Assert.Equal(destinazione.Nome, voce.Veicolo);
    }

    [Fact]
    public void ImpostaManualeVeicolo_ComeMaster_ConPropagazioneAPiuVeicoli_CompilaEntrambeLeCelle()
    {
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        // Requisito diverso, senza alcun Test: entrambi i veicoli partono "Da testare".
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-MAN-VEICOLO" }).ToList());
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = origine.Nome };

        vm.ImpostaManualeVeicolo(componente.Nome, "R-MAN-VEICOLO", new[] { origine.Nome, destinazione.Nome }, PunteggioConformita.AmpiamenteConforme, "verificato su entrambi", "1.2.3");

        var cellaOrigine = vm.Righe.Single(r => r.RequisitoId == "R-MAN-VEICOLO").Celle["Componente-PROP"];
        Assert.Equal(StatoCellaAudit.Valutato, cellaOrigine.Stato);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Manuale, cellaOrigine.Origine);
        Assert.Equal(PunteggioConformita.AmpiamenteConforme, cellaOrigine.Punteggio);
        Assert.Equal("1.2.3", cellaOrigine.BaselineSw);

        vm.SottoveicoloSelezionato = destinazione.Nome;
        var cellaDestinazione = vm.Righe.Single(r => r.RequisitoId == "R-MAN-VEICOLO").Celle["Componente-PROP"];
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Manuale, cellaDestinazione.Origine);
    }

    [Fact]
    public void ImpostaManualeVeicolo_NonMaster_NonVieneSalvata()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var veicolo = new Veicolo { Nome = "Viewer-Man-Veicolo" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "Componente-VIEWER-VEICOLO", VeicoliAssociati = { veicolo.Nome } }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-VIEWER-VEICOLO" }).ToList());
        var viewer = new Operatore { Nome = "Viewer Veicolo", Ruolo = RuoloOperatore.Viewer, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" };
        state.Master.Operatori.Save(state.Master.Operatori.Load().Append(viewer).ToList());
        Assert.True(state.ImpostaOperatore("Viewer Veicolo", "1234"));

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };
        vm.ImpostaManualeVeicolo("Componente-VIEWER-VEICOLO", "R-VIEWER-VEICOLO", new[] { veicolo.Nome }, PunteggioConformita.Conforme, "tentativo non autorizzato", null);

        Assert.Equal(StatoCellaAudit.DaTestare, vm.Righe.Single(r => r.RequisitoId == "R-VIEWER-VEICOLO").Celle["Componente-VIEWER-VEICOLO"].Stato);
    }

    [Fact]
    public void RimuoviValutazioneVeicolo_ComeMaster_LaCellaTornaAllEsitoAutomatico()
    {
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-RIM-VEICOLO" }).ToList());
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = origine.Nome };
        vm.ImpostaManualeVeicolo(componente.Nome, "R-RIM-VEICOLO", new[] { origine.Nome }, PunteggioConformita.NonConforme, "correzione", null);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Manuale, vm.Righe.Single(r => r.RequisitoId == "R-RIM-VEICOLO").Celle["Componente-PROP"].Origine);

        vm.RimuoviValutazioneVeicolo(componente.Nome, "R-RIM-VEICOLO", origine.Nome);

        var cella = vm.Righe.Single(r => r.RequisitoId == "R-RIM-VEICOLO").Celle["Componente-PROP"];
        Assert.Equal(StatoCellaAudit.DaTestare, cella.Stato);
        Assert.Null(cella.Origine);
    }

    [Fact]
    public void SalvaValutazioneManualeVeicoloSelezionato_NonPropagaAgliAltriVeicoli()
    {
        var state = CreaStatoComeMaster();
        var selezionato = new Veicolo { Nome = "Manuale-Selezionato" };
        var altro = new Veicolo { Nome = "Manuale-Altro" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Concat(new[] { selezionato, altro }).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente
        {
            Nome = "Componente-MANUALE-SOLO-SELEZIONATO",
            VeicoliAssociati = { selezionato.Nome, altro.Nome },
        }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-MANUALE-SOLO-SELEZIONATO" }).ToList());

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = selezionato.Nome };
        vm.SalvaValutazioneManualeVeicoloSelezionato(
            "Componente-MANUALE-SOLO-SELEZIONATO", "R-MANUALE-SOLO-SELEZIONATO",
            PunteggioConformita.NonConforme, "Solo il veicolo selezionato", "baseline-1");

        var valutazioni = state.Master.ValutazioniRequisitoEcuVeicolo.Load();
        var riga = Assert.Single(valutazioni);
        Assert.Equal(selezionato.Nome, riga.Veicolo);
        Assert.Equal("baseline-1", riga.BaselineSw);
        Assert.Equal(PunteggioConformita.NonConforme, riga.Punteggio);
        Assert.DoesNotContain(valutazioni, v => v.Veicolo == altro.Nome);
    }

    [Fact]
    public void RipristinaValoreStorico_CreaNuovoManualeSoloSulVeicoloSelezionato()
    {
        var state = CreaStatoComeMaster();
        var selezionato = new Veicolo { Nome = "Storico-Selezionato" };
        var altro = new Veicolo { Nome = "Storico-Altro" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Concat(new[] { selezionato, altro }).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente
        {
            Nome = "Componente-STORICO", VeicoliAssociati = { selezionato.Nome, altro.Nome },
        }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-STORICO" }).ToList());

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = selezionato.Nome };
        vm.SalvaValutazioneManualeVeicoloSelezionato(
            "Componente-STORICO", "R-STORICO", PunteggioConformita.NonConforme, "valore iniziale", "baseline-1");
        vm.ImpostaManualeVeicolo("Componente-STORICO", "R-STORICO", new[] { selezionato.Nome },
            PunteggioConformita.Conforme, "valore nuovo", "baseline-2");

        var eventoPrecedente = Assert.Single(vm.LeggiStoricoValutazioneCella("Componente-STORICO", "R-STORICO", selezionato.Nome),
            e => e.Nota == "valore iniziale");
        Assert.Equal(OrigineStoricoValutazioneAudit.Manuale, eventoPrecedente.Origine);
        Assert.True(vm.RipristinaValutazioneStorica("Componente-STORICO", "R-STORICO", selezionato.Nome,
            eventoPrecedente.EventId, "ripristinato", sovrascriveValoreVisibile: true, confermaSovrascrittura: true));

        var valutazioni = state.Master.ValutazioniRequisitoEcuVeicolo.Load();
        var riga = Assert.Single(valutazioni);
        Assert.Equal(selezionato.Nome, riga.Veicolo);
        Assert.Equal(PunteggioConformita.NonConforme, riga.Punteggio);
        Assert.Equal("ripristinato", riga.Nota);
        Assert.Equal("baseline-1", riga.BaselineSw);
        Assert.NotEqual(eventoPrecedente.EventId, riga.EventoCorrenteId);
        Assert.DoesNotContain(valutazioni, v => v.Veicolo == altro.Nome);
    }

    [Fact]
    public void StoricoTest_IncludeRevisioniDiTestEliminatoNelCestino()
    {
        var state = CreaStatoComeMaster();
        var veicolo = new Veicolo { Nome = "Storico-Test-Cestino" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "Componente-STORICO-CESTINO", VeicoliAssociati = { veicolo.Nome } }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-STORICO-CESTINO" }).ToList());
        var test = new TestCase
        {
            Id = "TC-STORICO-CESTINO", Componente = "Componente-STORICO-CESTINO", Veicolo = veicolo.Nome,
            RequisitoId = { "R-STORICO-CESTINO" },
        };
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 7, Data = new DateTime(2026, 3, 4, 12, 0, 0), Esito = Esito.Conforme, Matricola = "V",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-STORICO-CESTINO", Valore = PunteggioConformita.Conforme } },
            Dettagli = new DettagliTestSnapshot
            {
                Componente = new EcuSelection(new[] { "Componente-STORICO-CESTINO" }),
                Veicoli = { veicolo.Nome },
            },
        });
        state.TestCases.Save(test, true);
        state.TestCases.SoftDelete(test, "AB");
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };

        var evento = Assert.Single(vm.LeggiStoricoValutazioneCella("Componente-STORICO-CESTINO", "R-STORICO-CESTINO", veicolo.Nome));

        Assert.Equal(OrigineStoricoValutazioneAudit.Test, evento.Origine);
        Assert.Equal("TC-STORICO-CESTINO", evento.TestCaseId);
        Assert.Equal(7, evento.IterazioneTest);
    }

    [Fact]
    public void ApplicazioneManualeMassiva_UsaRigaColonnaVisibiliESoloVeicoloSelezionato()
    {
        var state = CreaStatoComeMaster();
        var selezionato = new Veicolo { Nome = "Bulk-Selezionato" };
        var altro = new Veicolo { Nome = "Bulk-Altro" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Concat(new[] { selezionato, altro }).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Concat(new[]
        {
            new Componente { Nome = "Componente-BULK-A", VeicoliAssociati = { selezionato.Nome, altro.Nome } },
            new Componente { Nome = "Componente-BULK-B", VeicoliAssociati = { selezionato.Nome, altro.Nome } },
        }).ToList());
        var standard = new Requisito { Id = "R-BULK-STANDARD" };
        var extra = new Requisito { Id = "R-BULK-Extra" };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, new[] { selezionato.Nome });
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Concat(new[] { standard, extra }).ToList());

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = selezionato.Nome };
        vm.FiltroTipoRequisito.Opzioni.Single(o => o.Valore == "Extra").Selezionato = true;

        var riga = vm.PreparaApplicazioneManualeAudit(ecuNome: null, requisitoId: extra.Id);
        Assert.Equal(2, riga.Celle.Count);
        Assert.All(riga.Celle, c =>
        {
            Assert.Equal(selezionato.Nome, c.Veicolo);
            Assert.Equal(extra.Id, c.RequisitoId);
        });

        var colonna = vm.PreparaApplicazioneManualeAudit("Componente-BULK-A", requisitoId: null);
        var requisitiVisibiliApplicabili = vm.RigheD4d
            .Where(r => r.Celle.ContainsKey("Componente-BULK-A"))
            .Select(r => r.RequisitoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(requisitiVisibiliApplicabili, colonna.Celle.Select(c => c.RequisitoId).ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.All(colonna.Celle, c =>
        {
            Assert.Equal("Componente-BULK-A", c.Componente);
            Assert.Equal(selezionato.Nome, c.Veicolo);
        });

        Assert.True(vm.ApplicaManualeMassivaAudit(riga, PunteggioConformita.ParzialmenteConforme,
            "nota batch", "baseline-bulk", confermaSovrascritture: false));
        var salvate = state.Master.ValutazioniRequisitoEcuVeicolo.Load();
        Assert.Equal(2, salvate.Count);
        Assert.All(salvate, v => Assert.Equal(selezionato.Nome, v.Veicolo));
        Assert.DoesNotContain(salvate, v => v.Veicolo == altro.Nome);
    }

    [Fact]
    public void ApplicazioneManualeMassiva_TestCreatoSuAltraPostazioneDopoAnteprima_FallisceSenzaScrivere()
    {
        var state = CreaStatoComeMaster();
        var veicolo = new Veicolo { Nome = "Manuale-Remote" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente
        {
            Nome = "Componente-MANUALE-REMOTE", VeicoliAssociati = { veicolo.Nome },
        }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-MANUALE-REMOTE" }).ToList());
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };
        var anteprima = vm.PreparaApplicazioneManualeAudit("Componente-MANUALE-REMOTE", "R-MANUALE-REMOTE");
        Assert.Single(anteprima.Celle);

        // Una seconda istanza repository simula il salvataggio completato dall'altra postazione dopo la
        // preview locale. L'indice/cache del ViewModel resta quello precedente finché il callback non refresh-a.
        var repoAltraPostazione = new TestCaseRepository(state.Paths, deviceIdOverride: "audit-remote-test");
        var testRemoto = new TestCase
        {
            Id = "TC-MANUALE-REMOTE", Componente = "Componente-MANUALE-REMOTE",
            RequisitoId = { "R-MANUALE-REMOTE" }, Veicolo = veicolo.Nome,
        };
        testRemoto.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now.AddMinutes(1), Esito = Esito.NonConforme,
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-MANUALE-REMOTE", Valore = PunteggioConformita.NonConforme } },
        });
        repoAltraPostazione.Save(testRemoto, isNew: true);

        var salvato = vm.ApplicaManualeMassivaAudit(
            anteprima, PunteggioConformita.Conforme, "conferma su fotografia precedente", null,
            confermaSovrascritture: false);

        Assert.False(salvato);
        Assert.Contains("fonti Test", vm.MessaggioErrore, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(state.Master.ValutazioniRequisitoEcuVeicolo.Load());
        var cella = vm.Righe.Single(r => r.RequisitoId == "R-MANUALE-REMOTE").Celle["Componente-MANUALE-REMOTE"];
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.NonConforme, cella.Punteggio);
    }

    [Fact]
    public void ApplicazioneManualeMassiva_RefreshTestIncompleto_FallisceChiusaSenzaScrivere()
    {
        var state = CreaStatoComeMaster();
        var veicolo = new Veicolo { Nome = "Manuale-Refresh-Incompleto" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente
        {
            Nome = "Componente-MANUALE-REFRESH", VeicoliAssociati = { veicolo.Nome },
        }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-MANUALE-REFRESH" }).ToList());
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };
        var anteprima = vm.PreparaApplicazioneManualeAudit("Componente-MANUALE-REFRESH", "R-MANUALE-REFRESH");
        Assert.Single(anteprima.Celle);

        var percorsoIlleggibile = Path.Combine(state.Paths.TestCasesDir, "Componente-REFRESH-INCOMPLETO", "testcase_AUDIT-BROKEN.json");
        Directory.CreateDirectory(Path.GetDirectoryName(percorsoIlleggibile)!);
        File.WriteAllText(percorsoIlleggibile, "{ json incompleto");

        var salvato = vm.ApplicaManualeMassivaAudit(
            anteprima, PunteggioConformita.Conforme, "refresh Test incompleto", null,
            confermaSovrascritture: false);

        Assert.False(salvato);
        Assert.NotNull(vm.MessaggioErrore);
        Assert.Contains("fotografia", vm.MessaggioErrore, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(state.Master.ValutazioniRequisitoEcuVeicolo.Load());
    }

    // Piano 6.3.1 §1, seconda consegna: semantica dei Test SENZA veicolo (dati storici, es. _SENZA_GAMMA) e
    // del KPI aggregato "coppie !" (punto (a), vista indipendente dal sottoveicolo selezionato).

    [Fact]
    public void TestSenzaVeicolo_LaCellaMostraPropagabileNonConfermato_NonDaTestareNeDiretta()
    {
        var state = CreaStatoComeMaster();
        var veicolo = new Veicolo { Nome = "Sottoveicolo-SenzaVeicolo" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "Componente-SV", VeicoliAssociati = { veicolo.Nome } }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-SV" }).ToList());

        // Test storico senza Veicolo/Veicoli (VeicoliEffettivi vuoto): non compare in nessuna cella come
        // Diretto, ma il suo punteggio non deve andare perso — la cella diventa "!" propagabile.
        var tc = new TestCase { Id = "TC-SENZA-VEICOLO", Componente = "Componente-SV", RequisitoId = { "R-SV" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-SV", Valore = PunteggioConformita.Conforme } } });
        state.TestCases.Save(tc, true);

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };

        var cella = vm.Righe.Single(r => r.RequisitoId == "R-SV").Celle["Componente-SV"];
        Assert.Equal(StatoCellaAudit.PropagabileNonConfermato, cella.Stato);
        Assert.Equal("!", cella.Testo);
        Assert.Null(cella.Punteggio);
        var candidato = Assert.Single(cella.CandidatiPropagazione);
        Assert.Equal(ValutazioneRequisitoEcuVeicoloService.SorgenteVeicoloEcuGenerica, candidato.Key);
        Assert.Equal(PunteggioConformita.Conforme, candidato.Value.PunteggioSorgente);
    }

    [Fact]
    public void TestSenzaVeicolo_SiPuoPropagareAUnVeicoloConcreto()
    {
        var state = CreaStatoComeMaster();
        var veicolo = new Veicolo { Nome = "Sottoveicolo-SenzaVeicolo-Propaga" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        var componente = new Componente { Nome = "Componente-SV-PROP", VeicoliAssociati = { veicolo.Nome } };
        state.Master.Componente.Save(state.Master.Componente.Load().Append(componente).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-SV-PROP" }).ToList());
        var tc = new TestCase { Id = "TC-LEGACY", Componente = "Componente-SV-PROP", RequisitoId = { "R-SV-PROP" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-SV-PROP", Valore = PunteggioConformita.ModeratamenteConforme } } });
        state.TestCases.Save(tc, true);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };
        var candidato = vm.Righe.Single(r => r.RequisitoId == "R-SV-PROP").Celle["Componente-SV-PROP"].CandidatiPropagazione[ValutazioneRequisitoEcuVeicoloService.SorgenteVeicoloEcuGenerica];
        var attesa = Assert.Single(vm.VeicoliPropagabiliPer(componente.Nome, "R-SV-PROP", candidato));

        vm.Propaga(componente.Nome, "R-SV-PROP", new[] { attesa }, "confermato dal dato storico _SENZA_GAMMA");

        var cella = vm.Righe.Single(r => r.RequisitoId == "R-SV-PROP").Celle["Componente-SV-PROP"];
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Propagata, cella.Origine);
        Assert.Equal(PunteggioConformita.ModeratamenteConforme, cella.Punteggio);
    }

    [Fact]
    public void KpiCoppiePropagabili_ContaUnaCellaSenzaVeicolo_SenzaAlterareGliAltriTotali()
    {
        var state = CreaStatoComeMaster();
        var totalePropagabiliPrima = new AuditViewModel(state).TotaleCoppiePropagabili;

        var veicolo = new Veicolo { Nome = "Sottoveicolo-Kpi-SenzaVeicolo" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Append(veicolo).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente { Nome = "Componente-KPI-SV", VeicoliAssociati = { veicolo.Nome } }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-KPI-SV" }).ToList());
        var tc = new TestCase { Id = "TC-KPI-SV", Componente = "Componente-KPI-SV", RequisitoId = { "R-KPI-SV" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-KPI-SV", Valore = PunteggioConformita.Conforme } } });
        state.TestCases.Save(tc, true);

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = veicolo.Nome };

        // KPI aggiuntivo: sale di uno per la nuova coppia "!" senza veicolo. Le altre card (valutate/
        // da testare/test creato/conflitti) restano quelle di sempre — nessuna ridefinizione silenziosa.
        Assert.Equal(totalePropagabiliPrima + 1, vm.TotaleCoppiePropagabili);
        Assert.True(vm.HaPropagabili);
    }

    [Fact]
    public void KpiCoppiePropagabili_TestDirettoNormale_NonERegistratoComePropagabile()
    {
        // Regressione (Piano 6.3.1 §1, seconda consegna, punto (a) — "coerente con prima per le celle con
        // test diretti"): un normale Test su un veicolo concreto continua a risultare Diretto/Valutato,
        // MAI conteggiato nel nuovo KPI "coppie !".
        var (state, origine, _, _) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = origine.Nome };

        var cellaDiretta = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle["Componente-PROP"];
        Assert.Equal(StatoCellaAudit.Valutato, cellaDiretta.Stato);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Diretto, cellaDiretta.Origine);
    }

    // --- Piano 6.4.0: Propagazione massiva — visibilità/abilitazione per ruolo, anteprima e conferma ---

    [Fact]
    public void PuoPropagareVeicolo_ComeMaster_ConCellePropagabili_EVero()
    {
        var (state, _, destinazione, _) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };

        Assert.True(vm.EMaster);
        Assert.True(vm.HaCellePropagabiliVeicoloSelezionato);
        Assert.True(vm.PuoPropagareVeicolo);
        Assert.True(vm.EcuHaCellePropagabili("Componente-PROP"));
    }

    [Fact]
    public void PuoPropagareVeicolo_ComeNonMaster_EFalso_AncheConCellePropagabili()
    {
        // Stesso scenario dati (celle "!" presenti), ma senza autenticarsi come Master: RuoloAttivo resta
        // Viewer di default (AppState.RuoloAttivo) — stessa regola di permesso della propagazione singola
        // già esistente (ApriPropagaFlyout, visibile solo per vm.EMaster).
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        var origine = new Veicolo { Nome = "My24-Prop-NM" };
        var destinazione = new Veicolo { Nome = "My26-Prop-NM" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Concat(new[] { origine, destinazione }).ToList());
        var componente = new Componente { Nome = "Componente-PROP-NM", VeicoliAssociati = { origine.Nome, destinazione.Nome } };
        state.Master.Componente.Save(state.Master.Componente.Load().Append(componente).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-PROP-NM" }).ToList());
        var tc = new TestCase { Id = "TC-PROP-NM", Componente = "Componente-PROP-NM", RequisitoId = { "R-PROP-NM" }, Veicolo = origine.Nome };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-PROP-NM", Valore = PunteggioConformita.Conforme } } });
        state.TestCases.Save(tc, true);

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };

        Assert.False(vm.EMaster);
        Assert.True(vm.HaCellePropagabiliVeicoloSelezionato); // la cella "!" c'è comunque, solo il permesso manca
        Assert.False(vm.PuoPropagareVeicolo);
        // EcuHaCellePropagabili riflette solo lo stato delle celle (come HaCellePropagabiliVeicoloSelezionato),
        // non il permesso: il code-behind la combina con vm.EMaster per decidere se mostrare il pulsante.
        Assert.True(vm.EcuHaCellePropagabili("Componente-PROP-NM"));
    }

    [Fact]
    public void HaCellePropagabiliVeicoloSelezionato_SenzaCellePropagabili_EFalso()
    {
        var (state, origine, _, _) = PreparaScenarioPropagazione(_dir);
        // Sul veicolo di ORIGINE (quello con il Test diretto) non c'è nessuna cella "!": la sua unica coppia
        // Componente×Requisito è Diretta.
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = origine.Nome };

        Assert.False(vm.HaCellePropagabiliVeicoloSelezionato);
        Assert.False(vm.PuoPropagareVeicolo);
    }

    [Fact]
    public void AnteprimaPropagazioneMassiva_PerEcu_RestituisceSoloLeCelleDiQuellEcu()
    {
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };

        var anteprima = vm.AnteprimaPropagazioneMassiva(componente.Nome);

        var voce = Assert.Single(anteprima.DaPropagare);
        Assert.Equal(componente.Nome, voce.Componente);
        Assert.Equal("R-PROP", voce.RequisitoId);
        Assert.Equal(origine.Nome, voce.Sorgente.VeicoloSorgente);
        Assert.Empty(anteprima.Conflitti);
    }

    [Fact]
    public void AnteprimaPropagazioneMassiva_PerRequisito_IncludeTutteLeEcuMaSoloLaRigaScelta()
    {
        var state = CreaStatoComeMaster();
        var origine = new Veicolo { Nome = "Veicolo-Propaga-Riga-Origine" };
        var destinazione = new Veicolo { Nome = "Veicolo-Propaga-Riga-Destinazione" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Concat(new[] { origine, destinazione }).ToList());
        var ecuA = new Componente { Nome = "Componente-PROPAGA-RIGA-A", VeicoliAssociati = { origine.Nome, destinazione.Nome } };
        var ecuB = new Componente { Nome = "Componente-PROPAGA-RIGA-B", VeicoliAssociati = { origine.Nome, destinazione.Nome } };
        state.Master.Componente.Save(state.Master.Componente.Load().Concat(new[] { ecuA, ecuB }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Concat(new[]
        {
            new Requisito { Id = "R-PROPAGA-RIGA" },
            new Requisito { Id = "R-ALTRA-RIGA" },
        }).ToList());

        foreach (var componente in new[] { ecuA, ecuB })
        foreach (var requisitoId in new[] { "R-PROPAGA-RIGA", "R-ALTRA-RIGA" })
        {
            var tc = new TestCase
            {
                Id = $"TC-{componente.Nome}-{requisitoId}", Componente = componente.Nome,
                RequisitoId = { requisitoId }, Veicolo = origine.Nome,
            };
            tc.Esecuzioni.Add(new Esecuzione
            {
                Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V-RIGA",
                PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = requisitoId, Valore = PunteggioConformita.Conforme } },
            });
            state.TestCases.Save(tc, true);
        }

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };
        var anteprima = vm.AnteprimaPropagazioneMassiva(ecuNome: null, requisitoId: "R-PROPAGA-RIGA");

        Assert.Equal(new[] { ecuA.Nome, ecuB.Nome }, anteprima.DaPropagare.Select(v => v.Componente).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(anteprima.DaPropagare, voce => Assert.Equal("R-PROPAGA-RIGA", voce.RequisitoId));
        Assert.Empty(anteprima.Conflitti);

        vm.ConfermaPropagazioneMassiva(null, anteprima.DaPropagare, "confermato per la riga scelta");

        Assert.Null(vm.MessaggioErrore);
        Assert.Equal(2, state.Master.ValutazioniRequisitoEcuVeicolo.Load().Count(v =>
            v.Veicolo == destinazione.Nome && v.RequisitoId == "R-PROPAGA-RIGA" && !v.IsArchiviata));
        Assert.Empty(state.Master.ValutazioniRequisitoEcuVeicolo.Load().Where(v =>
            v.Veicolo == destinazione.Nome && v.RequisitoId == "R-ALTRA-RIGA" && !v.IsArchiviata));
        Assert.All(new[] { ecuA.Nome, ecuB.Nome }, componente =>
            Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Propagata, vm.Righe.Single(r => r.RequisitoId == "R-PROPAGA-RIGA").Celle[componente].Origine));
        Assert.All(new[] { ecuA.Nome, ecuB.Nome }, componente =>
            Assert.Equal(StatoCellaAudit.PropagabileNonConfermato, vm.Righe.Single(r => r.RequisitoId == "R-ALTRA-RIGA").Celle[componente].Stato));
        Assert.Equal(4, state.TestCases.GetAll().Count(tc => tc.Veicolo == origine.Nome)); // i Test sorgente restano intatti
    }

    [Fact]
    public void ConfermaPropagazioneMassiva_ComeMaster_LaCellaDiventaValutataPropagata()
    {
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };
        var anteprima = vm.AnteprimaPropagazioneMassiva(componente.Nome);

        vm.ConfermaPropagazioneMassiva(componente.Nome, anteprima.DaPropagare, "propagazione massiva di verifica");

        Assert.True(vm.MessaggioErrore == null, vm.MessaggioErrore);
        var cella = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle[componente.Nome];
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Propagata, cella.Origine);
        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio);
        Assert.False(vm.HaCellePropagabiliVeicoloSelezionato); // non restano più "!" su questo veicolo
        Assert.False(vm.PuoPropagareVeicolo);
        Assert.True(vm.HaMessaggio);
    }

    [Fact]
    public void ConfermaPropagazioneMassiva_SenzaEssereMaster_NonScriveNulla()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        var origine = new Veicolo { Nome = "My24-Prop-NM2" };
        var destinazione = new Veicolo { Nome = "My26-Prop-NM2" };
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Concat(new[] { origine, destinazione }).ToList());
        var componente = new Componente { Nome = "Componente-PROP-NM2", VeicoliAssociati = { origine.Nome, destinazione.Nome } };
        state.Master.Componente.Save(state.Master.Componente.Load().Append(componente).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Append(new Requisito { Id = "R-PROP-NM2" }).ToList());
        var tc = new TestCase { Id = "TC-PROP-NM2", Componente = "Componente-PROP-NM2", RequisitoId = { "R-PROP-NM2" }, Veicolo = origine.Nome };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-PROP-NM2", Valore = PunteggioConformita.Conforme } } });
        state.TestCases.Save(tc, true);

        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };
        Assert.False(vm.EMaster);
        var cellaPrima = vm.Righe.Single(r => r.RequisitoId == "R-PROP-NM2").Celle[componente.Nome];
        Assert.Equal(StatoCellaAudit.PropagabileNonConfermato, cellaPrima.Stato);
        // AnteprimaPropagazioneMassiva non controlla il ruolo (solo lettura): usata qui solo per costruire
        // una voce attesa realistica, la vera barriera di permesso è dentro ConfermaPropagazioneMassiva.
        var celleAttese = vm.AnteprimaPropagazioneMassiva(componente.Nome).DaPropagare;
        Assert.Single(celleAttese);

        vm.ConfermaPropagazioneMassiva(componente.Nome, celleAttese, "tentativo senza permesso");

        var cellaDopo = vm.Righe.Single(r => r.RequisitoId == "R-PROP-NM2").Celle[componente.Nome];
        Assert.Equal(StatoCellaAudit.PropagabileNonConfermato, cellaDopo.Stato); // invariata: nessun permesso, nessuna scrittura
        Assert.Empty(state.Master.ValutazioniRequisitoEcuVeicolo.Load());
    }

    [Fact]
    public void ConfermaPropagazioneMassiva_UnTestDirettoCompareSulVeicoloVFraAnteprimaEConferma_CellaSaltata()
    {
        // Rilievo revisione indipendente 2026-09-26, punto 2: ConfermaPropagazioneMassiva deve rileggere
        // freschi anche i TestCase (non solo le valutazioni salvate) al momento della conferma — altrimenti
        // un Test diretto comparso nel frattempo sul veicolo target non verrebbe visto, e la cella verrebbe
        // propagata anche se ora ha un Diretto che dovrebbe avere sempre la precedenza.
        var (state, origine, destinazione, componente) = PreparaScenarioPropagazione(_dir);
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = destinazione.Nome };
        var anteprima = vm.AnteprimaPropagazioneMassiva(componente.Nome);
        Assert.Single(anteprima.DaPropagare); // "!" su R-PROP prima che compaia un Test diretto su destinazione

        // Fra l'anteprima e la conferma, un Test diretto viene creato/eseguito proprio sul veicolo di
        // destinazione (V) — come se un altro operatore avesse appena testato quella coppia.
        var tcDiretto = new TestCase { Id = "TC-PROP-DIRETTO-SOPRAVVENUTO", Componente = componente.Nome, RequisitoId = { "R-PROP" }, Veicolo = destinazione.Nome };
        tcDiretto.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.NonConforme, Matricola = "V2", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R-PROP", Valore = PunteggioConformita.NonConforme } } });
        state.TestCases.Save(tcDiretto, true);

        vm.ConfermaPropagazioneMassiva(componente.Nome, anteprima.DaPropagare, "propagazione dopo il Test sopravvenuto");

        // La cella ora è Diretta (il nuovo Test ha sempre precedenza): la propagazione massiva NON l'ha
        // sovrascritta con il valore propagato dell'anteprima originale.
        var cella = vm.Righe.Single(r => r.RequisitoId == "R-PROP").Celle[componente.Nome];
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Diretto, cella.Origine);
        Assert.Equal(PunteggioConformita.NonConforme, cella.Punteggio);
        Assert.Empty(state.Master.ValutazioniRequisitoEcuVeicolo.Load()); // nessuna riga Propagata scritta
    }
}
