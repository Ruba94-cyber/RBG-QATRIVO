using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Editor dedicato per un campo Tipo=ListaTesto (feedback utente 2026-08-24, punto 8): righe di
/// testo libero incollate o importate da file (es. un trace ValidationTool lungo, TX/RX riga per riga), ciascuna con
/// la propria evidenziazione via tasto destro — nessun pulsante dedicato in più per il colore, solo il menu
/// contestuale (vedi OnImpostaColoreRiga). A differenza di ListaOpzioniEditorWindow (una sola textarea) qui
/// serve un vero elenco di oggetti perché ogni riga porta anche un colore: l'incolla resta
/// un'aggiunta in blocco (mai deduplicata: un trace ripete le stesse righe molte volte, a differenza delle
/// opzioni di un campo Lista); l'import da file salta solo le righe già presenti nella lista (vedi
/// SeparaDuplicatiImportFile). Poi ogni riga si gestisce singolarmente.</summary>
public partial class ListaTestoEditorWindow : Window
{
    private readonly ObservableCollection<RigaEvidenziata> _righe = new();

    public ListaTestoEditorWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
        RigheItems.ItemsSource = _righe;
    }

    public static Task<List<RigaEvidenziata>?> Apri(Window owner, IReadOnlyList<RigaEvidenziata> righeAttuali)
    {
        var loc = LocalizationService.Instance;
        var w = new ListaTestoEditorWindow();
        var titolo = loc.Traduci("ListaTestoEditorTitolo");
        w.Title = titolo;
        w.TitoloText.Text = titolo;
        foreach (var r in righeAttuali) w._righe.Add(new RigaEvidenziata { Testo = r.Testo, Colore = r.Colore });

        var tcs = new TaskCompletionSource<List<RigaEvidenziata>?>();

        w.AggiungiRigheButton.Click += (_, _) => w.AggiungiRigheDaTesto();
        w.ImportaFileButton.Click += async (_, _) => await w.ImportaDaFile();

        w.SalvaButton.Click += (_, _) => { tcs.TrySetResult(w._righe.ToList()); w.Close(); };
        w.AnnullaButton.Click += (_, _) => { tcs.TrySetResult(null); w.Close(); };
        w.Closed += (_, _) => tcs.TrySetResult(null);

        w.ShowDialog(owner);
        return tcs.Task;
    }

    /// <summary>Split grezzo: solo fine riga normalizzata, nessun trim/dedup — le righe vuote (separatori
    /// tipo "----...") e i duplicati di un trace vanno preservati esattamente come incollati. A differenza di
    /// ListaOpzioniEditorWindow.ParseRighe (opzioni di un dropdown, dove i duplicati non hanno senso) qui un
    /// trace ripete le stesse righe molte volte di proposito. Pubblico e statico per essere testabile senza
    /// aprire una finestra Avalonia, stesso schema di ParseRighe.</summary>
    public static IEnumerable<string> DividiInRighe(string? testo) =>
        (testo ?? "").Replace("\r\n", "\n").Split('\n');

    private void AggiungiRigheDaTesto()
    {
        if (string.IsNullOrEmpty(IncollaBox.Text)) return;
        foreach (var riga in DividiInRighe(IncollaBox.Text)) _righe.Add(new RigaEvidenziata { Testo = riga });
        IncollaBox.Text = "";
    }

    private async Task ImportaDaFile()
    {
        try
        {
            var file = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("ListaScegliFileTitolo"),
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType> { new("Testo/log") { Patterns = new[] { "*.txt", "*.log" } }, FilePickerFileTypes.All }
            });
            var percorso = file.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(percorso) || !File.Exists(percorso)) return;

            var righeFile = File.ReadAllLines(percorso);
            if (righeFile.Length == 0) return;
            var (daAggiungere, duplicate) = SeparaDuplicatiImportFile(_righe.Select(r => r.Testo), righeFile);
            // UX-4: conferma con i conteggi prima di aggiungere; le righe gia presenti nella lista vengono saltate.
            if (daAggiungere.Count == 0)
            {
                await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("ListaImportaRigheTitolo"),
                    string.Format(LocalizationService.Instance.Traduci("ListaImportaNessunaNuovaFmt"), duplicate), LocalizationService.Instance.Traduci("OkBtn"), "");
                return;
            }
            if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("ListaImportaRigheTitolo"),
                    string.Format(LocalizationService.Instance.Traduci("ListaImportaNuoveFmt"), daAggiungere.Count, duplicate) + "\n" +
                    LocalizationService.Instance.Traduci("ListaImportaGiaPresentiTesto"),
                    LocalizationService.Instance.Traduci("AggiungiBtnSemplice"), LocalizationService.Instance.Traduci("Annulla"))) return;
            foreach (var riga in daAggiungere) _righe.Add(new RigaEvidenziata { Testo = riga });
        }
        catch (Exception ex) { CrashLogService.Registra("Import file lista testo", ex); }
    }

    /// <summary>Import da file (UX-4): le righe del file gia presenti nella lista vengono saltate (confronto esatto sul
    /// testo); le altre si aggiungono nell'ordine del file. Le ripetizioni dentro il file restano, perche' un trace le
    /// usa di proposito. Pubblico e statico per essere testabile senza aprire la finestra.</summary>
    public static (IReadOnlyList<string> DaAggiungere, int Duplicate) SeparaDuplicatiImportFile(
        IEnumerable<string> righeLista, IReadOnlyList<string> righeFile)
    {
        var presenti = new HashSet<string>(righeLista, StringComparer.Ordinal);
        var daAggiungere = new List<string>(righeFile.Count);
        var duplicate = 0;
        foreach (var riga in righeFile)
        {
            if (presenti.Contains(riga)) duplicate++;
            else daAggiungere.Add(riga);
        }
        return (daAggiungere, duplicate);
    }

    private void OnRimuoviRiga(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RigaEvidenziata riga }) _righe.Remove(riga);
    }

    private void OnImpostaColoreRiga(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: RigaEvidenziata riga, Tag: string coloreTesto }) return;
        if (Enum.TryParse<ColoreEvidenziazione>(coloreTesto, out var colore)) riga.Colore = colore;
    }
}
