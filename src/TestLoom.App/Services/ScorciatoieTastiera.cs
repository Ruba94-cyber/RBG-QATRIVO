using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace TestLoom.App.Services;

/// <summary>Azioni che una pagina (o un editor) espone alle scorciatoie globali gestite da MainWindow
/// (Addendum: audit UX 2026-08-26, punto 6 — prima di questo intervento l'unica scorciatoia dell'intera
/// applicazione era Ctrl+K per la ricerca globale). Ogni metodo torna true se ha davvero gestito il tasto:
/// MainWindow marca l'evento come gestito solo in quel caso, così una pagina che non implementa un'azione
/// non "assorbe" silenziosamente la scorciatoia impedendo il comportamento predefinito del controllo che ha
/// il fuoco. I metodi hanno un'implementazione predefinita perché quasi nessuna pagina le espone tutte.</summary>
public interface IScorciatoiePagina
{
    /// <summary>Ctrl+S. Deve salvare solo se la pagina è davvero in uno stato salvabile.</summary>
    bool SalvaDaTastiera() => false;

    /// <summary>Ctrl+N. Crea un nuovo elemento del tipo gestito dalla pagina.</summary>
    bool NuovoDaTastiera() => false;

    /// <summary>Esc. Annulla la modifica in corso; le implementazioni che possono perdere lavoro devono
    /// passare dalla stessa conferma usata dal pulsante equivalente, non uscire in silenzio.</summary>
    bool AnnullaDaTastiera() => false;
}

/// <summary>Marca il campo di ricerca/filtro principale di una pagina perché Ctrl+F possa portarvi il fuoco
/// senza che MainWindow debba conoscere la struttura di ogni singola view. Attaccata nello XAML della pagina:
/// <c>services:ScorciatoieTastiera.CampoFiltroPrincipale="True"</c>.</summary>
public static class ScorciatoieTastiera
{
    public static readonly AttachedProperty<bool> CampoFiltroPrincipaleProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("CampoFiltroPrincipale");

    public static void SetCampoFiltroPrincipale(Control control, bool value) => control.SetValue(CampoFiltroPrincipaleProperty, value);
    public static bool GetCampoFiltroPrincipale(Control control) => control.GetValue(CampoFiltroPrincipaleProperty);

    /// <summary>Porta il fuoco al campo filtro della porzione di albero visuale passata (tipicamente il
    /// ContentControl che ospita la pagina corrente). Torna false se la pagina non ne dichiara uno: in quel
    /// caso Ctrl+F non viene marcato gestito e resta disponibile al controllo che ha il fuoco.</summary>
    public static bool PortaFuocoAlFiltro(Visual? radice)
    {
        if (radice == null) return false;
        var campo = radice.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => GetCampoFiltroPrincipale(c) && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
        if (campo == null) return false;
        campo.Focus();
        if (campo is TextBox casella) casella.SelectAll();
        return true;
    }
}
