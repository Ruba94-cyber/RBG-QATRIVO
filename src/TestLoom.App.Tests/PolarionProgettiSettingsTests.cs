using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Elenco di piu progetti Ticketing nelle impostazioni API: validazione, default TICKET e compatibilita del file.</summary>
public sealed class PolarionProgettiSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PolProgetti_" + Guid.NewGuid().ToString("N"));
    private readonly DataPaths _paths;

    public PolarionProgettiSettingsTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new DataPaths(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private PolarionApiSettings Carica() => PolarionApiSettingsService.Load(_paths);

    [Fact]
    public void SenzaElenco_ProgettiEffettivi_ContieneSoloEsta_EIlJsonNonHaIlCampo()
    {
        var s = Carica();
        var effettivi = s.ProgettiEffettivi();
        Assert.Single(effettivi);
        Assert.Equal("TICKET_Tickets", effettivi[0].Id);
        PolarionApiSettingsService.Save(_paths, s, true);
        Assert.DoesNotContain("Progetti", File.ReadAllText(_paths.PolarionApiConfigFile));
    }

    [Fact]
    public void SalvaERicarica_ConservaProgettiConNome_EdEstaRestaPrimo()
    {
        var s = Carica();
        s.Progetti = new() { new() { Id = "REQ_ALFA", Nome = "Requisiti Alfa" }, new() { Id = "REQ_BETA", Nome = "" } };
        PolarionApiSettingsService.Save(_paths, s, true);

        var r = Carica();
        Assert.Equal(new[] { "TICKET_Tickets", "REQ_ALFA", "REQ_BETA" }, r.ProgettiEffettivi().Select(p => p.Id).ToArray());
        Assert.Equal("Requisiti Alfa", r.ProgettiEffettivi()[1].Nome);
        Assert.Equal("REQ_BETA", r.ProgettiEffettivi()[2].Nome);   // nome vuoto -> ID
        Assert.True(r.ProgettoConfigurato("req_alfa"));
        Assert.False(r.ProgettoConfigurato("SCONOSCIUTO"));
        Assert.False(r.ProgettoConfigurato(null));
    }

    [Fact]
    public void EstaNonDuplicato_EPuoAvereUnNomeLeggibile()
    {
        var s = Carica();
        s.Progetti = new() { new() { Id = "esta_tickets", Nome = "Ticket TICKET" }, new() { Id = "REQ_X", Nome = "X" } };
        PolarionApiSettingsService.Save(_paths, s, true);
        var effettivi = Carica().ProgettiEffettivi();
        Assert.Equal(new[] { "TICKET_Tickets", "REQ_X" }, effettivi.Select(p => p.Id).ToArray());
        Assert.Equal("Ticket TICKET", effettivi[0].Nome);
        // Una voce TICKET senza nome particolare non occupa spazio nell'elenco salvato.
        var s2 = Carica();
        s2.Progetti = new() { new() { Id = "TICKET_Tickets", Nome = "" } };
        PolarionApiSettingsService.Save(_paths, s2, true);
        Assert.Null(Carica().Progetti);
    }

    [Theory]
    [InlineData("1ABC")]
    [InlineData("REQ PROJ")]
    [InlineData("REQ/PROJ")]
    [InlineData("REQ-PROJ")]
    public void IdProgettoNonValido_RifiutatoAlSalvataggio(string id)
    {
        var s = Carica();
        s.Progetti = new() { new() { Id = id, Nome = "x" } };
        Assert.Throws<InvalidOperationException>(() => PolarionApiSettingsService.Save(_paths, s, true));
        Assert.False(File.Exists(_paths.PolarionApiConfigFile));
    }

    [Fact]
    public void Duplicati_NomeTroppoLungo_ETroppiProgetti_Rifiutati()
    {
        var s = Carica();
        s.Progetti = new() { new() { Id = "REQ_A", Nome = "a" }, new() { Id = "req_a", Nome = "b" } };
        Assert.Throws<InvalidOperationException>(() => PolarionApiSettingsService.Save(_paths, s, true));

        s.Progetti = new() { new() { Id = "REQ_A", Nome = new string('x', PolarionApiSettings.MaxNomeProgetto + 1) } };
        Assert.Throws<InvalidOperationException>(() => PolarionApiSettingsService.Save(_paths, s, true));

        s.Progetti = Enumerable.Range(0, PolarionApiSettings.MaxProgetti + 1)
            .Select(i => new PolarionProgettoConfig { Id = "REQ_" + i, Nome = "n" + i }).ToList();
        Assert.Throws<InvalidOperationException>(() => PolarionApiSettingsService.Save(_paths, s, true));
    }

    [Fact]
    public void RigheVuoteDelleImpostazioni_VengonoIgnorate()
    {
        var s = Carica();
        s.Progetti = new() { new() { Id = "", Nome = "" }, new() { Id = " REQ_OK ", Nome = " Ok " } };
        PolarionApiSettingsService.Save(_paths, s, true);
        var p = Assert.Single(Carica().Progetti!);
        Assert.Equal("REQ_OK", p.Id);
        Assert.Equal("Ok", p.Nome);
    }

    [Fact]
    public void FileVecchioSenzaProgetti_SiLegge_EProgettoEstaPersonalizzatoEPrimo()
    {
        File.WriteAllText(_paths.PolarionApiConfigFile,
            "{\"RevisioneDati\":3,\"ApiRoot\":\"\",\"ProjectId\":\"MIO_TICKET\",\"IntervalloMinuti\":15}");
        var s = Carica();
        Assert.Null(s.ErroreCaricamento);
        Assert.Null(s.Progetti);
        Assert.Equal("MIO_TICKET", s.ProgettiEffettivi().Single().Id);
    }

    [Fact]
    public void ClientVecchio_IgnoraIlCampoProgetti()
    {
        var s = Carica();
        s.Progetti = new() { new() { Id = "REQ_A", Nome = "A" } };
        var json = JsonSerializer.Serialize(s, JsonIo.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("Progetti", out _));
        // Un lettore senza quel campo (client precedente) deve poter deserializzare comunque: campo additivo, nessun cambio di tipo.
        var vecchio = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.Equal("TICKET_Tickets", vecchio["ProjectId"].GetString());
    }

    [Fact]
    public void NonMaster_NonPuoModificareLeImpostazioni()
    {
        var s = Carica();
        s.Progetti = new() { new() { Id = "REQ_A", Nome = "A" } };
        Assert.Throws<UnauthorizedAccessException>(() => PolarionApiSettingsService.Save(_paths, s, false));
    }
}
