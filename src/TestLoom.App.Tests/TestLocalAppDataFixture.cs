using System.Runtime.CompilerServices;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Isola TUTTI i dati locali della suite di test, e dei processi che la suite lancia, in una radice temporanea unica per
/// esecuzione: %APPDATA% (crash.log, trace.log, config.json) e %LOCALAPPDATA% (TestLoom\workspaces, workflow-transactions,
/// recovery-drafts, ...). Così nessun test scrive né legge i file reali della macchina dello sviluppatore (piano 6.4.2 §5:
/// prima dell'isolamento gli indici locali di decine di cartelle di test finivano sotto il %LOCALAPPDATA% reale).
/// <para>
/// La radice è impostata in due modi: come variabile d'ambiente <c>TestLoom_DATA_ROOT</c>, così ereditata da ogni processo figlio
/// (per esempio il host MultiPcHost), e direttamente in <see cref="TestLoomDataRoot"/>, così non dipende dall'ordine dei
/// ModuleInitializer dell'assembly. <see cref="WorkspaceLocalPaths.LocalApplicationDataOverrideForTests"/> resta puntato alla
/// stessa LocalAppData per coerenza con i test esistenti.
/// </para>
/// <para>
/// Il <see cref="ModuleInitializer"/> gira una sola volta per processo di test, prima di qualunque [Fact]. La cartella è unica
/// per questa esecuzione (GUID), così esecuzioni parallele non si pestano i piedi, e viene ripulita a fine processo (best
/// effort: la pulizia non fa mai fallire l'uscita del test runner). La stessa fixture è collegata nel progetto Headless.
/// </para>
/// <para>
/// In produzione questo file non esiste (è nel progetto TestLoom.App.Tests, non in TestLoom.App): <see cref="TestLoomDataRoot"/>
/// resta senza radice e nessun comportamento cambia.
/// </para>
/// </summary>
internal static class TestLocalAppDataFixture
{
    private static readonly string RadiceDiQuestaEsecuzione = Path.Combine(
        Path.GetTempPath(), "TestLoom_TestData_" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(RadiceDiQuestaEsecuzione);
        Environment.SetEnvironmentVariable(TestLoomDataRoot.NomeVariabile, RadiceDiQuestaEsecuzione);
        TestLoomDataRoot.ImpostaRadiceProcesso(RadiceDiQuestaEsecuzione);
        WorkspaceLocalPaths.LocalApplicationDataOverrideForTests = TestLoomDataRoot.LocalAppDataRoot;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    private static void Cleanup()
    {
        try
        {
            if (Directory.Exists(RadiceDiQuestaEsecuzione))
                Directory.Delete(RadiceDiQuestaEsecuzione, recursive: true);
        }
        catch
        {
            // Best effort: la pulizia non deve mai far fallire l'uscita del processo di test. Una cartella temporanea residua
            // sotto %TEMP% non inquina i dati reali e verrà ripulita dal sistema.
        }
    }
}
