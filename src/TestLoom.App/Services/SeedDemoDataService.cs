using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Genera un set esteso di dati fittizi per il testing end-to-end (§10 + Addendum §17): più componente/veicoli,
/// tutti i tipi di test, ruoli/PIN/gamma veicolari, e i casi limite discussi nella spec (iterazioni/revisioni
/// multiple, test correlati, NC aperte/chiuse sia auto che manuali, requisito archiviato ancora referenziato,
/// componente archiviata con test ancora modificabile, file corrotto, test esplorativo senza classificazione, lock
/// master file simulato, escalation banco->veicolo, UnitLogger sempre collegato, assegnazione con eccezione di
/// categoria). Tutti i nomi sono chiaramente etichettati come fittizi (mai nomi plausibili).
/// Riusa i repository/servizi reali cosi' i dati generati passano dagli stessi percorsi di codice dell'app.
/// </summary>
public static class SeedDemoDataService
{
    private const string DisposableSeedMarker = ".testloom-disposable-seed";
    // Operatore Prova A = Master, B/C = Standard (scoped a gamme diverse), D = Viewer.
    private static readonly string[] Operatori = { "Operatore Prova A", "Operatore Prova B", "Operatore Prova C", "Operatore Prova D" };
    private static readonly string[] Componente = { "Componente-TEST-BCM", "Componente-TEST-SGW", "Componente-TEST-EDC", "Componente-TEST-ADA", "Componente-TEST-TCU", "Componente-TEST-ABS", "Componente-TEST-HVAC" };

    // Frasi variate per Descrizione (Addendum, feedback QA: prima sempre lo stesso "Test fittizio #N", niente
    // che assomigliasse a un motivo di lavoro reale) — combinate con Funzionalità/componente/ambiente per un set
    // demo che si legge come test veri, non segnaposto ripetuti.
    private static readonly string[] TemplateDescrizioni =
    {
        "Verifica \"{0}\" su {1} in ambiente {2}, richiesta dopo aggiornamento della strategia diagnostica.",
        "Controllo di \"{0}\" su {1}: replica di una segnalazione riscontrata in campo su {2}.",
        "Prova di regressione per \"{0}\" su {1} dopo modifica del software applicativo.",
        "Validazione \"{0}\" su {1} per il rilascio della nuova baseline, ambiente {2}.",
        "Ricontrollo \"{0}\" su {1} a seguito di intervento sul cablaggio/{2}.",
        "Prima verifica di \"{0}\" su {1}: nessun precedente in archivio per questa combinazione.",
        "Estensione della copertura di \"{0}\" su {1}, richiesta dal team qualità per {2}.",
        "Ripetizione di \"{0}\" su {1} con parametri di rete diversi rispetto all'ultima baseline.",
    };
    private static readonly string[] Gamme = { "LCV Diesel", "MCV Diesel", "HCV Diesel", "LCV BEV" };
    // Sottoveicoli: modello + MY, uno per gamma (nomi chiaramente fittizi pur ispirati alla tassonomia reale).
    private static readonly (string Nome, string Gamma)[] Sottoveicoli =
    {
        ("Sottoveicolo-Test-Daily MY24", "LCV Diesel"),
        ("Sottoveicolo-Test-Medium MY24", "MCV Diesel"),
        ("Sottoveicolo-Test-Alpha MY24", "HCV Diesel"),
        ("Sottoveicolo-Test-Daily eDaily MY24", "LCV BEV"),
    };
    // Banchi di integrazione veicolare: stessa tassonomia dei Sottoveicoli sopra, modellati come Veicolo con
    // Banchi e veicoli condividono lo stesso elenco (Addendum redesign 2026-08): il tipo di test descrive
    // il contesto banco/veicolo, senza una categoria duplicata sul dato master.
    private static readonly (string Nome, string Gamma)[] Banchi =
    {
        ("Banco-Test-Daily MY24", "LCV Diesel"),
        ("Banco-Test-Medium MY24", "MCV Diesel"),
        ("Banco-Test-Alpha MY24", "HCV Diesel"),
    };
    private static readonly string[] Strumenti = { "ValidationTool", "CaptureTool", "UnitLogger", "Breakout Box", "Guasto Injection" };

    // Campi per strumento (Addendum redesign 2026-08, revisione feedback QA): ValidationTool/UnitLogger sono la migrazione
    // esatta dei campi prima hardcoded (CampiSpecifici/IdentificativiUdt); CaptureTool è nuovo, coerente con le
    // funzioni reali dello strumento (config bus, database segnali, script CAPL, trace). I nomi qui devono
    // combaciare esattamente con quelli usati per valorizzare ValoriStrumenti/ValoriStrumentiTest più sotto:
    // la chiave è "NomeStrumento::NomeCampo".
    private static readonly string[] CampiVenus =
    {
        "Comando (Service)", "ID/Sub-funzione", "Risposta attesa/ottenuta", "ADA", "SGW", "Componente Status",
        "Diagnostic Session", "Description ID"
    };
    // guasto non è il punto principale delle prove: strumento separato e selezionabile a parte, non un
    // sotto-gruppo dentro ValidationTool, perché si possono usare più strumenti insieme (Addendum redesign 2026-08,
    // seconda revisione feedback QA).
    private static readonly string[] CampiDtcInjection =
    {
        "Guasto - Operazione", "Guasto - Stato chiave/nottolino", "Guasto - Scenario", "Guasto - Transizioni",
        "Guasto - Atteso", "Guasto - Parametri correlati"
    };
    // Azione a veicolo/PIN: strumento fisico a sé (breakout box), non parte della diagnosi UDS di ValidationTool.
    private static readonly string[] CampiBreakoutBox =
    {
        "Azione veicolo", "PIN", "Segnale inviato", "Segnali letti", "Risultato atteso", "Stato veicolo",
        "Reazione osservata"
    };
    private static readonly string[] CampiUdt =
    {
        "Nome pacchetto", "Versione pacchetto", "Licenza", "Versione UnitLogger", "Lingua", "Pacchetto",
        "Versione Pacchetto"
    };
    // Km/ore/usura: strumento a sé, non parte del semplice UnitLogger diagnostico (feedback utente 2026-08-18,
    // punto 1 — "non devono comparire su UnitLogger semplice"), preselezionato solo su UnitLogger Performance.
    private static readonly string[] CampiComponenteUsato =
    {
        "Km percorsi", "Ore utilizzo", "Data primo utilizzo", "Storia manutenzione", "Note componente"
    };

    private static void SetCampo(Dictionary<string, string> valori, string strumento, string campo, string? valore)
    {
        if (string.IsNullOrEmpty(valore)) return;
        valori[$"{strumento}::{campo}"] = valore;
    }

    public static void Genera(string cartella)
    {
        // Ripulisce prima di rigenerare (bug trovato in collaudo live 2026-08-17): i master data qui sotto
        // sovrascrivono già il proprio file (es. gammaRepo.Save sostituisce tutto gamma.json), ma i test case
        // sono file singoli in una cartella — senza questa pulizia, rilanciare il seed su una cartella già
        // popolata da un giro precedente si limitava ad ACCUMULARE test vecchi sopra quelli nuovi invece di
        // ripartire da zero, coerente con lo scopo dichiarato di questo comando ("genera dati fittizi per il
        // testing end-to-end"). Sicuro solo perché l'unico chiamante è il flag CLI nascosto --seed-demo, mai
        // invocato automaticamente su una cartella condivisa reale.
        cartella = PreparaCartellaDisposable(cartella);
        GeneraCompletoCore(cartella, includiCasiQa: true, includiTestERequisiti: true);
        ScriviMarkerDisposable(cartella);
    }

    /// <summary>Rigenera da zero il dataset destinato a demo e collaudi manuali. A differenza del seed QA
    /// non aggiunge file corrotti o lock simulati: ogni elemento mostrato dall'app è utilizzabile.</summary>
    public static void GeneraDemoPulita(string cartella)
    {
        cartella = PreparaCartellaDisposable(cartella);
        GeneraCompletoCore(cartella, includiCasiQa: false, includiTestERequisiti: true);
        ScriviMarkerDisposable(cartella);
    }

    private static string PreparaCartellaDisposable(string cartella)
    {
        if (string.IsNullOrWhiteSpace(cartella))
            throw new ArgumentException("La cartella del seed non può essere vuota.", nameof(cartella));

        var percorso = Path.GetFullPath(cartella);
        var radiceVolume = Path.GetPathRoot(percorso);
        if (string.Equals(percorso.TrimEnd(Path.DirectorySeparatorChar),
                radiceVolume?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La radice di un volume non può essere usata per il seed demo.");

        if (!Directory.Exists(percorso)) return percorso;
        if (!Directory.EnumerateFileSystemEntries(percorso).Any()) return percorso;

        var marker = Path.Combine(percorso, DisposableSeedMarker);
        if (!File.Exists(marker))
            throw new InvalidOperationException(
                $"La cartella '{percorso}' contiene dati e non è marcata come disposable: nessun file è stato modificato.");

        Directory.Delete(percorso, recursive: true);
        return percorso;
    }

    private static void ScriviMarkerDisposable(string cartella)
    {
        Directory.CreateDirectory(cartella);
        File.WriteAllText(Path.Combine(cartella, DisposableSeedMarker),
            "Cartella generata da TestLoom per test/demo: può essere rigenerata integralmente.");
    }

    /// <summary>Popola una cartella realmente vuota con il dataset dimostrativo completo, senza i due
    /// artefatti volutamente guasti del seed QA (JSON corrotto e lock simulato). Non elimina mai contenuti:
    /// la verifica viene ripetuta qui, immediatamente prima di qualsiasi scrittura.</summary>
    public static void GeneraCompletoPerPrimoAvvio(string cartella)
    {
        VerificaCartellaVuota(cartella);
        GeneraCompletoCore(cartella, includiCasiQa: false, includiTestERequisiti: true);
    }

    /// <summary>Inizializzazione senza storico operativo: carica tutti i dati di configurazione demo
    /// (gamme, veicoli, componente, funzionalità/categorie, strumenti e relativi campi, template e cataloghi
    /// campi), ma lascia completamente vuoti Test, Requisiti e Operatori.</summary>
    public static void GeneraConfigurazionePerPrimoAvvio(string cartella)
    {
        VerificaCartellaVuota(cartella);
        GeneraCompletoCore(cartella, includiCasiQa: false, includiTestERequisiti: false);
    }

    private static void VerificaCartellaVuota(string cartella)
    {
        if (!Directory.Exists(cartella))
            throw new DirectoryNotFoundException("La cartella selezionata non esiste.");
        if (Directory.EnumerateFileSystemEntries(cartella).Any())
            throw new InvalidOperationException("I dati demo possono essere caricati solo in una cartella completamente vuota.");
    }

    private static void GeneraCompletoCore(string cartella, bool includiCasiQa, bool includiTestERequisiti)
    {

        var paths = new DataPaths(cartella);
        // Il seed opera soltanto su una cartella vuota/disposable: pubblica il gate prima dei
        // dati del nuovo schema. Non usare AppState/Applica (che inizializzerebbero altro storage).
        Directory.CreateDirectory(paths.Root);
        JsonIo.WriteAtomic(paths.VersionManifestFile, new VersionManifest(), overwrite: false);
        CompatibilitaVersioneService.EnsureTestUidSchema(paths);
        paths.EnsureStructure();

        var rnd = new Random(42);

        // ---- Master data ----
        var gammaRepo = new MasterDataRepository<Gamma>(paths.GammaFile);
        gammaRepo.Save(Gamme.Select(g => new Gamma { Nome = g, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" }).ToList());

        var veicoliRepo = new MasterDataRepository<Veicolo>(paths.VeicoliFile);
        var veicoliList = Sottoveicoli.Select(v => new Veicolo { Nome = v.Nome, GammaNome = v.Gamma, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" }).ToList();
        veicoliList.AddRange(Banchi.Select(b => new Veicolo { Nome = b.Nome, GammaNome = b.Gamma, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" }));
        veicoliList.Add(new Veicolo { Nome = "Sottoveicolo-Test-NonCategorizzato", GammaNome = "", UltimaModifica = DateTime.Now, ModificatoDa = "Setup" }); // caso: migrazione non bloccante, gamma non ancora assegnata
        veicoliRepo.Save(veicoliList);

        var strumentiRepo = new MasterDataRepository<StrumentoDiagnostico>(paths.StrumentiFile);
        strumentiRepo.Save(new List<StrumentoDiagnostico>
        {
            new()
            {
                Nome = "ValidationTool", UltimaModifica = DateTime.Now, ModificatoDa = "Setup",
                Tipo = TipoStrumento.Supporto,
                Campi = CampiVenus.Select(c => new DefinizioneCampoStrumento { Nome = c, Tipo = TipoCampoStrumento.Testo }).ToList()
            },
            new()
            {
                Nome = "Breakout Box", UltimaModifica = DateTime.Now, ModificatoDa = "Setup",
                Tipo = TipoStrumento.Supporto,
                Campi = CampiBreakoutBox.Select(c => new DefinizioneCampoStrumento { Nome = c, Tipo = TipoCampoStrumento.Testo }).ToList()
            },
            new()
            {
                Nome = "Guasto Injection", UltimaModifica = DateTime.Now, ModificatoDa = "Setup",
                Tipo = TipoStrumento.Comportamento,
                Campi = CampiDtcInjection.Select(c => new DefinizioneCampoStrumento { Nome = c, Tipo = TipoCampoStrumento.Testo }).ToList()
            },
            new()
            {
                Nome = "UnitLogger", UltimaModifica = DateTime.Now, ModificatoDa = "Setup",
                Tipo = TipoStrumento.DaTestare,
                // Dimostra i 2 ambiti rimasti dopo lo scorporo di "Componente Usato" (feedback utente
                // 2026-08-18, punto 1): Licenza/Versione UnitLogger non cambiano mai per il test (PerTest), il resto
                // varia liberamente operazione per operazione (PerOperazione, default).
                Campi = CampiUdt.Select(c => new DefinizioneCampoStrumento
                {
                    Nome = c,
                    Tipo = TipoCampoStrumento.Testo,
                    Ambito = c is "Licenza" or "Versione UnitLogger" ? TipoAmbitoCampo.PerTest : TipoAmbitoCampo.PerOperazione
                }).ToList()
            },
            new()
            {
                Nome = "Componente Usato", UltimaModifica = DateTime.Now, ModificatoDa = "Setup",
                Tipo = TipoStrumento.Supporto,
                // Km percorsi/Ore utilizzo/Storia manutenzione restano stabili dentro la stessa revisione ma
                // possono aggiornarsi alla successiva (PerRevisione); Data primo utilizzo/Note componente
                // variano operazione per operazione (PerOperazione, default) — stesso schema di prima, solo
                // spostato su uno strumento dedicato invece di vivere dentro UnitLogger.
                Campi = CampiComponenteUsato.Select(c => new DefinizioneCampoStrumento
                {
                    Nome = c,
                    Tipo = c is "Km percorsi" or "Ore utilizzo" ? TipoCampoStrumento.Numero : TipoCampoStrumento.Testo,
                    Ambito = c is "Km percorsi" or "Ore utilizzo" or "Storia manutenzione" ? TipoAmbitoCampo.PerRevisione : TipoAmbitoCampo.PerOperazione
                }).ToList()
            },
            new()
            {
                Nome = "CaptureTool", UltimaModifica = DateTime.Now, ModificatoDa = "Setup",
                Tipo = TipoStrumento.Supporto,
                Campi = new List<DefinizioneCampoStrumento>
                {
                    new() { Nome = "File trace (.blf/.asc)", Tipo = TipoCampoStrumento.Testo },
                    new() { Nome = "Configurazione bus", Tipo = TipoCampoStrumento.Lista, OpzioniLista = new() { "CAN 500 kbit/s", "CAN-FD 2 Mbit/s", "CAN 250 kbit/s", "LIN 19.2 kbit/s" } },
                    new() { Nome = "File database segnali (.dbc)", Tipo = TipoCampoStrumento.Testo },
                    new() { Nome = "ID/segnali monitorati", Tipo = TipoCampoStrumento.Testo },
                    new() { Nome = "Script CAPL usato", Tipo = TipoCampoStrumento.Testo },
                    new() { Nome = "Simulazione/iniezione messaggi attiva", Tipo = TipoCampoStrumento.Checkbox },
                }
            }
        });

        // Ad ogni operatore viene generato anche un codice di recupero (Addendum redesign 2026-08): nel seed non
        // serve mostrarlo/salvarlo da nessuna parte, esiste solo per popolare RecoveryCodeHash realisticamente.
        var operatoriRepo = new MasterDataRepository<Operatore>(paths.OperatoriFile);
        if (includiTestERequisiti)
        {
            var operatoriSeed = new List<Operatore>
            {
                new() { Nome = Operatori[0], Iniziali = IdGenerator.IniziliDa(Operatori[0]), PinHash = PinService.Hash("1234"), RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()), RichiedeCambioPin = true, Ruolo = RuoloOperatore.Master, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" },
                new() { Nome = Operatori[1], Iniziali = IdGenerator.IniziliDa(Operatori[1]), PinHash = PinService.Hash("1234"), RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()), RichiedeCambioPin = true, Ruolo = RuoloOperatore.Standard, GammeAssegnate = new List<string> { "LCV Diesel", "LCV BEV" }, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" },
                new() { Nome = Operatori[2], Iniziali = IdGenerator.IniziliDa(Operatori[2]), PinHash = PinService.Hash("1234"), RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()), RichiedeCambioPin = true, Ruolo = RuoloOperatore.Standard, GammeAssegnate = new List<string> { "MCV Diesel", "HCV Diesel" }, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" },
                new() { Nome = Operatori[3], Iniziali = IdGenerator.IniziliDa(Operatori[3]), PinHash = PinService.Hash("1234"), RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()), RichiedeCambioPin = true, Ruolo = RuoloOperatore.Viewer, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" },
            };
            // Sigillati subito (piano 6.4.2, §4b): senza, il Master demo diventerebbe Viewer al primo accesso
            // (ruolo non sigillato -> RuoloEffettivo Viewer), rompendo il flusso demo reale, non solo i test.
            foreach (var op in operatoriSeed) RuoloSigilloService.Sigilla(op);
            operatoriRepo.Save(operatoriSeed);
        }

        var ecuRepo = new MasterDataRepository<Componente>(paths.EcuFile);
        // Ogni componente montata su un sottoinsieme di sottoveicoli (Addendum redesign 2026-08, revisione feedback
        // QA): dato fittizio ma plausibile, così il picker "Veicoli associati" non parte mai vuoto in demo.
        var ecuList = Componente.Select((n, i) => new Models.Componente
        {
            Nome = n, UltimaModifica = DateTime.Now, ModificatoDa = "Setup",
            VeicoliAssociati = Sottoveicoli.Where((_, si) => si % Componente.Length == i || si == 0).Select(s => s.Nome).Distinct().ToList()
        }).ToList();
        ecuList.Add(new Models.Componente { Nome = "Componente-TEST-LEGACY", Archiviata = true, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" }); // caso: componente archiviata
        ecuRepo.Save(ecuList);

        var funzionalitaRepo = new MasterDataRepository<Funzionalita>(paths.FunzionalitaFile);
        var funzionalita = GeneraFunzionalita();
        funzionalitaRepo.Save(funzionalita);

        var categorieRepo = new MasterDataRepository<Categoria>(paths.CategorieFile);
        categorieRepo.Save(funzionalita.SelectMany(f => f.Categorie).Distinct()
            .Select(c => new Categoria { Nome = c, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" }).ToList());

        // Template dei tipi di test (Addendum redesign 2026-08, punto 2): i 4 standard automotive, stessi Uid
        // riusati sotto al posto del vecchio enum fisso TipoTest.
        var testTemplateRepo = new MasterDataRepository<TestTemplate>(paths.TestTemplateFile);
        var templates = TestTemplateDefaults.Seed();
        templates.Add(GeneraTemplateChangeRequest());
        // I campi di sistema demo (Progetto / Release obiettivo) appartengono solo al dataset completo:
        // la sola configurazione del primo avvio non li deve creare né referenziare (decisione utente).
        if (includiTestERequisiti)
        {
            foreach (var templateTest in templates.Where(t => !t.IsChangeRequest))
            {
                templateTest.Sezioni[0].Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = "progetto-demo", Nome = "Progetto", Ordine = 100 });
                templateTest.Sezioni[0].Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = "release-demo", Nome = "Release obiettivo", Ordine = 101 });
            }
        }
        testTemplateRepo.Save(templates);
        CatalogMigrationService.RegistraDopoSeed(paths);
        new MasterDataRepository<CampoSistemaDefinizione>(paths.CampiSistemaPianificazioneFile).Save(CampoSistemaDefinizioneSeed.SeedPianificazione());
        var campiDettagli = CampoSistemaDefinizioneSeed.SeedDettagli();
        if (includiTestERequisiti)
        {
            campiDettagli.Add(new CampoSistemaDefinizione { Chiave = "progetto-demo", Etichetta = "Progetto", Gruppo = GruppoCampoSistema.Dettagli, Tipo = TipoCampoStrumento.Lista, OpzioniLista = new() { "Phoenix", "Orion", "Atlas" }, UsabileComeFiltro = true, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" });
            campiDettagli.Add(new CampoSistemaDefinizione { Chiave = "release-demo", Etichetta = "Release obiettivo", Gruppo = GruppoCampoSistema.Dettagli, Tipo = TipoCampoStrumento.Lista, OpzioniLista = new() { "R26.3", "R26.4", "R27.1" }, UsabileComeFiltro = true, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" });
        }
        new MasterDataRepository<CampoSistemaDefinizione>(paths.CampiSistemaDettagliFile).Save(campiDettagli);

        if (!includiTestERequisiti) return;

        var requisiti = GeneraRequisiti();
        var requisitiRepo = new MasterDataRepository<Requisito>(paths.RequisitiFile);
        requisitiRepo.Save(requisiti);
        var bancoUid = templates.First(t => t.Nome == "Validazione Banco").Uid;
        var veicoloUid = templates.First(t => t.Nome == "Validazione Veicolo").Uid;
        var udtUid = templates.First(t => t.Nome == "UnitLogger").Uid;
        var udtPerfUid = templates.First(t => t.Nome == "UnitLogger Performance").Uid;
        var nomeTemplateVeicolo = templates.First(t => t.Nome == "Validazione Veicolo").Nome;

        // ---- Test case + NC ----
        var tcRepo = new TestCaseRepository(paths);
        var ncRepo = new NonConformitaRepository(paths);
        tcRepo.RefreshIndex();
        ncRepo.RefreshIndex();

        void SalvaDemo(TestCase test, bool isNew)
        {
            // Lo snapshot va fissato PRIMA della prima persistenza. Aggiungerlo in una
            // passata successiva riscriverebbe le revisioni storiche già immutabili.
            if (isNew)
            {
                foreach (var revisione in test.Esecuzioni)
                    revisione.Dettagli ??= new DettagliTestSnapshot
                    {
                        Componente = new EcuSelection(test.Componente), Veicolo = test.Veicolo,
                        Veicoli = test.VeicoliEffettivi.ToList(), TestTemplateUid = test.TestTemplateUid,
                        TestTemplateNome = templates.FirstOrDefault(t => t.Uid == test.TestTemplateUid)?.Nome ?? "",
                        Funzionalita = test.Funzionalita.ToList(), StrumentiUsati = test.StrumentiUsati.ToList(),
                        DescrizioneBreve = test.DescrizioneBreve, Descrizione = test.Descrizione,
                        DescrizioneProblema = test.DescrizioneProblema,
                        CampiCustom = new Dictionary<string, string>(test.CampiCustom),
                        ValoriStrumentiTest = new Dictionary<string, string>(test.ValoriStrumentiTest)
                    };
            }
            NormalizzaOrariInesistenti(test);
            tcRepo.Save(test, isNew);
        }

        var tuttiGliId = new List<string>();
        string NuovoId(string componente, string operatore, DateTime data)
        {
            var id = IdGenerator.Genera(tuttiGliId, IdGenerator.IniziliDa(operatore));
            tuttiGliId.Add(id);
            return id;
        }

        TestCase? primoBancoConEscalation = null;
        TestCase? primoBancoPerUdt = null;

        int contatore = 0;
        for (int i = 0; i < 160; i++)
        {
            contatore++;
            var componente = Componente[rnd.Next(Componente.Length)];
            var ecuSelezionate = new EcuSelection(new[] { componente });
            if (i % 5 == 0)
            {
                var secondaEcu = Componente[(Array.IndexOf(Componente, componente) + 1 + i) % Componente.Length];
                ecuSelezionate.Aggiungi(new[] { secondaEcu });
            }
            var operatore = Operatori[rnd.Next(3)]; // solo i primi 3 (Master + 2 Standard) creano test, il Viewer no
            var isBanco = rnd.Next(0, 2) == 0; // Validazione Banco/Veicolo dal giro casuale; UnitLogger e UnitLogger Performance generati a parte sotto (sempre collegati)
            // Banco pesca dal pool "banco integrazione" (Addendum redesign 2026-08 — stesso identico modello di
            // un veicolo, solo categoria diversa), Veicolo dal pool "reale": stesso principio applicato in
            // TestCaseEditorViewModel.VeicoliDisponibili.
            var sottoveicolo = isBanco
                ? Banchi[rnd.Next(Banchi.Length)].Nome
                : Sottoveicoli[rnd.Next(Sottoveicoli.Length)].Nome;
            var veicoliSelezionati = new List<string> { sottoveicolo };
            if (i % 6 == 0)
            {
                var pool = isBanco ? Banchi : Sottoveicoli;
                var gammaCorrente = pool.First(v => v.Nome == sottoveicolo).Gamma;
                var alternativa = pool.FirstOrDefault(v => v.Gamma != gammaCorrente);
                if (!string.IsNullOrWhiteSpace(alternativa.Nome)) veicoliSelezionati.Add(alternativa.Nome);
            }
            var dataCreazione = DateTime.Now.AddDays(-rnd.Next(1, 240));
            var req = requisiti[rnd.Next(requisiti.Count)];

            // Funzionalità collegate (Addendum, feedback QA: prima mai assegnate nel seed, la sezione
            // "Funzionalità collegate" del form restava sempre vuota nei dati demo) — una principale sempre,
            // una seconda su una quota di casi, per mostrare anche il caso multiplo.
            var fnzPrincipale = funzionalita[rnd.Next(funzionalita.Count)];
            var funzionalitaTc = new List<string> { fnzPrincipale.Nome };
            if (rnd.NextDouble() < 0.25)
            {
                var fnzSecondaria = funzionalita[rnd.Next(funzionalita.Count)];
                if (fnzSecondaria.Nome != fnzPrincipale.Nome) funzionalitaTc.Add(fnzSecondaria.Nome);
            }
            var ambienteTesto = isBanco ? "banco integrazione" : "veicolo";
            var descrizioneCompleta = string.Format(TemplateDescrizioni[rnd.Next(TemplateDescrizioni.Length)], fnzPrincipale.Nome, componente, ambienteTesto);

            var tc = new TestCase
            {
                Id = NuovoId(componente, operatore, dataCreazione),
                DataCreazione = dataCreazione,
                UltimaModifica = dataCreazione,
                ModificatoDa = operatore,
                CreatoDa = operatore,
                DataAssegnazione = dataCreazione,
                Componente = ecuSelezionate,
                Veicolo = sottoveicolo,
                Veicoli = veicoliSelezionati,
                TestTemplateUid = isBanco ? bancoUid : veicoloUid,
                RequisitoId = new List<string> { req.Id },
                Funzionalita = funzionalitaTc,
                DescrizioneBreve = $"{fnzPrincipale.Nome} · {ecuSelezionate}",
                Descrizione = descrizioneCompleta,
                StrumentiUsati = isBanco ? new List<string> { "ValidationTool", "CaptureTool" } : new List<string> { "ValidationTool", "Breakout Box", "CaptureTool" },
                Assegnatario = operatore,
                Impegno = (ImpegnoStimato)(i % Enum.GetValues<ImpegnoStimato>().Length),
                Scadenza = dataCreazione.AddDays(30 + i % 45),
                CampiCustom = new Dictionary<string, string>
                {
                    ["progetto-demo"] = new[] { "Phoenix", "Orion", "Atlas" }[i % 3],
                    ["release-demo"] = new[] { "R26.3", "R26.4", "R27.1" }[(i / 3) % 3]
                }
            };

            // alcuni test vengono assegnati a uno slave (Addendum §17.1), qualche volta fuori dalla propria gamma (§17.3)
            if (rnd.NextDouble() < 0.35)
            {
                var assegnatario = Operatori[1 + rnd.Next(2)];
                tc.Assegnatario = assegnatario;
                var gammaSottoveicolo = Sottoveicoli.Concat(Banchi).First(v => v.Nome == sottoveicolo).Gamma;
                var opAssegnato = operatoriRepo.Load().First(o => o.Nome == assegnatario);
                if (opAssegnato.GammeAssegnate.Count > 0 && !opAssegnato.GammeAssegnate.Contains(gammaSottoveicolo))
                    tc.FuoriCategoria = true;
            }

            var numEsecuzioni = rnd.Next(1, 4);
            var esitoFinale = SceglEsitoFinale(rnd, i);
            for (int e = 0; e < numEsecuzioni; e++)
            {
                var esito = e == numEsecuzioni - 1 ? esitoFinale : Esito.NonConforme;
                var esecuzione = new Esecuzione
                {
                    Iterazione = e + 1,
                    Data = dataCreazione.AddDays(e * 3),
                    Operatore = operatore,
                    Esito = esito,
                    Note = $"Esecuzione fittizia {e + 1}.",
                    Matricola = $"VF1TEST{contatore:D4}{e}",
                    BaselineSw = $"{rnd.Next(1, 3)}.{rnd.Next(0, 9)}.{rnd.Next(0, 9)}",
                    BaselineHw = $"Rev{rnd.Next(1, 4)}"
                };

                // La condizione principale porta l'esito "vero" dell'iterazione. Su una quota di casi se ne
                // aggiunge una seconda, sempre Conforme (quindi non può mai vincere sulla prima nel calcolo
                // "la condizione peggiore vince"): dimostra il caso reale che ha motivato la funzionalità —
                // stesso servizio, condizioni diverse, non tutte con lo stesso esito — senza alterare l'esito
                // atteso dell'esecuzione su cui contano già KPI/dashboard/report del resto del seed.
                var principale = new Condizione { Esito = esito, Componente = new EcuSelection(ecuSelezionate), Funzionalita = fnzPrincipale.Nome, Nota = $"Operazione principale, test fittizio #{contatore}." };
                if (isBanco)
                {
                    SetCampo(principale.ValoriStrumenti, "ValidationTool", "Comando (Service)", "22");
                    SetCampo(principale.ValoriStrumenti, "ValidationTool", "ID/Sub-funzione", "F1 89");
                    SetCampo(principale.ValoriStrumenti, "ValidationTool", "Risposta attesa/ottenuta", "62 F1 89 ...");
                    SetCampo(principale.ValoriStrumenti, "ValidationTool", "ADA", "NO");
                    SetCampo(principale.ValoriStrumenti, "ValidationTool", "SGW", "Closed");
                    SetCampo(principale.ValoriStrumenti, "ValidationTool", "Componente Status", "Normal");
                    SetCampo(principale.ValoriStrumenti, "ValidationTool", "Diagnostic Session", "Default");
                    SetCampo(principale.ValoriStrumenti, "ValidationTool", "Description ID", $"DID-{contatore:D4}");
                    if (rnd.NextDouble() < 0.3)
                    {
                        if (!tc.StrumentiUsati.Contains("Guasto Injection")) tc.StrumentiUsati.Add("Guasto Injection");
                        SetCampo(principale.ValoriStrumenti, "Guasto Injection", "Guasto - Operazione", "Iniezione guasto simulato");
                        SetCampo(principale.ValoriStrumenti, "Guasto Injection", "Guasto - Stato chiave/nottolino", "Chiave ON");
                        SetCampo(principale.ValoriStrumenti, "Guasto Injection", "Guasto - Scenario", "Scenario di prova");
                        SetCampo(principale.ValoriStrumenti, "Guasto Injection", "Guasto - Atteso", "Guasto atteso di prova");
                        SetCampo(principale.ValoriStrumenti, "Guasto Injection", "Guasto - Transizioni", "Active → Stored → Cleared");
                        SetCampo(principale.ValoriStrumenti, "Guasto Injection", "Guasto - Parametri correlati", "RPM, tensione batteria, stato rete");
                    }
                }
                else
                {
                    SetCampo(principale.ValoriStrumenti, "Breakout Box", "Azione veicolo", "Azione fittizia su veicolo");
                    SetCampo(principale.ValoriStrumenti, "Breakout Box", "PIN", $"PIN {rnd.Next(1, 40)}");
                    SetCampo(principale.ValoriStrumenti, "Breakout Box", "Segnale inviato", "Segnale di prova inviato");
                    SetCampo(principale.ValoriStrumenti, "Breakout Box", "Segnali letti", "Segnale di prova letto");
                    SetCampo(principale.ValoriStrumenti, "Breakout Box", "Risultato atteso", "Risultato atteso fittizio");
                    SetCampo(principale.ValoriStrumenti, "Breakout Box", "Stato veicolo", "Motore acceso, quadro ON");
                    SetCampo(principale.ValoriStrumenti, "Breakout Box", "Reazione osservata", "Risposta veicolo coerente con l'atteso");
                }
                SetCampo(principale.ValoriStrumenti, "CaptureTool", "File trace (.blf/.asc)", $"trace_demo_{contatore:D4}_{e + 1}.blf");
                SetCampo(principale.ValoriStrumenti, "CaptureTool", "Configurazione bus", i % 2 == 0 ? "CAN 500 kbit/s" : "CAN-FD 2 Mbit/s");
                SetCampo(principale.ValoriStrumenti, "CaptureTool", "File database segnali (.dbc)", "TestLoom_DEMO_NETWORK.dbc");
                SetCampo(principale.ValoriStrumenti, "CaptureTool", "ID/segnali monitorati", "0x18DAF110, VehicleSpeed, BatteryVoltage");
                SetCampo(principale.ValoriStrumenti, "CaptureTool", "Script CAPL usato", "demo_diagnostic_sequence.can");
                SetCampo(principale.ValoriStrumenti, "CaptureTool", "Simulazione/iniezione messaggi attiva", i % 3 == 0 ? "true" : "false");
                if (esito == Esito.ConformeConDeviazioni)
                    principale.DescrizioneDeviazione = "Scostamento temporale entro la tolleranza accettata per la demo.";
                if (esito == Esito.NonConforme)
                    principale.DescrizioneProblema = "Risposta diagnostica diversa dal valore atteso; anomalia demo collegata.";
                esecuzione.Condizioni.Add(principale);
                if (funzionalitaTc.Count > 1 || rnd.NextDouble() < 0.3)
                {
                    // Con 2+ Funzionalità sul test, la seconda condizione ne prova una diversa dalla principale
                    // (dimostra il caso che ha motivato la Funzionalità per operazione, feedback utente
                    // 2026-08-24, terza revisione: un test con più funzionalità dove non tutte hanno lo stesso
                    // esito); con una sola, resta la stessa funzionalità in condizione di esecuzione diversa.
                    var fnzSeconda = funzionalitaTc.Count > 1 ? funzionalitaTc[1] : fnzPrincipale.Nome;
                    var seconda = new Condizione { Esito = Esito.Conforme, Componente = new EcuSelection(ecuSelezionate), Funzionalita = fnzSeconda, Nota = "Chiave OFF — seconda reiterazione (dato fittizio)." };
                    if (isBanco)
                    {
                        SetCampo(seconda.ValoriStrumenti, "ValidationTool", "Comando (Service)", principale.ValoriStrumenti.GetValueOrDefault("ValidationTool::Comando (Service)"));
                        SetCampo(seconda.ValoriStrumenti, "ValidationTool", "ID/Sub-funzione", "F1 90");
                        SetCampo(seconda.ValoriStrumenti, "ValidationTool", "Risposta attesa/ottenuta", "62 F1 90 ...");
                        SetCampo(seconda.ValoriStrumenti, "ValidationTool", "ADA", "NO");
                        SetCampo(seconda.ValoriStrumenti, "ValidationTool", "SGW", "Closed");
                        SetCampo(seconda.ValoriStrumenti, "ValidationTool", "Componente Status", "Normal");
                        SetCampo(seconda.ValoriStrumenti, "ValidationTool", "Diagnostic Session", "Extended");
                    }
                    else
                    {
                        SetCampo(seconda.ValoriStrumenti, "Breakout Box", "Azione veicolo", "Azione fittizia su veicolo, seconda condizione");
                        SetCampo(seconda.ValoriStrumenti, "Breakout Box", "PIN", $"PIN {rnd.Next(1, 40)}");
                        SetCampo(seconda.ValoriStrumenti, "Breakout Box", "Segnale inviato", "Segnale di prova inviato (chiave OFF)");
                        SetCampo(seconda.ValoriStrumenti, "Breakout Box", "Segnali letti", "Segnale di prova letto (chiave OFF)");
                        SetCampo(seconda.ValoriStrumenti, "Breakout Box", "Risultato atteso", "Risultato atteso fittizio");
                        SetCampo(seconda.ValoriStrumenti, "Breakout Box", "Stato veicolo", "Motore spento, quadro OFF");
                        SetCampo(seconda.ValoriStrumenti, "Breakout Box", "Reazione osservata", "Nessuna reazione inattesa");
                    }
                    if (seconda.ValoriStrumenti.ContainsKey("ValidationTool::Comando (Service)") || seconda.ValoriStrumenti.ContainsKey("Breakout Box::Azione veicolo"))
                        esecuzione.Condizioni.Add(seconda);
                }

                if (esito == Esito.NonConforme)
                {
                    var nc = NonConformitaRulesService.CreaNuova(ncRepo.NextId(), tc.Id, esecuzione.Iterazione, operatore, $"NC fittizia su {tc.Id}, iterazione {esecuzione.Iterazione}.");
                    nc.TestUid = tc.Uid;
                    // backdatata alla data storica fittizia dell'esecuzione, non "adesso": altrimenti tutte le NC
                    // generate in questo giro di seed si accumulerebbero sulla stessa data, appiattendo grafici
                    // e KPI basati sul tempo (es. andamento Non Conformità nel report).
                    nc.DataApertura = esecuzione.Data;
                    nc.Destinatario = (DestinatarioSegnalazione)rnd.Next(0, 4);
                    // alcune NC restano aperte, altre vengono chiuse manualmente (caso richiesto dal §10)
                    if (rnd.NextDouble() < 0.4 && e < numEsecuzioni - 1)
                    {
                        NonConformitaRulesService.Chiudi(nc);
                        nc.DataChiusura = esecuzione.Data.AddDays(rnd.Next(1, 15));
                        nc.Azione = "Chiusa manualmente: rischio accettato senza retest (dato fittizio).";
                    }
                    else
                    {
                        nc.StatoDettaglio = (StatoDettaglioNc)rnd.Next(0, 4);
                    }
                    NormalizzaOrariInesistenti(nc);
                    ncRepo.Save(nc);
                    esecuzione.NonConformitaId = nc.Id;
                }
                // Punteggio di conformità 0-5/N.A. per l'audit (Addendum: grill-me 2026-08-18, punto 3):
                // auto-calcolato dall'Esito come nell'app reale, così la matrice Audit del seed non risulta
                // tutta "Da testare" — senza questo, nessuna coppia componente×Requisito avrebbe mai un punteggio.
                PunteggioConformitaService.SincronizzaPunteggi(esecuzione, tc.RequisitoId);
                tc.Esecuzioni.Add(esecuzione);
            }
            tc.StatoLavoro = esitoFinale switch
            {
                Esito.Conforme or Esito.ConformeConDeviazioni => StatoLavoroTest.Concluso,
                Esito.NonConforme => StatoLavoroTest.DaRitestare,
                Esito.DaRivedere => StatoLavoroTest.DaRivedere,
                _ => StatoLavoroTest.InAttesa
            };
            if (esitoFinale == Esito.NonConforme)
                tc.DescrizioneProblema = "Anomalia dimostrativa ancora da verificare nella prossima revisione.";
            if (i % 9 == 0)
                tc.ImpattiPotenziali.Add(new ImpattoPotenziale { Tipo = "Componente", Valore = ecuSelezionate.Last(), Motivo = "Possibile impatto indiretto da verificare nella demo.", Stato = i % 18 == 0 ? StatoImpatto.VerificatoStessaEsecuzione : StatoImpatto.DaVerificare });
            if (i % 11 == 0)
                tc.NotePianificazione.Add(new NotaPianificazione { Data = dataCreazione.AddDays(1), Operatore = Operatori[0], CampiModificati = "Scadenza", Nota = "Riprogrammazione dimostrativa per disponibilità veicolo." });
            // auto-chiusura NC se l'ultima esecuzione è Conforme e ne esisteva una aperta collegata a questo stesso test
            if (esitoFinale == Esito.Conforme)
            {
                var ncApertaDiQuestoTest = ncRepo.GetAll().FirstOrDefault(n => n.TestCaseId == tc.Id && n.Stato == StatoNonConformita.Aperta);
                if (ncApertaDiQuestoTest != null)
                {
                    NonConformitaRulesService.Chiudi(ncApertaDiQuestoTest);
                    NormalizzaOrariInesistenti(ncApertaDiQuestoTest);
                    ncRepo.Save(ncApertaDiQuestoTest, ncApertaDiQuestoTest.RevisioneDati);
                }
            }

            tc.Changelog.Add(new ChangelogEntry { Data = dataCreazione, Operatore = operatore, Campo = "(creazione)", ValoreNuovo = "dato fittizio di setup" });

            SalvaDemo(tc, isNew: true);
            if (primoBancoConEscalation == null && isBanco && tc.Esecuzioni.SelectMany(x => x.Condizioni).Any(c => c.ValoriStrumenti.ContainsKey("Guasto Injection::guasto - Operazione"))) primoBancoConEscalation = tc;
            if (primoBancoPerUdt == null && isBanco) primoBancoPerUdt = tc;
        }

        // ---- Casi limite espliciti richiesti dal §10 / Addendum §17 ----

        // Test esplorativo senza classificazione (nessun requisito/funzionalità)
        var esplorativo = new TestCase
        {
            Id = NuovoId("Componente-TEST-BCM", "Operatore Prova A", DateTime.Now),
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, ModificatoDa = "Operatore Prova A",
            Componente = "Componente-TEST-BCM", Veicolo = Banchi[0].Nome, TestTemplateUid = bancoUid,
            Descrizione = "Test esplorativo fittizio, creato al volo senza classificazione (caso 'da completare')."
        };
        esplorativo.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Operatore = "Operatore Prova A", Esito = Esito.NonEseguitoNA, Matricola = "VF1TESTESPL0" });
        SalvaDemo(esplorativo, isNew: true);

        // Test su componente archiviata, ma ancora modificabile (l'archiviazione è solo visiva, §3.5)
        var suEcuArchiviata = new TestCase
        {
            Id = NuovoId("Componente-TEST-LEGACY", "Operatore Prova B", DateTime.Now.AddDays(-400)),
            DataCreazione = DateTime.Now.AddDays(-400), UltimaModifica = DateTime.Now.AddDays(-400), ModificatoDa = "Operatore Prova B",
            Componente = "Componente-TEST-LEGACY", Veicolo = Banchi[0].Nome, TestTemplateUid = bancoUid,
            RequisitoId = new List<string> { requisiti[0].Id },
            Descrizione = "Test fittizio su componente ormai archiviata (progetto concluso), ma ancora modificabile per un eventuale fix urgente."
        };
        var esecSuEcuArchiviata = new Esecuzione { Iterazione = 1, Data = suEcuArchiviata.DataCreazione, Operatore = "Operatore Prova B", Esito = Esito.Conforme, Note = "Esito storico fittizio.", Matricola = "VF1TESTLEGACY" };
        PunteggioConformitaService.SincronizzaPunteggi(esecSuEcuArchiviata, suEcuArchiviata.RequisitoId);
        suEcuArchiviata.Esecuzioni.Add(esecSuEcuArchiviata);
        SalvaDemo(suEcuArchiviata, isNew: true);

        // Test vecchio che referenzia un requisito ormai archiviato (§3.5: non si elimina mai fisicamente)
        var reqArchiviato = requisiti.First(r => r.Archiviata);
        var suRequisitoArchiviato = new TestCase
        {
            Id = NuovoId("Componente-TEST-SGW", "Operatore Prova C", DateTime.Now.AddDays(-500)),
            DataCreazione = DateTime.Now.AddDays(-500), UltimaModifica = DateTime.Now.AddDays(-500), ModificatoDa = "Operatore Prova C",
            Componente = "Componente-TEST-SGW", Veicolo = Sottoveicoli[1].Nome, TestTemplateUid = veicoloUid,
            RequisitoId = new List<string> { reqArchiviato.Id },
            Descrizione = "Test fittizio vecchio, collegato a un requisito nel frattempo archiviato."
        };
        var esecSuRequisitoArchiviato = new Esecuzione { Iterazione = 1, Data = suRequisitoArchiviato.DataCreazione, Operatore = "Operatore Prova C", Esito = Esito.Conforme, Note = "Esito storico fittizio.", Matricola = "VF1TESTDEPR0" };
        PunteggioConformitaService.SincronizzaPunteggi(esecSuRequisitoArchiviato, suRequisitoArchiviato.RequisitoId);
        suRequisitoArchiviato.Esecuzioni.Add(esecSuRequisitoArchiviato);
        SalvaDemo(suRequisitoArchiviato, isNew: true);

        // Test su Sottoveicolo non ancora categorizzato (Gamma vuota, migrazione non bloccante, Addendum §17.3)
        var suNonCategorizzato = new TestCase
        {
            Id = NuovoId("Componente-TEST-BCM", "Operatore Prova A", DateTime.Now.AddDays(-2)),
            DataCreazione = DateTime.Now.AddDays(-2), UltimaModifica = DateTime.Now.AddDays(-2), ModificatoDa = "Operatore Prova A",
            Componente = "Componente-TEST-BCM", Veicolo = "Sottoveicolo-Test-NonCategorizzato", TestTemplateUid = bancoUid,
            RequisitoId = new List<string> { requisiti[2].Id },
            Descrizione = "Test fittizio su un sottoveicolo non ancora assegnato a nessuna Gamma (dato di migrazione)."
        };
        var esecSuNonCategorizzato = new Esecuzione { Iterazione = 1, Data = suNonCategorizzato.DataCreazione, Operatore = "Operatore Prova A", Esito = Esito.DaRivedere, Matricola = "VF1TESTNOCAT" };
        PunteggioConformitaService.SincronizzaPunteggi(esecSuNonCategorizzato, suNonCategorizzato.RequisitoId);
        suNonCategorizzato.Esecuzioni.Add(esecSuNonCategorizzato);
        SalvaDemo(suNonCategorizzato, isNew: true);

        // Escalation banco -> veicolo ("Confronto con Banco")
        if (primoBancoConEscalation != null)
        {
            // Il retest avviene sul veicolo reale corrispondente alla stessa Gamma del banco (Addendum redesign
            // 2026-08: il banco è solo una categoria diversa dello stesso Veicolo, non un'entità a parte).
            var gammaBanco = Banchi.First(b => b.Nome == primoBancoConEscalation.Veicolo).Gamma;
            var veicoloRealeCorrispondente = Sottoveicoli.First(v => v.Gamma == gammaBanco).Nome;
            var testVeicoloEscalation = new TestCase
            {
                Id = NuovoId(primoBancoConEscalation.Componente, "Operatore Prova A", DateTime.Now.AddDays(-10)),
                DataCreazione = DateTime.Now.AddDays(-10), UltimaModifica = DateTime.Now.AddDays(-10), ModificatoDa = "Operatore Prova A",
                Componente = primoBancoConEscalation.Componente, Veicolo = veicoloRealeCorrispondente, TestTemplateUid = veicoloUid,
                RequisitoId = primoBancoConEscalation.RequisitoId,
                Descrizione = "Retest a veicolo di un caso guasto Injection non riproducibile a banco (dato fittizio)."
            };
            var ultimaBanco = primoBancoConEscalation.Esecuzioni[^1];
            var esecEscalation = new Esecuzione { Iterazione = 1, Data = testVeicoloEscalation.DataCreazione, Operatore = "Operatore Prova A", Esito = Esito.Conforme, Note = "Confermato su veicolo reale (dato fittizio).", Matricola = "VF1TESTESCAL", BaselineSw = ultimaBanco.BaselineSw, BaselineHw = ultimaBanco.BaselineHw };
            var condEscalation = new Condizione { Esito = Esito.Conforme, Componente = new EcuSelection(testVeicoloEscalation.Componente) };
            SetCampo(condEscalation.ValoriStrumenti, "Breakout Box", "Azione veicolo", "Riproduzione guasto reale su veicolo");
            esecEscalation.Condizioni.Add(condEscalation);
            PunteggioConformitaService.SincronizzaPunteggi(esecEscalation, testVeicoloEscalation.RequisitoId);
            testVeicoloEscalation.Esecuzioni.Add(esecEscalation);
            SalvaDemo(testVeicoloEscalation, isNew: true);

            primoBancoConEscalation.TestCollegatiCreati.Add(new TestCollegatoCreato
            {
                TemplateDestinazioneNome = nomeTemplateVeicolo,
                TestCollegatoId = testVeicoloEscalation.Id
            });
            SalvaDemo(primoBancoConEscalation, isNew: false);
        }

        // Audit: coppia componente×Requisito con Test diversi discordanti (Addendum: grill-me 2026-08-18, seconda
        // follow-up — la matrice deve poter mostrare davvero lo stato Conflitto, non solo in teoria) e una
        // valutazione N.A. esplicita (Esito.NonApplicabile, distinto da NonEseguitoNA che non ha punteggio
        // automatico) — senza questi due casi il seed non eserciterebbe mai quegli stati del pannello Audit.
        {
            var ecuConflitto = Componente[0];
            var reqConflitto = requisiti[3];
            var sottoveicoloConflitto = Sottoveicoli[0].Nome;

            var testConflittoA = new TestCase
            {
                Id = NuovoId(ecuConflitto, "Operatore Prova A", DateTime.Now.AddDays(-20)),
                DataCreazione = DateTime.Now.AddDays(-20), UltimaModifica = DateTime.Now.AddDays(-20), ModificatoDa = "Operatore Prova A",
                Componente = ecuConflitto, Veicolo = sottoveicoloConflitto, TestTemplateUid = veicoloUid,
                RequisitoId = new List<string> { reqConflitto.Id },
                Descrizione = "Test fittizio A per dimostrare un conflitto Audit: stessa componente/Requisito del test B, esito discordante."
            };
            var esecConflittoA = new Esecuzione { Iterazione = 1, Data = testConflittoA.DataCreazione, Operatore = "Operatore Prova A", Esito = Esito.Conforme, Note = "Dato fittizio per demo conflitto Audit.", Matricola = "VF1TESTCONFA" };
            PunteggioConformitaService.SincronizzaPunteggi(esecConflittoA, testConflittoA.RequisitoId);
            testConflittoA.Esecuzioni.Add(esecConflittoA);
            SalvaDemo(testConflittoA, isNew: true);

            var testConflittoB = new TestCase
            {
                Id = NuovoId(ecuConflitto, "Operatore Prova B", DateTime.Now.AddDays(-5)),
                DataCreazione = DateTime.Now.AddDays(-5), UltimaModifica = DateTime.Now.AddDays(-5), ModificatoDa = "Operatore Prova B",
                Componente = ecuConflitto, Veicolo = sottoveicoloConflitto, TestTemplateUid = veicoloUid,
                RequisitoId = new List<string> { reqConflitto.Id },
                Descrizione = "Test fittizio B per dimostrare un conflitto Audit: stessa componente/Requisito del test A, esito discordante."
            };
            var esecConflittoB = new Esecuzione { Iterazione = 1, Data = testConflittoB.DataCreazione, Operatore = "Operatore Prova B", Esito = Esito.NonConforme, Note = "Dato fittizio per demo conflitto Audit.", Matricola = "VF1TESTCONFB" };
            PunteggioConformitaService.SincronizzaPunteggi(esecConflittoB, testConflittoB.RequisitoId);
            testConflittoB.Esecuzioni.Add(esecConflittoB);
            SalvaDemo(testConflittoB, isNew: true);

            var ecuNa = Componente[1];
            var reqNa = requisiti[4];
            var testNa = new TestCase
            {
                Id = NuovoId(ecuNa, "Operatore Prova C", DateTime.Now.AddDays(-8)),
                DataCreazione = DateTime.Now.AddDays(-8), UltimaModifica = DateTime.Now.AddDays(-8), ModificatoDa = "Operatore Prova C",
                Componente = ecuNa, Veicolo = Sottoveicoli[1].Nome, TestTemplateUid = veicoloUid,
                RequisitoId = new List<string> { reqNa.Id },
                Descrizione = "Test fittizio con esito Non applicabile, per dimostrare la cella N.A. nel pannello Audit."
            };
            var esecNa = new Esecuzione { Iterazione = 1, Data = testNa.DataCreazione, Operatore = "Operatore Prova C", Esito = Esito.NonApplicabile, Note = "Dato fittizio per demo N.A. Audit.", Matricola = "VF1TESTNAAUD" };
            PunteggioConformitaService.SincronizzaPunteggi(esecNa, testNa.RequisitoId);
            testNa.Esecuzioni.Add(esecNa);
            SalvaDemo(testNa, isNew: true);
        }

        // UnitLogger collegato a un test banco esistente (Addendum §17.6: UnitLogger sempre collegato, mai standalone)
        if (primoBancoPerUdt != null)
        {
            var funzionalitaUdtBase = funzionalita.First(f => f.Nome == "Verifica pacchetto diagnosi base").Nome;
            var funzionalitaUdtProgrammazione = funzionalita.First(f => f.Nome == "Verifica pacchetto programmazione").Nome;
            var udtCollegato = new TestCase
            {
                Id = NuovoId(primoBancoPerUdt.Componente, "Operatore Prova C", DateTime.Now.AddDays(-3)),
                DataCreazione = DateTime.Now.AddDays(-3), UltimaModifica = DateTime.Now.AddDays(-3), ModificatoDa = "Operatore Prova C",
                Componente = primoBancoPerUdt.Componente, Veicolo = primoBancoPerUdt.Veicolo, TestTemplateUid = udtUid,
                TestCollegatoRichiestoId = primoBancoPerUdt.Id,
                RequisitoId = primoBancoPerUdt.RequisitoId.ToList(),
                Funzionalita = new List<string> { funzionalitaUdtBase, funzionalitaUdtProgrammazione },
                StrumentiUsati = new List<string> { "UnitLogger" }, Assegnatario = "Operatore Prova C",
                DataAssegnazione = DateTime.Now.AddDays(-3), StatoLavoro = StatoLavoroTest.Concluso,
                DescrizioneBreve = "Verifica WU diagnosi base e programmazione",
                Descrizione = "Double-check UnitLogger del risultato ottenuto nel test banco collegato (dato fittizio)."
            };
            SetCampo(udtCollegato.ValoriStrumentiTest, "UnitLogger", "Licenza", "TestLoom-DEMO-LIC-001");
            SetCampo(udtCollegato.ValoriStrumentiTest, "UnitLogger", "Versione UnitLogger", "9.9.9");
            var esecUdt = new Esecuzione { Iterazione = 1, Data = udtCollegato.DataCreazione, Operatore = "Operatore Prova C", Esito = Esito.ConformeConDeviazioni, Deviazioni = "Piccola differenza di temporizzazione esplicitamente documentata.", Matricola = primoBancoPerUdt.Esecuzioni[^1].Matricola, BaselineSw = "3.4.1", BaselineHw = "Rev3" };
            var condizioneUdtBase = new Condizione { Esito = Esito.ConformeConDeviazioni, Componente = new EcuSelection(udtCollegato.Componente), Funzionalita = funzionalitaUdtBase, Nota = "Lettura completa della WU su lingua inglese.", DescrizioneDeviazione = "Tempo di apertura superiore di 150 ms rispetto al riferimento." };
            SetCampo(condizioneUdtBase.ValoriStrumenti, "UnitLogger", "Nome pacchetto", "Pacchetto-Test-Diag");
            SetCampo(condizioneUdtBase.ValoriStrumenti, "UnitLogger", "Versione pacchetto", "1.1.0");
            SetCampo(condizioneUdtBase.ValoriStrumenti, "UnitLogger", "Lingua", "EN");
            SetCampo(condizioneUdtBase.ValoriStrumenti, "UnitLogger", "Pacchetto", "TRUCK_TEST.BATTERY_TEST_BCM");
            SetCampo(condizioneUdtBase.ValoriStrumenti, "UnitLogger", "Versione Pacchetto", "32075");
            var condizioneUdtProgrammazione = new Condizione { Esito = Esito.Conforme, Componente = new EcuSelection(udtCollegato.Componente), Funzionalita = funzionalitaUdtProgrammazione, Nota = "Programmazione completata e verificata al riavvio." };
            SetCampo(condizioneUdtProgrammazione.ValoriStrumenti, "UnitLogger", "Nome pacchetto", "Pacchetto-Test-Diag");
            SetCampo(condizioneUdtProgrammazione.ValoriStrumenti, "UnitLogger", "Versione pacchetto", "1.1.0");
            SetCampo(condizioneUdtProgrammazione.ValoriStrumenti, "UnitLogger", "Lingua", "IT");
            SetCampo(condizioneUdtProgrammazione.ValoriStrumenti, "UnitLogger", "Pacchetto", "TRUCK_TEST.PROGRAMMING_BCM");
            SetCampo(condizioneUdtProgrammazione.ValoriStrumenti, "UnitLogger", "Versione Pacchetto", "1042");
            esecUdt.Condizioni.Add(condizioneUdtBase);
            esecUdt.Condizioni.Add(condizioneUdtProgrammazione);
            PunteggioConformitaService.SincronizzaPunteggi(esecUdt, udtCollegato.RequisitoId);
            udtCollegato.Esecuzioni.Add(esecUdt);
            SalvaDemo(udtCollegato, isNew: true);

            // UnitLogger Performance: stessa verifica UnitLogger ma su un veicolo/componente già in uso (Addendum §18) —
            // campi UnitLogger e campi di durabilità insieme, sempre collegato come il double-check UnitLogger sopra.
            var udtPerformance = new TestCase
            {
                Id = NuovoId(primoBancoPerUdt.Componente, "Operatore Prova B", DateTime.Now.AddDays(-1)),
                DataCreazione = DateTime.Now.AddDays(-1), UltimaModifica = DateTime.Now.AddDays(-1), ModificatoDa = "Operatore Prova B",
                Componente = primoBancoPerUdt.Componente, Veicolo = primoBancoPerUdt.Veicolo, TestTemplateUid = udtPerfUid,
                TestCollegatoRichiestoId = primoBancoPerUdt.Id,
                RequisitoId = primoBancoPerUdt.RequisitoId.ToList(),
                Funzionalita = new List<string> { funzionalitaUdtBase },
                StrumentiUsati = new List<string> { "UnitLogger", "Componente Usato" }, Assegnatario = "Operatore Prova B",
                DataAssegnazione = DateTime.Now.AddDays(-1), StatoLavoro = StatoLavoroTest.Concluso,
                DescrizioneBreve = "WU su componente con storico di utilizzo",
                Descrizione = "Verifica UnitLogger su un veicolo già in uso, con dati di durabilità (dato fittizio)."
            };
            SetCampo(udtPerformance.ValoriStrumentiTest, "UnitLogger", "Licenza", "TestLoom-DEMO-LIC-001");
            SetCampo(udtPerformance.ValoriStrumentiTest, "UnitLogger", "Versione UnitLogger", "9.9.9");
            var esecUdtPerf = new Esecuzione { Iterazione = 1, Data = udtPerformance.DataCreazione, Operatore = "Operatore Prova B", Esito = Esito.Conforme, Matricola = primoBancoPerUdt.Esecuzioni[^1].Matricola, BaselineSw = "3.4.2", BaselineHw = "Rev3" };
            SetCampo(esecUdtPerf.ValoriStrumenti, "Componente Usato", "Km percorsi", "84500");
            SetCampo(esecUdtPerf.ValoriStrumenti, "Componente Usato", "Ore utilizzo", "1200");
            SetCampo(esecUdtPerf.ValoriStrumenti, "Componente Usato", "Storia manutenzione", "Tagliando regolare, nessun intervento straordinario (dato fittizio).");
            var condizioneUdtPerf = new Condizione { Esito = Esito.Conforme, Componente = new EcuSelection(udtPerformance.Componente), Funzionalita = funzionalitaUdtBase, Nota = "Verifica su veicolo già in uso, dato fittizio." };
            SetCampo(condizioneUdtPerf.ValoriStrumenti, "UnitLogger", "Nome pacchetto", "Pacchetto-Test-Diag");
            SetCampo(condizioneUdtPerf.ValoriStrumenti, "UnitLogger", "Versione pacchetto", "1.2.0");
            SetCampo(condizioneUdtPerf.ValoriStrumenti, "UnitLogger", "Lingua", "EN");
            // Variante ortografica intenzionale della stessa WU/revisione: alimenta il banner doppioni e
            // consente di dimostrare il merge manuale senza confondere revisioni differenti.
            SetCampo(condizioneUdtPerf.ValoriStrumenti, "UnitLogger", "Pacchetto", "truck-test battery-test-bcm");
            SetCampo(condizioneUdtPerf.ValoriStrumenti, "UnitLogger", "Versione Pacchetto", "32075");
            SetCampo(condizioneUdtPerf.ValoriStrumenti, "Componente Usato", "Data primo utilizzo", DateTime.Now.AddYears(-2).ToString("dd/MM/yyyy"));
            SetCampo(condizioneUdtPerf.ValoriStrumenti, "Componente Usato", "Note componente", "Componente demo proveniente da flotta endurance.");
            esecUdtPerf.Condizioni.Add(condizioneUdtPerf);
            PunteggioConformitaService.SincronizzaPunteggi(esecUdtPerf, udtPerformance.RequisitoId);
            udtPerformance.Esecuzioni.Add(esecUdtPerf);
            SalvaDemo(udtPerformance, isNew: true);
        }

        // Test correlati: collega un paio di test generati casualmente
        var campione = tcRepo.GetAll().Where(t => !t.Eliminato).OrderBy(_ => rnd.Next()).Take(4).ToList();
        if (campione.Count >= 2)
        {
            campione[0].TestCorrelati.Add(campione[1].Id);
            campione[1].TestCorrelati.Add(campione[0].Id);
            SalvaDemo(campione[0], isNew: false);
            SalvaDemo(campione[1], isNew: false);
        }
        if (campione.Count >= 4)
        {
            campione[2].TestCorrelati.Add(campione[3].Id);
            SalvaDemo(campione[2], isNew: false);
        }

        // Ticket collegati (Claim/CR fittizi), su un paio di test del campione già estratto
        if (campione.Count >= 2)
        {
            campione[0].TicketCollegati.Add(new TicketRif { Riferimento = $"TICKET-{rnd.Next(10000, 99999)}" });
            campione[1].TicketCollegati.Add(new TicketRif { Riferimento = $"TICKET-{rnd.Next(10000, 99999)}" });
            SalvaDemo(campione[0], isNew: false);
            SalvaDemo(campione[1], isNew: false);
        }

        // Un test case eliminato (per popolare il Cestino), con un correlato che resterà "collegamento eliminato"
        if (campione.Count >= 1)
        {
            var daEliminare = tcRepo.GetAll().FirstOrDefault(t => t.Id != campione[0].Id && !t.Eliminato);
            if (daEliminare != null)
            {
                campione[0].TestCorrelati.Add(daEliminare.Id);
                SalvaDemo(campione[0], isNew: false);
                tcRepo.SoftDelete(daEliminare, "Operatore Prova A");
            }
        }

        if (includiCasiQa)
        {
            // Artefatti volutamente problematici riservati al seed QA/CLI, mai al demo del primo avvio.
            var corrottoPath = paths.TestCaseFile("Componente-TEST-BCM", "CORROTTO-TEST-99");
            Directory.CreateDirectory(Path.GetDirectoryName(corrottoPath)!);
            File.WriteAllText(corrottoPath, "{ questo non è JSON valido, generato apposta per testare lo skip ");

            var lockService = new LockService();
            lockService.TryAcquire(paths.RequisitiFile, "Operatore Prova B (sessione simulata)");
        }

        tcRepo.RefreshIndex();
        ncRepo.RefreshIndex();

        // Ultima passata di coerenza: anche i casi limite creati esplicitamente devono poter essere aperti,
        // modificati e salvati durante una demo senza fermarsi su campi obbligatori lasciati vuoti. Popola
        // inoltre lo snapshot di ogni revisione, così il confronto storico è dimostrabile su tutto il dataset.
        var templatePerUid = templates.ToDictionary(t => t.Uid);
        foreach (var test in tcRepo.GetAll().ToList())
        {
            test.NormalizzaVeicoli();
            test.CreatoDa = string.IsNullOrWhiteSpace(test.CreatoDa) ? test.ModificatoDa : test.CreatoDa;
            test.Assegnatario ??= string.IsNullOrWhiteSpace(test.ModificatoDa) ? Operatori[0] : test.ModificatoDa;
            test.DataAssegnazione ??= test.DataCreazione;
            test.Scadenza ??= test.DataCreazione.AddDays(45);
            test.CampiCustom.TryAdd("progetto-demo", new[] { "Phoenix", "Orion", "Atlas" }[(int)((uint)test.Id.GetHashCode() % 3)]);
            test.CampiCustom.TryAdd("release-demo", new[] { "R26.3", "R26.4", "R27.1" }[(int)((uint)test.Uid.GetHashCode() % 3)]);
            if (string.IsNullOrWhiteSpace(test.DescrizioneBreve))
                test.DescrizioneBreve = test.Funzionalita.FirstOrDefault() ?? "Caso dimostrativo completo";
            if (test.StrumentiUsati.Count == 0 && templatePerUid.TryGetValue(test.TestTemplateUid, out var template))
                test.StrumentiUsati = template.StrumentiPreselezionati.ToList();
            if (test.Esecuzioni.Count > 0 && test.StatoLavoro == StatoLavoroTest.Pianificato)
            {
                test.StatoLavoro = test.StatoCorrente switch
                {
                    Esito.Conforme or Esito.ConformeConDeviazioni => StatoLavoroTest.Concluso,
                    Esito.NonConforme => StatoLavoroTest.DaRitestare,
                    Esito.DaRivedere => StatoLavoroTest.DaRivedere,
                    _ => StatoLavoroTest.InAttesa
                };
            }
            // Le revisioni già salvate sono immutabili, anche nel seed. Completiamo solo la
            // revisione corrente: il repository conserva il prefisso storico autorevole.
            foreach (var revisione in test.Esecuzioni.TakeLast(1))
            {
                revisione.Dettagli ??= new DettagliTestSnapshot
                {
                    Componente = new EcuSelection(test.Componente), Veicolo = test.Veicolo, Veicoli = test.VeicoliEffettivi.ToList(),
                    TestTemplateUid = test.TestTemplateUid,
                    TestTemplateNome = templatePerUid.GetValueOrDefault(test.TestTemplateUid)?.Nome ?? "",
                    Funzionalita = test.Funzionalita.ToList(), StrumentiUsati = test.StrumentiUsati.ToList(),
                    DescrizioneBreve = test.DescrizioneBreve, Descrizione = test.Descrizione,
                    DescrizioneProblema = test.DescrizioneProblema,
                    CampiCustom = new Dictionary<string, string>(test.CampiCustom),
                    ValoriStrumentiTest = new Dictionary<string, string>(test.ValoriStrumentiTest)
                };
            }
            SalvaDemo(test, isNew: false);
        }
        tcRepo.RefreshIndex();
        var revisioniUdtRepo = new MasterDataRepository<RevisioneUdt>(paths.RevisioniUdtFile);
        foreach (var test in tcRepo.GetAll()) RevisioneUdtService.Sincronizza(revisioniUdtRepo, test, "Setup");

        GeneraChangeRequestSegnalazioniENotifiche(paths, templates, tcRepo, ncRepo);
    }

    private static Esito SceglEsitoFinale(Random rnd, int seed)
    {
        // distribuzione varia per esercitare tutti i bucket di copertura (Addendum §16.3)
        var v = rnd.NextDouble();
        return v switch
        {
            < 0.45 => Esito.Conforme,
            < 0.62 => Esito.ConformeConDeviazioni,
            < 0.80 => Esito.NonConforme,
            < 0.92 => Esito.DaRivedere,
            _ => Esito.NonEseguitoNA
        };
    }

    private static List<Requisito> GeneraRequisiti()
    {
        var lista = new List<Requisito>();
        for (int i = 1; i <= 20; i++)
        {
            lista.Add(new Requisito
            {
                Id = $"ST{i:D2}",
                Testo = $"Requisito fittizio {i}: descrizione di prova per verifica end-to-end dell'applicazione.",
                ApplicabileBanco = i % 3 != 0,
                ApplicabileVeicolo = i % 4 != 0,
                Archiviata = i == 20, // un requisito archiviato ma ancora referenziato da un test vecchio
                UltimaModifica = DateTime.Now.AddDays(-i),
                ModificatoDa = "Setup"
            });
        }
        return lista;
    }

    /// <summary>Funzionalità unificate con Categoria (Addendum redesign 2026-08: prima Funzionalità e UnitLogger erano
    /// due tabelle separate, unite qui perché la stessa funzionalità reale si applica spesso a entrambi i
    /// contesti — la Categoria distingue l'uso senza duplicare la voce).</summary>
    private static List<Funzionalita> GeneraFunzionalita()
    {
        // Una voce con due categorie (feedback utente 2026-08-24, dimostra il caso reale osservato nei menu
        // UnitLogger: lo stesso Param/funzionalità ricorre sotto più categorie/pagine diverse) invece di forzare
        // tutte le voci demo a una categoria sola.
        (string Nome, string[] Categorie)[] voci =
        {
            ("Diagnosi attiva iniettori", new[] { "Diagnostica avanzata" }),
            ("Lettura DID pressione", new[] { "Diagnostica base" }),
            ("Reset adattamenti", new[] { "Diagnostica avanzata" }),
            ("Calibrazione sensore", new[] { "Programmazione" }),
            ("Test attuatori", new[] { "Diagnostica avanzata" }),
            ("Verifica pacchetto diagnosi base", new[] { "Diagnostica base", "UnitLogger" }),
            ("Verifica pacchetto guasto estesi", new[] { "Diagnostica avanzata", "UnitLogger" }),
            ("Verifica pacchetto programmazione", new[] { "Programmazione", "UnitLogger" }),
        };
        var sottoveicoli = Sottoveicoli.Select(v => v.Nome).ToList();
        return voci.Select(v => new Funzionalita { Nome = v.Nome, Categorie = v.Categorie.ToList(), ApplicabilePerVeicolo = sottoveicoli, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" }).ToList();
    }

    private static TestTemplate GeneraTemplateChangeRequest()
    {
        var template = new TestTemplate
        {
            Nome = "Change Request Software", Icona = "🔁", Ordine = 20, IsChangeRequest = true,
            UltimaModifica = DateTime.Now, ModificatoDa = "Setup"
        };
        template.Sezioni.Add(new SezioneTemplate
        {
            Nome = "Dettagli modifica", Ordine = 0,
            Campi = new List<CampoTemplate>
            {
                new() { Nome = "Componente software", TipoLibero = TipoCampoStrumento.Lista, OpzioniListaLibero = new() { "Diagnostica componente", "Gateway", "Pacchetto UnitLogger" }, Obbligatorio = true, Ordine = 0 },
                new() { Nome = "Versione prevista", TipoLibero = TipoCampoStrumento.Testo, Obbligatorio = true, Ordine = 1 },
                new() { Nome = "Rischio tecnico", TipoLibero = TipoCampoStrumento.Lista, OpzioniListaLibero = new() { "Basso", "Medio", "Alto" }, Ordine = 2 }
            }
        });
        return template;
    }

    private static void GeneraChangeRequestSegnalazioniENotifiche(DataPaths paths, IReadOnlyList<TestTemplate> templates,
        TestCaseRepository tcRepo, NonConformitaRepository ncRepo)
    {
        var test = tcRepo.GetAll().Where(t => t.Esecuzioni.Count > 0).OrderByDescending(t => t.DataCreazione).ToList();
        var conformi = test.Where(t => t.StatoCorrente == Esito.Conforme).Take(3).ToList();
        var templateCr = templates.Single(t => t.IsChangeRequest);
        var crRepo = new ChangeRequestRepository(paths);
        crRepo.RefreshIndex();
        for (var i = 0; i < 4; i++)
        {
            var collegato = conformi.ElementAtOrDefault(i % Math.Max(1, conformi.Count)) ?? test[i];
            var stato = i switch { 0 => StatoChangeRequest.Aperta, 1 => StatoChangeRequest.DaValidare, 2 => StatoChangeRequest.Validata, _ => StatoChangeRequest.Chiusa };
            var cr = new ChangeRequest
            {
                Id = crRepo.NextId(), TemplateUid = templateCr.Uid,
                Titolo = new[] { "Aggiornamento timeout servizio diagnostico", "Allineamento gateway alla nuova baseline", "Correzione pacchetto UnitLogger programmazione", "Ottimizzazione sequenza lettura DID" }[i],
                Descrizione = "Change Request fittizia completa per dimostrare ambito, revisioni e validazione tramite Test.",
                Responsabile = Operatori[i % 3], Stato = stato, DataCreazione = DateTime.Now.AddDays(-30 + i * 6), UltimaModifica = DateTime.Now.AddDays(-i),
                VeicoliImpattati = collegato.VeicoliEffettivi.ToList(), EcuImpattate = collegato.Componente.ToList(),
                StrumentiImpattati = collegato.StrumentiUsati.ToList(),
                TicketCollegati = new() { new TicketRif { Riferimento = $"TICKET-DEMO-{4100 + i}" } }
            };
            foreach (var campo in templateCr.Sezioni.SelectMany(s => s.Campi))
                cr.CampiCustom[campo.Uid.ToString()] = campo.Nome switch { "Componente software" => "Diagnostica componente", "Versione prevista" => $"26.{i + 3}.0", _ => i < 2 ? "Medio" : "Basso" };
            if (stato != StatoChangeRequest.Aperta)
            {
                cr.ImplementataIl = cr.DataCreazione.AddDays(8); cr.ImplementataDa = cr.Responsabile; cr.RevisioneImplementata = 1;
                cr.TestValidazione.Add(new CollegamentoValidazioneCr
                {
                    TestId = collegato.Id, CollegatoDa = Operatori[0], CollegatoIl = cr.DataCreazione.AddDays(9),
                    VeicoliCoperti = collegato.VeicoliEffettivi.ToList(), EcuCoperte = collegato.Componente.ToList(), StrumentiCoperti = collegato.StrumentiUsati.ToList(),
                    EsecuzioneValidante = collegato.Esecuzioni[^1].Iterazione,
                    SnapshotValidazione = new SnapshotValidazioneCr { EseguitaIl = collegato.Esecuzioni[^1].Data, Esito = collegato.Esecuzioni[^1].Esito, Veicoli = collegato.VeicoliEffettivi.ToList(), Componente = collegato.Componente.ToList(), Strumenti = collegato.StrumentiUsati.ToList() }
                });
            }
            if (stato == StatoChangeRequest.Chiusa) { cr.ChiusaIl = DateTime.Now.AddDays(-1); cr.ChiusaDa = Operatori[0]; }
            cr.Revisioni.Add(new RevisioneChangeRequest { Numero = 1, Data = cr.DataCreazione, Autore = cr.Responsabile, Titolo = cr.Titolo, DescrizioneBreve = cr.DescrizioneBreve, Descrizione = cr.Descrizione, VeicoliImpattati = cr.VeicoliImpattati.ToList(), EcuImpattate = cr.EcuImpattate.ToList(), StrumentiImpattati = cr.StrumentiImpattati.ToList(), TestValidazioneId = cr.TestValidazione.Select(v => v.TestId).ToList() });
            cr.Changelog.Add(new ChangelogEntry { Data = cr.DataCreazione, Operatore = cr.Responsabile, Campo = "(creazione)", ValoreNuovo = "Change Request dimostrativa" });
            crRepo.Save(cr);
        }

        var segRepo = new SegnalazioneRepository(paths);
        segRepo.RefreshIndex();
        var candidati = test.SelectMany(t => t.Esecuzioni.SelectMany(e => e.Condizioni.Select(c => (Test: t, Esecuzione: e, Condizione: c))))
            .Where(x => x.Condizione.Esito is Esito.NonConforme or Esito.ConformeConDeviazioni).Take(4).ToList();
        var anomalie = ncRepo.GetAll();
        for (var i = 0; i < candidati.Count; i++)
        {
            var x = candidati[i];
            var seg = new SegnalazioneDaClassificare
            {
                TestId = x.Test.Id, TestUid = x.Test.Uid, CondizioneUid = x.Condizione.Uid, EsecuzioneIterazione = x.Esecuzione.Iterazione,
                CreataIl = x.Esecuzione.Data, CreataDa = x.Esecuzione.Operatore,
                Stato = (StatoSegnalazione)i,
                MotivazioneEsclusione = i == 2 ? "Comportamento previsto dalla specifica demo." : "",
                AnomaliaUid = i == 1 ? anomalie.FirstOrDefault(n => n.TestCaseId == x.Test.Id)?.Uid : null
            };
            segRepo.Save(seg);
        }

        var notifiche = new NotificaRepository(paths);
        foreach (var (operatore, i) in Operatori.Select((o, i) => (o, i)))
        {
            notifiche.Add(new NotificaApp { Destinatario = operatore, Data = DateTime.Now.AddHours(-i), Tipo = i % 2 == 0 ? "Assegnazione" : "ChangeRequest", Titolo = i % 2 == 0 ? "Nuovo Test assegnato" : "Change Request aggiornata", Messaggio = "Notifica dimostrativa: apri l'elemento collegato per verificare il flusso completo.", ElementoId = i % 2 == 0 ? test[i].Id : $"CR-{DateTime.Now.Year}-{i:D4}", Letta = i == 3 });
        }
        notifiche.Add(new NotificaApp { Destinatario = "*", Tipo = "Informazione", Titolo = "Dataset demo completo", Messaggio = "Dati fittizi rigenerati e pronti per il collaudo di tutte le aree.", Letta = false });
    }

    /// <summary>Le date della demo sono calcolate come "adesso meno N giorni/ore": possono cadere nell'ora che
    /// non esiste al passaggio all'ora legale (es. 29/03/2026 02:38 in Europa/Roma). Una tale DateTime locale non
    /// sopravvive identica al round-trip JSON (diventa 03:38 +02:00), quindi l'hash del Test salvato non
    /// coincide più con quello riletto e il salvataggio successivo della demo verrebbe rifiutato come conflitto.
    /// Le date inserite dall'utente o prese da DateTime.Now non possono cadere in quell'ora: il problema riguarda
    /// solo i dati generati qui, che vengono spostati di un'ora avanti come fa il round-trip.</summary>
    internal static void NormalizzaOrariInesistenti(TestCase test)
    {
        static DateTime V(DateTime d) =>
            d.Kind != DateTimeKind.Utc && TimeZoneInfo.Local.IsInvalidTime(d) ? d.AddHours(1) : d;
        static DateTime? N(DateTime? d) => d.HasValue ? V(d.Value) : null;

        test.DataCreazione = V(test.DataCreazione);
        test.UltimaModifica = V(test.UltimaModifica);
        test.DataEliminazione = N(test.DataEliminazione);
        test.Scadenza = N(test.Scadenza);
        test.DataAssegnazione = N(test.DataAssegnazione);
        foreach (var e in test.Esecuzioni) e.Data = V(e.Data);
        foreach (var a in test.Allegati) a.DataCaricamento = V(a.DataCaricamento);
        foreach (var t in test.TicketCollegati) t.CollegatoIl = V(t.CollegatoIl);
        foreach (var c in test.Changelog) c.Data = V(c.Data);
        foreach (var n in test.NotePianificazione) n.Data = V(n.Data);
        foreach (var t in test.TestCollegatiCreati) t.CreatoIl = V(t.CreatoIl);
    }

    /// <summary>Stessa regola per le Non Conformità della demo: DataApertura è copiata dalla data della
    /// revisione e la NC viene risalvata (chiusura automatica) con lo stesso confronto di hash.</summary>
    internal static void NormalizzaOrariInesistenti(NonConformita nc)
    {
        static DateTime V(DateTime d) =>
            d.Kind != DateTimeKind.Utc && TimeZoneInfo.Local.IsInvalidTime(d) ? d.AddHours(1) : d;
        static DateTime? N(DateTime? d) => d.HasValue ? V(d.Value) : null;

        nc.DataApertura = V(nc.DataApertura);
        nc.DataChiusura = N(nc.DataChiusura);
        nc.Scadenza = N(nc.Scadenza);
        nc.DataFix = N(nc.DataFix);
        foreach (var c in nc.CollegamentiPolarion) c.CollegatoIl = V(c.CollegatoIl);
        foreach (var a in nc.Allegati) a.DataCaricamento = V(a.DataCaricamento);
        foreach (var c in nc.Changelog) c.Data = V(c.Data);
    }
}
