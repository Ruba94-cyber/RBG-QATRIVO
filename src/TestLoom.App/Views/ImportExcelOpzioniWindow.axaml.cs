using Avalonia.Controls;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Chiede foglio e riga di intestazione prima di un import Excel (feedback utente 2026-08-25, punto 5)
/// invece di dedurli automaticamente da "primo foglio" e "prima riga non vuota", che con un banner/titolo unito
/// sopra l'intestazione o un workbook multi-foglio (es. versione bilingue) facevano perdere colonne o righe in
/// silenzio.</summary>
public partial class ImportExcelOpzioniWindow : Window
{
    public ImportExcelOpzioniWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
        ImportaButton.Click += (_, _) =>
        {
            (string? Foglio, int Riga)? risultato = ((string?)FoglioCombo.SelectedItem, (int)(RigaUpDown.Value ?? 1m));
            Close(risultato);
        };
        AnnullaButton.Click += (_, _) => Close(((string? Foglio, int Riga)?)null);
    }

    public static ImportExcelOpzioniWindow Crea(IReadOnlyList<string> fogli)
    {
        var w = new ImportExcelOpzioniWindow();
        w.FoglioCombo.ItemsSource = fogli;
        w.FoglioCombo.SelectedIndex = fogli.Count > 0 ? 0 : -1;
        return w;
    }

    /// <summary>Stesso schema "leggi opzioni -> chiama comando" per tutte le viste master data che offrono
    /// l'import Excel, invece di ripetere apertura file + lettura fogli + dialog in ognuna.</summary>
    public static async Task<ImportExcelRichiesta?> ChiediOpzioni(Window? proprietario, string percorsoFile)
    {
        if (proprietario == null) return null;
        List<string> fogli;
        using (var fs = File.OpenRead(percorsoFile)) fogli = ExcelImportService.ElencaFogli(fs);
        if (fogli.Count == 0) return null;

        var dialogo = Crea(fogli);
        var risultato = await dialogo.ShowDialog<(string? Foglio, int Riga)?>(proprietario);
        return risultato == null ? null : new ImportExcelRichiesta(percorsoFile, risultato.Value.Foglio, risultato.Value.Riga);
    }
}
