using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Proiezione di elenco in RAM delle Change Request (ChangeRequestElenco): equivalenza con il percorso storico
/// (CalcolaPaginaChangeRequest su PeekAllReadOnly) per filtri, ordinamenti, ricerca e pagine; invalidazione (nessun dato stantio)
/// dopo salvataggio, archiviazione, eliminazione, modifiche fuori banda e riconciliazione; annullamento; concorrenza.
/// </summary>
public sealed class ChangeRequestElencoTests : IDisposable
{
    private const int Pagina = 250;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_CrElenco_" + Guid.NewGuid().ToString("N"));
    private readonly string _indici = Path.Combine(Path.GetTempPath(), "TestLoom_CrElencoIdx_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in new[] { _root, _indici }) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
    }

    private DataPaths Percorsi() { var p = new DataPaths(_root); p.EnsureStructure(); return p; }

    private static readonly string[] Titoli = { "Aggiornare la calibrazione", "aggiornare la CALIBRAZIONE", "Correggere guasto", "Nuovo parametro", "Revisione diagnosi", "Zeta finale", "Ñandu", "Éclair", "Titolo_con_underscore", "" };
    private static readonly string[] Veicoli = { "Daily 24", "daily 24", "Alpha 24", "T-Way", "Eurocargo", "X-Way" };
    private static readonly string[] Componente = { "Componente-0", "componente-1", "Componente-2", "TCU", "EDC" };
    private static readonly string[] Funz = { "Frizione", "frizione", "Freno", "Sterzo" };
    private static readonly string[] Persone = { "Mario Rossi", "mario rossi", "Luca Bianchi", "", "Anna Verdi" };

    internal static ChangeRequest Genera(Random r, int n)
    {
        var apertura = new DateTime(2026, 1, 1).AddDays(r.Next(0, 120)).AddMinutes(r.Next(0, 24 * 60));
        var cr = new ChangeRequest
        {
            Uid = Guid.NewGuid(),
            Id = n % 17 == 0 ? $"cr-2026-{n:D4}" : $"CR-2026-{n:D4}",
            Titolo = Titoli[r.Next(Titoli.Length)] + (r.Next(3) == 0 ? $" {r.Next(20)}" : ""),
            DescrizioneBreve = Titoli[r.Next(Titoli.Length)],
            Descrizione = r.Next(5) == 0 ? "" : $"Descrizione {r.Next(40)} per la CR {n} " + (r.Next(10) == 0 ? new string('x', 3000) + " parola-rara " : ""),
            Responsabile = Persone[r.Next(Persone.Length)],
            CreatoDa = Persone[r.Next(Persone.Length)],
            Stato = (StatoChangeRequest)r.Next(6),
            DataCreazione = apertura,
            UltimaModifica = new DateTime(2026, 5, 1).AddMinutes(r.Next(0, 100000)),
            ImplementataIl = r.Next(3) == 0 ? apertura.AddDays(r.Next(1, 20)) : null,
            ChiusaIl = r.Next(3) == 0 ? apertura.AddDays(r.Next(1, 60)).AddHours(r.Next(24)) : null,
            Archiviata = r.Next(12) == 0,
        };
        for (var i = r.Next(0, 3); i > 0; i--) cr.VeicoliImpattati.Add(Veicoli[r.Next(Veicoli.Length)]);
        for (var i = r.Next(0, 3); i > 0; i--) cr.EcuImpattate.Add(Componente[r.Next(Componente.Length)]);
        for (var i = r.Next(0, 3); i > 0; i--) cr.FunzionalitaImpattate.Add(Funz[r.Next(Funz.Length)]);
        for (var i = r.Next(0, 3); i > 0; i--) cr.TicketCollegati.Add(new TicketRif { Riferimento = $"TK-{r.Next(300)}" });
        return cr;
    }

    private ChangeRequestRepository Popola(int quante, int seme = 7)
    {
        var repo = new ChangeRequestRepository(Percorsi(), localBaseDirectory: _indici);
        repo.RefreshIndex(); // come il bootstrap dell'applicazione: la fotografia dell'indice diventa completa
        var r = new Random(seme);
        for (var n = 1; n <= quante; n++) repo.Save(Genera(r, n));
        return repo;
    }

    private static readonly IReadOnlyDictionary<string, string> Gamme = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["Daily 24"] = "MY24", ["Alpha 24"] = "Alpha", ["T-Way"] = "T-Way", ["Eurocargo"] = "MY24" };

    private static NonConformitaViewModel.FiltroChangeRequest F(StatoChangeRequest? stato = null, string? testo = null, string? ticket = null,
        string[]? veicoli = null, string[]? componente = null, string[]? gamma = null, bool nessuna = false, string[]? assegnatari = null,
        string[]? funz = null, DateTime? apDa = null, DateTime? apA = null, DateTime? chDa = null, DateTime? chA = null, long pagina = 1,
        string[]? creatori = null, ChangeRequestOrdinamento? ord = null) =>
        new(stato, testo, ticket, veicoli, componente, gamma, nessuna, assegnatari, funz, apDa, apA, chDa, chA, pagina, creatori, ord);

    private static IEnumerable<NonConformitaViewModel.FiltroChangeRequest> Combinazioni()
    {
        yield return F();
        foreach (var s in Enum.GetValues<StatoChangeRequest>()) yield return F(stato: s);
        yield return F(testo: "calibrazione"); yield return F(testo: "CR-2026-00"); yield return F(testo: "parola-rara"); yield return F(testo: "ñandu");
        yield return F(testo: "descrizione 3"); yield return F(testo: "zzz-nessuno");
        yield return F(ticket: "TK-1"); yield return F(ticket: "tk-29");
        yield return F(veicoli: new[] { "Daily 24" }); yield return F(veicoli: new[] { "alpha 24", "T-Way" });
        yield return F(componente: new[] { "Componente-1" }); yield return F(componente: new[] { "TCU", "edc" });
        yield return F(gamma: new[] { "MY24" }); yield return F(gamma: new[] { "Alpha", "T-Way" });
        yield return F(nessuna: true); yield return F(nessuna: true, testo: "cr");
        yield return F(assegnatari: new[] { "Mario Rossi" }); yield return F(assegnatari: new[] { "" });
        yield return F(creatori: new[] { "Luca Bianchi", "anna verdi" });
        yield return F(funz: new[] { "Frizione" }); yield return F(funz: new[] { "sterzo", "Freno" });
        yield return F(apDa: new DateTime(2026, 2, 1)); yield return F(apA: new DateTime(2026, 2, 1));
        yield return F(apDa: new DateTime(2026, 2, 1), apA: new DateTime(2026, 3, 1));
        yield return F(chDa: new DateTime(2026, 2, 1)); yield return F(chA: new DateTime(2026, 3, 1)); yield return F(chDa: new DateTime(2026, 2, 1), chA: new DateTime(2026, 3, 1));
        yield return F(stato: StatoChangeRequest.Chiusa, veicoli: new[] { "Daily 24" }, componente: new[] { "Componente-0" }, testo: "cr", apDa: new DateTime(2026, 1, 15));
        yield return F(gamma: new[] { "MY24" }, assegnatari: new[] { "Mario Rossi" }, funz: new[] { "Freno" }, chDa: new DateTime(2026, 1, 1));
    }

    private static void ConfrontaConStorico(ChangeRequestRepository repo, string contesto, bool tutteLeChiavi = true)
    {
        var chiavi = tutteLeChiavi ? Enum.GetValues<ChangeRequestOrdinamentoChiave>() : new[] { ChangeRequestOrdinamentoChiave.Apertura };
        var tutte = repo.PeekAllReadOnly();
        foreach (var filtro in Combinazioni())
            foreach (var chiave in chiavi)
                foreach (var decr in new[] { true, false })
                    foreach (var pagina in new long[] { 1, 2, 99 })
                    {
                        var f = filtro with { Ordinamento = new ChangeRequestOrdinamento(chiave, decr), PaginaRichiesta = pagina };
                        var attesa = NonConformitaViewModel.CalcolaPaginaChangeRequest(tutte, f, Gamme);
                        Assert.True(repo.TryQueryElenco(NonConformitaViewModel.ConvertiFiltroCr(f, Gamme), f.Ordinamento!.Value, pagina, Pagina, default, out var trovata),
                            $"{contesto}: proiezione non utilizzabile");
                        var etichetta = $"{contesto} | {filtro} | {chiave} decr={decr} pag={pagina}";
                        Assert.True(attesa.Totale == trovata.Totale, $"Totale {etichetta}: atteso {attesa.Totale}, trovato {trovata.Totale}");
                        Assert.True(attesa.Pagina == trovata.Pagina, $"Pagina {etichetta}");
                        Assert.True(attesa.Righe.Select(c => c.Id).SequenceEqual(trovata.Righe.Select(c => c.Id)), $"Righe {etichetta}");
                        // Le righe sono CR complete e identiche (stessa revisione e stesso stato) a quelle del percorso storico.
                        Assert.True(attesa.Righe.Select(c => (c.Uid, c.RevisioneDati, c.Stato, c.Titolo)).SequenceEqual(trovata.Righe.Select(c => (c.Uid, c.RevisioneDati, c.Stato, c.Titolo))), $"Contenuto {etichetta}");
                    }
    }

    private static void ConfrontaValoriDistinti(ChangeRequestRepository repo)
    {
        var cr = repo.PeekAllReadOnly();
        var attesi = new[]
        {
            cr.SelectMany(c => c.VeicoliImpattati).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cr.SelectMany(c => c.EcuImpattate).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cr.SelectMany(c => c.FunzionalitaImpattate).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cr.Select(c => c.Responsabile).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cr.Select(c => c.CreatoDa).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
        var v = repo.TryValoriElenco();
        Assert.NotNull(v);
        Assert.Equal(attesi[0], v!.Veicoli); Assert.Equal(attesi[1], v.Componente); Assert.Equal(attesi[2], v.Funzionalita);
        Assert.Equal(attesi[3], v.Responsabili); Assert.Equal(attesi[4], v.Creatori);
    }

    [Fact]
    public void Equivalenza_FiltriOrdinamentiRicercaEPagine_UgualiAlPercorsoStorico()
    {
        var repo = Popola(620);
        ConfrontaConStorico(repo, "iniziale");
        ConfrontaValoriDistinti(repo);
        Assert.True(repo.ElencoPronto);
        Assert.Equal(repo.PeekAllReadOnly().Count, repo.ElencoConteggio);
    }

    [Fact]
    public void Invalidazione_SalvataggioArchiviazioneRipristinoEliminazione_NessunDatoStantio()
    {
        var repo = Popola(300);
        ConfrontaConStorico(repo, "base", tutteLeChiavi: false);

        // modifica di campi usati da filtri e da tutti gli ordinamenti
        var cr = repo.GetById("CR-2026-0010")!;
        cr.Titolo = "Zzzz nuovo titolo"; cr.Stato = StatoChangeRequest.Chiusa; cr.ChiusaIl = new DateTime(2026, 3, 3, 10, 0, 0);
        cr.VeicoliImpattati = new List<string> { "T-Way" }; cr.Responsabile = "Anna Verdi"; cr.UltimaModifica = DateTime.Now;
        repo.Save(cr, cr.RevisioneDati);
        ConfrontaConStorico(repo, "dopo-save");
        ConfrontaValoriDistinti(repo);

        // nuova CR
        var nuova = Genera(new Random(99), 5000); nuova.Archiviata = false; nuova.Titolo = "AAA inserita";
        repo.Save(nuova);
        ConfrontaConStorico(repo, "dopo-nuova");

        // archiviazione: sparisce dall'elenco attivo
        var daArchiviare = repo.GetById("CR-2026-0020")!;
        if (daArchiviare.Archiviata) daArchiviare = repo.GetById("CR-2026-0021")!;
        repo.Archivia(daArchiviare, "Test");
        ConfrontaConStorico(repo, "dopo-archivia", tutteLeChiavi: false);
        Assert.DoesNotContain(NonConformitaViewModel.CalcolaPaginaChangeRequest(repo.PeekAllReadOnly(), F(), null).Righe, c => c.Id == daArchiviare.Id);

        // ripristino: torna nell'elenco
        var archiviata = repo.GetAll(includeArchiviate: true).First(c => c.Id == daArchiviare.Id);
        repo.Ripristina(archiviata, "Test");
        ConfrontaConStorico(repo, "dopo-ripristino", tutteLeChiavi: false);

        // eliminazione definitiva
        var daEliminare = repo.GetById("CR-2026-0030")!;
        if (daEliminare.Archiviata) daEliminare = repo.GetById("CR-2026-0031")!;
        repo.EliminaDefinitivamente(daEliminare, "Test");
        ConfrontaConStorico(repo, "dopo-elimina");
        ConfrontaValoriDistinti(repo);
        Assert.Equal(repo.PeekAllReadOnly().Count, repo.ElencoConteggio);
    }

    [Fact]
    public void Invalidazione_ModificheFuoriBandaERiconciliazione_LaProiezioneSiRiallinea()
    {
        var paths = Percorsi();
        var repo = Popola(200);
        ConfrontaConStorico(repo, "base", tutteLeChiavi: false);

        // un altro PC (sync OneDrive) scrive una CR nuova, ne modifica una esistente e ne elimina una
        var r = new Random(3);
        var esterna = Genera(r, 7000); esterna.Archiviata = false; esterna.Titolo = "Arrivata dalla sync";
        JsonIo.WriteAtomic(paths.ChangeRequestFile(esterna.Id), esterna, overwrite: false);
        var modificata = repo.GetById("CR-2026-0005")!;
        modificata.Stato = StatoChangeRequest.Annullata; modificata.Titolo = "Modificata da un altro PC";
        modificata.RevisioneDati += 1;
        JsonIo.WriteAtomic(paths.ChangeRequestFile(modificata.Id), modificata, overwrite: true);
        File.Delete(paths.ChangeRequestFile("CR-2026-0006"));

        repo.RefreshIndex();
        ConfrontaConStorico(repo, "dopo-refresh");
        ConfrontaValoriDistinti(repo);
        var tutte = repo.PeekAllReadOnly();
        Assert.Contains(tutte, c => c.Titolo == "Arrivata dalla sync");
        Assert.DoesNotContain(tutte, c => c.Id == "CR-2026-0006");
        Assert.Equal(tutte.Count, repo.ElencoConteggio);
    }

    [Fact]
    public void Invalidazione_CopiaDiConflittoDuplicata_NonEntraNellElenco()
    {
        var paths = Percorsi();
        var repo = Popola(60);
        ConfrontaConStorico(repo, "base", tutteLeChiavi: false);
        // copia di conflitto OneDrive della CR-2026-0003: stesso Uid, nome file non canonico
        var originale = repo.GetById("CR-2026-0003")!;
        var copia = Path.Combine(paths.ChangeRequestDir, "cr_CR-2026-0003-DESKTOP-PC1.json");
        File.Copy(paths.ChangeRequestFile(originale.Id), copia);
        repo.RefreshIndex();
        ConfrontaConStorico(repo, "dopo-copia", tutteLeChiavi: false);
        Assert.Equal(repo.PeekAllReadOnly().Count, repo.ElencoConteggio);
    }

    [Fact]
    public void Deriva_IndiceModificatoDaAltraIstanza_FallbackStoricoEPoiRiallineamento()
    {
        var repoA = Popola(80);
        repoA.IntervalloVerificaElencoMs = 0; // verifica di coerenza a ogni interrogazione
        ConfrontaConStorico(repoA, "base", tutteLeChiavi: false);
        // seconda istanza sullo stesso indice locale e sugli stessi file (altro processo): la prima non vede l'hook di Save
        var repoB = new ChangeRequestRepository(Percorsi(), localBaseDirectory: _indici);
        var nuova = Genera(new Random(11), 9000); nuova.Archiviata = false; nuova.Titolo = "Scritta da B";
        repoB.Save(nuova);

        var f = F(testo: "Scritta da B");
        var attesa = NonConformitaViewModel.CalcolaPaginaChangeRequest(repoA.PeekAllReadOnly(), f, null);
        Assert.Single(attesa.Righe);
        // la proiezione di A e' indietro: il conteggio non torna, quindi la query rifiuta (false) invece di rispondere con dati stantii
        var primo = repoA.TryQueryElenco(NonConformitaViewModel.ConvertiFiltroCr(f, null), ChangeRequestOrdinamento.Predefinito, 1, Pagina, default, out var r1);
        Assert.False(primo && r1.Totale != attesa.Totale, "una risposta stantia non deve mai essere restituita");
        // il riallineamento in background porta la proiezione allo stato dell'indice
        var scadenza = DateTime.UtcNow.AddSeconds(20);
        ChangeRequestElencoRisultato? ok = null;
        while (DateTime.UtcNow < scadenza && ok == null)
        {
            if (repoA.TryQueryElenco(NonConformitaViewModel.ConvertiFiltroCr(f, null), ChangeRequestOrdinamento.Predefinito, 1, Pagina, default, out var r)) ok = r;
            else Thread.Sleep(50);
        }
        Assert.NotNull(ok);
        Assert.Equal(attesa.Totale, ok!.Totale);
        Assert.Equal(attesa.Righe.Select(c => c.Id), ok.Righe.Select(c => c.Id));
    }

    [Fact]
    public void Annullamento_TokenGiaAnnullato_LanciaOperationCanceled()
    {
        var repo = Popola(40);
        Assert.True(repo.TryQueryElenco(new ChangeRequestElencoFiltro(), ChangeRequestOrdinamento.Predefinito, 1, Pagina, default, out _));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            repo.TryQueryElenco(new ChangeRequestElencoFiltro(Testo: "cr"), ChangeRequestOrdinamento.Predefinito, 1, Pagina, cts.Token, out _));
    }

    [Fact]
    public void FiltroGammaSenzaLookup_LanciaCome_PercorsoStorico()
    {
        var repo = Popola(10);
        Assert.Throws<InvalidOperationException>(() =>
            repo.TryQueryElenco(new ChangeRequestElencoFiltro(Gamma: new[] { "MY24" }), ChangeRequestOrdinamento.Predefinito, 1, Pagina, default, out _));
        Assert.Throws<InvalidOperationException>(() =>
            NonConformitaViewModel.CalcolaPaginaChangeRequest(repo.PeekAllReadOnly(), F(gamma: new[] { "MY24" }), null));
    }

    [Fact]
    public async Task Precarica_ReportaProgressoEDopoLeQuerySonoPronte()
    {
        var repo = Popola(150);
        var passi = new List<(int, int)>();
        var progresso = new Progress<(int fatti, int totale)>(p => { lock (passi) passi.Add(p); });
        await repo.PrecaricaAsync(progresso, default);
        await Task.Delay(100);
        Assert.True(repo.ElencoPronto);
        lock (passi)
        {
            Assert.NotEmpty(passi);
            Assert.All(passi, p => Assert.True(p.Item1 <= p.Item2));
        }
        Assert.Equal(repo.PeekAllReadOnly().Count, repo.ElencoConteggio);
        // una seconda chiamata non rilegge nulla (stamp invariati)
        var secondi = new List<(int, int)>();
        await repo.PrecaricaAsync(new Progress<(int fatti, int totale)>(p => { lock (secondi) secondi.Add(p); }), default);
        await Task.Delay(100);
        lock (secondi) Assert.All(secondi, p => Assert.Equal(0, p.Item2));
    }

    [Fact]
    public async Task Concorrenza_SalvataggiEInterrogazioniParallele_NessunErroreEStatoFinaleUguale()
    {
        var repo = Popola(120);
        Assert.True(repo.TryQueryElenco(new ChangeRequestElencoFiltro(), ChangeRequestOrdinamento.Predefinito, 1, Pagina, default, out _));
        using var stop = new CancellationTokenSource();
        var errori = new List<Exception>();
        var lettori = Enumerable.Range(0, 3).Select(i => Task.Run(() =>
        {
            var chiavi = Enum.GetValues<ChangeRequestOrdinamentoChiave>();
            var k = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    repo.TryQueryElenco(new ChangeRequestElencoFiltro(Testo: "cr"), new ChangeRequestOrdinamento(chiavi[k++ % chiavi.Length], k % 2 == 0), 1, Pagina, default, out _);
                    repo.TryValoriElenco();
                    Thread.Sleep(3);
                }
                catch (Exception ex) { lock (errori) errori.Add(ex); }
            }
        })).ToArray();
        var r = new Random(5);
        for (var n = 0; n < 30; n++)
        {
            var c = repo.GetById($"CR-2026-{r.Next(1, 100):D4}");
            if (c == null) continue;
            c.Titolo = "Mod " + n; c.Stato = (StatoChangeRequest)r.Next(6);
            repo.Save(c, c.RevisioneDati);
        }
        stop.Cancel();
        await Task.WhenAll(lettori);
        Assert.Empty(errori);
        Assert.True(repo.IndexRecovery.IsUsable, "indice degradato dalla concorrenza: " + repo.IndexRecovery.Salute);
        Assert.Equal(repo.PeekAllReadOnly().Count, repo.ElencoConteggio);
        ConfrontaConStorico(repo, "dopo-concorrenza", tutteLeChiavi: false);
    }

    [Fact]
    public void Stima_MemoriaPerRigaContenuta()
    {
        var repo = Popola(200);
        Assert.True(repo.TryQueryElenco(new ChangeRequestElencoFiltro(), ChangeRequestOrdinamento.Predefinito, 1, Pagina, default, out _));
        var perRiga = repo.ElencoStimaByte / Math.Max(1, repo.ElencoConteggio);
        Assert.InRange(perRiga, 100, 20_000);
    }
}
