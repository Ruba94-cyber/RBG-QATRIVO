using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Finestra dedicata per editare le opzioni di un campo di tipo Lista (feedback utente 2026-08-20,
/// punto 14): sostituisce la vecchia casella a riga singola con opzioni separate da virgola — non più chiaro
/// su come separare le scelte e scomodo con testi lunghi. Una voce per riga, non ambiguo. Ritorna la lista
/// aggiornata se l'utente conferma, null se annulla/chiude senza salvare — il chiamante decide se applicarla.</summary>
public partial class ListaOpzioniEditorWindow : Window
{
    public ListaOpzioniEditorWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
    }

    public static Task<List<string>?> Apri(Window owner, IReadOnlyList<string> opzioniAttuali)
    {
        var loc = LocalizationService.Instance;
        var w = new ListaOpzioniEditorWindow();
        var titolo = loc.Traduci("ListaOpzioniEditorTitolo");
        w.Title = titolo;
        w.TitoloText.Text = titolo;
        w.TestoBox.Text = string.Join(Environment.NewLine, opzioniAttuali);

        var tcs = new TaskCompletionSource<List<string>?>();

        w.SalvaButton.Click += (_, _) =>
        {
            tcs.TrySetResult(ParseRighe(w.TestoBox.Text));
            w.Close();
        };
        w.AnnullaButton.Click += (_, _) => { tcs.TrySetResult(null); w.Close(); };
        w.Closed += (_, _) => tcs.TrySetResult(null);

        w.ShowDialog(owner);
        return tcs.Task;
    }

    /// <summary>Una voce per riga (non più CSV): righe vuote ignorate, duplicati uniti senza distinguere
    /// maiuscole/minuscole. Estratto a parte per essere testabile senza dover aprire una finestra Avalonia.</summary>
    public static List<string> ParseRighe(string? testo) => (testo ?? "")
        .Split('\n')
        .Select(r => r.Trim().TrimEnd('\r'))
        .Where(r => r.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Quando cambiare il Tipo di un campo deve aprire subito l'editor delle opzioni: passando a Lista un campo che non ne ha
    /// ancora. Usata da tutti gli editor di definizione dei campi (catalogo di sistema, campi strumento, campi liberi del Tipo di test),
    /// perché il comportamento sia lo stesso ovunque. Un tipo precedente sconosciuto non apre nulla (nessun cambio reale).</summary>
    public static bool DeveProporre(TipoCampoStrumento? precedente, TipoCampoStrumento nuovo, int opzioniCorrenti) =>
        precedente is { } prima && prima != TipoCampoStrumento.Lista && nuovo == TipoCampoStrumento.Lista && opzioniCorrenti == 0;
}
