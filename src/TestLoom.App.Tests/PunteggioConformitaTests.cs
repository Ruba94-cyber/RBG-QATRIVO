using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>LocalizationService.Instance è un singleton di processo: xunit esegue le classi di test in
/// parallelo tra loro, quindi qualunque test che legga un'etichetta dipendente dalla lingua deve stare nella
/// stessa collection xunit di ogni altro test che la cambia (qui LocalizationUiTests), altrimenti due classi
/// in esecuzione contemporanea si scavalcano a vicenda il valore di Lingua a metà test.</summary>
[CollectionDefinition("Localization", DisableParallelization = true)]
public class LocalizationCollection { }

[Collection("Localization")]
public class PunteggioConformitaTests
{
    [Fact]
    public void Label_Conforme_RestituisceConforme()
    {
        var loc = LocalizationService.Instance;
        var original = loc.Lingua;
        try
        {
            loc.Lingua = "it";
            Assert.Equal("Conforme (5)", PunteggioConformita.Conforme.Label());
        }
        finally { loc.Lingua = original; }
    }

    [Fact]
    public void Label_NonApplicabile_RestituisceNA()
    {
        var loc = LocalizationService.Instance;
        var original = loc.Lingua;
        try
        {
            loc.Lingua = "it";
            Assert.Equal("N.A.", PunteggioConformita.NonApplicabile.Label());
        }
        finally { loc.Lingua = original; }
    }

    [Fact]
    public void Esecuzione_PunteggiRequisiti_DefaultVuoto()
    {
        var esecuzione = new Esecuzione();
        Assert.Empty(esecuzione.PunteggiRequisiti);
    }

    [Fact]
    public void PunteggioRequisito_ValoriDiDefault()
    {
        var p = new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.Conforme };
        Assert.False(p.Sovrascritto);
        Assert.Equal("", p.MotivazioneOverride);
    }
}
