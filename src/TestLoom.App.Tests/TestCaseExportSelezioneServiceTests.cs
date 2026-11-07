using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class TestCaseExportSelezioneServiceTests
{
    private static TestCase TestProva()
    {
        var tc = new TestCase
        {
            Id = "TEST-SCELTA", Componente = "BCM", Veicolo = "Daily",
            Funzionalita = ["Guasto"], DescrizioneBreve = "Valutazione guasto",
            StrumentiUsati = ["UnitLogger"], DataCreazione = new DateTime(2026, 11, 6)
        };
        tc.ValoriStrumentiTest["UnitLogger::Versione"] = "9.0";
        tc.ValoriStrumentiTest["UnitLogger::Lingua"] = "IT";
        return tc;
    }

    [Fact]
    public void Descrittore_MostraOgniFoglioELeColonneDellaTabellaReale()
    {
        var catalogo = new[]
        {
            new StrumentoDiagnostico
            {
                Nome = "UnitLogger", Campi =
                [
                    new() { Nome = "Versione", Ambito = TipoAmbitoCampo.PerTest },
                    new() { Nome = "Lingua", Ambito = TipoAmbitoCampo.PerTest }
                ]
            }
        };
        var descrittore = TestCaseExportSelezioneService.CreaDescrittore(catalogo);
        Assert.Equal(TestCaseExportSelezioneService.Pagina, descrittore.Pagina);
        Assert.Contains(descrittore.Sezioni, s => s.Id == "Test"
            && s.Colonne!.Any(c => c.Id == "Descrizione breve"));
        Assert.Contains(descrittore.Sezioni, s => s.Id == "Campi strumenti"
            && s.Colonne!.Any(c => c.Id == "Valore"));
        Assert.Contains(descrittore.Sezioni, s => s.Id == "Campi custom");
        Assert.Contains(descrittore.Sezioni, s => s.Id == "Revisioni");
        Assert.False(descrittore.Sezioni.Single(s => s.Id == "Campi").PredefinitaAttiva);
        var virtuale = Assert.Single(descrittore.Sezioni,
            s => s.Id == TestCaseExportSelezioneService.SingoliCampiStrumento);
        Assert.False(virtuale.PredefinitaAttiva);
        Assert.Contains(virtuale.Colonne!, c => c.Id == "UnitLogger::Versione");
    }

    [Fact]
    public void Selezione_SoloFoglioTestConColonneScelteEliminaLeAltreSezioni()
    {
        using var wb = TestCaseCompleteExportService.CreaWorkbook([TestProva()]);
        var opzioni = new ExportConfigurazione
        {
            Sezioni = ["Test"],
            Colonne = new Dictionary<string, List<string>>
            {
                ["Test"] = ["ID Test", "Descrizione breve", "Componente"]
            }
        };
        TestCaseExportSelezioneService.Applica(wb, opzioni);
        Assert.Single(wb.Worksheets);
        var foglio = wb.Worksheet("Test");
        Assert.Equal(new[] { "ID Test", "Componente", "Descrizione breve" },
            foglio.Row(1).CellsUsed().Select(c => c.GetString()));
        Assert.Equal("TEST-SCELTA", foglio.Cell(2, 1).GetString());
        Assert.Equal("BCM", foglio.Cell(2, 2).GetString());
        Assert.Equal("Valutazione guasto", foglio.Cell(2, 3).GetString());
    }

    [Fact]
    public void SelezioneCampiStrumento_LimitaSoloLeChiaviEsplicitamenteScelte()
    {
        var strumenti = new[]
        {
            new StrumentoDiagnostico
            {
                Nome = "UnitLogger", Campi =
                [
                    new() { Nome = "Versione", Ambito = TipoAmbitoCampo.PerTest },
                    new() { Nome = "Lingua", Ambito = TipoAmbitoCampo.PerTest },
                    new() { Nome = "Licenza", Ambito = TipoAmbitoCampo.PerTest }
                ]
            }
        };
        var descrittore = TestCaseExportSelezioneService.CreaDescrittore(strumenti);
        var opzioni = ExportSelezione.Predefinita(descrittore);
        opzioni.Sezioni.Add(TestCaseExportSelezioneService.SingoliCampiStrumento);
        opzioni.Colonne[TestCaseExportSelezioneService.SingoliCampiStrumento] = ["UnitLogger::Versione"];

        var chiavi = TestCaseExportSelezioneService.ChiaviStrumentoSelezionate(opzioni, descrittore);
        Assert.NotNull(chiavi);
        Assert.Equal("UnitLogger::Versione", Assert.Single(chiavi));
        using var wb = TestCaseCompleteExportService.CreaWorkbook(
            [TestProva()], catalogoStrumenti: strumenti, singoliCampiStrumento: chiavi);
        var righe = wb.Worksheet("Campi strumenti").RowsUsed().Skip(1).ToList();
        Assert.Single(righe);
        Assert.Equal("UnitLogger::Versione", righe[0].Cell(8).GetString());
        Assert.Equal("9.0", righe[0].Cell(9).GetString());
        var catalogoRighe = wb.Worksheet("Definizioni strumenti").RowsUsed().Skip(1).ToList();
        Assert.Single(catalogoRighe);
        Assert.Equal("Versione", catalogoRighe[0].Cell(5).GetString());
        Assert.DoesNotContain(catalogoRighe, r => r.Cell(5).GetString() == "Lingua");
        // La selezione ridotta non modifica i dati del Test sorgente.
        Assert.Equal("IT", TestProva().ValoriStrumentiTest["UnitLogger::Lingua"]);
    }

    [Fact]
    public void SceltaCampiCustom_SupportaChiaviUidDeiTemplateESnapshotStorici()
    {
        var campoA = new CampoTemplate { Nome = "Firmware diagnostico", Tipo = TipoCampoTemplate.CampoLibero };
        var campoB = new CampoTemplate { Nome = "Firmware VCI", Tipo = TipoCampoTemplate.CampoLibero };
        var template = new TestTemplate
        {
            Nome = "Prova comunicazione",
            Sezioni = [new SezioneTemplate { Campi = [campoA, campoB] }]
        };
        var tc = TestProva();
        tc.CampiCustom[campoA.Uid.ToString()] = "SW-A";
        tc.CampiCustom[campoB.Uid.ToString()] = "SW-B";
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Dettagli = new DettagliTestSnapshot
            {
                CampiCustom = { [campoA.Uid.ToString()] = "SW-A-storico", [campoB.Uid.ToString()] = "SW-B-storico" }
            }
        });

        var descrittore = TestCaseExportSelezioneService.CreaDescrittore(catalogoTemplate: [template]);
        var campiCustom = Assert.Single(descrittore.Sezioni,
            sezione => sezione.Id == TestCaseExportSelezioneService.SingoliCampiCustom);
        Assert.False(campiCustom.PredefinitaAttiva);
        Assert.Contains(campiCustom.Colonne!, x => x.Id == campoA.Uid.ToString() && x.Etichetta == "Firmware diagnostico");
        var opzioni = ExportSelezione.Predefinita(descrittore);
        opzioni.Sezioni.Add(TestCaseExportSelezioneService.SingoliCampiCustom);
        opzioni.Colonne[TestCaseExportSelezioneService.SingoliCampiCustom] = [campoA.Uid.ToString()];
        var selezionati = TestCaseExportSelezioneService.ChiaviCustomSelezionate(opzioni, descrittore);

        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc], singoliCampiCustom: selezionati,
            configurazione: opzioni);
        var righe = wb.Worksheet("Campi custom").RowsUsed().Skip(1).ToList();
        Assert.Equal(2, righe.Count);
        Assert.All(righe, r => Assert.Equal(campoA.Uid.ToString(), r.Cell(5).GetString()));
        Assert.Contains(righe, r => r.Cell(6).GetString() == "SW-A-storico");
        Assert.Contains(righe, r => r.Cell(6).GetString() == "SW-A");
        Assert.False(wb.Worksheets.Any(ws => ws.Name == "Campi")); // raw JSON non scelto
    }

    [Fact]
    public void ExportInterattivoNonGeneraDatiRawSeIlFoglioNonESelezionato()
    {
        var descrittore = TestCaseExportSelezioneService.CreaDescrittore();
        var opzioni = ExportSelezione.Predefinita(descrittore);
        Assert.DoesNotContain("Campi", opzioni.Sezioni);
        using var wb = TestCaseCompleteExportService.CreaWorkbook([TestProva()], configurazione: opzioni);
        Assert.DoesNotContain(wb.Worksheets, ws => ws.Name == "Campi");
        TestCaseExportSelezioneService.Applica(wb, opzioni);
        Assert.Contains(wb.Worksheets, ws => ws.Name == "Test");

        // L'opzione esplicita permette ancora l'export integrale di audit.
        opzioni.Sezioni.Add("Campi");
        using var completo = TestCaseCompleteExportService.CreaWorkbook([TestProva()], configurazione: opzioni);
        Assert.Contains(completo.Worksheets, ws => ws.Name == "Campi");
    }

    [Fact]
    public void ColonneEscluse_NonRestanoNeppureNeiFogliDettagliDiTestoLungo()
    {
        var tc = TestProva();
        var testoIncluso = "SICURO-" + new string('s', 40_000);
        var testoDaEscludere = "SEGRETO-" + new string('x', 40_000);
        tc.DescrizioneBreve = testoIncluso;
        tc.Descrizione = testoDaEscludere;
        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc]);
        var opzioni = new ExportConfigurazione
        {
            Sezioni = ["Test"],
            Colonne = new Dictionary<string, List<string>>
            {
                ["Test"] = ["ID Test", "Descrizione breve"]
            }
        };
        TestCaseExportSelezioneService.Applica(wb, opzioni);
        var principale = wb.Worksheet("Test");
        Assert.Contains("Testo esteso", principale.Cell(2, 2).GetString());
        var dettaglio = wb.Worksheets.Single(ws =>
            ws.Name.StartsWith("Test dettagli estesi", StringComparison.OrdinalIgnoreCase));
        var righe = dettaglio.RowsUsed().Skip(1).ToList();
        Assert.NotEmpty(righe);
        Assert.All(righe, r => Assert.Equal("Descrizione breve", r.Cell(2).GetString()));
        var testoRicostruito = string.Concat(righe.Select(r => r.Cell(4).GetString()));
        Assert.Equal(testoIncluso, testoRicostruito);
        Assert.DoesNotContain("SEGRETO-", testoRicostruito);
    }

    [Fact]
    public void SceltePersistite_RestanoDistinteDaiFiltriEDalCatalogoCambiato()
    {
        var descrittore = TestCaseExportSelezioneService.CreaDescrittore();
        var opzioni = ExportSelezione.Predefinita(descrittore);
        opzioni.IgnoraFiltri = true;
        opzioni.Colonne["Test"] = ["ID Test"];
        var copia = opzioni.Clona();
        Assert.True(copia.IgnoraFiltri);
        Assert.Equal(["ID Test"], copia.Colonne["Test"]);
        Assert.Equal(["ID Test"], ExportSelezione.Normalizza(copia, descrittore).Colonne["Test"]);
    }
}
