using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>Punto 1 (feedback 2026-10): la Home conta il lavoro reale, cioe' le operazioni eseguite per componente, non le REV.</summary>
public sealed class ConteggioLavoroPerOperatoreTests
{
    private static Condizione Op(Esito esito, params string[] componente) => new() { Esito = esito, Componente = new EcuSelection(componente) };

    private static TestCase Test(params Esecuzione[] revisioni)
    {
        var tc = new TestCase { Id = "T", Uid = Guid.NewGuid(), Componente = new EcuSelection(new[] { "BCM" }) };
        tc.Esecuzioni.AddRange(revisioni);
        return tc;
    }

    [Fact]
    public void DieciOperazioniNellaStessaRev_ValgonoDieciEsecuzioni()
    {
        var rev = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = Enumerable.Range(0, 10).Select(_ => Op(Esito.Conforme, "BCM")).ToList() };
        Assert.Equal(10, HomeViewModel.ContaEsecuzioniPerOperatore([Test(rev)])["Mario"]);
    }

    [Fact]
    public void CelleEreditateDaEseguire_NonContano_RetestUgualeSi()
    {
        var rev1 = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM"), Op(Esito.Conforme, "BCM")] };
        // Nuova REV: le condizioni clonate partono Da eseguire; una sola viene rieseguita con lo STESSO esito.
        var rev2 = new Esecuzione { Iterazione = 2, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM"), Op(Esito.DaEseguire, "BCM")] };
        Assert.Equal(3, HomeViewModel.ContaEsecuzioniPerOperatore([Test(rev1, rev2)])["Mario"]);
    }

    [Fact]
    public void OperazioneSuDueEcu_ValeDue_NaENonTestataZero_ReteEsclusa()
    {
        var rev = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM", "VMU"), Op(Esito.NonApplicabile, "BCM"), Op(Esito.DaEseguire, "BCM")] };
        var rete = new Esecuzione { Iterazione = 2, Operatore = "Mario", Origine = OrigineRevisione.Rete, Condizioni = [Op(Esito.Conforme, "BCM")] };
        Assert.Equal(2, HomeViewModel.ContaEsecuzioniPerOperatore([Test(rev, rete)])["Mario"]);
    }

    [Fact]
    public void OperazioneSenzaEcuRisolvibile_ContaComunqueUno()
    {
        var tc = new TestCase { Id = "T", Uid = Guid.NewGuid() };
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme)] });
        Assert.Equal(1, HomeViewModel.ContaEsecuzioniPerOperatore([tc])["Mario"]);
    }

    private static Condizione Clone(Condizione c) =>
        System.Text.Json.JsonSerializer.Deserialize<Condizione>(System.Text.Json.JsonSerializer.Serialize(c, JsonIo.Options), JsonIo.Options)!;

    private static Esecuzione RevCopia(Esecuzione prec, int it, string operatore, OrigineRevisione origine = OrigineRevisione.Operatore) =>
        new() { Iterazione = it, Operatore = operatore, Origine = origine, Condizioni = prec.Condizioni.Select(Clone).ToList() };

    [Fact]
    public void CloneNonToccato_NuovaRevDiAltroOperatore_NonViene_Ricontato()
    {
        var rev1 = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM"), Op(Esito.NonConforme, "BCM")] };
        var rev2 = RevCopia(rev1, 2, "Luigi"); // stessi Uid e stessi esiti: nessuno ha toccato nulla
        var counts = HomeViewModel.ContaEsecuzioniPerOperatore([Test(rev1, rev2)]);
        Assert.Equal(2, counts["Mario"]);
        Assert.DoesNotContain("Luigi", counts.Keys);
    }

    [Fact]
    public void CloneConRiconoscimento_EreditaEdEEscluso_AnchePerOperatoreDiversoDellaRev()
    {
        var rev1 = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM")] };
        var tc = Test(rev1);
        OperationalSemantics.RiconosciEsecuzioni(tc);
        Assert.Equal("Mario", rev1.Condizioni[0].OperatoreEsecuzione);
        var rev2 = RevCopia(rev1, 2, "Luigi");
        tc.Esecuzioni.Add(rev2);
        OperationalSemantics.RiconosciEsecuzioni(tc);
        Assert.Equal(rev1.Condizioni[0].EsecuzioneUid, rev2.Condizioni[0].EsecuzioneUid);
        Assert.Equal("Mario", rev2.Condizioni[0].OperatoreEsecuzione);
        var counts = HomeViewModel.ContaEsecuzioniPerOperatore([tc]);
        Assert.Equal(1, counts["Mario"]);
        Assert.DoesNotContain("Luigi", counts.Keys);
    }

    [Fact]
    public void Retest_StessoEsito_NuovaIdentita_ContaComeNuovaEsecuzione()
    {
        var rev1 = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM")] };
        var tc = Test(rev1);
        OperationalSemantics.RiconosciEsecuzioni(tc);
        // Nuova REV: l'operazione riparte Da eseguire (nuovo Uid) e Luigi la riesegue con lo STESSO esito.
        var rev2 = new Esecuzione { Iterazione = 2, Operatore = "Luigi", Condizioni = [Op(Esito.Conforme, "BCM")] };
        tc.Esecuzioni.Add(rev2);
        OperationalSemantics.RiconosciEsecuzioni(tc);
        Assert.NotEqual(rev1.Condizioni[0].EsecuzioneUid, rev2.Condizioni[0].EsecuzioneUid);
        var counts = HomeViewModel.ContaEsecuzioniPerOperatore([tc]);
        Assert.Equal(1, counts["Mario"]);
        Assert.Equal(1, counts["Luigi"]);
    }

    [Fact]
    public void StessaOperazione_EsitoCambiato_ContaPerChiLoCambia()
    {
        var rev1 = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.NonConforme, "BCM")] };
        var tc = Test(rev1);
        OperationalSemantics.RiconosciEsecuzioni(tc);
        var rev2 = RevCopia(rev1, 2, "Luigi");
        rev2.Condizioni[0].Esito = Esito.Conforme; // stesso Uid, esito nuovo, riconoscimento ereditato ma obsoleto
        tc.Esecuzioni.Add(rev2);
        OperationalSemantics.RiconosciEsecuzioni(tc);
        Assert.Equal("Luigi", rev2.Condizioni[0].OperatoreEsecuzione);
        var counts = HomeViewModel.ContaEsecuzioniPerOperatore([tc]);
        Assert.Equal(1, counts["Mario"]);
        Assert.Equal(1, counts["Luigi"]);
    }

    [Fact]
    public void Pianificata_DaEseguire_NonContaEPerdeIlRiconoscimento()
    {
        var rev1 = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.DaEseguire, "BCM"), Op(Esito.NonEseguitoNA, "BCM")] };
        var tc = Test(rev1);
        rev1.Condizioni[0].EsecuzioneUid = Guid.NewGuid(); // residuo
        OperationalSemantics.RiconosciEsecuzioni(tc);
        Assert.Null(rev1.Condizioni[0].EsecuzioneUid);
        Assert.Empty(HomeViewModel.ContaEsecuzioniPerOperatore([tc]));
    }

    [Fact]
    public void RevRete_NonContaENonFaRicontareLeCopieSuccessive()
    {
        var rev1 = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.NonConforme, "BCM")] };
        var rete = RevCopia(rev1, 2, "Paolo", OrigineRevisione.Rete);
        rete.Condizioni[0].Esito = Esito.Conforme; // la rete chiude l'anomalia: nessun operatore
        var rev3 = RevCopia(rete, 3, "Luigi");     // copia non toccata dello stato deciso dalla rete
        var counts = HomeViewModel.ContaEsecuzioniPerOperatore([Test(rev1, rete, rev3)]);
        Assert.Equal(1, counts["Mario"]);
        Assert.DoesNotContain("Paolo", counts.Keys);
        Assert.DoesNotContain("Luigi", counts.Keys);
    }

    [Fact]
    public void DatiLegacySenzaRiconoscimento_DeduconoOperatoreDellaRevDiComparsa_SenzaRaddoppi()
    {
        var rev1 = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM"), Op(Esito.NonConforme, "BCM")] };
        var rev2 = RevCopia(rev1, 2, "Mario");
        rev2.Condizioni[1].Esito = Esito.Conforme; // solo questa cambia
        var rev3 = RevCopia(rev2, 3, "Luigi");
        Assert.All(new[] { rev1, rev2, rev3 }.SelectMany(r => r.Condizioni), c => Assert.Null(c.EsecuzioneUid));
        var counts = HomeViewModel.ContaEsecuzioniPerOperatore([Test(rev1, rev2, rev3)]);
        Assert.Equal(3, counts["Mario"]); // 2 in REV1 + 1 cambiata in REV2
        Assert.DoesNotContain("Luigi", counts.Keys);
    }

    [Fact]
    public void RiconoscimentoNonSerializzatoSeVuoto_ERoundTripConservaIlCampo()
    {
        var vuota = System.Text.Json.JsonSerializer.Serialize(Op(Esito.Conforme, "BCM"), JsonIo.Options);
        Assert.DoesNotContain("EsecuzioneUid", vuota);
        Assert.DoesNotContain("OperatoreEsecuzione", vuota);
        var c = Op(Esito.Conforme, "BCM");
        c.OperatoreEsecuzione = "Mario"; c.EsecuzioneUid = Guid.NewGuid(); c.DataEsecuzione = new DateTime(2026, 1, 2);
        var back = Clone(c);
        Assert.Equal(c.EsecuzioneUid, back.EsecuzioneUid);
        Assert.Equal("Mario", back.OperatoreEsecuzione);
        Assert.Equal(c.DataEsecuzione, back.DataEsecuzione);
    }

    [Fact]
    public void UnitaPerRevisioneSql_SommanoAlConteggioHome()
    {
        var rev1 = new Esecuzione { Iterazione = 1, Operatore = "Mario", Condizioni = [Op(Esito.Conforme, "BCM", "VMU"), Op(Esito.NonConforme, "BCM")] };
        var rev2 = RevCopia(rev1, 2, "Mario");
        var tc = Test(rev1, rev2);
        var unita = OperationalSemantics.OperatorWorkUnitsPerRevision(tc);
        Assert.Equal([3, 0], unita);
        Assert.Equal(unita.Sum(), HomeViewModel.ContaEsecuzioniPerOperatore([tc])["Mario"]);
    }
}
