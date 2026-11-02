using System.Text.Json;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura FlaUI delle azioni di scrittura dei master data in Impostazioni non ancora esercitate
/// (COPERTURA_FLAUI.md, sezione Master / Impostazioni): nuova riga con Nome digitato e salvata, validazione del Nome
/// obbligatorio, Annulla con conferma di abbandono, archiviazione di una riga, import Excel con l'anteprima
/// (Annulla e Importa seguito da Salva), import su tutti i cataloghi con import Excel, colonne nascoste dal menu
/// colonne e ripristino. Il dato persistito si legge dal workspace con il repository master data dell'applicazione
/// (AppState), non con un file JSON fisso: il layout su disco di Phase D non tiene componente.json nella radice condivisa.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
[Trait("Category", "CRUD")]
public sealed class MasterWuReportCoverageMasterDataUiTests : UiTestBase
{
    private const string EcuDefault = "Componente-QA-01";
    private const string NomeNuovo = "Componente-NUOVA-01";
    private const string NomeDaScartare = "Componente-DA-SCARTARE";
    private const string ImportA = "Componente-IMP-A";
    private const string ImportB = "Componente-IMP-B";
    private const string ColonnaUltimaModifica = "Ultima modifica";
    private const string ColonnaVeicoli = "Veicoli associati";
    private const string VoceRipristinaColonne = "Ripristina disposizione tabella";

    public MasterWuReportCoverageMasterDataUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(MasterWuReportCoverageMasterDataUiTests))) { }

    /// <summary>Catalogo componente persistito nel workspace, letto dal repository master dell'applicazione.</summary>
    private List<Componente> EcuPersistite()
    {
        using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
        return state.Master.Componente.Load();
    }

    /// <summary>Foto JSON completa del catalogo persistito: due foto uguali = nessuna scrittura.</summary>
    private string FotoEcu() => JsonSerializer.Serialize(EcuPersistite(), JsonIo.CompactOptions);

    /// <summary>Foto JSON del catalogo indicato (Funzionalita, Veicoli, Strumenti, Requisiti) persistito nel workspace.</summary>
    private string FotoCatalogo(string sezione)
    {
        using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
        return sezione switch
        {
            "funzionalita" => JsonSerializer.Serialize(state.Master.Funzionalita.Load(), JsonIo.CompactOptions),
            "veicoli" => JsonSerializer.Serialize(state.Master.Veicoli.Load(), JsonIo.CompactOptions),
            "strumenti" => JsonSerializer.Serialize(state.Master.Strumenti.Load(), JsonIo.CompactOptions),
            _ => JsonSerializer.Serialize(state.Master.Requisiti.Load(), JsonIo.CompactOptions),
        };
    }

    /// <summary>File .xlsx temporaneo con intestazione e righe; il primo foglio è quello proposto dall'import.</summary>
    private static string ScriviXlsx(string percorso, IReadOnlyList<string> intestazioni, params IReadOnlyList<string>[] righe)
    {
        using var wb = new XLWorkbook();
        var foglio = wb.Worksheets.Add("Foglio1");
        for (var c = 0; c < intestazioni.Count; c++) foglio.Cell(1, c + 1).Value = intestazioni[c];
        for (var r = 0; r < righe.Length; r++)
            for (var c = 0; c < righe[r].Count; c++) foglio.Cell(r + 2, c + 1).Value = righe[r][c];
        wb.SaveAs(percorso);
        return percorso;
    }

    private string XlsxEcu() => ScriviXlsx(Path.Combine(Workspace.RootDir, "import-componente.xlsx"),
        new[] { "Nome", "Veicoli" }, new[] { ImportA, "" }, new[] { ImportB, "" });

    [Fact]
    public void Componente_NuovaRigaSenzaNome_SalvaRifiutataConMessaggioENessunaScrittura()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var componente = new MainShellPage(window).GoToImpostazioni().VaiAEcu();
            componente.IniziaModifica();
            var fotoPrima = FotoEcu();

            componente.NuovaRiga();
            componente.Salva();

            Wait.ForCondition(() => (componente.MessaggioErrore ?? string.Empty).Contains("Nome obbligatorio", StringComparison.Ordinal),
                "banner di errore 'Nome obbligatorio' dopo Salva");
            Assert.True(componente.PuoSalvare(), "l'editor resta in modifica dopo un Salva rifiutato");
            Assert.Equal(fotoPrima, FotoEcu());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Componente_NuovaRigaSenzaNome_SalvaRifiutataConMessaggioENessunaScrittura), ex);
        }
    }

    [Fact]
    public void Componente_ArchiviaRigaESalva_LaRigaEscoDallaGrigliaEILWorkspaceLaMarcaArchiviata()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var componente = new MainShellPage(window).GoToImpostazioni().VaiAEcu();
            componente.IniziaModifica();
            Assert.Contains(EcuDefault, componente.ChiaviVisibili());

            componente.ArchiviaRiga(EcuDefault);
            Wait.ForCondition(() => !componente.ChiaviVisibili().Contains(EcuDefault), "riga archiviata fuori dalla griglia");

            componente.Salva();
            Wait.ForCondition(componente.PuoModificare, "ritorno in sola lettura dopo Salva");
            Wait.ForCondition(() => EcuPersistite().Any(e => e.Nome == EcuDefault && e.Archiviata),
                "il workspace marca componente-QA-01 come archiviata");
            Assert.DoesNotContain(EcuDefault, componente.ChiaviVisibili());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Componente_ArchiviaRigaESalva_LaRigaEscoDallaGrigliaEILWorkspaceLaMarcaArchiviata), ex);
        }
    }

    [Fact]
    public void Componente_ImportExcelAnteprimaAnnulla_NonAggiungeRigheENessunaScrittura()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var componente = new MainShellPage(window).GoToImpostazioni().VaiAEcu();
            componente.IniziaModifica();
            var fotoPrima = FotoEcu();

            var anteprima = ImportExcelFlow.ApriAnteprima(App, window, "Componente_ImportaExcel_Button", XlsxEcu());
            Assert.Contains("Righe lette: 2", anteprima.Message);
            Assert.Contains("Nuove da aggiungere: 2", anteprima.Message);

            anteprima.ConfirmNo();
            ImportExcelFlow.AttendiChiusuraAnteprima(App);
            Assert.DoesNotContain(ImportA, componente.ChiaviVisibili());
            Assert.DoesNotContain(ImportB, componente.ChiaviVisibili());

            componente.Annulla();
            Wait.ForCondition(componente.PuoModificare, "ritorno in sola lettura senza modifiche");
            Assert.Equal(fotoPrima, FotoEcu());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Componente_ImportExcelAnteprimaAnnulla_NonAggiungeRigheENessunaScrittura), ex);
        }
    }

    [Fact]
    public void Componente_ImportExcelAnteprimaImporta_PoiSalva_PersisteLeRigheSoloDopoSalva()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var componente = new MainShellPage(window).GoToImpostazioni().VaiAEcu();
            componente.IniziaModifica();
            var fotoPrima = FotoEcu();

            var anteprima = ImportExcelFlow.ApriAnteprima(App, window, "Componente_ImportaExcel_Button", XlsxEcu());
            Assert.Contains("Nuove da aggiungere: 2", anteprima.Message);
            anteprima.ConfirmYes();
            ImportExcelFlow.AttendiChiusuraAnteprima(App);

            Wait.ForCondition(() => componente.ChiaviVisibili().Contains(ImportA) && componente.ChiaviVisibili().Contains(ImportB),
                "righe importate visibili nell'editor");
            Assert.Equal(fotoPrima, FotoEcu()); // Importa aggiunge solo all'editor: il workspace non cambia prima di Salva

            componente.Salva();
            Wait.ForCondition(componente.PuoModificare, "ritorno in sola lettura dopo Salva");
            Wait.ForCondition(() => EcuPersistite().Select(e => e.Nome).Contains(ImportA)
                && EcuPersistite().Select(e => e.Nome).Contains(ImportB), "il workspace contiene le righe importate dopo Salva");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Componente_ImportExcelAnteprimaImporta_PoiSalva_PersisteLeRigheSoloDopoSalva), ex);
        }
    }

    [Theory]
    [Trait("Category", "Regression")]
    [InlineData("funzionalita", "Funz", "FUNZ-IMP-A")]
    [InlineData("veicoli", "Veic", "VEIC-IMP-A")]
    [InlineData("strumenti", "Strum", "STRUM-IMP-A")]
    [InlineData("requisiti", "Requis", "REQ-IMP-A")]
    public void ImportExcelAnteprimaAnnulla_SuiCataloghiConImport_NonScriveNulla(string sezione, string prefisso, string chiaveImport)
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiA(sezione);
            var catalogo = new MasterDataEditorPage(window, prefisso);
            catalogo.IniziaModifica();
            var fotoPrima = FotoCatalogo(sezione);

            var xlsx = ScriviXlsx(Path.Combine(Workspace.RootDir, $"import-{sezione}.xlsx"), IntestazioniImport(sezione), RigaImport(sezione, chiaveImport));
            var anteprima = ImportExcelFlow.ApriAnteprima(App, window, $"{prefisso}_ImportaExcel_Button", xlsx);
            Assert.Contains("Righe lette: 1", anteprima.Message);
            Assert.Contains("Nuove da aggiungere: 1", anteprima.Message);

            anteprima.ConfirmNo();
            ImportExcelFlow.AttendiChiusuraAnteprima(App);
            Assert.DoesNotContain(chiaveImport, catalogo.ChiaviVisibili());

            catalogo.Annulla();
            Wait.ForCondition(catalogo.PuoModificare, "ritorno in sola lettura senza modifiche");
            Assert.Equal(fotoPrima, FotoCatalogo(sezione));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow($"ImportExcelAnteprimaAnnulla_{sezione}", ex);
        }
    }

    private static IReadOnlyList<string> IntestazioniImport(string sezione) => sezione switch
    {
        "funzionalita" => new[] { "Nome", "Categorie", "Componenti associati" },
        "veicoli" => new[] { "Nome", "Gamma" },
        "strumenti" => new[] { "Nome" },
        _ => new[] { "Id", "Testo", "Banco", "Veicolo" },
    };

    private static IReadOnlyList<string> RigaImport(string sezione, string chiave) => sezione switch
    {
        "funzionalita" => new[] { chiave, "", "" },
        "veicoli" => new[] { chiave, "" },
        "strumenti" => new[] { chiave },
        _ => new[] { chiave, "Requisito importato per la prova di anteprima", "si", "no" },
    };

    private static List<string> Intestazioni(AutomationElement griglia) =>
        griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem))
            .Select(h => h.Name ?? string.Empty).Where(n => n.Length > 0).ToList();

    /// <summary>Clic destro sull'intestazione di una colonna: apre il menu colonne della griglia.</summary>
    private static void ApriMenuColonne(Window window, string intestazione) =>
        Wait.ForElement(() => window.FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem))
            .FirstOrDefault(h => string.Equals(h.Name, intestazione, StringComparison.Ordinal)),
            $"intestazione '{intestazione}' per aprire il menu colonne").RightClick();

    /// <summary>Attiva una voce del menu colonne: il menu Avalonia non espone un pattern di invocazione, quindi la
    /// voce prende il fuoco e riceve Invio (stesso schema di ReportPage per i menu contestuali).</summary>
    private void SceglieVoceMenu(string nome)
    {
        var voce = Wait.ForElement(() => App.TopLevelWindows()
            .Select(w => w.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(nome))))
            .FirstOrDefault(e => e != null), $"voce di menu '{nome}'");
        voce.Focus();
        Keyboard.Type(VirtualKeyShort.ENTER);
    }

    [Fact]
    public void Componente_ImportExcelImportaPoiAnnullaUltimoImport_RiportaLeRigheAlloStatoPrecedente()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var componente = new MainShellPage(window).GoToImpostazioni().VaiAEcu();
            componente.IniziaModifica();
            var chiaviPrima = componente.ChiaviVisibili().ToList();
            var fotoPrima = FotoEcu();

            var anteprima = ImportExcelFlow.ApriAnteprima(App, window, "Componente_ImportaExcel_Button", XlsxEcu());
            anteprima.ConfirmYes();
            ImportExcelFlow.AttendiChiusuraAnteprima(App);
            Wait.ForCondition(() => componente.ChiaviVisibili().Contains(ImportA) && componente.ChiaviVisibili().Contains(ImportB),
                "righe importate visibili nell'editor");

            // Il report finale resta visibile con i conteggi; "Annulla ultimo import" riporta l'editor com'era prima.
            var report = ElementFinders.WaitById(window, "Componente_ReportImport_Text").Name ?? string.Empty;
            Assert.Contains("aggiunte 2", report);

            ElementFinders.WaitButtonById(window, "Componente_AnnullaUltimoImport_Button").SafeInvoke();
            Wait.ForCondition(() => !componente.ChiaviVisibili().Contains(ImportA) && !componente.ChiaviVisibili().Contains(ImportB),
                "righe importate rimosse dall'editor dopo Annulla ultimo import");
            Assert.Equal(chiaviPrima, componente.ChiaviVisibili().ToList());
            Assert.Equal(fotoPrima, FotoEcu()); // l'annullamento riguarda solo l'editor: il workspace non cambia

            componente.Annulla();
            Wait.ForCondition(componente.PuoModificare, "ritorno in sola lettura dopo Annulla");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Componente_ImportExcelImportaPoiAnnullaUltimoImport_RiportaLeRigheAlloStatoPrecedente), ex);
        }
    }
}
