using System.Linq;
using FlaUI.Core.AutomationElements;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Paginazione della pagina Anomalie/CR (250 righe per pagina, RighePageSize in NonConformitaViewModel):
/// pulsanti Prima pagina / Precedente / Successiva della griglia NC e della scheda CR, con 251 elementi per
/// lista per avere due pagine. Nessuna suite precedente ha guidato questi comandi (COPERTURA_FLAUI.md).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class AnomalieCrCoveragePaginazioneUiTests : UiTestBase
{
    private const int NumeroElementi = 251;

    public AnomalieCrCoveragePaginazioneUiTests() : base(CreaWorkspace()) { }

    /// <summary>251 NC aperte e 251 Change Request, seminate via AppState (stessa tecnica delle altre suite NC).</summary>
    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AnomalieCrCoveragePaginazioneUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var templates = state.Master.TestTemplate.Load();
        templates.Add(new TestTemplate { Nome = "CR Template QA", IsChangeRequest = true, ModificatoDa = "UITests" });
        state.Master.TestTemplate.Save(templates);
        var templateCr = templates.Single(t => t.Nome == "CR Template QA");

        for (var i = 1; i <= NumeroElementi; i++)
        {
            state.NonConformita.Save(new NonConformita
            {
                Id = $"NC-QA-{i:0000}", DataApertura = DateTime.UtcNow.AddMinutes(-i), Severita = SeveritaAnomalia.Medium,
                StatoWorkflow = StatoAnomalia.InVerifica, Descrizione = $"NC di paginazione {i}",
            });
            state.ChangeRequests.Save(new ChangeRequest
            {
                Id = $"CR-QA-{i:0000}", Titolo = $"CR paginazione {i}", Descrizione = $"CR di paginazione {i}",
                TemplateUid = templateCr.Uid, EcuImpattate = new() { TestWorkspace.DefaultEcuName },
            });
        }
        return workspace;
    }

    /// <summary>Id delle Change Request mostrate nella scheda CR, in ordine verticale.</summary>
    private static IReadOnlyList<string> IdCr(Window window) =>
        ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox")
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .Where(t => (t.Name ?? "").StartsWith("CR-", StringComparison.Ordinal) && t.BoundingRectangle.Height > 0)
            .OrderBy(t => t.BoundingRectangle.Top).Select(t => t.Name!).ToList();

    [Fact]
    public void PaginazioneNc_SuccessivaPrecedenteEPrimaPagina_CambianoLeRigheMostrate()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 20, pausaMs: 300) == NumeroElementi,
                $"tutte le {NumeroElementi} NC aperte sono nel totale filtrato");
            Wait.ForCondition(() => anomalie.TestoPaginazione().Contains("Pagina 1 / 2"),
                "la prima pagina della griglia mostra 'Pagina 1 / 2'");

            var precedente = ElementFinders.WaitButtonById(window, "Anomalie_PaginaPrecedente_Button");
            Assert.False(precedente.IsEnabled, "sulla prima pagina il pulsante Precedente e' disabilitato");
            var primaPagina = anomalie.IdRighe();
            Assert.NotEmpty(primaPagina);

            ElementFinders.WaitButtonById(window, "Anomalie_PaginaSuccessiva_Button").SafeInvoke();
            Wait.ForCondition(() => anomalie.TestoPaginazione().Contains("Pagina 2 / 2"),
                "Successiva porta alla pagina 2 di 2");
            var secondaPagina = anomalie.IdRighe();
            Assert.NotEmpty(secondaPagina);
            Assert.Empty(primaPagina.Intersect(secondaPagina));

            ElementFinders.WaitButtonById(window, "Anomalie_PaginaPrecedente_Button").SafeInvoke();
            Wait.ForCondition(() => anomalie.TestoPaginazione().Contains("Pagina 1 / 2"),
                "Precedente riporta alla pagina 1");
            Assert.Equal(primaPagina, anomalie.IdRighe());

            ElementFinders.WaitButtonById(window, "Anomalie_PaginaSuccessiva_Button").SafeInvoke();
            Wait.ForCondition(() => anomalie.TestoPaginazione().Contains("Pagina 2 / 2"), "di nuovo alla pagina 2");
            ElementFinders.WaitButtonById(window, "Anomalie_PrimaPagina_Button").SafeInvoke();
            Wait.ForCondition(() => anomalie.TestoPaginazione().Contains("Pagina 1 / 2"),
                "Prima pagina riporta alla pagina 1");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PaginazioneNc_SuccessivaPrecedenteEPrimaPagina_CambianoLeRigheMostrate), ex);
        }
    }

    [Fact]
    public void PaginazioneNc_CambioFiltro_RiportaLaListaAllaPrimaPagina()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            Wait.ForCondition(() => anomalie.TestoPaginazione().Contains("Pagina 1 / 2"), "parte dalla pagina 1 di 2");
            ElementFinders.WaitButtonById(window, "Anomalie_PaginaSuccessiva_Button").SafeInvoke();
            Wait.ForCondition(() => anomalie.TestoPaginazione().Contains("Pagina 2 / 2"), "si e' spostato alla pagina 2");

            ElementFinders.WaitTextBoxById(window, "Anomalie_FiltroTesto_Input").SetText("NC-QA-0007");
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 15, pausaMs: 300) == 1,
                "la ricerca per Id lascia una sola NC");
            Wait.ForCondition(() => anomalie.TestoPaginazione().Contains("Pagina 1 / 1"),
                "con un solo risultato la paginazione torna a 'Pagina 1 / 1' senza pagina 2 residua");
            Assert.Equal(new[] { "NC-QA-0007" }, anomalie.IdRighe());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PaginazioneNc_CambioFiltro_RiportaLaListaAllaPrimaPagina), ex);
        }
    }

    [Fact]
    public void PaginazioneCr_SuccessivaEPrecedente_CambianoLeChangeRequestMostrate()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_PaginazioneCr_Text").Name?.Contains("Pagina 1 / 2") == true,
                "la scheda CR mostra 'Pagina 1 / 2'");
            var primaPagina = IdCr(window);
            Assert.NotEmpty(primaPagina);

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_PaginaSuccessiva_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_PaginazioneCr_Text").Name?.Contains("Pagina 2 / 2") == true,
                "Successiva porta la scheda CR alla pagina 2 di 2");
            var secondaPagina = IdCr(window);
            Assert.NotEmpty(secondaPagina);
            Assert.Empty(primaPagina.Intersect(secondaPagina));

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_PaginaPrecedente_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_PaginazioneCr_Text").Name?.Contains("Pagina 1 / 2") == true,
                "Precedente riporta la scheda CR alla pagina 1");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PaginazioneCr_SuccessivaEPrecedente_CambianoLeChangeRequestMostrate), ex);
        }
    }

    [Fact]
    public void PaginazioneCr_SchermoStrettoConFiltriAvanzati_AggiornaListaERicerca()
    {
        try
        {
            var window = App.WaitForMainWindow();
            FinestraNativa.AbilitaDpiAware();
            FinestraNativa.RidimensionaLogici(window, 1024, 700);
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();

            var expander = ElementFinders.WaitById(window, "Anomalie_Cr_Filtri_Expander");
            Assert.True(expander.Patterns.ExpandCollapse.TryGetPattern(out var expandCollapse),
                "il pannello avanzato CR espone ExpandCollapse anche con la finestra stretta");
            expandCollapse!.Expand();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_Cr_FiltroAssegnatiAMe_ToggleButton").BoundingRectangle.Height > 0,
                "i filtri avanzati CR sono espansi");

            var lista = ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox");
            Assert.True(lista.BoundingRectangle.Height > 0, "la lista CR resta visibile con i filtri avanzati aperti");
            Assert.True(ElementFinders.WaitButtonById(window, "Anomalie_Cr_PaginaSuccessiva_Button").IsEnabled,
                "con 251 CR la pagina successiva resta disponibile nello schermo stretto");
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_PaginazioneCr_Text").Name?.Contains("Pagina 1 / 2") == true,
                "la scheda CR parte dalla pagina 1 di 2");
            var primaPagina = IdCr(window);
            Assert.NotEmpty(primaPagina);

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_PaginaSuccessiva_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_PaginazioneCr_Text").Name?.Contains("Pagina 2 / 2") == true,
                "Successiva porta alla pagina 2 mentre i filtri avanzati restano aperti");
            var secondaPagina = IdCr(window);
            Assert.NotEmpty(secondaPagina);
            Assert.Empty(primaPagina.Intersect(secondaPagina));

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_PaginaPrecedente_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_PaginazioneCr_Text").Name?.Contains("Pagina 1 / 2") == true,
                "Precedente riporta alla pagina 1 con i filtri avanzati aperti");
            Assert.Equal(primaPagina, IdCr(window));

            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("CR-QA-0251");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0251" }),
                "la ricerca testo resta utilizzabile con i filtri avanzati aperti");
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_PaginazioneCr_Text").Name?.Contains("Pagina 1 / 1") == true,
                "il filtro testo riporta la paginazione a una sola pagina");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PaginazioneCr_SchermoStrettoConFiltriAvanzati_AggiornaListaERicerca), ex);
        }
    }
}
