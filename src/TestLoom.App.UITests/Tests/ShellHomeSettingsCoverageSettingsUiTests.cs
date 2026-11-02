using System.Text.Json;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Impostazioni -> Generale, aree non coperte: lingua (salvata subito nel config locale), preferenze con checkbox di
/// tipo notifica, backup ZIP "adesso" (con verifica), ricostruzione indice locale, pulizia storage, ordine della barra
/// di navigazione (persistito), e situazione vuota del pannello "Conflitti fra postazioni". Viewer, centro integrita',
/// bozze di emergenza e copia del backup esterno sono gia' coperti da altre classi e non vengono ripetuti.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Impostazioni")]
public sealed class ShellHomeSettingsCoverageSettingsUiTests : UiTestBase
{
    public ShellHomeSettingsCoverageSettingsUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(ShellHomeSettingsCoverageSettingsUiTests))) { }

    private string ConfigPath => Workspace.ConfigPath;

    private ImpostazioniPage ApriGenerale(out Window window)
    {
        window = App.WaitForMainWindow();
        var shell = new MainShellPage(window);
        shell.DismissNotificationsIfShown();
        var impostazioni = shell.GoToImpostazioni();
        impostazioni.VaiA("generale");
        return impostazioni;
    }

    private static string Messaggio(Window window) => ElementFinders.TryFindById(window, "Impostazioni_Messaggio_Text")?.Name ?? "";

    private static string TitoloPagina(Window window) => ElementFinders.TryFindById(window, "Impostazioni_Title_Text")?.Name ?? "";

    private string? Lingua()
    {
        using var doc = LeggiJson(ConfigPath);
        return Proprieta(doc.RootElement, "Lingua")?.GetString();
    }

    private List<string> OrdineNavConfig()
    {
        using var doc = LeggiJson(ConfigPath);
        var voce = Proprieta(doc.RootElement, "OrdineNavPrincipale");
        return voce == null ? new List<string>() : voce.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
    }

    [Fact]
    public void Lingua_PassaAInglese_EPoiTornaAItaliano_SalvandoIlConfigLocale()
    {
        try
        {
            var _ = ApriGenerale(out var window);
            Assert.Equal("Generale", TitoloPagina(window));

            ElementFinders.WaitById(window, "Impostazioni_Lingua_English_RadioButton").SafeInvoke();
            Wait.ForCondition(() => TitoloPagina(window) == "General", "titolo delle impostazioni in inglese", TimeSpan.FromSeconds(15));
            Wait.ForCondition(() => Lingua() == "en", "lingua 'en' salvata nel config locale", TimeSpan.FromSeconds(15));

            ElementFinders.WaitById(window, "Impostazioni_Lingua_Italiano_RadioButton").SafeInvoke();
            Wait.ForCondition(() => TitoloPagina(window) == "Generale", "titolo tornato in italiano", TimeSpan.FromSeconds(15));
            Wait.ForCondition(() => Lingua() == "it", "lingua 'it' salvata nel config locale", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Lingua_PassaAInglese_EPoiTornaAItaliano_SalvandoIlConfigLocale), ex); }
    }

    [Fact]
    public void Preferenze_SalvanoAvvioRidottoNotificheETipoSilenziato_NelConfigLocale()
    {
        try
        {
            ApriGenerale(out var window);

            var avvio = ElementFinders.WaitById(window, "Impostazioni_AvvioRidotto_CheckBox").AsCheckBox();
            if (avvio.IsChecked != true) avvio.SafeInvoke();
            var notifiche = ElementFinders.WaitById(window, "Impostazioni_NotificheWindows_CheckBox").AsCheckBox();
            if (notifiche.IsChecked != true) notifiche.SafeInvoke();

            // Il tipo "Informazione" e' conteggiato al momento: il checkbox lo silenzia quando viene deselezionato.
            var informazione = ElementFinders.WaitById(window, "Preferenze_NotificaTipo_Informazione_CheckBox").AsCheckBox();
            var silenziatoAtteso = informazione.IsChecked == true;
            informazione.SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Preferenze_NotificaTipo_Informazione_CheckBox").AsCheckBox().IsChecked == !silenziatoAtteso,
                "il checkbox del tipo Informazione cambia stato", TimeSpan.FromSeconds(10));

            ElementFinders.WaitById(window, "Impostazioni_SalvaPreferenze_Button").SafeInvoke();
            Wait.ForCondition(() => Messaggio(window).StartsWith("Preferenze locali salvate", StringComparison.Ordinal),
                "messaggio 'Preferenze locali salvate'", TimeSpan.FromSeconds(15));

            using var doc = LeggiJson(ConfigPath);
            var radice = doc.RootElement;
            Assert.True(Proprieta(radice, "AvvioRidotto")!.Value.GetBoolean(), "AvvioRidotto salvato a vero");
            Assert.True(Proprieta(radice, "NotificheWindows")!.Value.GetBoolean(), "NotificheWindows salvato a vero");
            var silenziati = Proprieta(radice, "NotificheTipiSilenziati")!.Value.EnumerateArray().Select(x => x.GetString()).ToList();
            Assert.Equal(silenziatoAtteso, silenziati.Contains("Informazione"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Preferenze_SalvanoAvvioRidottoNotificheETipoSilenziato_NelConfigLocale), ex); }
    }

    [Fact]
    public void BackupZipAdesso_CreaLoZipLocaleEPoiLoVerifica()
    {
        try
        {
            ApriGenerale(out var window);
            ElementFinders.WaitButtonById(window, "Impostazioni_Backup_Crea_Button").SafeInvoke();
            Wait.ForCondition(() => Messaggio(window).StartsWith("Backup JSON creato:", StringComparison.Ordinal),
                "messaggio 'Backup JSON creato'", TimeSpan.FromSeconds(60));

            var nome = Messaggio(window)["Backup JSON creato:".Length..].Trim();
            var cartella = WorkspaceLocalPaths.GetBackupDirectory(Workspace.WorkspaceDir);
            Assert.EndsWith(".zip", nome, StringComparison.OrdinalIgnoreCase);
            var zip = Path.Combine(cartella, nome);
            Assert.True(File.Exists(zip), $"lo ZIP creato deve esistere in {cartella}");
            Assert.True(new FileInfo(zip).Length > 0, "lo ZIP creato non e' vuoto");

            ElementFinders.WaitButtonById(window, "Impostazioni_Backup_VerificaZip_Button").SafeInvoke();
            Wait.ForCondition(() => Messaggio(window).Contains("Backup locale verificato", StringComparison.Ordinal),
                "verifica dell'ultimo ZIP locale", TimeSpan.FromSeconds(60));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(BackupZipAdesso_CreaLoZipLocaleEPoiLoVerifica), ex); }
    }

    [Fact]
    public void IndiceLocale_Ricostruisci_MostraEsitoEScriveIlDatabaseLocale()
    {
        try
        {
            ApriGenerale(out var window);
            ElementFinders.WaitButtonById(window, "Impostazioni_Indice_Ricostruisci_Button").SafeInvoke();
            Wait.ForCondition(() => Messaggio(window).StartsWith("Indice locale ricostruito correttamente", StringComparison.Ordinal),
                "esito 'Indice locale ricostruito correttamente'", TimeSpan.FromSeconds(90));

            var locale = WorkspaceLocalPaths.GetWorkspaceDirectory(Workspace.WorkspaceDir);
            Assert.True(Directory.Exists(locale) && Directory.EnumerateFiles(locale, "*.db", SearchOption.AllDirectories).Any(),
                $"la ricostruzione deve lasciare un database di indice in {locale}");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(IndiceLocale_Ricostruisci_MostraEsitoEScriveIlDatabaseLocale), ex); }
    }

    [Fact]
    public void PuliziaStorage_EseguiOra_AggiornaLUltimoGiro()
    {
        try
        {
            ApriGenerale(out var window);
            ElementFinders.WaitButtonById(window, "Impostazioni_Storage_Esegui_Button").SafeInvoke();
            Wait.ForCondition(() => (ElementFinders.TryFindById(window, "Impostazioni_Storage_Ultimo_Text")?.Name ?? "")
                    .Contains("Ultimo giro:", StringComparison.Ordinal),
                "testo 'Ultimo giro:' dopo la pulizia storage", TimeSpan.FromSeconds(90));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(PuliziaStorage_EseguiOra_AggiornaLUltimoGiro), ex); }
    }

    [Fact]
    public void OrdinePannelli_SpostaGiuLaVoceTest_CambiaLaBarraEPersisteNelConfig()
    {
        try
        {
            ApriGenerale(out var window);
            var testPrima = ElementFinders.WaitById(window, "Main_Nav_test").BoundingRectangle.Top;
            var anomaliePrima = ElementFinders.WaitById(window, "Main_Nav_anomalie").BoundingRectangle.Top;
            Assert.True(testPrima < anomaliePrima, "ordine di partenza: Test sopra Anomalie");

            ElementFinders.WaitButtonById(window, "Impostazioni_OrdineNav_Giu_test").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Main_Nav_anomalie").BoundingRectangle.Top
                    < ElementFinders.WaitById(window, "Main_Nav_test").BoundingRectangle.Top,
                "la barra di navigazione mostra Anomalie sopra Test dopo lo spostamento", TimeSpan.FromSeconds(15));

            Wait.ForCondition(() =>
                {
                    var ordine = OrdineNavConfig();
                    return ordine.IndexOf("anomalie") >= 0 && ordine.IndexOf("anomalie") < ordine.IndexOf("test");
                },
                "ordine salvato nel config locale (anomalie prima di test)", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(OrdinePannelli_SpostaGiuLaVoceTest_CambiaLaBarraEPersisteNelConfig), ex); }
    }

    [Fact]
    public void Conflitti_SenzaLog_MostraLaSituazioneVuotaEAggiornaSenzaErrori()
    {
        try
        {
            ApriGenerale(out var window);
            Wait.ForCondition(() => IdVisibile(window, "RecordFork_Nessuno_Text")
                    && TestoVisibile(window, "Nessun conflitto rilevato nei log sincronizzati finora.") != null,
                "situazione 'Nessun conflitto' nel pannello Conflitti", TimeSpan.FromSeconds(20));

            ElementFinders.WaitButtonById(window, "RecordFork_Aggiorna_Button").SafeInvoke();
            Wait.ForCondition(() => IdVisibile(window, "RecordFork_Nessuno_Text") && !IdVisibile(window, "RecordFork_List"),
                "dopo 'Aggiorna' il pannello resta vuoto", TimeSpan.FromSeconds(15));
            Assert.False(IdVisibile(window, "Impostazioni_Errore_Text"), "nessun messaggio di errore nelle Impostazioni");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Conflitti_SenzaLog_MostraLaSituazioneVuotaEAggiornaSenzaErrori), ex); }
    }
}
