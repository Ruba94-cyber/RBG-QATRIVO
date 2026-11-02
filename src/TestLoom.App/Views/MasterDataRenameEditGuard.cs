using Avalonia.Controls;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Impedisce di entrare in modifica diretta della chiave di una voce Master già salvata. La rinomina
/// deve passare dal pannello Unisci/Rinomina/Dividi che propaga il nuovo nome ai riferimenti.</summary>
internal static class MasterDataRenameEditGuard
{
    public static void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (sender is not DataGrid grid || grid.DataContext is not IEditorConModifiche editor) return;
        e.Cancel = editor.BloccaRinominaDiretta(e.Row.DataContext, e.Column.SortMemberPath);
    }
}
