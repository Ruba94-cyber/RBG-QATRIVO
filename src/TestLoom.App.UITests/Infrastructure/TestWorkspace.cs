using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.UITests.Infrastructure;

/// <summary>
/// Un workspace TestLoom completamente isolato per un singolo test: cartella condivisa temporanea + config.json
/// locale temporaneo (mai %AppData%\TestLoom.App\config.json, mai i dati reali dell'utente), con un
/// operatore Master già pronto e "ricordato" (Config.OperatoreAttivo) così RBG - QATRIVO.exe atterra direttamente
/// sulla Home senza dover pilotare la schermata di login per ogni test — vedi anche <see cref="LoginScreenAvailable"/>
/// per i pochi test che devono verificare davvero la UI di login.
///
/// La semina usa AppState/AppConfigService/MasterDataRepository dello stesso assembly TestLoom.App (stessa
/// tecnica già usata da TestLoom.App.Tests): NON pilota mai la UI da qui, serve solo a scrivere JSON noti sul
/// disco prima che il processo RBG - QATRIVO.exe reale venga avviato.
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    public const string DefaultOperatorName = "QA Master";
    public const string DefaultOperatorPin = "4242";
    public const string DefaultTemplateName = "QA Smoke Template";
    public const string DefaultEcuName = "Componente-QA-01";
    public const string DefaultVehicleName = "Veicolo-QA-01";

    public string RootDir { get; }
    /// <summary>Tema dei workspace di test: Light (deterministico) salvo TestLoom_TEST_THEME=Dark|System per le prove dedicate al tema.</summary>
    public static string TemaDiTest => Environment.GetEnvironmentVariable("TestLoom_TEST_THEME") is { Length: > 0 } t ? t : "Light";

    public string ConfigPath { get; }
    public string WorkspaceDir { get; }

    private TestWorkspace(string rootDir, string? cartellaCondivisa = null)
    {
        RootDir = rootDir;
        ConfigPath = Path.Combine(rootDir, "local", "config.json");
        WorkspaceDir = cartellaCondivisa ?? Path.Combine(rootDir, "workspace");
    }

    /// <summary>Adotta una cartella condivisa gia' esistente invece di crearne una vuota (richiesta utente
    /// 2026-09-22: "fai anche la prova con l'app reale e usando i dati demo veri", cioe' l'archivio di
    /// benchmark sintetico da 100k sotto TestLoom_6_TEST). Non semina nulla: operatori, cataloghi e Non Conformita' sono quelli gia' presenti.
    /// Scrive soltanto un config.json locale isolato, cosi' l'eseguibile non tocca mai la configurazione reale
    /// dell'utente in %AppData%. Dispose() cancella solo RootDir: la cartella adottata non viene mai
    /// rimossa.</summary>
    public static TestWorkspace AdottaCartellaEsistente(string namePrefix, string cartellaCondivisa, string operatore)
    {
        // REGOLE QA: il computer di sviluppo NON contiene né usa MAI la vera
        // cartella TestLoom. L'adozione di una cartella già presente è ammessa
        // solo per fixture disposable marcate TestLoom_6_TEST, non per OneDrive
        // e non per dataset aziendali. Non esiste un override da variabile env.
        var normalized = Path.GetFullPath(cartellaCondivisa);
        var segments = normalized.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!segments.Any(segment => segment.Equals("TestLoom_6_TEST", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "FlaUI vietato su cartelle non disposable. Usare una fixture sintetica " +
                "sotto TestLoom_6_TEST; MAI la vera cartella TestLoom del PC operativo.");
        if (!Directory.Exists(normalized))
            throw new DirectoryNotFoundException("Fixture disposable inesistente: " + normalized);
        // Neppure un collegamento/junction dentro TestLoom_6_TEST deve poter
        // puntare alla vera cartella: ogni segmento del percorso e' controllato.
        var ancestor = new DirectoryInfo(normalized);
        while (ancestor != null)
        {
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Fixture FlaUI non sicura: percorso che attraversa un link/junction.");
            // Un dataset corporate potrebbe accidentalmente contenere una
            // sottocartella chiamata TestLoom_6_TEST. Controllare i genitori.
            if (!string.Equals(ancestor.FullName, normalized, StringComparison.OrdinalIgnoreCase) &&
                (File.Exists(Path.Combine(ancestor.FullName, "operatori.json")) ||
                 File.Exists(Path.Combine(ancestor.FullName, "test-template.json")) ||
                 Directory.Exists(Path.Combine(ancestor.FullName, "testcases"))))
                throw new InvalidOperationException(
                    "Fixture FlaUI annidata in un workspace TestLoom esistente: accesso vietato.");
            ancestor = ancestor.Parent;
        }

        var root = Path.Combine(Path.GetTempPath(), "TestLoom_UITests", $"{namePrefix}_{Guid.NewGuid():N}");
        var ws = new TestWorkspace(root, cartellaCondivisa);
        Directory.CreateDirectory(Path.GetDirectoryName(ws.ConfigPath)!);
        new AppConfigService(ws.ConfigPath).Save(new AppConfig
        {
            CartellaCondivisa = cartellaCondivisa,
            Lingua = "it", TemaInterfaccia = TemaDiTest,
            OperatoreAttivo = operatore,
            UltimaSelezionePagine = { [$"TestEditor.Mode|{operatore}"] = "full" },
            VersioniOnboardingCompletate = new(StringComparer.OrdinalIgnoreCase)
            {
                [OnboardingService.Chiave(operatore, RuoloOperatore.Master)] = OnboardingService.VersioneCorrente,
            },
        });
        return ws;
    }

    /// <summary>Crea un workspace con un solo operatore Master già "loggato" (Config.OperatoreAttivo),
    /// il tour di onboarding già segnato completato (altrimenti l'overlay del tour coprirebbe la UI a ogni
    /// primo avvio di un operatore mai visto) e — se richiesto — un template/componente/veicolo minimi già pronti per
    /// i test di creazione Test Case, deliberatamente con pochissimi campi obbligatori (solo Descrizione breve
    /// + componente) per non dipendere dalle regole dei 4 template reali di prodotto, che possono cambiare.</summary>
    public static TestWorkspace CreateLoggedIn(string namePrefix, bool seedTestFixtures = true, bool funzionalitaNelTemplate = false, bool veicoloNelTemplate = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_UITests", $"{namePrefix}_{Guid.NewGuid():N}");
        var ws = new TestWorkspace(root);
        Directory.CreateDirectory(ws.WorkspaceDir);

        var configService = new AppConfigService(ws.ConfigPath);
        configService.Save(new AppConfig
        {
            CartellaCondivisa = ws.WorkspaceDir,
            Lingua = "it", TemaInterfaccia = TemaDiTest,
            OperatoreAttivo = DefaultOperatorName,
            UltimaSelezionePagine = { [$"TestEditor.Mode|{DefaultOperatorName}"] = "full" },
            VersioniOnboardingCompletate = new(StringComparer.OrdinalIgnoreCase)
            {
                [OnboardingService.Chiave(DefaultOperatorName, RuoloOperatore.Master)] = OnboardingService.VersioneCorrente,
            },
        });

        using var state = new AppState(configService, runBackgroundMaintenance: false);
        SeedOperator(state, DefaultOperatorName, DefaultOperatorPin, RuoloOperatore.Master);
        if (seedTestFixtures) SeedMinimalTestFixtures(state, funzionalitaNelTemplate, veicoloNelTemplate);

        return ws;
    }

    public const string RoleOperatorPin = "9191";

    /// <summary>Come <see cref="CreateLoggedIn"/> ma l'operatore attivo (quello "già loggato" all'avvio di
    /// RBG - QATRIVO.exe) ha il ruolo richiesto invece di essere sempre Master — necessario per verificare dalla UI
    /// reale cosa un operatore Standard/Viewer vede e puo' fare (sola lettura, PIN/chiave di recupero altrui
    /// nascosti in Operatori, azioni Master-only disabilitate...), non solo a livello di ViewModel/unit test.
    /// Il nome dell'operatore include il ruolo (es. "QA Standard") per restare distinguibile a colpo d'occhio
    /// nei log/screenshot di fallimento. Semina comunque i fixture minimi di Test (template/componente/veicolo) con
    /// lo stesso nome "UITests" indipendentemente dal ruolo attivo: la scrittura diretta via AppState non passa
    /// dai controlli di permesso della UI, che sono l'oggetto del test, non un ostacolo al seeding.</summary>
    public static TestWorkspace CreateLoggedInAs(string namePrefix, RuoloOperatore ruolo, bool seedTestFixtures = true)
    {
        var nomeOperatore = $"QA {ruolo}";
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_UITests", $"{namePrefix}_{Guid.NewGuid():N}");
        var ws = new TestWorkspace(root);
        Directory.CreateDirectory(ws.WorkspaceDir);

        var configService = new AppConfigService(ws.ConfigPath);
        configService.Save(new AppConfig
        {
            CartellaCondivisa = ws.WorkspaceDir,
            Lingua = "it", TemaInterfaccia = TemaDiTest,
            OperatoreAttivo = nomeOperatore,
            UltimaSelezionePagine = { [$"TestEditor.Mode|{nomeOperatore}"] = "full" },
            VersioniOnboardingCompletate = new(StringComparer.OrdinalIgnoreCase)
            {
                [OnboardingService.Chiave(nomeOperatore, ruolo)] = OnboardingService.VersioneCorrente,
            },
        });

        using var state = new AppState(configService, runBackgroundMaintenance: false);
        SeedOperator(state, nomeOperatore, RoleOperatorPin, ruolo);
        if (seedTestFixtures) SeedMinimalTestFixtures(state);

        return ws;
    }

    /// <summary>Workspace con operatori già pronti sul disco ma Config.OperatoreAttivo vuoto: RBG - QATRIVO.exe mostra
    /// davvero la schermata di login (FirstRunView, EPrimoAccesso=false perché la cartella non è vuota) invece
    /// di saltarla — usato solo dai test che esercitano la UI di accesso stessa.</summary>
    public static TestWorkspace CreateLoggedOut(string namePrefix)
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_UITests", $"{namePrefix}_{Guid.NewGuid():N}");
        var ws = new TestWorkspace(root);
        Directory.CreateDirectory(ws.WorkspaceDir);

        var configService = new AppConfigService(ws.ConfigPath);
        configService.Save(new AppConfig { CartellaCondivisa = ws.WorkspaceDir, Lingua = "it", TemaInterfaccia = TemaDiTest, OperatoreAttivo = "" });

        using var state = new AppState(configService, runBackgroundMaintenance: false);
        SeedOperator(state, DefaultOperatorName, DefaultOperatorPin, RuoloOperatore.Master);

        return ws;
    }

    /// <summary>Cartella condivisa completamente vuota e nessun operatore: RBG - QATRIVO.exe mostra il primo avvio con
    /// "Carica demo completa" (FirstRunViewModel.PuoInizializzareDemo).</summary>
    public static TestWorkspace CreateEmptyFirstRun(string namePrefix)
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_UITests", $"{namePrefix}_{Guid.NewGuid():N}");
        var ws = new TestWorkspace(root);
        Directory.CreateDirectory(ws.WorkspaceDir);
        new AppConfigService(ws.ConfigPath).Save(new AppConfig { CartellaCondivisa = ws.WorkspaceDir, Lingua = "it", TemaInterfaccia = TemaDiTest, OperatoreAttivo = "" });
        return ws;
    }

    /// <summary>Operatore Master della demo completa sintetica (primo elemento di SeedDemoDataService): il suo PIN e
    /// il ruolo sono quelli del pulsante "Carica demo completa" del primo avvio.</summary>
    public const string DemoCompletaOperatore = "Operatore Prova A";

    /// <summary>Workspace DEMO COMPLETA sintetica: stessi dati di "Carica demo completa" del primo avvio
    /// (<see cref="SeedDemoDataService.GeneraCompletoPerPrimoAvvio"/>: tutti i nomi sono fittizi, nessun dato reale),
    /// con l'operatore Master demo gia' "ricordato" e il tour di onboarding segnato completato, come
    /// <see cref="CreateLoggedIn"/>. La cartella vive sotto %TEMP%\TestLoom_UITests e viene eliminata da Dispose().</summary>
    public static TestWorkspace CreateDemoCompleta(string namePrefix)
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_UITests", $"{namePrefix}_{Guid.NewGuid():N}");
        var ws = new TestWorkspace(root);
        Directory.CreateDirectory(ws.WorkspaceDir);
        SeedDemoDataService.GeneraCompletoPerPrimoAvvio(ws.WorkspaceDir);

        new AppConfigService(ws.ConfigPath).Save(new AppConfig
        {
            CartellaCondivisa = ws.WorkspaceDir,
            Lingua = "it", TemaInterfaccia = TemaDiTest,
            OperatoreAttivo = DemoCompletaOperatore,
            UltimaSelezionePagine = { [$"TestEditor.Mode|{DemoCompletaOperatore}"] = "full" },
            VersioniOnboardingCompletate = new(StringComparer.OrdinalIgnoreCase)
            {
                [OnboardingService.Chiave(DemoCompletaOperatore, RuoloOperatore.Master)] = OnboardingService.VersioneCorrente,
            },
        });
        return ws;
    }

    private static void SeedOperator(AppState state, string nome, string pin, RuoloOperatore ruolo)
    {
        var operatori = state.Master.Operatori.Load();
        var operatore = new Operatore
        {
            Nome = nome,
            Iniziali = nome.Length >= 2 ? nome[..2].ToUpperInvariant() : nome.ToUpperInvariant(),
            PinHash = PinService.Hash(pin),
            RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()),
            Ruolo = ruolo,
            RichiedeCambioPin = false,
            ModificatoDa = "UITests",
        };
        // 6.4.2: un ruolo non sigillato vale Viewer (RuoloSigilloService); il seed passa dall'API reale.
        RuoloSigilloService.Sigilla(operatore);
        operatori.Add(operatore);
        state.Master.Operatori.Save(operatori);
    }

    /// <summary>Un solo template SELEZIONABILE, con solo Descrizione breve + componente (entrambi obbligatori): il
    /// minimo che rende il wizard di creazione Test realmente compilabile dai test UI senza dipendere dai
    /// campi/vincoli dei template di prodotto (Validazione Banco/Veicolo/UnitLogger/UnitLogger Performance).
    /// Il bootstrap di AppState semina questi 4 template di default appena la cartella è vuota — se restano
    /// selezionabili insieme al nostro, il passo 1 del wizard ("scegli il tipo test") mostra 5 pulsanti che
    /// condividono lo stesso AutomationId "Editor_TemplatePicker_Button" (sono ripetizioni dello stesso
    /// DataTemplate), rendendo ambigua qualunque ricerca-e-click. Li archiviamo invece di provare a sovrascrivere
    /// la lista: TestTemplatesDisponibili esclude già i template con Archiviata=true (tranne quello del test
    /// aperto), quindi il wizard mostra un solo pulsante, senza ambiguità.</summary>
    private static void SeedMinimalTestFixtures(AppState state, bool funzionalitaNelTemplate = false, bool veicoloNelTemplate = false)
    {
        state.Master.Componente.Save(new List<Componente> { new() { Nome = DefaultEcuName, ModificatoDa = "UITests" } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = DefaultVehicleName, ModificatoDa = "UITests" } });

        var sezione = new SezioneTemplate
        {
            Nome = "Dettagli",
            Campi = new List<CampoTemplate>
            {
                new() { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.DescrizioneBreve, Obbligatorio = true },
                new() { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.Componente, Obbligatorio = true },
            },
        };
        if (veicoloNelTemplate)
            sezione.Campi.Insert(0, new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.Veicolo, Obbligatorio = true });
        // Solo su richiesta (test di aggiunta rapida): un campo Funzionalita' nel template di base cambierebbe l'ordine
        // dei campi su cui contano altri test UI (es. riordino 'Strumenti usati' / 'Funzionalita'').
        if (funzionalitaNelTemplate)
            sezione.Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.Funzionalita, Obbligatorio = false });
        var templates = state.Master.TestTemplate.Load();
        foreach (var esistente in templates) esistente.Archiviata = true;
        templates.Add(new TestTemplate { Nome = DefaultTemplateName, Icona = "🧪", Sezioni = new List<SezioneTemplate> { sezione }, ModificatoDa = "UITests" });
        state.Master.TestTemplate.Save(templates);
    }

    public void Dispose()
    {
        // Ripulire SOLO le risorse di workspace TEST creati da questa fixture (mai
        // cartelle adottate, potenzialmente appartenenti a un utente). I nuovi marker
        // di sola lettura e le bozze/SQLite vivono appositamente fuori da OneDrive.
        if (WorkspaceDir.StartsWith(RootDir + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            try { WorkspaceReadOnlyService.SetEnabled(WorkspaceDir, false); } catch { }
            try
            {
                var dir = WorkspaceLocalPaths.GetWorkspaceDirectory(WorkspaceDir);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { }
            try
            {
                var dir = FailedSaveDraftService.LocalDirectory(WorkspaceDir);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { }
        }

        try { if (Directory.Exists(RootDir)) Directory.Delete(RootDir, recursive: true); }
        catch { /* best-effort: un file locked da un processo TestLoom non ancora terminato non deve far fallire il test */ }
    }
}
