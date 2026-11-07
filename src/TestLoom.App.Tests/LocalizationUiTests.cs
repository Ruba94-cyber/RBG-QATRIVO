using System.Globalization;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public class LocalizationUiTests
{
    [Theory]
    [InlineData("it", "NON COMPLETAMENTE COMPILATO", "reportistica")]
    [InlineData("en", "INCOMPLETELY FILLED IN", "reports")]
    [InlineData("fr", "PAS COMPLÈTEMENT RENSEIGNÉ", "rapports")]
    public void FunzionalitaMancante_AvvisaDiTestIncompletoEImpattoSullaReportistica(
        string lingua, string incompleto, string report)
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            loc.Lingua = lingua;
            var messaggio = loc.Traduci("FunzionalitaMancanteMsg");
            Assert.Contains(incompleto, messaggio, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(report, messaggio, StringComparison.OrdinalIgnoreCase);
            Assert.True(messaggio.Count(c => c == '\n') >= 2);
            Assert.False(string.IsNullOrWhiteSpace(loc.Traduci("FunzionalitaMancanteProsegui")));
            Assert.False(string.IsNullOrWhiteSpace(loc.Traduci("FunzionalitaMancanteIndietro")));
        }
        finally { loc.Lingua = originale; }
    }

    [Theory]
    [InlineData("it", "API", "modulo standard")]
    [InlineData("en", "API", "standard form")]
    [InlineData("fr", "API", "formulaire standard")]
    public void PolarionSettingsDistinguishApiWritesFromBrowserForm(string language, string api, string form)
    {
        var loc = LocalizationService.Instance;
        var original = loc.Lingua;
        try
        {
            loc.Lingua = language;
            Assert.Contains(api, loc.Traduci("PolarionApiWriteToggle"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(form, loc.Traduci("PolarionBrowserUrlHelp"), StringComparison.OrdinalIgnoreCase);
        }
        finally { loc.Lingua = original; }
    }

    [Fact]
    public void LocalizedCountConverter_UsesCurrentLanguage()
    {
        var loc = LocalizationService.Instance;
        var original = loc.Lingua;
        try
        {
            loc.Lingua = "en";
            var result = LocalizedCountConverter.Instance.Convert(
                new object?[] { 3, loc.Lingua }, typeof(string), "TestInclusiFmt", CultureInfo.InvariantCulture);
            Assert.Equal("3 tests included", result);
        }
        finally { loc.Lingua = original; }
    }

    [Fact]
    public void WorkflowLabels_TranslateStatusAndSeverityToEnglish()
    {
        var loc = LocalizationService.Instance;
        var original = loc.Lingua;
        try
        {
            loc.Lingua = "en";
            Assert.Equal("Open", StatoNonConformita.Aperta.Label());
            Assert.Equal("Highest", SeveritaAnomalia.Highest.Label());
            Assert.Equal("Medium", SeveritaAnomalia.Medium.Label());
        }
        finally { loc.Lingua = original; }
    }

    [Fact]
    public async Task UnsavedChangesConfirmation_UsesCurrentLanguage()
    {
        var loc = LocalizationService.Instance;
        var originalLanguage = loc.Lingua;
        var originalHandler = ConfirmService.Handler;
        try
        {
            loc.Lingua = "en";
            ConfirmService.Handler = (title, message, yes, no) =>
            {
                Assert.Equal("Unsaved changes", title);
                Assert.Equal("There are unsaved changes. Continue and discard them?", message);
                Assert.Equal("Continue without saving", yes);
                Assert.Equal("Cancel", no);
                return Task.FromResult(false);
            };

            Assert.False(await ConfirmService.ConfermaAbbandonoModifiche());
        }
        finally
        {
            ConfirmService.Handler = originalHandler;
            loc.Lingua = originalLanguage;
        }
    }

    [Theory]
    [InlineData("AuditTitolo", "Audit di conformità", "Conformity audit")]
    [InlineData("OrdinePannelliTitolo", "Ordine pannelli", "Panel order")]
    [InlineData("OnboardingIntestazione", "RBG - QATRIVO · TOUR GUIDATO", "RBG - QATRIVO · GUIDED TOUR")]
    [InlineData("CambioPinDemoTitolo", "Proteggi l'account demo", "Secure the demo account")]
    [InlineData("CercaNellaGuida", "Cerca nella guida…", "Search help…")]
    [InlineData("NavAudit", "Audit", "Audit")]
    [InlineData("NavAnomalie / CR", "Anomalie / CR", "Anomalies / CR")]
    [InlineData("ForkSyncTitolo", "Conflitti di sincronizzazione Test", "Test synchronization conflicts")]
    [InlineData("IndiceLocaleTitolo", "Indice locale Test", "Local Test index")]
    [InlineData("StoragePuliziaEsegui", "Esegui pulizia ora", "Run cleanup now")]
    [InlineData("NotificheEliminaLette", "🗑 Elimina lette", "🗑 Delete read")]
    [InlineData("StorageManutenzioneAutomatica", "La manutenzione automatica viene eseguita in background ogni 12 ore.", "Automatic maintenance runs in the background every 12 hours.")]
    [InlineData("StorageSqliteOttimizzato", " SQLite locale ottimizzato; nessun VACUUM necessario.", " Local SQLite optimized; no VACUUM required.")]
    [InlineData("ForkGiaRisolto", "Il conflitto risulta già risolto o non è più presente dopo la sincronizzazione.", "The conflict is already resolved or is no longer present after synchronization.")]
    public void NuoveAreeUi_HannoTestoItalianoEInglese(string chiave, string italiano, string inglese)
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            loc.Lingua = "it";
            Assert.Equal(italiano, loc.Traduci(chiave));
            loc.Lingua = "en";
            Assert.Equal(inglese, loc.Traduci(chiave));
        }
        finally { loc.Lingua = originale; }
    }

    [Fact]
    public void EstaFilterLocalizedConverter_UsesLanguageWhilePreservingUnderlyingValue()
    {
        var loc = LocalizationService.Instance;
        var original = loc.Lingua;
        try
        {
            foreach (var (language, expected) in new[] { ("it", "Tutti"), ("en", "All"), ("fr", "Tous") })
            {
                loc.Lingua = language;
                IList<object?> values = new object?[] { "Tutti", language };
                Assert.Equal(expected, PolarionFilterLocalizedConverter.Instance.Convert(
                    values, typeof(string), null, CultureInfo.InvariantCulture));
                Assert.Equal("Tutti", values[0]);
                values[0] = "new_request";
                Assert.Equal("new_request", PolarionFilterLocalizedConverter.Instance.Convert(
                    values, typeof(string), null, CultureInfo.InvariantCulture));
            }
        }
        finally { loc.Lingua = original; }
    }

    [Fact]
    public void EstaDashboard_TraduceEtichetteEFiltri_SenzaCambiareValoriPolarion()
    {
        var loc = LocalizationService.Instance;
        var old = loc.Lingua;
        try
        {
            var keys = LocalizationService.TraduzioniItalianoInglese.Keys
                .Where(k => k.StartsWith("EstaRpt", StringComparison.Ordinal)).ToList();
            Assert.Equal(51, keys.Count);
            Assert.All(keys, key => Assert.True(
                LocalizationService.TraduzioniFrancesi.ContainsKey(key),
                "Missing French TICKET report label: " + key));

            loc.Lingua = "it";
            Assert.Equal("Aggiorna report TICKET", loc.Traduci("EstaRptRefresh"));
            Assert.Equal("Tutti", PolarionFilterLabelConverter.Instance.Convert(
                "Tutti", typeof(string), null, CultureInfo.InvariantCulture));
            loc.Lingua = "en";
            Assert.Equal("Refresh TICKET report", loc.Traduci("EstaRptRefresh"));
            Assert.Equal("All", PolarionFilterLabelConverter.Instance.Convert(
                "Tutti", typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Opened", PolarionFilterLabelConverter.Instance.Convert(
                "Apertura", typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("bug", PolarionFilterLabelConverter.Instance.Convert(
                "bug", typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("new_request", PolarionFilterLabelConverter.Instance.Convert(
                "new_request", typeof(string), null, CultureInfo.InvariantCulture));
            loc.Lingua = "fr";
            Assert.Equal("Actualiser le rapport TICKET", loc.Traduci("EstaRptRefresh"));
            Assert.Equal("Tous", PolarionFilterLabelConverter.Instance.Convert(
                "Tutti", typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Toutes", PolarionFilterLabelConverter.Instance.Convert(
                "Tutte", typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Clôture", PolarionFilterLabelConverter.Instance.Convert(
                "Chiusura", typeof(string), null, CultureInfo.InvariantCulture));
        }
        finally { loc.Lingua = old; }
    }

    [Fact]
    public void EstaMonthlyTrend_LabelsFollowSelectedLanguageAndPreserveCounts()
    {
        var loc = LocalizationService.Instance;
        var original = loc.Lingua;
        var trend = new PolarionLifecycleViewModel.PolarionMonthlyTrend("2026-10", 8, 3, 2);
        try
        {
            loc.Lingua = "it";
            Assert.Equal("Aperti: 8", trend.CreatedLabel);
            Assert.Equal("Testing rilevati: 3", trend.TestingLabel);
            Assert.Equal("Closed rilevati: 2", trend.ClosedLabel);
            loc.Lingua = "en";
            Assert.Equal("Opened: 8", trend.CreatedLabel);
            Assert.Equal("Testing observed: 3", trend.TestingLabel);
            Assert.Equal("Closures observed: 2", trend.ClosedLabel);
            loc.Lingua = "fr";
            Assert.Equal("Ouverts : 8", trend.CreatedLabel);
            Assert.Equal("Tests observés : 3", trend.TestingLabel);
            Assert.Equal("Clôtures observées : 2", trend.ClosedLabel);
            Assert.Equal("2026-10", trend.Period);
        }
        finally { loc.Lingua = original; }
    }

    [Fact]
    public void EstaReportRow_TranslatesDisplayedMetadataButKeepsStoredFields()
    {
        var loc = LocalizationService.Instance;
        var old = loc.Lingua;
        var row = new PolarionReportService.Row
        {
            TicketId = "TICKET-82", Type = "new_request", Status = "assigned",
            Archived = true, HasCache = false
        };
        try
        {
            loc.Lingua = "it";
            Assert.Equal("Sì", row.ArchiveText);
            Assert.Equal("Non sincronizzato", row.DataQuality);
            Assert.Equal("Non noto", row.TestingText);
            loc.Lingua = "en";
            Assert.Equal("Yes", row.ArchiveText);
            Assert.Equal("Not synchronized", row.DataQuality);
            Assert.Equal("Unknown", row.TestingText);
            loc.Lingua = "fr";
            Assert.Equal("Oui", row.ArchiveText);
            Assert.Equal("Non synchronisé", row.DataQuality);
            Assert.Equal("Inconnu", row.TestingText);
            Assert.Equal("new_request", row.Type);
            Assert.Equal("assigned", row.Status);
        }
        finally { loc.Lingua = old; }
    }

    [Fact]
    public void EtichettaRuolo_DefaultSegueLingua_MaPersonalizzazioneRestaInvariata()
    {
        var catalogo = CampoSistemaDefinizioneSeed.SeedPianificazione();
        Assert.Equal("Work status", RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(
            catalogo, RuoloCampoSistema.StatoLavoro, "Work status"));

        catalogo.Single(c => c.Ruolo == RuoloCampoSistema.StatoLavoro).Etichetta = "Gate cliente";
        Assert.Equal("Gate cliente", RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(
            catalogo, RuoloCampoSistema.StatoLavoro, "Work status"));
    }

}
