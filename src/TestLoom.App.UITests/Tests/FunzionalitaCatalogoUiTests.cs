using System.Text.Json;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Catalogo Funzionalita' oltre il ciclo di modifica base: nuova voce e archiviazione, categorie (Expander annidato), flyout
/// "Componenti associati" della riga, Unisci/Rinomina/Dividi con propagazione ai Test che usano la voce, import Excel con la colonna
/// "Veicoli per componente" (6.7.2). Le verifiche leggono il catalogo persistito tramite il repository Phase D e i Test dai file del workspace.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
[Trait("Category", "Forms")]
[Trait("Area", "Funzionalita")]
public sealed class FunzionalitaCatalogoUiTests : UiTestBase
{
    private const string FuncArchiviare = "Func-Archiviare";
    private const string FuncNuova = "Func-QA-01";
    private const string FuncVecchia = "Func-Vecchia";
    private const string FuncRinominata = "Func-Nuova";
    private const string FuncA = "Func-A";
    private const string FuncB = "Func-B";
    private const string FuncGrande = "Func-Grande";
    private const string FuncP1 = "Func-P1";
    private const string FuncP2 = "Func-P2";
    private const string FuncEcu = "Func-Componente";
    private const string FuncXls = "Func-Xls-01";
    private const string EcuDue = "Componente-QA-02";
    private const string EcuTre = "Componente-QA-03";
    private const string CategoriaNuova = "Cat-QA-01";
    private const string CategoriaDaScartare = "Cat-Da-Scartare";
    private const string TestVecchia = "QA-FUNZ-1";
    private const string TestA = "QA-FUNZ-2";
    private const string VeicoloXls = TestWorkspace.DefaultVehicleName;

    public FunzionalitaCatalogoUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(FunzionalitaCatalogoUiTests), seedTestFixtures: true);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var componente = state.Master.Componente.Load();
        componente.Add(new Componente { Nome = EcuDue, ModificatoDa = "UITests" });
        componente.Add(new Componente { Nome = EcuTre, ModificatoDa = "UITests" });
        state.Master.Componente.Save(componente);
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = FuncArchiviare, ModificatoDa = "UITests" },
            new() { Nome = FuncVecchia, ModificatoDa = "UITests" },
            new() { Nome = FuncA, ModificatoDa = "UITests" },
            new() { Nome = FuncB, ModificatoDa = "UITests" },
            new() { Nome = FuncGrande, ModificatoDa = "UITests" },
            new() { Nome = FuncEcu, TutteLeEcu = false, ModificatoDa = "UITests" },
        });
        SeminaTestConFunzionalita(state, TestVecchia, FuncVecchia);
        SeminaTestConFunzionalita(state, TestA, FuncA);
        return ws;
    }

    /// <summary>Test minimo che usa la funzionalità indicata: serve a verificare la propagazione di rinomina/unione.</summary>
    private static void SeminaTestConFunzionalita(AppState state, string id, string funzionalita)
    {
        var tc = new TestCase
        {
            Id = id,
            DescrizioneBreve = "Test con funzionalita' seminata",
            TestTemplateUid = TestCoverageSeed.UidTemplatePrincipale(state),
            CreatoDa = "QA Master",
            StatoLavoro = StatoLavoroTest.Pianificato,
            ModificatoDa = "UITests",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        };
        tc.Componente.Add(TestWorkspace.DefaultEcuName);
        tc.Funzionalita.Add(funzionalita);
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.Now,
            Operatore = "UITests",
            Esito = Esito.Conforme,
            Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme } },
        });
        state.TestCases.Save(tc, true);
    }

    private List<Funzionalita> FunzionalitaPersistite()
    {
        var path = new DataPaths(Workspace.WorkspaceDir).FunzionalitaFile;
        // Phase D stores the authoritative catalog in the shared journal/materialized index; the legacy JSON
        // file is intentionally absent after migration. Load through the production repository adapter without
        // constructing AppState (whose startup recovery would affect the operation being observed).
        return new MasterDataRepository<Funzionalita>(path).Load();
    }

    private List<Categoria> CategoriePersistite()
    {
        using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
        return state.Master.Categorie.Load();
    }

    private TestCase? TestPersistito(string id) => TestCoverageSeed.TrovaTestCase(Workspace, id);

    /// <summary>Attende che il Test <paramref name="id"/> soddisfi la condizione; in caso di timeout riporta lo stato del Test, il catalogo e i banner del pannello.</summary>
    private void AttendiTest(MasterUnionePannelloPage pannello, string id, Func<TestCase?, bool> condizione, string descrizione)
    {
        try
        {
            Wait.ForCondition(() => condizione(TestPersistito(id)), descrizione);
        }
        catch (TimeoutException ex)
        {
            var tc = TestPersistito(id);
            var statoTest = tc == null ? "non trovato" : "[" + string.Join(", ", tc.Funzionalita) + "]";
            var catalogo = string.Join(", ", FunzionalitaPersistite().Select(f => f.Nome + (f.Archiviata ? "(archiviata)" : "")));
            var file = string.Join(" | ", TestCoverageSeed.FileTestCase(Workspace)
                .Select(f => Path.GetRelativePath(Workspace.WorkspaceDir, f) + "=>" + (TestCoverageSeed.ETombstone(f) ? "tombstone" : string.Join("+", TestCoverageSeed.LeggiTestCase(f).Funzionalita))));
            throw new Exception($"{ex.Message} Stato: Funzionalita del Test={statoTest}; file=[{file}]; catalogo=[{catalogo}]; " +
                $"errore pannello='{pannello.Errore}'; esito='{pannello.Esito}'", ex);
        }
    }

    private string FotoCategorie() => JsonSerializer.Serialize(CategoriePersistite(), JsonIo.CompactOptions);

    /// <summary>Nomi delle funzionalità attive (non archiviate) nel catalogo persistito.</summary>
    private IEnumerable<string> NomiAttivi() => FunzionalitaPersistite().Where(f => !f.Archiviata).Select(f => f.Nome);

    private void AttendiCatalogo(Func<List<Funzionalita>, bool> condizione, string descrizione)
    {
        try
        {
            Wait.ForCondition(() => condizione(FunzionalitaPersistite()), descrizione);
        }
        catch (TimeoutException ex)
        {
            var stato = string.Join("; ", FunzionalitaPersistite().Select(f =>
                $"{f.Nome}: tutte={f.TutteLeEcu}, componente=[{string.Join(",", f.EcuAssociate)}], archiviata={f.Archiviata}"));
            throw new TimeoutException($"{ex.Message} Catalogo persistito corrente: {stato}", ex);
        }
    }

    private MasterDataEditorPage ApriFunzionalitaInModifica()
    {
        var window = App.WaitForMainWindow();
        var pagina = new MainShellPage(window).GoToImpostazioni().VaiAFunzionalita();
        pagina.IniziaModifica();
        return pagina;
    }

    // ---------------------------------------------------------------- catalogo base

    [Fact]
    public void Funzionalita_NuovaRigaSalvata_PersisteNelCatalogo()
    {
        try
        {
            var pagina = ApriFunzionalitaInModifica();
            pagina.NuovaRiga();
            pagina.ScriviChiaveUltimaRiga(FuncNuova);
            Assert.Contains(FuncNuova, pagina.ChiaviVisibili());

            pagina.Salva();
            Wait.ForCondition(() => !pagina.InModifica, "funzionalita' in sola lettura dopo Salva");
            Wait.ForCondition(() => NomiAttivi().Contains(FuncNuova), "la nuova funzionalita' e' nel catalogo persistito");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Funzionalita_NuovaRigaSalvata_PersisteNelCatalogo), ex);
        }
    }

    [Fact]
    public void Funzionalita_RigaArchiviataESalvata_LaMarcaArchiviataNelCatalogo()
    {
        try
        {
            var pagina = ApriFunzionalitaInModifica();
            Assert.Contains(FuncArchiviare, pagina.ChiaviVisibili());

            pagina.ArchiviaRiga(FuncArchiviare);
            Wait.ForCondition(() => !pagina.ChiaviVisibili().Contains(FuncArchiviare), "riga archiviata fuori dalla griglia");

            pagina.Salva();
            Wait.ForCondition(() => !pagina.InModifica, "funzionalita' in sola lettura dopo Salva");
            Wait.ForCondition(() => FunzionalitaPersistite().Any(f => f.Nome == FuncArchiviare && f.Archiviata),
                "il catalogo persistito marca la voce come archiviata");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Funzionalita_RigaArchiviataESalvata_LaMarcaArchiviataNelCatalogo), ex);
        }
    }

    // ---------------------------------------------------------------- categorie

    [Fact]
    public void Funzionalita_Categorie_NuovaSalvataEAnnullaSenzaScrivere()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiAFunzionalita();
            var pagina = new FunzionalitaCatalogoPage(App, window);
            pagina.EspandiCategorie();
            pagina.IniziaModificaCategorie();
            pagina.NuovaCategoria();
            pagina.ScriviNomeUltimaCategoria(CategoriaNuova);
            Assert.Contains(CategoriaNuova, pagina.NomiCategorieVisibili());

            pagina.SalvaCategorie();
            Wait.ForCondition(() => !pagina.CategorieInModifica, "categorie in sola lettura dopo Salva");
            Wait.ForCondition(() => CategoriePersistite().Any(c => c.Nome == CategoriaNuova && !c.Archiviata),
                "la nuova categoria e' in categorie.json");

            var fotoPrima = FotoCategorie();
            pagina.IniziaModificaCategorie();
            pagina.NuovaCategoria();
            pagina.ScriviNomeUltimaCategoria(CategoriaDaScartare);
            pagina.AnnullaCategorie();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => !pagina.CategorieInModifica, "categorie in sola lettura dopo Annulla confermato");
            Assert.DoesNotContain(CategoriaDaScartare, pagina.NomiCategorieVisibili());
            Assert.Equal(fotoPrima, FotoCategorie());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Funzionalita_Categorie_NuovaSalvataEAnnullaSenzaScrivere), ex);
        }
    }

    // ---------------------------------------------------------------- Componenti associati (flyout)

    [Fact]
    public void Funzionalita_EcuAssociate_DalFlyout_SalvaLeEcuSceltePersistenti()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = new MainShellPage(window).GoToImpostazioni().VaiAFunzionalita();
            pagina.IniziaModifica();
            var flyout = new FunzionalitaCatalogoPage(App, window);

            flyout.ApriFlyoutEcu(FuncEcu, TestWorkspace.DefaultEcuName);
            flyout.ImpostaEcu(TestWorkspace.DefaultEcuName, accesa: true, ecuAttesa: TestWorkspace.DefaultEcuName);
            flyout.ImpostaEcu(EcuDue, accesa: true, ecuAttesa: TestWorkspace.DefaultEcuName);
            flyout.ChiudiFlyoutEcu(TestWorkspace.DefaultEcuName);
            Wait.ForCondition(() => flyout.RiepilogoEcuRiga(FuncEcu).Contains(EcuDue, StringComparison.Ordinal),
                "riepilogo della riga con la seconda componente scelta");

            pagina.Salva();
            Wait.ForCondition(() => !pagina.InModifica, "funzionalita' in sola lettura dopo Salva");
            AttendiCatalogo(catalogo =>
            {
                var voce = catalogo.SingleOrDefault(f => f.Nome == FuncEcu);
                return voce != null && !voce.TutteLeEcu && voce.EcuAssociate.Count == 2
                    && voce.EcuAssociate.Contains(TestWorkspace.DefaultEcuName) && voce.EcuAssociate.Contains(EcuDue)
                    && !voce.EcuAssociate.Contains(EcuTre);
            }, "il catalogo persistito contiene le due Componenti associati");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Funzionalita_EcuAssociate_DalFlyout_SalvaLeEcuSceltePersistenti), ex);
        }
    }

    // ---------------------------------------------------------------- Unisci / Rinomina / Dividi

    [Fact]
    public void Funzionalita_Rinomina_PropagaIlNuovoNomeAlTestCollegato()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiAFunzionalita();
            var pannello = new MasterUnionePannelloPage(App, window, "Funz");
            pannello.Espandi();
            pannello.SelezionaVoce(FuncVecchia);
            pannello.Rinomina(FuncRinominata);

            AttendiCatalogo(catalogo => catalogo.Any(f => !f.Archiviata && f.Nome == FuncRinominata) &&
                !catalogo.Any(f => !f.Archiviata && f.Nome == FuncVecchia),
                "il catalogo persistito ha il nuovo nome e non il vecchio");
            AttendiTest(pannello, TestVecchia, t => t?.Funzionalita.Contains(FuncRinominata) == true, "il Test collegato usa il nuovo nome della funzionalita'");
            Assert.DoesNotContain(FuncVecchia, TestPersistito(TestVecchia)!.Funzionalita);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Funzionalita_Rinomina_PropagaIlNuovoNomeAlTestCollegato), ex);
        }
    }

    [Fact]
    public void Funzionalita_Rinomina_SuNomeEsistente_MostraErroreSenzaScrittura()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiAFunzionalita();
            var pannello = new MasterUnionePannelloPage(App, window, "Funz");
            pannello.Espandi();
            var fotoPrima = JsonSerializer.Serialize(FunzionalitaPersistite(), JsonIo.CompactOptions);

            pannello.SelezionaVoce(FuncA);
            pannello.RinominaSenzaConfermare(FuncB);

            Wait.ForCondition(() => !string.IsNullOrWhiteSpace(pannello.Errore), "errore di rinomina su un nome esistente");
            Assert.Equal(fotoPrima, JsonSerializer.Serialize(FunzionalitaPersistite(), JsonIo.CompactOptions));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Funzionalita_Rinomina_SuNomeEsistente_MostraErroreSenzaScrittura), ex);
        }
    }

    [Fact]
    public void Funzionalita_Unisci_RiassegnaIlTestEToglieLaVoceScartata()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiAFunzionalita();
            var pannello = new MasterUnionePannelloPage(App, window, "Funz");
            pannello.Espandi();
            pannello.SelezionaVoce(FuncA);
            pannello.UnisciIn(FuncB);

            AttendiCatalogo(catalogo => !catalogo.Any(f => !f.Archiviata && f.Nome == FuncA) &&
                catalogo.Any(f => !f.Archiviata && f.Nome == FuncB),
                "la voce scartata esce dal catalogo attivo e resta quella corretta");
            AttendiTest(pannello, TestA, t => t?.Funzionalita.Contains(FuncB) == true, "il Test collegato ora usa la funzionalita' corretta");
            Assert.DoesNotContain(FuncA, TestPersistito(TestA)!.Funzionalita);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Funzionalita_Unisci_RiassegnaIlTestEToglieLaVoceScartata), ex);
        }
    }

    [Fact]
    public void Funzionalita_Dividi_CreaLeNuoveVociEMantieneLOriginale()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiAFunzionalita();
            var pannello = new MasterUnionePannelloPage(App, window, "Funz");
            pannello.Espandi();
            pannello.SelezionaVoce(FuncGrande);
            pannello.Dividi(FuncP1, FuncP2);

            Wait.ForCondition(() => NomiAttivi().Contains(FuncP1) && NomiAttivi().Contains(FuncP2),
                "le due nuove voci compaiono nel catalogo attivo");
            Assert.Contains(FuncGrande, NomiAttivi()); // la divisione non rimuove mai l'originale
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Funzionalita_Dividi_CreaLeNuoveVociEMantieneLOriginale), ex);
        }
    }

    // ---------------------------------------------------------------- import Excel con "Veicoli per componente"

    [Fact]
    public void Funzionalita_ImportExcel_ColonnaVeicoliPerEcu_SalvaLaRestrizioneSullaFunzionalita()
    {
        try
        {
            var pagina = ApriFunzionalitaInModifica();
            var xlsx = ScriviXlsxFunzionalita();

            var anteprima = ImportExcelFlow.ApriAnteprima(App, App.WaitForMainWindow(), "Funz_ImportaExcel_Button", xlsx);
            Assert.Contains("Nuove da aggiungere: 1", anteprima.Message);
            anteprima.ConfirmYes();
            ImportExcelFlow.AttendiChiusuraAnteprima(App);
            Wait.ForCondition(() => pagina.ChiaviVisibili().Contains(FuncXls), "la funzionalita' importata compare nell'editor");

            pagina.Salva();
            Wait.ForCondition(() => !pagina.InModifica, "funzionalita' in sola lettura dopo Salva");
            Wait.ForCondition(() =>
            {
                var voce = FunzionalitaPersistite().SingleOrDefault(f => f.Nome == FuncXls);
                return voce != null && voce.VeicoliPerEcu.Any(v => v.Componente == TestWorkspace.DefaultEcuName
                    && v.Veicoli.Contains(VeicoloXls));
            }, "il catalogo persistito restringe la funzionalita' al veicolo indicato per la sua componente");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Funzionalita_ImportExcel_ColonnaVeicoliPerEcu_SalvaLaRestrizioneSullaFunzionalita), ex);
        }
    }

    /// <summary>File .xlsx temporaneo con le colonne del catalogo Funzionalita' (Nome, Categorie, Componenti associati, Veicoli per componente).</summary>
    private string ScriviXlsxFunzionalita()
    {
        var percorso = Path.Combine(Workspace.RootDir, "import-funzionalita.xlsx");
        using var wb = new XLWorkbook();
        var foglio = wb.Worksheets.Add("Foglio1");
        string[] intestazioni = { "Nome", "Categorie", "Componenti associati", "Veicoli per componente" };
        string[] riga = { FuncXls, "", TestWorkspace.DefaultEcuName, $"{TestWorkspace.DefaultEcuName} => {VeicoloXls}" };
        for (var c = 0; c < intestazioni.Length; c++) foglio.Cell(1, c + 1).Value = intestazioni[c];
        for (var c = 0; c < riga.Length; c++) foglio.Cell(2, c + 1).Value = riga[c];
        wb.SaveAs(percorso);
        return percorso;
    }
}
