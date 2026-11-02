using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class OperationEvidenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_OpEvidence_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _opened = new();

    private TestCaseEditorViewModel CreaVm()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var operatore = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        state.ImpostaOperatore(operatore.Nome, "1234");
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        _opened.Add(state);
        _opened.Add(vm);
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        return vm;
    }

    private string File1(string name = "evidence.png")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        return path;
    }

    [Fact]
    public void ReservedKeyRoundTripsIgnoresGarbageAndIsNeverACopiedToolField()
    {
        var valori = new Dictionary<string, string> { ["UnitLogger::Campo"] = "x" };
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.True(EvidenzaOperazione.Aggiungi(valori, a));
        Assert.True(EvidenzaOperazione.Aggiungi(valori, a)); // idempotent
        Assert.True(EvidenzaOperazione.Aggiungi(valori, b));
        Assert.Equal([a, b], EvidenzaOperazione.Leggi(valori));
        valori[EvidenzaOperazione.ChiaveValoriStrumenti] += ";not-a-guid;;";
        Assert.Equal([a, b], EvidenzaOperazione.Leggi(valori));

        var copia = DurataOperazione.CopiaSenzaDurata(valori);
        Assert.False(copia.ContainsKey(EvidenzaOperazione.ChiaveValoriStrumenti));
        Assert.Equal("x", copia["UnitLogger::Campo"]);
        Assert.False(DurataOperazione.HaValoriStrumenti(new Dictionary<string, string>
            { [EvidenzaOperazione.ChiaveValoriStrumenti] = a.ToString() }));

        Assert.True(EvidenzaOperazione.Rimuovi(valori, a));
        Assert.True(EvidenzaOperazione.Rimuovi(valori, b));
        Assert.False(valori.ContainsKey(EvidenzaOperazione.ChiaveValoriStrumenti));
        Assert.False(EvidenzaOperazione.Rimuovi(valori, b));
    }

    [Fact]
    public void LimitPerOperationIsEnforced()
    {
        var valori = new Dictionary<string, string>();
        for (var i = 0; i < EvidenzaOperazione.MassimoPerOperazione; i++)
            Assert.True(EvidenzaOperazione.Aggiungi(valori, Guid.NewGuid()));
        Assert.False(EvidenzaOperazione.Aggiungi(valori, Guid.NewGuid()));
        Assert.Equal(EvidenzaOperazione.MassimoPerOperazione, EvidenzaOperazione.Leggi(valori).Count);
    }

    [Fact]
    public void LegacyOperationJsonWithoutKeyLoadsAsTestLevelAndCountIsNotSerialized()
    {
        var json = JsonSerializer.Serialize(new Condizione(), JsonIo.Options);
        Assert.DoesNotContain("NumeroEvidenze", json);
        Assert.DoesNotContain("HaEvidenze", json);
        var letta = JsonSerializer.Deserialize<Condizione>(json, JsonIo.Options)!;
        Assert.Empty(EvidenzaOperazione.Leggi(letta.ValoriStrumenti));
        var allegatoJson = JsonSerializer.Serialize(new Allegato { NomeFile = "a.png" }, JsonIo.Options);
        Assert.DoesNotContain("EtichettaOperazione", allegatoJson);
    }

    [Fact]
    public async Task AttachingToOperationLinksOnlyThatOperationAndSurvivesSerialization()
    {
        var vm = CreaVm();
        vm.AggiungiCondizioneCommand.Execute(null);
        var conds = vm.RevisioneCorrente!.Condizioni;
        Assert.True(conds.Count >= 2);
        var prima = conds[0];
        var altra = conds[1];

        await vm.AggiungiEvidenzaOperazioneSicura(prima, File1());

        var allegato = Assert.Single(vm.TestCase.Allegati);
        Assert.Equal(1, prima.NumeroEvidenze);
        Assert.Equal(0, altra.NumeroEvidenze);
        Assert.Equal(allegato.Uid, Assert.Single(vm.EvidenzeDiOperazione(prima)).Uid);
        Assert.Empty(vm.EvidenzeDiOperazione(altra));
        Assert.Contains("REV 1", allegato.EtichettaOperazione);
        Assert.True(vm.ModificheNonSalvate);

        var json = JsonSerializer.Serialize(vm.RevisioneCorrente, JsonIo.Options);
        var round = JsonSerializer.Deserialize<Esecuzione>(json, JsonIo.Options)!;
        Assert.Equal(allegato.Uid, Assert.Single(EvidenzaOperazione.Leggi(round.Condizioni[0].ValoriStrumenti)));
    }

    [Fact]
    public async Task ReuseUnlinkAndDeleteLeaveNoOrphanLinksAndNeverDeleteTheFileOnUnlink()
    {
        var vm = CreaVm();
        vm.AggiungiCondizioneCommand.Execute(null);
        var conds = vm.RevisioneCorrente!.Condizioni;
        await vm.AggiungiEvidenzaOperazioneSicura(conds[0], File1());
        var allegato = vm.TestCase.Allegati.Single();

        Assert.True(vm.CollegaEvidenzaEsistente(conds[1], allegato)); // same file reused by a second operation
        Assert.Equal(1, conds[1].NumeroEvidenze);
        Assert.Single(vm.TestCase.Allegati);

        vm.ScollegaEvidenzaOperazioneCommand.Execute(allegato);
        Assert.Equal(0, conds[0].NumeroEvidenze);
        Assert.Equal(0, conds[1].NumeroEvidenze);
        Assert.Single(vm.TestCase.Allegati); // unlink keeps the Test-level attachment
        Assert.Equal("", allegato.EtichettaOperazione);

        Assert.True(vm.CollegaEvidenzaEsistente(conds[0], allegato));
        vm.RimuoviAllegatoCommand.Execute(allegato);
        Assert.Empty(vm.TestCase.Allegati);
        Assert.Empty(EvidenzaOperazione.Leggi(conds[0].ValoriStrumenti));
        Assert.Equal(0, conds[0].NumeroEvidenze);
    }

    [Fact]
    public async Task ForeignOrReadOnlyOperationIsRejectedAndNoFileIsAdded()
    {
        var vm = CreaVm();
        var estranea = new Condizione { Funzionalita = "other" };
        await vm.AggiungiEvidenzaOperazioneSicura(estranea, File1());
        Assert.Empty(vm.TestCase.Allegati);
        Assert.Empty(EvidenzaOperazione.Leggi(estranea.ValoriStrumenti));

        var propria = vm.RevisioneCorrente!.Condizioni[0];
        vm.InModifica = false;
        await vm.AggiungiEvidenzaOperazioneSicura(propria, File1("two.png"));
        Assert.Empty(vm.TestCase.Allegati);
        Assert.False(vm.CollegaEvidenzaEsistente(propria, new Allegato()));
    }

    [Fact]
    public async Task UnsupportedFileIsRejectedWithoutPartialLink()
    {
        var vm = CreaVm();
        var op = vm.RevisioneCorrente!.Condizioni[0];
        var exe = Path.Combine(_dir, "bad.exe");
        File.WriteAllText(exe, "x");
        await vm.AggiungiEvidenzaOperazioneSicura(op, exe);
        Assert.Empty(vm.TestCase.Allegati);
        Assert.Empty(EvidenzaOperazione.Leggi(op.ValoriStrumenti));
        Assert.NotEmpty(vm.ErroriValidazione);
    }

    [Fact]
    public void EvidenceExcludesOperationFromBeingTreatedAsEmptyPlaceholder()
    {
        var valori = new Dictionary<string, string>();
        EvidenzaOperazione.Aggiungi(valori, Guid.NewGuid());
        Assert.NotEmpty(EvidenzaOperazione.Leggi(valori)); // the VM placeholder check reads this explicitly
        Assert.False(DurataOperazione.HaValoriStrumenti(valori)); // but it is not a tool value
    }

    public void Dispose()
    {
        foreach (var item in _opened.AsEnumerable().Reverse()) item.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
