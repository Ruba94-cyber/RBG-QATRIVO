using System.IO.Compression;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class TestCaseEditorPolarionPreviewTests : IDisposable
{
    private const string TestId = "TC-Ticketing-LIVE-SNAPSHOT";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "TestLoom_PolarionLive_" + Guid.NewGuid().ToString("N"));
    private readonly string _output = Path.Combine(Path.GetTempPath(), "TestLoom_PolarionPackage_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var path in new[] { _workspace, _output })
            if (Directory.Exists(path)) { try { Directory.Delete(path, recursive: true); } catch { } }
    }

    [Fact]
    public async Task AnteprimaEPacchetto_UsanoStrumentiEOperazioniCorrentiNonSalvati()
    {
        SeedDemoDataService.Genera(_workspace);
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_workspace);
        state.InizializzaCartella(_workspace);

        var test = new TestCase
        {
            Id = TestId,
            DescrizioneBreve = "Ticketing live snapshot",
            TestTemplateUid = state.Master.TestTemplate.Load().First().Uid,
            Componente = "Componente-LIVE",
            StrumentiUsati = new() { "Vecchio strumento" },
        };
        test.ValoriStrumentiTest["Vecchio strumento::Banco"] = "dato precedente";
        var esecuzione = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Operatore = "QA" };
        esecuzione.Condizioni.Add(new Condizione { Nota = "nota salvata" });
        esecuzione.ValoriStrumenti["Vecchio strumento::Risultato"] = "risultato precedente";
        test.Esecuzioni.Add(esecuzione);
        state.TestCases.Save(test, isNew: true);

        var vm = new TestCaseEditorViewModel(state, TestId, () => { }, _ => { });
        var vecchio = vm.StrumentiOptions.Single(o => o.Valore == "Vecchio strumento");
        vecchio.Selezionato = false;
        vm.StrumentiOptions.Add(new StrumentoOption { Valore = "Nuovo strumento", Selezionato = true });
        vm.TestCase.ValoriStrumentiTest["Nuovo strumento::Banco"] = "banco scelto ora";
        vm.TestCase.ValoriStrumentiTest["Vecchio strumento::Banco"] = "dato precedente";
        vm.RevisioneCorrente!.Condizioni[0].Nota = "operazione aggiunta senza salvare";
        vm.RevisioneCorrente.Condizioni[0].ValoriStrumenti["Nuovo strumento::Risultato"] = "risultato corrente";
        vm.RevisioneCorrente.Condizioni[0].ValoriStrumenti["Vecchio strumento::Risultato"] = "risultato precedente";
        var sezioni = new HashSet<string> { PolarionSezioni.CampiStrumento, PolarionSezioni.Operazioni };

        var anteprima = vm.GeneraAnteprimaPolarion(sezioni);
        Assert.Contains("banco scelto ora", anteprima);
        Assert.Contains("operazione aggiunta senza salvare", anteprima);
        Assert.Contains("risultato corrente", anteprima);
        Assert.DoesNotContain("dato precedente", anteprima);
        Assert.DoesNotContain("risultato precedente", anteprima);

        await vm.CreaTicketSicuro(new RichiestaPacchettoPolarion(_output, sezioni));
        Assert.Empty(vm.ErroriValidazione);
        var percorso = Assert.Single(Directory.GetFiles(_output, "*.zip"));
        using (var zip = ZipFile.OpenRead(percorso))
        using (var stream = Assert.IsAssignableFrom<Stream>(zip.GetEntry($"TestLoom_{TestId}.txt")?.Open()))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var testoPacchetto = await reader.ReadToEndAsync();
            Assert.Contains("banco scelto ora", testoPacchetto);
            Assert.Contains("operazione aggiunta senza salvare", testoPacchetto);
            Assert.Contains("risultato corrente", testoPacchetto);
            Assert.DoesNotContain("dato precedente", testoPacchetto);
            Assert.DoesNotContain("risultato precedente", testoPacchetto);
        }

        Assert.Equal(new[] { "Vecchio strumento" }, state.TestCases.GetById(TestId)!.StrumentiUsati);
        vm.Dispose();
    }
}
