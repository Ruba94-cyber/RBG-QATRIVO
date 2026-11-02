using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Pivot standard del Report (componente x categoria, componente e funzionalita' per stato, veicolo x categoria) e misura/campo valore della
/// pivot personalizzata. Tutti i test leggono il file xlsx realmente salvato su disco e riaperto, non solo l'output dei servizi.</summary>
public sealed class ReportPivotStandardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoomPivotStd_" + Guid.NewGuid().ToString("N"));

    public ReportPivotStandardTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    public static string[] NomiFogliStandard()
    {
        var loc = LocalizationService.Instance;
        return
        [
            loc.Scegli("Componente x categoria", "Componente x category", "Componente x catégorie"),
            loc.Scegli("Componente e funzionalità", "Componente and function", "Componente et fonction"),
            loc.Scegli("Veicolo x categoria", "Vehicle x category", "Véhicule x catégorie"),
        ];
    }

    private static readonly List<Veicolo> Veicoli = [new() { Nome = "Daily A", GammaNome = "G" }, new() { Nome = "Daily B", GammaNome = "G" }];

    private static TestCase T(string id, string funz, string componente, string veicolo, Esito esito)
    {
        var tc = new TestCase { Id = id, DescrizioneBreve = "d-" + id, Componente = new EcuSelection([componente]), Veicoli = [veicolo], Funzionalita = [funz] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 3, 1), Operatore = "Mario", Esito = esito,
            Condizioni = [new Condizione { Funzionalita = funz, Esito = esito, Componente = new EcuSelection([componente]) }]
        });
        return tc;
    }

    private XLWorkbook Genera(bool numeri, bool percentuali)
    {
        var funzioni = new List<Funzionalita> { new() { Nome = "F1", Categorie = ["Cat1"] }, new() { Nome = "F2", Categorie = ["Cat1", "Cat2"] } };
        var test = new[]
        {
            T("1", "F1", "BCM", "Daily A", Esito.Conforme), T("2", "F1", "BCM", "Daily B", Esito.NonConforme),
            T("3", "F2", "BCM", "Daily A", Esito.Conforme), T("4", "F2", "EDC", "Daily A", Esito.Conforme),
            T("5", "F2", "EDC", "Daily B", Esito.NonConforme),
        };
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.CategoriaFunzionalita };
        var preparata = ReportMatriceService.Prepara(test, config, Veicoli, funzioni, []);
        using var wb = new XLWorkbook();
        ReportExportArricchimentiService.ScriviPivotStato(wb, preparata, config, funzioni, [], CancellationToken.None, numeri, percentuali);
        var percorso = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        wb.SaveAs(percorso);
        return new XLWorkbook(percorso);
    }

    /// <summary>Legge i blocchi (titolo in colonna A -> intestazioni X nella riga successiva -> righe Y) in un dizionario titolo -> (y, x) -> valore.</summary>
    private static Dictionary<string, Dictionary<(string Y, string X), double?>> Blocchi(IXLWorksheet ws)
    {
        var risultato = new Dictionary<string, Dictionary<(string, string), double?>>();
        var ultima = ws.LastRowUsed()!.RowNumber();
        var colonne = ws.LastColumnUsed()!.ColumnNumber();
        for (var r = 4; r <= ultima; r++)
        {
            var titolo = ws.Cell(r, 1).GetString();
            if (string.IsNullOrEmpty(titolo)) continue;
            var intest = r + 1;
            if (ws.Cell(intest, 1).IsEmpty() || ws.Cell(intest, 2).IsEmpty()) continue;
            var dict = new Dictionary<(string, string), double?>();
            var riga = intest + 1;
            while (riga <= ultima && !ws.Cell(riga, 1).IsEmpty())
            {
                for (var c = 2; c <= colonne; c++)
                    dict[(ws.Cell(riga, 1).GetString(), ws.Cell(intest, c).GetString())] = ws.Cell(riga, c).IsEmpty() ? null : ws.Cell(riga, c).GetDouble();
                riga++;
            }
            risultato[titolo] = dict;
            r = riga;
        }
        return risultato;
    }

    [Fact]
    public void EcuPerCategoria_EcuSuXCategoriaSuY_PercentualiSommanoUnoSulDenominatoreEsplicito()
    {
        using var wb = Genera(numeri: true, percentuali: true);
        var ws = wb.Worksheet(NomiFogliStandard()[0]);
        Assert.Contains("Componente", ws.Cell(2, 1).GetString() + ws.Cell(1, 1).GetString());
        Assert.Contains("%", ws.Cell(2, 1).GetString());
        var blocchi = Blocchi(ws);
        var totale = blocchi.Single(b => b.Key.StartsWith(LocalizationService.Instance.Scegli("Totale", "Total", "Total"), StringComparison.Ordinal)).Value;
        // X = componente, Y = categoria
        Assert.Contains(totale.Keys, k => k.X == "BCM" && k.Y == "Cat1");
        Assert.Contains(totale.Keys, k => k.X == "EDC" && k.Y == "Cat2");
        var percentuali = blocchi.Where(b => b.Key.EndsWith(" - %", StringComparison.Ordinal)).Select(b => b.Value).ToList();
        var numeri = blocchi.Where(b => b.Key.Contains(" - ") && !b.Key.EndsWith(" - %", StringComparison.Ordinal)).Select(b => b.Value).ToList();
        Assert.True(percentuali.Count >= 2);
        Assert.Equal(percentuali.Count, numeri.Count);
        foreach (var chiave in totale.Keys.Where(k => totale[k] is > 0))
        {
            Assert.Equal(1.0, percentuali.Sum(p => p[chiave] ?? 0), 6);
            Assert.Equal(totale[chiave]!.Value, numeri.Sum(n => n[chiave] ?? 0), 6);
        }
        // Cella senza combinazioni nel totale: vuota, mai 0%.
        foreach (var chiave in totale.Keys.Where(k => (totale[k] ?? 0) == 0))
            Assert.All(percentuali, p => Assert.Null(p[chiave]));
        // Cat1/BCM: F1 su Daily A e B + F2 su Daily A (solo combinazioni esistenti)
        Assert.Equal(3, totale[("Cat1", "BCM")]);
    }

    [Fact]
    public void VeicoloPerCategoria_VeicoloSuXCategoriaSuY()
    {
        using var wb = Genera(true, true);
        var blocchi = Blocchi(wb.Worksheet(NomiFogliStandard()[2]));
        var totale = blocchi.First().Value;
        Assert.Contains(totale.Keys, k => k.X == "Daily A" && k.Y == "Cat1");
        Assert.Contains(totale.Keys, k => k.X == "Daily B" && k.Y == "Cat2");
        Assert.Contains("%", wb.Worksheet(NomiFogliStandard()[2]).Cell(2, 1).GetString());
    }

    [Fact]
    public void EcuEFunzionalita_SuAsseXConDueLivelliEStatiSuRighe()
    {
        using var wb = Genera(true, true);
        var ws = wb.Worksheet(NomiFogliStandard()[1]);
        Assert.Equal("Componente", ws.Cell(4, 1).GetString());
        var coppie = Enumerable.Range(2, ws.LastColumnUsed()!.ColumnNumber() - 1).Select(c => (ws.Cell(4, c).GetString(), ws.Cell(5, c).GetString())).ToList();
        Assert.Contains(("BCM", "F1"), coppie);
        Assert.Contains(("EDC", "F2"), coppie);
        Assert.Contains("%", ws.Cell(2, 1).GetString());
        // Per ogni colonna la somma delle percentuali sulle righe % e' 1 e coincide con il totale nelle righe numero.
        var righePct = ws.RowsUsed().Where(r => r.Cell(1).GetString().EndsWith(" - %", StringComparison.Ordinal)).ToList();
        var righeNum = ws.RowsUsed().Where(r => r.RowNumber() > 6 && !r.Cell(1).GetString().EndsWith(" - %", StringComparison.Ordinal)).ToList();
        var totale = ws.Row(6);
        Assert.NotEmpty(righePct);
        for (var c = 2; c <= coppie.Count + 1; c++)
        {
            Assert.Equal(1.0, righePct.Sum(r => r.Cell(c).IsEmpty() ? 0 : r.Cell(c).GetDouble()), 6);
            Assert.Equal(totale.Cell(c).GetDouble(), righeNum.Sum(r => r.Cell(c).GetDouble()), 6);
        }
    }

    [Fact]
    public void SoloNumeri_NonScriveBlocchiPercentuali_SoloPercentuali_NonScriveBlocchiNumero()
    {
        using (var wb = Genera(true, false))
            foreach (var nome in NomiFogliStandard())
                Assert.DoesNotContain(wb.Worksheet(nome).CellsUsed(), c => c.GetString().EndsWith(" - %", StringComparison.Ordinal));
        using (var wb = Genera(false, true))
            foreach (var nome in NomiFogliStandard())
            {
                var ws = wb.Worksheet(nome);
                Assert.Contains(ws.CellsUsed(), c => c.GetString().EndsWith(" - %", StringComparison.Ordinal));
                Assert.DoesNotContain(ws.CellsUsed(), c => c.Address.ColumnNumber == 1 && c.GetString().EndsWith(LocalizationService.Instance.Scegli(" - Numero", " - Count", " - Nombre"), StringComparison.Ordinal));
            }
    }

    [Fact]
    public void FunzionalitaSenzaCategoria_HaEtichettaLocalizzataENonStringaVuota()
    {
        var funzioni = new List<Funzionalita> { new() { Nome = "F1", Categorie = ["Cat1"] }, new() { Nome = "F3", Categorie = [] } };
        var test = new[] { T("1", "F1", "BCM", "Daily A", Esito.Conforme), T("2", "F3", "BCM", "Daily A", Esito.Conforme) };
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Funzionalita };
        var preparata = ReportMatriceService.Prepara(test, config, Veicoli, funzioni, []);
        using var wb = new XLWorkbook();
        var fonti = ReportExportArricchimentiService.ScriviPivotStato(wb, preparata, config, funzioni, [], CancellationToken.None);
        Assert.False(fonti.Troncata);
        var senza = LocalizationService.Instance.Scegli("Senza categoria", "No category", "Sans catégorie");
        var ecuCat = Blocchi(wb.Worksheet(NomiFogliStandard()[0])).First().Value;
        Assert.Contains(ecuCat.Keys, k => k.Y == senza && k.X == "BCM");
        Assert.DoesNotContain(ecuCat.Keys, k => k.Y.Length == 0);
        var categorie = fonti.Categorie;
        Assert.Contains(Enumerable.Range(2, 5).Select(r => categorie.Cell(r, 4).GetString()), v => v == senza);
    }

    [Fact]
    public void TroppiValoriPerColonne_ScriveFoglioDiAvvisoSenzaEccezione()
    {
        var xs = Enumerable.Range(0, 16_001).Select(i => "E" + i).ToList();
        using var wb = new XLWorkbook();
        ReportExportArricchimentiService.ScriviIncrocioBlocchi(wb, "Troppe", "Componente", "Categoria", xs, ["C"], ["Stato"],
            (_, _, _) => 1, "nota", true, true, CancellationToken.None);
        var ws = wb.Worksheet("Troppe");
        Assert.Equal(LocalizationService.Instance.Scegli("Avviso", "Warning", "Avertissement"), ws.Cell(1, 1).GetString());
        Assert.Contains("16", ws.Cell(2, 1).GetString());
        Assert.Equal(2, ws.LastRowUsed()!.RowNumber());
    }

    [Fact]
    public void UnitaDurata_HaEtichetteLocalizzateNonIlNomeDellEnum()
    {
        var loc = LocalizationService.Instance;
        Assert.Equal(loc.Scegli("Giorni", "Days", "Jours"), UnitaDurataEtichette.Etichetta(UnitaDurataNc.Giorni));
        Assert.Equal(loc.Scegli("Settimane", "Weeks", "Semaines"), UnitaDurataEtichette.Etichetta(UnitaDurataNc.Settimane));
        Assert.Equal(loc.Scegli("Mesi", "Months", "Mois"), UnitaDurataEtichette.Etichetta(UnitaDurataNc.Mesi));
    }

    [Fact]
    public void NessunaDelleDueMisure_ERifiutato()
    {
        var config = new ConfigurazioneMatriceReport();
        var preparata = ReportMatriceService.Prepara([], config, [], [], []);
        using var wb = new XLWorkbook();
        Assert.Throws<ArgumentException>(() => ReportExportArricchimentiService.ScriviPivotStato(wb, preparata, config, [], [], CancellationToken.None, false, false));
    }

    // ---- Misura / campo valore della pivot generica (letta dal file salvato)

    private static readonly ExportColonna[] Campi =
    [
        new("gruppo", "Gruppo"), new("stato", "Stato"), new("giorni", "Giorni", true), new("testo", "Testo"),
    ];

    private XLWorkbook Salva(IEnumerable<IReadOnlyList<object?>> righe, string? percentuale, string? metrica, string? campo)
    {
        using var wb = new XLWorkbook();
        ExportPivotService.Scrivi(wb, Campi, righe, "gruppo", "stato", percentuale, default, null, metrica, campo);
        var percorso = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        wb.SaveAs(percorso);
        return new XLWorkbook(percorso);
    }

    private static IReadOnlyList<object?>[] Righe() =>
    [
        new object?[] { "A", "Aperto", 10, "x" }, new object?[] { "A", "Chiuso", 20.5, "x" },
        new object?[] { "B", "Aperto", null, "x" }, new object?[] { "B", "Aperto", "", "x" }, new object?[] { "B", "Chiuso", 4, "x" },
    ];

    [Fact]
    public void Somma_UsaIlCampoNumericoEScriveNumeriNelSorgente()
    {
        using var wb = Salva(Righe(), "totale", "somma", "giorni");
        var pivot = wb.Worksheets.Single(w => w.PivotTables.Any()).PivotTables.Single();
        Assert.Contains(pivot.Values, v => v.SourceName == "Giorni" && v.SummaryFormula == XLPivotSummary.Sum);
        var valoreSomma = pivot.Values.First(v => v.SourceName == "Giorni");
        Assert.Contains("Giorni", valoreSomma.CustomName);
        var origine = wb.Worksheets.Single(w => !w.PivotTables.Any() && w.Name != "Pivot");
        Assert.Equal(10, origine.Cell(2, 3).GetDouble());
        Assert.Equal(XLDataType.Number, origine.Cell(3, 3).DataType);
        Assert.True(origine.Cell(4, 3).IsEmpty());
        Assert.True(origine.Cell(5, 3).IsEmpty());
        var nota = wb.Worksheet("Pivot").Cell(3, 1).GetString();
        Assert.Contains("Giorni", nota);
        Assert.Contains("totale", nota, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(pivot.Values, v => v.CustomName.StartsWith("% "));
    }

    [Fact]
    public void Media_EscludeVuotiEOmettePercentualeConNota()
    {
        using var wb = Salva(Righe(), "riga", "media", "giorni");
        var pivot = wb.Worksheets.Single(w => w.PivotTables.Any()).PivotTables.Single();
        Assert.Single(pivot.Values);
        Assert.Equal(XLPivotSummary.Average, pivot.Values.Single().SummaryFormula);
        var nota = wb.Worksheet("Pivot").Cell(3, 1).GetString();
        Assert.Contains("vuot", nota, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Conteggio_EDefault_ConDenominatoreEsplicitoNellIntestazione()
    {
        using var wb = Salva(Righe(), "colonna", null, null);
        var pivot = wb.Worksheets.Single(w => w.PivotTables.Any()).PivotTables.Single();
        Assert.Equal(2, pivot.Values.Count());
        Assert.Contains(pivot.Values, v => v.CustomName.Contains("colonna", StringComparison.OrdinalIgnoreCase) && v.CustomName.StartsWith("%"));
    }

    [Fact]
    public void ValoreNonNumerico_DaErroreChiaroSenzaConvertireA0()
    {
        var righe = new IReadOnlyList<object?>[] { new object?[] { "A", "Aperto", "abc", "x" } };
        using var wb = new XLWorkbook();
        var ex = Assert.Throws<InvalidOperationException>(() => ExportPivotService.Scrivi(wb, Campi, righe, "gruppo", "stato", null, default, null, "somma", "giorni"));
        Assert.Contains("abc", ex.Message);
        Assert.Contains("Giorni", ex.Message);
    }

    [Fact]
    public void CampoNonNumericoOMancante_VieneRifiutato()
    {
        using var wb = new XLWorkbook();
        Assert.Throws<ArgumentException>(() => ExportPivotService.Scrivi(wb, Campi, Righe(), "gruppo", "stato", null, default, null, "somma", "testo"));
        Assert.Throws<ArgumentException>(() => ExportPivotService.Scrivi(wb, Campi, Righe(), "gruppo", "stato", null, default, null, "media", null));
        Assert.Throws<ArgumentException>(() => ExportPivotService.Scrivi(wb, Campi, Righe(), "gruppo", "stato", null, default, null, "mediana", "giorni"));
    }

    [Fact]
    public void ConfigurazioneConMisura_ViaOverloadDiScelta()
    {
        var scelta = new ExportConfigurazione { PivotRighe = "gruppo", PivotColonne = "stato", PivotMetrica = "somma", PivotCampoValore = "giorni" };
        using var wb = new XLWorkbook();
        ExportPivotService.Scrivi(wb, Campi, Righe(), scelta);
        Assert.Contains(wb.Worksheet("Pivot").PivotTables.Single().Values, v => v.SourceName == "Giorni");
    }

    [Fact]
    public void Nc_E_Cr_ConGiorniDiVita_SommaSuFileSalvato()
    {
        var apertura = new DateTime(2026, 4, 1);
        var nc = new NonConformita { Id = "NC1", DataApertura = apertura, DataChiusura = apertura.AddDays(9) };
        Assert.Equal(9, ExportPivotService.GiorniDiVita(nc.DataApertura, nc.DataChiusura, new DateTime(2026, 6, 1)));
        Assert.Equal(61, ExportPivotService.GiorniDiVita(apertura, null, new DateTime(2026, 6, 1)));
        Assert.Null(ExportPivotService.GiorniDiVita(default, null, DateTime.Today));
        Assert.Null(ExportPivotService.GiorniDiVita(apertura, apertura.AddDays(-1), DateTime.Today));
        Assert.Contains(NonConformitaExportService.CampiPivot(), c => c.Numerica && c.Id == "Giorni di vita");
        Assert.Contains(ChangeRequestExportService.CampiPivot(), c => c.Numerica && c.Id == "Giorni di vita");
        var cr = new ChangeRequest { Id = "CR1", DataCreazione = apertura, ChiusaIl = apertura.AddDays(3) };
        var riga = Assert.Single(ChangeRequestExportService.RighePivot([cr], null, new DateTime(2026, 6, 1)));
        Assert.Equal(ChangeRequestExportService.CampiPivot().Count, riga.Count);
        Assert.Equal(3, riga[^1]);
        using var wb = new XLWorkbook();
        ExportPivotService.Scrivi(wb, ChangeRequestExportService.CampiPivot(), [riga], "Stato", "Responsabile", null, default, null, "somma", "Giorni di vita");
        var percorso = Path.Combine(_dir, "cr.xlsx");
        wb.SaveAs(percorso);
        using var riaperto = new XLWorkbook(percorso);
        Assert.Contains(riaperto.Worksheets.Single(w => w.PivotTables.Any()).PivotTables.Single().Values, v => v.SummaryFormula == XLPivotSummary.Sum && v.CustomName.Contains(ExportPivotService.EtichettaGiorniDiVita));
    }
}
