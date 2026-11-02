using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Coppie sospette delle viste master (6.6.1) in headless: la lista e' virtualizzata (si creano solo le righe visibili),
/// contiene tutte le coppie e mantiene l'AutomationId. Una prova per vista: Veicoli, Gamma, Strumenti, Operatori.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutCoppieMasterHeadlessTests
{
    /// <summary>Mille coppie di doppioni: ogni voce ha una gemella che differisce solo per maiuscole.</summary>
    private static List<T> Doppioni<T>(Func<string, T> crea) =>
        Enumerable.Range(0, 1_000).SelectMany(i => new[] { crea($"VOCE-{i:D4}"), crea($"voce-{i:D4}") }).ToList();

    [Fact]
    public void CoppieVeicoli_ListaVirtualizzata() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var stato = MasterHeadless.StatoConCartella(out var dir);
            try
            {
                var vm = new VeicoliViewModel(stato);
                VerificaCoppie(stato, vm, new VeicoliView { DataContext = vm }, "Veicoli_CoppieSospette_Lista",
                    Doppioni(n => new Veicolo { Nome = n }));
            }
            finally
            {
                MasterHeadless.Pulisci(dir);
            }
        });

    [Fact]
    public void CoppieGamma_ListaVirtualizzata() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var stato = MasterHeadless.StatoConCartella(out var dir);
            try
            {
                var vm = new GammaViewModel(stato);
                VerificaCoppie(stato, vm, new GammaView { DataContext = vm }, "Gamma_CoppieSospette_Lista",
                    Doppioni(n => new Gamma { Nome = n }));
            }
            finally
            {
                MasterHeadless.Pulisci(dir);
            }
        });

    [Fact]
    public void CoppieStrumenti_ListaVirtualizzata() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var stato = MasterHeadless.StatoConCartella(out var dir);
            try
            {
                var vm = new StrumentiViewModel(stato);
                VerificaCoppie(stato, vm, new StrumentiView { DataContext = vm }, "Strumenti_CoppieSospette_Lista",
                    Doppioni(n => new StrumentoDiagnostico { Nome = n }));
            }
            finally
            {
                MasterHeadless.Pulisci(dir);
            }
        });

    [Fact]
    public void CoppieOperatori_ListaVirtualizzata() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var stato = MasterHeadless.StatoConCartella(out var dir);
            try
            {
                var vm = new OperatoriViewModel(stato);
                VerificaCoppie(stato, vm, new OperatoriView { DataContext = vm }, "Operatori_CoppieSospette_Lista",
                    Doppioni(n => new Operatore { Nome = n }));
            }
            finally
            {
                MasterHeadless.Pulisci(dir);
            }
        });

    private static void VerificaCoppie<TVm, T>(AppState stato, TVm vm, UserControl vista, string idLista, List<T> voci)
        where TVm : MasterDataEditorViewModelBase<T>
        where T : MasterEntity, new()
    {
        vm.Unione.RicalcolaCoppieSospette(voci);
        var attese = vm.Unione.CoppieSospette.Count;
        Assert.Equal(1_000, attese);

        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var lista = vista.GetVisualDescendants().OfType<ListBox>()
                .Single(l => AutomationProperties.GetAutomationId(l) == idLista);
            Assert.Equal(attese, lista.ItemCount);
            var creati = lista.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Assert.True(creati > 0 && creati < 100, $"{idLista}: {creati} righe create per {attese} coppie");
        }
        finally
        {
            finestra.Hide();
        }
    }
}
