using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Le tabelle del PDF troncate dichiarano quante righe mostrano (requisiti 60, anomalie 40) con un testo localizzato.
/// Il PDF non si legge come testo in questa suite: si controlla la nota che la generazione mette sotto la tabella.</summary>
public class PdfReportTroncaturaTests
{
    [Fact]
    public void NotaTroncatura_TabellaCompleta_NonAggiungeNessunaNota()
    {
        Assert.Null(PdfReportService.NotaTroncatura("PdfMostratiRequisitiFmt", totale: 60, massimo: PdfReportService.MassimoRequisitiPdf));
        Assert.Null(PdfReportService.NotaTroncatura("PdfMostrateAnomalieFmt", totale: 3, massimo: PdfReportService.MassimoAnomaliePdf));
    }

    [Fact]
    public void NotaTroncatura_RequisitiOltreIlLimite_DichiaraMostratiDiTotale()
    {
        var nota = PdfReportService.NotaTroncatura("PdfMostratiRequisitiFmt", totale: 61, massimo: PdfReportService.MassimoRequisitiPdf);

        Assert.Equal(string.Format(LocalizationService.Instance.Traduci("PdfMostratiRequisitiFmt"), 60, 61), nota);
    }

    [Fact]
    public void NotaTroncatura_AnomalieOltreIlLimite_DichiaraMostrateDiTotale()
    {
        var nota = PdfReportService.NotaTroncatura("PdfMostrateAnomalieFmt", totale: 45, massimo: PdfReportService.MassimoAnomaliePdf);

        Assert.Equal(string.Format(LocalizationService.Instance.Traduci("PdfMostrateAnomalieFmt"), 40, 45), nota);
    }
}
