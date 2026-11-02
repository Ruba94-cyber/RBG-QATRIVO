using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copre il feedback utente 2026-09-23: "le funzionalità che oggi appartengono o meno a una categoria
/// devono poter essere assegnate a una o più componente, in modo da non dover scorrere troppo all'interno del test",
/// precisato poi in modo vincolante come scelta esplicita e mutuamente esclusiva tra un elenco di componente
/// specifiche e "Tutti i componenti" (Funzionalita.TutteLeEcu/EcuAssociate). Copre modello, import/export Excel del
/// Master Funzionalità e il filtro conseguente nell'editor Test e nell'editor Segnalazioni rete.</summary>
[Collection("Localization")]
public sealed class FunzionalitaEcuAssociateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_FunzionalitaEcuAssociate_" + Guid.NewGuid().ToString("N"));

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

    // ---------- Modello ----------

    [Fact]
    public void Funzionalita_Nuova_TutteLeEcuVeraEcuAssociateVuoto()
    {
        var f = new Funzionalita { Nome = "Diagnosi accensione" };

        Assert.True(f.TutteLeEcu);
        Assert.Empty(f.EcuAssociate);
        Assert.Equal("*", f.EcuAssociateTesto);
        Assert.Equal("Tutti i componenti", f.EcuAssociateRiepilogo);
    }

    [Fact]
    public void EcuAssociateTesto_SetConNomiEcu_DisattivaTutteLeEcu()
    {
        var f = new Funzionalita { Nome = "Diagnosi accensione" };

        f.EcuAssociateTesto = "BCM, SGW";

        Assert.False(f.TutteLeEcu);
        Assert.Equal(new[] { "BCM", "SGW" }, f.EcuAssociate);
        Assert.Equal("BCM, SGW", f.EcuAssociateRiepilogo);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("tutte")]
    [InlineData("ALL")]
    [InlineData("")]
    [InlineData("   ")]
    public void EcuAssociateTesto_SetConTokenOVuoto_TornaATutteLeEcu(string token)
    {
        var f = new Funzionalita { Nome = "X", EcuAssociate = new List<string> { "BCM" }, TutteLeEcu = false };

        f.EcuAssociateTesto = token;

        Assert.True(f.TutteLeEcu);
        Assert.Empty(f.EcuAssociate);
    }

    // ---------- Master Funzionalità: editor e import/export Excel ----------

    [Fact]
    public void FunzionalitaViewModel_EcuDisponibili_SoloEcuAttive()
    {
        var state = CreateMasterState();
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM", ModificatoDa = "Seed" },
            new() { Nome = "SGW-Archiviata", Archiviata = true, ModificatoDa = "Seed" },
        });

        var vm = new FunzionalitaViewModel(state);

        Assert.Contains("BCM", vm.EcuDisponibili);
        Assert.DoesNotContain("SGW-Archiviata", vm.EcuDisponibili);
    }

    private static string CreaFileImportFunzionalita(string dir, params (string nome, string categorie, string componente)[] righe)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "Nome";
        ws.Cell(1, 2).Value = "Categorie";
        ws.Cell(1, 3).Value = "Componente";
        for (var r = 0; r < righe.Length; r++)
        {
            ws.Cell(r + 2, 1).Value = righe[r].nome;
            ws.Cell(r + 2, 2).Value = righe[r].categorie;
            ws.Cell(r + 2, 3).Value = righe[r].componente;
        }
        var percorso = Path.Combine(dir, "import_" + Guid.NewGuid().ToString("N") + ".xlsx");
        wb.SaveAs(percorso);
        return percorso;
    }

    [Fact]
    public void ImportExcel_ColonnaEcuConTokenTutte_ImportaComeTutteLeEcu()
    {
        var state = CreateMasterState();
        var percorso = CreaFileImportFunzionalita(_dir, ("Diagnosi accensione", "", "*"));

        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        Assert.False(vm.HaMessaggioErrore);
        var importata = vm.Items.Single(f => f.Nome == "Diagnosi accensione");
        Assert.True(importata.TutteLeEcu);
        Assert.Empty(importata.EcuAssociate);
    }

    [Fact]
    public void ImportExcel_ColonnaEcuConNomiValidi_ImportaAssociazioneSpecifica()
    {
        var state = CreateMasterState();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", ModificatoDa = "Seed" }, new() { Nome = "SGW", ModificatoDa = "Seed" } });
        var percorso = CreaFileImportFunzionalita(_dir, ("Diagnosi accensione", "", "BCM, SGW"));

        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        Assert.False(vm.HaMessaggioErrore);
        var importata = vm.Items.Single(f => f.Nome == "Diagnosi accensione");
        Assert.False(importata.TutteLeEcu);
        Assert.Equal(new[] { "BCM", "SGW" }, importata.EcuAssociate);
    }

    [Fact]
    public void ImportExcel_ColonnaEcuConNomeSconosciuto_BloccaImportAtomico()
    {
        var state = CreateMasterState();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", ModificatoDa = "Seed" } });
        var percorso = CreaFileImportFunzionalita(_dir, ("Diagnosi accensione", "", "Componente-INESISTENTE"));

        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        Assert.True(vm.HaMessaggioErrore);
        Assert.DoesNotContain(vm.Items, f => f.Nome == "Diagnosi accensione");
    }

    [Fact]
    public void ImportExcel_CellaEcuVuota_RestaTutteLeEcuDiDefault()
    {
        var state = CreateMasterState();
        var percorso = CreaFileImportFunzionalita(_dir, ("Diagnosi accensione", "", ""));

        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        Assert.False(vm.HaMessaggioErrore);
        var importata = vm.Items.Single(f => f.Nome == "Diagnosi accensione");
        Assert.True(importata.TutteLeEcu);
    }

    [Fact]
    public void ExportEcuAssociateTesto_RoundTripAttraversoImport()
    {
        var state = CreateMasterState();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", ModificatoDa = "Seed" } });
        var originale = new Funzionalita { Nome = "Diagnosi accensione" };
        originale.EcuAssociateTesto = "BCM";
        var testoEsportato = originale.EcuAssociateTesto;

        var percorso = CreaFileImportFunzionalita(_dir, ("Diagnosi accensione reimportata", "", testoEsportato));
        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        var reimportata = vm.Items.Single(f => f.Nome == "Diagnosi accensione reimportata");
        Assert.Equal(originale.TutteLeEcu, reimportata.TutteLeEcu);
        Assert.Equal(originale.EcuAssociate, reimportata.EcuAssociate);
    }

    /// <summary>Simmetrico a VeicoliViewModel.HaRiferimenti per Componente.VeicoliAssociati (feedback utente
    /// 2026-09-23): eliminare definitivamente un componente ancora citata in Funzionalita.EcuAssociate lascerebbe un
    /// riferimento rotto nel filtro Funzionalità del Test, scoperto solo più tardi invece che al momento
    /// dell'eliminazione.</summary>
    [Fact]
    public void EcuViewModel_HaRiferimenti_BloccaEliminazioneSeCitataInEcuAssociateDiUnaFunzionalita()
    {
        var state = CreateMasterState();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", Archiviata = true, ModificatoDa = "Seed" } });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Specifica-BCM", TutteLeEcu = false, EcuAssociate = new List<string> { "BCM" }, ModificatoDa = "Seed" },
        });
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(e => e.Nome == "BCM");

        vm.EliminaDefinitivamenteCommand.Execute(riga);

        Assert.Contains(riga, vm.Items);
        Assert.True(vm.HaMessaggioErrore);
    }

    // ---------- Filtro nell'editor Test ----------

    [Fact]
    public void TestCaseEditor_FunzionalitaDisponibili_NessunaEcuScelta_MostraTutteLeAttive()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Generica", ModificatoDa = "Seed" },
            new() { Nome = "Specifica-BCM", TutteLeEcu = false, EcuAssociate = new List<string> { "BCM" }, ModificatoDa = "Seed" },
        });

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.Contains("Generica", vm.FunzionalitaDisponibili);
        Assert.Contains("Specifica-BCM", vm.FunzionalitaDisponibili);
    }

    [Fact]
    public void TestCaseEditor_FunzionalitaDisponibili_ConEcuScelta_MostraSoloAssociateEGeneriche()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Generica", ModificatoDa = "Seed" },
            new() { Nome = "Specifica-BCM", TutteLeEcu = false, EcuAssociate = new List<string> { "BCM" }, ModificatoDa = "Seed" },
            new() { Nome = "Specifica-SGW", TutteLeEcu = false, EcuAssociate = new List<string> { "SGW" }, ModificatoDa = "Seed" },
        });

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.AggiungiEcuCommand.Execute("BCM");

        Assert.Contains("Generica", vm.FunzionalitaDisponibili);
        Assert.Contains("Specifica-BCM", vm.FunzionalitaDisponibili);
        Assert.DoesNotContain("Specifica-SGW", vm.FunzionalitaDisponibili);
    }

    [Fact]
    public void TestCaseEditor_MostraTutteLeFunzionalita_IgnoraIlFiltro()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Specifica-SGW", TutteLeEcu = false, EcuAssociate = new List<string> { "SGW" }, ModificatoDa = "Seed" },
        });

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.AggiungiEcuCommand.Execute("BCM");
        Assert.DoesNotContain("Specifica-SGW", vm.FunzionalitaDisponibili);

        vm.MostraTutteLeFunzionalita = true;

        Assert.Contains("Specifica-SGW", vm.FunzionalitaDisponibili);
    }

    /// <summary>Una Funzionalita già collegata al test non deve mai sparire dal picker per un cambio di componente
    /// successivo (stesso principio di EcuDisponibili che tiene sempre visibile il componente corrente del test),
    /// altrimenti l'operatore vedrebbe un valore selezionato non più presente tra le opzioni disponibili.</summary>
    [Fact]
    public void TestCaseEditor_FunzionalitaGiaCollegata_RestaVisibileAncheSeNonPiuAssociataAllEcuScelta()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Specifica-SGW", TutteLeEcu = false, EcuAssociate = new List<string> { "SGW" }, ModificatoDa = "Seed" },
        });

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.NuovaFunzionalita = "Specifica-SGW";
        vm.AggiungiFunzionalitaCommand.Execute(null);
        vm.AggiungiEcuCommand.Execute("BCM");

        Assert.Contains("Specifica-SGW", vm.FunzionalitaDisponibili);
    }

    // ---------- Filtro nell'editor Segnalazioni rete ----------

    private AppState CreateStateSegnalazione()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", ModificatoDa = "Seed" }, new() { Nome = "SGW", ModificatoDa = "Seed" } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily MY24", GammaNome = "Daily", ModificatoDa = "Seed" } });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Generica", ModificatoDa = "Seed" },
            new() { Nome = "Specifica-BCM", TutteLeEcu = false, EcuAssociate = new List<string> { "BCM" }, ModificatoDa = "Seed" },
            new() { Nome = "Specifica-SGW", TutteLeEcu = false, EcuAssociate = new List<string> { "SGW" }, ModificatoDa = "Seed" },
        });
        var operatori = state.Master.Operatori.Load();
        operatori.Add(TestOperatori.Sigilla(new Operatore { Nome = "Operatore Rete", Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Operatore Rete", "1234"));
        return state;
    }

    [Fact]
    public void SegnalazioneRete_FunzionalitaDisponibili_ConEcuScelta_MostraSoloAssociateEGeneriche()
    {
        var state = CreateStateSegnalazione();
        var vm = new SegnalazioneReteEditorViewModel(state, id: null, torna: () => { });

        vm.AggiungiEcuCommand.Execute("BCM");

        Assert.Contains("Generica", vm.FunzionalitaDisponibili);
        Assert.Contains("Specifica-BCM", vm.FunzionalitaDisponibili);
        Assert.DoesNotContain("Specifica-SGW", vm.FunzionalitaDisponibili);
    }

    [Fact]
    public void SegnalazioneRete_MostraTutteLeFunzionalita_IgnoraIlFiltro()
    {
        var state = CreateStateSegnalazione();
        var vm = new SegnalazioneReteEditorViewModel(state, id: null, torna: () => { });
        vm.AggiungiEcuCommand.Execute("BCM");
        Assert.DoesNotContain("Specifica-SGW", vm.FunzionalitaDisponibili);

        vm.MostraTutteLeFunzionalita = true;

        Assert.Contains("Specifica-SGW", vm.FunzionalitaDisponibili);
    }
}
