using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed partial class DtcCorrezioniReview2EditorTests
{
    // ---- 6) ScegliDtcIns: elenco costruito da uno snapshot copiato sul thread UI ------------------------------------------

    [Fact]
    public async Task ScegliDtcIns_CostruisceLElencoDaSnapshot_EscludeIDtcGiaAggiunti_SenzaEnumerareLaCollezioneOsservabileNelTask()
    {
        var vm = Apri(CreaStato(Extra("D1", ("P0001", "ECM", null), ("P0002", "ECM", null), ("P0003", "ECM", null))),
            Test(new[] { "D1" }, new[] { "F1" }, new[] { "Guasto Injection" }));
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0002", "D1"));
        var originale = SelezionaDialogService.Handler;
        IReadOnlyList<VoceSelezionabile>? viste = null;
        SelezionaDialogService.Handler = (_, voci, _) => { viste = voci; return Task.FromResult<IReadOnlyList<VoceSelezionabile>>(new[] { voci[0] }); };
        try
        {
            await vm.ScegliDtcInsCommand.ExecuteAsync(null);
            Assert.NotNull(viste);
            Assert.Equal(new[] { "P0001", "P0003" }, viste!.Select(v => ((DtcOpzione)v.Valore).Codice).ToArray());
            Assert.Equal("P0001", vm.DtcInsScelto?.Codice);
        }
        finally { SelezionaDialogService.Handler = originale; }
        // il costruttore dell'elenco non tocca piu DtcInseriti: lo snapshot e un HashSet gia copiato
        var snapshot = typeof(TestCaseEditorViewModel).GetMethod("SnapshotDtcIns", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null)!;
        Assert.IsAssignableFrom<ISet<string>>(snapshot.GetType().GetProperty("Chiavi")!.GetValue(snapshot));
    }
}
