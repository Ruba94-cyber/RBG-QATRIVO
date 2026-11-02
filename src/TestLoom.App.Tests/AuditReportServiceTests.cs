using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>AuditReportService esporta la matrice completa Requisito×componente per sottoveicolo (Addendum:
/// grill-me 2026-08-18, follow-up, punto 5): un foglio per Sottoveicolo più "Legenda", solo numeri puri o
/// N/A nelle celle (mai gli stati mostrati in-app). Riusa il dataset di semina come ReportGenerationTests,
/// per esercitare l'export su dati realistici invece che su un fixture sintetico troppo pulito.</summary>
[Trait("Category", "Lento")]
public class AuditReportServiceTests : IDisposable
{
    [Fact]
    public void DettaglioDtc_SuddivideLeRigheSenzaPerderle()
    {
        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var righe = Enumerable.Range(0, 5).Select(i => new DtcAvanzamento("R1", $"P{i}", "ECM", StatoDtc.DaFare, null, null, null, null, "", "", false)).ToList();
        AuditReportService.ScriviFoglioDettaglioDtc(workbook, righe, massimoRighePerFoglio: 2);
        Assert.Equal(3, workbook.Worksheets.Count);
        Assert.Equal(new[] { "P0", "P1", "P2", "P3", "P4" }, workbook.Worksheets
            .SelectMany(s => s.RowsUsed().Skip(1).Select(r => r.Cell(2).GetString())).ToArray());
    }
    private readonly string _dir;
    private readonly AppState _state;

    public AuditReportServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomAuditReportTests_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        _state.InizializzaCartella(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void GeneraExcel_SulDatasetCompleto_NonLanciaEProduceUnFileNonVuoto()
    {
        var bytes = AuditReportService.GeneraExcel(_state);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void GeneraExcel_HaUnFoglioLegendaConLaScalaPunteggio()
    {
        var bytes = AuditReportService.GeneraExcel(_state);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var legenda = workbook.Worksheet("Legenda");

        var righe = legenda.RowsUsed().ToList();
        Assert.Contains(righe, r => r.Cell(1).GetString() == "0" && r.Cell(2).GetString() == "Non compliant");
        Assert.Contains(righe, r => r.Cell(1).GetString() == "N/A" && r.Cell(2).GetString() == "Not applicable");
    }

    [Fact]
    public void GeneraExcel_HaUnFoglioPerCiascunSottoveicoloNonArchiviato()
    {
        var bytes = AuditReportService.GeneraExcel(_state);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));

        foreach (var veicolo in _state.Master.Veicoli.Load().Where(v => !v.Archiviata))
            Assert.True(workbook.TryGetWorksheet(veicolo.Nome.Length > 31 ? veicolo.Nome[..31] : veicolo.Nome, out _),
                $"Manca il foglio per il veicolo '{veicolo.Nome}'");
    }

    [Fact]
    public void GeneraExcel_OverloadAuditView_EsportaSoloVeicoloSelezionatoETipoAttivo()
    {
        var veicoli = _state.Master.Veicoli.Load().Where(v => !v.Archiviata).ToList();
        var selezionato = veicoli.First(v => AuditMatriceService.EcuDelSottoveicolo(_state.Master.Componente.Load(), v.Nome).Count > 0);
        var altro = veicoli.First(v => !v.Nome.Equals(selezionato.Nome, StringComparison.OrdinalIgnoreCase));
        var standard = new Requisito { Id = "R-EXPORT-STANDARD", Testo = "Solo standard" };
        var extra = new Requisito { Id = "R-EXPORT-Extra", Testo = "Solo Extra" };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, new[] { selezionato.Nome });
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Concat(new[] { standard, extra }).ToList());

        var bytes = AuditReportService.GeneraExcel(_state, selezionato.Nome, new[] { "Extra" });
        using var workbook = new XLWorkbook(new MemoryStream(bytes));

        var nomeSelezionato = selezionato.Nome.Length > 31 ? selezionato.Nome[..31] : selezionato.Nome;
        var nomeAltro = altro.Nome.Length > 31 ? altro.Nome[..31] : altro.Nome;
        Assert.True(workbook.TryGetWorksheet(nomeSelezionato, out var foglio));
        Assert.False(workbook.TryGetWorksheet(nomeAltro, out _));
        Assert.Contains("R-EXPORT-Extra", foglio!.Column(1).CellsUsed().Select(c => c.GetString()));
        Assert.DoesNotContain("R-EXPORT-STANDARD", foglio.Column(1).CellsUsed().Select(c => c.GetString()));
    }

    [Fact]
    public void GeneraExcel_OverloadAuditView_SenzaVeicoloNonRipiegaSuTutti()
    {
        Assert.Throws<ArgumentException>(() => AuditReportService.GeneraExcel(_state, "  ", null));
        Assert.Throws<InvalidOperationException>(() => AuditReportService.GeneraExcel(_state, "veicolo non più disponibile", null));
    }

    [Fact]
    public void GeneraExcel_DettaglioDtcRispettaVeicoloETipoAttivi()
    {
        var veicoli = _state.Master.Veicoli.Load().Where(v => !v.Archiviata).ToList();
        var selezionato = veicoli.First(v => AuditMatriceService.EcuDelSottoveicolo(_state.Master.Componente.Load(), v.Nome).Count > 0);
        var altro = veicoli.First(v => !v.Nome.Equals(selezionato.Nome, StringComparison.OrdinalIgnoreCase));
        var componente = AuditMatriceService.EcuDelSottoveicolo(_state.Master.Componente.Load(), selezionato.Nome)[0];
        var extra = new Requisito
        {
            Id = "R-EXPORT-guasto-FILTER", Testo = "Requisito Extra",
            Guasto = new List<DtcRequisito>
            {
                new() { Codice = "P-AUDIT-FILTER", Componente = componente, Veicolo = selezionato.Nome },
                new() { Codice = "P-AUDIT-FILTER", Componente = componente, Veicolo = altro.Nome },
            },
        };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, new[] { selezionato.Nome, altro.Nome });
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(extra).ToList());

        // Il foglio dettaglio è derivato da operazioni guasto realmente eseguite. Il solo mapping di anagrafica
        // non produce una riga (e il provider non deve inventarla per il codice sintetico del fixture).
        var testDtc = new TestCase
        {
            Id = "TC-AUDIT-guasto-FILTER",
            Componente = new EcuSelection(new[] { componente }),
            Veicolo = selezionato.Nome,
            RequisitoId = new List<string> { extra.Id },
        };
        testDtc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = new DateTime(2026, 6, 1),
            Esito = Esito.Conforme,
            Condizioni =
            {
                new Condizione
                {
                    Guasto = "P-AUDIT-FILTER", DtcRequisitoId = extra.Id,
                    DtcPasso = PassoDtc.Validazione, Esito = Esito.Conforme,
                    Componente = new EcuSelection(new[] { componente }), DtcVeicolo = selezionato.Nome,
                },
            },
        });
        _state.TestCases.Save(testDtc, true);

        var bytesD4d = AuditReportService.GeneraExcel(_state, selezionato.Nome, new[] { "Extra" });
        using var workbookD4d = new XLWorkbook(new MemoryStream(bytesD4d));
        var dettaglio = workbookD4d.Worksheet("Guasto Extra");
        var righeDtc = dettaglio.RowsUsed().Skip(1).ToList();
        Assert.Contains(righeDtc, r => r.Cell(2).GetString() == "P-AUDIT-FILTER" && r.Cell(9).GetString() == selezionato.Nome);
        Assert.All(righeDtc, r => Assert.Equal(selezionato.Nome, r.Cell(9).GetString()));

        var bytesStandard = AuditReportService.GeneraExcel(_state, selezionato.Nome, new[] { "Standard" });
        using var workbookStandard = new XLWorkbook(new MemoryStream(bytesStandard));
        Assert.False(workbookStandard.TryGetWorksheet("Guasto Extra", out _));
    }

    [Fact]
    public async Task ExportCsvDettaglioDtc_RispettaRequisitoSelezionatoEFiltriCorrenti()
    {
        var veicolo = _state.Master.Veicoli.Load().First(v => !v.Archiviata).Nome;
        var componente = _state.Master.Componente.Load().First(e => !e.Archiviata).Nome;
        var selezionato = new Requisito
        {
            Id = "R-CSV-SELEZIONATO", Testo = "Requisito selezionato",
            Guasto = new List<DtcRequisito>
            {
                new() { Codice = "P-CSV-MANTIENI", Componente = componente, Veicolo = veicolo },
                new() { Codice = "P-CSV-ESCLUDI", Componente = componente, Veicolo = veicolo },
            }
        };
        var altro = new Requisito
        {
            Id = "R-CSV-ALTRO", Testo = "Altro requisito",
            Guasto = new List<DtcRequisito> { new() { Codice = "P-CSV-ALTRO", Componente = componente, Veicolo = veicolo } }
        };
        RequisitoApplicabilita.ImpostaTipo(selezionato, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaTipo(altro, TipoRequisito.Extra);
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Concat(new[] { selezionato, altro }).ToList());

        var vm = new AuditViewModel(_state);
        await vm.CaricaDettaglioDtcAsync(selezionato.Id);
        Assert.Equal(2, vm.DtcRighe.Count);
        vm.DtcFiltroTesto = "P-CSV-MANTIENI";
        Assert.Single(vm.DtcRighe);

        var percorso = Path.Combine(_dir, "audit-guasto-filtrato.csv");
        await vm.EsportaDettaglioDtcCsv(percorso);

        var righe = File.ReadAllLines(percorso);
        Assert.Equal(2, righe.Length); // intestazione e una sola riga filtrata
        Assert.Contains("R-CSV-SELEZIONATO", righe[1]);
        Assert.Contains("P-CSV-MANTIENI", righe[1]);
        Assert.DoesNotContain("P-CSV-ESCLUDI", righe[1]);
        Assert.DoesNotContain("R-CSV-ALTRO", string.Join("\n", righe));
        Assert.DoesNotContain("P-CSV-ALTRO", string.Join("\n", righe));
    }

    [Fact]
    public void GeneraExcel_DueVeicoliConStessoPrefissoDi31Caratteri_HannoFogliDistinti()
    {
        // Excel accetta al massimo 31 caratteri per nome foglio: due veicoli con lo stesso prefisso non devono far fallire l'export.
        const string prefisso = "Sottoveicolo con nome molto lungo ";
        var veicoli = _state.Master.Veicoli.Load();
        veicoli.Add(new Veicolo { Nome = prefisso + "Alfa" });
        veicoli.Add(new Veicolo { Nome = prefisso + "Beta" });
        _state.Master.Veicoli.Save(veicoli);

        var bytes = AuditReportService.GeneraExcel(_state);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var nomi = workbook.Worksheets.Select(w => w.Name).ToList();
        Assert.Equal(nomi.Count, nomi.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(nomi, nome => Assert.True(nome.Length <= 31, $"Nome foglio troppo lungo: '{nome}'"));
        Assert.Equal(2, nomi.Count(nome => nome.StartsWith("Sottoveicolo con nome molto", StringComparison.Ordinal)));
    }

    [Fact]
    public void GeneraExcel_CellaValutata_MostraSoloIlNumeroPuntoNessunTestoDiStato()
    {
        var requisito = new Requisito { Id = "R-AUDIT-EXPORT-1", Testo = "Requisito export" };
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(requisito).ToList());

        var ecuConVeicolo = _state.Master.Componente.Load().First(e => e.VeicoliAssociati.Count > 0 && !e.Archiviata);
        var veicoloNome = ecuConVeicolo.VeicoliAssociati[0];

        var tc = new TestCase { Id = "TC-AUDIT-EXPORT-1", Componente = ecuConVeicolo.Nome, Veicolo = veicoloNome, RequisitoId = new List<string> { requisito.Id } };
        var esecuzione = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V" };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, tc.RequisitoId);
        tc.Esecuzioni.Add(esecuzione);
        _state.TestCases.Save(tc, true);

        var bytes = AuditReportService.GeneraExcel(_state);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var nomeFoglio = veicoloNome.Length > 31 ? veicoloNome[..31] : veicoloNome;
        var foglio = workbook.Worksheet(nomeFoglio);

        var colonnaEcu = foglio.Row(1).CellsUsed().First(c => c.GetString() == ecuConVeicolo.Nome).Address.ColumnNumber;
        var rigaRequisito = foglio.Column(1).CellsUsed().First(c => c.GetString() == requisito.Id).Address.RowNumber;

        Assert.Equal("5", foglio.Cell(rigaRequisito, colonnaEcu).GetString());
    }

    [Fact]
    public void GeneraExcel_ConflittoTraTestDiversi_CellaVuotaEFoglioConflittiValorizzato()
    {
        var requisito = new Requisito { Id = "R-AUDIT-CONFLITTO", Testo = "Requisito in conflitto" };
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(requisito).ToList());

        var ecuConVeicolo = _state.Master.Componente.Load().First(e => e.VeicoliAssociati.Count > 0 && !e.Archiviata);
        var veicoloNome = ecuConVeicolo.VeicoliAssociati[0];

        var tcA = new TestCase { Id = "TC-CONFLITTO-A", Componente = ecuConVeicolo.Nome, Veicolo = veicoloNome, RequisitoId = new List<string> { requisito.Id } };
        var esecuzioneA = new Esecuzione { Iterazione = 1, Data = new DateTime(2026, 1, 1), Esito = Esito.Conforme, Matricola = "V" };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzioneA, tcA.RequisitoId);
        tcA.Esecuzioni.Add(esecuzioneA);
        _state.TestCases.Save(tcA, true);

        var tcB = new TestCase { Id = "TC-CONFLITTO-B", Componente = ecuConVeicolo.Nome, Veicolo = veicoloNome, RequisitoId = new List<string> { requisito.Id } };
        var esecuzioneB = new Esecuzione { Iterazione = 1, Data = new DateTime(2026, 1, 1), Esito = Esito.NonConforme, Matricola = "V" };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzioneB, tcB.RequisitoId);
        tcB.Esecuzioni.Add(esecuzioneB);
        _state.TestCases.Save(tcB, true);

        var bytes = AuditReportService.GeneraExcel(_state);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var nomeFoglio = veicoloNome.Length > 31 ? veicoloNome[..31] : veicoloNome;
        var foglioSottoveicolo = workbook.Worksheet(nomeFoglio);
        var colonnaEcu = foglioSottoveicolo.Row(1).CellsUsed().First(c => c.GetString() == ecuConVeicolo.Nome).Address.ColumnNumber;
        var rigaRequisito = foglioSottoveicolo.Column(1).CellsUsed().First(c => c.GetString() == requisito.Id).Address.RowNumber;
        Assert.True(foglioSottoveicolo.Cell(rigaRequisito, colonnaEcu).IsEmpty());

        var foglioConflitti = workbook.Worksheet("Conflitti");
        var righe = foglioConflitti.RowsUsed().Skip(2);
        Assert.Contains(righe, r => r.Cell(1).GetString() == ecuConVeicolo.Nome && r.Cell(2).GetString() == requisito.Id
            && r.Cell(3).GetString() == requisito.Testo
            && r.Cell(4).GetString().Contains("TC-CONFLITTO-A") && r.Cell(4).GetString().Contains("TC-CONFLITTO-B"));
    }

    [Fact]
    public void GeneraExcel_ConflittoManualeLegacy_EsportaFontiValoriEDettaglio()
    {
        var requisito = new Requisito { Id = "R-AUDIT-MAN-LEGACY", Testo = "Tie manuale legacy" };
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(requisito).ToList());
        var componente = _state.Master.Componente.Load().First(e => e.VeicoliAssociati.Count > 0 && !e.Archiviata);
        var veicolo = componente.VeicoliAssociati[0];
        var data = new DateTime(2026, 4, 5, 10, 11, 12);
        _state.Master.ValutazioniRequisitoEcuVeicolo.Save(new List<ValutazioneRequisitoEcuVeicolo>
        {
            new ValutazioneRequisitoEcuVeicolo
            {
                Componente = componente.Nome, RequisitoId = requisito.Id, Veicolo = veicolo,
                Punteggio = PunteggioConformita.NonConforme, Origine = OrigineValutazioneRequisito.Manuale,
                UltimaModifica = data, Operatore = "AB", Nota = "manuale locale",
            },
        });
        _state.Master.RisoluzioniConflittoAudit.Save(new List<RisoluzioneConflittoAudit>
        {
            new RisoluzioneConflittoAudit
            {
                Componente = componente.Nome, RequisitoId = requisito.Id, PunteggioManuale = PunteggioConformita.Conforme,
                UltimaModifica = data, ModificatoDa = "CD", Motivazione = "legacy globale",
            },
        });

        using var workbook = new XLWorkbook(new MemoryStream(AuditReportService.GeneraExcel(_state, veicolo, null)));
        var conflict = Assert.Single(workbook.Worksheet("Conflitti").RowsUsed().Skip(2), r =>
            r.Cell(1).GetString() == componente.Nome && r.Cell(2).GetString() == requisito.Id && r.Cell(6).GetString() == veicolo);

        Assert.Contains("Manuale veicolo", conflict.Cell(7).GetString());
        Assert.Contains("Manuale legacy globale", conflict.Cell(7).GetString());
        Assert.Contains("0", conflict.Cell(7).GetString());
        Assert.Contains("5", conflict.Cell(7).GetString());
        Assert.Contains("manuale locale", conflict.Cell(7).GetString());
        Assert.Contains("legacy globale", conflict.Cell(7).GetString());
    }

    [Fact]
    public void GeneraExcel_ConflittoTestManuale_EsportaTestEManualeNelDettaglio()
    {
        var requisito = new Requisito { Id = "R-AUDIT-TEST-MAN", Testo = "Tie Test manuale" };
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(requisito).ToList());
        var componente = _state.Master.Componente.Load().First(e => e.VeicoliAssociati.Count > 0 && !e.Archiviata);
        var veicolo = componente.VeicoliAssociati[0];
        var data = new DateTime(2026, 6, 7, 8, 9, 10);
        var test = new TestCase { Id = "TC-CONFLITTO-TEST-MAN", Componente = componente.Nome, Veicolo = veicolo, RequisitoId = { requisito.Id } };
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = data, Esito = Esito.Conforme, Matricola = "V",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = requisito.Id, Valore = PunteggioConformita.Conforme } },
        });
        _state.TestCases.Save(test, true);
        _state.Master.ValutazioniRequisitoEcuVeicolo.Save(new List<ValutazioneRequisitoEcuVeicolo>
        {
            new ValutazioneRequisitoEcuVeicolo
            {
                Componente = componente.Nome, RequisitoId = requisito.Id, Veicolo = veicolo,
                Punteggio = PunteggioConformita.NonConforme, Origine = OrigineValutazioneRequisito.Manuale,
                UltimaModifica = data, Operatore = "AB", Nota = "manuale dopo pari istante",
            },
        });

        using var workbook = new XLWorkbook(new MemoryStream(AuditReportService.GeneraExcel(_state, veicolo, null)));
        var conflict = Assert.Single(workbook.Worksheet("Conflitti").RowsUsed().Skip(2), r =>
            r.Cell(1).GetString() == componente.Nome && r.Cell(2).GetString() == requisito.Id && r.Cell(6).GetString() == veicolo);

        Assert.Contains("Test TC-CONFLITTO-TEST-MAN = 5", conflict.Cell(7).GetString());
        Assert.Contains("Manuale veicolo = 0", conflict.Cell(7).GetString());
        Assert.Contains("manuale dopo pari istante", conflict.Cell(7).GetString());
    }

    [Fact]
    public void GeneraExcel_FoglioSottoveicolo_MostraIlTestoDelRequisitoAccantoAllId()
    {
        var requisito = new Requisito { Id = "R-AUDIT-TESTO", Testo = "Testo completo del requisito da esportare" };
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(requisito).ToList());
        // Un veicolo senza Componenti associati mostra solo il messaggio "Nessuna componente associata": il test deve usare un veicolo con
        // componente, non il primo per nome (l'ordine dei cataloghi non e' un contratto del test).
        var componente = _state.Master.Componente.Load();
        var veicolo = _state.Master.Veicoli.Load()
            .First(v => !v.Archiviata && AuditMatriceService.EcuDelSottoveicolo(componente, v.Nome).Count > 0);

        var bytes = AuditReportService.GeneraExcel(_state);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var nomeFoglio = veicolo.Nome.Length > 31 ? veicolo.Nome[..31] : veicolo.Nome;
        var foglio = workbook.Worksheet(nomeFoglio);

        Assert.Equal("Testo", foglio.Cell(1, 2).GetString());
        var rigaRequisito = foglio.Column(1).CellsUsed().First(c => c.GetString() == requisito.Id).Address.RowNumber;
        Assert.Equal(requisito.Testo, foglio.Cell(rigaRequisito, 2).GetString());
    }

    [Fact]
    public void GeneraExcel_CellaNonValutata_RestaVuota()
    {
        var requisito = new Requisito { Id = "R-AUDIT-EXPORT-VUOTA", Testo = "Requisito mai testato" };
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(requisito).ToList());

        var ecuConVeicolo = _state.Master.Componente.Load().First(e => e.VeicoliAssociati.Count > 0 && !e.Archiviata);
        var veicoloNome = ecuConVeicolo.VeicoliAssociati[0];

        var bytes = AuditReportService.GeneraExcel(_state);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var nomeFoglio = veicoloNome.Length > 31 ? veicoloNome[..31] : veicoloNome;
        var foglio = workbook.Worksheet(nomeFoglio);

        var colonnaEcu = foglio.Row(1).CellsUsed().First(c => c.GetString() == ecuConVeicolo.Nome).Address.ColumnNumber;
        var rigaRequisito = foglio.Column(1).CellsUsed().First(c => c.GetString() == requisito.Id).Address.RowNumber;

        Assert.True(foglio.Cell(rigaRequisito, colonnaEcu).IsEmpty());
    }

    // --- Piano 6.3.1 §1: simbolo "!" per la cella propagabile non confermata, marcatura Propagato/Manuale ---

    [Fact]
    public void GeneraExcel_CellaPropagabileNonConfermata_MostraIlPuntoEsclamativoConCommento()
    {
        var requisito = new Requisito { Id = "R-AUDIT-PROPAGABILE", Testo = "Requisito propagabile" };
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(requisito).ToList());

        var ecuConDueVeicoli = _state.Master.Componente.Load().First(e => e.VeicoliAssociati.Count > 1 && !e.Archiviata);
        var veicoloConTest = ecuConDueVeicoli.VeicoliAssociati[0];
        var veicoloSenzaTest = ecuConDueVeicoli.VeicoliAssociati[1];

        var tc = new TestCase { Id = "TC-AUDIT-PROPAGABILE", Componente = ecuConDueVeicoli.Nome, Veicolo = veicoloConTest, RequisitoId = new List<string> { requisito.Id } };
        var esecuzione = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V" };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, tc.RequisitoId);
        tc.Esecuzioni.Add(esecuzione);
        _state.TestCases.Save(tc, true);

        var bytes = AuditReportService.GeneraExcel(_state);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var nomeFoglio = veicoloSenzaTest.Length > 31 ? veicoloSenzaTest[..31] : veicoloSenzaTest;
        var foglio = workbook.Worksheet(nomeFoglio);

        var colonnaEcu = foglio.Row(1).CellsUsed().First(c => c.GetString() == ecuConDueVeicoli.Nome).Address.ColumnNumber;
        var rigaRequisito = foglio.Column(1).CellsUsed().First(c => c.GetString() == requisito.Id).Address.RowNumber;
        var cella = foglio.Cell(rigaRequisito, colonnaEcu);

        Assert.Equal("!", cella.GetString());
        Assert.True(cella.HasComment);
        Assert.Contains(veicoloConTest, cella.GetComment().Text);
    }

    [Fact]
    public void GeneraExcel_ValutazioneManuale_MostraIlNumeroConCommentoOrigine()
    {
        var requisito = new Requisito { Id = "R-AUDIT-MANUALE-VEICOLO", Testo = "Requisito con valutazione manuale per veicolo" };
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(requisito).ToList());

        var ecuConVeicolo = _state.Master.Componente.Load().First(e => e.VeicoliAssociati.Count > 0 && !e.Archiviata);
        var veicoloNome = ecuConVeicolo.VeicoliAssociati[0];

        _state.Master.ValutazioniRequisitoEcuVeicolo.Save(new List<ValutazioneRequisitoEcuVeicolo>
        {
            new()
            {
                RequisitoId = requisito.Id, Componente = ecuConVeicolo.Nome, Veicolo = veicoloNome,
                Punteggio = PunteggioConformita.ParzialmenteConforme, Origine = OrigineValutazioneRequisito.Manuale,
                Nota = "inserito a mano per verifica export", Operatore = "QA", UltimaModifica = DateTime.Now,
            }
        });

        var bytes = AuditReportService.GeneraExcel(_state);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var nomeFoglio = veicoloNome.Length > 31 ? veicoloNome[..31] : veicoloNome;
        var foglio = workbook.Worksheet(nomeFoglio);

        var colonnaEcu = foglio.Row(1).CellsUsed().First(c => c.GetString() == ecuConVeicolo.Nome).Address.ColumnNumber;
        var rigaRequisito = foglio.Column(1).CellsUsed().First(c => c.GetString() == requisito.Id).Address.RowNumber;
        var cella = foglio.Cell(rigaRequisito, colonnaEcu);

        Assert.Equal("2", cella.GetString());
        Assert.True(cella.HasComment);
        Assert.Contains("Manuale", cella.GetComment().Text);
        Assert.Contains("inserito a mano per verifica export", cella.GetComment().Text);
    }
}
