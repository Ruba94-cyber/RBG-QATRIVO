using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Cataloghi master Strumenti, Veicoli e Funzionalità: crea una voce con "Nuovo", la salva e poi la archivia dal pulsante
/// della sua riga. Ogni esito si verifica sul file del catalogo, non solo sulla griglia. Copre le righe I48 (Strumenti
/// nuovo/archivia), I41 (Veicoli nuovo/archivia) e I28 (Funzionalità nuova/archivia riga).
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
[Trait("Area", "MasterData")]
public sealed class MasterDataNuovoArchiviaUiTests : UiTestBase
{
    private const string NomeNuovo = "QA Voce Nuova";

    public MasterDataNuovoArchiviaUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(MasterDataNuovoArchiviaUiTests))) { }

    /// <summary>Crea la voce in modifica, salva e verifica che il catalogo la contenga non archiviata.</summary>
    private void CreaESalva(MasterDataEditorPage pagina, Func<AppState, IEnumerable<MasterEntityNome>> catalogo)
    {
        pagina.IniziaModifica();
        pagina.NuovaRiga();
        pagina.ScriviChiaveUltimaRiga(NomeNuovo);
        pagina.Salva();
        Wait.ForCondition(() => !pagina.InModifica, "pagina tornata in lettura dopo Salva");
        Wait.ForCondition(() => LeggiVoce(catalogo) is { Archiviata: false }, "voce creata nel catalogo, non archiviata");
    }

    /// <summary>Archivia la voce dal pulsante della sua riga e verifica che il catalogo la segni come archiviata.</summary>
    private void ArchiviaESalva(MasterDataEditorPage pagina, Func<AppState, IEnumerable<MasterEntityNome>> catalogo)
    {
        pagina.IniziaModifica();
        pagina.ArchiviaRiga(NomeNuovo);
        pagina.Salva();
        Wait.ForCondition(() => !pagina.InModifica, "pagina tornata in lettura dopo Salva dell'archiviazione");
        Wait.ForCondition(() => LeggiVoce(catalogo) is { Archiviata: true }, "voce archiviata nel catalogo");
    }

    /// <summary>Voce con il nome di prova nel catalogo persistito, letto con uno stato nuovo (il TestLoom può essere aperto).</summary>
    private MasterEntityNome? LeggiVoce(Func<AppState, IEnumerable<MasterEntityNome>> catalogo)
    {
        using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
        return catalogo(state).SingleOrDefault(v => v.Nome == NomeNuovo);
    }

    [Fact]
    public void Strumenti_NuovoSalvaEArchivia_ScriveIlCatalogo()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiA("strumenti");
            var pagina = new MasterDataEditorPage(window, "Strum");
            CreaESalva(pagina, s => s.Master.Strumenti.Load().Select(x => new MasterEntityNome(x.Nome, x.Archiviata)));
            ArchiviaESalva(pagina, s => s.Master.Strumenti.Load().Select(x => new MasterEntityNome(x.Nome, x.Archiviata)));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Strumenti_NuovoSalvaEArchivia_ScriveIlCatalogo), ex);
        }
    }

    [Fact]
    public void Veicoli_NuovoSalvaEArchivia_ScriveIlCatalogo()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiA("veicoli");
            var pagina = new MasterDataEditorPage(window, "Veic");
            CreaESalva(pagina, s => s.Master.Veicoli.Load().Select(x => new MasterEntityNome(x.Nome, x.Archiviata)));
            ArchiviaESalva(pagina, s => s.Master.Veicoli.Load().Select(x => new MasterEntityNome(x.Nome, x.Archiviata)));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Veicoli_NuovoSalvaEArchivia_ScriveIlCatalogo), ex);
        }
    }

    [Fact]
    public void Funzionalita_NuovaSalvaEArchiviaRiga_ScriveIlCatalogo()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiA("funzionalita");
            var pagina = new MasterDataEditorPage(window, "Funz");
            CreaESalva(pagina, s => s.Master.Funzionalita.Load().Select(x => new MasterEntityNome(x.Nome, x.Archiviata)));
            ArchiviaESalva(pagina, s => s.Master.Funzionalita.Load().Select(x => new MasterEntityNome(x.Nome, x.Archiviata)));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Funzionalita_NuovaSalvaEArchiviaRiga_ScriveIlCatalogo), ex);
        }
    }
}

/// <summary>Vista minima di una voce di catalogo (nome e stato di archiviazione) usata per le verifiche sul file.</summary>
public sealed record MasterEntityNome(string Nome, bool Archiviata);
