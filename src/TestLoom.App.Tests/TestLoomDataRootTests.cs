using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Isolamento dei dati locali (variabile TestLoom_DATA_ROOT): la suite e i processi che lancia non scrivono mai in
/// %APPDATA% o %LOCALAPPDATA% reali, e senza la variabile (produzione) i percorsi restano quelli reali.
/// </summary>
[Collection("TracciaStatica")]
public class TestLoomDataRootTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Interpreta_VariabileAssenteOVuota_SignificaProduzione(string? valore)
    {
        Assert.Null(TestLoomDataRoot.InterpretaRadice(valore));
    }

    [Fact]
    public void Interpreta_PercorsoTraVirgolette_DiventaAssolutoSenzaVirgolette()
    {
        var atteso = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TestLoom_Prova_" + Guid.NewGuid().ToString("N")));

        Assert.Equal(atteso, TestLoomDataRoot.InterpretaRadice("  \"" + atteso + "\"  "));
    }

    [Fact]
    public void RadiceAppDataELocalAppData_SenzaRadice_RestanoLeCartelleReali()
    {
        const string appData = @"C:\Users\utente\AppData\Roaming";
        const string localAppData = @"C:\Users\utente\AppData\Local";

        Assert.Equal(appData, TestLoomDataRoot.RadiceAppData(null, appData));
        Assert.Equal(localAppData, TestLoomDataRoot.RadiceLocalAppData(null, localAppData));
    }

    [Fact]
    public void RadiceAppDataELocalAppData_ConRadice_PuntanoAlleSottocartelleIsolate()
    {
        var radice = Path.Combine(Path.GetTempPath(), "TestLoom_Radice_Prova");

        Assert.Equal(Path.Combine(radice, "AppData"), TestLoomDataRoot.RadiceAppData(radice, @"C:\reale"));
        Assert.Equal(Path.Combine(radice, "LocalAppData"), TestLoomDataRoot.RadiceLocalAppData(radice, @"C:\reale"));
    }

    [Fact]
    public void ProcessoDiTest_PercorsiLocaliSonoSempreSottoLaRadiceIsolata()
    {
        var radice = TestLoomDataRoot.Radice;
        Assert.NotNull(radice);

        Assert.StartsWith(radice!, TestLoomDataRoot.AppDataRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(radice!, TestLoomDataRoot.LocalAppDataRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(radice!, CrashLogService.LogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(radice!, new AppConfigService().CartellaLocale, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            TestLoomDataRoot.LocalAppDataRoot);
    }

    [Fact]
    public void EsecuzioneDiProva_NonScriveMaiNeiLogRealiDellUtente()
    {
        var realeCartella = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TestLoom");
        var marcatore = "isolamento-" + Guid.NewGuid().ToString("N");

        // Esecuzione di prova: errore registrato, voce di traccia e cartella locale di un workspace, come farebbe l'app.
        CrashLogService.Registra("Test.Isolamento", new InvalidOperationException(marcatore));
        DiagnosticTraceService.Registra("Test.Isolamento", marcatore);
        var cartellaWorkspace = WorkspaceLocalPaths.GetWorkspaceDirectory(
            Path.Combine(Path.GetTempPath(), marcatore),
            Path.Combine(TestLoomDataRoot.LocalAppDataRoot, "TestLoom", "workspaces"));
        Directory.CreateDirectory(cartellaWorkspace);

        // Il marcatore è finito nei file della radice isolata...
        Assert.Contains(marcatore, LeggiTesto(Path.Combine(CrashLogService.LogDirectory, "crash.log")));
        Assert.Contains(marcatore, LeggiTesto(Path.Combine(TestLoomDataRoot.AppDataRoot, "TestLoom", "trace.log")));
        Assert.StartsWith(TestLoomDataRoot.LocalAppDataRoot, cartellaWorkspace, StringComparison.OrdinalIgnoreCase);

        // ...e MAI in quelli reali. Il controllo è sul marcatore, non sulla dimensione o sulla data del file: resta valido
        // anche se un TestLoom reale scrive nel frattempo sulla stessa macchina.
        Assert.DoesNotContain(marcatore, LeggiTesto(Path.Combine(realeCartella, "crash.log")));
        Assert.DoesNotContain(marcatore, LeggiTesto(Path.Combine(realeCartella, "trace.log")));
    }

    /// <summary>Legge un file di log senza bloccarlo per chi scrive (condivisione in lettura/scrittura); assente = vuoto.</summary>
    private static string LeggiTesto(string percorso)
    {
        if (!File.Exists(percorso)) return "";
        using var stream = new FileStream(percorso, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
