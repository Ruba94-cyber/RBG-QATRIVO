using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Controllo dei riferimenti a una Change Request prima dell'eliminazione definitiva: il percorso veloce (candidati
/// dall'indice locale, confermati sul JSON) deve dare la stessa risposta del percorso completo, e ricadere su di esso
/// quando l'indice non e' allineato ai file o l'Id non e' ricercabile.</summary>
public sealed class ChangeRequestRiferimentiTests : IDisposable
{
    private readonly string _radice = Path.Combine(Path.GetTempPath(), "testloom-cr-riferimenti-" + Guid.NewGuid().ToString("N"));
    private string CartellaCondivisa => Path.Combine(_radice, "condivisa");
    private string CartellaIndice => Path.Combine(_radice, "indice");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_radice, recursive: true); } catch (IOException) { /* residuo di un handle SQLite */ }
    }

    /// <summary>Id CR del dataset: indice 0..n-1. Citano: i%100==0 cita (i+7)%n; archiviate: i%7==3.</summary>
    internal static string IdCr(int indice, int n) => $"CR-2026-{indice % n:D5}";

    /// <summary>Scrive CR come le scrive l'app (JsonIo.Options). Riferimenti incrociati su circa l'1% delle CR, alcune
    /// archiviate. Usato anche dal banco di misura (ChangeRequestRiferimentiBenchmarkTests).</summary>
    internal static void CreaChangeRequest(DataPaths paths, int n)
    {
        Directory.CreateDirectory(paths.ChangeRequestDir);
        for (var i = 0; i < n; i++)
        {
            var archiviata = i % 7 == 3;
            var cr = new ChangeRequest
            {
                Uid = Guid.NewGuid(),
                RevisioneDati = 1,
                Id = IdCr(i, n),
                Titolo = $"Change Request banco {i}",
                Descrizione = $"Richiesta di modifica del banco, sottosistema {i % 37}.",
                Stato = (StatoChangeRequest)(i % 4),
                DataCreazione = new DateTime(2026, 1, 1).AddDays(i % 300),
                UltimaModifica = new DateTime(2026, 6, 1).AddDays(i % 150),
                Archiviata = archiviata,
                DataArchiviazione = archiviata ? new DateTime(2026, 7, 1) : null,
            };
            cr.TestValidazione.Add(new CollegamentoValidazioneCr { TestId = $"TC-{i % 5000:D6}" });
            cr.VeicoliImpattati.Add($"V{i % 400:D3}");
            cr.VeicoliImpattati.Add($"V{(i + 13) % 400:D3}");
            cr.EcuImpattate.Add($"Componente-{i % 900:D4}");
            cr.Changelog.Add(new ChangelogEntry { Data = cr.DataCreazione, Operatore = "banco", Campo = "creazione", ValoreNuovo = "aperta" });
            cr.Changelog.Add(new ChangelogEntry { Data = cr.UltimaModifica, Operatore = "banco", Campo = "stato", ValorePrecedente = "aperta", ValoreNuovo = "in lavorazione" });
            if (i % 100 == 0) cr.CrCorrelate.Add(IdCr(i + 7, n));
            File.WriteAllBytes(paths.ChangeRequestFile(cr.Id), JsonSerializer.SerializeToUtf8Bytes(cr, JsonIo.Options));
        }
    }

    private ChangeRequestRepository ApriRepository() =>
        new(new DataPaths(CartellaCondivisa), null, CartellaIndice);

    [Fact]
    public void ReferenziataDaAltraCr_IndiceAllineato_StessaRispostaDelPercorsoCompleto()
    {
        const int n = 600;
        var paths = new DataPaths(CartellaCondivisa);
        CreaChangeRequest(paths, n);
        var repo = ApriRepository();
        repo.RefreshIndex();

        Assert.True(repo.IndiceAllineatoAiFile());
        // 7: citata da 0 (attiva). 507: citata da 500, archiviata. 1, 50, 0: non citate.
        var attese = new (int Indice, bool Esito)[] { (7, true), (107, true), (507, true), (1, false), (50, false), (0, false) };
        foreach (var (indice, esito) in attese)
        {
            var id = IdCr(indice, n);
            Assert.True(repo.TentaReferenziataDaIndice(id, out var veloce), $"percorso veloce non applicabile per {id}");
            Assert.Equal(esito, veloce);
            Assert.Equal(esito, repo.ReferenziataDaAltraCrPercorsoCompleto(id));
            Assert.Equal(esito, repo.ReferenziataDaAltraCr(id));
        }
    }

    [Fact]
    public void ReferenziataDaAltraCr_IndiceNonAllineato_RicadeSulPercorsoCompleto()
    {
        const int n = 300;
        var paths = new DataPaths(CartellaCondivisa);
        CreaChangeRequest(paths, n);
        var repo = ApriRepository();
        repo.RefreshIndex();

        // Un altro PC scrive una CR che cita IdCr(9) senza che l'indice locale sia stato aggiornato.
        var nuova = new ChangeRequest { Uid = Guid.NewGuid(), RevisioneDati = 1, Id = "CR-2026-NUOVA01", Titolo = "nuova" };
        nuova.CrCorrelate.Add(IdCr(9, n));
        File.WriteAllBytes(paths.ChangeRequestFile(nuova.Id), JsonSerializer.SerializeToUtf8Bytes(nuova, JsonIo.Options));

        Assert.False(repo.IndiceAllineatoAiFile());
        Assert.False(repo.TentaReferenziataDaIndice(IdCr(9, n), out _));
        // Non applicabile: la risposta e' quella del percorso completo (sull'indice ancora non aggiornato).
        Assert.Equal(repo.ReferenziataDaAltraCrPercorsoCompleto(IdCr(9, n)), repo.ReferenziataDaAltraCr(IdCr(9, n)));

        // Dopo la riconciliazione l'indice torna allineato e il percorso veloce vede il riferimento.
        repo.RefreshIndex();
        Assert.True(repo.IndiceAllineatoAiFile());
        Assert.True(repo.ReferenziataDaAltraCr(IdCr(9, n)));
        Assert.True(repo.ReferenziataDaAltraCrPercorsoCompleto(IdCr(9, n)));
    }

    [Fact]
    public void ReferenziataDaAltraCr_IndiceMancanteOMaiRiconciliato_RicadeSulPercorsoCompleto()
    {
        const int n = 300;
        CreaChangeRequest(new DataPaths(CartellaCondivisa), n);
        var repo = ApriRepository();   // nessun RefreshIndex: la sessione non ha mai riconciliato l'indice

        Assert.False(repo.IndiceAllineatoAiFile());
        Assert.False(repo.TentaReferenziataDaIndice(IdCr(7, n), out _));
        Assert.Equal(repo.ReferenziataDaAltraCrPercorsoCompleto(IdCr(7, n)), repo.ReferenziataDaAltraCr(IdCr(7, n)));
    }

    [Fact]
    public void ReferenziataDaAltraCr_CopiaDiConflittoEsclusaDallIndice_IndiceRestaAllineato()
    {
        const int n = 300;
        var paths = new DataPaths(CartellaCondivisa);
        CreaChangeRequest(paths, n);
        // Copia di conflitto OneDrive della CR 1: stesso contenuto, nome non canonico. L'indice la esclude per costruzione.
        var canonico = paths.ChangeRequestFile(IdCr(1, n));
        File.Copy(canonico, Path.Combine(paths.ChangeRequestDir, Path.GetFileNameWithoutExtension(canonico) + " (copia).json"));
        var repo = ApriRepository();
        repo.RefreshIndex();

        Assert.NotEmpty(repo.CopieDiConflitto);
        Assert.True(repo.IndiceAllineatoAiFile());
        Assert.True(repo.TentaReferenziataDaIndice(IdCr(7, n), out var veloce));
        Assert.True(veloce);
        Assert.Equal(repo.ReferenziataDaAltraCrPercorsoCompleto(IdCr(7, n)), veloce);
    }

    [Theory]
    [InlineData("CR-2026-00007", true)]
    [InlineData("CR-Sigla-1", false)]          // contiene 'i' e 'S': ricerca non affidabile sull'indice
    [InlineData("CR 2026", false)]             // spazio: non ricercabile
    [InlineData("CR-2026-ü", false)]           // non ASCII
    [InlineData("", false)]
    public void IdRicercabileNellIndice_AccettaSoloIdAsciiSenzaIESenzaSpazi(string id, bool atteso) =>
        Assert.Equal(atteso, ChangeRequestRepository.IdRicercabileNellIndice(id));
}
