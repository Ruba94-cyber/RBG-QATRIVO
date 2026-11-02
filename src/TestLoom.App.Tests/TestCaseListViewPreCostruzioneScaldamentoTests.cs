using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Copertura del punto 2 del piano 6.4.0 (scaldamento della View "Test" durante l'avvio, vedi
/// App.axaml.cs.ScaldaViewFuoriSchermoAsync): la View viene costruita dal ViewLocator, agganciata
/// temporaneamente a un host fuori schermo per un vero passaggio di styling/layout, poi staccata SUBITO
/// (Content del contenitore ospite azzerato) prima che l'app mostri la prima pagina "Test" reale. Questo test
/// riproduce lo stesso ciclo con un contenitore di appoggio al posto della Window fuori schermo (una vera
/// finestra Avalonia non e' costruibile in un test headless senza la sua piattaforma): dimostra che (1) la View
/// pre-costruita risulta ancora riusabile da ViewLocator dopo lo stacco, cioe' la prima navigazione reale
/// (secondo Build sulla stessa istanza di ViewModel) restituisce la STESSA istanza di View, e (2) il ciclo
/// aggancio/sgancio del code-behind (CollegaViewModel/ScollegaViewModel su AttachedToVisualTree/
/// DetachedFromVisualTree, vedi TestCaseListView.axaml.cs) non duplica la sottoscrizione a
/// RichiestaAzzeramentoSelezioneGriglia — stesso schema di AuditViewReuseSubscriptionTests.</summary>
[Collection("Localization")]
public sealed class TestCaseListViewPreCostruzioneScaldamentoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TestListPreCostruzione_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private static int NumeroSottoscrittoriAzzeramento(TestCaseListViewModel vm)
    {
        var campo = typeof(TestCaseListViewModel).GetField("RichiestaAzzeramentoSelezioneGriglia", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(campo);
        var delegato = campo!.GetValue(vm) as MulticastDelegate;
        return delegato?.GetInvocationList().Length ?? 0;
    }

    /// <summary>TestCaseListView aggancia AttachedToVisualTree a un metodo privato nominato
    /// (OnAttachedToVisualTree, invocabile per riflessione con la sua firma a due parametri), ma
    /// DetachedFromVisualTree a una lambda anonima nel costruttore ("(_, _) => ScollegaViewModel()") — non
    /// nominabile per riflessione. Per il ramo di stacco invochiamo direttamente ScollegaViewModel(), lo stesso
    /// metodo privato che quella lambda richiama, senza parametri da passare.</summary>
    private static void SimulaAttached(TestCaseListView view)
    {
        var metodo = typeof(TestCaseListView).GetMethod("OnAttachedToVisualTree", BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(object), typeof(Avalonia.VisualTreeAttachmentEventArgs) }, null);
        Assert.NotNull(metodo);
        metodo!.Invoke(view, new object?[] { view, null });
    }

    private static void SimulaDetached(TestCaseListView view)
    {
        var metodo = typeof(TestCaseListView).GetMethod("ScollegaViewModel", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(metodo);
        metodo!.Invoke(view, null);
    }

    [Fact]
    public void ViewPreCostruitaEStaccata_VieneRiusataDalViewLocatorAllaPrimaNavigazioneReale()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var vm = new TestCaseListViewModel(state, _ => { });
        var locator = new ViewLocator();

        // Fase di scaldamento: costruzione + aggancio a un ospite fuori schermo (qui un Decorator al posto
        // della Window headless-incompatibile) + stacco immediato, esattamente come ScaldaViewFuoriSchermoAsync.
        var viewScaldata = locator.Build(vm)!;
        var ospiteScaldamento = new Decorator { Child = viewScaldata };
        Assert.NotNull(viewScaldata.Parent); // agganciata durante il "Measure/Arrange fuori schermo"
        ospiteScaldamento.Child = null; // stacco simmetrico, come host.Content = null prima di host.Close()

        Assert.Null(viewScaldata.Parent);
        Assert.Null(viewScaldata.GetVisualParent());

        // Prima navigazione reale: MainWindow chiede di nuovo la View per LO STESSO ViewModel.
        var viewNavigazioneReale = locator.Build(vm);

        Assert.Same(viewScaldata, viewNavigazioneReale);

        var ospiteReale = new Decorator { Child = viewNavigazioneReale };
        Assert.Same(viewNavigazioneReale, ospiteReale.Child);
    }

    [Fact]
    public void CicloScaldamentoPoiNavigazioneReale_NonDuplicaLaSottoscrizioneRichiestaAzzeramento()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var vm = new TestCaseListViewModel(state, _ => { });
        var view = (TestCaseListView)new ViewLocator().Build(vm)!;
        // ViewLocator.Build costruisce SOLO la View: il DataContext e' assegnato dal ContentControl reale
        // (PaginaCorrenteHost) quando la mostra. Qui lo impostiamo a mano per lo stesso motivo di
        // AuditViewReuseSubscriptionTests: CollegaViewModel si aggancia su DataContextChanged/Attached.
        view.DataContext = vm;

        // Scaldamento: Attached (ospite fuori schermo) poi Detached (stacco a fine scaldamento).
        SimulaAttached(view);
        Assert.Equal(1, NumeroSottoscrittoriAzzeramento(vm));
        SimulaDetached(view);
        Assert.Equal(0, NumeroSottoscrittoriAzzeramento(vm));

        // Prima navigazione reale: Attached di nuovo, SENZA che il DataContext sia mai cambiato (stessa View,
        // stesso ViewModel) — esattamente il caso che il riuso del ViewLocator introduce.
        SimulaAttached(view);
        Assert.Equal(1, NumeroSottoscrittoriAzzeramento(vm));

        // Uscita e rivisita successiva della pagina: ancora nessun accumulo.
        SimulaDetached(view);
        SimulaAttached(view);
        Assert.Equal(1, NumeroSottoscrittoriAzzeramento(vm));
    }
}
