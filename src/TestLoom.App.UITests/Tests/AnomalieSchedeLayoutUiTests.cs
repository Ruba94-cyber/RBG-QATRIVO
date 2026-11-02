using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using Mouse = FlaUI.Core.Input.Mouse;
using MouseButton = FlaUI.Core.Input.MouseButton;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Layout della pagina Anomalie/CR (6.7.2): maniglia tra le due schede (trascinamento, doppio clic che torna alla
/// proporzione predefinita, proporzione ricordata per operatore dopo il riavvio), pulsante Espandi/Ripristina della CR e
/// modalità Compatto (con la CR aperta il pannello filtri si nasconde; il pulsante Filtri lo riapre e chiude la CR).
/// Le misure sono le posizioni UIA; la modalità di layout si fissa con le dimensioni logiche della finestra, prima di
/// aprire la pagina, così il punto di partenza è deterministico.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
[Trait("Area", "AnomalieCr")]
public sealed class AnomalieSchedeLayoutUiTests : IDisposable
{
    private const string TemplateCrNome = "CR Layout QA";
    // Su questo PC lo schermo è 1280x800 logici (200%): la modalità Standard (larghezza >= 1366) non è raggiungibile,
    // quindi tutti i test lavorano in Compatto, che è anche la modalità in cui la pagina mostra le due schede.
    private const int LarghezzaFinestra = 1200;
    private const int AltezzaFinestra = 780;

    private readonly TestWorkspace _workspace;

    public AnomalieSchedeLayoutUiTests() => _workspace = CreaWorkspace();

    public void Dispose() => _workspace.Dispose();

    /// <summary>Tre NC aperte e due CR sul template di change request: entrambe le schede hanno contenuto.</summary>
    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AnomalieSchedeLayoutUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);

        var templates = state.Master.TestTemplate.Load();
        templates.Add(new TestTemplate { Nome = TemplateCrNome, IsChangeRequest = true, ModificatoDa = "UITests" });
        state.Master.TestTemplate.Save(templates);
        var templateCr = templates.Single(t => t.Nome == TemplateCrNome);

        for (var i = 1; i <= 3; i++)
        {
            state.NonConformita.Save(new NonConformita
            {
                Id = $"NC-LAY-000{i}", DataApertura = DateTime.UtcNow.AddMinutes(-i), Severita = SeveritaAnomalia.Medium,
                StatoWorkflow = StatoAnomalia.InVerifica, Descrizione = $"NC di layout {i}",
            });
        }
        for (var i = 1; i <= 2; i++)
        {
            state.ChangeRequests.Save(new ChangeRequest
            {
                Id = $"CR-LAY-000{i}", Titolo = $"CR di layout {i}", Descrizione = $"Descrizione CR {i}",
                TemplateUid = templateCr.Uid, EcuImpattate = new() { TestWorkspace.DefaultEcuName },
            });
        }
        return workspace;
    }

    /// <summary>Fissa la finestra alla modalità voluta e apre la pagina Anomalie nella scheda Tutte.</summary>
    private static Window ApriAnomalieTutte(TestLoomApplication app)
    {
        var window = app.WaitForMainWindow();
        FinestraNativa.AbilitaDpiAware();
        FinestraNativa.RidimensionaLogici(window, LarghezzaFinestra, AltezzaFinestra);
        new MainShellPage(window).GoToAnomalie();
        ElementFinders.WaitButtonById(window, "Anomalie_SchedaTutte_Toggle").SafeInvoke();
        Wait.ForCondition(() => Visibile(window, "Anomalie_Righe_DataGrid"), "griglia NC visibile nella scheda Tutte");
        return window;
    }

    private static void AssicuraCrAperta(Window window)
    {
        if (!Visibile(window, "Anomalie_Cr_FiltroTesto_Input"))
            ElementFinders.WaitButtonById(window, "Anomalie_Cr_Toggle").SafeInvoke();
        Wait.ForCondition(() => Visibile(window, "Anomalie_Cr_FiltroTesto_Input"), "scheda CR aperta");
    }

    private static bool Visibile(Window window, string automationId)
    {
        var el = ElementFinders.TryFindById(window, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }

    private static System.Drawing.Rectangle Rettangolo(Window window, string automationId) =>
        ElementFinders.WaitById(window, automationId).BoundingRectangle;

    /// <summary>Posizione della maniglia relativa al bordo superiore della finestra: indipendente da dove l'app si apre.</summary>
    private static int OffsetManiglia(Window window) =>
        Rettangolo(window, "Anomalie_Schede_Splitter").Top - window.BoundingRectangle.Top;

    private static System.Drawing.Point CentroManiglia(Window window)
    {
        var r = Rettangolo(window, "Anomalie_Schede_Splitter");
        return new System.Drawing.Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    /// <summary>Trascinamento a passi, come un operatore: Avalonia deve avere il tempo di applicare lo spostamento prima del
    /// rilascio, altrimenti la maniglia torna alla proporzione precedente.</summary>
    private static void TrascinaLentamente(System.Drawing.Point da, System.Drawing.Point a)
    {
        const int passi = 6;
        Mouse.MoveTo(da);
        Mouse.Down(MouseButton.Left);
        for (var i = 1; i <= passi; i++)
        {
            Mouse.MoveTo(new System.Drawing.Point(da.X + (a.X - da.X) * i / passi, da.Y + (a.Y - da.Y) * i / passi));
            Thread.Sleep(40);
        }
        Thread.Sleep(150);
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(300);
    }

    private static Exception Diagnostica(TestLoomApplication? app, string nomeTest, Exception ex)
    {
        var dir = FailureArtifacts.Capture(app, nomeTest, ex);
        return new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
    }

    [Fact]
    public void Maniglia_TrascinaCambiaLaProporzione_RestaDopoRiavvio_EDoppioClicRiportaLaPredefinita()
    {
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(_workspace);
            var window = ApriAnomalieTutte(app);
            AssicuraCrAperta(window);
            Wait.ForCondition(() => Visibile(window, "Anomalie_Schede_Splitter"), "maniglia visibile con entrambe le schede");
            var offsetPredefinito = OffsetManiglia(window);

            // Trascinamento verso il basso: con la proporzione predefinita le anomalie sono già al minimo e la CR ha spazio
            // da cedere (verso l'alto la maniglia non si muove), quindi la maniglia scende e la CR si riduce.
            var centro = CentroManiglia(window);
            TrascinaLentamente(centro, new System.Drawing.Point(centro.X, centro.Y + 60));
            Thread.Sleep(500);
            Assert.True(OffsetManiglia(window) >= offsetPredefinito + 12,
                $"la maniglia non è scesa: prima {offsetPredefinito} px, dopo {OffsetManiglia(window)} px dal bordo della finestra");
            var offsetTrascinato = OffsetManiglia(window);

            // Riavvio: la proporzione scelta dall'operatore è salvata e torna uguale.
            app.Dispose();
            app = TestLoomApplication.Launch(_workspace);
            window = ApriAnomalieTutte(app);
            AssicuraCrAperta(window);
            Wait.ForCondition(() => Visibile(window, "Anomalie_Schede_Splitter"), "maniglia visibile dopo il riavvio");
            Wait.ForCondition(() => Math.Abs(OffsetManiglia(window) - offsetTrascinato) <= 6,
                "la proporzione trascinata sopravvive al riavvio");

            // Doppio clic sulla maniglia: torna alla proporzione predefinita 45/55.
            Mouse.LeftDoubleClick(CentroManiglia(window));
            Wait.ForCondition(() => Math.Abs(OffsetManiglia(window) - offsetPredefinito) <= 6,
                "il doppio clic riporta la maniglia alla proporzione predefinita");
        }
        catch (Exception ex)
        {
            throw Diagnostica(app, nameof(Maniglia_TrascinaCambiaLaProporzione_RestaDopoRiavvio_EDoppioClicRiportaLaPredefinita), ex);
        }
        finally
        {
            app?.Dispose();
        }
    }

    [Fact]
    public void Espandi_OccupaLAreaConLaCr_ERipristinaTornaAlleDueSchede()
    {
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(_workspace);
            var window = ApriAnomalieTutte(app);
            AssicuraCrAperta(window);
            Wait.ForCondition(() => Visibile(window, "Anomalie_Cr_Espandi_Button"), "pulsante Espandi visibile con entrambe le schede");
            var altezzaCrPrima = Rettangolo(window, "Anomalie_Cr_Lista_ListBox").Height;

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_Espandi_Button").SafeInvoke();
            Wait.ForCondition(() => !Visibile(window, "Anomalie_Righe_DataGrid"), "anomalie ridotte: la griglia NC è nascosta");
            Wait.ForCondition(() => !Visibile(window, "Anomalie_Schede_Splitter"), "anomalie ridotte: la maniglia è nascosta");
            Wait.ForCondition(() => Rettangolo(window, "Anomalie_Cr_Lista_ListBox").Height > altezzaCrPrima * 1.3,
                "la CR occupa l'area lasciata libera dalle anomalie");

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_Espandi_Button").SafeInvoke();
            Wait.ForCondition(() => Visibile(window, "Anomalie_Righe_DataGrid") && Visibile(window, "Anomalie_Schede_Splitter"),
                "Ripristina riporta alle due schede con la maniglia");
        }
        catch (Exception ex)
        {
            throw Diagnostica(app, nameof(Espandi_OccupaLAreaConLaCr_ERipristinaTornaAlleDueSchede), ex);
        }
        finally
        {
            app?.Dispose();
        }
    }

    [Fact]
    public void Compatto_ConLaCrAperta_NascondeIFiltri_EIlPulsanteFiltriRiapreIlPannelloEChiudeLaCr()
    {
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(_workspace);
            var window = ApriAnomalieTutte(app);
            Wait.ForCondition(() => !Visibile(window, "Anomalie_Cr_FiltroTesto_Input"), "in Compatto la CR parte chiusa");
            Wait.ForCondition(() => Visibile(window, "Anomalie_FiltroStato_Combo"), "il pannello filtri parte aperto (preferenza predefinita)");

            // Aprire la CR con il pannello filtri aperto: in Compatto il pannello si nasconde.
            ElementFinders.WaitButtonById(window, "Anomalie_Cr_Toggle").SafeInvoke();
            Wait.ForCondition(() => Visibile(window, "Anomalie_Cr_FiltroTesto_Input"), "la CR si apre dalla sua intestazione");
            Wait.ForCondition(() => !Visibile(window, "Anomalie_FiltroStato_Combo"), "con la CR aperta il pannello filtri si nasconde");

            ElementFinders.WaitButtonById(window, "Anomalie_ToggleFiltri_Button").SafeInvoke();
            Wait.ForCondition(() => Visibile(window, "Anomalie_FiltroStato_Combo"), "Filtri riapre il pannello");
            Wait.ForCondition(() => !Visibile(window, "Anomalie_Cr_FiltroTesto_Input"), "Filtri chiude la CR");
        }
        catch (Exception ex)
        {
            throw Diagnostica(app, nameof(Compatto_ConLaCrAperta_NascondeIFiltri_EIlPulsanteFiltriRiapreIlPannelloEChiudeLaCr), ex);
        }
        finally
        {
            app?.Dispose();
        }
    }
}
