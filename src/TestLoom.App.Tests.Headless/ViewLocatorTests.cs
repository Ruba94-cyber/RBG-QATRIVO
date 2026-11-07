using Avalonia.Controls;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Copertura del riuso delle View introdotto in ViewLocator (correzione prestazioni 2026-09-25):
/// una rivisita di pagina con lo stesso ViewModel non deve ricostruire la View da zero (Activator.CreateInstance
/// era la causa misurata del rallentamento su MainViewModel.OttieniPagina), ma solo se la View cache non e'
/// gia' agganciata altrove (guardia di sicurezza per il caso, oggi non presente in TestLoom ma da non rompere in
/// futuro, in cui lo stesso ViewModel finisse mostrato in due punti contemporaneamente).
/// Gira nel processo headless: da 6.7.2 NonConformitaView contiene un GridSplitter, la cui inizializzazione statica
/// richiede la piattaforma Avalonia, che il processo TestLoom.App.Tests non inizializza.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class ViewLocatorTests
{
    [Fact]
    public void Build_StessaIstanzaViewModel_RiusaLaStessaIstanzaView() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var locator = new ViewLocator();
            var state = TestStateFactory.Create();
            var vm = new NonConformitaViewModel(state, _ => { });

            var prima = locator.Build(vm);
            var seconda = locator.Build(vm);

            Assert.IsType<NonConformitaView>(prima);
            Assert.Same(prima, seconda);
        });

    [Fact]
    public void Build_ViewModelDiverso_RestituisceIstanzeDiverse() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var locator = new ViewLocator();
            var state = TestStateFactory.Create();
            var vm1 = new NonConformitaViewModel(state, _ => { });
            var vm2 = new NonConformitaViewModel(state, _ => { });

            var view1 = locator.Build(vm1);
            var view2 = locator.Build(vm2);

            Assert.NotSame(view1, view2);
        });

    [Fact]
    public void Build_ViewCacheGiaAgganciataAltrove_NeCreaUnaNuova() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            // Simula il caso di sicurezza: lo stesso ViewModel viene richiesto una seconda volta mentre la View
            // costruita in precedenza e' ancora agganciata (genitore logico/visuale non nullo) da qualche altra
            // parte dell'albero — riusarla la strapperebbe da li'.
            var locator = new ViewLocator();
            var state = TestStateFactory.Create();
            var vm = new NonConformitaViewModel(state, _ => { });

            var prima = locator.Build(vm)!;
            var host = new Decorator { Child = prima };

            var seconda = locator.Build(vm);

            Assert.NotSame(prima, seconda);
            Assert.Same(prima, host.Child); // la View originale resta al suo posto, non viene strappata via.
        });

    [Fact]
    public void PreparaInCacheSeAssente_ViewGiaMontata_NonLaSostituisce()
        => LayoutHeadlessSessione.Esegui(() =>
        {
            var locator = new ViewLocator();
            var state = TestStateFactory.Create();
            var vm = new NonConformitaViewModel(state, _ => { });
            var prima = locator.Build(vm)!;
            var host = new Decorator { Child = prima };

            // Il precache arriva tardi, dopo la navigazione e il mount.
            locator.PreparaInCacheSeAssente(vm);

            Assert.Same(prima, host.Child);
            host.Child = null;
            Assert.Same(prima, locator.Build(vm));
        });

    [Fact]
    public void PreparaInCacheSeAssente_PrimaNavigazione_RiusaLaViewPreparata()
        => LayoutHeadlessSessione.Esegui(() =>
        {
            var locator = new ViewLocator();
            var state = TestStateFactory.Create();
            var vm = new NonConformitaViewModel(state, _ => { });

            locator.PreparaInCacheSeAssente(vm);
            var vista = locator.Build(vm);
            Assert.NotNull(vista);
            Assert.Same(vista, locator.Build(vm));
        });

    [Fact]
    public void Build_ParametroNullo_RestituisceNullo() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var locator = new ViewLocator();
            Assert.Null(locator.Build(null));
        });
}
