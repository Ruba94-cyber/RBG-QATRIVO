namespace TestLoom.App.UITests.Infrastructure;

/// <summary>Trova TestLoom.exe già compilato invece di ricompilarlo da qui — la suite UI presuppone che
/// `dotnet build` sul progetto principale sia già stato eseguito (stesso presupposto di qualunque smoke test
/// "avvia l'app reale"). L'override <c>TestLoom_EXE_PATH</c> è pensato per CI/agenti dove l'output può vivere in
/// un percorso non standard.</summary>
public static class TestLoomExeLocator
{
    public static string Resolve()
    {
        var overridePath = Environment.GetEnvironmentVariable("TestLoom_EXE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!File.Exists(overridePath))
                throw new FileNotFoundException($"TestLoom_EXE_PATH punta a un file inesistente: {overridePath}");
            return overridePath;
        }

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                "Impossibile risalire alla radice del repository TestLoom da " + AppContext.BaseDirectory);

        var candidates = new[] { "Release", "Debug" }
            .Select(cfg => Path.Combine(repoRoot, "src", "TestLoom.App", "bin", cfg, "net10.0", "win-x64", "TestLoom.exe"))
            .Where(File.Exists)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        if (candidates.Count == 0)
            throw new FileNotFoundException(
                "TestLoom.exe non trovato sotto src/TestLoom.App/bin/{Debug|Release}/net10.0/win-x64/. " +
                "Esegui prima 'dotnet build src/TestLoom.App/TestLoom.App.csproj' (Debug o Release), oppure " +
                "imposta la variabile d'ambiente TestLoom_EXE_PATH con il percorso dell'eseguibile da testare.");

        return candidates[0].FullName;
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
