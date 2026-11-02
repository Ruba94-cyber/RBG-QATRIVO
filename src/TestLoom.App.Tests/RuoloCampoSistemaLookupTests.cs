using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class RuoloCampoSistemaLookupTests
{
    [Fact]
    public void PerRuolo_TrovaLUnicoCampoConQuelRuolo()
    {
        var catalogo = new List<CampoSistemaDefinizione> { new() { Chiave = "Componente", Ruolo = RuoloCampoSistema.Componente } };
        var voce = RuoloCampoSistemaLookup.PerRuolo(catalogo, RuoloCampoSistema.Componente);
        Assert.NotNull(voce);
        Assert.Equal("Componente", voce!.Chiave);
    }

    [Fact]
    public void PerRuolo_NullSeNessunCampoHaQuelRuolo()
    {
        var catalogo = new List<CampoSistemaDefinizione>();
        Assert.Null(RuoloCampoSistemaLookup.PerRuolo(catalogo, RuoloCampoSistema.Componente));
    }

    [Fact]
    public void PerRuolo_IgnoraCampiArchiviati()
    {
        var catalogo = new List<CampoSistemaDefinizione> { new() { Chiave = "Componente", Ruolo = RuoloCampoSistema.Componente, Archiviata = true } };
        Assert.Null(RuoloCampoSistemaLookup.PerRuolo(catalogo, RuoloCampoSistema.Componente));
    }

    [Fact]
    public void ChiaveDelRuolo_RestituisceSoloLaChiave()
    {
        var catalogo = new List<CampoSistemaDefinizione> { new() { Chiave = "Componente", Ruolo = RuoloCampoSistema.Componente } };
        Assert.Equal("Componente", RuoloCampoSistemaLookup.ChiaveDelRuolo(catalogo, RuoloCampoSistema.Componente));
    }

    [Fact]
    public void PerRuolo_NessunoRestituisceSempreNull()
    {
        var catalogo = new List<CampoSistemaDefinizione> { new() { Chiave = "X", Ruolo = RuoloCampoSistema.Nessuno } };
        Assert.Null(RuoloCampoSistemaLookup.PerRuolo(catalogo, RuoloCampoSistema.Nessuno));
    }

    [Fact]
    public void EtichettaDelRuolo_RestituisceLEtichettaDelCatalogoSeValorizzata()
    {
        var catalogo = new List<CampoSistemaDefinizione> { new() { Chiave = "Componente", Ruolo = RuoloCampoSistema.Componente, Etichetta = "Centralina" } };
        Assert.Equal("Centralina", RuoloCampoSistemaLookup.EtichettaDelRuolo(catalogo, RuoloCampoSistema.Componente, "Componente"));
    }

    [Fact]
    public void EtichettaDelRuolo_UsaIlFallbackSeNessunCampoHaQuelRuolo()
    {
        var catalogo = new List<CampoSistemaDefinizione>();
        Assert.Equal("Componente", RuoloCampoSistemaLookup.EtichettaDelRuolo(catalogo, RuoloCampoSistema.Componente, "Componente"));
    }

    [Fact]
    public void EtichettaDelRuolo_UsaIlFallbackSeLEtichettaCatalogataEVuota()
    {
        var catalogo = new List<CampoSistemaDefinizione> { new() { Chiave = "Componente", Ruolo = RuoloCampoSistema.Componente, Etichetta = "" } };
        Assert.Equal("Componente", RuoloCampoSistemaLookup.EtichettaDelRuolo(catalogo, RuoloCampoSistema.Componente, "Componente"));
    }
}
