using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Unione delle REV di un Test in conflitto (caso T-BD-0012: 4 REV da una parte, 5 dall'altra): tutte le REV tenute,
/// comuni invariate, nuove in ordine di data e rinumerate, NumeroRevisione ricalcolato, nessuna operazione persa.</summary>
public sealed class TestRevisionMergeTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Unspecified);

    private static Esecuzione Rev(int it, int giorno, string op, string note = "") =>
        new() { Iterazione = it, Data = T0.AddDays(giorno), Operatore = op, Note = note, Matricola = "Matricola" };

    private static byte[] Bytes(Guid uid, params Esecuzione[] rev)
    {
        var tc = new TestCase { Id = "T-BD-0012", Uid = uid, Descrizione = "d", CreatoDa = "x", ModificatoDa = "x" };
        tc.Esecuzioni.AddRange(rev);
        return JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options);
    }

    private static List<Esecuzione> Unite(RecordMergeResult r) => ((TestCase)r.Model!).Esecuzioni;

    [Fact]
    public void RevNuoveSuEntrambeLeParti_TuttetenuteInOrdineDiDataERinumerate()
    {
        var uid = Guid.NewGuid();
        Esecuzione[] comuni = [Rev(1, 0, "Anna"), Rev(2, 1, "Anna"), Rev(3, 2, "Bruno")];
        var mia = Bytes(uid, [.. comuni, Rev(4, 5, "Anna", "mia-5-novembre")]);
        var altra = Bytes(uid, [.. comuni, Rev(4, 3, "Bruno", "loro-3"), Rev(5, 6, "Bruno", "loro-6")]);

        var prep = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, mia, altra);

        Assert.True(prep.Confrontabile);
        Assert.DoesNotContain(prep.Plan!.Differences, d => d.Name == "Esecuzioni");
        Assert.DoesNotContain(prep.Plan.Fields, f => f.Name == "NumeroRevisione");
        Assert.Contains("numerazione", prep.Plan.Nota);
        var res = prep.Plan.Build(RecordMergeChoices.Tutti(RecordFieldChoice.Mia, prep.Plan), "Master");
        var rev = Unite(res);
        Assert.Equal(6, rev.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, rev.Select(r => r.Iterazione));
        Assert.Equal(new[] { "", "", "", "loro-3", "mia-5-novembre", "loro-6" }, rev.Select(r => r.Note));
        Assert.Equal(6, ((TestCase)res.Model!).NumeroRevisione);
        Assert.Contains(((TestCase)res.Model!).Changelog, c => c.ValoreNuovo?.Contains("REV unite") == true);
    }

    [Fact]
    public void QuattroControCinque_StessoPrefisso_SiTieneLaPiuLunga_SenzaRinumerare()
    {
        var uid = Guid.NewGuid();
        Esecuzione[] comuni = [Rev(1, 0, "Anna"), Rev(2, 1, "Anna"), Rev(3, 2, "Anna"), Rev(4, 3, "Anna")];
        var quattro = Bytes(uid, comuni);
        var cinque = Bytes(uid, [.. comuni, Rev(5, 4, "Bruno")]);

        var plan = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, quattro, cinque).Plan!;
        var res = plan.Build(RecordMergeChoices.Tutti(RecordFieldChoice.Mia, plan), "Master");

        Assert.Equal(5, Unite(res).Count);
        Assert.Equal(5, ((TestCase)res.Model!).NumeroRevisione);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Unite(res).Select(r => r.Iterazione));
    }

    [Fact]
    public void StessaRevModificataInModoDiverso_EntrambeTenute_NessunaSceltaRichiesta()
    {
        var uid = Guid.NewGuid();
        var mia = Bytes(uid, Rev(1, 0, "Anna", "nota A"));
        var altra = Bytes(uid, Rev(1, 0, "Anna", "nota B"));

        var plan = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, mia, altra).Plan!;

        Assert.DoesNotContain(plan.Differences, d => d.Name == "Esecuzioni");
        var rev = Unite(plan.Build(new RecordMergeChoices(), "Master"));
        Assert.Equal(2, rev.Count);
        Assert.Contains("nota A", rev.Select(r => r.Note));
        Assert.Contains("nota B", rev.Select(r => r.Note));
    }
}
