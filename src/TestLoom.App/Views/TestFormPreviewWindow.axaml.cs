using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Threading;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Anteprima live del form (feedback utente 2026-08-20, punto 4), condivisa tra TestTemplateEditorWindow
/// (Tipi di test) e CampiStrumentoEditorWindow (Strumenti) — stesso rendering, sorgente diversa. Non modale
/// (Show, non ShowDialog): il Master continua a modificare la finestra principale mentre l'anteprima resta
/// aperta a fianco. Si aggiorna a intervalli regolari invece di agganciare un evento a ogni singolo controllo
/// dell'editor sorgente (decine di TextBox/ComboBox costruiti a mano in code-behind) — 700ms è impercettibile
/// per un'anteprima "mentre scrivo", molto più semplice e robusto di ricablare ogni singolo campo.</summary>
public partial class TestFormPreviewWindow : Window
{
    private DispatcherTimer? _timer;

    public TestFormPreviewWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this, grande: true);
        ChiudiButton.Click += (_, _) => Close();
    }

    public static void Apri(Window owner, Func<List<PreviewSezione>> ottieniSezioni)
    {
        var w = new TestFormPreviewWindow();
        void Aggiorna() => w.Host.Content = TestFormPreviewRenderer.Renderizza(ottieniSezioni());
        Aggiorna();

        w._timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        w._timer.Tick += (_, _) => Aggiorna();
        w._timer.Start();
        w.Closed += (_, _) => w._timer?.Stop();

        w.Show(owner);
    }
}
