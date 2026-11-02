using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Parita' fra la ricerca indicizzata (LocalMasterIndexStore.Query, FTS5) e il filtro in memoria dei selettori
/// (PickerMotore: sottostringa normalizzata). Documenta perche' i selettori NON usano l'indice come sorgente della lista:
/// FTS5 (tokenize unicode61 remove_diacritics 2) confronta prefissi di parola, quindi su una query interna alla parola
/// ("tore" in "motore") non trova una corrispondenza che il selettore deve mostrare. Su query di prefisso di parola i due
/// risultati coincidono. Il test fallisce se cambia il tokenizer dell'indice: allora va rivalutato l'uso di FTS5 nei selettori.</summary>
public sealed class PickerIndiceFtsParitaTests : IDisposable
{
    private static readonly string[] Nomi = { "Centralina motore", "Attuatore freno", "Perché sensore", "Città test" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PickerIndiceFts", Guid.NewGuid().ToString("N"));
    private readonly LocalMasterIndexStore _store;

    public PickerIndiceFtsParitaTests()
    {
        var shared = Path.Combine(_root, "shared");
        Directory.CreateDirectory(shared);
        _store = new LocalMasterIndexStore(shared, Path.Combine(_root, "local"));
        var items = Nomi.Select(n => new Componente { Nome = n, ModificatoDa = "Seed" }).ToList();
        _store.ReconcileCatalog("componente", items, e => e.Nome, e => e.Archiviata, e => e.Nome, sourceFingerprint: "v1");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* connessioni SQLite ancora aperte dal sistema: la cartella temporanea resta */ }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("motore")]
    [InlineData("perche")]
    [InlineData("citta")]
    [InlineData("freno")]
    public void QueryDiPrefissoDiParola_CoincideConIlFiltroInMemoria(string query)
    {
        Assert.True(_store.FtsEnabled, "il test richiede FTS5 attivo nel build SQLite");

        Assert.Equal(FiltroInMemoria(query), NomiIndicizzati(query));
    }

    [Theory]
    [InlineData("tore")]
    [InlineData("reno")]
    public void QueryDentroLaParola_FtsPerdeCorrispondenzeCheIlSelettoreMostra(string query)
    {
        Assert.True(_store.FtsEnabled, "il test richiede FTS5 attivo nel build SQLite");

        var inMemoria = FiltroInMemoria(query);
        var indicizzati = NomiIndicizzati(query);

        Assert.NotEmpty(inMemoria);
        Assert.True(indicizzati.Count < inMemoria.Count, "FTS5 deve trovare meno corrispondenze interne alla parola");
        Assert.All(indicizzati, nome => Assert.Contains(nome, inMemoria));
    }

    private List<string> NomiIndicizzati(string query) =>
        _store.Query<Componente>("componente", query, false, 0, 100)
            .Select(hit => hit.Item.Nome)
            .OrderBy(nome => nome, StringComparer.Ordinal)
            .ToList();

    private static List<string> FiltroInMemoria(string query)
    {
        var motore = new PickerMotore<Componente>(Nomi.Select(n => new Componente { Nome = n }), e => e.Nome);
        return motore.Cerca(query).Voci
            .Select(e => e.Nome)
            .OrderBy(nome => nome, StringComparer.Ordinal)
            .ToList();
    }
}
