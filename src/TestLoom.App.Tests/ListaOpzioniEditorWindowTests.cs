using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al punto 14 (feedback utente 2026-08-20): editor dedicato per le opzioni di un
/// campo Lista, una voce per riga invece di CSV separato da virgole — più chiaro su come separare le scelte.</summary>
public class ListaOpzioniEditorWindowTests
{
    [Fact]
    public void ParseRighe_UnaVocePerRiga_RitornaListaNellOrdine()
    {
        var risultato = ListaOpzioniEditorWindow.ParseRighe("Rosso\nVerde\nBlu");
        Assert.Equal(new[] { "Rosso", "Verde", "Blu" }, risultato);
    }

    [Fact]
    public void ParseRighe_RigheVuote_VengonoIgnorate()
    {
        var risultato = ListaOpzioniEditorWindow.ParseRighe("Rosso\n\n\nVerde\n   \nBlu");
        Assert.Equal(new[] { "Rosso", "Verde", "Blu" }, risultato);
    }

    [Fact]
    public void ParseRighe_Duplicati_VengonoUnitiSenzaDistinguereMaiuscole()
    {
        var risultato = ListaOpzioniEditorWindow.ParseRighe("Rosso\nrosso\nROSSO\nVerde");
        Assert.Equal(new[] { "Rosso", "Verde" }, risultato);
    }

    [Fact]
    public void ParseRighe_SpazioAiBordi_VieneRimosso()
    {
        var risultato = ListaOpzioniEditorWindow.ParseRighe("  Rosso  \n Verde\t");
        Assert.Equal(new[] { "Rosso", "Verde" }, risultato);
    }

    [Fact]
    public void ParseRighe_ConCarriageReturn_GestisceCorrettamenteWindowsLineEndings()
    {
        var risultato = ListaOpzioniEditorWindow.ParseRighe("Rosso\r\nVerde\r\nBlu");
        Assert.Equal(new[] { "Rosso", "Verde", "Blu" }, risultato);
    }

    [Fact]
    public void ParseRighe_TestoVuotoONull_RitornaListaVuota()
    {
        Assert.Empty(ListaOpzioniEditorWindow.ParseRighe(""));
        Assert.Empty(ListaOpzioniEditorWindow.ParseRighe(null));
    }

    [Fact]
    public void ParseRighe_VirgoleNelTesto_NonVengonoSpezzate_UnaVoceCompleta()
    {
        // Cuore del punto 14: a differenza del vecchio CSV, una virgola dentro il testo di una voce non la
        // spezza più in due opzioni separate.
        var risultato = ListaOpzioniEditorWindow.ParseRighe("Motore, Cambio, Freni\nAltro");
        Assert.Equal(new[] { "Motore, Cambio, Freni", "Altro" }, risultato);
    }
}
