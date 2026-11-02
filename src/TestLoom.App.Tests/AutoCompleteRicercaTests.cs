using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>AutoCompleteRicerca (ReportView: componente e veicoli con FilterMode=None). Equivalenza con il predicato del
/// filtro interno su dati ASCII, Invio prima del debounce, scarto dei risultati superati, dispose. Il comportamento
/// visivo del menu di Avalonia non e' coperto qui (nessuna piattaforma headless in questo progetto di test).</summary>
public sealed class AutoCompleteRicercaTests
{
    private static readonly string[] Query =
    {
        "", "e", "Componente-004", "centralina", "GAMMA 1", "xyz", "0012", "sensore temperatura gamma 1",
    };

    private static List<string> Dataset() =>
        Enumerable.Range(0, 400)
            .Select(i => $"Componente-{i:D4} {(i % 7 == 0 ? "centralina motore" : "sensore temperatura")} gamma {i % 13}")
            .ToList();

    private static List<string> Atteso(IEnumerable<string> voci, string query) =>
        voci.Where(v => AutoCompleteFilters.Stringa(query, v)).ToList();

    [Fact]
    public async Task Cerca_SuDatiAscii_RestituisceGliStessiRisultatiDelFiltroInterno()
    {
        var voci = Dataset();
        var applicati = new List<IReadOnlyList<string>>();
        using var ricerca = new AutoCompleteRicerca<string>(voci, v => v, applicati.Add, postaSulThreadUi: a => a());

        foreach (var query in Query)
        {
            // Percorso sincrono (Invio prima del debounce) e percorso asincrono (digitazione) devono coincidere.
            ricerca.Allinea(query);
            Assert.Equal(Atteso(voci, query), applicati[^1]);

            ricerca.Digita(query);
            await ricerca.UltimaRicerca;
            Assert.Equal(Atteso(voci, query), applicati[^1]);
        }
    }

    [Fact]
    public void Allinea_PrimaDelDebounce_AppliaSubitoIlRisultatoDelTestoCorrenteEAnnullaLaRicercaPendente()
    {
        var voci = Dataset();
        var applicati = new List<IReadOnlyList<string>>();
        // Il debounce non termina mai da solo: solo l'annullamento lo chiude.
        using var ricerca = new AutoCompleteRicerca<string>(voci, v => v, applicati.Add,
            debounceMs: 200, attesa: (_, ct) => Task.Delay(Timeout.Infinite, ct), postaSulThreadUi: a => a());

        ricerca.Digita("componente-004");
        Assert.Empty(applicati);

        ricerca.Allinea("componente-004");
        Assert.Single(applicati);
        Assert.Equal(Atteso(voci, "componente-004"), applicati[0]);
    }

    [Fact]
    public async Task Digita_RisultatoSuperato_NonVieneApplicato()
    {
        var voci = Dataset();
        var applicati = new List<IReadOnlyList<string>>();
        var attese = new List<TaskCompletionSource>();
        Task Attesa(int _, CancellationToken ct)
        {
            var attesa = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => attesa.TrySetCanceled(ct));
            attese.Add(attesa);
            return attesa.Task;
        }
        using var ricerca = new AutoCompleteRicerca<string>(voci, v => v, applicati.Add,
            debounceMs: 50, attesa: Attesa, postaSulThreadUi: a => a());

        ricerca.Digita("centralina");   // attesa[0], poi superata da una digitazione successiva
        ricerca.Digita("componente-004");      // annulla attesa[0] e attende attesa[1]
        attese[1].SetResult();
        await ricerca.UltimaRicerca;

        Assert.Single(applicati);
        Assert.Equal(Atteso(voci, "componente-004"), applicati[0]);
    }

    [Fact]
    public async Task Dispose_NonApplicaRisultatiPendenti()
    {
        var applicati = new List<IReadOnlyList<string>>();
        var ricerca = new AutoCompleteRicerca<string>(Dataset(), v => v, applicati.Add,
            debounceMs: 200, attesa: (_, ct) => Task.Delay(Timeout.Infinite, ct), postaSulThreadUi: a => a());

        ricerca.Digita("centralina");
        ricerca.Dispose();
        await ricerca.UltimaRicerca;

        Assert.Empty(applicati);
    }

    [Fact]
    public void Cerca_IgnoraAccentiECasoComeIlMotoreDeiPicker()
    {
        // Regola unica dei selettori (PickerTesto): diversa dal filtro interno di prima, che confrontava gli accenti.
        // Il cambio e' voluto e documentato nel report: "perche" trova "perché", il filtro precedente no.
        var voci = new List<string> { "perché", "motore" };
        var applicati = new List<IReadOnlyList<string>>();
        using var ricerca = new AutoCompleteRicerca<string>(voci, v => v, applicati.Add, postaSulThreadUi: a => a());

        ricerca.Allinea("PERCHE");

        Assert.Equal(new[] { "perché" }, applicati[^1]);
        Assert.False(AutoCompleteFilters.Stringa("perche", "perché"));
    }
}
