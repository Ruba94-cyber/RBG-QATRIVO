using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class TooltipValoriReportTests
{
    [Fact]
    public void CelleReport_DescrivonoModalitaStatiEValoriManuali()
    {
        var lingua = LocalizationService.Instance.Lingua;
        LocalizationService.Instance.Lingua = "it";
        try
        {
            var conforme = new CellaMatriceReport
            {
                ModalitaStato = true,
                Stati = new StatiMatrice(1, 0, 0, 0, 0, 0),
                Manuali = 1,
            };
            var tooltipStato = MatriceTooltipService.TooltipCella(conforme);
            Assert.Contains("Stato =", tooltipStato);
            Assert.Contains("ultimo esito", tooltipStato);
            Assert.Contains("Valore manuale", tooltipStato);

            var testPercentuale = new CellaMatriceReport { MostraConteggioTest = true };
            var tooltipTest = MatriceTooltipService.TooltipCella(testPercentuale);
            Assert.Contains("Test % =", tooltipTest);
            Assert.Contains("TestCase", tooltipTest);
            Assert.Contains("snapshot", tooltipTest, StringComparison.OrdinalIgnoreCase);

            var tooltipPercentuale = MatriceTooltipService.DescrizioneModalita(ModalitaCellaMatrice.Percentuale);
            Assert.Contains("Stato % =", tooltipPercentuale);
            Assert.Contains("combinazione", tooltipPercentuale, StringComparison.OrdinalIgnoreCase);

            var tooltipTempoTotale = MatriceTooltipService.DescrizioneModalita(ModalitaCellaMatrice.TempoTotale);
            Assert.Contains("Tempo totale =", tooltipTempoTotale);
            Assert.Contains("durata nota", tooltipTempoTotale, StringComparison.OrdinalIgnoreCase);
            var tooltipTempoMedio = MatriceTooltipService.DescrizioneModalita(ModalitaCellaMatrice.TempoMedio);
            Assert.Contains("Tempo medio =", tooltipTempoMedio);
            Assert.Contains("durate mancanti", tooltipTempoMedio, StringComparison.OrdinalIgnoreCase);

            var na = new CellaMatriceReport { NonApplicabile = true, NotaNonApplicabile = "Nota NA" };
            var tooltipNa = MatriceTooltipService.TooltipCella(na);
            Assert.Contains("N.A. =", tooltipNa);
            Assert.Contains("Nota NA", tooltipNa);
        }
        finally { LocalizationService.Instance.Lingua = lingua; }
    }

    [Fact]
    public void HomeEReport_ColleganoTooltipAValoriEControlli()
    {
        var radice = new DirectoryInfo(AppContext.BaseDirectory);
        while (radice != null && !Directory.Exists(Path.Combine(radice.FullName, "src", "TestLoom.App", "Views"))) radice = radice.Parent;
        Assert.NotNull(radice);
        var viste = Path.Combine(radice!.FullName, "src", "TestLoom.App", "Views");

        var home = File.ReadAllText(Path.Combine(viste, "HomeView.axaml"));
        Assert.Contains("EsitoTooltipConverter.Instance", home);
        Assert.Contains("ToolTip.Tip=", home); // il selettore KPI della Home usa tooltip dedicati (chiave HomeTooltipConformita non più referenziata dalla vista)

        var grafico = File.ReadAllText(Path.Combine(viste, "GraficoCard.axaml"));
        Assert.Contains("HomeTooltipGraficoEsiti", grafico);

        var report = File.ReadAllText(Path.Combine(viste, "ReportView.axaml"));
        Assert.Contains("ReportMatriceTooltipConverter.Instance", report);
        Assert.Contains("MatriceTooltipModalitaStatoPercentuale", report);
        Assert.Contains("MatriceTooltipValoreConforme", report);
    }

    [Fact]
    public void ReportExcel_IncludeLeDefinizioniNelDescrittoreECommento()
    {
        var lingua = LocalizationService.Instance.Lingua;
        LocalizationService.Instance.Lingua = "it";
        var cartella = Path.Combine(Path.GetTempPath(), "TestLoomTooltipReport_" + Guid.NewGuid().ToString("N"));
        try
        {
            SeedDemoDataService.Genera(cartella);
            using var state = TestStateFactory.Create();
            state.InizializzaCartella(cartella);
            var configurazione = new ConfigurazioneMatriceReport { ModalitaCella = ModalitaCellaMatrice.Stato };
            var bytes = ReportService.GeneraExcel(new ReportFilter(), state, "Operatore", configurazione);
            using var workbook = new XLWorkbook(new MemoryStream(bytes));
            var foglio = workbook.Worksheet("Matrice configurabile");

            var spiegazione = MatriceTooltipService.DescrizioneModalita(ModalitaCellaMatrice.Stato);
            Assert.Equal(spiegazione, foglio.Cell(3, 1).GetString());
            Assert.True(foglio.Cell(3, 1).HasComment);
            Assert.Contains("Stato =", foglio.Cell(3, 1).GetComment().Text);
        }
        finally
        {
            LocalizationService.Instance.Lingua = lingua;
            try { if (Directory.Exists(cartella)) Directory.Delete(cartella, recursive: true); } catch { }
        }
    }
}
