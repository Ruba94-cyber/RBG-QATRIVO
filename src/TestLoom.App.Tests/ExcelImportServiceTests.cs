using ClosedXML.Excel;
using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>Copertura del punto 8 (feedback utente 2026-08-20): import posizionale (colonna A/B/C... = ordine
/// delle colonne a schermo), non più per nome di intestazione — il testo in cima alla colonna non viene più
/// letto per il significato, solo la sua posizione conta.</summary>
public class ExcelImportServiceTests
{
    private static MemoryStream CreaWorkbook(string[] headers, params string[][] righe)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        for (var r = 0; r < righe.Length; r++)
            for (var c = 0; c < righe[r].Length; c++)
                ws.Cell(r + 2, c + 1).Value = righe[r][c];

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void LeggiRigheOrdinate_MappaColonnePerPosizione_IgnorandoLIntestazione()
    {
        using var stream = CreaWorkbook(new[] { "Qualunque Testo", "Anche Questo Non Conta" },
            new[] { "Componente_A", "" },
            new[] { "Componente_B", "x" });

        var righe = ExcelImportService.LeggiRigheOrdinate(stream, null, 1);

        Assert.Equal(2, righe.Count);
        Assert.Equal("Componente_A", righe[0][0]);
        Assert.Equal("Componente_B", righe[1][0]);
        Assert.Equal("x", righe[1][1]);
    }

    [Fact]
    public void LeggiRigheOrdinate_DatiInColonnaSenzaIntestazione_NonImportatiMaSegnalati()
    {
        // L'intestazione copre solo A:B: il dato in C non entra nell'import e l'operatore deve saperlo.
        using var stream = CreaWorkbook(new[] { "Nome", "Gamma" },
            new[] { "Daily 24", "Gamma Media", "dato senza intestazione" });
        var avvisi = new List<string>();

        var righe = ExcelImportService.LeggiRigheOrdinate(stream, null, 1, avvisi);

        Assert.Equal(2, righe[0].Count);
        var avviso = Assert.Single(avvisi);
        Assert.Equal(string.Format(LocalizationService.Instance.Traduci("ImportColonneSenzaIntestazioneFmt"), "C"), avviso);
    }

    [Fact]
    public void LeggiRigheOrdinate_TuttiIDatiNellIntestazione_NessunAvviso()
    {
        using var stream = CreaWorkbook(new[] { "Nome", "Gamma" }, new[] { "Daily 24", "Gamma Media" });
        var avvisi = new List<string>();

        ExcelImportService.LeggiRigheOrdinate(stream, null, 1, avvisi);

        Assert.Empty(avvisi);
    }

    // Cuore del punto 8: prima l'intestazione doveva corrispondere esattamente al nome della proprietà C#
    // interna (es. "GammaNome"), non all'etichetta mostrata a schermo ("Gamma") — causa più probabile del
    // "non funziona" segnalato. Ora l'intestazione può essere qualunque cosa, anche l'etichetta a schermo.
    [Fact]
    public void LeggiRigheOrdinate_IntestazioneConEtichettaASchermo_FunzionaUgualmente()
    {
        using var stream = CreaWorkbook(new[] { "Nome", "Gamma" },
            new[] { "Daily 24", "Gamma Media" });

        var righe = ExcelImportService.LeggiRigheOrdinate(stream, null, 1);

        Assert.Single(righe);
        Assert.Equal("Daily 24", righe[0][0]);
        Assert.Equal("Gamma Media", righe[0][1]);
    }

    [Fact]
    public void LeggiRigheOrdinate_SaltaRigheCompletamenteVuote()
    {
        using var stream = CreaWorkbook(new[] { "Nome" },
            new[] { "Componente_A" },
            new[] { "" },
            new[] { "Componente_B" });

        var righe = ExcelImportService.LeggiRigheOrdinate(stream, null, 1);

        Assert.Equal(2, righe.Count);
        Assert.Equal("Componente_A", righe[0][0]);
        Assert.Equal("Componente_B", righe[1][0]);
    }

    // Regressione punto 5 (feedback utente 2026-08-25): file reale con un banner/titolo unito su più colonne
    // sulla riga 1 (es. "Acme Requirements" unito A1:F1) sopra la vera intestazione (riga 2). Prima, la riga 1
    // veniva presa come intestazione e il conteggio colonne si fermava a 1 (solo la cella-àncora del merge
    // risulta "usata"), perdendo la colonna B di ogni riga. Ora l'utente indica esplicitamente la riga 2.
    [Fact]
    public void LeggiRigheOrdinate_ConBannerUnitoSopraLIntestazione_LeggeCorrettamenteIndicandoLaRigaGiusta()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("English");
        ws.Range(1, 1, 1, 6).Merge();
        ws.Cell(1, 1).Value = "Acme Requirements";
        ws.Cell(2, 1).Value = "ID"; ws.Cell(2, 2).Value = "Requirement";
        ws.Cell(3, 1).Value = "ST01"; ws.Cell(3, 2).Value = "The componente shall be programmable.";
        ws.Cell(4, 1).Value = "ST02"; ws.Cell(4, 2).Value = "Every CAN control unit shall comply.";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var righe = ExcelImportService.LeggiRigheOrdinate(ms, "English", 2);

        Assert.Equal(2, righe.Count);
        Assert.Equal("ST01", righe[0][0]);
        Assert.Equal("The componente shall be programmable.", righe[0][1]);
        Assert.Equal("ST02", righe[1][0]);
        Assert.Equal("Every CAN control unit shall comply.", righe[1][1]);
    }

    [Fact]
    public void ElencaFogli_RestituisceINomiNellOrdine()
    {
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("English");
        wb.Worksheets.Add("Italiano");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var fogli = ExcelImportService.ElencaFogli(ms);

        Assert.Equal(new[] { "English", "Italiano" }, fogli);
    }

    [Fact]
    public void LeggiRigheOrdinate_FoglioVuoto_RestituisceListaVuota()
    {
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("Sheet1");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var righe = ExcelImportService.LeggiRigheOrdinate(ms, null, 1);

        Assert.Empty(righe);
    }
}
