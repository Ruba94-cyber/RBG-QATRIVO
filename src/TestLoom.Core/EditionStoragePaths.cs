namespace TestLoom.Core;

/// <summary>
/// Resolves separate local storage namespaces for separately installed
/// editions of a test-management application. No migration or file IO occurs.
/// An edition must opt into its own directory names; the existing private
/// application retains its historical paths unchanged.
/// </summary>
public static class EditionStoragePaths
{
    public static string ConfigDirectory(string appDataRoot, string editionFolder) =>
        Path.Combine(appDataRoot, SafeEditionFolder(editionFolder));

    public static string ConfigFile(string appDataRoot, string editionFolder) =>
        Path.Combine(ConfigDirectory(appDataRoot, editionFolder), "config.json");

    public static string WorkspaceCacheDirectory(string localAppDataRoot, string editionFolder) =>
        Path.Combine(localAppDataRoot, SafeEditionFolder(editionFolder), "workspaces");

    /// <summary>
    /// Only one directory segment is accepted: never permit an edition name to
    /// redirect an installation to another application's storage.
    /// </summary>
    private static string SafeEditionFolder(string editionFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editionFolder);
        if (editionFolder.Length > 64 ||
            editionFolder.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') ||
            IsReservedWindowsName(editionFolder))
            throw new ArgumentException("Edition folder must be a single safe directory name.", nameof(editionFolder));
        return editionFolder;
    }

    private static bool IsReservedWindowsName(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4 && upper[3] is >= '1' and <= '9'
                && (upper.StartsWith("COM", StringComparison.Ordinal)
                    || upper.StartsWith("LPT", StringComparison.Ordinal)));
    }
}
