using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Condivisione 6.7.0 dalla UI reale: modalita' "Cartella di rete (SMB, LAN o VPN)" contro "OneDrive / SharePoint
/// sincronizzata" (Impostazioni, ComboBox + Applica), cambio della cartella dati da Impostazioni, riavvio, modifica
/// esterna del workspace con TestLoom aperto, cartella resa non disponibile mentre l'app gira, diagnostica (Centro
/// Integrita').
/// Ogni test usa un workspace temporaneo isolato (TestWorkspace) e un processo TestLoom.exe proprio: il lock di istanza
/// singola impedisce due sessioni dell'app sullo stesso PC, quindi le modifiche "da un altro PC" vengono simulate
/// scrivendo sul disco condiviso dal processo di test (stessi repository dell'app, come gia' fa TestCoverageSeed).
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Condivisione670")]
public sealed class Condivisione670UiTests
{
    private const string EtichettaRete = "Cartella di rete (SMB, LAN o VPN)";
    private const string FrammentoMotivoManuale = "scelta manualmente per questo PC";
    private const string IdTestSeminato = "TC-QA-0701";
    private const string TitoloTestSeminato = "Test condiviso per collaudo modalita di rete";

    /// <summary>Primo avvio su un workspace nuovo: la schermata "Apertura di TestLoom..." puo' restare oltre i 30 s del default.</summary>
    private static readonly TimeSpan AvvioMassimo = TimeSpan.FromSeconds(120);

    // ---------------------------------------------------------------- helper di sessione

    /// <summary>Crea un Test sul disco condiviso del workspace con i repository reali dell'app. Non pilota la UI.</summary>
    private static void SeminaTest(TestWorkspace ws, string id)
    {
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        TestCoverageSeed.SeedTest(state, id, TitoloTestSeminato, TestCoverageSeed.UidTemplatePrincipale(state),
            TestWorkspace.DefaultEcuName, Esito.Conforme, StatoLavoroTest.Pianificato, creatoDa: "Altro Utente");
    }

    private static Window ApriImpostazioniGenerale(TestLoomApplication app)
    {
        var window = app.WaitForMainWindow(AvvioMassimo);
        var shell = new MainShellPage(window);
        shell.DismissNotificationsIfShown();
        shell.GoToImpostazioni().VaiA("generale");
        return window;
    }

    /// <summary>Testo "Modalita' in uso: ..." del blocco Modalita' cartella condivisa (AutomationId dedicato).</summary>
    private static string ModalitaInUso(Window window) =>
        ElementFinders.WaitById(window, "Impostazioni_ModalitaCondivisione_Effettiva_Text").Name ?? "";

    private static void ScegliModalitaEApplica(Window window, string etichetta)
    {
        ElementFinders.WaitComboBoxById(window, "Impostazioni_ModalitaCondivisione_ComboBox").SelectByText(etichetta);
        ElementFinders.WaitButtonById(window, "Impostazioni_ModalitaCondivisione_Applica_Button").SafeInvoke();
    }

    private static void AttendiMessaggioSalvataggioModalita(Window window) =>
        Wait.ForCondition(() => ModalitaInUso(window).Contains(EtichettaRete) && ModalitaInUso(window).Contains(FrammentoMotivoManuale),
            "modalita' 'Cartella di rete' applicata e dichiarata come scelta manuale del PC",
            TimeSpan.FromSeconds(20));

    private static IReadOnlyList<string> RigheTestVisibili(TestListPage lista) => lista.VisibleRowIds();

    /// <summary>Ritorna l'eccezione di diagnostica da rilanciare (screenshot/albero UI salvati sotto TestResults).</summary>
    private static Exception Diagnostica(TestLoomApplication? app, string nomeTest, Exception ex)
    {
        var dir = FailureArtifacts.Capture(app, nomeTest, ex);
        return new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
    }

    /// <summary>Percorso UNC di amministrazione (UNC localhost\C$\...) per una cartella locale, o null se non ricavabile.</summary>
    private static string? PercorsoUncAdminShare(string percorsoLocale)
    {
        var full = Path.GetFullPath(percorsoLocale);
        var radice = Path.GetPathRoot(full);
        if (radice == null || radice.Length < 3 || radice[1] != ':') return null;
        return @"\" + @"\localhost\" + char.ToUpperInvariant(radice[0]) + "$\\" + full[3..];
    }

    // ---------------------------------------------------------------- 1. modalita' e cartella: persistenza al riavvio

    [Fact]
    public void ModalitaRete_SalvataERiavviata_RestaInUsoEIDatiVisibili()
    {
        const string nome = nameof(ModalitaRete_SalvataERiavviata_RestaInUsoEIDatiVisibili);
        using var ws = TestWorkspace.CreateLoggedIn(nome);
        SeminaTest(ws, IdTestSeminato);
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(ws);
            var window = ApriImpostazioniGenerale(app);
            ScegliModalitaEApplica(window, EtichettaRete);
            AttendiMessaggioSalvataggioModalita(window);

            Wait.ForCondition(() => new AppConfigService(ws.ConfigPath).Load().ModalitaCartellaCondivisa == ModalitaCondivisioneService.ValoreRete,
                "config locale con modalita' Rete", TimeSpan.FromSeconds(10));

            app.Dispose();
            app = null;

            app = TestLoomApplication.Launch(ws);
            window = ApriImpostazioniGenerale(app);
            Wait.ForCondition(() => ModalitaInUso(window).Contains(EtichettaRete),
                "dopo il riavvio la modalita' Cartella di rete resta in uso", TimeSpan.FromSeconds(30));
            Assert.Contains(FrammentoMotivoManuale, ModalitaInUso(window));

            var lista = new MainShellPage(window).GoToTestList();
            Wait.ForCondition(() => RigheTestVisibili(lista).Contains(IdTestSeminato),
                "il Test seminato sulla cartella di rete e' visibile dopo il riavvio", TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) { throw Diagnostica(app, nome, ex); }
        finally { app?.Dispose(); }
    }

    [Fact]
    public void CartellaDatiUncAdminShare_ModalitaRete_SalvataERiavviata()
    {
        const string nome = nameof(CartellaDatiUncAdminShare_ModalitaRete_SalvataERiavviata);
        using var ws = TestWorkspace.CreateLoggedIn(nome);
        SeminaTest(ws, IdTestSeminato);

        var unc = PercorsoUncAdminShare(ws.WorkspaceDir);
        if (unc == null || !Directory.Exists(unc))
        {
            // xUnit 2.9 non ha Skip a runtime: il caso viene dichiarato a output e NON conta come verificato.
            Console.WriteLine($"SKIP {nome}: admin share non accessibile ({unc ?? "percorso non ricavabile"}). " +
                              "Serve un account amministratore locale e il servizio Server attivo.");
            return;
        }

        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(ws);
            var window = ApriImpostazioniGenerale(app);

            ElementFinders.WaitTextBoxById(window, "Impostazioni_Cartella_TextBox").SetText(unc);
            ElementFinders.WaitButtonById(window, "Impostazioni_Cartella_Aggiorna_Button").SafeInvoke();
            Wait.ForCondition(() => new AppConfigService(ws.ConfigPath).Load().CartellaCondivisa == unc,
                "cartella dati cambiata sul percorso UNC", TimeSpan.FromSeconds(60));

            ScegliModalitaEApplica(window, EtichettaRete);
            AttendiMessaggioSalvataggioModalita(window);
            Wait.ForCondition(() => new AppConfigService(ws.ConfigPath).Load().ModalitaCartellaCondivisa == ModalitaCondivisioneService.ValoreRete,
                "config locale con modalita' Rete sul percorso UNC", TimeSpan.FromSeconds(10));

            app.Dispose();
            app = null;

            app = TestLoomApplication.Launch(ws);
            window = ApriImpostazioniGenerale(app);
            Wait.ForCondition(() => ModalitaInUso(window).Contains(EtichettaRete),
                "dopo il riavvio la modalita' Rete resta in uso sul percorso UNC", TimeSpan.FromSeconds(30));

            var lista = new MainShellPage(window).GoToTestList();
            Wait.ForCondition(() => RigheTestVisibili(lista).Contains(IdTestSeminato),
                "il Test seminato e' leggibile dal percorso UNC dopo il riavvio", TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) { throw Diagnostica(app, nome, ex); }
        finally { app?.Dispose(); }
    }

    // ---------------------------------------------------------------- 2. modifica esterna con l'app aperta

    [Fact]
    public void ModificaEsternaAlloCondiviso_ConAppAperta_VisibileDopoNavigazione()
    {
        const string nome = nameof(ModificaEsternaAlloCondiviso_ConAppAperta_VisibileDopoNavigazione);
        using var ws = TestWorkspace.CreateLoggedIn(nome);
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(ws);
            var window = app.WaitForMainWindow(AvvioMassimo);
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();

            var listaPrima = shell.GoToTestList();
            Wait.ForCondition(() => RigheTestVisibili(listaPrima).Count == 0,
                "nessun Test prima della modifica esterna", TimeSpan.FromSeconds(20));

            // Un altro PC scrive sul disco condiviso mentre questa istanza e' aperta.
            SeminaTest(ws, IdTestSeminato);

            shell = new MainShellPage(app.WaitForMainWindow());
            shell.GoToHome();
            var listaDopo = shell.GoToTestList();
            Wait.ForCondition(() => RigheTestVisibili(listaDopo).Contains(IdTestSeminato),
                "il Test scritto dall'altro PC compare dopo navigazione Home -> Lista Test", TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) { throw Diagnostica(app, nome, ex); }
        finally { app?.Dispose(); }
    }

    // ---------------------------------------------------------------- 3. cartella resa non disponibile mentre l'app gira

    /// <summary>
    /// Esito 2026-10-13: il banner "Cartella condivisa non disponibile" non compariva dopo la rimozione del collegamento
    /// alla cartella dati, nemmeno con "Aggiorna ora". Causa (6.7.1): il banner era subordinato a CartellaValida, che e'
    /// falso proprio quando la cartella manca, e "Aggiorna ora" / il giro periodico uscivano subito se !CartellaValida:
    /// nessuna sonda ricalcolava lo stato. Ora una sonda periodica (MonitorRaggiungibilitaRadice) ricontrolla la radice.
    /// La simulazione e' equa: la cartella configurata e' una junction (mklink /J) verso i dati reali; rimuovere la junction
    /// rende la cartella assente senza toccare i dati, come un disco di rete smontato.
    /// </summary>
    [Fact]
    public void CartellaRinominataMentreAperta_BannerEAppSenzaCrash_IlRipristinoRiallinea()
    {
        const string nome = nameof(CartellaRinominataMentreAperta_BannerEAppSenzaCrash_IlRipristinoRiallinea);
        using var ws = TestWorkspace.CreateLoggedIn(nome);
        // La cartella dati configurata e' un collegamento (junction) ai dati reali, che restano sempre in "reale".
        // Non si puo' rinominare la cartella con TestLoom aperto: Windows nega il Move (file aperti dal processo, vedi
        // l'esito di questo test nel log). Togliendo e rimettendo il collegamento la cartella configurata risulta
        // assente mentre i dati restano intatti.
        var collegamento = ws.WorkspaceDir;
        var reale = collegamento + "_reale";
        Directory.Move(collegamento, reale);
        CreaJunction(collegamento, reale);
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(ws);
            var window = app.WaitForMainWindow(AvvioMassimo);
            new MainShellPage(window).DismissNotificationsIfShown();
            // Prima della simulazione la cartella e' buona: nessun banner. Cosi' il banner dopo non puo' essere un residuo.
            Assert.Null(VisibilePerIdSafe(window, "Main_StatoCondivisione_Text"));

            Directory.Delete(collegamento);
            Assert.True(Directory.Exists(reale) && !Directory.Exists(collegamento),
                "la cartella dati non e' piu' raggiungibile, ma i dati reali restano nella cartella di destinazione");

            string testoBanner = "";
            Wait.ForCondition(() =>
            {
                TryAggiornaOra(window);
                var banner = VisibilePerIdSafe(window, "Main_StatoCondivisione_Text");
                if (banner == null) return false;
                testoBanner = TestoBanner(banner);
                return true;
            }, "banner di stato della cartella condivisa mostrato con la cartella non raggiungibile", TimeSpan.FromSeconds(120));
            Assert.True(app.IsRunning, "TestLoom non deve chiudersi con la cartella non raggiungibile");
            Assert.False(string.IsNullOrWhiteSpace(testoBanner), "il banner deve spiegare il problema");

            // L'interfaccia resta navigabile: nessun crash, nessuna pagina bloccata.
            new MainShellPage(window).GoToImpostazioni();
            Assert.True(ElementFinders.ExistsById(window, "Impostazioni_Sidebar_List"),
                "Impostazioni resta navigabile con la cartella non disponibile (banner: " + testoBanner + ")");

            CreaJunction(collegamento, reale);

            Wait.ForCondition(() =>
            {
                TryAggiornaOra(window);
                return VisibilePerIdSafe(window, "Main_StatoCondivisione_Text") == null;
            }, "banner di stato sparisce dopo il ripristino della cartella", TimeSpan.FromSeconds(120));
            Assert.True(app.IsRunning, "TestLoom resta aperta anche dopo il ripristino");
            var shell = new MainShellPage(app.WaitForMainWindow());
            shell.GoToHome();
            Assert.False(shell.HasNavigationError, "la Home torna navigabile dopo il ripristino");
        }
        catch (Exception ex) { throw Diagnostica(app, nome, ex); }
        finally
        {
            // Ripristino comunque: TestWorkspace.Dispose cancella la cartella configurata, non quella reale.
            if (!Directory.Exists(collegamento) && Directory.Exists(reale))
            {
                try { CreaJunction(collegamento, reale); } catch { }
            }
            app?.Dispose();
        }
    }

    /// <summary>Preme "Aggiorna ora" se e' presente; il ricalcolo dello stato avviene solo con questo giro di rilettura.</summary>
    private static void TryAggiornaOra(Window window)
    {
        var pulsante = ElementFinders.TryFindById(window, "Main_AggiornaOra_Button");
        if (pulsante == null) return;
        try { pulsante.AsButton().SafeInvoke(); } catch { }
    }

    /// <summary>Crea un collegamento di directory (junction, nessun privilegio amministrativo richiesto).</summary>
    private static void CreaJunction(string collegamento, string destinazione)
    {
        using var processo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{collegamento}\" \"{destinazione}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })
            ?? throw new InvalidOperationException("impossibile avviare cmd.exe per mklink /J");
        var output = processo.StandardOutput.ReadToEnd() + processo.StandardError.ReadToEnd();
        processo.WaitForExit();
        if (processo.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J fallito ({processo.ExitCode}): {output.Trim()}");
    }

    private static AutomationElement? VisibilePerIdSafe(Window window, string automationId) =>
        ShellHomeSettingsCoverageSupport.VisibilePerId(window, automationId);

    /// <summary>Testo del banner: l'id sta sul TextBlock che contiene il messaggio (il Border non e' esposto a UIA).</summary>
    private static string TestoBanner(AutomationElement testo) => testo.Name ?? "";

    // ---------------------------------------------------------------- 4. diagnostica (Centro Integrita')

    /// <summary>
    /// Verifica voluta: il Centro Integrita' deve dichiarare modalita' e cartella in uso. Oggi mostra solo
    /// "Cartella operativa: accessibile|non disponibile" e "Modalita' visualizzatore" (WorkspaceDiagnosticSnapshotService),
    /// mentre la riga "Modalita' cartella condivisa" esiste solo nel file di esportazione diagnostica (DiagnosticExportService).
    /// </summary>
    [Fact]
    public void CentroIntegrita_MostraModalitaCondivisioneInUso()
    {
        const string nome = nameof(CentroIntegrita_MostraModalitaCondivisioneInUso);
        using var ws = TestWorkspace.CreateLoggedIn(nome);
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(ws);
            var window = ApriImpostazioniGenerale(app);
            var page = new Post653ImpostazioniPage(window, app).ApriCentroIntegrita();
            Wait.ForCondition(() => page.Status.Contains("Fotografia locale completata"), "fotografia locale completata");
            Assert.Contains("Modalità cartella condivisa", page.Details);
        }
        catch (Exception ex) { throw Diagnostica(app, nome, ex); }
        finally { app?.Dispose(); }
    }

    /// <summary>Stesso bug della verifica di modalita', per la cartella: il Centro Integrita' non riporta il percorso dati in uso.</summary>
    [Fact]
    public void CentroIntegrita_MostraPercorsoCartellaInUso()
    {
        const string nome = nameof(CentroIntegrita_MostraPercorsoCartellaInUso);
        using var ws = TestWorkspace.CreateLoggedIn(nome);
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(ws);
            var window = ApriImpostazioniGenerale(app);
            var page = new Post653ImpostazioniPage(window, app).ApriCentroIntegrita();
            Wait.ForCondition(() => page.Status.Contains("Fotografia locale completata"), "fotografia locale completata");
            Assert.Contains(ws.WorkspaceDir, page.Details);
        }
        catch (Exception ex) { throw Diagnostica(app, nome, ex); }
        finally { app?.Dispose(); }
    }
}
