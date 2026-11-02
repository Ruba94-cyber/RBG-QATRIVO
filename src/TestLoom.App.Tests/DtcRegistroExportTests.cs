using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class DtcRegistroExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_DtcRegistroExport_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task ExportExcel_IncludeFiltriEsitiProblemiENomiStrumentiDelTest()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var master = TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234")
        });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));

        var test = new TestCase
        {
            Id = "T-guasto-01", Uid = Guid.NewGuid(), DescrizioneBreve = "prova guasto",
            DataCreazione = DateTime.UtcNow, UltimaModifica = DateTime.UtcNow,
            ModificatoDa = master.Nome, Veicolo = "V1", Veicoli = ["V1"], Componente = new EcuSelection(new[] { "ECM" })
        };
        test.StrumentiUsati.AddRange(new[] { "Guasto Injection", "CaptureTool", "ValidationTool" });
        var operazione = new Condizione
        {
            Strumento = "Guasto Injection", Guasto = "P0100", DtcRequisitoId = "R-Extra", DtcVeicolo = "V1",
            DtcPasso = PassoDtc.Validazione, Esito = Esito.ConformeConDeviazioni,
            Componente = new EcuSelection(new[] { "ECM" }), Nota = "nota banco",
            DescrizioneDeviazione = "risposta lenta", DescrizioneProblema = "Guasto resta attivo"
        };
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = DateTime.UtcNow, Operatore = master.Nome,
            Esito = Esito.ConformeConDeviazioni, Condizioni = [operazione]
        });
        state.TestCases.Save(test, true);
        state.TestCases.Save(CreaTestDtc("T-guasto-02", "V2", master.Nome), isNew: true);

        using var vm = new DtcRegistroViewModel(state);
        Assert.True(SpinWait.SpinUntil(() => !vm.Caricamento, TimeSpan.FromSeconds(10)));
        vm.VeicoloSelezionato = vm.VeicoliFiltro.Single(v => v.Valore == "V1");
        Assert.True(SpinWait.SpinUntil(() => !vm.Caricamento && vm.Righe.All(r => r.Veicolo == "V1"), TimeSpan.FromSeconds(10)));
        Assert.Single(vm.Righe);
        var output = Path.Combine(_root, "registro.xlsx");
        var result = await vm.EsportaExcelVerso(output);

        Assert.True(result.Stato == StatoEsportazione.Completata, result.Errore?.ToString() ?? $"Esportazione: {result.Stato}");
        using var workbook = new XLWorkbook(output);
        var sheet = Assert.Single(workbook.Worksheets);
        Assert.Equal("P0100", sheet.Cell(2, 5).GetString());
        Assert.Equal(LocalizationService.Instance.Traduci("DtcRegistroValidazione"), sheet.Cell(2, 6).GetString());
        Assert.Equal("Guasto Injection", sheet.Cell(2, 12).GetString());
        Assert.Contains("CaptureTool", sheet.Cell(2, 13).GetString());
        Assert.Contains("ValidationTool", sheet.Cell(2, 13).GetString());
        Assert.Equal("nota banco", sheet.Cell(2, 14).GetString());
        Assert.Equal("risposta lenta", sheet.Cell(2, 15).GetString());
        Assert.Equal("Guasto resta attivo", sheet.Cell(2, 16).GetString());
        // Stato dell'associazione a un requisito Extra (la riga ne ha uno) e Test UID, per elaborazioni successive.
        Assert.Equal(LocalizationService.Instance.Traduci("DtcRegistroAssociato"), sheet.Cell(2, 4).GetString());
        Assert.True(Guid.TryParse(sheet.Cell(2, 18).GetString(), out _));
        Assert.Equal(2, sheet.LastRowUsed()!.RowNumber());
        var soloPivot = Path.Combine(_root, "solo-pivot.xlsx");
        var scelta = new ExportConfigurazione { Sezioni = ["pivot"], PivotRighe = "DtcRegistroColVeicolo", PivotColonne = "DtcRegistroColEcu" };
        var pivotResult = await vm.EsportaExcelVerso(soloPivot, scelta);
        Assert.True(pivotResult.Stato == StatoEsportazione.Completata, pivotResult.Errore?.ToString());
        using var pivotWorkbook = new XLWorkbook(soloPivot);
        Assert.DoesNotContain(pivotWorkbook.Worksheets, w => w.Name == LocalizationService.Instance.Traduci("DtcRegistroFoglioExcel"));
        Assert.Single(pivotWorkbook.Worksheets.Where(w => w.PivotTables.Any()));
    }

    [Fact]
    public void Aggiorna_RicaricaVeicoliAncheQuandoLaGenerazioneIndiceNonCambia()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var master = TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234")
        });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));

        state.TestCases.Save(CreaTestDtc("T-guasto-V1", "V1", master.Nome), isNew: true);
        using var vm = new DtcRegistroViewModel(state);
        Assert.True(SpinWait.SpinUntil(() => !vm.Caricamento, TimeSpan.FromSeconds(10)));
        Assert.Contains(vm.VeicoliFiltro, v => v.Valore == "V1");

        var generazionePrima = state.TestCases.LocalIndexGeneration;
        state.TestCases.Save(CreaTestDtc("T-guasto-V2", "V2", master.Nome), isNew: true);
        Assert.Equal(generazionePrima, state.TestCases.LocalIndexGeneration);

        vm.Aggiorna();

        Assert.True(SpinWait.SpinUntil(
            () => !vm.Caricamento && vm.VeicoliFiltro.Any(v => v.Valore == "V2"),
            TimeSpan.FromSeconds(10)));
        Assert.Contains(vm.VeicoliFiltro, v => v.Valore == "V1");
    }

    [Fact]
    public void Aggiorna_SeVeicoloSelezionatoScompare_RicaricaPaginaSenzaFiltroVeicolo()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var master = TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234")
        });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));

        state.TestCases.Save(CreaTestDtc("T-guasto-V1", "V1", master.Nome), isNew: true);
        state.TestCases.Save(CreaTestDtc("T-guasto-V2", "V2", master.Nome), isNew: true);
        using var vm = new DtcRegistroViewModel(state);
        Assert.True(SpinWait.SpinUntil(() => !vm.Caricamento, TimeSpan.FromSeconds(10)));

        vm.VeicoloSelezionato = vm.VeicoliFiltro.Single(v => v.Valore == "V1");
        Assert.True(SpinWait.SpinUntil(
            () => !vm.Caricamento && vm.TotaleRisultati == 1 && vm.Righe.Any(r => r.Veicolo == "V1"),
            TimeSpan.FromSeconds(10)));

        var testDaRimuovere = state.TestCases.GetById("T-guasto-V1")!;
        state.TestCases.SoftDelete(testDaRimuovere, master.Nome);
        vm.Aggiorna();

        Assert.True(SpinWait.SpinUntil(
            () => !vm.Caricamento && vm.VeicoloSelezionato?.Valore == null && vm.Righe.Any(r => r.Veicolo == "V2"),
            TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(vm.VeicoliFiltro, v => v.Valore == "V1");
        Assert.Equal(1, vm.TotaleRisultati);
        Assert.Equal("V2", Assert.Single(vm.Righe).Veicolo);
    }

    private static TestCase CreaTestDtc(string id, string veicolo, string operatore)
    {
        var test = new TestCase
        {
            Id = id,
            Uid = Guid.NewGuid(),
            DataCreazione = DateTime.UtcNow,
            UltimaModifica = DateTime.UtcNow,
            ModificatoDa = operatore,
            Veicolo = veicolo,
            Veicoli = [veicolo],
            Componente = new EcuSelection(new[] { "ECM" })
        };
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.UtcNow,
            Operatore = operatore,
            Esito = Esito.Conforme,
            Condizioni =
            [
                new Condizione
                {
                    Strumento = "Guasto Injection",
                    Guasto = "P0100",
                    DtcVeicolo = veicolo,
                    DtcPasso = PassoDtc.Validazione,
                    Esito = Esito.Conforme,
                    Componente = new EcuSelection(new[] { "ECM" })
                }
            ]
        });
        return test;
    }
}
