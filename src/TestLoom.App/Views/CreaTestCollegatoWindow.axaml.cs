using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Menu interattivo "Crea test collegato" (feedback utente 2026-08-20, punto 17): sostituisce le
/// vecchie AzioneCollegataTemplate preconfigurate dal Master nel template — ora si sceglie ogni volta, a
/// runtime, quale/i tipo/i di test collegato creare (compreso lo stesso tipo del test di partenza, e più di
/// uno insieme) e quali campi portare nel/nei nuovo/i test, senza alcuna configurazione statica da seguire.</summary>
public partial class CreaTestCollegatoWindow : Window
{
    private HashSet<TestTemplate>? _templatiScelti;
    private HashSet<string>? _campiScelti;

    public CreaTestCollegatoWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
    }

    /// <summary>Ritorna (template di destinazione scelti, chiavi CampiSistema da copiare) se l'utente conferma,
    /// null se annulla/chiude. templateCorrente compare anch'esso tra le scelte (si può collegare un test allo
    /// stesso tipo di partenza), non è escluso.</summary>
    public static Task<(List<TestTemplate> templati, HashSet<string> campi)?> Apri(Window owner, IReadOnlyList<TestTemplate> tuttiITemplate)
    {
        var loc = LocalizationService.Instance;
        var w = new CreaTestCollegatoWindow { Title = loc.Traduci("CreaTestCollegatoTitolo") };
        var templatiScelti = new HashSet<TestTemplate>();
        var campiScelti = new HashSet<string>(CampiSistema.Chiavi); // default: tutti selezionati, comodo per il caso comune "copia tutto"
        w._templatiScelti = templatiScelti;
        w._campiScelti = campiScelti;

        var templatiNonArchiviati = tuttiITemplate.Where(t => !t.Archiviata).OrderBy(t => t.Nome).ToList();
        foreach (var template in templatiNonArchiviati)
        {
            var check = new CheckBox { Content = template.Nome };
            AutomationProperties.SetAutomationId(check, $"CreaTestCollegato_Template_{template.Nome}");
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) templatiScelti.Add(template); else templatiScelti.Remove(template);
                w.CreaButton.IsEnabled = templatiScelti.Count > 0;
            };
            w.TemplatiPanel.Children.Add(check);
        }
        // Ricerca solo oltre soglia (feedback utente 2026-08-21), stessa soglia di ListaOltreSogliaConverter.
        if (templatiNonArchiviati.Count > 10)
        {
            w.TemplatiRicercaBox.PlaceholderText = loc.Traduci("CercaPlaceholder");
            w.TemplatiRicercaBox.IsVisible = true;
            w.TemplatiRicercaBox.TextChanged += (_, _) =>
            {
                var q = w.TemplatiRicercaBox.Text ?? "";
                foreach (var check in w.TemplatiPanel.Children.OfType<CheckBox>())
                    check.IsVisible = string.IsNullOrWhiteSpace(q) || (check.Content?.ToString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
            };
        }

        foreach (var chiave in CampiSistema.Chiavi)
        {
            var check = new CheckBox { Content = CampiSistema.LabelDefault(chiave), IsChecked = true };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) campiScelti.Add(chiave); else campiScelti.Remove(chiave);
            };
            w.CampiPanel.Children.Add(check);
        }
        if (CampiSistema.Chiavi.Count > 10)
        {
            w.CampiRicercaBox.PlaceholderText = loc.Traduci("CercaPlaceholder");
            w.CampiRicercaBox.IsVisible = true;
            w.CampiRicercaBox.TextChanged += (_, _) =>
            {
                var q = w.CampiRicercaBox.Text ?? "";
                foreach (var check in w.CampiPanel.Children.OfType<CheckBox>())
                    check.IsVisible = string.IsNullOrWhiteSpace(q) || (check.Content?.ToString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
            };
        }

        w.CreaButton.IsEnabled = false;
        var tcs = new TaskCompletionSource<(List<TestTemplate>, HashSet<string>)?>();
        w.CreaButton.Click += (_, _) => { tcs.TrySetResult((templatiScelti.ToList(), campiScelti)); w.Close(); };
        w.AnnullaButton.Click += (_, _) => { tcs.TrySetResult(null); w.Close(); };
        w.Closed += (_, _) => tcs.TrySetResult(null);

        w.ShowDialog(owner);
        return tcs.Task;
    }
}
