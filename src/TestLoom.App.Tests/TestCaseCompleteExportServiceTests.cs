using System.Text.Json;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Excel trasversale Test: dati persistiti, storici e campi futuri, anche fuori dalla pagina corrente.</summary>
public sealed class TestCaseCompleteExportServiceTests
{
    private static TestCase CreaTest(string id)
    {
        var tc = new TestCase
        {
            Id = id, DataCreazione = new DateTime(2026, 11, 6),
            DescrizioneBreve = "Diagnosi guasto", Descrizione = "Prova eseguita su componente reale",
            StatoLavoro = StatoLavoroTest.InCorso,
            Veicolo = "Daily MY24", Componente = "BCM",
            RequisitoId = ["REQ-004"], Funzionalita = ["Diagnosi guasto"],
            StrumentiUsati = ["UnitLogger", "CaptureTool"],
            Assegnatario = "Operatore QA"
        };
        tc.CampiCustom["Sorgente"] = "Test manuale";
        tc.CampiCustom["Nome.a[0]"] = "Campo con delimitatori";
        tc.ValoriStrumentiTest["UnitLogger::Versione"] = "9.0";
        tc.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["CampoFuturo"] = JsonSerializer.SerializeToElement(new { SottoCampo = "nuova versione" })
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = new DateTime(2026, 11, 6),
            Operatore = "Operatore QA", Esito = Esito.NonConforme,
            Matricola = "ZCFC0000000000001", BaselineSw = "SW-01", BaselineHw = "HW-01",
            Note = "Prima esecuzione", ValoriStrumenti = { ["UnitLogger::Snapshot REV"] = "REV-1" },
            Condizioni =
            {
                new Condizione
                {
                    Strumento = "UnitLogger", Funzionalita = "Diagnosi guasto", Componente = "BCM",
                    Guasto = "P0100", DtcPasso = PassoDtc.Validazione,
                    Esito = Esito.NonConforme, Nota = "Guasto riprodotto",
                    ValoriStrumenti = { ["UnitLogger::Risposta"] = "7F 19 22" }
                }
            }
        });
        return tc;
    }

    [Fact]
    public void Workbook_ContieneTuttiIFogliELeOperazioniDiOgniRevisione()
    {
        var tc = CreaTest("TEST-101");
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 2, Data = new DateTime(2026, 11, 7), Esito = Esito.Conforme,
            Condizioni = { new Condizione { Esito = Esito.Conforme, Nota = "Riprova conforme" } }
        });
        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc]);
        Assert.Contains("Guida", wb.Worksheets.Select(x => x.Name));
        Assert.Contains("Test", wb.Worksheets.Select(x => x.Name));
        Assert.Contains("Revisioni", wb.Worksheets.Select(x => x.Name));
        Assert.Contains("Operazioni", wb.Worksheets.Select(x => x.Name));
        Assert.Contains("Campi", wb.Worksheets.Select(x => x.Name));
        Assert.Contains("Campi strumenti", wb.Worksheets.Select(x => x.Name));
        Assert.Equal(2, wb.Worksheet("Test").RowsUsed().Count());
        Assert.Equal(3, wb.Worksheet("Revisioni").RowsUsed().Count());
        Assert.Equal(3, wb.Worksheet("Operazioni").RowsUsed().Count());
        Assert.Contains(wb.Worksheet("Test").RowsUsed().Skip(1), r => r.Cell(1).GetString() == "TEST-101");
        Assert.Contains(wb.Worksheet("Operazioni").RowsUsed().Skip(1), r => r.Cell(9).GetString() == "P0100");
    }

    [Fact]
    public void CampiCompleti_NonPerdonoStrumentiCustomSnapshotNidoEDatiFuturi()
    {
        var tc = CreaTest("TEST-102");
        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc]);
        var dati = wb.Worksheet("Campi").RowsUsed().Skip(1)
            .ToDictionary(r => r.Cell(3).GetString(), r => r.Cell(5).GetString(), StringComparer.Ordinal);
        Assert.Equal("9.0", dati["$[\"ValoriStrumentiTest\"][\"UnitLogger::Versione\"]"]);
        Assert.Equal("Test manuale", dati["$[\"CampiCustom\"][\"Sorgente\"]"]);
        Assert.Equal("Campo con delimitatori", dati["$[\"CampiCustom\"][\"Nome.a[0]\"]"]);
        Assert.Equal("REV-1", dati["$[\"Esecuzioni\"][0][\"ValoriStrumenti\"][\"UnitLogger::Snapshot REV\"]"]);
        Assert.Equal("7F 19 22", dati["$[\"Esecuzioni\"][0][\"Condizioni\"][0][\"ValoriStrumenti\"][\"UnitLogger::Risposta\"]"]);
        Assert.Equal("nuova versione", dati["$[\"CampoFuturo\"][\"SottoCampo\"]"]);
        Assert.Equal("P0100", dati["$[\"Esecuzioni\"][0][\"Condizioni\"][0][\"Guasto\"]"]);
    }

    [Fact]
    public void ExportGenerale_IncludeCampiCustomStoriciTicketAllegatiPunteggiECronologia()
    {
        var tc = CreaTest("TEST-TRACCIABILITA");
        var allegatoUid = Guid.NewGuid();
        tc.Allegati.Add(new Allegato
        {
            Uid = allegatoUid, NomeFile = "trace_2026.asc", CaricatoDa = "QA",
            DataCaricamento = new DateTime(2026, 11, 6), DimensioneByte = 2048,
            Sha256 = "digest", Descrizione = "Tracciato prova"
        });
        tc.Esecuzioni[0].Condizioni[0].ValoriStrumenti[EvidenzaOperazione.ChiaveValoriStrumenti]
            = allegatoUid.ToString("D");
        tc.TicketCollegati.Add(new TicketRif
        {
            Riferimento = "TICKET-100", Url = "https://example.invalid/ticket100",
            CollegatoDa = "QA", CollegatoIl = new DateTime(2026, 11, 6),
            Note = "Riferimento al problema"
        });
        tc.Changelog.Add(new ChangelogEntry
        {
            Data = new DateTime(2026, 11, 6), Operatore = "QA", Campo = "Stato",
            ValorePrecedente = "Pianificato", ValoreNuovo = "InCorso"
        });
        tc.NotePianificazione.Add(new NotaPianificazione
        {
            Data = new DateTime(2026, 11, 6), Operatore = "Master",
            CampiModificati = "Scadenza", Nota = "Posticipo per test di banco"
        });
        tc.Esecuzioni[0].PunteggiRequisiti.Add(new PunteggioRequisito
        {
            RequisitoId = "REQ-004", Sovrascritto = true, MotivazioneOverride = "Analisi del responsabile"
        });
        tc.Esecuzioni[0].Dettagli = new DettagliTestSnapshot
        {
            CampiCustom = { ["Campo storico"] = "REV 1" }
        };

        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc]);
        Assert.Contains(wb.Worksheet("Campi custom").RowsUsed().Skip(1),
            r => r.Cell(3).GetString() == "Test corrente" && r.Cell(5).GetString() == "Sorgente"
                 && r.Cell(6).GetString() == "Test manuale");
        Assert.Contains(wb.Worksheet("Campi custom").RowsUsed().Skip(1),
            r => r.Cell(3).GetString() == "Snapshot Test nella REV"
                 && r.Cell(5).GetString() == "Campo storico" && r.Cell(6).GetString() == "REV 1");
        Assert.Contains(wb.Worksheet("Punteggi requisiti").RowsUsed().Skip(1),
            r => r.Cell(4).GetString() == "REQ-004"
                 && r.Cell(7).GetString() == "Analisi del responsabile");
        Assert.Contains(wb.Worksheet("Allegati").RowsUsed().Skip(1),
            r => r.Cell(3).GetString() == allegatoUid.ToString("D")
                 && r.Cell(4).GetString() == "trace_2026.asc"
                 && r.Cell(11).GetString().Contains("REV 1"));
        Assert.Contains(wb.Worksheet("Ticket").RowsUsed().Skip(1),
            r => r.Cell(3).GetString() == "TICKET-100");
        Assert.Contains(wb.Worksheet("Cronologia Test").RowsUsed().Skip(1),
            r => r.Cell(6).GetString() == "Pianificato"
                 && r.Cell(7).GetString() == "InCorso");
        Assert.Contains(wb.Worksheet("Note pianificazione").RowsUsed().Skip(1),
            r => r.Cell(5).GetString() == "Scadenza"
                 && r.Cell(6).GetString() == "Posticipo per test di banco");
    }

    [Fact]
    public void ReportTest_NomeTipoLeggibileEMantieneQuelloStoricoDellaRevisione()
    {
        var tc = CreaTest("TEST-TIPO");
        tc.TestTemplateUid = Guid.NewGuid();
        tc.Esecuzioni[0].Dettagli = new DettagliTestSnapshot
        {
            TestTemplateUid = tc.TestTemplateUid,
            TestTemplateNome = "Validazione banco prima del rename"
        };
        var catalogo = new[]
        {
            new TestTemplate { Uid = tc.TestTemplateUid, Nome = "Validazione diagnostica" }
        };
        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc], catalogoTipiTest: catalogo);
        Assert.Equal("Validazione diagnostica", wb.Worksheet("Test").Cell(2, 4).GetString());
        Assert.Equal("Validazione banco prima del rename", wb.Worksheet("Revisioni").Cell(2, 21).GetString());

        // Se il tipo e' stato cancellato dal catalogo, l'UID resta comunque in export:
        // il foglio non deve inventare un nome usando quello del Test attuale.
        using var senzaCatalogo = TestCaseCompleteExportService.CreaWorkbook([tc]);
        Assert.Equal("", senzaCatalogo.Worksheet("Test").Cell(2, 4).GetString());
        Assert.Equal("Validazione banco prima del rename",
            senzaCatalogo.Worksheet("Revisioni").Cell(2, 21).GetString());
    }

    [Fact]
    public void CampiStrumenti_ConCatalogoEsportaDefinizioniValoriEVuotiSenzaApplicareIlDefault()
    {
        var tc = CreaTest("TEST-CATALOGO");
        var catalogo = new List<StrumentoDiagnostico>
        {
            new()
            {
                Nome = "UnitLogger", Tipo = TipoStrumento.DaTestare, Ordine = 2,
                Campi =
                [
                    new() { Nome = "Versione", Ambito = TipoAmbitoCampo.PerTest, Tipo = TipoCampoStrumento.Testo,
                            Gruppo = "Configurazione", ValoreDefault = "10.0", Ordine = 1 },
                    new() { Nome = "Lingua", Ambito = TipoAmbitoCampo.PerTest, Tipo = TipoCampoStrumento.Lista,
                            OpzioniLista = ["IT", "EN"], ValoreDefault = "IT", Ordine = 2 },
                    new() { Nome = "Snapshot REV", Ambito = TipoAmbitoCampo.PerRevisione, Tipo = TipoCampoStrumento.Testo },
                    new() { Nome = "Temperatura", Ambito = TipoAmbitoCampo.PerRevisione, Tipo = TipoCampoStrumento.Numero,
                            UnitaMisura = "°C" },
                    new() { Nome = "Risposta", Ambito = TipoAmbitoCampo.PerOperazione, Tipo = TipoCampoStrumento.Testo },
                    new() { Nome = "Comando", Ambito = TipoAmbitoCampo.PerOperazione, Tipo = TipoCampoStrumento.Testo,
                            ValoreDefault = "22 F1 90" }
                ]
            },
            new() { Nome = "CaptureTool", Archiviata = true }
        };

        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc], catalogoStrumenti: catalogo);
        var righe = wb.Worksheet("Campi strumenti").RowsUsed().Skip(1).ToList();

        var lingua = Assert.Single(righe, r => r.Cell(3).GetString() == "Test corrente" &&
                                            r.Cell(7).GetString() == "Lingua");
        Assert.Equal("", lingua.Cell(9).GetString());
        Assert.Equal("PerTest", lingua.Cell(10).GetString());
        Assert.Equal("Lista", lingua.Cell(11).GetString());
        Assert.Contains("IT", lingua.Cell(14).GetString());
        Assert.Contains("EN", lingua.Cell(14).GetString());
        Assert.Equal("IT", lingua.Cell(15).GetString());
        Assert.Equal("Non compilato", lingua.Cell(16).GetString());
        Assert.Equal("Definito nel catalogo corrente", lingua.Cell(17).GetString());
        Assert.Equal("Sì", lingua.Cell(18).GetString());

        var versione = Assert.Single(righe, r => r.Cell(3).GetString() == "Test corrente" &&
                                              r.Cell(7).GetString() == "Versione");
        Assert.Equal("9.0", versione.Cell(9).GetString());
        Assert.Equal("10.0", versione.Cell(15).GetString());
        Assert.Equal("Compilato", versione.Cell(16).GetString());
        Assert.Equal("Configurazione", versione.Cell(12).GetString());

        var temp = Assert.Single(righe, r => r.Cell(3).GetString() == "Revisione" &&
                                          r.Cell(7).GetString() == "Temperatura");
        Assert.Equal("Non compilato", temp.Cell(16).GetString());
        Assert.Equal("°C", temp.Cell(13).GetString());

        var comando = Assert.Single(righe, r => r.Cell(3).GetString() == "Operazione" &&
                                             r.Cell(7).GetString() == "Comando");
        Assert.Equal("", comando.Cell(9).GetString());
        Assert.Equal("22 F1 90", comando.Cell(15).GetString());
        Assert.Equal("Non compilato", comando.Cell(16).GetString());

        var definizioni = wb.Worksheet("Definizioni strumenti").RowsUsed().Skip(1).ToList();
        Assert.Contains(definizioni, r => r.Cell(1).GetString() == "UnitLogger" &&
                                          r.Cell(5).GetString() == "Temperatura" &&
                                          r.Cell(9).GetString() == "°C");
        Assert.Contains(definizioni, r => r.Cell(1).GetString() == "CaptureTool" &&
                                          r.Cell(13).GetString() == "Strumento senza campi definiti");
        Assert.DoesNotContain(righe, r => r.Cell(3).GetString() == "Test corrente" &&
                                          r.Cell(7).GetString() == "Temperatura");
    }

    [Fact]
    public void CampiStrumenti_ConservaChiaviNonCatalogateEStrumentiRimossi()
    {
        var tc = CreaTest("TEST-LEGACY");
        tc.StrumentiUsati.Add("Tool rimosso");
        tc.ValoriStrumentiTest["Tool rimosso::Legacy"] = "valore storico";
        tc.ValoriStrumentiTest["UnitLogger::Opzione cancellata"] = "da preservare";
        var catalogo = new List<StrumentoDiagnostico>
        {
            new() { Nome = "UnitLogger", Campi = [new() { Nome = "Versione", Ambito = TipoAmbitoCampo.PerTest }] },
            new() { Nome = "CaptureTool" }
        };

        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc], catalogoStrumenti: catalogo);
        var righe = wb.Worksheet("Campi strumenti").RowsUsed().Skip(1).ToList();
        Assert.Contains(righe, r => r.Cell(7).GetString() == "Legacy" &&
                                    r.Cell(9).GetString() == "valore storico" &&
                                    r.Cell(17).GetString() == "Strumento non presente nel catalogo");
        Assert.Contains(righe, r => r.Cell(7).GetString() == "Opzione cancellata" &&
                                    r.Cell(9).GetString() == "da preservare" &&
                                    r.Cell(17).GetString() == "Campo non piu' definito");
        Assert.Contains(wb.Worksheet("Definizioni strumenti").RowsUsed().Skip(1),
            r => r.Cell(1).GetString() == "Tool rimosso" &&
                 r.Cell(13).GetString() == "Strumento non presente nel catalogo");
    }

    [Fact]
    public void CampiStrumenti_RevisioniStoricheUsanoStrumentiDelloSnapshotNonQuelliDiOggi()
    {
        var tc = CreaTest("TEST-REV-STRUMENTI");
        tc.StrumentiUsati.Clear();
        tc.StrumentiUsati.Add("UnitLogger");
        tc.Esecuzioni[0].Dettagli = new DettagliTestSnapshot
        {
            StrumentiUsati = ["CaptureTool"],
            ValoriStrumentiTest = { ["CaptureTool::Versione"] = "18.0" }
        };
        tc.Esecuzioni[0].ValoriStrumenti["CaptureTool::Canale"] = "CAN1";
        var catalogo = new List<StrumentoDiagnostico>
        {
            new() { Nome = "UnitLogger", Campi = [new() { Nome = "Lingua", Ambito = TipoAmbitoCampo.PerTest }] },
            new() { Nome = "CaptureTool", Campi =
                [
                    new() { Nome = "Versione", Ambito = TipoAmbitoCampo.PerTest },
                    new() { Nome = "Canale", Ambito = TipoAmbitoCampo.PerRevisione },
                    new() { Nome = "Baudrate", Ambito = TipoAmbitoCampo.PerRevisione, UnitaMisura = "bit/s" }
                ]
            }
        };
        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc], catalogoStrumenti: catalogo);
        var righe = wb.Worksheet("Campi strumenti").RowsUsed().Skip(1).ToList();
        Assert.Contains(righe, r => r.Cell(3).GetString() == "Snapshot Test nella REV" &&
                                    r.Cell(7).GetString() == "Versione" && r.Cell(9).GetString() == "18.0");
        Assert.Contains(righe, r => r.Cell(3).GetString() == "Revisione" &&
                                    r.Cell(7).GetString() == "Baudrate" &&
                                    r.Cell(16).GetString() == "Non compilato");
        Assert.DoesNotContain(righe, r => r.Cell(3).GetString() == "Snapshot Test nella REV" &&
                                          r.Cell(7).GetString() == "Lingua");
    }

    [Fact]
    public void Operazioni_EsportaTentativiRipetutiDurataEdEvidenzeSenzaSpacciareMetadatiPerCampiStrumento()
    {
        var tc = CreaTest("TEST-TENTATIVI");
        var revisione = tc.Esecuzioni[0];
        var primo = revisione.Condizioni[0];
        primo.ValoriStrumenti[DurataOperazione.ChiaveValoriStrumenti] = "12";
        var uidAllegato = Guid.NewGuid();
        primo.ValoriStrumenti[EvidenzaOperazione.ChiaveValoriStrumenti] = uidAllegato.ToString("D");
        var ripetizione = new Condizione
        {
            OperazioneOrigineUid = primo.Uid,
            Funzionalita = primo.Funzionalita,
            Esito = Esito.Conforme, Nota = "Ripetizione corretta",
            Strumento = "UnitLogger",
            ValoriStrumenti = { [DurataOperazione.ChiaveValoriStrumenti] = "5" }
        };
        revisione.Condizioni.Add(ripetizione);

        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc]);
        var righe = wb.Worksheet("Operazioni").RowsUsed().Skip(1).ToList();
        Assert.Equal(2, righe.Count);
        Assert.Equal("1", righe[0].Cell(20).GetString());
        Assert.Equal("No", righe[0].Cell(21).GetString());
        Assert.Equal("Sì", righe[1].Cell(21).GetString());
        Assert.Equal("12", righe[0].Cell(22).GetString());
        Assert.Equal("1", righe[0].Cell(23).GetString());
        Assert.Equal(uidAllegato.ToString("D"), righe[0].Cell(24).GetString());
        Assert.Equal("2", righe[1].Cell(20).GetString());
        Assert.Equal("5", righe[1].Cell(22).GetString());
        Assert.DoesNotContain(wb.Worksheet("Campi strumenti").RowsUsed().Skip(1),
            r => r.Cell(6).GetString() is "TEMPO" or "EVIDENZE");
        Assert.Contains(wb.Worksheet("Campi").RowsUsed().Skip(1),
            r => r.Cell(3).GetString().Contains(DurataOperazione.ChiaveValoriStrumenti));
    }

    [Fact]
    public void CampiStrumenti_SonoConsultabiliSeparatamentePerTestRevisioneEOperazione()
    {
        var tc = CreaTest("TEST-105");
        tc.Esecuzioni[0].Dettagli = new DettagliTestSnapshot
        {
            ValoriStrumentiTest = new Dictionary<string, string> { ["UnitLogger::Versione storica"] = "8.5" }
        };
        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc]);
        var righe = wb.Worksheet("Campi strumenti").RowsUsed().Skip(1).ToList();
        Assert.Contains(righe, r => r.Cell(3).GetString() == "Test corrente"
                                  && r.Cell(6).GetString() == "UnitLogger" && r.Cell(7).GetString() == "Versione"
                                  && r.Cell(9).GetString() == "9.0");
        Assert.Contains(righe, r => r.Cell(3).GetString() == "Revisione"
                                  && r.Cell(7).GetString() == "Snapshot REV");
        Assert.Contains(righe, r => r.Cell(3).GetString() == "Operazione"
                                  && r.Cell(7).GetString() == "Risposta");
        Assert.Contains(righe, r => r.Cell(3).GetString() == "Snapshot Test nella REV"
                                  && r.Cell(7).GetString() == "Versione storica"
                                  && r.Cell(9).GetString() == "8.5");
    }

    [Fact]
    public void ExportSuDisco_Atomico_EProtezioneTestoExcelCheIniziaComeFormula()
    {
        var path = Path.Combine(Path.GetTempPath(), "TestLoom_TestExport_" + Guid.NewGuid().ToString("N") + ".xlsx");
        var tc = CreaTest("TEST-103");
        tc.CampiCustom["ValoreFormula"] = "=1+1";
        try
        {
            TestCaseCompleteExportService.Esporta(path, [tc]);
            using var wb = new XLWorkbook(path);
            var value = wb.Worksheet("Campi").RowsUsed().Skip(1)
                .Single(r => r.Cell(3).GetString() == "$[\"CampiCustom\"][\"ValoreFormula\"]")
                .Cell(5);
            Assert.Equal("=1+1", value.GetString());
            Assert.False(value.HasFormula);
            Assert.True(value.Style.IncludeQuotePrefix);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void CampiMoltoLunghi_NonVengonoTroncatiDaExcel()
    {
        var tc = CreaTest("TEST-LUNGO");
        var valore = string.Concat(Enumerable.Repeat("diagnosi😀", 5_000));
        tc.ValoriStrumentiTest["UnitLogger::Risposta completa"] = valore;
        using var wb = TestCaseCompleteExportService.CreaWorkbook([tc]);
        var principale = wb.Worksheet("Campi strumenti").RowsUsed().Skip(1)
            .Single(r => r.Cell(7).GetString() == "Risposta completa");
        Assert.Contains("Testo esteso", principale.Cell(9).GetString());
        var righe = wb.Worksheets.Where(w => w.Name.StartsWith("Campi strumenti dettagli", StringComparison.Ordinal))
            .SelectMany(w => w.RowsUsed().Skip(1)).Where(r => r.Cell(2).GetString() == "Valore")
            .OrderBy(r => int.Parse(r.Cell(3).GetString().Split('/')[0])).ToList();
        Assert.NotEmpty(righe);
        Assert.Equal(valore, string.Concat(righe.Select(r => r.Cell(4).GetString())));
    }

    [Fact]
    public void Annullamento_NonLasciaUnExcelParziale()
    {
        var path = Path.Combine(Path.GetTempPath(), "TestLoom_TestExport_Cancel_" + Guid.NewGuid().ToString("N") + ".xlsx");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => TestCaseCompleteExportService.Esporta(path, [CreaTest("TEST-104")], cts.Token));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ExportNonEUnaPagina_LaSorgenteCompletaEEnumerataUnaVolta()
    {
        var source = Enumerable.Range(0, 63).Select(i => CreaTest($"TEST-{i:D5}")).ToArray();
        using var wb = TestCaseCompleteExportService.CreaWorkbook(source);
        var rows = wb.Worksheet("Test").RowsUsed().Skip(1).ToArray();
        Assert.Equal(63, rows.Length);
        Assert.Contains(rows, r => r.Cell(1).GetString() == "TEST-00062");
    }
}
