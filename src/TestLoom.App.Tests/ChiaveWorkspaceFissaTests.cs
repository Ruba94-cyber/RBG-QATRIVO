using System.Security.Cryptography;
using System.Text;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.6.1 (decisione A): la chiave del workspace locale è fissata all'apertura per ogni cartella configurata e non
/// segue lo stato delle unità durante la sessione. Un utente esistente con dati locali sotto la chiave con la lettera la
/// mantiene per sempre; una nuova configurazione usa la chiave canonica (lettera risolta in UNC); lettera e UNC della stessa
/// cartella puntano alla stessa chiave tramite alias salvati nel config del PC. Nessun dato viene spostato. Ogni test usa un
/// percorso unico, così lo stato locale di un test non influenza gli altri.</summary>
[Collection("CondivisioneRete")]
public class ChiaveWorkspaceFissaTests : IDisposable
{
    public ChiaveWorkspaceFissaTests() => WorkspaceLocalPaths.ReimpostaChiaviSessione();

    public void Dispose() => WorkspaceLocalPaths.ReimpostaChiaviSessione();

    /// <summary>Chiave con la formula in uso prima della 6.6.1 (SHA-256 del percorso in maiuscolo, 32 esadecimali).</summary>
    private static string ChiaveStorica(string percorso)
    {
        var normalizzato = Path.GetFullPath(percorso).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizzato)))[..32];
    }

    private static string Ortografia(string percorso) => Path.GetFullPath(percorso).TrimEnd('\\');

    /// <summary>Lettera Z: unica per il test; il percorso UNC corrisponde via il risolutore fittizio (UNC srv\dati).</summary>
    private static string PercorsoUnico() => @"Z:\testloom-" + Guid.NewGuid().ToString("N");

    private static string UncPer(string percorsoConLettera) => @"\" + @"\srv\dati\" + Path.GetFileName(percorsoConLettera);

    private static Dictionary<string, string> Configurazione() => new(StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(@"C:\TestLoom\Dati")]
    [InlineData(@"C:\Users\operatore\OneDrive - Azienda\TestLoom")]
    [InlineData(@"\" + @"\srv\share\TestLoom")]
    public void PercorsiLocaliOneDriveEUncRestanoSullaChiaveStorica(string percorso)
    {
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));
        Assert.Equal(ChiaveStorica(percorso), WorkspaceLocalPaths.ComputeWorkspaceKey(percorso));
    }

    [Fact]
    public void NuovaConfigurazione_LetteraEUncDannoLaStessaChiaveCanonicaEAlias()
    {
        var z = PercorsoUnico();
        var unc = UncPer(z);
        var salvate = Configurazione();
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));

        var chiave = WorkspaceLocalPaths.FissaChiaveAllApertura(z, salvate, out var modificata);

        Assert.True(modificata);
        Assert.Equal(ChiaveStorica(unc), chiave);                       // nuova configurazione: chiave canonica (UNC)
        Assert.Equal(chiave, WorkspaceLocalPaths.ComputeWorkspaceKey(z));
        Assert.Equal(chiave, WorkspaceLocalPaths.ComputeWorkspaceKey(unc));
        Assert.Equal(chiave, salvate[Ortografia(z)]);                   // alias salvati nel config del PC
        Assert.Equal(chiave, salvate[unc]);
    }

    [Fact]
    public void UtenteEsistente_MantieneLaChiaveConLaLetteraEIlSuoOutboxResta()
    {
        var z = PercorsoUnico();
        var chiaveGrezza = ChiaveStorica(z);
        // Dati di un utente già esistente, sotto la chiave con la lettera: outbox del journal dei record.
        var outbox = Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "workspaces", chiaveGrezza, "record-journal", "outbox.ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(outbox)!);
        File.WriteAllText(outbox, "{\"evento\":1}\n");
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));

        var chiave = WorkspaceLocalPaths.FissaChiaveAllApertura(z, Configurazione(), out var modificataIgnorata);

        Assert.Equal(chiaveGrezza, chiave);
        // Il workspace della sessione legge lo stesso outbox: il flush trova i dati lasciati prima.
        Assert.Equal(outbox, Path.Combine(WorkspaceLocalPaths.GetWorkspaceDirectory(z), "record-journal", "outbox.ndjson"));
        Assert.True(File.Exists(outbox));
    }

    [Fact]
    public void Disconnessione_NonCambiaLaChiaveDellaSessione()
    {
        var z = PercorsoUnico();
        var salvate = Configurazione();
        string chiave;
        using (new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati")))
            chiave = WorkspaceLocalPaths.FissaChiaveAllApertura(z, salvate, out var modificataIgnorata);

        // La VPN cade: l'unità resta di rete ma il percorso non è più risolvibile, oppure la lettera non è più mappata.
        using (new RisolutoreUnitaFinto(("Z:", null)))
            Assert.Equal(chiave, WorkspaceLocalPaths.ComputeWorkspaceKey(z));
        using (new RisolutoreUnitaFinto())
            Assert.Equal(chiave, WorkspaceLocalPaths.ComputeWorkspaceKey(z));
    }

    [Fact]
    public void BozzeScritteDuranteIlGuasto_SonoVisibiliDopoLaRiconnessione()
    {
        var z = PercorsoUnico();
        var salvate = Configurazione();
        string cartellaBozze;
        using (new RisolutoreUnitaFinto(("Z:", null)))   // apertura durante il guasto: nuova configurazione non risolvibile
        {
            WorkspaceLocalPaths.FissaChiaveAllApertura(z, salvate, out var modificataIgnorata);
            cartellaBozze = FailedSaveDraftService.LocalDirectory(z);
            Directory.CreateDirectory(cartellaBozze);
            File.WriteAllText(Path.Combine(cartellaBozze, "bozza.json"), "scritta-durante-il-guasto");
        }
        using (new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati")))   // riconnessione
        {
            Assert.Equal(cartellaBozze, FailedSaveDraftService.LocalDirectory(z));
            Assert.Equal("scritta-durante-il-guasto", File.ReadAllText(Path.Combine(cartellaBozze, "bozza.json")));
        }
    }

    [Fact]
    public void FissareDueVolte_NonCambiaLaChiaveNeLaConfigurazione()
    {
        var z = PercorsoUnico();
        var salvate = Configurazione();
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));
        var prima = WorkspaceLocalPaths.FissaChiaveAllApertura(z, salvate, out var modificataIgnorata);
        var seconda = WorkspaceLocalPaths.FissaChiaveAllApertura(z, salvate, out var modificata);
        Assert.Equal(prima, seconda);
        Assert.False(modificata);
    }

    [Fact]
    public void UnitaMappataNonRisolta_ConservaLaChiaveGrezzaSenzaRegistrazione()
    {
        using var _ = new RisolutoreUnitaFinto();
        Assert.Equal(ChiaveStorica(@"Y:\TestLoom"), WorkspaceLocalPaths.ComputeWorkspaceKey(@"Y:\TestLoom"));
    }
}
