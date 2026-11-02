using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1): durata manuale per operazione, accessor tipizzato su chiave riservata di ValoriStrumenti,
/// passo +/-5, regola di copia. Null (non nota) e zero (noto) restano distinti.</summary>
public class DurataOperazioniTests
{
    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("   ", true, null)]
    [InlineData("0", true, 0)]
    [InlineData("15", true, 15)]
    [InlineData(" 20 ", true, 20)]
    [InlineData("-5", false, null)]
    [InlineData("1,5", false, null)]
    [InlineData("1.5", false, null)]
    [InlineData("abc", false, null)]
    [InlineData("1000001", false, null)]
    public void TryParse_AccettaSoloInteriNonNegativiOVuoto(string? testo, bool valido, int? atteso)
    {
        Assert.Equal(valido, DurataOperazione.TryParse(testo, out var minuti));
        Assert.Equal(atteso, minuti);
    }

    [Fact]
    public void ZeroEVuoto_SonoValoriDistinti()
    {
        var valori = new Dictionary<string, string>();
        DurataOperazione.Scrivi(valori, 0);
        Assert.Equal(0, DurataOperazione.Leggi(valori));

        DurataOperazione.Scrivi(valori, null);
        Assert.Null(DurataOperazione.Leggi(valori));
        Assert.False(valori.ContainsKey(DurataOperazione.ChiaveValoriStrumenti));
    }

    [Fact]
    public void Scrivi_PreservaLeAltreChiaviDelloStrumento()
    {
        var valori = new Dictionary<string, string>
        {
            ["ValidationTool::Comando (Service)"] = "22",
            ["UnitLogger::Pacchetto"] = "WU-1",
        };

        DurataOperazione.Scrivi(valori, 30);
        Assert.Equal(30, DurataOperazione.Leggi(valori));
        DurataOperazione.Scrivi(valori, null);

        Assert.Equal("22", valori["ValidationTool::Comando (Service)"]);
        Assert.Equal("WU-1", valori["UnitLogger::Pacchetto"]);
        Assert.Equal(2, valori.Count);
    }

    [Fact]
    public void Scrivi_NegativoORiferitoAlLimite_Rifiutato()
    {
        var valori = new Dictionary<string, string>();
        Assert.Throws<ArgumentOutOfRangeException>(() => DurataOperazione.Scrivi(valori, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurataOperazione.Scrivi(valori, DurataOperazione.MassimoMinuti + 1));
        Assert.Empty(valori);
    }

    [Fact]
    public void Leggi_ValoreCorrottoNonDiventaUnNumero()
    {
        var valori = new Dictionary<string, string> { [DurataOperazione.ChiaveValoriStrumenti] = "-5" };
        Assert.Null(DurataOperazione.Leggi(valori));
    }

    [Theory]
    [InlineData(null, 5, 5)]
    [InlineData(null, -5, null)]
    [InlineData(0, 5, 5)]
    [InlineData(0, -5, 0)]
    [InlineData(3, -5, 0)]
    [InlineData(12, 5, 17)]
    [InlineData(12, -5, 7)]
    [InlineData(1_000_000, 5, 1_000_000)]
    public void Passo_NonScendeMaiSottoZeroENonInventaUnoZeroDaVuoto(int? corrente, int passo, int? atteso)
    {
        Assert.Equal(atteso, DurataOperazione.Passo(corrente, passo));
    }

    [Fact]
    public void Condizione_DurataMinuti_NonEmessaSeNonValorizzata()
    {
        var condizione = new Condizione { Funzionalita = "Lettura DID" };
        Assert.Null(condizione.DurataMinuti);

        var senzaDurata = JsonSerializer.Serialize(condizione, JsonIo.Options);
        Assert.DoesNotContain(DurataOperazione.ChiaveValoriStrumenti, senzaDurata);
    }

    [Fact]
    public void Condizione_DurataMinuti_RoundTripJsonConZeroNoto()
    {
        var condizione = new Condizione { Funzionalita = "Lettura DID" };
        condizione.DurataMinuti = 0;

        var json = JsonSerializer.Serialize(condizione, JsonIo.Options);
        Assert.Contains(DurataOperazione.ChiaveValoriStrumenti, json);

        var letta = JsonSerializer.Deserialize<Condizione>(json, JsonIo.Options)!;
        Assert.Equal(0, letta.DurataMinuti);
    }

    [Fact]
    public void ClienteSenzaAccessorTipizzato_RitrasmetteLaChiaveEGliAltriValori()
    {
        const string json = """
            {"Funzionalita":"F1","ValoriStrumenti":{"TEMPO::DurataMinuti":"45","ValidationTool::ADA":"NO"}}
            """;

        var condizione = JsonSerializer.Deserialize<Condizione>(json, JsonIo.Options)!;
        Assert.Equal(45, condizione.DurataMinuti);

        var riscritta = JsonSerializer.Serialize(condizione, JsonIo.Options);
        Assert.Contains("\"TEMPO::DurataMinuti\": \"45\"", riscritta);
        Assert.Contains("\"ValidationTool::ADA\": \"NO\"", riscritta);
    }

    [Fact]
    public void HaValoriStrumenti_LaSolaDurataNonEUnDatoStrumento()
    {
        var soloDurata = new Dictionary<string, string> { [DurataOperazione.ChiaveValoriStrumenti] = "15" };
        var conStrumento = new Dictionary<string, string> { ["ValidationTool::ADA"] = "NO", [DurataOperazione.ChiaveValoriStrumenti] = "15" };
        Assert.False(DurataOperazione.HaValoriStrumenti(soloDurata));
        Assert.True(DurataOperazione.HaValoriStrumenti(conStrumento));
        Assert.False(DurataOperazione.HaValoriStrumenti(null));
    }

    [Fact]
    public void CopiaSenzaDurata_RimuoveLaSoleDurataEConservaGliAltriValori()
    {
        var sorgente = new Dictionary<string, string>
        {
            ["ValidationTool::ADA"] = "NO",
            [DurataOperazione.ChiaveValoriStrumenti] = "45",
        };

        var copia = DurataOperazione.CopiaSenzaDurata(sorgente);

        Assert.Single(copia);
        Assert.Equal("NO", copia["ValidationTool::ADA"]);
        Assert.Equal(2, sorgente.Count);                    // l'originale non viene toccato
        Assert.Empty(DurataOperazione.CopiaSenzaDurata(null));
    }

    [Fact]
    public void TestoTicket_NonRiportaLaDurataComeCampoStrumento()
    {
        var sb = new System.Text.StringBuilder();
        var valori = new Dictionary<string, string>
        {
            ["ValidationTool::ADA"] = "NO",
            [DurataOperazione.ChiaveValoriStrumenti] = "45",
        };

        TicketExportService.AppendValoriStrumenti(sb, valori);

        var testo = sb.ToString();
        Assert.Contains("NO", testo);
        Assert.DoesNotContain("TEMPO", testo);
        Assert.DoesNotContain("45", testo);
    }

    [Fact]
    public void DatiUtente_LaSolaDurataNonContaComeDatoInserito()
    {
        var condizione = new Condizione { Funzionalita = "F" };
        condizione.DurataMinuti = 15;
        Assert.Empty(DtcOperazioneRegole.DatiUtente(condizione));
    }

    [Fact]
    public void SceltaCopia_PreferisceLoStessoVeicoloPiuRecente()
    {
        var osservazioni = new[]
        {
            new DurataOperazione.OsservazioneDurata(20, "Daily", new DateTime(2026, 1, 1), "TC-1", 0, 0),
            new DurataOperazione.OsservazioneDurata(10, "Daily", new DateTime(2026, 2, 1), "TC-2", 0, 0),
            new DurataOperazione.OsservazioneDurata(99, "Pickup", new DateTime(2026, 3, 1), "TC-3", 0, 0),
        };
        Assert.Equal(10, DurataOperazione.SceltaCopia(osservazioni, "Daily"));
    }

    [Fact]
    public void SceltaCopia_RicadeSuUnAltroVeicoloSoloSeNessunoStesso()
    {
        var osservazioni = new[]
        {
            new DurataOperazione.OsservazioneDurata(20, "Daily", new DateTime(2026, 1, 1), "TC-1", 0, 0),
            new DurataOperazione.OsservazioneDurata(99, "Pickup", new DateTime(2026, 3, 1), "TC-3", 0, 0),
        };
        Assert.Equal(99, DurataOperazione.SceltaCopia(osservazioni, "Van"));
    }

    [Fact]
    public void SceltaCopia_SenzaOsservazioniRestaVuoto()
    {
        Assert.Null(DurataOperazione.SceltaCopia(Array.Empty<DurataOperazione.OsservazioneDurata>(), "Daily"));
    }

    [Fact]
    public void SceltaCopia_ZeroNotoEUnValoreCopiabile()
    {
        var osservazioni = new[]
        {
            new DurataOperazione.OsservazioneDurata(0, "Daily", new DateTime(2026, 4, 1), "TC-1", 0, 0),
        };
        Assert.Equal(0, DurataOperazione.SceltaCopia(osservazioni, "Daily"));
    }

    [Fact]
    public void SceltaCopia_ParitaDiDataUsaTestIdRevisioneETentativo()
    {
        var stessaData = new DateTime(2026, 5, 1);
        var osservazioni = new[]
        {
            new DurataOperazione.OsservazioneDurata(20, "Daily", stessaData, "TC-1", 2, 0),
            new DurataOperazione.OsservazioneDurata(30, "Daily", stessaData, "TC-2", 0, 0),
            new DurataOperazione.OsservazioneDurata(40, "Daily", stessaData, "TC-2", 1, 3),
        };
        // Ordine del Report: Id ordinale poi revisione poi tentativo => TC-2 rev 1 e' il piu' recente.
        Assert.Equal(40, DurataOperazione.SceltaCopia(osservazioni, "daily"));
    }
}
