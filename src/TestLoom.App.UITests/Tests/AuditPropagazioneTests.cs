using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Piano 6.3.1 §1 (seconda consegna): copertura end-to-end obbligatoria della propagazione esplicita
/// "!" — un Master apre il dialogo dalla cella, sceglie un sottoinsieme di veicoli propagabili, conferma con
/// nota, e la cella scelta diventa "Propagato" con tooltip che riporta la nota; un Viewer non può propagare
/// (nessun controllo cliccabile sulla cella "!"). Workspace seminato direttamente via AppState (stessa tecnica
/// di TestWorkspace/TestLoom.App.Tests: JSON noti su disco PRIMA di avviare RBG - QATRIVO.exe, mai pilotando la UI per
/// la semina) invece di estendere TestWorkspace, per restare nell'ownership di questa consegna (non toccare
/// l'infrastruttura condivisa più del necessario).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Audit")]
public sealed class AuditPropagazioneTests : IDisposable
{
    private const string Componente = "Componente-E2E";
    private const string RequisitoId = "R-E2E";
    private const string VeicoloOrigine = "Veicolo-E2E-Origine";
    private const string VeicoloDestinazioneA = "Veicolo-E2E-DestA";
    private const string VeicoloDestinazioneB = "Veicolo-E2E-DestB";
    private const string ViewerNome = "QA Viewer E2E";
    private const string ViewerPin = "9191";

    private readonly TestWorkspace _workspace;

    public AuditPropagazioneTests()
    {
        _workspace = TestWorkspace.CreateLoggedOut(nameof(AuditPropagazioneTests));
        SeedScenarioPropagazione(_workspace);
    }

    public void Dispose() => _workspace.Dispose();

    /// <summary>componente condivisa da tre veicoli, un Test diretto solo su VeicoloOrigine: VeicoloDestinazioneA e
    /// VeicoloDestinazioneB risultano entrambi "!" (propagabili dalla stessa sorgente) — dà un vero
    /// sottoinsieme da scegliere nel dialogo (propaga solo ad A, lascia B invariato).</summary>
    private static void SeedScenarioPropagazione(TestWorkspace ws)
    {
        var configService = new AppConfigService(ws.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var viewer = new Operatore
        {
            Nome = ViewerNome,
            Iniziali = "QV",
            PinHash = PinService.Hash(ViewerPin),
            RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()),
            Ruolo = RuoloOperatore.Viewer,
            RichiedeCambioPin = false,
            ModificatoDa = "UITests",
        };
        state.Master.Operatori.Save(state.Master.Operatori.Load().Append(viewer).ToList());

        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = VeicoloOrigine, ModificatoDa = "UITests" },
            new() { Nome = VeicoloDestinazioneA, ModificatoDa = "UITests" },
            new() { Nome = VeicoloDestinazioneB, ModificatoDa = "UITests" },
        });
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = Componente, VeicoliAssociati = { VeicoloOrigine, VeicoloDestinazioneA, VeicoloDestinazioneB }, ModificatoDa = "UITests" },
        });
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = RequisitoId, Testo = "Requisito E2E propagazione", ModificatoDa = "UITests" } });

        var tc = new TestCase { Id = "TC-E2E-PROP", Componente = Componente, RequisitoId = { RequisitoId }, Veicolo = VeicoloOrigine };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V-E2E",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = RequisitoId, Valore = PunteggioConformita.Conforme } },
        });
        state.TestCases.Save(tc, true);
    }

    [Fact]
    public void Master_PropagaASottoinsiemeDiVeicoli_LaCellaScoltaDiventaPropagataConNotaNelTooltip()
    {
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(_workspace);
            var window = app.WaitForMainWindow();
            Wait.ForCondition(() => Helpers.ElementFinders.ExistsById(window, "Login_Operatore_ComboBox"), "schermata di login");
            new LoginPage(window).LoginAs(TestWorkspace.DefaultOperatorName, TestWorkspace.DefaultOperatorPin);
            Wait.ForCondition(() => MainShellPage.IsShown(window), "shell principale dopo login Master");

            var shell = new MainShellPage(window, app);
            var audit = shell.GoToAudit();
            audit.SelezionaVeicolo(VeicoloDestinazioneA);

            Wait.ForCondition(() => audit.CellaMostraPropagabile(Componente, RequisitoId), "cella '!' per il veicolo di destinazione A");

            var dialogo = audit.ApriDialogoPropaga(Componente, RequisitoId);
            const string nota = "Confermato identico su banco E2E";
            // Solo il veicolo cliccato (A) resta selezionato: B, elencato anch'esso nel dialogo perché
            // ugualmente propagabile dalla stessa sorgente, NON viene toccato — vera scelta di un
            // sottoinsieme, non "tutti" implicito.
            audit.ConfermaPropagazione(dialogo, new[] { VeicoloDestinazioneA }, nota);

            Wait.ForCondition(() => audit.CellaMostraValutazioneSalvata(Componente, RequisitoId), "cella A marcata come Propagata dopo la conferma");
            var tooltip = audit.TooltipCella(Componente, RequisitoId, valutazioneSalvata: true);
            Assert.NotNull(tooltip);
            Assert.Contains(nota, tooltip);
            Assert.Contains("propagazione", tooltip, StringComparison.OrdinalIgnoreCase);

            // B non è stato selezionato nel dialogo: resta "!" come prima, la propagazione non si è
            // "sparsa" da sola sugli altri veicoli propagabili.
            audit.SelezionaVeicolo(VeicoloDestinazioneB);
            Wait.ForCondition(() => audit.CellaMostraPropagabile(Componente, RequisitoId), "cella B resta '!' (non selezionata nel dialogo)");
        }
        catch (Exception ex)
        {
            FailureArtifacts.Capture(app, nameof(Master_PropagaASottoinsiemeDiVeicoli_LaCellaScoltaDiventaPropagataConNotaNelTooltip), ex);
            throw;
        }
        finally
        {
            app?.Dispose();
        }
    }

    [Fact]
    public void Viewer_NonPuoPropagare_LaCellaNonEUnControlloCliccabile()
    {
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(_workspace);
            var window = app.WaitForMainWindow();
            Wait.ForCondition(() => Helpers.ElementFinders.ExistsById(window, "Login_Operatore_ComboBox"), "schermata di login");
            new LoginPage(window).LoginAs(ViewerNome, ViewerPin);
            Wait.ForCondition(() => MainShellPage.IsShown(window), "shell principale dopo login Viewer");

            var shell = new MainShellPage(window, app);
            var audit = shell.GoToAudit();
            audit.SelezionaVeicolo(VeicoloDestinazioneA);

            // Un Viewer vede comunque lo stato "!" (la cella non sparisce), ma senza nessun controllo
            // cliccabile che apra il dialogo: niente AutomationId "Audit_Propagabile_...Button" (Button),
            // la cella è un Border passivo (vedi AuditView.axaml.cs, branch "vm.EMaster").
            Wait.ForCondition(() => !audit.CellaMostraPropagabile(Componente, RequisitoId), "cella '!' NON cliccabile per un Viewer");
        }
        catch (Exception ex)
        {
            FailureArtifacts.Capture(app, nameof(Viewer_NonPuoPropagare_LaCellaNonEUnControlloCliccabile), ex);
            throw;
        }
        finally
        {
            app?.Dispose();
        }
    }
}
