using System.Text.RegularExpressions;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class OnboardingServiceTests
{
    [Theory]
    [InlineData(RuoloOperatore.Master)]
    [InlineData(RuoloOperatore.Standard)]
    [InlineData(RuoloOperatore.Viewer)]
    public void CreaPassi_ProduceUnPercorsoCompletoPerOgniRuolo(RuoloOperatore ruolo)
    {
        var passi = OnboardingService.CreaPassi(ruolo, "it");

        Assert.True(passi.Count >= 7);
        Assert.Equal("home", passi[0].NavId);
        Assert.Equal("home", passi[^1].NavId);
        Assert.All(passi, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Titolo));
            Assert.False(string.IsNullOrWhiteSpace(p.Testo));
            Assert.Contains(p.NavId, new[] { "home", "test", "anomalie", "report", "audit", "impostazioni" });
        });
        Assert.True(passi.Count(p => !string.IsNullOrWhiteSpace(p.TargetId)) >= 3);
    }

    [Fact]
    public void CreaPassi_MasterOperatoreViewer_HannoContenutiDiversiEPertinenti()
    {
        var master = OnboardingService.CreaPassi(RuoloOperatore.Master, "it");
        var operatore = OnboardingService.CreaPassi(RuoloOperatore.Standard, "it");
        var viewer = OnboardingService.CreaPassi(RuoloOperatore.Viewer, "it");

        Assert.Contains(master, p => p.Testo.Contains("Operatori") && p.NavId == "impostazioni");
        Assert.Contains(operatore, p => p.Testo.Contains("allega evidenze") && p.NavId == "test");
        Assert.Contains(viewer, p => (p.Titolo + " " + p.Testo).Contains("sola lettura") && p.NavId == "impostazioni");
        Assert.NotEqual(master.Select(p => p.Titolo), operatore.Select(p => p.Titolo));
        Assert.NotEqual(operatore.Select(p => p.Titolo), viewer.Select(p => p.Titolo));
    }

    [Fact]
    public void Completamento_ESeparatoPerUtenteERuolo()
    {
        var config = new AppConfig();

        OnboardingService.SegnaCompletato(config, "Mario Rossi", RuoloOperatore.Standard);

        Assert.True(OnboardingService.ECompletato(config, "Mario Rossi", RuoloOperatore.Standard));
        Assert.False(OnboardingService.ECompletato(config, "Mario Rossi", RuoloOperatore.Master));
        Assert.False(OnboardingService.ECompletato(config, "Altro Utente", RuoloOperatore.Standard));
    }

    [Fact]
    public void Completamento_ChiaveUtenteIgnoraMaiuscoleESpaziEsterni()
    {
        var config = new AppConfig();
        OnboardingService.SegnaCompletato(config, "  Mario Rossi  ", RuoloOperatore.Viewer);

        Assert.True(OnboardingService.ECompletato(config, "mario rossi", RuoloOperatore.Viewer));
    }

    [Theory]
    [InlineData(RuoloOperatore.Master)]
    [InlineData(RuoloOperatore.Standard)]
    [InlineData(RuoloOperatore.Viewer)]
    public void CreaPassi_Inglese_NonLasciaTestiItalianiPrincipali(RuoloOperatore ruolo)
    {
        var passi = OnboardingService.CreaPassi(ruolo, "en");

        Assert.Equal("Welcome to TestLoom", passi[0].Titolo);
        Assert.DoesNotContain(passi, p => p.Titolo.Contains("Pronto") || p.Testo.Contains("sola lettura"));
    }

    [Fact]
    public void Completamento_VersionePrecedenteRimostraIlTourUnaSolaVolta()
    {
        var config = new AppConfig();
        const string operatore = "Mario Rossi";
        OnboardingService.SegnaCompletato(config, operatore, RuoloOperatore.Standard);
        config.VersioniOnboardingCompletate![OnboardingService.Chiave(operatore, RuoloOperatore.Standard)] =
            OnboardingService.VersioneCorrente - 1;

        Assert.False(OnboardingService.ECompletato(config, operatore, RuoloOperatore.Standard));

        OnboardingService.SegnaCompletato(config, operatore, RuoloOperatore.Standard);
        Assert.True(OnboardingService.ECompletato(config, operatore, RuoloOperatore.Standard));
    }

    [Theory]
    [InlineData(RuoloOperatore.Master)]
    [InlineData(RuoloOperatore.Standard)]
    [InlineData(RuoloOperatore.Viewer)]
    public void CreaPassi_ItalianoEInglese_HannoLaStessaNavigazione(RuoloOperatore ruolo)
    {
        var it = OnboardingService.CreaPassi(ruolo, "it");
        var en = OnboardingService.CreaPassi(ruolo, "en");

        Assert.Equal(it.Select(p => (p.Icona, p.NavId, p.TargetId)), en.Select(p => (p.Icona, p.NavId, p.TargetId)));
    }

    [Theory]
    [InlineData(RuoloOperatore.Master)]
    [InlineData(RuoloOperatore.Standard)]
    [InlineData(RuoloOperatore.Viewer)]
    public void CreaPassi_TargetIdEsistonoNelleViewXaml(RuoloOperatore ruolo)
    {
        var inVista = TargetIdNelleViste();

        var passi = OnboardingService.CreaPassi(ruolo, "it").Concat(OnboardingService.CreaPassi(ruolo, "en"));
        foreach (var passo in passi.Where(p => !string.IsNullOrEmpty(p.TargetId)))
            Assert.True(inVista.Contains(passo.TargetId), $"TargetId '{passo.TargetId}' del tour non esiste nei file .axaml.");
    }

    [Fact]
    public void CreaPassi_IndietroSempreEConflittiSoloPerMaster()
    {
        Assert.Contains(OnboardingService.CreaPassi(RuoloOperatore.Standard, "it"), p => p.TargetId == "main-indietro");
        Assert.Contains(OnboardingService.CreaPassi(RuoloOperatore.Viewer, "it"), p => p.TargetId == "main-indietro");
        Assert.Contains(OnboardingService.CreaPassi(RuoloOperatore.Master, "it"), p => p.TargetId == "settings-conflicts");
        Assert.DoesNotContain(OnboardingService.CreaPassi(RuoloOperatore.Standard, "it"), p => p.TargetId == "settings-conflicts");
        Assert.DoesNotContain(OnboardingService.CreaPassi(RuoloOperatore.Viewer, "it"), p => p.TargetId == "settings-conflicts");
    }

    private static HashSet<string> TargetIdNelleViste()
    {
        var regex = new Regex(@"Spotlight\.TargetId=""([^""]+)""");
        var trovati = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(ViewsDir(), "*.axaml", SearchOption.AllDirectories))
            foreach (Match match in regex.Matches(File.ReadAllText(file)))
                trovati.Add(match.Groups[1].Value);
        return trovati;
    }

    private static string ViewsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "TestLoom.App", "Views");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir, "src", "TestLoom.App", "Views");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Cartella Views non trovata a partire da " + AppContext.BaseDirectory);
    }
}
