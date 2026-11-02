using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed class ChangeRequestExportServiceTests
{
    [Fact]
    public async Task ExportPaginaAnomalieCreaDueFogliConTutteLeRigheFiltrate()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_AnomalieCrExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var state = TestStateFactory.Create();
            state.InizializzaCartella(root);
            state.NonConformita.Save(new NonConformita { Id = "NC-MATCH", Descrizione = "needle problema", DataApertura = DateTime.Today });
            state.NonConformita.Save(new NonConformita { Id = "NC-ALTRO", Descrizione = "altro problema", DataApertura = DateTime.Today });
            for (var i = 1; i <= 250; i++)
                state.NonConformita.Save(new NonConformita
                {
                    Id = $"NC-MATCH-{i:D3}", Descrizione = $"needle problema {i:D3}", DataApertura = DateTime.Today.AddMinutes(-i)
                });
            state.ChangeRequests.Save(new ChangeRequest { Id = "CR-MATCH", Titolo = "needle modifica", DataCreazione = DateTime.Today });
            state.ChangeRequests.Save(new ChangeRequest { Id = "CR-ALTRO", Titolo = "altra modifica", DataCreazione = DateTime.Today });
            // La griglia mostra 250 righe: almeno una corrispondenza oltre pagina deve comunque finire nel file.
            for (var i = 1; i <= 250; i++)
                state.ChangeRequests.Save(new ChangeRequest
                {
                    Id = $"CR-MATCH-{i:D3}", Titolo = $"needle modifica {i:D3}", DataCreazione = DateTime.Today.AddMinutes(-i)
                });

            var vm = new NonConformitaViewModel(state, _ => { });
            vm.FiltroTesto = "needle";
            var percorso = Path.Combine(root, "anomalie-cr.xlsx");
            await vm.EsportaExcelVerso(percorso);

            using var workbook = new XLWorkbook(percorso);
            var nc = workbook.Worksheet("Non Conformità");
            var cr = workbook.Worksheet("Change Request");
            Assert.Equal("NC-MATCH", nc.Cell(2, 1).GetString());
            Assert.Equal(252, nc.LastRowUsed()!.RowNumber());
            Assert.Contains(nc.Rows(2, 252), row => row.Cell(1).GetString() == "NC-MATCH-250");
            Assert.DoesNotContain(nc.Rows(2, 252), row => row.Cell(1).GetString() == "NC-ALTRO");
            var crIdColonna = cr.Row(1).CellsUsed().Single(c => c.GetString() == "ID").Address.ColumnNumber;
            Assert.Equal("CR-MATCH", cr.Cell(2, crIdColonna).GetString());
            Assert.Equal(252, cr.LastRowUsed()!.RowNumber());
            Assert.Contains(cr.Rows(2, 252), row => row.Cell(crIdColonna).GetString() == "CR-MATCH-250");
            Assert.DoesNotContain(cr.Rows(2, 252), row => row.Cell(crIdColonna).GetString() == "CR-ALTRO");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ScriviFoglio_EsportaDueFogliConGammaECampiCompleti()
    {
        using var workbook = new XLWorkbook();
        var cr = new ChangeRequest
        {
            Id = "CR-42", Titolo = "Adeguamento BCM", Descrizione = "Dettaglio completo",
            MotivazioneDuplicato = "Seguito in CR-41",
            Stato = StatoChangeRequest.Validata, Responsabile = "Paolo", CreatoDa = "Marta",
            DataCreazione = new DateTime(2026, 5, 1), ChiusaIl = new DateTime(2026, 5, 8),
            MotivoChiusura = "Risolto", VeicoliImpattati = ["V1"], EcuImpattate = ["BCM"],
            FunzionalitaImpattate = ["Chiusura centralizzata"], VinCliente = ["Matricola-1"],
            CampiCustom = new Dictionary<string, string> { ["Rilascio"] = "R24.3" },
            TicketCollegati = [new TicketRif { Riferimento = "TICKET-1", Url = "https://example.test/TICKET-1", Note = "Claim" }],
            TestValidazione = [new CollegamentoValidazioneCr
            {
                TestId = "T-42", EccezioneCopertura = true, MotivazioneEccezione = "Banco indisponibile",
                ApprovataDa = "Master", ApprovataIl = new DateTime(2026, 5, 2),
                DeviazioneApprovata = true, MotivazioneDeviazione = "Tolleranza accettata", DeviazioneApprovataDa = "Quality",
                EsecuzioneValidante = 3, CollegatoDa = "Marta",
                SnapshotValidazione = new SnapshotValidazioneCr
                {
                    EseguitaIl = new DateTime(2026, 5, 3), Esito = Esito.Conforme, Veicoli = ["V1"], Componente = ["BCM"]
                }
            }],
            Allegati = [new Allegato { NomeFile = "log.zip", CaricatoDa = "Paolo", DimensioneByte = 1234, Descrizione = "Log prova" }],
            Revisioni = [new RevisioneChangeRequest
            {
                Numero = 2, Data = new DateTime(2026, 5, 4), Autore = "Marta", Titolo = "Revisione aggiornata",
                Descrizione = "Dettaglio revisione", MotivazioneDuplicato = "Accorpata", TestValidazioneId = ["T-42"],
                CampiCustom = new Dictionary<string, string> { ["Rilascio"] = "R24.2" }
            }],
            Changelog = [new ChangelogEntry { Data = new DateTime(2026, 5, 5), Operatore = "Paolo", Campo = "Stato", ValorePrecedente = "Aperta", ValoreNuovo = "Validata" }]
        };

        ChangeRequestExportService.ScriviFoglio(workbook, [cr], gammaPerVeicolo: new Dictionary<string, string> { ["V1"] = "Gamma A" });

        var sheet = workbook.Worksheet("Change Request");
        Assert.Single(workbook.Worksheets);
        var intestazioni = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        string Cella(string nome) => sheet.Cell(2, intestazioni.IndexOf(nome) + 1).GetString();
        Assert.Equal("CR-42", Cella("ID"));
        Assert.Equal("Dettaglio completo", Cella("Descrizione"));
        Assert.Equal(new DateTime(2026, 5, 1), sheet.Cell(2, intestazioni.IndexOf("Data creazione") + 1).GetDateTime());
        Assert.Equal("BCM", Cella("Componente impattate"));
        Assert.Equal("Gamma A", Cella("Gamma"));
        Assert.Equal("Seguito in CR-41", Cella("Motivazione duplicato"));
        Assert.Equal("R24.3", Cella("Campo custom: Rilascio"));
        Assert.Contains("https://example.test/TICKET-1", Cella("Ticket"));
        Assert.Contains("Banco indisponibile", Cella("Test di validazione (dettagli)"));
        Assert.Contains("T-42", Cella("Test di validazione (dettagli)"));
        Assert.Contains("log.zip", Cella("Allegati (dettagli)"));
        Assert.Contains("Revisione aggiornata", Cella("Revisioni (dettagli)"));
        Assert.Contains("Aperta", Cella("Cronologia (dettagli)"));
    }

    [Fact]
    public void ScriviFoglio_DettaglioOltreLimiteCella_RestaCompletoNelFoglioEsteso()
    {
        using var workbook = new XLWorkbook();
        var cr = new ChangeRequest { Id = "CR-LUNGA", Descrizione = new string('x', 32_768) };
        ChangeRequestExportService.ScriviFoglio(workbook, [cr]);
        var principale = workbook.Worksheet("Change Request");
        var headers = principale.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        var marker = principale.Cell(2, headers.IndexOf("Descrizione") + 1).GetString();
        Assert.Contains("Testo esteso", marker);
        var dettaglio = workbook.Worksheets.Single(w => w.Name.StartsWith("Change Request dettagli estesi", StringComparison.Ordinal));
        var testoCompleto = string.Concat(dettaglio.RowsUsed().Skip(1)
            .Where(r => r.Cell(1).GetString() == "CR-LUNGA" && r.Cell(2).GetString() == "Descrizione")
            .OrderBy(r => int.Parse(r.Cell(3).GetString().Split('/')[0], System.Globalization.CultureInfo.InvariantCulture))
            .Select(r => r.Cell(4).GetString()));
        Assert.Equal(cr.Descrizione, testoCompleto);
    }
}
