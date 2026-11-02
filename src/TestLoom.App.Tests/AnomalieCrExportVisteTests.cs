using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Export Anomalie/CR: oltre ai fogli dati, le tabelle come visualizzate (stesso ordine, stessi testi, colonna Veicoli, date CR)
/// e il foglio Info; l'ordinamento scelto in pagina e' quello del file.</summary>
public sealed class AnomalieCrExportVisteTests
{
    [Fact]
    public async Task Export_HaVisteInTestaDatiSottoEInfoERispettaLOrdinamentoCr()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_AnomalieCrViste_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var state = TestStateFactory.Create();
            state.InizializzaCartella(root);
            state.NonConformita.Save(new NonConformita { Id = "NC-1", Descrizione = "uno", DataApertura = new DateTime(2026, 1, 5) });
            state.ChangeRequests.Save(new ChangeRequest
            {
                Id = "CR-A", Titolo = "Zeta", DataCreazione = new DateTime(2026, 2, 1), ImplementataIl = new DateTime(2026, 3, 1),
                ChiusaIl = new DateTime(2026, 4, 1), VeicoliImpattati = ["Daily", "Bus"]
            });
            state.ChangeRequests.Save(new ChangeRequest { Id = "CR-B", Titolo = "Alfa", DataCreazione = new DateTime(2026, 2, 2), VeicoliImpattati = ["Crafter"] });

            var vm = new NonConformitaViewModel(state, _ => { });
            vm.ImpostaOrdinamentoCr(ChangeRequestOrdinamentoChiave.Titolo); // crescente: Alfa, Zeta
            var percorso = Path.Combine(root, "anomalie-cr.xlsx");
            await vm.EsportaExcelVerso(percorso);

            using var wb = new XLWorkbook(percorso);
            var loc = LocalizationService.Instance;
            var nomi = wb.Worksheets.Select(w => w.Name).ToList();
            Assert.Equal(loc.Traduci("ExpFoglioVistaNc"), nomi[0]);
            Assert.Equal(loc.Traduci("ExpFoglioVistaCr"), nomi[1]);
            Assert.Contains("Non Conformità", nomi);
            Assert.Contains("Change Request", nomi);
            Assert.Contains(loc.Traduci("ExpFoglioInfo"), nomi);

            var vista = wb.Worksheet(nomi[1]);
            Assert.Equal(loc.Traduci("ColVeicoliAssociati"), vista.Row(1).CellsUsed().Single(c => c.GetString() == loc.Traduci("ColVeicoliAssociati")).GetString());
            string Col(int riga, string chiave) => vista.Cell(riga, vista.Row(1).CellsUsed().Single(c => c.GetString() == loc.Traduci(chiave)).Address.ColumnNumber).GetString();
            Assert.Equal("CR-B", Col(2, "ColId"));
            Assert.Equal("Crafter", Col(2, "ColVeicoliAssociati"));
            Assert.Equal("CR-A", Col(3, "ColId"));
            Assert.Equal("Bus +1", Col(3, "ColVeicoliAssociati"));
            Assert.Equal("01/03/2026", Col(3, "ColImplementazione"));
            Assert.Equal("01/04/2026", Col(3, "ColChiusura"));
            Assert.Equal("", Col(2, "ColChiusura"));

            var nc = wb.Worksheet(nomi[0]);
            Assert.Equal("NC-1", nc.Cell(2, 1).GetString());
            Assert.Equal("05/01/2026", nc.Cell(2, nc.Row(1).CellsUsed().Single(c => c.GetString() == loc.Traduci("ColApertura")).Address.ColumnNumber).GetString());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Scelta_ColonneESezioni_EFiltroImpostatoDalDescrittore()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_AnomalieCrScelta_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var state = TestStateFactory.Create();
            state.InizializzaCartella(root);
            state.NonConformita.Save(new NonConformita { Id = "NC-1", Descrizione = "uno", DataApertura = new DateTime(2026, 1, 5) });
            state.ChangeRequests.Save(new ChangeRequest { Id = "CR-A", Titolo = "needle uno", DataCreazione = new DateTime(2026, 2, 1), VeicoliImpattati = ["Daily"] });
            state.ChangeRequests.Save(new ChangeRequest { Id = "CR-B", Titolo = "altro", DataCreazione = new DateTime(2026, 2, 2) });

            var vm = new NonConformitaViewModel(state, _ => { });
            var descrittore = vm.CreaDescrittoreExport();
            var testo = descrittore.Filtri!.OfType<ExportFiltroTesto>().Single(f => f.Id == "testo");
            testo.Imposta("needle"); // come lo imposterebbe il dialogo alla conferma
            Assert.Equal("needle", vm.FiltroTesto);
            Assert.Contains(descrittore.Filtri!, f => f.Id == "gamma");
            Assert.Contains(descrittore.Filtri!, f => f.Id == "aperturada");

            var scelta = new ExportConfigurazione { Nome = "t", Sezioni = [AnomalieSezioniExport.VistaCr, AnomalieSezioniExport.DatiCr] };
            scelta.Colonne[AnomalieSezioniExport.VistaCr] = ["id", "veicoli"];
            scelta.Colonne[AnomalieSezioniExport.DatiCr] = ["ID", "Titolo"];
            var percorso = Path.Combine(root, "scelta.xlsx");
            await vm.EsportaExcelVerso(percorso, scelta);

            using var wb = new XLWorkbook(percorso);
            var loc = LocalizationService.Instance;
            Assert.Equal(new[] { loc.Traduci("ExpFoglioVistaCr"), "Change Request" }, wb.Worksheets.Select(w => w.Name));
            var vista = wb.Worksheet(loc.Traduci("ExpFoglioVistaCr"));
            Assert.Equal(2, vista.LastColumnUsed()!.ColumnNumber());
            Assert.Equal(2, vista.LastRowUsed()!.RowNumber()); // solo CR-A passa il filtro "needle"
            Assert.Equal("CR-A", vista.Cell(2, 1).GetString());
            var dati = wb.Worksheet("Change Request");
            Assert.Equal(new[] { "ID", "Titolo" }, new[] { dati.Cell(1, 1).GetString(), dati.Cell(1, 2).GetString() });
            Assert.Equal(2, dati.LastRowUsed()!.RowNumber());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
