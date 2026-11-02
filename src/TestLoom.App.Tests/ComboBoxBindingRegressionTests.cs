using System.Text.RegularExpressions;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al bug segnalato dall'utente 2026-08-20: due AutoCompleteBox (componente e Veicolo in
/// TestCaseEditorView) usavano Text="{Binding ...}" invece di SelectedItem — il testo digitato ma non
/// selezionato dalla lista veniva scritto direttamente nel modello ad ogni carattere, sovrascrivendo
/// silenziosamente una selezione valida al salvataggio. Tutti gli altri AutoCompleteBox dell'app usavano già
/// SelectedItem correttamente; qui si blocca una regressione futura scansionando ogni file .axaml.</summary>
public class ComboBoxBindingRegressionTests
{
    private static string ViewsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "TestLoom.App", "Views");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir, "src", "TestLoom.App", "Views");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Cartella Views non trovata a partire da " + AppContext.BaseDirectory);
    }

    // Cattura "<AutoCompleteBox" seguito, ovunque prima del successivo ">" di apertura tag, da Text="{Binding
    // — ammettendo che gli attributi siano su più righe, come nel markup reale.
    private static readonly Regex TextBindingSuAutoCompleteBox = new(
        @"<AutoCompleteBox\b(?:[^>]|\r|\n)*?\bText=""\{Binding", RegexOptions.Compiled);

    [Fact]
    public void NessunAutoCompleteBox_UsaTextInveceDiSelectedItem_InTuttaLApp()
    {
        var violazioni = new List<string>();
        foreach (var file in Directory.GetFiles(ViewsDir(), "*.axaml", SearchOption.AllDirectories))
        {
            var contenuto = File.ReadAllText(file);
            if (TextBindingSuAutoCompleteBox.IsMatch(contenuto))
                violazioni.Add(Path.GetFileName(file));
        }

        Assert.True(violazioni.Count == 0,
            "AutoCompleteBox con Text=\"{Binding...}\" invece di SelectedItem (testo digitato ma non selezionato " +
            "sovrascrive silenziosamente il valore salvato) trovati in: " + string.Join(", ", violazioni));
    }

    /// <summary>Aggiornato 2026-08-20 (stesso giorno, secondo follow-up): componente/Veicolo sono passati prima da
    /// AutoCompleteBox (popup non affidabile, AvaloniaUI/Avalonia#8903) a TextBox filtro + ListBox sempre
    /// visibile — ma anche quello, su liste potenzialmente enormi (centinaia di componente, fino a 80 veicoli),
    /// allungava ogni riga del form anche a riposo. Ora è un Button che apre SelezionaDialog (un dialog
    /// dedicato con ricerca): stesso principio di sempre (la selezione vive SOLO nella proprietà bindata a
    /// Command, mai nel testo digitato), ma compatto come un campo normale.</summary>
    [Fact]
    public void TestCaseEditorView_EcuEVeicolo_UsanoPickerDialog()
    {
        var file = Path.Combine(ViewsDir(), "TestCaseEditorView.axaml");
        var contenuto = File.ReadAllText(file);
        Assert.Contains("Command=\"{Binding ScegliEcuCommand}\"", contenuto);
        Assert.Contains("Command=\"{Binding ScegliVeicoloCommand}\"", contenuto);
    }

    [Fact]
    public void FunzionalitaOperazione_NonPuoRiscrivereNullDuranteIlCaricamento()
    {
        var file = Path.Combine(ViewsDir(), "TestCaseEditorView.axaml");
        var contenuto = File.ReadAllText(file);

        Assert.Contains("SelectedItem=\"{Binding Funzionalita, Mode=OneWay}\"", contenuto);
        Assert.Contains("SelectionChanged=\"OnFunzionalitaOperazioneModificata\"", contenuto);
    }

    /// <summary>Punti 6/13: la lista completa deve aprirsi al primo click/focus, non solo iniziando a scrivere.
    /// Applicato globalmente via uno stile su Styles.axaml (AutoCompleteBoxBehavior.ApriAlFocus) invece che
    /// view per view — questo test blocca sia la rimozione accidentale della regola sia una regressione a un
    /// approccio "per singola view" che dimentichi qualche combobox futura.</summary>
    [Fact]
    public void StylesGlobali_ApronoLaListaAlFocus_PerOgniAutoCompleteBoxDellApp()
    {
        var dir = ViewsDir();
        var stylesFile = Path.Combine(Path.GetDirectoryName(dir)!, "Styles.axaml");
        Assert.True(File.Exists(stylesFile), $"Styles.axaml non trovato in {stylesFile}");
        var contenuto = File.ReadAllText(stylesFile);

        Assert.Contains("Selector=\"AutoCompleteBox\"", contenuto);
        Assert.Contains("AutoCompleteBoxBehavior.ApriAlFocus", contenuto);
        Assert.Contains("Value=\"True\"", contenuto);
    }
}
