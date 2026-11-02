using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Filtri della pagina Anomalie: Cerca e Ticket TICKET non devono essere più alti dei controlli vicini
/// (in 6.5.3 si allungavano fino all'altezza "etichetta + controllo" della riga).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Layout")]
[Trait("Category", "Regression")]
public sealed class AnomalieFiltriLayoutUiTests : UiTestBase
{
    public AnomalieFiltriLayoutUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(AnomalieFiltriLayoutUiTests))) { }

    [Fact]
    public void CercaETicketEsta_HannoAltezzaDeiControlliVicini()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            var cerca = ElementFinders.WaitById(window, "Anomalie_FiltroTesto_Input");
            var assegnatario = ElementFinders.WaitById(window, "Anomalie_FiltroAssegnatario_Button");
            var expander = ElementFinders.WaitById(window, "Anomalie_Filtri_Expander");
            if (expander.Patterns.ExpandCollapse.IsSupported &&
                expander.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value != FlaUI.Core.Definitions.ExpandCollapseState.Expanded)
                expander.Patterns.ExpandCollapse.Pattern.Expand();
            var ticket = ElementFinders.WaitById(window, "Anomalie_FiltroTicket_Input");
            var ticketEsta = ElementFinders.WaitById(window, "Anomalie_FiltroTicketEsta_Combo");

            var dir = Path.Combine(AppContext.BaseDirectory, "TestResults", "Layout");
            Directory.CreateDirectory(dir);
            FlaUI.Core.Capturing.Capture.Element(window).ToFile(Path.Combine(dir, "anomalie-filtri.png"));

            // Tolleranza di pochi pixel per bordi/DPI; prima della correzione il rapporto era circa 2:1.
            Assert.InRange(cerca.BoundingRectangle.Height, assegnatario.BoundingRectangle.Height - 6, assegnatario.BoundingRectangle.Height + 6);
            Assert.InRange(ticket.BoundingRectangle.Height, ticketEsta.BoundingRectangle.Height - 6, ticketEsta.BoundingRectangle.Height + 6);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CercaETicketEsta_HannoAltezzaDeiControlliVicini), ex); }
    }
}
