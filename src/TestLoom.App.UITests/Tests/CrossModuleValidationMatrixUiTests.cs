using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Matrice P1 della validazione dei form CR e segnalazioni: prova la vera UI,
/// verifica il popup campi obbligatori e, soprattutto, che un tentativo invalido
/// NON crei un documento autorevole nella cartella dati.
/// I campi vengono compilati dalla UI; AppState e usato SOLO nella fixture seed.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
[Trait("Category", "Regression")]
public sealed class CrossModuleValidationMatrixUiTests : UiTestBase
{
    public CrossModuleValidationMatrixUiTests()
        : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(CrossModuleValidationMatrixUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath),
            runBackgroundMaintenance: false);
        var templates = state.Master.TestTemplate.Load();
        templates.Add(new TestTemplate
        {
            Nome = "CR_QA_Validazione",
            IsChangeRequest = true,
            ModificatoDa = "FlaUI"
        });
        state.Master.TestTemplate.Save(templates);
        return ws;
    }

    private void ChiudiPopupCampiMancanti()
    {
        var dialog = App.WaitForWindowByContent("MissingFields_Close_Button");
        ElementFinders.WaitButtonById(dialog, "MissingFields_Close_Button").SafeInvoke();
        Wait.ForCondition(() => App.IsWindowClosedByContent("MissingFields_Close_Button"),
            "popup campi obbligatori chiuso");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ChangeRequest_CampiObbligatoriMancanti_NonScriveIlFile(bool titolo, bool componente)
    {
        try
        {
            var w = App.WaitForMainWindow();
            var shell = new MainShellPage(w);
            var ed = shell.GoToAnomalie().NuovaChangeRequest();
            if (titolo) ed.SetTitolo("CR con titolo ma ambito assente");
            if (componente) ed.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            ed.Salva();

            ChiudiPopupCampiMancanti();
            Assert.True(ElementFinders.ExistsById(w, "Cr_Title_Text"));
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(Workspace.WorkspaceDir, "change-requests"), "cr_*.json"));
            Assert.False(shell.HasNavigationError);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ChangeRequest_CampiObbligatoriMancanti_NonScriveIlFile), ex,
                step: $"titolo={titolo}; componente={componente}");
        }
    }

    [Theory]
    [InlineData("matricola")]
    [InlineData("licenza")]
    [InlineData("descrizione-breve")]
    [InlineData("descrizione")]
    [InlineData("ambito")]
    public void Segnalazione_CampoObbligatorioAssente_NonCreaNonConformita(string mancante)
    {
        try
        {
            var w = App.WaitForMainWindow();
            var shell = new MainShellPage(w);
            var ed = shell.GoToAnomalie().NuovaSegnalazioneRete();
            if (mancante != "matricola") ed.AggiungiVin("ZFA0000000000QA01");
            if (mancante != "licenza") ed.AggiungiLicenza("LIC-100");
            if (mancante != "descrizione-breve") ed.SetDescrizioneBreve("Segnalazione incompleta QA");
            if (mancante != "descrizione") ed.SetDescrizione("Descrizione testata end-to-end con FlaUI.");
            if (mancante != "ambito") ed.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            ed.SetSeverita("Medium");
            ed.SetPriorita("Normal");
            ed.SetStato("InVerifica");
            ed.Salva();

            ChiudiPopupCampiMancanti();
            Assert.True(ElementFinders.ExistsById(w, "Segnalazione_Title_Text"));
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(Workspace.WorkspaceDir, "nonconformita"), "nc_*.json"));
            Assert.False(shell.HasNavigationError);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_CampoObbligatorioAssente_NonCreaNonConformita), ex,
                step: mancante);
        }
    }

    [Fact]
    public void Segnalazione_SenzaTicketAsist_PuoSalvareSeGliAltriCampiSonoValidi()
    {
        try
        {
            var w = App.WaitForMainWindow();
            var shell = new MainShellPage(w);
            var ed = shell.GoToAnomalie().NuovaSegnalazioneRete();
            ed.AggiungiVin("ZFA0000000000TK01");
            ed.AggiungiLicenza("LIC-TICKET-OPZIONALE");
            ed.SetDescrizioneBreve("Segnalazione senza ticket Asist");
            ed.SetDescrizione("Il ticket Asist è facoltativo quando gli altri dati sono completi.");
            ed.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            ed.SetSeverita("Medium");
            ed.SetPriorita("Normal");
            ed.SetStato("InVerifica");

            ed.SalvaDaHeader();
            Wait.ForCondition(() => ElementFinders.ExistsById(w, "Anomalie_Title_Text"),
                "una segnalazione valida senza ticket torna alla lista Anomalie/CR");

            var salvata = Directory.EnumerateFiles(
                    Path.Combine(Workspace.WorkspaceDir, "nonconformita"), "nc_*.json")
                .Select(percorso => JsonIo.TryRead<NonConformita>(percorso, out _))
                .Single(nc => nc?.DescrizioneBreveSegnalata == "Segnalazione senza ticket Asist");
            Assert.Empty(salvata!.CollegamentiPolarion);
            Assert.False(shell.HasNavigationError);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_SenzaTicketAsist_PuoSalvareSeGliAltriCampiSonoValidi), ex);
        }
    }
}
