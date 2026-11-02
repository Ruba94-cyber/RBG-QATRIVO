using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Concorrenza multi-PC sugli allegati: usa SOLO le API pubbliche reali di <see cref="AttachmentService"/>
/// (Salva/Elimina/PercorsoCompleto/CalcolaSha256 — vedi <see cref="AttachmentServiceTests"/> per il perimetro).
/// AttachmentService.Salva NON sovrascrive mai un file con lo stesso nome (crea con FileMode.CreateNew
/// esclusivo e rinomina in caso di collisione, vedi commento su SalvaCoreAsync): una "sostituzione" realistica
/// da una seconda postazione è quindi sempre Elimina(vecchio) + Salva(nuovo, stesso NomeFile) — lo stesso
/// percorso che farebbe l'utente nell'editor. Questi test verificano che un lettore concorrente (postazione A)
/// non riceva mai contenuto troncato o ibrido, che nessuna IOException sfugga come crash non gestito, e che
/// l'hash calcolato sia sempre coerente con la versione effettivamente letta.
/// </summary>
public sealed class AttachmentConcurrentReplaceReadTests : IDisposable
{
    private readonly string _dir;
    private readonly DataPaths _paths;
    private readonly TestCase _tc;

    // Due PNG 1x1 validi ma con contenuto diverso (secondo pixel/canale), cosi' un hash sbagliato o un
    // contenuto ibrido sono distinguibili in modo inequivocabile da "vecchio" e "nuovo".
    private static readonly byte[] PngVecchio = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] PngNuovo = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYPjPAAABBgABMYXlpAAAAABJRU5ErkJggg==");

    public AttachmentConcurrentReplaceReadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AttachmentConcurrentReplace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new DataPaths(_dir);
        _paths.EnsureStructure();
        _tc = new TestCase { Id = "Componente-TEST-BCM-20260101-OPA-01", Componente = "Componente-TEST-BCM" };
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string ScriviSorgente(string nomeFile, byte[] contenuto)
    {
        var percorso = Path.Combine(_dir, nomeFile);
        File.WriteAllBytes(percorso, contenuto);
        return percorso;
    }

    private static string HashDi(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Postazione B sostituisce l'allegato (Elimina + Salva con lo stesso nome) mentre la postazione A
    /// tiene APERTO un handle di lettura sul contenuto vecchio (scenario "l'operatore ha il file aperto in un
    /// viewer esterno mentre arriva l'aggiornamento"). EliminaRecoverable cattura internamente l'IOException di
    /// File.Delete quando il file e' ancora in uso (CrashLogService.Registra, "delete rinviata") — quindi la
    /// cancellazione fisica e' differita e il lettore continua a vedere il contenuto integro e coerente col
    /// proprio hash calcolato ad apertura, non un file a meta' sovrascritto.</summary>
    [Fact]
    public void SecondaPostazioneSostituisceAllegato_MentreALoHaApertoInLettura_ALeggeSempreContenutoIntegroCoerenteConIlSuoHash()
    {
        var sorgenteVecchia = ScriviSorgente("originale.png", PngVecchio);
        var vecchio = AttachmentService.Salva(_paths, _tc, sorgenteVecchia, "Operatore A");
        var percorsoVecchio = AttachmentService.PercorsoCompleto(_paths, _tc, vecchio);

        using var handleLetturaA = new FileStream(percorsoVecchio, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[PngVecchio.Length];
        var lettiParziali = handleLetturaA.Read(buffer, 0, buffer.Length / 2); // A ha letto solo META' del file finora.
        Assert.True(lettiParziali > 0);

        // Postazione B tenta la sostituzione (Elimina + Salva con lo stesso NomeFile) mentre A ha ancora
        // l'handle aperto: la delete fisica sara' rinviata (EliminaRecoverable cattura l'IOException), quindi
        // Salva non trova il vecchio nome libero e assegna un nome nuovo (mai una scrittura in-place sul file
        // che A sta ancora leggendo).
        Allegato? nuovoDiB = null;
        var ex = Record.Exception(() =>
        {
            AttachmentService.Elimina(_paths, _tc, vecchio);
            var sorgenteNuova = ScriviSorgente("nuovo_sorgente.png", PngNuovo);
            nuovoDiB = AttachmentService.Salva(_paths, _tc, sorgenteNuova, "Operatore B");
        });
        Assert.Null(ex); // Elimina/Salva non devono mai propagare un'eccezione non gestita al chiamante.
        Assert.NotNull(nuovoDiB);
        Assert.NotEqual(vecchio.NomeFile, nuovoDiB!.NomeFile); // rinominato, mai sovrascritto in-place.

        // A completa la lettura del proprio handle, aperto PRIMA della sostituzione: deve vedere esattamente e
        // per intero il contenuto vecchio, mai un mix vecchio/nuovo o un troncamento.
        var restoLetto = handleLetturaA.Read(buffer, lettiParziali, buffer.Length - lettiParziali);
        Assert.Equal(buffer.Length, lettiParziali + restoLetto);
        Assert.Equal(PngVecchio, buffer);
        Assert.Equal(HashDi(PngVecchio), HashDi(buffer));
    }

    /// <summary>Nessun handle aperto in comune: la postazione A esegue letture ripetute (CalcolaSha256) mentre
    /// la B esegue Elimina+Salva con lo stesso NomeFile, senza alcuna sincronizzazione. Polling con timeout
    /// (non sleep arbitrari): ogni singola lettura riuscita deve produrre un hash APPARTENENTE all'insieme dei
    /// contenuti realmente scritti su disco (vecchio o nuovo) — mai un terzo valore che tradirebbe un
    /// contenuto ibrido — e ogni eccezione osservata deve essere un IOException/FileNotFoundException
    /// transitorio (finestra fisica di assenza del file fra la delete e la successiva create), non un crash che
    /// si propaga fuori dal test.</summary>
    [Fact]
    public async Task LetturaRipetutaSenzaLock_DuranteSostituzioneConcorrente_OgniHashOsservatoEUnoDeiDueContenutiRealiMaiUnIbrido()
    {
        var sorgenteVecchia = ScriviSorgente("condiviso.png", PngVecchio);
        var vecchio = AttachmentService.Salva(_paths, _tc, sorgenteVecchia, "Operatore A");
        var percorso = AttachmentService.PercorsoCompleto(_paths, _tc, vecchio);
        var hashVecchio = HashDi(PngVecchio);
        var hashNuovo = HashDi(PngNuovo);

        var hashOsservati = new System.Collections.Concurrent.ConcurrentBag<string>();
        var eccezioniInattese = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var stop = new CancellationTokenSource();

        var lettore = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    hashOsservati.Add(AttachmentService.CalcolaSha256(percorso));
                }
                catch (IOException) { /* transitorio: file momentaneamente assente/bloccato fra delete e create. */ }
                catch (UnauthorizedAccessException) { /* transitorio equivalente su Windows durante la finestra di rename/delete-recreate. */ }
                catch (Exception ex) { eccezioniInattese.Add(ex); }
            }
        });

        var scrittore = Task.Run(() =>
        {
            AttachmentService.Elimina(_paths, _tc, vecchio);
            var sorgenteNuova = ScriviSorgente("nuovo.png", PngNuovo);
            var nuovoAllegato = AttachmentService.Salva(_paths, _tc, sorgenteNuova, "Operatore B");
            return nuovoAllegato;
        });

        var nuovo = await scrittore;

        // Almeno una lettura successiva alla scrittura, per verificare anche lo stato finale nel campione —
        // nessun sleep arbitrario: si attende solo il completamento del prossimo giro del lettore.
        while (hashOsservati.Count < 2 && !lettore.IsCompleted) { }
        stop.Cancel();
        Assert.True(await Task.WhenAny(lettore, Task.Delay(TimeSpan.FromSeconds(5))) == lettore,
            "Il thread lettore non è terminato entro il timeout.");

        Assert.Empty(eccezioniInattese);
        Assert.All(hashOsservati, h => Assert.True(h == hashVecchio || h == hashNuovo,
            $"Hash osservato '{h}' non corrisponde a nessuno dei due contenuti reali: possibile lettura ibrida/troncata."));

        // Lo stato finale su disco deve essere esattamente il nuovo contenuto, con hash coerente coi metadati
        // dell'Allegato restituito da Salva.
        Assert.Equal(nuovo.Sha256, HashDi(PngNuovo));
        var percorsoFinale = AttachmentService.PercorsoCompleto(_paths, _tc, nuovo);
        Assert.Equal(hashNuovo, AttachmentService.CalcolaSha256(percorsoFinale));
    }

    /// <summary>Se, per la finestra di delete rinviata (handle ancora aperto altrove), Salva non riesce a
    /// riottenere lo STESSO NomeFile, il comportamento documentato di SalvaCoreAsync è rinominare
    /// (mai sovrascrivere): la nuova voce ha un NomeFile diverso e il vecchio file resta leggibile col proprio
    /// hash intatto finché non viene effettivamente rimosso. Nessun dato perso in nessuno dei due casi.</summary>
    [Fact]
    public void SostituzioneConNomeFileOccupatoDaHandleAncoraAperto_CreaNuovoNomeInveceDiCorrompereIlVecchio()
    {
        var sorgenteVecchia = ScriviSorgente("occupato.png", PngVecchio);
        var vecchio = AttachmentService.Salva(_paths, _tc, sorgenteVecchia, "Operatore A");
        var percorsoVecchio = AttachmentService.PercorsoCompleto(_paths, _tc, vecchio);

        using var handleAperto = new FileStream(percorsoVecchio, FileMode.Open, FileAccess.Read, FileShare.Read);

        AttachmentService.Elimina(_paths, _tc, vecchio); // delete fisica rinviata: handleAperto la impedisce.
        Assert.True(File.Exists(percorsoVecchio)); // ancora presente e leggibile: nessuna evidenza persa.

        var sorgenteNuova = ScriviSorgente("occupato_nuova_sorgente.png", PngNuovo);
        // Stesso NomeFile del vecchio richiesto esplicitamente tramite il nome del file sorgente.
        File.Move(sorgenteNuova, Path.Combine(Path.GetDirectoryName(sorgenteNuova)!, vecchio.NomeFile), overwrite: true);
        var percorsoSorgenteConflittuale = Path.Combine(Path.GetDirectoryName(sorgenteNuova)!, vecchio.NomeFile);

        var nuovo = AttachmentService.Salva(_paths, _tc, percorsoSorgenteConflittuale, "Operatore B");

        Assert.NotEqual(vecchio.NomeFile, nuovo.NomeFile); // rinominato, non sovrascritto.
        Assert.Equal(PngVecchio, File.ReadAllBytes(percorsoVecchio)); // il vecchio e' ancora integro.
        Assert.Equal(PngNuovo, File.ReadAllBytes(AttachmentService.PercorsoCompleto(_paths, _tc, nuovo)));
    }
}
