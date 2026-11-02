using System;
using System.Collections.Generic;
using Avalonia.Controls;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Popup dedicato ai campi obbligatori mancanti al salvataggio (feedback utente 2026-08-20, punto 15:
/// "dovrebbe portarmi direttamente su quelli", non lasciare solo un banner di testo generico da interpretare).
/// Ogni riga è cliccabile: chiama vaiAlCampo(chiave), che il chiamante (TestCaseEditorView) usa per portare in
/// vista la sezione contenente il campo ed evidenziarlo (Spotlight), poi chiude il popup.</summary>
public partial class CampiMancantiDialog : Window
{
    public CampiMancantiDialog()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
    }

    public static void Mostra(Window owner, IReadOnlyList<CampoMancante> campiMancanti, Action<string> vaiAlCampo)
    {
        var w = new CampiMancantiDialog { Title = TestLoom.App.Services.LocalizationService.Instance.Traduci("CampiMancantiTitolo") };
        w.ChiudiButton.Click += (_, _) => w.Close();

        foreach (var campo in campiMancanti)
        {
            var riga = campo;
            var btn = new Button { Classes = { "Secondary" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left, Content = riga.Messaggio };
            btn.Click += (_, _) => { vaiAlCampo(riga.Chiave); w.Close(); };
            w.ElencoPanel.Children.Add(btn);
        }

        w.Show(owner);
    }
}
