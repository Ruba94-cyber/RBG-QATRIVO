using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Lettura di sola lettura delle Change Request (ciclo prestazioni 6.6.1, wt-p661-opt-cr): PeekAllReadOnly evita la copia di
/// GetAll per filtri, paginazione, ricerca e riferimenti. Il contratto da proteggere: stessi record e stesso ordine di GetAll,
/// nessuna fotografia di sicurezza creata dalla lettura (un salvataggio resta bloccato finche' la CR non e' letta con GetById),
/// mutazioni che non raggiungono il repository, refresh che vede le modifiche di un'altra postazione, concorrenza senza eccezioni.
/// </summary>
public sealed class ChangeRequestPeekReadOnlyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_CrPeek_" + Guid.NewGuid().ToString("N"));
    private readonly string _indiciA = Path.Combine(Path.GetTempPath(), "TestLoom_CrPeekIdxA_" + Guid.NewGuid().ToString("N"));
    private readonly string _indiciB = Path.Combine(Path.GetTempPath(), "TestLoom_CrPeekIdxB_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _indiciA, _indiciB })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private DataPaths Percorsi()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        return paths;
    }

    private static ChangeRequest NuovaCr(int n, StatoChangeRequest stato, bool archiviata = false) => new()
    {
        Id = $"CR-2026-{n:D4}",
        Titolo = $"Change request {n}",
        Descrizione = $"Descrizione della CR {n}",
        Responsabile = n % 2 == 0 ? "Mario Rossi" : "Luca Bianchi",
        Stato = stato,
        Archiviata = archiviata,
        DataCreazione = new DateTime(2026, 1, 1).AddDays(n % 5),
        UltimaModifica = new DateTime(2026, 2, 1).AddHours(n),
        VeicoliImpattati = new List<string> { n % 3 == 0 ? "Daily 24" : "Alpha 24" },
        EcuImpattate = new List<string> { $"Componente-{n % 4}" },
        TicketCollegati = n % 4 == 0 ? new List<TicketRif> { new() { Riferimento = $"TK-{n}" } } : new(),
    };

    private static IEnumerable<(string Id, string Titolo, long Revisione, StatoChangeRequest Stato)> Proiezione(IEnumerable<ChangeRequest> crs) =>
        crs.Select(c => (c.Id, c.Titolo, c.RevisioneDati, c.Stato));

    [Fact]
    public void PeekAllReadOnly_MostraGliStessiRecordDiGetAllNelloStessoOrdine()
    {
        var paths = Percorsi();
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _indiciA);
        for (var n = 1; n <= 30; n++)
            repo.Save(NuovaCr(n, n % 3 == 0 ? StatoChangeRequest.Validata : StatoChangeRequest.DaValidare, archiviata: n % 7 == 0));

        Assert.Equal(Proiezione(repo.GetAll()), Proiezione(repo.PeekAllReadOnly()));
        Assert.Equal(Proiezione(repo.GetAll(includeArchiviate: true)), Proiezione(repo.PeekAllReadOnly(includeArchiviate: true)));
        Assert.True(repo.PeekAllReadOnly(includeArchiviate: true).Count > repo.PeekAllReadOnly().Count); // le archiviate ci sono solo se richieste
    }

    [Fact]
    public void PeekAllReadOnly_MutareIRisultatiNonAlteraIlRepository()
    {
        var paths = Percorsi();
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _indiciA);
        repo.Save(NuovaCr(1, StatoChangeRequest.DaValidare));
        repo.Save(NuovaCr(2, StatoChangeRequest.DaValidare));

        // Mutazione per errore di un oggetto letto con PeekAllReadOnly: il repository non la vede.
        var letti = repo.PeekAllReadOnly();
        letti.Single(c => c.Id == "CR-2026-0001").Titolo = "MUTATO DA PEEK";
        Assert.DoesNotContain(repo.GetAll(), c => c.Titolo == "MUTATO DA PEEK");
        Assert.DoesNotContain(repo.PeekAllReadOnly(), c => c.Titolo == "MUTATO DA PEEK");

        // Mutazione di un oggetto ottenuto con GetAll (copia): la lettura successiva resta quella salvata.
        var copia = repo.GetAll().Single(c => c.Id == "CR-2026-0002");
        copia.Titolo = "MUTATO DA GETALL";
        copia.Changelog.Add(new ChangelogEntry { Campo = "test" });
        Assert.DoesNotContain(repo.PeekAllReadOnly(), c => c.Titolo == "MUTATO DA GETALL");
        Assert.Equal("Change request 2", repo.GetById("CR-2026-0002")!.Titolo);
        Assert.Empty(repo.GetById("CR-2026-0002")!.Changelog);
    }

    [Fact]
    public void PeekAllReadOnly_NonRegistraLaFotografiaDiSicurezza_IlSalvataggioNeRichiedeUnaLetturaConGetById()
    {
        var paths = Percorsi();
        // Un'altra postazione scrive la CR sul disco condiviso; questa postazione la vede solo dopo il refresh.
        JsonIo.WriteAtomic(paths.ChangeRequestFile("CR-2026-0009"), NuovaCr(9, StatoChangeRequest.DaValidare));
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _indiciA);
        repo.RefreshIndex();

        var letta = repo.PeekAllReadOnly().Single();
        letta.Titolo = "salvataggio senza fotografia";
        var eccezione = Assert.Throws<IOException>(() => repo.Save(letta, letta.RevisioneDati));
        Assert.Contains("Fotografia di sicurezza", eccezione.Message);

        // Dopo una lettura con GetById la fotografia c'e' e il salvataggio con la stessa revisione riesce.
        var daModificare = repo.GetById("CR-2026-0009")!;
        daModificare.Titolo = "salvataggio dopo GetById";
        repo.Save(daModificare, daModificare.RevisioneDati);
        Assert.Equal("salvataggio dopo GetById", repo.GetById("CR-2026-0009")!.Titolo);
    }

    [Fact]
    public void CrSalvataDaUnaltraPostazione_CompareNelloSnapshotDopoIlRefresh()
    {
        var paths = Percorsi();
        var postazioneA = new ChangeRequestRepository(paths, localBaseDirectory: _indiciA);
        var postazioneB = new ChangeRequestRepository(paths, localBaseDirectory: _indiciB);

        postazioneA.Save(NuovaCr(1, StatoChangeRequest.DaValidare));
        postazioneB.RefreshIndex();
        Assert.Contains(postazioneB.PeekAllReadOnly(), c => c.Id == "CR-2026-0001" && c.Titolo == "Change request 1");

        // A modifica la CR (nuova revisione, titolo piu' lungo): B la vede solo dopo il refresh della propria cartella.
        var daModificare = postazioneA.GetById("CR-2026-0001")!;
        daModificare.Titolo = "Change request 1 modificata sull'altra postazione";
        postazioneA.Save(daModificare, daModificare.RevisioneDati);
        Assert.Contains(postazioneB.PeekAllReadOnly(), c => c.Id == "CR-2026-0001" && c.Titolo == "Change request 1");

        postazioneB.RefreshIndex();
        var vista = Assert.Single(postazioneB.PeekAllReadOnly());
        Assert.Equal("Change request 1 modificata sull'altra postazione", vista.Titolo);
        Assert.Equal(daModificare.RevisioneDati, vista.RevisioneDati);
    }

    [Fact]
    public void FiltriEPagine_UgualiAlPercorsoConGetAll()
    {
        var paths = Percorsi();
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _indiciA);
        for (var n = 1; n <= 150; n++)
            repo.Save(NuovaCr(n, n % 4 == 0 ? StatoChangeRequest.Validata : n % 5 == 0 ? StatoChangeRequest.Chiusa : StatoChangeRequest.DaValidare,
                archiviata: n % 11 == 0));

        var gamme = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Daily 24"] = "MY24", ["Alpha 24"] = "Alpha" };
        var filtri = new[]
        {
            new NonConformitaViewModel.FiltroChangeRequest(null, null, null, null, null, null, false, null, null, null, null, null, null, 1),
            new NonConformitaViewModel.FiltroChangeRequest(StatoChangeRequest.DaValidare, null, null, null, null, null, false, null, null, null, null, null, null, 1),
            new NonConformitaViewModel.FiltroChangeRequest(null, "change request 1", null, null, null, null, false, null, null, null, null, null, null, 1),
            new NonConformitaViewModel.FiltroChangeRequest(null, null, null, new[] { "Daily 24" }, null, null, false, null, null, null, null, null, null, 2),
            new NonConformitaViewModel.FiltroChangeRequest(null, null, null, null, null, new[] { "MY24" }, false, null, null, null, null, null, null, 1),
            new NonConformitaViewModel.FiltroChangeRequest(null, null, null, null, new[] { "Componente-1" }, null, false, new[] { "Mario Rossi" }, null, new DateTime(2026, 1, 2), new DateTime(2026, 1, 4), null, null, 1),
            new NonConformitaViewModel.FiltroChangeRequest(null, null, null, null, null, null, false, null, null, null, null, new DateTime(2026, 1, 1), null, 3),
            new NonConformitaViewModel.FiltroChangeRequest(null, null, "TK-8", null, null, null, false, null, null, null, null, null, null, 1),
        };
        foreach (var filtro in filtri)
        {
            var attesa = NonConformitaViewModel.CalcolaPaginaChangeRequest(repo.GetAll(), filtro, gamme);
            var trovata = NonConformitaViewModel.CalcolaPaginaChangeRequest(repo.PeekAllReadOnly(), filtro, gamme);
            Assert.Equal(attesa.Totale, trovata.Totale);
            Assert.Equal(attesa.Pagina, trovata.Pagina);
            Assert.Equal(attesa.Righe.Select(c => c.Id), trovata.Righe.Select(c => c.Id));
        }
    }

    [Fact]
    public void FiltriCr_SupportanoVeicoloEcuGammaResponsabileCreatoreFunzionalitaEDate()
    {
        var giusta = new ChangeRequest
        {
            Id = "CR-2026-9201", Titolo = "CR per Daily", Descrizione = "Diagnosi",
            Responsabile = "Operatore Rete", CreatoDa = "Operatore Rete",
            DataCreazione = new DateTime(2026, 1, 4), ChiusaIl = new DateTime(2026, 1, 8),
            VeicoliImpattati = new() { "Daily 24" }, EcuImpattate = new() { "BCM" },
            FunzionalitaImpattate = new() { "Diagnosi accensione" },
            TicketCollegati = new() { new TicketRif { Riferimento = "TK-9201" } }
        };
        var diversa = new ChangeRequest
        {
            Id = "CR-2026-9202", Titolo = "CR per Alpha", Descrizione = "Diagnosi",
            Responsabile = "Altro", CreatoDa = "Altro",
            DataCreazione = new DateTime(2026, 1, 4), ChiusaIl = new DateTime(2026, 1, 8),
            VeicoliImpattati = new() { "Alpha 24" }, EcuImpattate = new() { "ECM" },
            FunzionalitaImpattate = new() { "Diagnosi accensione" },
            TicketCollegati = new() { new TicketRif { Riferimento = "TK-9202" } }
        };
        var filtro = new NonConformitaViewModel.FiltroChangeRequest(
            Stato: null, Testo: null, Ticket: "TK-9201",
            Veicoli: new[] { "daily 24" }, Componente: new[] { "bcm" }, Gamma: new[] { "my26" },
            NessunaCr: false, Assegnatari: new[] { "operatore rete" },
            Funzionalita: new[] { "diagnosi accensione" },
            AperturaDa: new DateTime(2026, 1, 4), AperturaA: new DateTime(2026, 1, 4),
            ChiusuraDa: new DateTime(2026, 1, 8), ChiusuraA: new DateTime(2026, 1, 8),
            PaginaRichiesta: 1, Creatori: new[] { "OPERATORE RETE" });
        var gamma = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Daily 24"] = "MY26", ["Alpha 24"] = "MY25"
        };

        var pagina = NonConformitaViewModel.CalcolaPaginaChangeRequest(new[] { giusta, diversa }, filtro, gamma);

        Assert.Equal(1, pagina.Totale);
        Assert.Equal("CR-2026-9201", Assert.Single(pagina.Righe).Id);
    }

    [Fact]
    public async Task LetturaESalvataggioConcorrenti_NessunaEccezioneENessunDatoIncoerente()
    {
        var paths = Percorsi();
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _indiciA);
        for (var n = 1; n <= 20; n++) repo.Save(NuovaCr(n, StatoChangeRequest.DaValidare));
        // Come all'avvio: la prima riconciliazione completa precede le letture concorrenti (finche' manca, le letture
        // ricadono sui JSON e non sull'indice).
        repo.RefreshIndex();

        var scrittore = Task.Run(() =>
        {
            for (var giro = 1; giro <= 60; giro++)
            {
                var id = $"CR-2026-{(giro - 1) % 20 + 1:D4}";
                var cr = repo.GetById(id)!;
                cr.Titolo = $"aggiornamento {giro:D3}";
                repo.Save(cr, cr.RevisioneDati);
            }
        });
        var lettori = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            for (var giro = 0; giro < 150; giro++)
            {
                var istantanea = repo.PeekAllReadOnly();
                Assert.Equal(20, istantanea.Count);
                Assert.Equal(20, istantanea.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                // Ogni titolo e' quello iniziale o uno di quelli scritti dal giro: mai un valore misto o vuoto.
                Assert.All(istantanea, c => Assert.True(
                    c.Titolo.StartsWith("Change request", StringComparison.Ordinal) || c.Titolo.StartsWith("aggiornamento", StringComparison.Ordinal),
                    $"titolo non coerente: {c.Titolo}"));
            }
        })).ToArray();

        await Task.WhenAll(lettori.Append(scrittore));

        // Dopo i giri ogni CR ha l'ultimo titolo scritto. Ogni CR e' salvata 3 volte (giri k, k+20, k+40) dopo la creazione a revisione 1:
        // revisione finale 4.
        var finale = repo.PeekAllReadOnly().ToDictionary(c => c.Id);
        for (var giro = 41; giro <= 60; giro++)
            Assert.StartsWith("aggiornamento", finale[$"CR-2026-{(giro - 1) % 20 + 1:D4}"].Titolo, StringComparison.Ordinal);
        Assert.Equal(4, finale["CR-2026-0001"].RevisioneDati);
    }
}
