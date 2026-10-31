namespace TestLoom.Core;

/// <summary>
/// General-purpose, reversible copy of field values within a configured test
/// operation. The original existence of every dictionary key is preserved.
/// This component has no dependency on diagnostic log import or any industry.
/// </summary>
public static class FieldCopyTransaction
{
    // Keep the existing preview limit used by the test editor.
    public const int PreviewRowLimit = 10;

    /// <summary>
    /// Apply a set of field values and capture their original state for undo.
    /// Duplicate keys are allowed: the first original value is saved and the
    /// last submitted value becomes the expected state after the copy.
    /// </summary>
    public static FieldCopyUndo Apply(
        Dictionary<string, string> destination,
        IEnumerable<KeyValuePair<string, string>> updates)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(updates);

        // Materialize before writing: exceptions from a deferred enumerable must
        // not leave a partially updated destination.
        var changes = updates.ToList();
        foreach (var change in changes)
        {
            ArgumentNullException.ThrowIfNull(change.Key);
            ArgumentNullException.ThrowIfNull(change.Value);
        }

        var snapshots = new Dictionary<string, FieldCopyUndo.Entry>(destination.Comparer);
        foreach (var change in changes)
        {
            if (!snapshots.TryGetValue(change.Key, out var entry))
            {
                bool existed = destination.TryGetValue(change.Key, out var originalValue);
                entry = new FieldCopyUndo.Entry(change.Key, existed, originalValue, change.Value);
            }
            else
            {
                entry = entry with { AppliedValue = change.Value };
            }

            snapshots[change.Key] = entry;
        }

        foreach (var change in changes)
            destination[change.Key] = change.Value;

        return new FieldCopyUndo(destination, snapshots.Values.ToArray());
    }
}

/// <summary>
/// Undo token for a single general-purpose field copy. Changes are rejected
/// if the destination no longer contains the values written by that copy.
/// </summary>
public sealed class FieldCopyUndo
{
    internal sealed record Entry(string Key, bool Existed, string? OriginalValue, string AppliedValue);

    private readonly Dictionary<string, string> _destination;
    private readonly IReadOnlyList<Entry> _entries;

    internal FieldCopyUndo(Dictionary<string, string> destination, IReadOnlyList<Entry> entries)
    {
        _destination = destination;
        _entries = entries;
    }

    public bool IsUnchangedAfterApply => _entries.All(entry =>
        _destination.TryGetValue(entry.Key, out var current) &&
        string.Equals(current, entry.AppliedValue, StringComparison.Ordinal));

    /// <summary>Restore all previous values, or remove originally absent keys.</summary>
    public void Undo()
    {
        if (!IsUnchangedAfterApply)
            throw new InvalidOperationException("Cannot undo a field copy after its values have changed.");

        foreach (var entry in _entries)
        {
            if (entry.Existed)
                _destination[entry.Key] = entry.OriginalValue!;
            else
                _destination.Remove(entry.Key);
        }
    }
}
