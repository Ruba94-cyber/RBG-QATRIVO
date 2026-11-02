using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Bug "in modifica da un'altra postazione" mentre si e' l'unico a modificare: i lock propri scaduti per
/// heartbeat saltati (OneDrive) vanno riacquisiti in modo trasparente; solo un lock vivo di un'altra sessione e'
/// un conflitto. Due istanze di LockService sulla stessa cartella simulano due PC (stesso nome operatore).</summary>
public class LockEnsureOwnedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoomEnsureOwned_" + Guid.NewGuid().ToString("N"));
    private readonly string _target;
    private string LockPath => LockService.LockPathFor(_target);

    public LockEnsureOwnedTests()
    {
        Directory.CreateDirectory(_dir);
        _target = Path.Combine(_dir, "componente.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private void Backdate(TimeSpan eta)
    {
        var info = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(LockPath), JsonIo.Options)!;
        info.UltimoHeartbeat = DateTime.Now - eta;
        info.AcquisitoIl = DateTime.Now - eta;
        File.WriteAllText(LockPath, JsonSerializer.Serialize(info, JsonIo.Options));
        // Il timestamp del writer è solo contenuto: la scadenza parte dal primo avvistamento locale.
        File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow - eta);
    }

    [Fact]
    public void LockProprioScadutoSenzaAltriDetentori_EnsureOwnedRinnovaESenzaConflitto()
    {
        var clock = new TestMonotonicClock();
        var pc1 = new LockService("PC-Test", clock.CreateTracker());
        Assert.True(pc1.TryAcquire(_target, "Mario").Acquisito);
        Backdate(LockService.Timeout + TimeSpan.FromMinutes(10));
        Assert.NotNull(pc1.GetActiveLock(_target)); // il primo lettore aspetta l'intero timeout locale
        clock.Advance(LockService.Timeout + TimeSpan.FromMinutes(10));
        Assert.Null(pc1.GetActiveLock(_target));

        var esito = pc1.EnsureOwned(_target, "Mario");

        Assert.True(esito.Posseduto);
        Assert.Null(esito.Holder);
        Assert.NotNull(pc1.GetActiveLock(_target));
        Assert.Equal(StatoLockAltrui.Libero, pc1.GetForeignActiveLock(_target).Stato);
        Assert.True(pc1.IsOwnedByThisSession(_target));
    }

    [Fact]
    public void LockProprioScadutoMaSostituitoDaAltraSessioneScaduta_EnsureOwnedRiacquisisce()
    {
        var clock = new TestMonotonicClock();
        var observations = clock.CreateTracker();
        var pc1 = new LockService("PC-Test", observations);
        pc1.TryAcquire(_target, "Mario");
        Backdate(LockService.Timeout + TimeSpan.FromMinutes(10));
        Assert.NotNull(pc1.GetActiveLock(_target));
        clock.Advance(LockService.Timeout + TimeSpan.FromMinutes(10));
        // Un altro PC (stesso operatore) ripulisce lo scaduto e prende il lock, poi lo perde a sua volta.
        var pc2 = new LockService("PC-Test", observations);
        Assert.True(pc2.TryAcquire(_target, "Mario").Acquisito);
        pc2.Release(_target);

        var esito = pc1.EnsureOwned(_target, "Mario");

        Assert.True(esito.Posseduto);
        Assert.True(esito.Riacquisito);
        Assert.Null(esito.Holder);
        Assert.True(pc1.IsOwnedByThisSession(_target));
    }

    [Fact]
    public void LockVivoDiUnAltroPcConStessoOperatore_EConflittoReale()
    {
        var pc1 = new LockService("PC-A");
        var pc2 = new LockService("PC-B"); // stesso operatore, altro PC
        Assert.True(pc2.TryAcquire(_target, "Mario").Acquisito);

        var esito = pc1.EnsureOwned(_target, "Mario");

        Assert.False(esito.Posseduto);
        Assert.NotNull(esito.Holder);
        Assert.Equal(StatoLockAltrui.Occupato, pc1.GetForeignActiveLock(_target).Stato);
        Assert.Equal(StatoLockAltrui.Libero, pc2.GetForeignActiveLock(_target).Stato);
        Assert.True(pc2.IsOwnedByThisSession(_target));
        Assert.False(pc1.IsOwnedByThisSession(_target));
    }

    [Fact]
    public void LockProprioCancellatoDaCleanup_EnsureOwnedLoRiacquisisce()
    {
        var pc1 = new LockService();
        pc1.TryAcquire(_target, "Mario");
        File.Delete(LockPath); // rimosso dalla manutenzione / dal sync

        var esito = pc1.EnsureOwned(_target, "Mario");

        Assert.True(esito.Posseduto);
        Assert.True(esito.Riacquisito);
        Assert.Null(esito.Holder);
        Assert.True(File.Exists(LockPath));
        Assert.True(pc1.IsOwnedByThisSession(_target));
    }

    [Fact]
    public void LockProprioNonScaduto_EnsureOwnedNonRiacquisisce()
    {
        var pc1 = new LockService();
        pc1.TryAcquire(_target, "Mario");

        var esito = pc1.EnsureOwned(_target, "Mario");

        Assert.True(esito.Posseduto);
        Assert.False(esito.Riacquisito);
    }

    [Fact]
    public void DeviceId_StabileTraLeAcquisizioni()
    {
        var locks = new LockService();
        locks.TryAcquire(_target, "Mario");
        var primo = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(LockPath), JsonIo.Options)!.DeviceId;
        File.Delete(LockPath);
        locks.TryAcquire(_target, "Mario");
        var secondo = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(LockPath), JsonIo.Options)!.DeviceId;

        Assert.False(string.IsNullOrWhiteSpace(primo));
        Assert.Equal(primo, secondo);
        Assert.Equal(primo, locks.DeviceId);
    }

    [Fact]
    public void Heartbeat_SottoHandleEsclusivoTransitorio_RitentaEAggiorna()
    {
        var locks = new LockService();
        locks.TryAcquire(_target, "Mario");
        Backdate(TimeSpan.FromMinutes(15)); // il file resta nostro (SessionId invariato), heartbeat vecchio
        var prima = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(LockPath), JsonIo.Options)!.UltimoHeartbeat;

        // Simula il client di sync che trattiene il file per ~300 ms, poi lo rilascia.
        var stream = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var rilascio = new Thread(() => { Thread.Sleep(300); stream.Dispose(); });
        rilascio.Start();

        locks.Heartbeat(_target, "Mario");
        rilascio.Join();

        var dopo = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(LockPath), JsonIo.Options)!.UltimoHeartbeat;
        // Il rinnovo è scritto in UTC: si confrontano gli istanti, non i tick di due Kind diversi.
        Assert.True(dopo.ToUniversalTime() > prima.ToUniversalTime().AddMinutes(10));
        Assert.True(locks.IsOwnedByThisSession(_target)); // l'ownership non e' andata persa
    }

    [Fact]
    public void Heartbeat_ConHandleOccupatoPerSempre_NonLanciaENonPerdeOwnership()
    {
        var locks = new LockService();
        locks.TryAcquire(_target, "Mario");
        using var stream = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var ex = Record.Exception(() => locks.Heartbeat(_target, "Mario"));

        Assert.Null(ex);
        stream.Dispose();
        Assert.True(locks.IsOwnedByThisSession(_target));
    }

    [Fact]
    public void Heartbeat_ConsenteLetturaConcorrente()
    {
        var locks = new LockService();
        locks.TryAcquire(_target, "Mario");
        // Un lettore (GetActiveLock di un altro PC) non deve mai vedere un file vuoto/illeggibile durante il rinnovo.
        for (var i = 0; i < 20; i++)
        {
            locks.Heartbeat(_target, "Mario");
            Assert.NotNull(LockService.TryReadLockInfo(_target));
        }
    }

    [Fact]
    public void ResiduoDiUnaNostraEsecuzionePrecedente_StessoOperatoreEDevice_EnsureOwnedLoRiacquisisceSenzaAvviso()
    {
        var precedente = new LockService("PC-A");
        Assert.True(precedente.TryAcquire(_target, "Mario").Acquisito); // app crashata: lock vivo ma orfano
        var nuova = new LockService("PC-A"); // stessa postazione, nuova esecuzione

        Assert.Equal(StatoLockAltrui.Libero, nuova.GetForeignActiveLock(_target, "Mario").Stato);
        var esito = nuova.EnsureOwned(_target, "Mario");

        Assert.True(esito.Posseduto);
        Assert.Null(esito.Holder);
        Assert.True(nuova.IsOwnedByThisSession(_target));
        Assert.False(precedente.IsOwnedByThisSession(_target));
    }

    [Fact]
    public void ResiduoStessoDeviceMaOperatoreDiverso_RestaAltrui()
    {
        var a = new LockService("PC-A");
        a.TryAcquire(_target, "Luca");
        var b = new LockService("PC-A");

        Assert.Equal(StatoLockAltrui.Occupato, b.GetForeignActiveLock(_target, "Mario").Stato);
        Assert.NotNull(b.EnsureOwned(_target, "Mario").Holder);
    }

    [Fact]
    public void StessoOperatoreDeviceDiverso_TryAcquireNonRubaIlLock()
    {
        var a = new LockService("PC-A");
        a.TryAcquire(_target, "Mario");
        var b = new LockService("PC-B");

        var r = b.TryAcquire(_target, "Mario");

        Assert.False(r.Acquisito);
        Assert.True(a.IsOwnedByThisSession(_target));
    }

    // P2.1: un rinnovo che non riesce a scrivere il file non deve essere dichiarato confermato.

    [Fact]
    public void EnsureOwned_FileNonLeggibileDaAltriProcessi_NonConfermaIlLock()
    {
        var locks = new LockService("PC-Rinnovo");
        Assert.True(locks.TryAcquire(_target, "Mario").Acquisito);

        using (new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var esito = locks.EnsureOwned(_target, "Mario");
            Assert.False(esito.Posseduto);
            Assert.Null(esito.Holder);
            Assert.False(esito.Confermato);
        }

        Assert.True(locks.EnsureOwned(_target, "Mario").Confermato);
    }

    [Fact]
    public void EnsureOwned_FileNonScrivibileMaLeggibile_MantieneIlPossessoMaNonLoConferma()
    {
        var locks = new LockService("PC-Rinnovo");
        Assert.True(locks.TryAcquire(_target, "Mario").Acquisito);

        using (new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var esito = locks.EnsureOwned(_target, "Mario");
            Assert.True(esito.Posseduto);
            Assert.False(esito.Confermato);
        }

        Assert.True(locks.EnsureOwned(_target, "Mario").Confermato);
    }
}
