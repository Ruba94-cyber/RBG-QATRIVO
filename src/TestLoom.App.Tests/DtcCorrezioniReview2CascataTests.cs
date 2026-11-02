using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed partial class DtcCorrezioniReview2EditorTests
{
    // ---- 4) cascata veicolo ---------------------------------------------------------------------------------------------

    [Fact]
    public void CodiceLibero_Cambiato_NonLasciaIlVeicoloDelDtcPrecedente_ERivalidaConLAbbinamento()
    {
        var d1 = Extra("D1", ("P0001", "ECM", "A"), ("P0002", "ECM", "B"));
        var vm = Apri(CreaStato(d1), Test(new[] { "D1" }, new[] { "F1" }, new[] { "Guasto Injection" }, new[] { "A", "B" }));
        var c = new Condizione { Esito = Esito.DaEseguire, Componente = new EcuSelection(new[] { "ECM" }) };
        Ops(vm).Add(c);
        Assert.True(vm.ImpostaDtcCodiceLibero(c, "P0001"));
        c.DtcVeicolo = "A";                                                         // l'utente (o la precedente scelta) aveva il veicolo A
        Assert.True(vm.ImpostaDtcCodiceLibero(c, "P0002"));                         // P0002 esiste solo per B
        Assert.Equal("B", c.DtcVeicolo);
        Assert.Equal("D1", c.DtcRequisitoId);
        Assert.True(vm.ImpostaDtcCodiceLibero(c, "P9999"));                         // nessuna voce: veicolo azzerato, guasto libero
        Assert.Null(c.DtcVeicolo);
        Assert.Null(c.DtcRequisitoId);
    }

    [Fact]
    public void AggiungiDaElenco_UsaIlVeicoloDelDtcScelto_NonQuelloVuotoDelBlocco()
    {
        var d1 = Extra("D1", ("P0001", "ECM", "A"));
        var vm = Apri(CreaStato(d1), Test(new[] { "D1" }, new[] { "F1" }, new[] { "Guasto Injection" }, new[] { "A", "B" }));
        vm.DtcInsVeicolo = "A";
        var opzione = Assert.Single(vm.DtcInsDisponibili);
        vm.DtcInsVeicolo = null;                                                    // il blocco non ha un veicolo selezionato al momento dell'aggiunta
        vm.DtcInsScelto = opzione;
        vm.AggiungiDtcCommand.Execute(null);
        Assert.Null(vm.DtcInsErrore);
        Assert.All(Ops(vm), o => Assert.Equal("A", o.DtcVeicolo));
    }
}
