using Avalonia.Controls;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Editor del catalogo campi di sistema aperto come dialogo dal "Gestisci campi" di Tipi di test
/// (Addendum: grill-me 2026-08-18, follow-up — "sposta tutto dentro Tipi di test"): stessa
/// CampoSistemaDefinizioneViewModel/View che prima viveva come sotto-sezione fissa di Impostazioni, ora
/// raggiungibile solo da qui, così tutta la gestione dei campi (rinomina, tipo, nuovi campi) vive in un unico
/// posto invece che sparsa tra Impostazioni e Tipi di test.</summary>
public partial class CampoSistemaCatalogoWindow : Window
{
    public CampoSistemaCatalogoWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this, grande: true);
    }

    public static System.Threading.Tasks.Task Apri(Window owner, AppState state, GruppoCampoSistema gruppo)
    {
        var w = new CampoSistemaCatalogoWindow();
        var vm = new CampoSistemaDefinizioneViewModel(state, gruppo);
        w.CampoView.DataContext = vm;
        w.Title = $"{LocalizationService.Instance.Traduci("GestioneCampiDiSistemaTitolo")} — {vm.Titolo}";
        return w.ShowDialog(owner);
    }
}
