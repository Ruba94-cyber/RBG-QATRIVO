using System.Text.Json;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public class LockServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _target;

    public LockServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomLockTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _target = Path.Combine(_dir, "testcase_TC-1.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private bool HookRiguardaQuestoTarget(string targetPath) =>
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(_target));

    [Fact]
    public void ReleaseIfSession_NonStaccaUnLockCreatoDaUnaSessionePiuRecente()
    {
        // Due istanze (sessioni diverse) dello stesso dispositivo: la seconda subentra come residuo di questa postazione.
        var primoLocks = new LockService("PC-Release");
        var primo = primoLocks.TryAcquire(_target, "Operatore A").DetenutoDa!.SessionId!;
        var secondoLocks = new LockService("PC-Release");
        var secondo = secondoLocks.TryAcquire(_target, "Operatore A");
        Assert.True(secondo.Acquisito);
        var sessioneSecondo = secondo.DetenutoDa!.SessionId!;
        Assert.NotEqual(primo, sessioneSecondo);

        // Il rilascio della sessione precedente (acquisizione tardiva di un editor chiuso) non deve toccare il lock nuovo.
        primoLocks.ReleaseIfSession(_target, primo);
        Assert.True(File.Exists(_target + ".lock"));
        Assert.Equal(sessioneSecondo, LockService.TryReadLockInfo(_target)!.SessionId);

        secondoLocks.ReleaseIfSession(_target, sessioneSecondo);
        Assert.False(File.Exists(_target + ".lock"));
    }

    [Fact]
    public void TryAcquireNuovaSessione_NonCondivideIlLockGiaPossedutoDallaStessaIstanza()
    {
        var locks = new LockService("PC-Nuova-Sessione");
        var prima = locks.TryAcquire(_target, "Operatore A");
        Assert.True(prima.Acquisito);

        var seconda = locks.TryAcquireNuovaSessione(_target, "Operatore A");

        Assert.False(seconda.Acquisito);
        Assert.True(seconda.SessioneGiaPosseduta);
        Assert.Equal(prima.DetenutoDa!.SessionId, seconda.DetenutoDa!.SessionId);
        Assert.Equal(EsitoIo.Ok, locks.ReleaseIfSessionTracked(_target, prima.DetenutoDa.SessionId!));
    }

    [Fact]
    public async Task LockGate_DopoRilascioEsattoFallito_NuovaAcquisizioneStessoOperatoreERetryVecchioNonRimuovonoIlNuovoLock()
    {
        var locks = new LockService("PC-Release-Retry-StessaPostazione");
        var sessioneVecchia = locks.TryAcquire(_target, "Operatore A").DetenutoDa!.SessionId!;
        var lockPath = _target + ".lock";
        var contenutoOriginale = File.ReadAllText(lockPath);
        var sessioneDaPulire = sessioneVecchia;

        try
        {
            // Forza un errore dopo il detach: il contenuto alterato viene ripristinato, così tutti i retry
            // del coordinatore falliscono deterministicamente senza dipendere dai lock del filesystem host.
            LockService.TestHookPrimaDiVerificaSpostato = (targetPath, detachedPath) =>
            {
                if (!HookRiguardaQuestoTarget(targetPath)) return;
                File.WriteAllText(detachedPath, "{corrotto-dopo-detach");
            };

            var primoRilascio = await LockGate.RilasciaSessioneEsattaConRetryAsync(
                locks, _target, sessioneVecchia, attesaPerTest: static _ => Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(EsitoIo.Errore, primoRilascio);
            Assert.True(File.Exists(lockPath), "Dopo il fallimento dei retry il lease resta sul disco.");

            LockService.TestHookPrimaDiVerificaSpostato = null;
            File.WriteAllText(lockPath, contenutoOriginale);

            var nuovaAcquisizione = await LockGate.EseguiAsync(
                _target, () => Task.Run(() => locks.TryAcquire(_target, "Operatore A")));
            Assert.True(nuovaAcquisizione.Acquisito);
            var sessioneNuova = nuovaAcquisizione.DetenutoDa!.SessionId!;
            sessioneDaPulire = sessioneNuova;
            Assert.NotEqual(sessioneVecchia, sessioneNuova);

            // Il retry lasciato dal vecchio editor deve essere idempotente rispetto al lease nuovo.
            var retryVecchio = await LockGate.RilasciaSessioneEsattaConRetryAsync(
                locks, _target, sessioneVecchia, attesaPerTest: static _ => Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(EsitoIo.Ok, retryVecchio);
            Assert.Equal(sessioneNuova, LockService.TryReadLockInfo(_target)!.SessionId);
        }
        finally
        {
            LockService.TestHookPrimaDiVerificaSpostato = null;
            if (File.Exists(lockPath) && LockService.TryReadLockInfo(_target) == null)
                File.WriteAllText(lockPath, contenutoOriginale);
            locks.ReleaseIfSessionTracked(_target, sessioneDaPulire);
        }
    }

    [Fact]
    public async Task LockGate_RilascioInIOLento_NonLasciaPassareUnaNuovaAcquisizione()
    {
        var locks = new LockService("PC-Release-Lento");
        var sessione = locks.TryAcquire(_target, "Operatore A").DetenutoDa!.SessionId!;
        using var rilascioEntrato = new ManualResetEventSlim();
        using var continuaRilascio = new ManualResetEventSlim();
        LockService.TestHookPrimaDiVerificaSpostato = (targetPath, _) =>
        {
            if (!HookRiguardaQuestoTarget(targetPath)) return;
            rilascioEntrato.Set();
            if (!continuaRilascio.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("Il test non ha sbloccato il rilascio lento.");
        };

        var lockPath = _target + ".lock";
        var rilascio = LockGate.RilasciaSessioneEsattaConRetryAsync(locks, _target, sessione);
        Task<LockResult>? acquisizione = null;
        try
        {
            Assert.True(await Task.Run(() => rilascioEntrato.Wait(TimeSpan.FromSeconds(5))).WaitAsync(TimeSpan.FromSeconds(6)));
            Assert.False(File.Exists(lockPath)); // il file vecchio è staccato, ma l'operazione di rilascio non è terminata

            acquisizione = LockGate.EseguiAsync(_target, () =>
                Task.Run(() => locks.TryAcquire(_target, "Operatore B")));

            // ReleaseIfSession ha un timeout configurato per questo percorso: superarlo intercetta l'I/O lasciato
            // indietro dal timeout, che prima poteva cedere il gate mentre la prima operazione era ancora attiva.
            await Task.Delay(TimeoutCondivisione.Per(_target) + TimeSpan.FromMilliseconds(500));
            Assert.False(acquisizione.IsCompleted, "Una nuova acquisizione ha superato un rilascio ancora dentro l'I/O.");

            continuaRilascio.Set();
            Assert.Equal(EsitoIo.Ok, await rilascio.WaitAsync(TimeSpan.FromSeconds(10)));
            var risultato = await acquisizione.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(risultato.Acquisito);
            Assert.Equal(risultato.DetenutoDa!.SessionId, LockService.TryReadLockInfo(_target)!.SessionId);
            Assert.Equal(EsitoIo.Ok, locks.ReleaseIfSessionTracked(_target, risultato.DetenutoDa.SessionId!));
            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            continuaRilascio.Set();
            LockService.TestHookPrimaDiVerificaSpostato = null;
            try { await rilascio.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* Preserve the original assertion. */ }
            if (acquisizione != null)
            {
                try
                {
                    var risultato = await acquisizione.WaitAsync(TimeSpan.FromSeconds(10));
                    if (risultato.Acquisito && risultato.DetenutoDa?.SessionId is { } sessioneResidua)
                        locks.ReleaseIfSessionTracked(_target, sessioneResidua);
                }
                catch { /* Preserve the original assertion. */ }
            }
        }
    }

    [Fact]
    public void TryAcquire_SuTargetLibero_Riesce()
    {
        var locks = new LockService();
        var risultato = locks.TryAcquire(_target, "Operatore A");
        Assert.True(risultato.Acquisito);
        Assert.True(File.Exists(_target + ".lock"));
    }

    [Fact]
    public void TryAcquire_SecondoOperatore_FallisceEDiceChiLoDetiene()
    {
        var locks = new LockService();
        locks.TryAcquire(_target, "Operatore A");

        var secondo = locks.TryAcquire(_target, "Operatore B");

        Assert.False(secondo.Acquisito);
        Assert.NotNull(secondo.DetenutoDa);
        Assert.Equal("Operatore A", secondo.DetenutoDa!.Operatore);
    }

    [Fact]
    public void TryAcquire_StessoOperatore_RiacquisisceSenzaErrori()
    {
        var locks = new LockService();
        locks.TryAcquire(_target, "Operatore A");

        var riacquisito = locks.TryAcquire(_target, "Operatore A");

        Assert.True(riacquisito.Acquisito);
    }

    [Fact]
    public void Release_LiberaIlLockPerUnAltroOperatore()
    {
        var locks = new LockService();
        locks.TryAcquire(_target, "Operatore A");
        locks.Release(_target);

        var risultato = locks.TryAcquire(_target, "Operatore B");

        Assert.True(risultato.Acquisito);
    }

    [Fact]
    public void GetActiveLock_NullSeNonCEUnLockAttivo()
    {
        var locks = new LockService();
        Assert.Null(locks.GetActiveLock(_target));
    }

    [Fact]
    public void GetActiveLock_RiportaOperatoreEOrarioDiAcquisizione()
    {
        var locks = new LockService();
        locks.TryAcquire(_target, "Operatore A");

        var attivo = locks.GetActiveLock(_target);

        Assert.NotNull(attivo);
        Assert.Equal("Operatore A", attivo!.Operatore);
    }

    [Fact]
    public void ForceUnlock_RimuoveIlLockAncheDiUnAltroOperatore()
    {
        var locks = new LockService();
        locks.TryAcquire(_target, "Operatore A");

        locks.ForceUnlock(_target);

        Assert.Null(locks.GetActiveLock(_target));
    }

    [Fact]
    public void Heartbeat_NonCreaUnLockSeNonNeEsisteGiaUno()
    {
        var locks = new LockService();
        locks.Heartbeat(_target, "Operatore A"); // nessun lock esistente: non deve creare nulla
        Assert.Null(locks.GetActiveLock(_target));
    }

    [Fact]
    public void Heartbeat_DiUnAltroOperatore_NonTocaIlLockAltrui()
    {
        var locks = new LockService();
        locks.TryAcquire(_target, "Operatore A");

        locks.Heartbeat(_target, "Operatore B"); // non è il detentore: non deve avere effetto

        var attivo = locks.GetActiveLock(_target);
        Assert.Equal("Operatore A", attivo!.Operatore);
    }

    [Fact]
    public void GetActiveLock_LockScaduto_EUnaLetturaPura()
    {
        var clock = new TestMonotonicClock();
        var locks = new LockService("PC-Lettore", clock.CreateTracker());
        var lockPath = LockService.LockPathFor(_target);
        var stale = new LockInfo
        {
            Operatore = "Operatore vecchio",
            AcquisitoIl = DateTime.Now - LockService.Timeout - TimeSpan.FromMinutes(1),
            UltimoHeartbeat = DateTime.Now - LockService.Timeout - TimeSpan.FromMinutes(1),
            DeviceId = "old-device",
            SessionId = Guid.NewGuid().ToString("N")
        };
        File.WriteAllText(lockPath, JsonSerializer.Serialize(stale, JsonIo.Options));
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - LockService.Timeout - TimeSpan.FromMinutes(1));

        Assert.NotNull(locks.GetActiveLock(_target)); // un vecchio mtime non basta al primo avvistamento locale
        clock.Advance(LockService.Timeout + TimeSpan.FromSeconds(1));
        Assert.Null(locks.GetActiveLock(_target));
        Assert.NotNull(LockService.TryReadLockInfo(_target)); // un altro lettore inizia il proprio periodo conservativo

        // Le due API si chiamano "Get/Read": non devono più cancellare lo storage condiviso come side effect.
        Assert.True(File.Exists(lockPath));

        // È l'acquisizione, cioè il percorso intenzionalmente mutante, che rimuove il lock scaduto e lo
        // sostituisce in modo esclusivo con la nuova sessione.
        var acquired = locks.TryAcquire(_target, "Operatore nuovo");
        Assert.True(acquired.Acquisito);
        Assert.Equal("Operatore nuovo", locks.GetActiveLock(_target)!.Operatore);
    }

    [Fact]
    public void CleanupStaleLocks_TransactionStraniera_NonCancellaNulla()
    {
        var root = Path.Combine(_dir, "workspace");
        var paths = new DataPaths(root);
        paths.EnsureStructure();
        var target = paths.TestCaseFile("ECM", "TC-STALE");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "{}");
        var lockPath = LockService.LockPathFor(target);
        var stale = new LockInfo
        {
            Operatore = "Operatore vecchio",
            AcquisitoIl = DateTime.Now - LockService.Timeout - TimeSpan.FromMinutes(1),
            UltimoHeartbeat = DateTime.Now - LockService.Timeout - TimeSpan.FromMinutes(1),
            DeviceId = "old-device",
            SessionId = Guid.NewGuid().ToString("N")
        };
        File.WriteAllText(lockPath, JsonSerializer.Serialize(stale, JsonIo.Options));
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - LockService.Timeout - TimeSpan.FromMinutes(1));

        var transactions = Path.Combine(root, "_ditm", "transactions");
        Directory.CreateDirectory(transactions);
        File.WriteAllText(Path.Combine(transactions, Guid.NewGuid().ToString("N") + ".pending.json"), "{broken");

        var result = new LockService().CleanupStaleLocks(root);

        Assert.Equal(0, result.TotaleRimossi);
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public void RimuoviLockAbbandonato_RimuoveSoloIlLockDellaSessioneAbbandonata()
    {
        // 6.7.1 (fix rete): il lock creato da un tentativo scaduto va rimosso solo se porta la SessionId di quel tentativo.
        var lockPath = LockService.LockPathFor(_target);
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new LockInfo
        {
            Operatore = "Operatore A",
            AcquisitoIl = DateTime.Now,
            UltimoHeartbeat = DateTime.Now,
            DeviceId = "device-test",
            SessionId = "sessione-abbandonata"
        }, JsonIo.Options));
        var locks = new LockService();

        locks.RimuoviLockAbbandonato(lockPath, "sessione-altrui");
        Assert.True(File.Exists(lockPath), "Il lock di un'altra sessione non va toccato.");

        locks.RimuoviLockAbbandonato(lockPath, "sessione-abbandonata");
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void RimuoviLockAbbandonato_RitentaSeLaPrimaCancellazioneFallisceAncheSeTransitoria()
    {
        // 6.7.1 (fix rete): un errore transitorio di cancellazione (handle del client di sincronizzazione o SMB) non deve
        // lasciare il lock orfano per 20 minuti. L'handle senza FileShare.Delete fa fallire il primo tentativo; viene rilasciato
        // dopo 50 ms e il retry con backoff deve riuscire.
        var lockPath = LockService.LockPathFor(_target);
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new LockInfo
        {
            Operatore = "Operatore A",
            AcquisitoIl = DateTime.Now,
            UltimoHeartbeat = DateTime.Now,
            DeviceId = "device-test",
            SessionId = "sessione-bloccata"
        }, JsonIo.Options));
        var tenuto = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var rilascio = Task.Run(() =>
        {
            Thread.Sleep(50);
            tenuto.Dispose();
        });

        new LockService().RimuoviLockAbbandonato(lockPath, "sessione-bloccata");
        rilascio.Wait();

        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void RimuoviLockAbbandonato_NonCancellaUnLockSostituitoDaUnaltraSessioneTraITentativi()
    {
        // Review (MEDIUM, TOCTOU): la SessionId va riverificata prima di OGNI cancellazione, non solo al primo tentativo.
        // Il primo tentativo fallisce (handle senza FileShare.Delete). Prima del secondo, un'altra sessione ha riacquisito il
        // path: il lock nuovo non deve essere cancellato dalla pulizia del tentativo abbandonato.
        var lockPath = LockService.LockPathFor(_target);
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new LockInfo
        {
            Operatore = "Operatore A",
            AcquisitoIl = DateTime.Now,
            UltimoHeartbeat = DateTime.Now,
            DeviceId = "device-test",
            SessionId = "sessione-abbandonata"
        }, JsonIo.Options));
        var altraSessione = JsonSerializer.Serialize(new LockInfo
        {
            Operatore = "Operatore B",
            AcquisitoIl = DateTime.Now,
            UltimoHeartbeat = DateTime.Now,
            DeviceId = "device-altro",
            SessionId = "sessione-nuova"
        }, JsonIo.Options);
        FileStream? tenuto = new(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        new LockService().RimuoviLockAbbandonato(lockPath, "sessione-abbandonata", tentativo =>
        {
            if (tentativo != 2) return;
            tenuto?.Dispose();
            tenuto = null;
            File.Delete(lockPath);
            File.WriteAllText(lockPath, altraSessione);
        });
        tenuto?.Dispose();

        Assert.True(File.Exists(lockPath), "Il lock riacquisito da un'altra sessione non va cancellato.");
        Assert.Equal("sessione-nuova", JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(lockPath), JsonIo.Options)!.SessionId);
    }

    // P2.2: un errore di lettura o di rilascio non è mai "libero" né "rilasciato".

    [Fact]
    public void GetForeignActiveLock_FileBloccatoDaUnAltroProcesso_RestituisceSconosciuto()
    {
        Assert.True(new LockService("PC-A").TryAcquire(_target, "Operatore A").Acquisito);
        using var bloccato = new FileStream(_target + ".lock", FileMode.Open, FileAccess.Read, FileShare.None);

        var lettura = new LockService("PC-B").GetForeignActiveLock(_target, "Operatore B");

        Assert.Equal(StatoLockAltrui.Sconosciuto, lettura.Stato);
        Assert.Null(lettura.Info);
    }

    [Fact]
    public void GetForeignActiveLock_ContenutoIllegibileAppenaScritto_RestituisceSconosciuto()
    {
        // Un altro PC sta ancora scrivendo il file: JSON non valido ma recente. Non è un lock libero.
        File.WriteAllText(_target + ".lock", "{\"Operatore\":\"Ma");

        var lettura = new LockService("PC-B").GetForeignActiveLock(_target, "Operatore B");

        Assert.Equal(StatoLockAltrui.Sconosciuto, lettura.Stato);
    }

    [Fact]
    public void GetForeignActiveLock_FileAssente_RestituisceLiberoELockDiAltroPcRestituisceOccupato()
    {
        Assert.Equal(StatoLockAltrui.Libero, new LockService("PC-B").GetForeignActiveLock(_target, "Operatore B").Stato);

        Assert.True(new LockService("PC-A").TryAcquire(_target, "Operatore A").Acquisito);
        var occupato = new LockService("PC-B").GetForeignActiveLock(_target, "Operatore B");

        Assert.Equal(StatoLockAltrui.Occupato, occupato.Stato);
        Assert.Equal("Operatore A", occupato.Info!.Operatore);
    }

    [Fact]
    public void Release_ConFileBloccato_RestituisceErroreEConservaLaProprieta()
    {
        var locks = new LockService("PC-Release");
        Assert.True(locks.TryAcquire(_target, "Operatore A").Acquisito);

        using (new FileStream(_target + ".lock", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Il distacco non riesce per I/O: il lock resta su disco con il nostro SessionId.
            Assert.Equal(EsitoIo.Errore, locks.Release(_target));
            Assert.True(File.Exists(_target + ".lock"));
        }

        // La proprietà in memoria è stata conservata: a file libero il rilascio successivo rimuove davvero il lock.
        Assert.Equal(EsitoIo.Ok, locks.Release(_target));
        Assert.False(File.Exists(_target + ".lock"));
    }

    [Fact]
    public void Release_ConJsonIlleggibile_RestituisceErroreConservaProprietaERiprova()
    {
        var locks = new LockService("PC-Release-corrotto");
        Assert.True(locks.TryAcquire(_target, "Operatore A").Acquisito);
        var lockPath = _target + ".lock";
        var contenutoOriginale = File.ReadAllText(lockPath);
        File.WriteAllText(lockPath, "{corrotto");

        Assert.Equal(EsitoIo.Errore, locks.Release(_target));
        Assert.True(File.Exists(lockPath), "Il file illeggibile è ancora presente e il rilascio non va dichiarato riuscito.");

        File.WriteAllText(lockPath, contenutoOriginale);
        Assert.Equal(EsitoIo.Ok, locks.Release(_target));
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void Heartbeat_ConJsonIlleggibile_ConservaProprietaPerIlRilascioSuccessivo()
    {
        var locks = new LockService("PC-Heartbeat-corrotto");
        Assert.True(locks.TryAcquire(_target, "Operatore A").Acquisito);
        var lockPath = _target + ".lock";
        var contenutoOriginale = File.ReadAllText(lockPath);
        File.WriteAllText(lockPath, "{corrotto");

        Assert.Equal(EsitoIo.Errore, locks.Heartbeat(_target, "Operatore A"));

        File.WriteAllText(lockPath, contenutoOriginale);
        Assert.Equal(EsitoIo.Ok, locks.Release(_target));
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void Heartbeat_ConFileBloccato_RestituisceErroreSenzaRinnovare()
    {
        var locks = new LockService("PC-Heartbeat");
        Assert.True(locks.TryAcquire(_target, "Operatore A").Acquisito);
        using var bloccato = new FileStream(_target + ".lock", FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(EsitoIo.Errore, locks.Heartbeat(_target, "Operatore A"));
    }

    // P2.9: la scadenza richiede un'intera osservazione locale monotona dello stesso contenuto.

    [Fact]
    public void Lock_ScrittoConOrologioIndietro_NonEScadutoSeIlFileELFresco()
    {
        // Il PC che ha scritto il lock ha l'orologio 40 minuti indietro: il timestamp nel file è vecchio, ma il file è
        // stato aggiornato adesso. Il lock è vivo e non va sottratto a chi lo detiene.
        var orologioIndietro = DateTime.Now.AddMinutes(-40);
        File.WriteAllText(_target + ".lock", JsonSerializer.Serialize(new LockInfo
        {
            Operatore = "Mario",
            AcquisitoIl = orologioIndietro,
            UltimoHeartbeat = orologioIndietro,
            DeviceId = "PC-Remoto",
            SessionId = "sessione-remota"
        }, JsonIo.Options));

        var locale = new LockService("PC-Locale");
        var risultato = locale.TryAcquire(_target, "Luca");

        Assert.False(risultato.Acquisito, "Un lock vivo non va sottratto per colpa dell'orologio di chi lo ha scritto.");
        Assert.Equal("Mario", risultato.DetenutoDa?.Operatore);
        Assert.Equal(StatoLockAltrui.Occupato, locale.GetForeignActiveLock(_target, "Luca").Stato);
    }

    [Fact]
    public void Lock_ScrittoConOrologioAvanti_RecuperabileDopoOsservazioneMonotonaCompleta()
    {
        // Il timestamp nel file e l'mtime possono essere nel futuro: un primo lettore non deve sottrarre il lock.
        var clock = new TestMonotonicClock();
        var locale = new LockService("PC-Locale", clock.CreateTracker());
        var orologioAvanti = DateTime.Now.AddMinutes(40);
        var lockPath = _target + ".lock";
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new LockInfo
        {
            Operatore = "Mario",
            AcquisitoIl = orologioAvanti,
            UltimoHeartbeat = orologioAvanti,
            DeviceId = "PC-Remoto",
            SessionId = "sessione-morta"
        }, JsonIo.Options));
        File.SetLastWriteTimeUtc(lockPath, orologioAvanti.ToUniversalTime());

        Assert.False(locale.TryAcquire(_target, "Luca").Acquisito);
        clock.Advance(LockService.Timeout + TimeSpan.FromSeconds(1));
        var risultato = locale.TryAcquire(_target, "Luca");

        Assert.True(risultato.Acquisito, "Un lock identico osservato per oltre il timeout è scaduto, indipendentemente dagli orologi client.");
        Assert.Equal("Luca", risultato.DetenutoDa!.Operatore);
    }

    [Theory]
    [InlineData(-1, ModalitaCondivisioneService.ValoreOneDrive)]
    [InlineData(1, ModalitaCondivisioneService.ValoreRete)]
    public void Lock_PrimoAvvistamentoConTimestampRemotoPassatoOFuturo_RestaAttivo(int offsetOre, string modalita)
    {
        ModalitaCondivisioneService.ImpostaOverrideRadice(_dir, modalita);
        try
        {
            var clock = new TestMonotonicClock();
            var locks = new LockService("PC-Lettore", clock.CreateTracker());
            var remoteTime = DateTime.UtcNow.AddHours(offsetOre);
            var lockPath = _target + ".lock";
            File.WriteAllText(lockPath, JsonSerializer.Serialize(new LockInfo
            {
                Operatore = "Mario",
                AcquisitoIl = remoteTime,
                UltimoHeartbeat = remoteTime,
                DeviceId = "PC-Remoto",
                SessionId = "sessione-remota"
            }, JsonIo.Options));
            File.SetLastWriteTimeUtc(lockPath, remoteTime);

            Assert.NotNull(locks.GetActiveLock(_target));
            Assert.False(locks.TryAcquire(_target, "Luca").Acquisito);
        }
        finally { ModalitaCondivisioneService.ImpostaOverrideRadice(_dir, null); }
    }

    [Fact]
    public void Lock_HeartbeatAvanzante_ResettaLInteroPeriodoAncheConMtimeInvariato()
    {
        var clock = new TestMonotonicClock();
        var locks = new LockService("PC-Lettore", clock.CreateTracker());
        var lockPath = _target + ".lock";
        var mtimeRemoto = DateTime.UtcNow.AddYears(1);
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new LockInfo
        {
            Operatore = "Mario",
            AcquisitoIl = DateTime.UtcNow.AddHours(-3),
            UltimoHeartbeat = DateTime.UtcNow.AddHours(-1),
            DeviceId = "PC-Remoto",
            SessionId = "sessione-remota"
        }, JsonIo.Options));
        File.SetLastWriteTimeUtc(lockPath, mtimeRemoto);
        Assert.NotNull(locks.GetActiveLock(_target));

        clock.Advance(LockService.Timeout + TimeSpan.FromSeconds(1));
        var heartbeat = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(lockPath), JsonIo.Options)!;
        heartbeat.UltimoHeartbeat = DateTime.UtcNow;
        File.WriteAllText(lockPath, JsonSerializer.Serialize(heartbeat, JsonIo.Options));
        File.SetLastWriteTimeUtc(lockPath, mtimeRemoto);

        Assert.NotNull(locks.GetActiveLock(_target));
        Assert.False(locks.TryAcquire(_target, "Luca").Acquisito);

        clock.Advance(LockService.Timeout + TimeSpan.FromSeconds(1));
        Assert.Null(locks.GetActiveLock(_target));
        Assert.True(locks.TryAcquire(_target, "Luca").Acquisito);
    }

    [Fact]
    public void LeaseObservationTracker_CambioImprontaERemovalRadiceSonoConservativiEBounded()
    {
        var clock = new TestMonotonicClock();
        var tracker = clock.CreateTracker(capacity: 3);
        var root = Path.Combine(_dir, "workspace");
        var lockPath = Path.Combine(root, "a.json.lock");

        Assert.False(tracker.EScaduto(lockPath, "versione-1", LockService.Timeout));
        clock.Advance(LockService.Timeout + TimeSpan.FromSeconds(1));
        Assert.True(tracker.EScaduto(lockPath, "versione-1", LockService.Timeout));
        Assert.False(tracker.EScaduto(lockPath, "versione-2", LockService.Timeout));

        var otherPath = Path.Combine(root, "nested", "b.json.lock");
        tracker.EScaduto(otherPath, "b", LockService.Timeout);
        var outsidePath = Path.Combine(_dir, "elsewhere", "c.json.lock");
        tracker.EScaduto(outsidePath, "c", LockService.Timeout);
        new LockService("PC-Test", tracker).ResetLeaseObservationsUnderRoot(root);

        Assert.Equal(1, tracker.Count);
        Parallel.For(0, 100, i => tracker.EScaduto(Path.Combine(_dir, $"many-{i}.lock"), "same", LockService.Timeout));
        Assert.InRange(tracker.Count, 1, 3);
    }

    [Fact]
    public void LeaseObservationTracker_EvictionDaChurnNonAnticipaLaScadenza()
    {
        var clock = new TestMonotonicClock();
        var tracker = clock.CreateTracker(capacity: 2);
        var vivo = Path.Combine(_dir, "vivo.lock");
        var altro = Path.Combine(_dir, "altro.lock");
        var churn = Path.Combine(_dir, "churn.lock");

        Assert.False(tracker.EScaduto(vivo, "lease-vivo-1", LockService.Timeout));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(tracker.EScaduto(altro, "lease-altro", LockService.Timeout));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(tracker.EScaduto(vivo, "lease-vivo-2", LockService.Timeout));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(tracker.EScaduto(churn, "lease-churn-1", LockService.Timeout));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(tracker.EScaduto(Path.Combine(_dir, "churn-2.lock"), "lease-churn-2", LockService.Timeout));
        Assert.InRange(tracker.Count, 1, 2);

        // A heartbeat advanced while this path's observation was evicted; its old local age cannot expire it.
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.False(tracker.EScaduto(vivo, "lease-vivo-3", LockService.Timeout));
        clock.Advance(LockService.Timeout + TimeSpan.FromSeconds(1));
        Assert.True(tracker.EScaduto(vivo, "lease-vivo-3", LockService.Timeout));
    }

    [Fact]
    public void LeaseObservationTracker_RilascioEAssenzaRimuovonoLeEntry()
    {
        var clock = new TestMonotonicClock();
        var tracker = clock.CreateTracker();
        var locks = new LockService("PC-Test", tracker);

        Assert.True(locks.TryAcquire(_target, "Mario").Acquisito);
        Assert.NotNull(locks.GetActiveLock(_target));
        Assert.Equal(1, tracker.Count);
        Assert.Equal(EsitoIo.Ok, locks.Release(_target));
        Assert.Equal(0, tracker.Count);

        Assert.True(locks.TryAcquire(_target, "Mario").Acquisito);
        Assert.NotNull(locks.GetActiveLock(_target));
        File.Delete(_target + ".lock");
        Assert.Null(locks.GetActiveLock(_target));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void TryAcquire_ScriveITimestampInUtc()
    {
        Assert.True(new LockService("PC-A").TryAcquire(_target, "Mario").Acquisito);

        var scritto = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(_target + ".lock"), JsonIo.Options)!;
        Assert.Equal(DateTimeKind.Utc, scritto.AcquisitoIl.Kind);
        Assert.Equal(DateTimeKind.Utc, scritto.UltimoHeartbeat.Kind);
    }

    [Fact]
    public void TryDetachLock_LockSostituitoTraVerificaESpostamento_NonLoCancellaELoRipristina()
    {
        var lockPath = _target + ".lock";
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new LockInfo
        {
            Operatore = "Vecchio",
            AcquisitoIl = DateTime.UtcNow.AddHours(-2),
            UltimoHeartbeat = DateTime.UtcNow.AddHours(-2),
            DeviceId = "PC-X",
            SessionId = "vecchia"
        }, JsonIo.Options));
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - LockService.Timeout - TimeSpan.FromMinutes(5));

        // Il predicato vede il lock scaduto. Nella finestra prima dello spostamento un altro PC lo rilascia e ne crea uno
        // vivo sullo stesso path: File.Move agisce per path, quindi sposterebbe proprio quello nuovo.
        var esito = LockService.TryDetachLock(lockPath, snapshot =>
        {
            if (snapshot.Info?.SessionId != "vecchia") return false;
            File.Move(lockPath, lockPath + ".rilasciato");
            File.WriteAllText(lockPath, JsonSerializer.Serialize(new LockInfo
            {
                Operatore = "Luca",
                AcquisitoIl = DateTime.UtcNow,
                UltimoHeartbeat = DateTime.UtcNow,
                DeviceId = "PC-Y",
                SessionId = "viva"
            }, JsonIo.Options));
            return true;
        }, allowUnreadable: false, out _);

        Assert.Equal(EsitoDistacco.NonNecessario, esito);
        Assert.Equal("viva", LockService.TryReadLockInfo(_target)?.SessionId);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
    }

    [Fact]
    public void TryDetachLock_QuandoSecondaLetturaIlleggibile_RipristinaLockERestituisceErrore()
    {
        var lockPath = _target + ".lock";
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new LockInfo
        {
            Operatore = "Mario",
            AcquisitoIl = DateTime.UtcNow,
            UltimoHeartbeat = DateTime.UtcNow,
            DeviceId = "PC-X",
            SessionId = "valida"
        }, JsonIo.Options));

        var esito = LockService.TryDetachLock(lockPath, snapshot =>
        {
            if (snapshot.Info?.SessionId != "valida") return false;
            File.Move(lockPath, lockPath + ".originale");
            File.WriteAllText(lockPath, "{illeggibile");
            return true;
        }, allowUnreadable: false, out _);

        Assert.Equal(EsitoDistacco.Errore, esito);
        Assert.True(File.Exists(lockPath), "Il file ripristinato deve essere presente.");
        Assert.Equal("{illeggibile", File.ReadAllText(lockPath));
    }

    [Fact]
    public void Release_QuandoSecondaLetturaFallisce_RestituisceErroreEConservaProprieta()
    {
        var locks = new LockService("PC-Release-2");
        Assert.True(locks.TryAcquire(_target, "Operatore A").Acquisito);
        var lockPath = _target + ".lock";
        var contenutoOriginale = File.ReadAllText(lockPath);

        try
        {
            LockService.TestHookPrimaDiVerificaSpostato = (targetPath, detachedPath) =>
            {
                if (!HookRiguardaQuestoTarget(targetPath)) return;
                File.WriteAllText(detachedPath, "{corrotto-dopo-move");
            };

            var esito = locks.Release(_target);
            Assert.Equal(EsitoIo.Errore, esito);
            Assert.True(File.Exists(lockPath), "Il lock ripristinato deve essere presente.");
            Assert.Equal("{corrotto-dopo-move", File.ReadAllText(lockPath));

            // La sessione conserva la proprietà locale: rimesso il contenuto originale, il rilascio successivo riesce.
            LockService.TestHookPrimaDiVerificaSpostato = null;
            File.WriteAllText(lockPath, contenutoOriginale);
            Assert.Equal(EsitoIo.Ok, locks.Release(_target));
            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            LockService.TestHookPrimaDiVerificaSpostato = null;
        }
    }
}
