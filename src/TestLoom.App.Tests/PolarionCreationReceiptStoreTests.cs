using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionCreationReceiptStoreTests
{
    [Fact]
    public void BrowserBeginPersistsCompleteReceiptAndRejectsInvalidIdentityWithoutCreatingOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom-Receipt-" + Guid.NewGuid().ToString("N"));
        var storage = new PolarionCreationReceiptStore(new DataPaths(root));
        var uid = Guid.NewGuid();
        var marker = "TestLoomREF" + new string('E', 32);
        Assert.Throws<ArgumentException>(() => storage.BeginBrowser(false, "NC-4", uid,
            marker, "bug", null, "operator1", Guid.NewGuid(), null));
        Assert.Null(storage.Read(false, "NC-4", uid));
        var receipt = storage.BeginBrowser(false, "NC-4", uid, marker, "bug", null, "operator1");
        var pending = Assert.Single(new PolarionCreationReceiptStore(new DataPaths(root)).PendingBrowserReceipts());
        Assert.Equal(marker, pending.BrowserCorrelationMarker);
        Assert.Equal("bug", pending.ExpectedTicketType);
        Assert.False(storage.PendingBrowserReceipts(out var hasUnreadable).Count == 0);
        Assert.False(hasUnreadable);
        Assert.Throws<InvalidOperationException>(() => storage.BeginBrowser(false, "NC-4", uid,
            marker, "bug", null, "operator1"));
        storage.MarkCreated(receipt, new PolarionWriteService.Created("TICKET-4", "TICKET_Tickets/TICKET-4"));
        Assert.Equal("TICKET-4", receipt.CreatedTicketId);
        Assert.Equal("TICKET-4", storage.Read(false, "NC-4", uid)!.CreatedTicketId);
        storage.Clear(receipt);
    }

    [Fact]
    public void PreventsRepeatPostAfterUncertainOutcomeAndPreservesCreatedId()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom-Receipt-" + Guid.NewGuid().ToString("N"));
        var storage = new PolarionCreationReceiptStore(new DataPaths(root));
        var uid = Guid.NewGuid();
        var first = storage.Begin(false, "NC-01", uid);
        Assert.Throws<InvalidOperationException>(() => storage.Begin(false, "NC-01", uid));
        storage.MarkCreated(first, new PolarionWriteService.Created("TICKET-123",
            "TICKET_Tickets/TICKET-123", "https://example.invalid/TICKET-123"));
        Assert.Equal("TICKET-123", storage.Read(false, "NC-01", uid)!.CreatedTicketId);
        Assert.Throws<InvalidOperationException>(() => storage.Begin(false, "NC-01", uid));
        storage.Clear(first);
        Assert.Null(storage.Read(false, "NC-01", uid));
        var next = storage.Begin(false, "NC-01", uid);
        Assert.NotEqual(first.OperationId, next.OperationId);
        storage.Clear(next);
    }

    [Fact]
    public void BrowserReceiptRetainsMarkerInMemoryAndOriginatingTestAcrossReads()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom-Receipt-" + Guid.NewGuid().ToString("N"));
        var storage = new PolarionCreationReceiptStore(new DataPaths(root));
        var ncUid = Guid.NewGuid();
        var testUid = Guid.NewGuid();
        var receipt = storage.Begin(false, "NC-1", ncUid);
        var marker = "TestLoomREF" + new string('A', 32);

        storage.MarkBrowserPrepared(receipt, marker, "bug", "external.user",
            "operator1", testUid, "TEST-13");

        // This very object is passed straight back to the creation dialog for
        // the Description + TestLoomREF clipboard / browser prefill.
        Assert.Equal(marker, receipt.BrowserCorrelationMarker);
        Assert.Equal(testUid, receipt.OriginTestUid);
        Assert.Equal("TEST-13", receipt.OriginTestId);
        Assert.Equal("bug", receipt.ExpectedTicketType);

        var saved = storage.Read(false, "NC-1", ncUid)!;
        Assert.Equal(marker, saved.BrowserCorrelationMarker);
        Assert.Equal(testUid, saved.OriginTestUid);
        Assert.Equal("TEST-13", saved.OriginTestId);
        Assert.Single(storage.PendingBrowserReceipts());
        storage.Clear(receipt);
    }

    [Fact]
    public void BrowserReceiptRejectsPartialOriginatingTestIdentityBeforeWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom-Receipt-" + Guid.NewGuid().ToString("N"));
        var storage = new PolarionCreationReceiptStore(new DataPaths(root));
        var uid = Guid.NewGuid();
        var receipt = storage.Begin(true, "CR-1", uid);
        Assert.Throws<ArgumentException>(() => storage.MarkBrowserPrepared(receipt,
            "TestLoomREF" + new string('B', 32), "new_request", null,
            "operator1", originTestUid: Guid.NewGuid()));
        Assert.Null(storage.Read(true, "CR-1", uid)!.BrowserCorrelationMarker);
        storage.Clear(receipt);
    }

    [Fact]
    public void VerifiedBrowserReceiptRemainsDiscoverableAfterCreatedMarkerUntilCleared()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom-Receipt-" + Guid.NewGuid().ToString("N"));
        var dataPaths = new DataPaths(root);
        var firstInstance = new PolarionCreationReceiptStore(dataPaths);
        var uid = Guid.NewGuid();
        var receipt = firstInstance.Begin(false, "NC-501", uid);
        firstInstance.MarkBrowserPrepared(receipt, "TestLoomREF" + new string('D', 32),
            "bug", null, "operatorA", Guid.NewGuid(), "TEST-5");

        var created = new PolarionWriteService.Created("TICKET-501", "TICKET_Tickets/TICKET-501");
        firstInstance.MarkCreated(receipt, created);
        var afterRestart = new PolarionCreationReceiptStore(dataPaths);
        var pending = Assert.Single(afterRestart.PendingBrowserReceipts());
        Assert.Equal("TICKET-501", pending.CreatedTicketId);
        Assert.Equal(receipt.BrowserCorrelationMarker, pending.BrowserCorrelationMarker);
        Assert.NotNull(pending.OriginTestUid);

        // Same ID is safe to re-record after an interrupted cleanup, a different
        // ID must never overwrite the durable verification receipt.
        afterRestart.MarkCreated(pending, created);
        Assert.Throws<InvalidDataException>(() =>
            afterRestart.MarkCreated(pending, new PolarionWriteService.Created(
                "TICKET-999", "TICKET_Tickets/TICKET-999")));
        Assert.Equal("TICKET-501", afterRestart.Read(false, "NC-501", uid)?.CreatedTicketId);
        afterRestart.Clear(pending);
        Assert.Empty(afterRestart.PendingBrowserReceipts());
    }

    [Fact]
    public void CorruptReceiptBlocksItsOwnSourceButNotOtherPendingBrowserHandoffs()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom-Receipt-" + Guid.NewGuid().ToString("N"));
        var storage = new PolarionCreationReceiptStore(new DataPaths(root));
        var corruptUid = Guid.NewGuid();
        var goodUid = Guid.NewGuid();
        var corrupt = storage.Begin(false, "NC-911", corruptUid);
        var good = storage.Begin(true, "CR-912", goodUid);
        storage.MarkBrowserPrepared(corrupt, "TestLoomREF" + new string('A', 32), "bug", null, "operatorA");
        storage.MarkBrowserPrepared(good, "TestLoomREF" + new string('B', 32), "new_request", null, "operatorA");

        var dir = Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom",
            "workspaces", WorkspaceLocalPaths.ComputeWorkspaceKey(root), "ticketing-create-receipts");
        var corruptPath = Directory.EnumerateFiles(dir, "*.json").Single(path =>
            File.ReadAllText(path).Contains("NC-911", StringComparison.Ordinal));

        try
        {
            File.WriteAllText(corruptPath, "{invalid-json");
            Assert.Throws<InvalidDataException>(() => storage.Read(false, "NC-911", corruptUid));
            Assert.Throws<InvalidDataException>(() => storage.Begin(false, "NC-911", corruptUid));
            var pending = Assert.Single(storage.PendingBrowserReceipts(out var hasUnreadable));
            Assert.True(hasUnreadable);
            Assert.Equal("CR-912", pending.SourceId);
            Assert.Equal(good.OperationId, pending.OperationId);
        }
        finally
        {
            File.Delete(corruptPath);
            storage.Clear(good);
        }
    }

    [Fact]
    public void BrowserMonitorRotatesBoundedBatchAcrossAllPendingSources()
    {
        var receipts = Enumerable.Range(1, 13)
            .Select(i => new PolarionCreationReceiptStore.Receipt { SourceId = "NC-" + i })
            .ToList();
        var next = 0;
        var visited = new HashSet<string>();
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var batch = PolarionBrowserLinkMonitor.SelectBatch(receipts, ref next, limit: 8);
            Assert.InRange(batch.Count, 1, 8);
            foreach (var item in batch)
                visited.Add(item.SourceId);
        }
        Assert.Equal(13, visited.Count);
        Assert.InRange(next, 0, receipts.Count - 1);
        Assert.Empty(PolarionBrowserLinkMonitor.SelectBatch(
            Array.Empty<PolarionCreationReceiptStore.Receipt>(), ref next));
        Assert.Equal(0, next);
    }

    [Fact]
    public void CrAndNcMarkersAreSeparateAndCannotClearSomeoneElsesReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom-Receipt-" + Guid.NewGuid().ToString("N"));
        var storage = new PolarionCreationReceiptStore(new DataPaths(root));
        var uid = Guid.NewGuid();
        var nc = storage.Begin(false, "REF-01", uid);
        var cr = storage.Begin(true, "REF-01", uid);
        Assert.Throws<IOException>(() => storage.Clear(new PolarionCreationReceiptStore.Receipt
        {
            OperationId = Guid.NewGuid(), IsCr = false, SourceId = "REF-01", SourceUid = uid
        }));
        storage.Clear(nc);
        storage.Clear(cr);
    }
}
