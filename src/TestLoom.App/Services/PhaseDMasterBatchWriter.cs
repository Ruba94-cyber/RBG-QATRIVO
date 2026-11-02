namespace TestLoom.App.Services;

/// <summary>
/// Pubblicazione di un batch di mutation Phase D sul journal del device corrente: DeviceSequence dal contatore locale
/// (con floor sul journal condiviso), validazione del batch e Append. La usano il repository engine e la risoluzione dei
/// conflitti dei cataloghi master, cosi' entrambi scrivono nello stesso identico formato.
/// </summary>
internal static class PhaseDMasterBatchWriter
{
    public static void Pubblica(DataPaths paths, string? localBaseDirectory, IReadOnlyList<PhaseDMasterMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(mutations);
        var batch = new PhaseDMasterMutationBatch
        {
            BatchId = Guid.NewGuid(),
            DeviceId = DeviceIdentityService.GetOrCreate(),
            DeviceSequence = PhaseDMasterJournalService.NextDeviceSequence(paths.Root, localBaseDirectory),
            CreatedUtc = DateTime.UtcNow,
            Mutations = mutations.ToList()
        };
        batch.Validate();
        PhaseDMasterJournalService.Append(paths, batch);
    }
}
