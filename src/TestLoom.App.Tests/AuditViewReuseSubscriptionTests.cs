using System.Reflection;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Copertura del FAIL ALTO della review 2026-09-25 sul ramo perf/6.4.0-ui-riuso-view: AuditView
/// agganciava vm.MatriceAggiornata solo su DataContextChanged e lo staccava su DetachedFromVisualTree. Con il
/// ViewLocator che ora RIUSA la stessa istanza di View per la stessa istanza di ViewModel di pagina (Audit e'
/// una pagina cache-viva in MainViewModel._pagineCache), un ciclo Detach/Attach senza cambio di DataContext
/// (uscita e rientro nella pagina Audit) staccava la sottoscrizione e non la riagganciava mai piu' — la
/// griglia smetteva di aggiornarsi silenziosamente dopo la prima uscita. La correzione aggancia/sgancia anche
/// su AttachedToVisualTree/DetachedFromVisualTree. Questo test invoca direttamente i due handler privati (le
/// transizioni di visual tree non si generano in un test headless senza una finestra reale) e verifica che la
/// sottoscrizione a vm.MatriceAggiornata sopravviva a piu' cicli Detach/Attach senza sparire ne' duplicarsi.</summary>
[Collection("Localization")]
public sealed class AuditViewReuseSubscriptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AuditReuse_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private static int NumeroSottoscrittoriMatriceAggiornata(AuditViewModel vm)
    {
        var campo = typeof(AuditViewModel).GetField("MatriceAggiornata", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(campo);
        var delegato = campo!.GetValue(vm) as Action;
        return delegato?.GetInvocationList().Length ?? 0;
    }

    private static void InvocaEventoVisualTree(AuditView view, string nomeMetodo)
    {
        // GetMethod(nome) e' ambiguo: Avalonia.Visual dichiara gia' un OnAttachedToVisualTree/
        // OnDetachedFromVisualTree protetto a UN parametro (il metodo virtuale che sovrascrive il ciclo di
        // vita), distinto dal nostro handler PRIVATO a due parametri (object?, VisualTreeAttachmentEventArgs)
        // agganciato come EventHandler. Bisogna specificare la firma per prendere quello giusto.
        var metodo = typeof(AuditView).GetMethod(nomeMetodo, BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(object), typeof(Avalonia.VisualTreeAttachmentEventArgs) }, null);
        Assert.NotNull(metodo);
        metodo!.Invoke(view, new object?[] { view, null });
    }

    [Fact]
    public void RivisitaLaPagina_DopoDetachEAttach_ResubscriveSenzaDuplicare()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var vm = new AuditViewModel(state);
        var view = new AuditView { DataContext = vm };

        // DataContextChanged (evento reale della proprieta') ha gia' agganciato la prima volta.
        Assert.Equal(1, NumeroSottoscrittoriMatriceAggiornata(vm));

        // Simula l'uscita dalla pagina Audit (DetachedFromVisualTree) seguita dalla rivisita (AttachedToVisualTree)
        // SENZA che il DataContext cambi — esattamente il caso della View riusata dal ViewLocator.
        try
        {
            for (var i = 0; i < 3; i++)
            {
                InvocaEventoVisualTree(view, "OnDetachedFromVisualTree");
                Assert.Equal(0, NumeroSottoscrittoriMatriceAggiornata(vm));

                InvocaEventoVisualTree(view, "OnAttachedToVisualTree");
                Assert.Equal(1, NumeroSottoscrittoriMatriceAggiornata(vm));
            }
        }
        finally
        {
            // L'ultimo Attach ha agganciato anche LocalizationService.Instance.PropertyChanged (statico): senza
            // questo Detach la View di questo thread resterebbe iscritta e un test successivo che cambia lingua da
            // un altro thread (LowEndPcAndMultiUserReadSafetyTests.LanguageSwitch_ResponsiveAndInstant) fallirebbe
            // con "The calling thread cannot access this object".
            InvocaEventoVisualTree(view, "OnDetachedFromVisualTree");
        }
    }

    [Fact]
    public void RivisitaLaPagina_DopoDetachEAttach_LocalizationServiceNonAccumulaHandler()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var vm = new AuditViewModel(state);
        var view = new AuditView { DataContext = vm };

        var campoLoc = typeof(LocalizationService).GetField(
            "PropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        // Il nome del campo backing di un evento standard INotifyPropertyChanged e' "PropertyChanged".
        Assert.NotNull(campoLoc);

        int ConteggioHandlerLocalization()
        {
            var delegato = campoLoc!.GetValue(LocalizationService.Instance) as MulticastDelegate;
            return delegato?.GetInvocationList().Length ?? 0;
        }

        var base_ = ConteggioHandlerLocalization();
        InvocaEventoVisualTree(view, "OnAttachedToVisualTree");
        Assert.Equal(base_ + 1, ConteggioHandlerLocalization());

        InvocaEventoVisualTree(view, "OnDetachedFromVisualTree");
        Assert.Equal(base_, ConteggioHandlerLocalization());

        // Un secondo ciclo Attach/Detach non deve lasciare handler residui: prima del fix, il costruttore
        // agganciava una volta sola e Detached non veniva mai piu' bilanciato da un nuovo Attach.
        InvocaEventoVisualTree(view, "OnAttachedToVisualTree");
        InvocaEventoVisualTree(view, "OnDetachedFromVisualTree");
        Assert.Equal(base_, ConteggioHandlerLocalization());
    }
}
