using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>Il percorso Segnalazione di rete deve poter accodare un allegato prima che esista l'NC persistita.
/// Il fixture usa il campo reale UnitLogger "Asist" per verificare che non venga duplicato rispetto ai dati pratica e
/// attraversa il picker nativo Windows con un .msg temporaneo prima di salvare.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class NetworkReportAttachmentUiTests : UiTestBase
{
    private const string StrumentoUdt = "UnitLogger";
    private const string DescrizioneBreve = "Segnalazione UI con allegato prima del salvataggio";
    private const string FileMsg = "allegato-segnalazione-qa.msg";

    public NetworkReportAttachmentUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(NetworkReportAttachmentUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var strumenti = state.Master.Strumenti.Load();
        var unitlogger = strumenti.FirstOrDefault(s => s.Nome.Equals(StrumentoUdt, StringComparison.OrdinalIgnoreCase));
        if (unitlogger == null)
        {
            unitlogger = new StrumentoDiagnostico { Nome = StrumentoUdt, Tipo = TipoStrumento.DaTestare, ModificatoDa = "UITests" };
            strumenti.Add(unitlogger);
        }
        unitlogger.Campi = new List<DefinizioneCampoStrumento>
        {
            new() { Nome = "Asist", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
            new() { Nome = "UnitLogger Version", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
        };
        state.Master.Strumenti.Save(strumenti);
        return workspace;
    }

    private NonConformita? TrovaSegnalazioneSalvata()
    {
        var paths = new DataPaths(Workspace.WorkspaceDir);
        if (!Directory.Exists(paths.NonConformitaDir)) return null;
        foreach (var path in Directory.GetFiles(paths.NonConformitaDir, "nc_*.json"))
        {
            var nc = JsonIo.TryRead<NonConformita>(path, out _);
            if (nc?.DescrizioneBreveSegnalata == DescrizioneBreve) return nc;
        }
        return null;
    }

    [Fact]
    public void SegnalazioneNuova_AccodaMsgConAsistVuotoPrimaDelSalvataggioEPersisteIlBinario()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToAnomalie().NuovaSegnalazioneRete();
            editor.AggiungiNumeroPratica("PRATICA-UI-NETWORK-001");
            editor.AggiungiVin("ZFA0000000000NET01");
            editor.AggiungiLicenza("LIC-UI-NETWORK-001");
            editor.SetDescrizioneBreve(DescrizioneBreve);
            editor.SetDescrizione("Segnalazione di rete sintetica, con file Outlook scelto prima del salvataggio.");
            editor.SetSeverita("Medium");
            editor.SetPriorita("Normal");
            editor.SetStato("InVerifica");
            editor.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            ElementFinders.WaitComboBoxById(window, "Segnalazione_Strumento_Combo").SelectByText(StrumentoUdt);

            Wait.ForCondition(() => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Any(t => t.Name == "UnitLogger Version"),
                "campo tecnico UnitLogger non equivalente ai campi di origine visibile");
            Assert.DoesNotContain(window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)), t => t.Name == "Asist");

            var msgPath = Path.Combine(Workspace.RootDir, FileMsg);
            File.WriteAllText(msgPath,
                "From: qa@example.test\r\nTo: support@example.test\r\nSubject: UI attachment fixture\r\n\r\nSynthetic message body.");
            ElementFinders.WaitButtonById(window, "Segnalazione_ScegliAllegato_Button").SafeInvoke();
            NativeFileDialogPage.Attendi(App).ConfermaPercorso(msgPath);

            Wait.ForCondition(() => window.FindAllDescendants(cf => cf.ByAutomationId("Segnalazione_Allegato_Nome_Button"))
                    .Any(b => b.Name == FileMsg),
                "il file .msg compare tra gli allegati accodati nell'editor non ancora salvato");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Segnalazione_ModificheNonSalvate_Text"),
                "l'allegato accodato marca la nuova segnalazione come non salvata");
            Assert.Null(TrovaSegnalazioneSalvata());

            editor.SalvaDaHeader();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"),
                "ritorno alla lista Anomalie dopo aver salvato la segnalazione");
            Wait.ForCondition(() => TrovaSegnalazioneSalvata() != null,
                "NC di rete salvata con la descrizione del fixture", TimeSpan.FromSeconds(20));
            var salvata = TrovaSegnalazioneSalvata()
                ?? throw new InvalidOperationException("La segnalazione non è stata trovata dopo il ritorno alla lista.");

            Assert.Equal(StrumentoUdt, salvata.StrumentoSegnalato);
            Assert.False(salvata.ValoriStrumentoSegnalato.TryGetValue("Asist", out var asist) && !string.IsNullOrWhiteSpace(asist),
                "Asist non viene duplicato tra i campi dello strumento e resta vuoto nell'NC");
            Assert.Contains("PRATICA-UI-NETWORK-001", salvata.NumeriPraticaRete);
            var allegato = Assert.Single(salvata.Allegati, a => a.NomeFile == FileMsg);
            var percorsoSalvato = AttachmentService.PercorsoCompleto(new DataPaths(Workspace.WorkspaceDir), salvata, allegato);
            Assert.True(File.Exists(percorsoSalvato), "il file .msg accodato prima del salvataggio viene trasferito nel bucket NC persistito");
            Assert.Equal(new FileInfo(msgPath).Length, new FileInfo(percorsoSalvato).Length);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SegnalazioneNuova_AccodaMsgConAsistVuotoPrimaDelSalvataggioEPersisteIlBinario), ex);
        }
    }
}
