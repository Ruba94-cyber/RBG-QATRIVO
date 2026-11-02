using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public sealed record MasterArchiveSlice<T>(IReadOnlyList<T> Items, long Total) where T : MasterEntity;

public abstract partial class MasterDataEditorViewModelBase<T> where T : MasterEntity, new()
{
    /// <summary>
    /// Conteggio Archivio senza materializzare il catalogo completo quando il tipo dispone del read-model
    /// paginato. Il read-model viene prima riallineato alla fonte autorevole; in caso di errore si torna al
    /// repository. Il valore e' a 64 bit: le soglie usate nei benchmark non sono limiti di cardinalita.
    /// refresh:false salta la riallineazione (vedi ReadArchivedPage) e, sul fallback non paginato, riusa gli
    /// Items gia' in memoria invece di un altro giro completo di Load() da disco — usato dall'Archivio
    /// unificato, che ha appena costruito questo stesso editor un istante prima nello stesso Ricarica().
    /// </summary>
    public long ContaElementiArchivio(bool refresh = true)
    {
        if (_pagedRead != null)
        {
            try { return _pagedRead.ReadArchivedPage(0, 1, refresh).Total; }
            catch (Exception ex)
            {
                CrashLogService.Registra($"Archivio master {typeof(T).Name}: fallback conteggio autorevole", ex);
            }
        }
        return (refresh ? _repo.Load().AsEnumerable() : Items.AsEnumerable()).LongCount(EArchiviata);
    }

    /// <summary>
    /// Restituisce soltanto la finestra richiesta dall'Archivio. I cataloghi dotati di read-model usano SQLite
    /// archived-only; i cataloghi legacy mantengono il fallback storico fino alla migrazione Phase D.
    /// </summary>
    public MasterArchiveSlice<T> CaricaPaginaElementiArchivio(long offset, int limit, bool refresh = true)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));

        if (_pagedRead != null)
        {
            try
            {
                var page = _pagedRead.ReadArchivedPage(offset, limit, refresh);
                return new MasterArchiveSlice<T>(page.Items, page.Total);
            }
            catch (Exception ex)
            {
                // Degrado prestazionale soltanto: se l'indice locale è mancante/corrotto, la fonte condivisa
                // verificata resta sempre disponibile e nessuna riga viene nascosta per comodità.
                CrashLogService.Registra($"Archivio master {typeof(T).Name}: fallback pagina autorevole", ex);
            }
        }

        var archived = (refresh ? _repo.Load().AsEnumerable() : Items.AsEnumerable()).Where(EArchiviata);
        var pageItems = SkipLong(archived, offset).Take(limit).ToList();
        return new MasterArchiveSlice<T>(pageItems, archived.LongCount());
    }

    private static IEnumerable<T> SkipLong(IEnumerable<T> source, long count)
    {
        using var e = source.GetEnumerator();
        while (count > 0 && e.MoveNext()) count--;
        while (e.MoveNext()) yield return e.Current;
    }
}
