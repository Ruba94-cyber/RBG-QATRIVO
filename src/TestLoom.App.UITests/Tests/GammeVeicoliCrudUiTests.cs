using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// CRUD delle Gamme veicolari dalla sezione annidata nella pagina Veicoli (Expander chiuso di default, griglia inline
/// "VeicGamma_Griglia"). Il workspace parte con due gamme seminate: una da rinominare e una da archiviare. Le verifiche
/// leggono il catalogo persistito (gamma.json) con un AppState nuovo, non l'immagine della griglia.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
[Trait("Category", "Forms")]
[Trait("Area", "Gamme")]
public sealed class GammeVeicoliCrudUiTests : UiTestBase
{
    private const string GammaDaRinominare = "Gamma-Seme";
    private const string GammaDaArchiviare = "Gamma-Archiviare";
    private const string GammaNuova = "Gamma-QA-01";
    private const string GammaRinominata = "Gamma-Rinominata";
    private const string GammaDaScartare = "Gamma-Da-Scartare";

    public GammeVeicoliCrudUiTests() : base(CreaWorkspaceConGamme()) { }

    private static TestWorkspace CreaWorkspaceConGamme()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(GammeVeicoliCrudUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        state.Master.Gamma.Save(new List<Gamma>
        {
            new() { Nome = GammaDaRinominare, ModificatoDa = "UITests" },
            new() { Nome = GammaDaArchiviare, ModificatoDa = "UITests" },
        });
        return ws;
    }

    private List<Gamma> GammePersistite()
    {
        using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
        return state.Master.Gamma.Load();
    }

    private string FotoGamme() => JsonSerializer.Serialize(GammePersistite(), JsonIo.CompactOptions);

    private GammeVeicoloPage ApriGammeInModifica()
    {
        var window = App.WaitForMainWindow();
        new MainShellPage(window).GoToImpostazioni().VaiAVeicoli();
        var gamme = new GammeVeicoloPage(window);
        gamme.Espandi();
        gamme.IniziaModifica();
        return gamme;
    }

    [Fact]
    public void Gamma_NuovaRigaSalvata_AppareNelCatalogoEPersisteNelFile()
    {
        try
        {
            var gamme = ApriGammeInModifica();
            gamme.Nuovo();
            gamme.ScriviNomeUltimaRiga(GammaNuova);
            Assert.Contains(GammaNuova, gamme.NomiVisibili());

            gamme.Salva();
            Wait.ForCondition(() => !gamme.InModifica, "gamme in sola lettura dopo Salva");
            Wait.ForCondition(() => GammePersistite().Any(g => g.Nome == GammaNuova && !g.Archiviata),
                "la nuova gamma è scritta in gamma.json");
            Assert.Contains(GammaNuova, gamme.NomiVisibili());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Gamma_NuovaRigaSalvata_AppareNelCatalogoEPersisteNelFile), ex);
        }
    }

    [Fact]
    public void Gamma_AnnullaConRigaNonSalvata_ChiedeConferma_NoRestaSi_Scarta_SenzaScrivere()
    {
        try
        {
            var gamme = ApriGammeInModifica();
            var fotoPrima = FotoGamme();
            gamme.Nuovo();
            gamme.ScriviNomeUltimaRiga(GammaDaScartare);

            gamme.Annulla();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma dopo No");
            Assert.True(gamme.InModifica, "No lascia le gamme in modifica");
            Assert.Contains(GammaDaScartare, gamme.NomiVisibili());

            gamme.Annulla();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => !gamme.InModifica, "gamme in sola lettura dopo Sì");
            Assert.DoesNotContain(GammaDaScartare, gamme.NomiVisibili());
            Assert.Equal(fotoPrima, FotoGamme());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Gamma_AnnullaConRigaNonSalvata_ChiedeConferma_NoRestaSi_Scarta_SenzaScrivere), ex);
        }
    }

    /// <summary>
    /// Una gamma già salvata non si rinomina scrivendo nella cella: il Salva è bloccato dal guard di Unisci/Rinomina
    /// (che rimanda al pannello "Rinomina..."), e il file resta invariato. Il pannello Rinomina per le gamme non è
    /// montato nella pagina Veicoli (esiste solo in GammaView.axaml, non montato): è un difetto dell'app, riportato
    /// separatamente; questo test verifica solo il blocco.
    /// </summary>
    [Fact]
    public void Gamma_RinominaInlineDiUnaGammaSalvata_BloccataConMessaggioESenzaScrittura()
    {
        try
        {
            var gamme = ApriGammeInModifica();
            var fotoPrima = FotoGamme();
            gamme.RinominaRiga(GammaDaRinominare, GammaRinominata);

            gamme.Salva();
            Wait.ForCondition(() => (gamme.MessaggioErrore ?? string.Empty).Contains("non può essere rinominata", StringComparison.Ordinal),
                "banner di blocco della rinomina diretta dopo Salva");
            Assert.True(gamme.InModifica, "l'editor resta in modifica dopo la rinomina diretta bloccata");
            Assert.Equal(fotoPrima, FotoGamme());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Gamma_RinominaInlineDiUnaGammaSalvata_BloccataConMessaggioESenzaScrittura), ex);
        }
    }

    [Fact]
    public void Gamma_ArchiviaRigaESalva_LaRigaEscoDallaGrigliaEIlFileLaMarcaArchiviata()
    {
        try
        {
            var gamme = ApriGammeInModifica();
            Assert.Contains(GammaDaArchiviare, gamme.NomiVisibili());

            gamme.ArchiviaRiga(GammaDaArchiviare);
            Wait.ForCondition(() => !gamme.NomiVisibili().Contains(GammaDaArchiviare), "riga archiviata fuori dalla griglia");

            gamme.Salva();
            Wait.ForCondition(() => !gamme.InModifica, "gamme in sola lettura dopo Salva");
            Wait.ForCondition(() => GammePersistite().Any(g => g.Nome == GammaDaArchiviare && g.Archiviata),
                "gamma.json marca la gamma come archiviata");
            Assert.DoesNotContain(GammaDaArchiviare, gamme.NomiVisibili());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Gamma_ArchiviaRigaESalva_LaRigaEscoDallaGrigliaEIlFileLaMarcaArchiviata), ex);
        }
    }

    [Fact]
    public void Gamma_NomeVuoto_SalvaRifiutataConMessaggioENessunaScrittura()
    {
        try
        {
            var gamme = ApriGammeInModifica();
            var fotoPrima = FotoGamme();
            gamme.Nuovo();

            gamme.Salva();
            Wait.ForCondition(() => (gamme.MessaggioErrore ?? string.Empty).Contains("Nome obbligatorio", StringComparison.Ordinal),
                "banner di errore 'Nome obbligatorio' dopo Salva");
            Assert.True(gamme.InModifica, "l'editor resta in modifica dopo un Salva rifiutato");
            Assert.Equal(fotoPrima, FotoGamme());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Gamma_NomeVuoto_SalvaRifiutataConMessaggioENessunaScrittura), ex);
        }
    }
}
