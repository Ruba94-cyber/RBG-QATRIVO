using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class ExportConfigurazioniServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ExportConfig_" + Guid.NewGuid().ToString("N"));
    private readonly AppState _state;

    public ExportConfigurazioniServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
    }

    public void Dispose() { _state.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private static ExportConfigurazione Cfg(string nome, params string[] sezioni) => new() { Nome = nome, Sezioni = sezioni.ToList() };

    [Fact]
    public void SalvaERicarica_ConservaSezioniColonneEUltimaUsata()
    {
        var c = Cfg("Manager", "vista", "pivot");
        c.Colonne["vista"] = ["ID", "Stato", "Veicoli"];
        Assert.Equal(EsitoSalvataggioExport.Salvata, ExportConfigurazioniService.Salva(_state, "anomalie", c));

        var letta = ExportConfigurazioniService.Carica(_state, "anomalie");
        Assert.Equal("Manager", letta.Ultima);
        var uscita = Assert.Single(letta.Configurazioni);
        Assert.True(uscita.HaSezione("PIVOT"));
        Assert.Equal(new[] { "ID", "Stato", "Veicoli" }, uscita.ColonneDi("VISTA"));
        Assert.Null(uscita.ColonneDi("dati"));
        Assert.Empty(ExportConfigurazioniService.Carica(_state, "report").Configurazioni);
    }

    [Fact]
    public void StessoNome_Sostituisce_ENuovoOltreLeSei_VieneRifiutato()
    {
        for (var i = 1; i <= ExportConfigurazioniService.Massimo; i++)
            Assert.Equal(EsitoSalvataggioExport.Salvata, ExportConfigurazioniService.Salva(_state, "report", Cfg("C" + i, "dati")));
        Assert.Equal(EsitoSalvataggioExport.LimiteRaggiunto, ExportConfigurazioniService.Salva(_state, "report", Cfg("Settima", "dati")));
        Assert.Equal(EsitoSalvataggioExport.Salvata, ExportConfigurazioniService.Salva(_state, "report", Cfg("c3", "vista", "info")));

        var lette = ExportConfigurazioniService.Carica(_state, "report");
        Assert.Equal(6, lette.Configurazioni.Count);
        Assert.Equal(new[] { "vista", "info" }, lette.Configurazioni.Single(c => c.Nome == "c3").Sezioni);
    }

    [Fact]
    public void NomiNonValidi_EliminaEUltima()
    {
        Assert.Equal(EsitoSalvataggioExport.NomeNonValido, ExportConfigurazioniService.Salva(_state, "nc", Cfg("   ", "dati")));
        Assert.Equal(EsitoSalvataggioExport.NomeNonValido, ExportConfigurazioniService.Salva(_state, "nc", Cfg(new string('x', 41), "dati")));
        ExportConfigurazioniService.Salva(_state, "nc", Cfg("A", "dati"));
        ExportConfigurazioniService.Salva(_state, "nc", Cfg("B", "vista"));
        ExportConfigurazioniService.ImpostaUltima(_state, "nc", "A");
        Assert.Equal("A", ExportConfigurazioniService.Carica(_state, "nc").UltimaUsata?.Nome);

        Assert.True(ExportConfigurazioniService.Elimina(_state, "nc", "a"));
        Assert.False(ExportConfigurazioniService.Elimina(_state, "nc", "A"));
        var dopo = ExportConfigurazioniService.Carica(_state, "nc");
        Assert.Null(dopo.UltimaUsata);
        Assert.Equal("B", Assert.Single(dopo.Configurazioni).Nome);
    }

    [Fact]
    public void ContenutoDanneggiato_EquivaleANessunaConfigurazione()
    {
        ViewStateService.Scrivi(_state, "Export.Configurazioni.report", "{non json");
        var letta = ExportConfigurazioniService.Carica(_state, "report");
        Assert.Empty(letta.Configurazioni);
        Assert.Equal(EsitoSalvataggioExport.Salvata, ExportConfigurazioniService.Salva(_state, "report", Cfg("Nuova", "dati")));
    }
    [Fact]
    public void ColonneEsplicite_MantengonoOrdineAncheQuandoSonoTutte()
    {
        var descriptor = new ExportDescrittore("test", "Export",
            [new ExportSezione("vista", "Vista",
                [new ExportColonna("id", "ID"), new ExportColonna("stato", "Stato"), new ExportColonna("veicolo", "Veicolo")])], "");
        var config = Cfg("Ordine", "vista");
        config.Colonne["vista"] = ["veicolo", "id", "stato"];

        var normalizzata = ExportSelezione.Normalizza(config, descriptor);
        Assert.Equal(new[] { "veicolo", "id", "stato" }, normalizzata.ColonneDi("vista"));
        Assert.Equal(new[] { "veicolo", "id", "stato" }, ExportSelezione.ColonneEsportate(normalizzata, descriptor.Sezioni[0]));
        Assert.Equal(new[] { 2, 0, 1 },
            ExportColonneSelezione.Indici(["id", "stato", "veicolo"], normalizzata.ColonneDi("vista")));

        ExportConfigurazioniService.Salva(_state, "anomalie-nc", normalizzata);
        var riletta = Assert.Single(ExportConfigurazioniService.Carica(_state, "anomalie-nc").Configurazioni);
        Assert.Equal(new[] { "veicolo", "id", "stato" }, riletta.ColonneDi("vista"));
        Assert.Empty(ExportConfigurazioniService.Carica(_state, "anomalie-cr").Configurazioni);
    }

    [Fact]
    public void ColonneDupeOInesistenti_NonRipetonoColonne()
    {
        Assert.Equal(new[] { 2, 0 },
            ExportColonneSelezione.Indici(["id", "stato", "veicolo"],
                ["veicolo", "non-esiste", "id", "veicolo"]));
        Assert.Equal(new[] { 0, 1, 2 },
            ExportColonneSelezione.Indici(["id", "stato", "veicolo"], ["non-esiste"]));
    }

}
