using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura del bug reale segnalato dall'utente 2026-09-16: "l'import da Excel mi ha funzionato una
/// sola volta e con file minori di 100 righe" — il file reale allegato (Funzionalita_Master_Deploy_UnitLogger.xlsx)
/// aveva firma OLE2/CFBF, cioè è in realtà un vecchio binario Excel 97-2003 nonostante l'estensione ".xlsx".
/// ClosedXML legge solo il formato Open XML moderno: falliva sempre su quel file, indipendentemente dal numero
/// di righe (nessun limite di 100 righe esiste da nessuna parte nel codice — il "sotto 100 funziona" era quasi
/// certamente una coincidenza su quali file erano vecchio/nuovo formato), e il fallimento arrivava SOLO al
/// crash.log (OnImportaExcel nelle View), mai a video. La fixture Fixtures/funzionalita_legacy.xls è un vero
/// .xls (non simulato) generato con xlwt, per verificare la lettura reale via ExcelDataReader, non solo la
/// rilevazione del formato.</summary>
public sealed class ExcelImportServiceLegacyXlsTests
{
    private static string PercorsoFixture(string nome) => Path.Combine(AppContext.BaseDirectory, "Fixtures", nome);

    [Fact]
    public void ElencaFogli_FileXlsLegacyConEstensioneXlsx_LeggeIFogliCorrettamente()
    {
        // Stesso scenario del file reale segnalato: contenuto .xls, nome/estensione .xlsx.
        var percorsoOriginale = PercorsoFixture("funzionalita_legacy.xls");
        var percorsoRinominato = Path.Combine(Path.GetTempPath(), "Funzionalita_Master_Deploy_UnitLogger_" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.Copy(percorsoOriginale, percorsoRinominato);
        try
        {
            using var fs = File.OpenRead(percorsoRinominato);
            var fogli = ExcelImportService.ElencaFogli(fs);
            Assert.Equal(new[] { "Sheet1", "Altro" }, fogli);
        }
        finally { File.Delete(percorsoRinominato); }
    }

    [Fact]
    public void LeggiRigheOrdinate_FileXlsLegacy_LeggeLeRigheComeUnVeroXlsx()
    {
        using var fs = File.OpenRead(PercorsoFixture("funzionalita_legacy.xls"));
        var righe = ExcelImportService.LeggiRigheOrdinate(fs, "Sheet1", 1);

        Assert.Single(righe);
        Assert.Equal("Diagnosi XLS", righe[0][0]);
        Assert.Equal("Elettrica, Motore", righe[0][1]);
    }

    private static AppState NuovoStatoMaster(string dir)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    [Fact]
    public void Funzionalita_ImportaFileXlsLegacyRinominatoXlsx_FunzionaEndToEndAttraversoIlViewModel()
    {
        // Riproduce esattamente il caso dell'utente: sceglie un file ".xlsx" che è in realtà un vecchio .xls,
        // nella sezione Funzionalità, e si aspetta che l'import funzioni come con un vero .xlsx.
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ImportXlsLegacy_" + Guid.NewGuid().ToString("N"));
        var state = NuovoStatoMaster(dir);
        var percorso = Path.Combine(Path.GetTempPath(), "Funzionalita_Master_Deploy_UnitLogger_" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.Copy(PercorsoFixture("funzionalita_legacy.xls"), percorso);
        try
        {
            var vm = new FunzionalitaViewModel(state);
            vm.IniziaModificaCommand.AttendiModifica();
            vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, "Sheet1", 1));

            Assert.False(vm.HaMessaggioErrore);
            var importata = vm.Items.FirstOrDefault(f => f.Nome == "Diagnosi XLS");
            Assert.NotNull(importata);
            Assert.Equal(new List<string> { "Elettrica", "Motore" }, importata!.Categorie);
        }
        finally
        {
            File.Delete(percorso);
            if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ElencaFogli_FileNonExcel_LanciaErroreComprensibileInveceDiFallireInSilenzio()
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("questo non è un file Excel, solo testo"));
        var ex = Assert.Throws<FormatoExcelNonValidoException>(() => ExcelImportService.ElencaFogli(ms));
        Assert.Contains("non è un .xlsx/.xls Excel leggibile", ex.Message);
    }

    [Fact]
    public void LeggiRigheOrdinate_FileConFirmaOle2MaContenutoCorrotto_LanciaErroreComprensibile()
    {
        // Firma OLE2/CFBF valida (le stesse che ha un vero .xls) ma corpo del file spazzatura: deve fallire
        // con un messaggio comprensibile, non con l'eccezione tecnica grezza di ExcelDataReader né in silenzio.
        var bytes = new byte[64];
        bytes[0] = 0xD0; bytes[1] = 0xCF; bytes[2] = 0x11; bytes[3] = 0xE0;
        bytes[4] = 0xA1; bytes[5] = 0xB1; bytes[6] = 0x1A; bytes[7] = 0xE1;
        using var ms = new MemoryStream(bytes);
        Assert.Throws<FormatoExcelNonValidoException>(() => ExcelImportService.ElencaFogli(ms));
    }
}
