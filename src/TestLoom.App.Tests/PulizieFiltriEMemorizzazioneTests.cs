using System.Collections.Specialized;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Pulizie 6.6.1 a basso rischio: FiltroMultiSelect.Popola notifica una sola volta (Reset) e sostituisce
/// le voci senza tenere i gestori di quelle vecchie; GammeDisponibili e TestVincoloDisponibili nell'editor Test sono
/// memorizzate e si ricalcolano solo quando cambiano i loro input (template del Test, catalogo template, catalogo
/// veicoli, uid ammessi).</summary>
public sealed class PulizieFiltriEMemorizzazioneTests
{
    [Fact]
    public void FiltroMultiSelect_Popola_NotificaUnSoloReset_ESostituisceLeVociSenzaGestoriVecchi()
    {
        var filtro = new FiltroMultiSelect();
        var notifiche = new List<NotifyCollectionChangedAction>();
        filtro.Opzioni.CollectionChanged += (_, e) => notifiche.Add(e.Action);
        var cambiati = 0;
        filtro.Cambiato += () => cambiati++;

        filtro.Popola(Enumerable.Range(0, 1000).Select(i => ($"V{i:D4}", $"Voce {i}")).ToList());
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, notifiche);
        Assert.Equal(1000, filtro.Opzioni.Count);

        var vecchia = filtro.Opzioni[0];
        filtro.Popola(new[] { ("A", "Alfa"), ("B", "Beta") });
        Assert.Equal(2, filtro.Opzioni.Count);

        // La voce della generazione precedente non e' piu' nella lista: modificarla non deve notificare il filtro.
        cambiati = 0;
        vecchia.Selezionato = true;
        Assert.Equal(0, cambiati);

        // Le voci correnti notificano una volta per cambio e la selezione resta leggibile.
        filtro.Opzioni[1].Selezionato = true;
        Assert.Equal(1, cambiati);
        Assert.Equal(new[] { "B" }, filtro.Selezionati);
    }

    [Fact]
    public void FiltroMultiSelect_Ripopola_ConStessoContenuto_NonRifaLeVoci()
    {
        var filtro = new FiltroMultiSelect();
        filtro.Popola(new[] { ("A", "Alfa"), ("B", "Beta") });
        var prima = filtro.Opzioni[0];
        var notifiche = 0;
        filtro.Opzioni.CollectionChanged += (_, _) => notifiche++;

        filtro.Ripopola(new[] { ("A", "Alfa"), ("B", "Beta") });

        Assert.Equal(0, notifiche);
        Assert.Same(prima, filtro.Opzioni[0]);
    }

    private static AppState CreaStatoConVeicoliETemplate(string dir, out Guid templateConVincolo, out Guid templateAmmesso)
    {
        Directory.CreateDirectory(dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(dir);
        templateAmmesso = Guid.Parse("44444444-4444-4444-8444-444444444444");
        templateConVincolo = Guid.Parse("55555555-5555-4555-8555-555555555555");
        state.Master.TestTemplate.Save(new List<TestTemplate>
        {
            new() { Uid = templateAmmesso, Nome = "Modello ammesso" },
            new() { Uid = templateConVincolo, Nome = "Modello con vincolo", VincoloCollegamento = new VincoloCollegamentoTemplate { TemplateAmmessiUid = new List<Guid> { templateAmmesso } } },
        });
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = "VEI-1", GammaNome = "Gamma A" },
            new() { Nome = "VEI-2", GammaNome = "Gamma B" },
        });
        for (var i = 0; i < 20; i++)
            state.TestCases.Save(new TestCase { Id = $"TC-{i:D4}", TestTemplateUid = i % 2 == 0 ? templateConVincolo : templateAmmesso }, isNew: true);
        state.RicaricaTutto();
        return state;
    }

    [Fact]
    public void GammeDisponibili_RestanoMemorizzateFinoAlRicaricamentoDelCatalogo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_PulizieGamme_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var state = CreaStatoConVeicoliETemplate(dir, out _, out _);
            var editor = new TestCaseEditorViewModel(state, "TC-0000", () => { }, _ => { });

            var prima = editor.GammeDisponibili;
            Assert.Same(prima, editor.GammeDisponibili);
            Assert.Equal(new[] { "Gamma A", "Gamma B" }, prima);

            // Un nuovo veicolo con una gamma nuova compare solo dopo il ricaricamento del catalogo.
            // Si riparte dai veicoli gia' salvati (stessi Uid): ri-salvare VEI-1 con un Uid nuovo sarebbe una collisione di chiave.
            var veicoli = state.Master.Veicoli.Load().ToList();
            veicoli.Add(new Veicolo { Nome = "VEI-3", GammaNome = "Gamma C" });
            state.Master.Veicoli.Save(veicoli);
            state.RicaricaTutto();

            var dopo = editor.GammeDisponibili;
            Assert.NotSame(prima, dopo);
            Assert.Equal(new[] { "Gamma A", "Gamma B", "Gamma C" }, dopo);
        }
        finally
        {
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    [Fact]
    public void TestVincoloDisponibili_FiltraPerTemplateAmmessiEResta_MemorizzataFinoAiLoInput()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_PulizieVincolo_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var state = CreaStatoConVeicoliETemplate(dir, out _, out var templateAmmesso);
            var editor = new TestCaseEditorViewModel(state, "TC-0000", () => { }, _ => { });

            var prima = editor.TestVincoloDisponibili;
            Assert.Same(prima, editor.TestVincoloDisponibili);
            // Solo i Test collegabili con il template ammesso (TC-0001, TC-0003, ...): nessun altro.
            var attesi = editor.TestCorrelatiDisponibili.Where(t => t.TestTemplateUid == templateAmmesso).Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(attesi, prima.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());
            Assert.NotEmpty(attesi);

            // Dopo il ricaricamento dei template (nuova istantanea del catalogo) il risultato si ricalcola.
            state.RicaricaTutto();
            var dopoRicaricamento = editor.TestVincoloDisponibili;
            Assert.NotSame(prima, dopoRicaricamento);
            Assert.Equal(attesi, dopoRicaricamento.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());
        }
        finally
        {
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
