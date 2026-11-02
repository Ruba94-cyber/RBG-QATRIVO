using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Piano 6.4.0: copertura end-to-end della Propagazione massiva — un Master apre il dialogo dal
/// pulsante "Propaga tutti i '!'" di una colonna componente (o dal pulsante a livello veicolo per tutti i componenti),
/// confirma con una nota unica obbligatoria, e le celle "!" concordanti del veicolo selezionato diventano
/// "Propagato"; un Viewer non vede nessuno dei due pulsanti. Stessa tecnica di semina di
/// AuditPropagazioneTests (JSON noti su disco via AppState, mai pilotando la UI).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Audit")]
public sealed class AuditPropagazioneMassivaTests : IDisposable
{
    private const string EcuA = "Componente-E2E-MASSIVA-A";
    private const string EcuB = "Componente-E2E-MASSIVA-B";
    private const string RequisitoId1 = "R-E2E-MASSIVA-1";
    private const string RequisitoId2 = "R-E2E-MASSIVA-2";
    private const string VeicoloOrigine = "Veicolo-E2E-Massiva-Origine";
    private const string VeicoloTarget = "Veicolo-E2E-Massiva-Target";
    private const string ViewerNome = "QA Viewer E2E Massiva";
    private const string ViewerPin = "9192";

    private readonly TestWorkspace _workspace;

    public AuditPropagazioneMassivaTests()
    {
        _workspace = TestWorkspace.CreateLoggedOut(nameof(AuditPropagazioneMassivaTests));
        SeedScenarioPropagazioneMassiva(_workspace);
    }

    public void Dispose() => _workspace.Dispose();

    private static List<ValutazioneRequisitoEcuVeicolo> LeggiValutazioni(TestWorkspace workspace)
    {
        var paths = new DataPaths(workspace.WorkspaceDir);
        return new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(paths.ValutazioniRequisitoEcuVeicoloFile).Load();
    }

    /// <summary>Due componente condivise dal veicolo target, ciascuna con un Test diretto solo sul veicolo di
    /// origine: entrambe le coppie (EcuA×R1, EcuB×R2) risultano "!" per VeicoloTarget, con un'unica sorgente
    /// concordante ciascuna — dà un vero scenario "Propaga tutti i '!' di questa componente" (una coppia) e "di
    /// tutti i componenti" (entrambe).</summary>
    private static void SeedScenarioPropagazioneMassiva(TestWorkspace ws)
    {
        var configService = new AppConfigService(ws.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var viewer = new Operatore
        {
            Nome = ViewerNome,
            Iniziali = "QM",
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
            new() { Nome = VeicoloTarget, ModificatoDa = "UITests" },
        });
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = EcuA, VeicoliAssociati = { VeicoloOrigine, VeicoloTarget }, ModificatoDa = "UITests" },
            new() { Nome = EcuB, VeicoliAssociati = { VeicoloOrigine, VeicoloTarget }, ModificatoDa = "UITests" },
        });
        state.Master.Requisiti.Save(new List<Requisito>
        {
            new() { Id = RequisitoId1, Testo = "Requisito E2E propagazione massiva 1", ModificatoDa = "UITests" },
            new() { Id = RequisitoId2, Testo = "Requisito E2E propagazione massiva 2", ModificatoDa = "UITests" },
        });

        var tc1 = new TestCase { Id = "TC-E2E-MASSIVA-1", Componente = EcuA, RequisitoId = { RequisitoId1 }, Veicolo = VeicoloOrigine };
        tc1.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V-E2E-M1",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = RequisitoId1, Valore = PunteggioConformita.Conforme } },
        });
        state.TestCases.Save(tc1, true);

        var tc2 = new TestCase { Id = "TC-E2E-MASSIVA-2", Componente = EcuB, RequisitoId = { RequisitoId2 }, Veicolo = VeicoloOrigine };
        tc2.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V-E2E-M2",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = RequisitoId2, Valore = PunteggioConformita.AmpiamenteConforme } },
        });
        state.TestCases.Save(tc2, true);

        // R1 è condiviso da entrambe i componenti: la propagazione dalla riga requisito deve includere entrambe,
        // lasciando fuori la diversa riga R2 presente sul componente-B.
        var tc3 = new TestCase { Id = "TC-E2E-MASSIVA-3", Componente = EcuB, RequisitoId = { RequisitoId1 }, Veicolo = VeicoloOrigine };
        tc3.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V-E2E-M3",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = RequisitoId1, Valore = PunteggioConformita.Conforme } },
        });
        state.TestCases.Save(tc3, true);
    }

    [Fact]
    public void Master_PropagaMassivaPerEcu_LaCellaDiQuellaEcuDiventaPropagata_LAltraRestaCome()
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
            audit.SelezionaVeicolo(VeicoloTarget);

            Wait.ForCondition(() => audit.CellaMostraPropagabile(EcuA, RequisitoId1), "cella '!' su EcuA per il veicolo target");
            Wait.ForCondition(() => audit.CellaMostraPropagabile(EcuB, RequisitoId2), "cella '!' su EcuB per il veicolo target");
            Wait.ForCondition(() => audit.PulsantePropagaEcuVisibile(EcuA), "pulsante 'Propaga tutti i \"!\"' sulla colonna EcuA");

            var dialogo = audit.ApriDialogoPropagaMassivaEcu(EcuA);
            audit.ConfermaPropagazioneMassiva(dialogo, "Confermato in massa su EcuA - E2E");

            Wait.ForCondition(() => audit.CellaMostraValutazioneSalvata(EcuA, RequisitoId1), "cella EcuA marcata come Propagata dopo la conferma massiva");
            var tooltip = audit.TooltipCella(EcuA, RequisitoId1, valutazioneSalvata: true);
            Assert.NotNull(tooltip);
            Assert.Contains("Confermato in massa su EcuA", tooltip);

            // EcuB non era nel perimetro di "Propaga tutti i '!' di EcuA": resta "!" come prima, la
            // propagazione massiva non si è "sparsa" oltre il componente scelta.
            Wait.ForCondition(() => audit.CellaMostraPropagabile(EcuB, RequisitoId2), "cella EcuB resta '!' (fuori dal perimetro della propagazione massiva per EcuA)");
        }
        catch (Exception ex)
        {
            FailureArtifacts.Capture(app, nameof(Master_PropagaMassivaPerEcu_LaCellaDiQuellaEcuDiventaPropagata_LAltraRestaCome), ex);
            throw;
        }
        finally
        {
            app?.Dispose();
        }
    }

    [Fact]
    public void Master_PropagaMassivaPerVeicolo_EntrambeLeEcuDiventanoPropagate()
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
            audit.SelezionaVeicolo(VeicoloTarget);

            Wait.ForCondition(() => audit.PulsantePropagaVeicoloVisibile(), "pulsante 'Propaga tutti i \"!\" di tutti i componenti'");

            var dialogo = audit.ApriDialogoPropagaMassivaVeicolo();
            audit.ConfermaPropagazioneMassiva(dialogo, "Confermato in massa su tutto il veicolo - E2E");

            Wait.ForCondition(() => audit.CellaMostraValutazioneSalvata(EcuA, RequisitoId1), "cella EcuA propagata dalla propagazione massiva a livello veicolo");
            Wait.ForCondition(() => audit.CellaMostraValutazioneSalvata(EcuB, RequisitoId2), "cella EcuB propagata dalla propagazione massiva a livello veicolo");
        }
        catch (Exception ex)
        {
            FailureArtifacts.Capture(app, nameof(Master_PropagaMassivaPerVeicolo_EntrambeLeEcuDiventanoPropagate), ex);
            throw;
        }
        finally
        {
            app?.Dispose();
        }
    }

    [Fact]
    public void Master_PropagaRigaRequisito_PropagaSuTutteLeEcuSoloPerIlRequisitoScelto()
    {
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(_workspace);
            var window = app.WaitForMainWindow();
            Wait.ForCondition(() => Helpers.ElementFinders.ExistsById(window, "Login_Operatore_ComboBox"), "schermata di login");
            new LoginPage(window).LoginAs(TestWorkspace.DefaultOperatorName, TestWorkspace.DefaultOperatorPin);
            Wait.ForCondition(() => MainShellPage.IsShown(window), "shell principale dopo login Master");

            var audit = new MainShellPage(window, app).GoToAudit();
            audit.SelezionaVeicolo(VeicoloTarget);

            var idPulsanteRiga = $"Audit_PropagaRiga_Button_{RequisitoId1}";
            var pulsanteRiga = Helpers.ElementFinders.WaitButtonById(window, idPulsanteRiga);
            pulsanteRiga.SafeInvoke();
            var dialogo = app.WaitForWindowByContent("Audit_PropagaMassiva_Conferma_Button");
            audit.ConfermaPropagazioneMassiva(dialogo, "Confermato per la riga R1 - E2E");

            Wait.ForCondition(() => audit.CellaMostraValutazioneSalvata(EcuA, RequisitoId1), "cella EcuA × R1 propagata dalla riga selezionata");
            Wait.ForCondition(() => audit.CellaMostraValutazioneSalvata(EcuB, RequisitoId1), "cella EcuB × R1 propagata dalla stessa riga");
            Wait.ForCondition(() => audit.CellaMostraPropagabile(EcuB, RequisitoId2), "cella EcuB × R2 esclusa dalla propagazione della riga R1");

            var propagazioni = LeggiValutazioni(_workspace).Where(v =>
                v.Veicolo == VeicoloTarget && v.Origine == OrigineValutazioneRequisito.Propagata && !v.IsArchiviata).ToList();
            Assert.Equal(2, propagazioni.Count);
            Assert.All(propagazioni, v => Assert.Equal(RequisitoId1, v.RequisitoId));
            Assert.Contains(propagazioni, v => v.Componente == EcuA);
            Assert.Contains(propagazioni, v => v.Componente == EcuB);
        }
        catch (Exception ex)
        {
            FailureArtifacts.Capture(app, nameof(Master_PropagaRigaRequisito_PropagaSuTutteLeEcuSoloPerIlRequisitoScelto), ex);
            throw;
        }
        finally
        {
            app?.Dispose();
        }
    }

    [Fact]
    public void Viewer_NonVedeINessunPulsanteDiPropagazioneMassiva()
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
            audit.SelezionaVeicolo(VeicoloTarget);

            // Un Viewer vede comunque le celle "!" (stato invariato), ma nessuno dei due pulsanti di
            // Propagazione massiva è presente — stessa regola di permesso della propagazione singola
            // (AuditPropagazioneTests.Viewer_NonPuoPropagare_LaCellaNonEUnControlloCliccabile).
            Wait.ForCondition(() => Helpers.ElementFinders.ExistsById(window, "Audit_Sottoveicolo_ComboBox") || Helpers.ElementFinders.ExistsById(window, "Audit_Sottoveicolo_Scegli_Button"), "matrice Audit caricata per il Viewer");
            Assert.False(audit.PulsantePropagaEcuVisibile(EcuA));
            Assert.False(audit.PulsantePropagaVeicoloVisibile());
        }
        catch (Exception ex)
        {
            FailureArtifacts.Capture(app, nameof(Viewer_NonVedeINessunPulsanteDiPropagazioneMassiva), ex);
            throw;
        }
        finally
        {
            app?.Dispose();
        }
    }
}
