using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Dialogo per scegliere l'ordine dei valori di un asse della matrice Report: elenco scorrevole,
/// frecce su/giu' sul valore selezionato (pulsanti grandi per il touch), ripristino alfabetico.
/// Risultato: null = annullato; lista vuota = ripristino alfabetico; altrimenti il nuovo ordine.</summary>
public partial class OrdinaValoriWindow : Window
{
    private readonly ObservableCollection<string> _valori = new();
    private bool _ripristinato;

    public OrdinaValoriWindow()
    {
        InitializeComponent();
        Lista.ItemsSource = _valori;
        // Limiti e posizione come per tutti i dialoghi (AdattamentoDialogo): niente copia della formula qui.
        AdattamentoDialogo.Registra(this);
    }

    public static Task<List<string>?> Apri(Window owner, string etichettaDimensione, IReadOnlyList<string> valoriAttuali)
    {
        var loc = LocalizationService.Instance;
        var w = new OrdinaValoriWindow();
        var titolo = string.Format(loc.Traduci("MatriceOrdinaTitoloFmt"), etichettaDimensione);
        w.Title = titolo;
        w.TitoloText.Text = titolo;
        w.SottotitoloText.Text = loc.Traduci("MatriceOrdinaSottotitolo");
        w.RipristinaButton.Content = loc.Traduci("MatriceOrdinaRipristinaBtn");
        w.AnnullaButton.Content = loc.Traduci("Annulla");
        w.OkButton.Content = loc.Traduci("MatriceOrdinaOkBtn");
        ToolTip.SetTip(w.SuButton, loc.Traduci("MatriceOrdinaSuTip"));
        ToolTip.SetTip(w.GiuButton, loc.Traduci("MatriceOrdinaGiuTip"));
        foreach (var v in valoriAttuali) w._valori.Add(v);
        if (w._valori.Count > 0) w.Lista.SelectedIndex = 0;

        var tcs = new TaskCompletionSource<List<string>?>();
        w.SuButton.Click += (_, _) => w.Sposta(-1);
        w.GiuButton.Click += (_, _) => w.Sposta(+1);
        w.RipristinaButton.Click += (_, _) => w.Ripristina();
        w.OkButton.Click += (_, _) =>
        {
            tcs.TrySetResult(w._ripristinato ? new List<string>() : w._valori.ToList());
            w.Close();
        };
        w.AnnullaButton.Click += (_, _) => { tcs.TrySetResult(null); w.Close(); };
        w.Closed += (_, _) => tcs.TrySetResult(null);

        w.ShowDialog(owner);
        return tcs.Task;
    }

    private void Sposta(int delta)
    {
        var nuovo = OrdineAssiReportService.Sposta(_valori, Lista.SelectedIndex, delta);
        if (nuovo != Lista.SelectedIndex)
        {
            _ripristinato = false;
            Lista.SelectedIndex = nuovo;
        }
        if (Lista.SelectedItem != null) Lista.ScrollIntoView(Lista.SelectedItem);
    }

    private void Ripristina()
    {
        var selezionato = Lista.SelectedItem as string;
        var ordinati = OrdineAssiReportService.Ordina(_valori, null);
        _valori.Clear();
        foreach (var v in ordinati) _valori.Add(v);
        _ripristinato = true;
        if (selezionato != null) Lista.SelectedItem = selezionato;
    }
}
