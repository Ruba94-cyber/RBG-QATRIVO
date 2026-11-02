using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Analisi richiesta (release/6.4.2, scenario multi-PC "due postazioni offline creano ciascuna un Test con lo
/// stesso operatore/iniziali"): IdGenerator.Genera è deliberatamente decentralizzato (vedi il commento in
/// IdGenerator.cs) — ogni istanza calcola il proprio progressivo dagli ID che CONOSCE localmente, senza
/// contatore centrale. Due postazioni offline che partono dallo stesso storico (stesso numero di Test
/// esistenti) e usano le STESSE iniziali operatore calcolano perciò, in modo del tutto legittimo, la STESSA
/// stringa Id ("T-OPA-0006" == "T-OPA-0006"): non è un bug del generatore, è una conseguenza necessaria di non
/// avere un contatore centrale (scelta di design esplicita, irrinunciabile senza un server).
///
/// Decisione (motivata nel report finale): NON si è cambiato il formato Id (niente componente-dispositivo
/// incorporata: comprometterebbe la brevità/leggibilità voluta dal feedback utente 2026-08-20/24) e NON si
/// riscrive/rinumera automaticamente al momento della sincronizzazione (riscrivere file + tutti i riferimenti
/// incrociati NC/CR/Segnalazioni in automatico su un semplice sospetto di collisione è un'operazione a rischio
/// dati maggiore del problema che risolverebbe, su un percorso cross-processo già delicato). La rete di
/// sicurezza ESISTENTE (TestCaseRepository.ClassificaDuplicatiEEscludiCopieDiConflitto + DuplicatiId,
/// verificata sopra in TestCaseConflictCopyTests) è già quella corretta per questo caso: nessun file viene mai
/// sovrascritto o cancellato, la collisione resta sempre visibile (banner "ID duplicati" in Impostazioni) per
/// una risoluzione manuale di un Master. Questo test dimostra che quella rete di sicurezza regge davvero nello
/// scenario concreto "due postazioni offline, stesse iniziali".
/// </summary>
public sealed class IdCollisionMultiPcTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_IdCollision_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Genera_DuePostazioniOfflineStessoStoricoEStesseIniziali_ProduceLoStessoId()
    {
        // Precondizione della collisione: due chiamate decentralizzate indipendenti, stesso snapshot di ID
        // esistenti (le due postazioni non si sono ancora risincronizzate), stesse iniziali operatore.
        var idEsistentiSuEntrambePostazioni = new[] { "T-OPA-0001", "T-OPA-0002" };

        var idPostazioneA = IdGenerator.Genera(idEsistentiSuEntrambePostazioni, "OPA");
        var idPostazioneB = IdGenerator.Genera(idEsistentiSuEntrambePostazioni, "OPA");

        Assert.Equal(idPostazioneA, idPostazioneB); // "T-OPA-0003" su entrambe: la collisione è reale e attesa
    }

    [Fact]
    public void DuePostazioniOfflineCreanoTestConStessoIdEStesseIniziali_DopoSyncNessunDatoPersoEConflittoVisibile()
    {
        var pathsA = new DataPaths(Path.Combine(_root, "postazioneA"));
        pathsA.EnsureStructure();
        var repoA = new TestCaseRepository(pathsA);

        var pathsB = new DataPaths(Path.Combine(_root, "postazioneB"));
        pathsB.EnsureStructure();
        var repoB = new TestCaseRepository(pathsB);

        // Ciascuna postazione, offline, genera il proprio Id dal proprio storico locale (vuoto su entrambe:
        // caso limite più semplice della collisione, stesso principio del test sopra con storico non vuoto).
        var idA = IdGenerator.Genera(Array.Empty<string>(), "OPA");
        var testA = new TestCase { Id = idA, Componente = "ECM", Veicolo = "Daily", Descrizione = "creato su A" };
        repoA.Save(testA, isNew: true);

        var idB = IdGenerator.Genera(Array.Empty<string>(), "OPA");
        var testB = new TestCase { Id = idB, Componente = "ECM", Veicolo = "Daily", Descrizione = "creato su B" };
        repoB.Save(testB, isNew: true);

        Assert.Equal(idA, idB); // collisione riprodotta: stesso Id, due Test realmente distinti (Uid diversi)
        Assert.NotEqual(testA.Uid, testB.Uid);

        // Sincronizzazione OneDrive: i due file, con lo stesso nome canonico, finiscono nella stessa cartella
        // condivisa (comportamento reale di una cartella OneDrive unica multi-PC).
        var pathsCondivisi = new DataPaths(Path.Combine(_root, "condivisa"));
        pathsCondivisi.EnsureStructure();
        var pathA = repoA.PercorsoPer(testA);
        var pathB = repoB.PercorsoPer(testB);
        var dirCondivisa = pathsCondivisi.TestCaseDir("ECM");
        Directory.CreateDirectory(dirCondivisa);
        var destA = Path.Combine(dirCondivisa, Path.GetFileName(pathA));
        File.Copy(pathA, destA);
        // OneDrive non sovrascrive mai silenziosamente un file con contenuto diverso: la seconda scrittura
        // arriva come copia di conflitto rinominata (qui simulata direttamente col suffisso già rinominato).
        var destBConflitto = destA.Replace(".json", "-POSTAZIONEB.json");
        File.Copy(pathB, destBConflitto);

        var repoCondiviso = new TestCaseRepository(pathsCondivisi);
        repoCondiviso.RefreshIndex();

        // Nessun dato perso: entrambi i file restano sul disco e leggibili.
        Assert.True(File.Exists(destA));
        Assert.True(File.Exists(destBConflitto));
        var recuperatoB = JsonIo.TryRead<TestCase>(destBConflitto, out _);
        Assert.Equal("creato su B", recuperatoB?.Descrizione);

        // Nessuna sovrascrittura silenziosa: siccome i due Test hanno Uid DIVERSI, il classificatore
        // content-driven non li tratta come "stessa copia OneDrive" (quel percorso è per un vero duplicato
        // fisico dello stesso Test) — restano un vero ID duplicato ambiguo, sempre visibile, mai perso.
        var tutti = repoCondiviso.GetAll();
        Assert.Equal(2, tutti.Count); // entrambi i file restano indicizzati (nessuno rimosso/scartato)...
        var duplicato = Assert.Single(repoCondiviso.DuplicatiId); // ...e la collisione resta segnalata, non nascosta
        Assert.Equal(idA, duplicato.Id);
        Assert.Equal(2, duplicato.Percorsi.Count);
    }
}
