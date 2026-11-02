using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Export Excel/PDF/CSV/ZIP su thread di background: con annullamento non resta alcun file, la destinazione
/// gia presente resta intatta e il ViewModel torna disponibile. Il completamento scrive il file finale.</summary>
[Collection("Localization")]
public sealed class EsportazioneAnnullamentoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_EsportAnnull_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStatoConDatiDemo()
    {
        SeedDemoDataService.Genera(_dir);
        Directory.CreateDirectory(_dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(_dir);
        return state;
    }

    private void NessunTemporaneoRimasto() => Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

    private static string Messaggio(string chiave) => LocalizationService.Instance.Traduci(chiave);

    // ---------------------------------------------------------------- generatori: token gia annullato

    [Fact]
    public void Generatori_ConTokenGiaAnnullato_LanciaSenzaProdurreRisultato()
    {
        using var state = CreaStatoConDatiDemo();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => ReportService.GeneraExcel(new ReportFilter(), state, "Tester", null, cts.Token));
        Assert.Throws<OperationCanceledException>(() => PdfReportService.GeneraPdf(new ReportFilter(), state, "Tester", null, cts.Token));
        Assert.Throws<OperationCanceledException>(() => AuditReportService.GeneraExcel(state, cts.Token));
        Assert.Throws<OperationCanceledException>(() => NonConformitaExportService.GeneraExcel(Array.Empty<NonConformita>(), state, cts.Token));
        Assert.Throws<OperationCanceledException>(() => DiagnosticExportService.GeneraZip(state, cts.Token));
    }

    // ---------------------------------------------------------------- ViewModel: annullamento durante l'export

    [Fact]
    public async Task ReportExcel_Annullato_NonCreaIlFileENonToccaQuelloEsistente()
    {
        using var state = CreaStatoConDatiDemo();
        var destinazione = Path.Combine(_dir, "report_annullato.xlsx");
        File.WriteAllText(destinazione, "PRECEDENTE");
        var vm = new ReportViewModel(state);

        var attesa = vm.EsportaExcelVerso(destinazione);
        // La preparazione dei dati (su thread di lavoro) precede la generazione: si attende che l'export sia davvero in corso.
        for (var i = 0; i < 600 && !vm.GenerazioneInCorso; i++) await Task.Delay(50);
        Assert.True(vm.GenerazioneInCorso);
        vm.AnnullaEsportazioneCommand.Execute(null);
        await attesa;

        Assert.False(vm.GenerazioneInCorso);
        Assert.Equal("PRECEDENTE", File.ReadAllText(destinazione));
        NessunTemporaneoRimasto();
        Assert.Equal(Messaggio("EsportazioneAnnullata"), vm.Messaggio);
    }

    [Fact]
    public async Task ReportPdf_Annullato_NonCreaIlFile()
    {
        using var state = CreaStatoConDatiDemo();
        var destinazione = Path.Combine(_dir, "report_annullato.pdf");
        var vm = new ReportViewModel(state);

        var attesa = vm.EsportaPdfVerso(destinazione);
        Assert.True(vm.GenerazioneInCorso);
        vm.AnnullaEsportazioneCommand.Execute(null);
        await attesa;

        Assert.False(File.Exists(destinazione));
        NessunTemporaneoRimasto();
        Assert.Equal(Messaggio("EsportazioneAnnullata"), vm.Messaggio);
    }

    [Fact]
    public async Task AuditExcel_Annullato_NonLasciaFileParziale()
    {
        using var state = CreaStatoConDatiDemo();
        var destinazione = Path.Combine(_dir, "audit_annullato.xlsx");
        var vm = new AuditViewModel(state);

        var attesa = vm.EsportaExcelVerso(destinazione);
        Assert.True(vm.EsportazioneInCorso);
        vm.AnnullaEsportazioneCommand.Execute(null);
        await attesa;

        Assert.False(vm.EsportazioneInCorso);
        Assert.False(File.Exists(destinazione));
        NessunTemporaneoRimasto();
        Assert.Equal(Messaggio("EsportazioneAnnullata"), vm.Messaggio);
    }

    [Fact]
    public async Task AuditDettaglioDtcCsv_Annullato_NonLasciaFileParziale()
    {
        using var state = CreaStatoConDatiDemo();
        var destinazione = Path.Combine(_dir, "dtc_annullato.csv");
        var vm = new AuditViewModel(state);

        // L'export del dettaglio guasto richiede un requisito aperto nel drill-down.
        await vm.CaricaDettaglioDtcAsync("R-guasto-ANNULLAMENTO");
        var attesa = vm.EsportaDettaglioDtcCsv(destinazione);
        Assert.True(vm.EsportazioneInCorso);
        vm.AnnullaEsportazioneCommand.Execute(null);
        await attesa;

        Assert.False(File.Exists(destinazione));
        NessunTemporaneoRimasto();
        Assert.False(vm.EsportazioneInCorso);
    }

    [Fact]
    public async Task NonConformitaExcel_Annullato_NonCreaIlFile()
    {
        using var state = CreaStatoConDatiDemo();
        var destinazione = Path.Combine(_dir, "nc_annullato.xlsx");
        var vm = new NonConformitaViewModel(state, _ => { });

        var attesa = vm.EsportaExcelVerso(destinazione);
        Assert.True(vm.EsportazioneInCorso);
        vm.AnnullaEsportazioneCommand.Execute(null);
        await attesa;

        Assert.False(vm.EsportazioneInCorso);
        Assert.False(File.Exists(destinazione));
        NessunTemporaneoRimasto();
        Assert.Equal(Messaggio("EsportazioneAnnullata"), vm.Messaggio);
    }

    // ---------------------------------------------------------------- completamento: file finale, nessun temporaneo

    [Fact]
    public async Task ReportExcel_Completato_ScriveIlFileFinaleSenzaTemporanei()
    {
        using var state = CreaStatoConDatiDemo();
        var destinazione = Path.Combine(_dir, "report_ok.xlsx");
        var vm = new ReportViewModel(state);

        await vm.EsportaExcelVerso(destinazione);

        Assert.True(File.Exists(destinazione));
        // Un .xlsx e un contenitore ZIP: inizia con "PK".
        Assert.Equal(new byte[] { 0x50, 0x4B }, File.ReadAllBytes(destinazione).Take(2).ToArray());
        NessunTemporaneoRimasto();
        Assert.False(vm.GenerazioneInCorso);
        Assert.Null(vm.MessaggioErrore);
    }
}
