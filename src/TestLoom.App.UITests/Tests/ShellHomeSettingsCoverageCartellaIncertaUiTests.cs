using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Banner "Cartella condivisa incerta" (SharedFolderHealthMonitor). Il segnale e' sintetico ma reale: una copia di
/// conflitto OneDrive recente ("... (Laptop-2).json") accanto al file canonico rende la cartella "incerta" senza
/// alterare alcun dato. Il banner deve spiegare il motivo, e "Attiva visualizzatore" deve attivare la sola lettura
/// su questo PC (verificato sul marker del servizio, non solo sulla UI).
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "CartellaIncerta")]
public sealed class ShellHomeSettingsCoverageCartellaIncertaUiTests : UiTestBase
{
    private static readonly TimeSpan AttesaSalute = TimeSpan.FromSeconds(90);

    public ShellHomeSettingsCoverageCartellaIncertaUiTests() : base(CreaWorkspaceConCopiaDiConflitto()) { }

    private static TestWorkspace CreaWorkspaceConCopiaDiConflitto()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(ShellHomeSettingsCoverageCartellaIncertaUiTests));
        var manifest = new DataPaths(ws.WorkspaceDir).VersionManifestFile;
        if (!File.Exists(manifest))
            throw new InvalidOperationException($"Fixture non valida: manca il file canonico '{manifest}' accanto alla copia di conflitto.");
        var copia = Path.Combine(Path.GetDirectoryName(manifest)!,
            Path.GetFileNameWithoutExtension(manifest) + " (Laptop-2).json");
        File.Copy(manifest, copia, overwrite: true);
        File.SetLastWriteTime(copia, DateTime.Now);
        return ws;
    }

    /// <summary>Attende il banner premendo "Aggiorna ora": il ricalcolo della salute della cartella avviene con il giro di
    /// rilettura del workspace, che nel test non parte da solo. Ritenta finche' il banner non compare o scade il tempo.</summary>
    private static AutomationElement BannerIncerta(Window window)
    {
        AutomationElement? banner = null;
        var scadenza = DateTime.UtcNow + AttesaSalute;
        while (banner == null && DateTime.UtcNow < scadenza)
        {
            ElementFinders.WaitButtonById(window, "Main_AggiornaOra_Button").SafeInvoke();
            Wait.TryForCondition(() => (banner = VisibilePerId(window, "Main_CartellaIncerta_Banner")) != null, TimeSpan.FromSeconds(15));
        }
        return banner ?? throw new TimeoutException(
            "Il banner 'Cartella condivisa incerta' non compare dopo 'Aggiorna ora'. " + DescriviBanner(window));
    }

    /// <summary>Diagnostica: il banner puo' essere nell'albero UIA ma fuori dall'area visibile (VisibilePerId lo scarta).</summary>
    private static string DescriviBanner(Window window)
    {
        var banner = ElementFinders.TryFindById(window, "Main_CartellaIncerta_Banner");
        var pulsante = ElementFinders.TryFindById(window, "Main_AggiornaOra_Button");
        var descrizioneBanner = banner == null
            ? "banner assente dall'albero UIA"
            : $"banner {banner.BoundingRectangle.Left:0},{banner.BoundingRectangle.Top:0} " +
              $"{banner.BoundingRectangle.Width:0}x{banner.BoundingRectangle.Height:0} px UIA, fuori schermo={banner.Properties.IsOffscreen.ValueOrDefault}";
        var descrizionePulsante = pulsante == null
            ? "pulsante assente"
            : $"'Aggiorna ora' {pulsante.BoundingRectangle.Left:0},{pulsante.BoundingRectangle.Top:0} " +
              $"{pulsante.BoundingRectangle.Width:0}x{pulsante.BoundingRectangle.Height:0} px UIA, fuori schermo={pulsante.Properties.IsOffscreen.ValueOrDefault}";
        return $"{descrizioneBanner}; {descrizionePulsante}; {DescriviGeometria(window)}.";
    }

    [Fact]
    public void CartellaIncerta_BannerSpiegaLaCopiaDiConflitto_EAttivaIlVisualizzatore()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).DismissNotificationsIfShown();

            var banner = BannerIncerta(window);
            Assert.True(TestoContieneVisibile(banner, "Copie di conflitto"),
                "il banner deve spiegare che sono presenti copie di conflitto recenti");
            Assert.True(TestoContieneVisibile(banner, "(Laptop-2).json"),
                "il banner deve citare il file che ha reso la cartella incerta");

            ElementFinders.WaitButtonById(window, "Main_AttivaVisualizzatore_Button").SafeInvoke();
            ConfermaSeCompare(App, TimeSpan.FromSeconds(5));

            var root = new DataPaths(Workspace.WorkspaceDir).Root;
            Wait.ForCondition(() => WorkspaceReadOnlyService.IsEnabled(root),
                "visualizzatore locale attivo (sola lettura su questo PC)", TimeSpan.FromSeconds(20));
            Wait.ForCondition(() => !VisibileInUia(ElementFinders.TryFindById(window, "Main_CartellaIncerta_Banner")),
                "il banner sparisce con il visualizzatore attivo", TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CartellaIncerta_BannerSpiegaLaCopiaDiConflitto_EAttivaIlVisualizzatore), ex); }
    }

    // Diagnostica: il servizio di salute, letto direttamente sul workspace di test, deve dichiarare la cartella incerta
    // per la copia di conflitto. Separa un difetto del fixture da un difetto del ricalcolo nell'interfaccia.
    [Fact]
    public void Diagnostica_SaluteCartella_ValutataDalServizioSulFixture()
    {
        var salute = SharedFolderHealthMonitor.Valuta(new DataPaths(Workspace.WorkspaceDir));
        Assert.True(salute.Stato == SharedFolderHealthState.Incerta,
            $"Valutazione del servizio: {salute.Stato}; motivi: {string.Join(" | ", salute.Motivi)}");
    }
}
