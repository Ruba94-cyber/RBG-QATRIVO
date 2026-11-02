using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Copertura del punto 8 (feedback utente 2026-08-24): a differenza di ListaOpzioniEditorWindow.ParseRighe
/// (opzioni di un dropdown), DividiInRighe non deduplica né scarta righe vuote — un trace incollato ripete le
/// stesse righe e ha separatori vuoti di proposito, vanno preservati esattamente.</summary>
public class ListaTestoEditorWindowTests
{
    [Fact]
    public void DividiInRighe_PreservaDuplicati()
    {
        var risultato = ListaTestoEditorWindow.DividiInRighe("TX > 22 01 07\nRX < 62 01 07\nTX > 22 01 07").ToList();
        Assert.Equal(new[] { "TX > 22 01 07", "RX < 62 01 07", "TX > 22 01 07" }, risultato);
    }

    [Fact]
    public void DividiInRighe_PreservaRigheVuoteESeparatori()
    {
        var risultato = ListaTestoEditorWindow.DividiInRighe("riga1\n\n----\nriga2").ToList();
        Assert.Equal(new[] { "riga1", "", "----", "riga2" }, risultato);
    }

    [Fact]
    public void DividiInRighe_NonRimuoveSpaziAiBordi()
    {
        // a differenza di ParseRighe: un trace può avere indentazione significativa (allineamento colonne)
        var risultato = ListaTestoEditorWindow.DividiInRighe("   3549 - TX > 2F 01 07").ToList();
        Assert.Equal(new[] { "   3549 - TX > 2F 01 07" }, risultato);
    }

    [Fact]
    public void DividiInRighe_GestisceCarriageReturnWindows()
    {
        var risultato = ListaTestoEditorWindow.DividiInRighe("riga1\r\nriga2\r\nriga3").ToList();
        Assert.Equal(new[] { "riga1", "riga2", "riga3" }, risultato);
    }

    [Fact]
    public void DividiInRighe_TestoVuotoONull_RitornaUnaRigaVuota()
    {
        // Split su stringa vuota produce un array con un solo elemento vuoto, non una lista vuota:
        // comportamento di String.Split, documentato qui perché non ovvio a chi legge il chiamante.
        Assert.Equal(new[] { "" }, ListaTestoEditorWindow.DividiInRighe("").ToList());
        Assert.Equal(new[] { "" }, ListaTestoEditorWindow.DividiInRighe(null).ToList());
    }
}
