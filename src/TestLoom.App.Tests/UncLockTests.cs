using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.6.1 (f, test lock su UNC): acquisizione, esclusione fra due PC e rilascio dei lock su un vero percorso UNC.
/// Il test gira solo se è disponibile una cartella UNC scrivibile: TestLoom_TEST_UNC_DIR (per esempio UNC server\share\testloom-test,
/// condivisione SMB di prova raggiunta in LAN o VPN) oppure la radice amministrativa UNC localhost\C$ in una sessione
/// amministratore. Ogni artefatto (probe, lock, commit) sta SOLO in una sottocartella univoca creata per il test e rimossa al
/// termine: nessun file viene scritto direttamente nella radice della condivisione (6.6.1, correzione G). Se nessuna delle due
/// è disponibile, il MemberData non produce righe e xunit riporta il test come SKIPPED (No data found).</summary>
[Collection("CondivisioneRete")]
public class UncLockTests
{
    public static IEnumerable<object[]> CartelleUncScrivibili()
    {
        var variabile = Environment.GetEnvironmentVariable("TestLoom_TEST_UNC_DIR");
        if (!string.IsNullOrWhiteSpace(variabile) && ScrivibileSicuro(variabile))
        {
            yield return new object[] { variabile };
            yield break;
        }
        var amministrativa = @"\" + @"\localhost\C$\Users\Public";
        if (ScrivibileSicuro(amministrativa)) yield return new object[] { amministrativa };
    }

    /// <summary>
    /// Verifica la scrittura creando una sottocartella univoca, un probe dentro di essa, e rimuovendo tutto. Mai un file nella
    /// radice della condivisione.
    /// </summary>
    private static bool ScrivibileSicuro(string cartella)
    {
        var sottocartella = Path.Combine(cartella, "TestLoom_probe_" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!Directory.Exists(cartella)) return false;
            Directory.CreateDirectory(sottocartella);
            var prova = Path.Combine(sottocartella, "testloom-prova.tmp");
            File.WriteAllText(prova, "prova");
            File.Delete(prova);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { if (Directory.Exists(sottocartella)) Directory.Delete(sottocartella, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>Sottocartella univoca del test, creata sotto la base e rimossa al termine: tutti gli artefatti vi stanno dentro.</summary>
    private static string SottocartellaDiProva(string cartellaUnc, string prefisso)
    {
        var dir = Path.Combine(cartellaUnc, prefisso + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [MemberData(nameof(CartelleUncScrivibili))]
    public void CommitLock_SuPercorsoUnc_EscludeLaSecondaScritturaFinoAlRilascio(string cartellaUnc)
    {
        using var _ = new OverrideModalitaScope(null);
        var cartella = SottocartellaDiProva(cartellaUnc, "TestLoom_commit_");
        try
        {
            var file = Path.Combine(cartella, "testcase_2.json");
            File.WriteAllText(file, "{}");

            using (TestCaseCommitLock.AcquireAll("TC-2", new[] { file }, TimeSpan.FromSeconds(5)))
            {
                // Mentre il commit è in corso, un secondo salvataggio sullo stesso record scade invece di sovrascrivere.
                Assert.Throws<TestCaseSaveLockTimeoutException>(() =>
                    TestCaseCommitLock.AcquireAll("TC-2", new[] { file }, TimeSpan.FromSeconds(1)).Dispose());
            }

            // Dopo il rilascio il record torna disponibile.
            using var dopo = TestCaseCommitLock.AcquireAll("TC-2", new[] { file }, TimeSpan.FromSeconds(5));
            Assert.NotNull(dopo);
        }
        finally
        {
            try { Directory.Delete(cartella, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [MemberData(nameof(CartelleUncScrivibili))]
    public void Lock_SuPercorsoUnc_AcquisisceEscludeIlSecondoPcERilascia(string cartellaUnc)
    {
        using var _ = new OverrideModalitaScope(null);
        var cartella = SottocartellaDiProva(cartellaUnc, "TestLoom_lock_");
        try
        {
            var file = Path.Combine(cartella, "testcase_1.json");
            File.WriteAllText(file, "{}");
            Assert.Equal(ModalitaCondivisione.CartellaRete, ModalitaCondivisioneService.Effettiva(file));
            Assert.Equal(TimeoutCondivisione.CartellaRete, TimeoutCondivisione.Per(file));

            var primoPc = new LockService("PC-A");
            var secondoPc = new LockService("PC-B");
            Assert.True(primoPc.TryAcquire(file, "Operatore A").Acquisito);
            Assert.NotNull(primoPc.GetActiveLock(file));
            Assert.False(secondoPc.TryAcquire(file, "Operatore B").Acquisito);

            primoPc.Release(file);
            Assert.True(secondoPc.TryAcquire(file, "Operatore B").Acquisito);
            secondoPc.Release(file);
        }
        finally
        {
            try { Directory.Delete(cartella, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
