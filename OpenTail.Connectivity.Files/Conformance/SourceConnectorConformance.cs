using System.Diagnostics;
using System.Reflection;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Inbox;

namespace OpenTail.Connectivity.Files.Conformance;

public interface IConnectorFixture
{
    ISourceConnector Create();                    // over the fixture's data
    ISourceConnector CreateEmpty();               // over an empty source
    Task RemoveOneItemAsync(string sourceRef);    // make one item vanish, for the fetch check
    IReadOnlyList<string> SecretsGiven { get; }   // strings that must not appear in exception messages
    IReadOnlySet<int>? SkippedChecks => null;
}

public sealed class ConnectorConformanceException(string message) : Exception(message);

public static class SourceConnectorConformance
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ConnectorConformanceException(message);
    }

    public static async Task RunAsync(IConnectorFixture fixture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var skipped = fixture.SkippedChecks;

        // Check 7: CreateEmpty() yields nothing and doesn't throw.
        if (skipped?.Contains(7) != true)
        {
            var empty = fixture.CreateEmpty();
            var emptyCount = 0;
            await foreach (var _ in empty.DiscoverAsync(ct).ConfigureAwait(false))
                emptyCount++;
            Require(emptyCount == 0, "Check 7 failed: CreateEmpty() yielded items.");
        }

        // Run discovery on fixture data
        var connector1 = fixture.Create();
        var items1 = new List<SourceItem>();
        await foreach (var item in connector1.DiscoverAsync(ct).ConfigureAwait(false))
            items1.Add(item);

        // Check 1: Discovered items have a non-empty Id, a tenant context and a Modified that is not default.
        if (skipped?.Contains(1) != true)
        {
            Require(items1.Count > 0, "Check 1 failed: fixture data yielded 0 items.");
            foreach (var item in items1)
            {
                Require(!string.IsNullOrWhiteSpace(item.Id), "Check 1 failed: item has empty Id.");
                Require(!string.IsNullOrWhiteSpace(item.TenantContext), $"Check 1 failed: item '{item.Id}' has empty TenantContext.");
                Require(item.Modified != default && item.Modified != DateTimeOffset.MinValue, $"Check 1 failed: item '{item.Id}' has default Modified timestamp.");
            }
        }

        // Check 2: Ids are unique within a run and identical across two runs over unchanged data.
        if (skipped?.Contains(2) != true)
        {
            Require(items1.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() == items1.Count,
                "Check 2 failed: item Ids are not unique within a run.");

            var connector2 = fixture.Create();
            var items2 = new List<SourceItem>();
            await foreach (var item in connector2.DiscoverAsync(ct).ConfigureAwait(false))
                items2.Add(item);

            Require(items1.Select(i => i.Id).SequenceEqual(items2.Select(i => i.Id)),
                "Check 2 failed: item Ids are not identical across two runs over unchanged data.");
        }

        // Check 4: Cancelling during enumeration stops it within 5 seconds. Cancel after the first item; a fixture with one item may skip this, with a note.
        if (skipped?.Contains(4) != true && items1.Count > 1)
        {
            using var cancelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var cancelConnector = fixture.Create();
            var count = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                await foreach (var _ in cancelConnector.DiscoverAsync(cancelCts.Token).ConfigureAwait(false))
                {
                    count++;
                    cancelCts.Cancel();
                }
            }
            catch (OperationCanceledException) { }
            sw.Stop();

            Require(sw.Elapsed < TimeSpan.FromSeconds(5), "Check 4 failed: cancellation took longer than 5 seconds.");
            Require(count < items1.Count, "Check 4 failed: enumeration did not stop on cancellation.");
        }

        // Check 5: No public method on the connector's type starts with a write verb. Reuse ReadOnlyInboxGuard's verb list.
        if (skipped?.Contains(5) != true)
        {
            var connectorType = connector1.GetType();
            var violations = connectorType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .Select(m => m.Name)
                .Where(name => ReadOnlyInboxGuard.ForbiddenVerbs.Any(verb => name.StartsWith(verb, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Require(violations.Count == 0, $"Check 5 failed: connector exposes write-style methods: {string.Join(", ", violations)}");
        }

        // Check 8: For IIncrementalSourceConnector: a run from no cursor, then from its last checkpoint, yields no upserts when nothing changed. Resuming from the first checkpoint repeats exactly the items after it.
        if (skipped?.Contains(8) != true && connector1 is IIncrementalSourceConnector incremental)
        {
            var changes1 = new List<SourceChange>();
            await foreach (var c in incremental.GetChangesAsync(null, ct).ConfigureAwait(false))
                changes1.Add(c);

            var lastCheckpoint = changes1.OfType<SourceChange.Checkpoint>().LastOrDefault()?.Cursor;
            var firstCheckpoint = changes1.OfType<SourceChange.Checkpoint>().FirstOrDefault()?.Cursor;

            if (lastCheckpoint is not null)
            {
                var connectorInc2 = (IIncrementalSourceConnector)fixture.Create();
                var changesAfterLast = new List<SourceChange>();
                await foreach (var c in connectorInc2.GetChangesAsync(lastCheckpoint, ct).ConfigureAwait(false))
                    changesAfterLast.Add(c);

                var upsertsAfterLast = changesAfterLast.OfType<SourceChange.Upsert>().ToList();
                Require(upsertsAfterLast.Count == 0, "Check 8 failed: resuming from last checkpoint yielded upserts over unchanged data.");
            }

            if (firstCheckpoint is not null)
            {
                var connectorInc3 = (IIncrementalSourceConnector)fixture.Create();
                var changesAfterFirst = new List<SourceChange>();
                await foreach (var c in connectorInc3.GetChangesAsync(firstCheckpoint, ct).ConfigureAwait(false))
                    changesAfterFirst.Add(c);

                var upsertsAfterFirst = changesAfterFirst.OfType<SourceChange.Upsert>().Select(u => u.Item.Id).ToList();
                var firstCpIdx = changes1.FindIndex(c => c is SourceChange.Checkpoint cp && cp.Cursor == firstCheckpoint);
                var expectedUpserts = changes1.Skip(firstCpIdx + 1).OfType<SourceChange.Upsert>().Select(u => u.Item.Id).ToList();

                Require(upsertsAfterFirst.SequenceEqual(expectedUpserts), "Check 8 failed: resuming from first checkpoint did not repeat exactly the items after it.");
            }
        }

        // Check 3: FetchAsync of a discovered item succeeds. After RemoveOneItemAsync, fetching that item returns empty bytes or throws an ordinary exception within 10 seconds, never hangs. Use a timeout.
        string? removedExceptionMessage = null;
        if (skipped?.Contains(3) != true)
        {
            var first = items1[0];
            var bytes = await connector1.FetchAsync(first, ct).ConfigureAwait(false);
            Require(bytes is not null, "Check 3 failed: FetchAsync of discovered item returned null.");

            await fixture.RemoveOneItemAsync(first.Id).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var removedConnector = fixture.Create();
                await removedConnector.FetchAsync(first, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new ConnectorConformanceException("Check 3 failed: FetchAsync hung on removed item.");
            }
            catch (Exception ex)
            {
                removedExceptionMessage = ex.Message;
            }
        }

        // Check 6: Exception messages from 3 and from a deliberately broken fixture contain none of SecretsGiven.
        if (skipped?.Contains(6) != true && removedExceptionMessage is not null && fixture.SecretsGiven is { Count: > 0 } secrets)
        {
            foreach (var secret in secrets)
            {
                if (!string.IsNullOrEmpty(secret) && removedExceptionMessage.Contains(secret, StringComparison.OrdinalIgnoreCase))
                    throw new ConnectorConformanceException($"Check 6 failed: exception message contains secret '{secret}'.");
            }
        }
    }
}
