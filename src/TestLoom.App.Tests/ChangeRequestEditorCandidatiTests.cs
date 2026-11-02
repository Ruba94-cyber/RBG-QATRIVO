using System.Threading;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Editor di Change Request (ciclo prestazioni 6.6.1): l'elenco dei Test candidati viene calcolato fuori dal thread UI e
/// pubblicato quando e' pronto; la sorgente del selettore (PickerMotore) cerca come prima. L'editor non attende il calcolo.
/// </summary>
public sealed class ChangeRequestEditorCandidatiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CrCandidati_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState Stato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        return state;
    }

    /// <summary>40 Test con date di creazione a coppie (ordine stabile da verificare), alcuni eliminati.</summary>
    private static void CreaTest(AppState s)
    {
        for (var i = 0; i < 40; i++)
        {
            s.TestCases.Save(new TestCase
            {
                Id = $"TEST-{i:D3}",
                Componente = $"Componente-{i % 4}",
                DescrizioneBreve = $"Descrizione {i}",
                DataCreazione = new DateTime(2026, 1, 1).AddDays(i % 10),
                Eliminato = i % 9 == 0,
            }, isNew: true);
        }
    }

    /// <summary>Riferimento: la regola precedente (non eliminati, dal piu' recente, stesso ordine di GetAll).</summary>
    private static List<string> AttesiTest(AppState s) =>
        s.TestCases.GetAll().Where(t => !t.Eliminato).OrderByDescending(t => t.DataCreazione).Select(t => t.Id).ToList();

    private static string ChiaveRiferimento(TestCase t) => $"{t.Id} {t.DescrizioneBreve} {t.Componente}";

    [Fact]
    public void Editor_ElencoCandidatiUgualeAlRiferimento()
    {
        using var s = Stato();
        CreaTest(s);
        var vm = new ChangeRequestEditorViewModel(s, null, () => { }, _ => { });

        Assert.Equal(AttesiTest(s), vm.TestDisponibili.Select(t => t.Id));
        Assert.NotNull(vm.PickerTestDisponibili);
        Assert.Equal(vm.TestDisponibili.Count, vm.PickerTestDisponibili!.Totale);
    }

    [Fact]
    public void Selettore_RicercaUgualeAlFiltroDiRiferimento()
    {
        using var s = Stato();
        CreaTest(s);
        var vm = new ChangeRequestEditorViewModel(s, null, () => { }, _ => { });
        var candidati = s.TestCases.GetAll().Where(t => !t.Eliminato).OrderByDescending(t => t.DataCreazione).ToList();

        foreach (var query in new[] { "componente-2", "descrizione 1", "TEST-02", "descrizione" })
        {
            var attesi = candidati
                .Where(t => PickerTesto.Corrisponde(PickerTesto.Normalizza(ChiaveRiferimento(t)), PickerTesto.Normalizza(query)))
                .Select(t => t.Id).ToList();
            var trovati = vm.PickerTestDisponibili!.Cerca(query).Voci.Select(t => t.Id).ToList();
            Assert.Equal(attesi, trovati);
        }
    }

    [Fact]
    public async Task Editor_NonAttendeIlCalcoloDeiCandidati()
    {
        using var s = Stato();
        CreaTest(s);
        var attesi = AttesiTest(s);
        using var rilascio = new ManualResetEventSlim(false);

        var vm = new ChangeRequestEditorViewModel(s, null, () => { }, _ => { },
            forzaBackground: true, gateCaricamentoTest: () => rilascio.Wait(TimeSpan.FromSeconds(30)));

        // Il costruttore torna subito: l'elenco non e' ancora pubblicato.
        Assert.Empty(vm.TestDisponibili);
        Assert.Null(vm.PickerTestDisponibili);

        rilascio.Set();
        await vm.AttendiCaricamentoTestDisponibiliAsync();

        Assert.Equal(attesi, vm.TestDisponibili.Select(t => t.Id));
        Assert.NotNull(vm.PickerTestDisponibili);
        Assert.Equal(attesi.Count, vm.PickerTestDisponibili!.Totale);
    }
}
