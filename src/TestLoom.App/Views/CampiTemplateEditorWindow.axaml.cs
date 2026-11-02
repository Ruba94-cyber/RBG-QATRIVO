using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Editor dedicato dei campi di UNA sezione di template (la tabella condivisa Pianificazione/Dettagli
/// che prima viveva incollata dentro TestTemplateEditorWindow): stesso schema di CampiStrumentoEditorWindow,
/// una finestra per sezione invece di una finestra unica con selettore/tab (Addendum: collaudo live
/// 2026-08-17, punto aggiunto al grill-me — "i campi di sistema così come la hanno gli strumenti dovrebbero
/// avere un editor dedicato, uno per Pianificazione e uno per Dettagli"). Riusa TestTemplateEditorWindow.
/// BuildCampiPanel invece di duplicarne la logica: la tabella è identica, solo la finestra che la ospita
/// cambia.</summary>
public partial class CampiTemplateEditorWindow : Window
{
    public CampiTemplateEditorWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this, grande: true);
    }

    public static Task Apri(Window owner, AppState? state, GruppoCampoSistema? gruppo, string titoloSezione, List<CampoTemplate> campi, IReadOnlyList<string> chiaviFisse, System.Func<string, string> T, bool isChangeRequest = false, TestTemplate? templateProprietario = null)
    {
        var w = new CampiTemplateEditorWindow();
        var titolo = string.Format(T("CampiTemplateEditorTitoloFmt"), titoloSezione);
        w.Title = titolo;
        w.TitoloText.Text = titolo;
        w.ChiudiButton.Click += (_, _) => w.Close();

        TestTemplateEditorWindow.BuildCampiPanel(w.RootPanel, w, state, gruppo, campi, chiaviFisse, T, isChangeRequest, templateProprietario);

        return w.ShowDialog(owner);
    }
}
