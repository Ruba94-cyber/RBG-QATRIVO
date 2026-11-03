using System.Runtime.CompilerServices;
using TestLoom.App.Services;

namespace TestLoom.App.UITests.Infrastructure;

/// <summary>
/// Radice dati isolata per la suite FlaUI. Ogni RBG - QATRIVO.exe lanciato dalla suite (<see cref="TestLoomApplication.Launch"/> e il
/// secondo processo di StartupTests) eredita la variabile <c>TestLoom_DATA_ROOT</c> impostata qui, quindi scrive crash.log,
/// trace.log e %LOCALAPPDATA%\TestLoom sotto una cartella temporanea di questa esecuzione invece che nei file reali
/// dell'utente. La cartella viene cancellata a fine processo (best effort). Stesso schema di TestLocalAppDataFixture
/// (xUnit): ModuleInitializer, quindi prima di qualunque test.
/// </summary>
internal static class UiTestDataRootFixture
{
    private static readonly string RadiceDiQuestaEsecuzione = Path.Combine(
        Path.GetTempPath(), "TestLoom_UiTestData_" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(RadiceDiQuestaEsecuzione);
        Environment.SetEnvironmentVariable(TestLoomDataRoot.NomeVariabile, RadiceDiQuestaEsecuzione);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    private static void Cleanup()
    {
        // Diagnosi: con TestLoom_UITEST_CONSERVA_DATI=1 la radice resta su disco (trace.log, crash.log) per leggere i tempi di chiusura.
        if (Environment.GetEnvironmentVariable("TestLoom_UITEST_CONSERVA_DATI") == "1") return;
        try
        {
            if (Directory.Exists(RadiceDiQuestaEsecuzione))
                Directory.Delete(RadiceDiQuestaEsecuzione, recursive: true);
        }
        catch
        {
            // Best effort: un RBG - QATRIVO.exe ancora aperto può tenere bloccati dei file; la cartella temporanea non inquina i dati reali.
        }
    }
}
