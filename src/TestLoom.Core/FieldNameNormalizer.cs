using System.Text;

namespace TestLoom.Core;

/// <summary>
/// Domain-neutral normalization of human-entered labels and configurable field names.
/// Does not depend on any tool, equipment brand, log format or automotive data model.
/// </summary>
public static class FieldNameNormalizer
{
    /// <summary>
    /// Returns invariant uppercase letters and digits, preserving Unicode characters
    /// but ignoring spaces and punctuation. Keep this behavior stable: existing
    /// catalog matching and saved labels rely on the same comparison semantics.
    /// </summary>
    public static string Normalize(string value)
    {
        var uppercase = value.ToUpperInvariant();
        var normalized = new StringBuilder(uppercase.Length);
        foreach (var character in uppercase)
        {
            if (char.IsLetterOrDigit(character))
                normalized.Append(character);
        }

        return normalized.ToString();
    }
}
