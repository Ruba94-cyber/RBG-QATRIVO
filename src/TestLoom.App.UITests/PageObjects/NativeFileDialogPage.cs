using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>
/// Dialog di sistema Windows "Apri" / "Salva con nome", aperto da StorageProvider (OpenFilePickerAsync per
/// l'import Excel, SaveFilePickerAsync per export Excel/PDF). Non è un controllo Avalonia e non ha AutomationId
/// propri: è un common item dialog (classe finestra "#32770") di proprietà del processo TestLoom.
/// Verificato dal vivo (probe 2026-10-10): il dialog NON compare tra le finestre di primo livello
/// (<see cref="TestLoomApplication.TopLevelWindows"/>) ma come figlio diretto della finestra principale nell'albero UIA.
/// Il campo "Nome file" è un Edit con AutomationId "1148" nel dialog di apertura, e un Edit dentro il contenitore
/// "FileNameControlHost" in quello di salvataggio; la conferma è il pulsante Id "1" (Apri/Salva), l'annullamento Id "2".
/// Il selettore di cartella (FolderPickerOpenOptions, titolo "Scegli dove salvare il pacchetto..."), stesso dialog
/// comune con FOS_PICKFOLDERS, ha invece un Edit "Cartella:" con AutomationId "1152" e la conferma "Selezione cartella"
/// con Id "1" (osservato nell'albero UIA del fallimento CrEsportaPacchetto, uifailures-e4cc00c).
/// FindFirstDescendant non è affidabile su questi controlli ospitati: la ricerca percorre i figli con FindAllChildren.
/// Nessun percorso reale dell'utente viene mai scelto: i test passano sempre un file o una cartella temporanea del workspace di test.
/// </summary>
public sealed class NativeFileDialogPage
{
    private const string ClasseFinestra = "#32770";
    private const string CampoNomeFileId = "1148";
    private const string CampoCartellaId = "1152";
    private const string ContenitoreNomeFileSalvataggio = "FileNameControlHost";
    private const string ConfermaId = "1";
    private const string AnnullaId = "2";

    private readonly TestLoomApplication _app;
    private readonly AutomationElement _dialogo;

    private NativeFileDialogPage(TestLoomApplication app, AutomationElement dialogo)
    {
        _app = app;
        _dialogo = dialogo;
    }

    /// <summary>Attende il dialog di sistema Apri/Salva del processo TestLoom (può comparire con qualche secondo di
    /// ritardo dopo il click che lo apre, soprattutto alla prima apertura del processo).</summary>
    public static NativeFileDialogPage Attendi(TestLoomApplication app, TimeSpan? timeout = null)
    {
        var dialogo = Wait.Until<AutomationElement>(
            () => TrovaDialogo(app),
            d => CampoNomeFile(d) != null,
            timeout ?? TimeSpan.FromSeconds(30),
            "dialog di sistema Apri/Salva con nome");
        return new NativeFileDialogPage(app, dialogo);
    }

    /// <summary>Dialog di sistema del processo: finestra di primo livello oppure figlio diretto di una finestra
    /// di primo livello (è così che Windows lo espone con la finestra principale di TestLoom).</summary>
    private static AutomationElement? TrovaDialogo(TestLoomApplication app)
    {
        foreach (var finestra in app.TopLevelWindows())
        {
            if (EDialogoSistema(finestra)) return finestra;
            foreach (var figlio in FigliSicuri(finestra))
                if (EDialogoSistema(figlio)) return figlio;
        }
        return null;
    }

    private static bool EDialogoSistema(AutomationElement elemento)
    {
        try { return elemento.ClassName == ClasseFinestra; }
        catch { return false; }
    }

    /// <summary>Il campo "Nome file": Edit con AutomationId "1148" (apertura), oppure il campo "Cartella:" Edit con "1152"
    /// (selettore di cartella), oppure Edit dentro "FileNameControlHost" (salvataggio). Null finché il dialog non è pronto.</summary>
    private static AutomationElement? CampoNomeFile(AutomationElement dialogo)
    {
        var diretto = TrovaRicorsivo(dialogo, e => (e.AutomationId == CampoNomeFileId || e.AutomationId == CampoCartellaId)
                                                   && e.ControlType == ControlType.Edit);
        if (diretto != null) return diretto;
        var contenitore = TrovaRicorsivo(dialogo, e => e.AutomationId == ContenitoreNomeFileSalvataggio);
        return contenitore == null ? null : TrovaRicorsivo(contenitore, e => e.ControlType == ControlType.Edit);
    }

    private static AutomationElement[] FigliSicuri(AutomationElement radice)
    {
        try { return radice.FindAllChildren(); }
        catch { return Array.Empty<AutomationElement>(); }
    }

    private static AutomationElement? TrovaRicorsivo(AutomationElement radice, Func<AutomationElement, bool> predicato, int profonditaMax = 6)
    {
        var figli = FigliSicuri(radice);
        foreach (var figlio in figli)
            if (Soddisfa(figlio, predicato)) return figlio;
        if (profonditaMax == 0) return null;
        foreach (var figlio in figli)
        {
            var trovato = TrovaRicorsivo(figlio, predicato, profonditaMax - 1);
            if (trovato != null) return trovato;
        }
        return null;
    }

    private static bool Soddisfa(AutomationElement elemento, Func<AutomationElement, bool> predicato)
    {
        try { return predicato(elemento); }
        catch { return false; }
    }

    private AutomationElement PulsanteConId(string automationId) =>
        Wait.ForElement(() => TrovaRicorsivo(_dialogo, e => e.AutomationId == automationId, 2),
            $"pulsante del dialog di sistema con Id '{automationId}'");

    /// <summary>Scrive il percorso nel campo "Nome file" e conferma (Apri/Salva). Il chiamante attende poi l'effetto
    /// reale (la finestra successiva o il file scritto).</summary>
    public void ConfermaPercorso(string percorsoCompleto)
    {
        var campo = CampoNomeFile(_dialogo)
            ?? throw new InvalidOperationException("Campo 'Nome file' non trovato nel dialog di sistema.");
        campo.AsTextBox().Text = percorsoCompleto;
        PulsanteConId(ConfermaId).SafeInvoke();
        Wait.ForCondition(() => !Aperto, "chiusura del dialog di sistema dopo la conferma");
    }

    /// <summary>Annulla il dialog di sistema tramite il pulsante "Annulla" (Id "2") e attende la chiusura.</summary>
    public void Annulla()
    {
        PulsanteConId(AnnullaId).SafeInvoke();
        Wait.ForCondition(() => !Aperto, "chiusura del dialog di sistema dopo Annulla");
    }

    /// <summary>Titolo del dialog di sistema (es. "Salva grafico come PNG"), per riconoscere quale export è aperto.</summary>
    public string Titolo => _dialogo.Name ?? string.Empty;

    /// <summary>Vero finché il dialog di sistema di questo processo è ancora aperto.</summary>
    public bool Aperto
    {
        get
        {
            var trovato = TrovaDialogo(_app);
            return trovato != null && CampoNomeFile(trovato) != null;
        }
    }
}
