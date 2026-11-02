using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al punto 8 (feedback utente 2026-08-20): "prendere la tabella esattamente come
/// è ora" — colonna A dell'Excel = prima colonna a schermo, B = seconda, ecc. L'esempio esplicito dell'utente
/// era Veicoli (colonna Nome, colonna Gamma): prima l'intestazione doveva corrispondere esattamente al nome
/// della proprietà C# interna "GammaNome" invece di "Gamma" come mostrato a schermo, e la colonna veniva
/// ignorata in silenzio se non corrispondeva — causa più probabile del "non funziona" segnalato.</summary>
public sealed class ImportExcelPosizionaleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ImportPosizionale_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateMasterState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    private static string CreaFileTemp(string dir, string intestazioneA, string intestazioneB, params (string a, string b)[] righe)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = intestazioneA;
        ws.Cell(1, 2).Value = intestazioneB;
        for (var r = 0; r < righe.Length; r++)
        {
            ws.Cell(r + 2, 1).Value = righe[r].a;
            ws.Cell(r + 2, 2).Value = righe[r].b;
        }
        var percorso = Path.Combine(dir, "import_" + Guid.NewGuid().ToString("N") + ".xlsx");
        wb.SaveAs(percorso);
        return percorso;
    }

    [Fact]
    public void Veicoli_ImportaConIntestazioneEtichettaASchermo_FunzionaOraCheEPosizionale()
    {
        var state = CreateMasterState();
        // Esattamente lo scenario segnalato dall'utente: intestazioni "Nome"/"Gamma" (come a schermo), non
        // "Nome"/"GammaNome" (nome interno che l'import per-intestazione avrebbe richiesto prima).
        var percorso = CreaFileTemp(_dir, "Nome", "Gamma", ("Daily 27 Import", "Gamma Media"));

        var vm = new VeicoliViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        Assert.False(vm.HaMessaggioErrore);
        var importato = vm.Items.FirstOrDefault(v => v.Nome == "Daily 27 Import");
        Assert.NotNull(importato);
        Assert.Equal("Gamma Media", importato!.GammaNome);
    }

    [Fact]
    public void Veicoli_ImportaConIntestazioneQualunque_FunzionaComunque_SoloLaPosizioneConta()
    {
        var state = CreateMasterState();
        var percorso = CreaFileTemp(_dir, "Colonna1", "Colonna2", ("Eurocargo Import", "Gamma Pesante"));

        var vm = new VeicoliViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        var importato = vm.Items.FirstOrDefault(v => v.Nome == "Eurocargo Import");
        Assert.NotNull(importato);
        Assert.Equal("Gamma Pesante", importato!.GammaNome);
    }

    [Fact]
    public void Componente_ImportaNomeEVeicoliAssociati_ColonnaBSplittataPerVirgola()
    {
        var state = CreateMasterState();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily 24" }, new() { Nome = "eDaily 24" } });
        var percorso = CreaFileTemp(_dir, "Nome", "Veicoli", ("BCM Import", "Daily 24, eDaily 24"));

        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        var importata = vm.Items.FirstOrDefault(e => e.Nome == "BCM Import");
        Assert.NotNull(importata);
        Assert.Equal(new[] { "Daily 24", "eDaily 24" }, importata!.VeicoliAssociati);
    }

    [Fact]
    public void Componente_RigheRipetuteESeparatorePuntoEVirgola_UnisconoTuttiIVeicoli()
    {
        var state = CreateMasterState();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily 24" }, new() { Nome = "eDaily 24" }, new() { Nome = "Alpha" } });
        var percorso = CreaFileTemp(_dir, "Nome", "Veicoli", ("BCM Import", "Daily 24; eDaily 24"), ("BCM Import", "Alpha"));
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));
        Assert.Equal(new[] { "Daily 24", "eDaily 24", "Alpha" }, vm.Items.Single(e => e.Nome == "BCM Import").VeicoliAssociati);
    }

    [Fact]
    public void Componente_RiferimentoVeicoloSconosciuto_BloccaInteroImport()
    {
        var state = CreateMasterState();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily 24" } });
        var percorso = CreaFileTemp(_dir, "Nome", "Veicoli", ("BCM OK", "Daily 24"), ("BCM KO", "Veicolo inesistente"));
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));
        Assert.DoesNotContain(vm.Items, e => e.Nome is "BCM OK" or "BCM KO");
        Assert.Contains("Veicolo inesistente", vm.MessaggioErrore);
    }

    [Fact]
    public void Funzionalita_ImportaNomeECategoria_OrdinePosizionale()
    {
        var state = CreateMasterState();
        var percorso = CreaFileTemp(_dir, "Nome", "Categoria", ("Diagnosi Import", "Elettrica"));

        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        var importata = vm.Items.FirstOrDefault(f => f.Nome == "Diagnosi Import");
        Assert.NotNull(importata);
        Assert.Equal(new List<string> { "Elettrica" }, importata!.Categorie);
    }

    [Fact]
    public void Funzionalita_ImportaPiuCategorieSeparateDaVirgola()
    {
        var state = CreateMasterState();
        var percorso = CreaFileTemp(_dir, "Nome", "Categoria", ("Diagnosi Import", "Elettrica, Motore"));

        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        var importata = vm.Items.FirstOrDefault(f => f.Nome == "Diagnosi Import");
        Assert.NotNull(importata);
        Assert.Equal(new List<string> { "Elettrica", "Motore" }, importata!.Categorie);
    }

    [Fact]
    public void Requisiti_ImportaQuattroColonnePosizionali()
    {
        var state = CreateMasterState();
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "Id"; ws.Cell(1, 2).Value = "Testo"; ws.Cell(1, 3).Value = "Banco"; ws.Cell(1, 4).Value = "Veicolo";
        ws.Cell(2, 1).Value = "REQ-IMP-01"; ws.Cell(2, 2).Value = "Requisito importato"; ws.Cell(2, 3).Value = "x"; ws.Cell(2, 4).Value = "";
        var percorso = Path.Combine(_dir, "req_import.xlsx");
        wb.SaveAs(percorso);

        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        var importato = vm.Items.FirstOrDefault(r => r.Id == "REQ-IMP-01");
        Assert.NotNull(importato);
        Assert.Equal("Requisito importato", importato!.Testo);
        Assert.True(importato.ApplicabileBanco);
        Assert.False(importato.ApplicabileVeicolo);
    }

    // Regressione end-to-end punto 5 (feedback utente 2026-08-25), stessa struttura del file reale che ha
    // motivato il fix: banner/titolo unito su più colonne sulla riga 1, intestazione vera sulla riga 2, workbook
    // con due fogli (bilingue). Passa dal ViewModel reale con la scelta esplicita di foglio e riga di
    // intestazione (ImportExcelOpzioniWindow), non dal servizio di lettura direttamente.
    [Fact]
    public void Requisiti_ImportaDaFileConBannerUnitoEDueFogli_TramiteViewModelConFoglioERigaScelti()
    {
        var state = CreateMasterState();
        using var wb = new XLWorkbook();
        var wsEn = wb.Worksheets.Add("English");
        wsEn.Range(1, 1, 1, 4).Merge();
        wsEn.Cell(1, 1).Value = "Acme Requirements";
        wsEn.Cell(2, 1).Value = "ID"; wsEn.Cell(2, 2).Value = "Requirement";
        wsEn.Cell(3, 1).Value = "ST01"; wsEn.Cell(3, 2).Value = "The componente shall be programmable via an EOBD connector.";
        wsEn.Cell(4, 1).Value = "ST02"; wsEn.Cell(4, 2).Value = "Every CAN control unit shall comply with UDS on CAN.";
        var wsIt = wb.Worksheets.Add("Italiano");
        wsIt.Cell(1, 1).Value = "ID"; wsIt.Cell(1, 2).Value = "Requisito";
        wsIt.Cell(2, 1).Value = "ST01"; wsIt.Cell(2, 2).Value = "La centralina deve essere programmabile via connettore EOBD.";
        var percorso = Path.Combine(_dir, "req_iveco_bilingue.xlsx");
        wb.SaveAs(percorso);

        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, "English", 2));

        Assert.Null(vm.MessaggioErrore);
        var st01 = vm.Items.FirstOrDefault(r => r.Id == "ST01");
        var st02 = vm.Items.FirstOrDefault(r => r.Id == "ST02");
        Assert.NotNull(st01);
        Assert.NotNull(st02);
        Assert.Equal("The componente shall be programmable via an EOBD connector.", st01!.Testo);
        Assert.Equal("Every CAN control unit shall comply with UDS on CAN.", st02!.Testo);
        // Nessuna riga fantasma dall'intestazione vera (riga 2) o dal banner (riga 1) presa per errore come dato.
        Assert.DoesNotContain(vm.Items, r => r.Id is "ID" or "Acme  Requirements" or "Acme Requirements");
    }
}
