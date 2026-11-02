using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Un campo generico da mostrare in anteprima, indipendente da dove viene (CampoTemplate di un Tipo
/// di test o DefinizioneCampoStrumento di uno Strumento) — feedback utente 2026-08-20, punto 4: anteprima live
/// di come apparirà il form, sia per i Tipi di test sia per gli Strumenti (stessa sezione dedicata che
/// comparirebbe nel form quando lo strumento è selezionato).</summary>
public sealed class PreviewCampo
{
    public required string Nome { get; init; }
    public required TipoCampoStrumento Tipo { get; init; }
    public bool Obbligatorio { get; init; }
    public IReadOnlyList<string> OpzioniLista { get; init; } = Array.Empty<string>();
    public string? UnitaMisura { get; init; }
}

public sealed class PreviewSezione
{
    public required string Titolo { get; init; }
    public required IReadOnlyList<PreviewCampo> Campi { get; init; }
}

/// <summary>Anteprima live del form di compilazione (feedback utente 2026-08-20, punto 4): non un'anteprima
/// del formato di esportazione, ma un rendering WYSIWYG delle sezioni/campi così come appariranno
/// nell'editor del test, con gli stessi controlli rappresentativi usati lì (TestCaseEditorView) per tipo di
/// campo — TextBox/Numero+unità/ComboBox/ToggleSwitch/CalendarDatePicker. Sono disabilitati (IsEnabled=False):
/// serve solo a vedere la forma del form, non a compilarlo davvero.</summary>
public static class TestFormPreviewRenderer
{
    /// <summary>Mappa l'intero foglio di un TestTemplate: Pianificazione e Riferimenti (campi piatti, senza
    /// una vera "sezione" nel modello) più ogni sezione di Dettagli — non solo Dettagli (bug segnalato
    /// dall'utente 2026-08-20: l'anteprima mostrava solo Dettagli, mentre il template è personalizzabile per
    /// intero foglio). I campi di sistema (Tipo=CampoSistema) non hanno un tipo di controllo proprio in questo
    /// livello di astrazione — quale controllo useranno dipende dalla chiave (Componente è una scelta, Matricola è testo,
    /// ecc.), quindi qui sono rappresentati genericamente come testo: l'obiettivo dell'anteprima è la
    /// forma/ordine delle sezioni e dei campi, non la fedeltà pixel-per-pixel di ognuno.</summary>
    public static List<PreviewSezione> DaTemplate(TestTemplate template)
    {
        PreviewSezione? SezionePiatta(string titolo, List<CampoTemplate> campi) => campi.Count == 0 ? null : new PreviewSezione
        {
            Titolo = titolo,
            Campi = campi.OrderBy(c => c.Ordine).Select(CampoTemplateAPreview).ToList(),
        };

        var risultato = new List<PreviewSezione>();
        var pianificazione = SezionePiatta(LocalizationService.Instance.Traduci("TplPianificazioneBreve"), template.CampiPianificazione);
        if (pianificazione != null) risultato.Add(pianificazione);
        var riferimenti = SezionePiatta(LocalizationService.Instance.Traduci("TplRiferimentiBreve"), template.CampiRiferimenti);
        if (riferimenti != null) risultato.Add(riferimenti);
        risultato.AddRange(template.Sezioni.OrderBy(s => s.Ordine).Select(sezione => new PreviewSezione
        {
            Titolo = sezione.Nome,
            Campi = sezione.Campi.OrderBy(c => c.Ordine).Select(CampoTemplateAPreview).ToList(),
        }));
        return risultato;
    }

    private static PreviewCampo CampoTemplateAPreview(CampoTemplate campo) => new()
    {
        Nome = string.IsNullOrWhiteSpace(campo.Nome) ? "(senza nome)" : campo.Nome,
        Tipo = campo.Tipo == TipoCampoTemplate.CampoSistema ? TipoCampoStrumento.Testo : campo.TipoLibero,
        Obbligatorio = campo.Obbligatorio,
        OpzioniLista = campo.OpzioniListaLibero,
    };

    /// <summary>Un solo strumento come sezione unica: è così che comparirebbe nel form quando selezionato tra
    /// gli strumenti usati per l'operazione.</summary>
    public static PreviewSezione DaStrumento(StrumentoDiagnostico strumento) => new()
    {
        Titolo = strumento.Nome,
        // OrderBy(Ordine) (revisione F1 .ai/artifacts/review_riordino.json): stesso ordine usato dal form
        // reale in CampoStrumentoValoreLayout.SpaccaPerGruppo, così l'anteprima non mente sulla posizione.
        Campi = strumento.Campi.OrderBy(c => c.Ordine).Select(c => new PreviewCampo
        {
            Nome = string.IsNullOrWhiteSpace(c.Nome) ? "(senza nome)" : c.Nome,
            Tipo = c.Tipo,
            OpzioniLista = c.OpzioniLista,
            UnitaMisura = c.UnitaMisura,
        }).ToList(),
    };

    public static Control Renderizza(IReadOnlyList<PreviewSezione> sezioni)
    {
        var root = new StackPanel { Spacing = 12 };
        if (sezioni.Count == 0 || sezioni.All(s => s.Campi.Count == 0))
        {
            root.Children.Add(new TextBlock
            {
                Text = LocalizationService.Instance.Traduci("AnteprimaFormVuota"),
                Classes = { "Muted" }, FontStyle = FontStyle.Italic
            });
            return root;
        }

        foreach (var sezione in sezioni)
        {
            if (sezione.Campi.Count == 0) continue;
            var card = new Border { Classes = { "Card" } };
            var contenuto = new StackPanel { Spacing = 10 };
            contenuto.Children.Add(new TextBlock { Text = sezione.Titolo, Classes = { "SectionTitle" } });

            var griglia = new WrapPanel();
            foreach (var campo in sezione.Campi)
            {
                var colonna = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 0, 16, 12), MinWidth = 220, MaxWidth = 320 };
                var etichetta = campo.Obbligatorio ? campo.Nome + " *" : campo.Nome;
                colonna.Children.Add(new TextBlock { Text = etichetta, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#64748B") });
                colonna.Children.Add(CreaControlloRappresentativo(campo));
                griglia.Children.Add(colonna);
            }
            contenuto.Children.Add(griglia);
            card.Child = contenuto;
            root.Children.Add(card);
        }
        return root;
    }

    private static Control CreaControlloRappresentativo(PreviewCampo campo)
    {
        Control controllo = campo.Tipo switch
        {
            TipoCampoStrumento.Numero => CreaCampoNumero(campo.UnitaMisura),
            TipoCampoStrumento.Lista => new ComboBox { ItemsSource = campo.OpzioniLista, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = campo.OpzioniLista.Count == 0 ? LocalizationService.Instance.Traduci("AnteprimaListaVuota") : null },
            TipoCampoStrumento.Checkbox => new ToggleSwitch(),
            TipoCampoStrumento.Data => new CalendarDatePicker { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "gg/mm/aaaa" },
            TipoCampoStrumento.File => new TextBlock { Text = LocalizationService.Instance.Traduci("NessunFileCollegato"), Classes = { "Muted" } },
            _ => new TextBox { Classes = { "Field" } },
        };
        controllo.IsEnabled = false;
        return controllo;
    }

    private static Control CreaCampoNumero(string? unita)
    {
        if (string.IsNullOrWhiteSpace(unita)) return new TextBox { Classes = { "Field" } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var box = new TextBox { Classes = { "Field" } };
        var label = new TextBlock { Text = unita, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(6, 0, 0, 0), Classes = { "Muted" } };
        Grid.SetColumn(box, 0); Grid.SetColumn(label, 1);
        grid.Children.Add(box); grid.Children.Add(label);
        return grid;
    }
}
