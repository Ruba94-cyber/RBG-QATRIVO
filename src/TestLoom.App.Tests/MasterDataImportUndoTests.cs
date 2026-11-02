using System.Text.Json;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>6.6.0 (import ROLLBACK e retry idempotente) sugli editor master: l'import Excel resta nell'editor fino a Salva, e
/// "Annulla ultimo import" riporta esattamente lo stato precedente, senza toccare le altre modifiche. Un guasto a meta' import
/// non lascia righe parziali; il retry completa senza duplicati. Il report finale resta visibile e si copia negli appunti.</summary>
/// <remarks>[Collection("Localization")]: mutano ConfirmService.Handler e ClipboardService.Handler, statici e condivisi dal processo.</remarks>
[Collection("Localization")]
public sealed class MasterDataImportUndoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterUndo_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;
    private readonly Func<string, Task<bool>>? _clipboardOriginale = ClipboardService.Handler;
    private readonly List<AppState> _stati = new();

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        ClipboardService.Handler = _clipboardOriginale;
        foreach (var s in _stati) { try { s.Dispose(); } catch { } }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    /// <summary>Risponde "Importa" a ogni conferma dell'anteprima (come l'utente che conferma).</summary>
    private static void ConfermaSempre() => ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

    private AppState CreaStatoMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        _stati.Add(state);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Undo", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore("Master Undo", "1234"));
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily 24" }, new() { Nome = "eDaily 24" } });
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = new List<string> { "Daily 24" } },
            new() { Nome = "ABS", VeicoliAssociati = new List<string>() },
        });
        return state;
    }

    /// <summary>File Excel con intestazioni Nome | Veicoli associati e le righe indicate (prima riga dati = riga 2).</summary>
    private string CreaFileExcel(params (string Nome, string Veicoli)[] righe)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "Nome";
        ws.Cell(1, 2).Value = "Veicoli";
        for (var r = 0; r < righe.Length; r++)
        {
            ws.Cell(r + 2, 1).Value = righe[r].Nome;
            ws.Cell(r + 2, 2).Value = righe[r].Veicoli;
        }
        var percorso = Path.Combine(_dir, "ecu_" + Guid.NewGuid().ToString("N") + ".xlsx");
        wb.SaveAs(percorso);
        return percorso;
    }

    /// <summary>Fotografia completa delle righe in editor (JSON): due fotografie uguali = stesso stato.</summary>
    private static string Stato(EcuViewModel vm) => JsonSerializer.Serialize(vm.Items.ToList(), JsonIo.Options);

    private static ImportExcelRichiesta Richiesta(string percorso) => new(percorso, null, 1);

    /// <summary>Componente con un guasto iniettabile durante l'applicazione dell'import (la fase di anteprima non lo subisce).</summary>
    private sealed class EcuConGuasto : EcuViewModel
    {
        public EcuConGuasto(AppState state) : base(state) { }
        public bool Guasto { get; set; }
        protected override bool ProvaUnisciRigaImport(Componente destinazione, Componente sorgente)
        {
            if (Guasto) throw new IOException("Guasto simulato durante l'applicazione dell'import.");
            return base.ProvaUnisciRigaImport(destinazione, sorgente);
        }
    }

    [Fact]
    public void Undo_RipristinaEsattamenteLoStatoPrecedenteALImport()
    {
        var state = CreaStatoMaster();
        var percorso = CreaFileExcel(("ECM Nuova", "Daily 24"), ("BCM", "eDaily 24"));
        ConfermaSempre();
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var prima = Stato(vm);

        vm.ImportaDaExcelCommand.Execute(Richiesta(percorso));
        Assert.False(vm.HaMessaggioErrore);
        Assert.NotEqual(prima, Stato(vm));
        Assert.True(vm.PuoAnnullareUltimoImport);

        vm.AnnullaUltimoImportCommand.Execute(null);

        Assert.Equal(prima, Stato(vm));
        Assert.False(vm.PuoAnnullareUltimoImport);
        Assert.False(vm.HaMessaggioErrore);
    }

    [Fact]
    public void Undo_NonAnnullaSeUnaRigaImportataECambiataDopo_E_NonPerdeNulla()
    {
        var state = CreaStatoMaster();
        var percorso = CreaFileExcel(("ECM Nuova", "Daily 24"));
        ConfermaSempre();
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(Richiesta(percorso));
        var riga = vm.Items.Single(e => e.Nome == "ECM Nuova");
        riga.VeicoliAssociati = new List<string> { "eDaily 24" }; // modifica dell'utente dopo l'import
        var dopoModifica = Stato(vm);

        vm.AnnullaUltimoImportCommand.Execute(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.Equal(dopoModifica, Stato(vm));       // nulla rimosso, nulla ripristinato
        Assert.False(vm.PuoAnnullareUltimoImport);   // l'annullamento non e' piu' offerto per questo import
    }

    [Fact]
    public void Undo_NonToccaLeAltreModificheFatteDopoLImport()
    {
        var state = CreaStatoMaster();
        var percorso = CreaFileExcel(("ECM Nuova", "Daily 24"), ("BCM", "eDaily 24"));
        ConfermaSempre();
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var prima = Stato(vm);
        vm.ImportaDaExcelCommand.Execute(Richiesta(percorso));

        // Modifica dell'utente su una riga NON toccata dall'import: deve sopravvivere all'annullamento.
        vm.Items.Single(e => e.Nome == "ABS").VeicoliAssociati = new List<string> { "Daily 24" };
        vm.AnnullaUltimoImportCommand.Execute(null);

        Assert.DoesNotContain(vm.Items, e => e.Nome == "ECM Nuova");
        Assert.Equal(new List<string> { "Daily 24" }, vm.Items.Single(e => e.Nome == "ABS").VeicoliAssociati);
        Assert.Equal(
            JsonSerializer.Serialize(JsonSerializer.Deserialize<List<Componente>>(prima, JsonIo.Options)!.Single(e => e.Nome == "BCM"), JsonIo.Options),
            JsonSerializer.Serialize(vm.Items.Single(e => e.Nome == "BCM"), JsonIo.Options));
    }

    [Fact]
    public void Import_StessoFileDueVolte_NonDuplicaLeRighe()
    {
        var state = CreaStatoMaster();
        var percorso = CreaFileExcel(("ECM Nuova", "Daily 24"), ("BCM", "eDaily 24"));
        ConfermaSempre();
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(Richiesta(percorso));
        var dopoPrimo = Stato(vm);

        vm.ImportaDaExcelCommand.Execute(Richiesta(percorso));

        Assert.Equal(dopoPrimo, Stato(vm));
        Assert.Equal(1, vm.Items.Count(e => e.Nome == "ECM Nuova"));
        Assert.Contains("aggiunte 0", vm.ReportImport);
    }

    [Fact]
    public void Import_GuastoAMetaApplicazione_NonLasciaRigheParzialiERiprovaSenzaDuplicati()
    {
        var state = CreaStatoMaster();
        // La prima riga e' nuova (verrebbe aggiunta), la seconda unisce a BCM: il guasto arriva dopo la prima.
        var percorso = CreaFileExcel(("ECM Nuova", "Daily 24"), ("BCM", "eDaily 24"));
        var vm = new EcuConGuasto(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var prima = Stato(vm);
        // L'anteprima viene eseguita prima della conferma: il guasto si attiva solo dopo, durante l'applicazione.
        ConfirmService.Handler = (_, _, _, _) => { vm.Guasto = true; return Task.FromResult(true); };

        vm.ImportaDaExcelCommand.Execute(Richiesta(percorso));

        Assert.True(vm.HaMessaggioErrore);
        Assert.Equal(prima, Stato(vm));                   // tutto-o-niente: nessuna riga aggiunta, BCM invariato
        Assert.True(vm.PuoRiprovareImport);
        Assert.Contains("non applicato", vm.ReportImport);

        ConfermaSempre(); // il retry non deve riattivare il guasto
        vm.Guasto = false;
        vm.RiprovaImportCommand.Execute(null);

        Assert.False(vm.HaMessaggioErrore);
        Assert.Equal(1, vm.Items.Count(e => e.Nome == "ECM Nuova"));
        Assert.Contains("eDaily 24", vm.Items.Single(e => e.Nome == "BCM").VeicoliAssociati);
        Assert.False(vm.PuoRiprovareImport);
    }

    [Fact]
    public void Report_RiportaIConteggiEPuoEssereCopiatoNegliAppunti()
    {
        var state = CreaStatoMaster();
        var percorso = CreaFileExcel(("ECM Nuova", "Daily 24"), ("BCM", "eDaily 24"));
        ConfermaSempre();
        string? copiato = null;
        ClipboardService.Handler = testo => { copiato = testo; return Task.FromResult(true); };
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();

        vm.ImportaDaExcelCommand.Execute(Richiesta(percorso));

        Assert.True(vm.HaReportImport);
        Assert.Contains("righe lette 2", vm.ReportImport);
        Assert.Contains("aggiunte 1", vm.ReportImport);
        Assert.Contains("unite 1", vm.ReportImport);
        Assert.Contains("errori 0", vm.ReportImport);

        vm.CopiaReportImportCommand.Execute(null);
        Assert.Equal(vm.ReportImport, copiato);
    }

    [Fact]
    public void Report_CopiaNonRiuscita_MostraUnErroreInvecheLaConferma()
    {
        var state = CreaStatoMaster();
        var percorso = CreaFileExcel(("ECM Nuova", "Daily 24"));
        ConfermaSempre();
        ClipboardService.Handler = _ => Task.FromResult(false);
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(Richiesta(percorso));

        vm.CopiaReportImportCommand.Execute(null);

        Assert.Equal(LocalizationService.Instance.Traduci("ImportReportCopiaFallita"), vm.MessaggioErrore);
        Assert.NotEqual(LocalizationService.Instance.Traduci("ImportReportCopiato"), vm.Messaggio);
    }

    [Fact]
    public async Task Undo_NonDisponibileDopoSalva()
    {
        var state = CreaStatoMaster();
        var percorso = CreaFileExcel(("ECM Nuova", "Daily 24"));
        ConfermaSempre();
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(Richiesta(percorso));
        Assert.True(vm.PuoAnnullareUltimoImport);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.HaMessaggioErrore);
        Assert.False(vm.PuoAnnullareUltimoImport);
        Assert.False(vm.HaReportImport);
    }
}
