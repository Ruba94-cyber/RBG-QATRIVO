using System.Reflection;
using System.Text.RegularExpressions;
using System.Globalization;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura della terza lingua (francese): ogni chiave ha la sua traduzione, i segnaposto e i a capo
/// coincidono in it/en/fr, la lingua "fr" cambia ricerca, etichette enum ed export, la scelta sopravvive al riavvio,
/// e l'aiuto mantiene gli stessi argomenti visibili per ruolo e contesto in ogni lingua.</summary>
[Collection("Localization")]
public class LocalizzazioneFrancaTests
{
    private static readonly Regex Segnaposto = new(@"\{[0-9]+(?::[^}]*)?\}");

    private static string[] SegnapostiOrdinati(string testo) =>
        Segnaposto.Matches(testo).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();

    [Fact]
    public void OgniChiaveDelDizionario_HaUnaTraduzioneFrancese_NonVuota()
    {
        var mancanti = LocalizationService.TraduzioniItalianoInglese.Keys
            .Where(k => !LocalizationService.TraduzioniFrancesi.TryGetValue(k, out var fr) || string.IsNullOrWhiteSpace(fr))
            .ToList();

        Assert.Empty(mancanti);
    }

    [Fact]
    public void SegnapostiIdentici_TraItalianoIngleseFrancese()
    {
        var difformi = new List<string>();
        foreach (var (chiave, coppia) in LocalizationService.TraduzioniItalianoInglese)
        {
            var fr = LocalizationService.TraduzioniFrancesi[chiave];
            var atteso = SegnapostiOrdinati(coppia.It);
            if (!atteso.SequenceEqual(SegnapostiOrdinati(coppia.En)) || !atteso.SequenceEqual(SegnapostiOrdinati(fr)))
                difformi.Add(chiave);
        }

        Assert.Empty(difformi);
    }

    [Fact]
    public void A_CapoIdentici_TraItalianoInglesEFrancese()
    {
        var difformi = LocalizationService.TraduzioniItalianoInglese
            .Where(kv =>
            {
                var aCapoIt = kv.Value.It.Count(c => c == '\n');
                return aCapoIt != kv.Value.En.Count(c => c == '\n')
                    || aCapoIt != LocalizationService.TraduzioniFrancesi[kv.Key].Count(c => c == '\n');
            })
            .Select(kv => kv.Key)
            .ToList();

        Assert.Empty(difformi);
    }

    [Fact]
    public void NessunaTraduzioneFrancese_PerChiaviSconosciute()
    {
        var sconosciute = LocalizationService.TraduzioniFrancesi.Keys
            .Where(k => !LocalizationService.TraduzioniItalianoInglese.ContainsKey(k))
            .ToList();

        Assert.Empty(sconosciute);
    }

    [Fact]
    public void LinguaFrancese_CambiaLaRicerca_eRicadeSullInglese()
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        var campo = typeof(LocalizationService).GetField("Francese", BindingFlags.NonPublic | BindingFlags.Static)!;
        var francese = (Dictionary<string, string>)campo.GetValue(null)!;
        const string chiave = "NavTest";
        var salvato = francese[chiave];
        try
        {
            loc.Lingua = "it";
            Assert.Equal("Test", loc.Traduci(chiave));
            loc.Lingua = "en";
            Assert.Equal("Tests", loc.Traduci(chiave));
            loc.Lingua = "fr";
            Assert.Equal(salvato, loc.Traduci(chiave));

            // fr senza traduzione: ricade sull'inglese, poi sulla chiave se sconosciuta
            francese.Remove(chiave);
            Assert.Equal("Tests", loc.Traduci(chiave));
            Assert.Equal("CHIAVE_INESISTENTE", loc.Traduci("CHIAVE_INESISTENTE"));
        }
        finally
        {
            francese[chiave] = salvato;
            loc.Lingua = originale;
        }
    }

    [Fact]
    public void LinguaFrancese_CambiaEtichetteEnum()
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            loc.Lingua = "it";
            Assert.Equal("Da eseguire", Esito.DaEseguire.Label());
            loc.Lingua = "en";
            Assert.Equal("To be executed", Esito.DaEseguire.Label());
            loc.Lingua = "fr";
            Assert.Equal("À exécuter", Esito.DaEseguire.Label());
            Assert.Equal("Opérateur", RuoloOperatore.Standard.Label());
            Assert.Equal("Banc", AmbienteProva.Banco.Label());
        }
        finally
        {
            loc.Lingua = originale;
        }
    }

    [Fact]
    public void StatiChangeRequest_SonoLocalizzatiInItalianoIngleseEFrancese()
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        string Etichetta(StatoChangeRequest stato) =>
            WorkflowLabelConverter.Instance.Convert(stato, typeof(string), null, CultureInfo.InvariantCulture)?.ToString() ?? "";
        var stati = Enum.GetValues<StatoChangeRequest>();
        try
        {
            loc.Lingua = "it";
            Assert.Equal(new[] { "Aperta", "Implementata", "Da validare", "Validata", "Chiusa", "Annullata" }, stati.Select(Etichetta));
            loc.Lingua = "en";
            Assert.Equal(new[] { "Open", "Implemented", "To validate", "Validated", "Closed", "Cancelled" }, stati.Select(Etichetta));
            loc.Lingua = "fr";
            Assert.Equal(new[] { "Ouverte", "Implémentée", "À valider", "Validée", "Clôturée", "Annulée" }, stati.Select(Etichetta));
        }
        finally
        {
            loc.Lingua = originale;
        }
    }

    [Fact]
    public void Scegli_RestituisceIlTestoDellaLinguaCorrente()
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            loc.Lingua = "it";
            Assert.Equal("it", loc.Scegli("it", "en", "fr"));
            loc.Lingua = "en";
            Assert.Equal("en", loc.Scegli("it", "en", "fr"));
            loc.Lingua = "fr";
            Assert.Equal("fr", loc.Scegli("it", "en", "fr"));
            Assert.True(loc.EFrancese);
            Assert.False(loc.EInglese);
            Assert.False(loc.EItaliano);
        }
        finally
        {
            loc.Lingua = originale;
        }
    }

    [Fact]
    public void LinguaSconosciuta_RicadeSullItaliano()
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            loc.Lingua = "xx";
            Assert.Equal("it", loc.Lingua);
            Assert.Equal("it", LocalizationService.NormalizzaLingua("de"));
            Assert.Equal("it", LocalizationService.NormalizzaLingua(null));
            Assert.Equal("fr", LocalizationService.NormalizzaLingua("fr"));
        }
        finally
        {
            loc.Lingua = originale;
        }
    }

    [Fact]
    public void LinguaFrancese_SopravvivePersistenzaERiavvio()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_FrConfig_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "config.json");
            var service = new AppConfigService(path);
            var config = service.Load();
            config.Lingua = "fr";
            service.Save(config);

            Assert.Equal("fr", new AppConfigService(path).Load().Lingua);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Onboarding_FrancesePerOgniRuolo_HaStessoNumeroDiPassiDellItaliano()
    {
        foreach (var ruolo in new[] { RuoloOperatore.Master, RuoloOperatore.Standard, RuoloOperatore.Viewer })
        {
            var it = OnboardingService.CreaPassi(ruolo, "it");
            var fr = OnboardingService.CreaPassi(ruolo, "fr");

            Assert.Equal(it.Count, fr.Count);
            Assert.Equal("Bienvenue dans TestLoom", fr[0].Titolo);
            Assert.Equal(it.Select(p => p.NavId), fr.Select(p => p.NavId));
            Assert.Equal(it.Select(p => p.TargetId), fr.Select(p => p.TargetId));
        }
    }

    [Fact]
    public void Aiuto_FrancesePerOgniArgomento_StessoIndiceDellItaliano()
    {
        var it = HelpViewModel.TitoliArgomenti("it");
        var fr = HelpViewModel.TitoliArgomenti("fr");

        Assert.Equal(it.Count, fr.Count);
        Assert.Equal("Accès", fr[0]);
        Assert.Equal(HelpViewModel.TitoliArgomenti("en").Count, fr.Count);
    }

    [Theory]
    [InlineData(RuoloOperatore.Master)]
    [InlineData(RuoloOperatore.Standard)]
    [InlineData(RuoloOperatore.Viewer)]
    public void Aiuto_ParitaPerRuoloEContesto_StessiIndiciVisibiliInItalianoEFrancese(RuoloOperatore ruolo)
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            foreach (var contesto in new[] { "home", "test", "anomalie", "report", "audit", "impostazioni" })
            {
                var attesi = IndiciVisibili("it", ruolo, contesto);
                Assert.NotEmpty(attesi);
                Assert.Equal(attesi, IndiciVisibili("fr", ruolo, contesto));
            }
        }
        finally
        {
            loc.Lingua = originale;
        }
    }

    /// <summary>Indici, nell'elenco della lingua indicata, degli argomenti visibili per ruolo e contesto, nell'ordine mostrato.</summary>
    private static List<int> IndiciVisibili(string lingua, RuoloOperatore ruolo, string contesto)
    {
        LocalizationService.Instance.Lingua = lingua;
        var vm = new HelpViewModel(() => ruolo) { Contesto = contesto };
        var elenco = HelpViewModel.SezioniDi(lingua);
        var indici = vm.Sezioni.Select(s => elenco.FindIndex(e => ReferenceEquals(e, s))).ToList();
        Assert.DoesNotContain(-1, indici);
        return indici;
    }

    [Fact]
    public void AuditExcel_InFrancese_IntestazioniTradotteNelFile()
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_AuditFr_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var state = TestStateFactory.Create();
            state.InizializzaCartella(dir);
            loc.Lingua = "fr";

            var bytes = AuditReportService.GeneraExcel(state);

            using var workbook = new XLWorkbook(new MemoryStream(bytes));
            var legenda = workbook.Worksheet("Legenda");
            Assert.Equal("Score", legenda.Cell(4, 1).GetString());
            Assert.Equal("Signification", legenda.Cell(4, 2).GetString());
        }
        finally
        {
            loc.Lingua = originale;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
