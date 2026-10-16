using System.Diagnostics;
using System.Text.Json.Nodes;
using JellyCache.Core;

internal static class StorageChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        void Reject<T>(Action action, string name) where T : Exception
        {
            try { action(); } catch (T) { count++; return; }
            throw new Exception(name);
        }
        async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
        {
            try { await action(); } catch (T) { count++; return; }
            throw new Exception(name);
        }
        var testRoot = Path.GetFullPath(Path.Combine(".tools", "cache-store-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        const int size = 32 * 1024;
        var bytes = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        string Source(string name)
        {
            var path = Path.Combine(testRoot, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        string Root(string name) => Path.Combine(testRoot, name);
        var a = Source("source-a"); var b = Source("source-b"); var c = Source("source-c");
        var aid = Guid.NewGuid(); var bid = Guid.NewGuid(); var cid = Guid.NewGuid();
        var fast = new CacheLimits(size * 2, 0, 1024 * 1024 * 1024);
        Reject<ArgumentException>(() => new CacheStore(Root("bad-limits"), fast with { CapacityBytes = 0 }), "capacity must be positive");
        var foreign = Root("foreign"); Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "untouched");
        Reject<IOException>(() => new CacheStore(foreign, fast), "unmanaged root refused");
        Check(File.ReadAllText(Path.Combine(foreign, "keep.txt")) == "untouched", "foreign data preserved");
        using (var store = new CacheStore(Root("main"), fast))
        {
            Reject<IOException>(() => new CacheStore(Root("main"), fast), "single owner lock");
            Check(await store.CopyAsync(aid, a) == CopyOutcome.Published, "publish complete copy");
            Check(await store.CopyAsync(aid, a) == CopyOutcome.AlreadyPresent, "duplicate copy omitted");
            Check(store.TryAcquire(aid, b) is null, "source identity mismatch cannot hit");
            var one = store.TryAcquire(aid, a)!;
            var two = store.TryAcquire(aid, a)!;
            one.Dispose(); one.Dispose();
            Check(store.Usage.Leases == 1, "same file leases are counted and disposal is idempotent");
            two.Dispose();
            Check(store.Usage.Leases == 0, "last lease releases pin");
            using (var lease = store.TryAcquire(aid, a)!)
            {
                Check(lease is not null, "complete item selectable");
                var read = new byte[128];
                lease!.Stream.Seek(1234, SeekOrigin.Begin);
                Check(await lease.Stream.ReadAsync(read) == read.Length && read.SequenceEqual(bytes.Skip(1234).Take(128)), "leased random read");
                Reject<InvalidOperationException>(store.Dispose, "cannot close pinned store");
                Check(await store.CopyAsync(bid, b) == CopyOutcome.Published, "second copy fits");
                using var second = store.TryAcquire(bid, b)!;
                Check(await store.CopyAsync(cid, c) == CopyOutcome.CapacityBlocked, "all pinned blocks eviction");
                Check(store.Usage.CompleteBytes == size * 2 && store.Usage.ReservedBytes == 0, "blocked copy keeps budget");
            }
            using (var touch = store.TryAcquire(aid, a)!) { }
            Check(await store.CopyAsync(cid, c) == CopyOutcome.Published, "unpinned LRU reclaim");
            Check(store.TryAcquire(bid, b) is null, "least recently used removed");
            using (var survivor = store.TryAcquire(aid, a)) Check(survivor is not null, "recently read retained");
            Check(File.ReadAllBytes(b).SequenceEqual(bytes), "source untouched by eviction");
            Reject<ArgumentException>(() => store.CopyAsync(Guid.NewGuid(), Path.Combine(Root("main"), "data")).GetAwaiter().GetResult(), "source inside cache refused");
        }
        using (var reopened = new CacheStore(Root("main"), fast))
        {
            using var lease = reopened.TryAcquire(aid, a);
            Check(lease is not null && reopened.Usage.CompleteFiles == 2, "cold restart verifies index and bytes");
        }
        var corruptFile = Path.Combine(Root("main"), aid.ToString("N") + ".media");
        var originalTime = File.GetLastWriteTimeUtc(corruptFile);
        var changed = bytes.ToArray(); changed[10] ^= 0xff;
        File.WriteAllBytes(corruptFile, changed);
        File.SetLastWriteTimeUtc(corruptFile, originalTime);
        using (var reopened = new CacheStore(Root("main"), fast))
            Check(reopened.TryAcquire(aid, a) is null && reopened.Usage.CompleteFiles == 1, "cold hash detects corruption preserving size and timestamp");

        using (var store = new CacheStore(Root("source-change"), fast))
        {
            await store.CopyAsync(aid, a);
            File.SetLastWriteTimeUtc(a, File.GetLastWriteTimeUtc(a).AddMinutes(1));
            Check(store.TryAcquire(aid, a) is null, "source version change prevents hit");
        }
        using (var store = new CacheStore(Root("source-outage"), fast)) await store.CopyAsync(aid, a);
        File.Move(a, a + ".offline");
        try
        {
            using var store = new CacheStore(Root("source-outage"), fast);
            Check(store.Usage.CompleteFiles == 1 && store.TryAcquire(aid, a) is null, "source outage preserves verified cache without allowing hit");
        }
        finally { File.Move(a + ".offline", a); }
        using (var store = new CacheStore(Root("source-outage"), fast))
        using (var lease = store.TryAcquire(aid, a)) Check(lease is not null, "restored source can reuse retained cache");
        using (var store = new CacheStore(Root("small"), fast with { CapacityBytes = size - 1 }))
            Check(await store.CopyAsync(aid, a) == CopyOutcome.CapacityBlocked && store.Usage.CompleteFiles == 0, "oversize never copied");
        using (var store = new CacheStore(Root("space"), fast with { MinimumFreeBytes = 100 }, () => size + 99))
            Check(await store.CopyAsync(aid, a) == CopyOutcome.CapacityBlocked, "disk safety reserve checked before copy");
        var spaceCalls = 0;
        using (var store = new CacheStore(Root("shrinking-disk"), fast, () => ++spaceCalls == 1 ? long.MaxValue : 0))
        {
            await RejectAsync<IOException>(async () => await store.CopyAsync(aid, a), "disk margin during copy");
            Check(store.Usage.ReservedBytes == 0 && !Directory.EnumerateFiles(Root("shrinking-disk"), "*.part").Any(), "disk error releases pending bytes");
        }
        using (var store = new CacheStore(Root("cancel"), fast with { BytesPerSecond = size }))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await RejectAsync<OperationCanceledException>(async () => await store.CopyAsync(aid, a, cancel.Token), "cancel partial copy");
            Check(store.Usage.ReservedBytes == 0 && store.TryAcquire(aid, a) is null
                && !Directory.EnumerateFiles(Root("cancel"), "*.part").Any(), "cancel cleans owned temporary and never publishes");
        }
        using (var store = new CacheStore(Root("serial"), fast with { BytesPerSecond = size * 2 }))
        {
            var watch = Stopwatch.StartNew();
            var first = store.CopyAsync(aid, a);
            var duplicate = store.CopyAsync(aid, a);
            await Task.Delay(75);
            Check(store.Usage.ReservedBytes == size && store.Usage.CompleteBytes == 0, "reservation accounts for in-progress data");
            Check(store.TryAcquire(aid, a) is null, "incomplete copy unavailable");
            Check(await first == CopyOutcome.Published && await duplicate == CopyOutcome.AlreadyPresent, "concurrent duplicate serialized");
            Check(watch.Elapsed.TotalSeconds >= 0.45, "average copy rate bounded");
        }
        using (var store = new CacheStore(Root("changed-during-copy"), fast with { BytesPerSecond = size * 2 }))
        {
            var task = store.CopyAsync(aid, a);
            await Task.Delay(100);
            File.SetLastWriteTimeUtc(a, File.GetLastWriteTimeUtc(a).AddMinutes(1));
            await RejectAsync<IOException>(async () => await task, "source change during copy refuses publication");
            Check(store.Usage.ReservedBytes == 0 && store.Usage.CompleteFiles == 0, "source change clears pending reservation");
        }
        var reduced = Root("reduced");
        using (var store = new CacheStore(reduced, fast))
        { await store.CopyAsync(aid, a); await store.CopyAsync(bid, b); }
        using (var store = new CacheStore(reduced, fast with { CapacityBytes = size }))
            Check(store.Usage.CompleteBytes == size && store.TryAcquire(aid, a) is null, "restart enforces reduced quota with LRU");
        var recovery = Root("recovery");
        using (var store = new CacheStore(recovery, fast)) await store.CopyAsync(aid, a);
        var indexPath = Path.Combine(recovery, "index.json");
        var index = JsonNode.Parse(File.ReadAllText(indexPath))!.AsArray();
        index[0]!["Complete"] = false;
        File.WriteAllText(indexPath, index.ToJsonString());
        File.Move(Path.Combine(recovery, aid.ToString("N") + ".media"), Path.Combine(recovery, aid.ToString("N") + ".part"));
        using (var store = new CacheStore(recovery, fast))
            Check(store.Usage.CompleteFiles == 0 && !Directory.EnumerateFiles(recovery, "*.part").Any(), "restart cleans journal-owned incomplete copy");
        using (var store = new CacheStore(recovery, fast)) await store.CopyAsync(aid, a);
        index = JsonNode.Parse(File.ReadAllText(indexPath))!.AsArray();
        index[0]!["Complete"] = false;
        File.WriteAllText(indexPath, index.ToJsonString());
        using (var store = new CacheStore(recovery, fast))
            Check(store.Usage.CompleteFiles == 0 && !Directory.EnumerateFiles(recovery, "*.media").Any(), "restart cleans move-before-index-commit crash");
        using (var store = new CacheStore(recovery, fast)) await store.CopyAsync(aid, a);
        File.WriteAllText(Path.Combine(recovery, "unrecognized.dat"), "preserve");
        Reject<IOException>(() => new CacheStore(recovery, fast), "unexpected files block startup cleanup");
        Check(File.ReadAllText(Path.Combine(recovery, "unrecognized.dat")) == "preserve", "unknown recovery file preserved");
        File.WriteAllText(Path.Combine(recovery, "index.json"), "not-json");
        Reject<System.Text.Json.JsonException>(() => new CacheStore(recovery, fast), "broken index not silently adopted");
        Check(File.Exists(Path.Combine(recovery, aid.ToString("N") + ".media")), "broken index preserves existing media");
        var clearRoot = Root("clear");
        using (var store = new CacheStore(clearRoot, fast))
        {
            await store.CopyAsync(aid, a); await store.CopyAsync(bid, b);
            using (var active = store.TryAcquire(aid, a)!)
            {
                var result = store.ClearUnoccupied();
                Check(result.State == "completed" && result.DeletedFiles == 1 && result.DeletedBytes == size
                    && result.SkippedProtected == 1, "cleanup deletes only unoccupied indexed entries");
                Check(store.Usage.CompleteFiles == 1 && store.Usage.Leases == 1, "cleanup preserves active lease accounting");
                Check(active.Stream.ReadByte() == bytes[0], "active reader remains readable after cleanup");
                Check(File.ReadAllBytes(a).SequenceEqual(bytes) && File.ReadAllBytes(b).SequenceEqual(bytes), "cleanup never touches source files");
            }
            Check(store.ClearUnoccupied().DeletedFiles == 1 && store.Usage.CompleteFiles == 0, "released item can be cleared without restart");
            Check(store.ClearUnoccupied().DeletedFiles == 0, "repeated empty cleanup is harmless");
            await store.CopyAsync(aid, a);
            File.WriteAllText(Path.Combine(clearRoot, "unknown.keep"), "preserve");
            var refused = store.ClearUnoccupied();
            Check(refused.State == "failed" && refused.DeletedFiles == 0 && store.IsReady(aid, a), "foreign file refuses cleanup before deleting owned data");
            Check(File.ReadAllText(Path.Combine(clearRoot, "unknown.keep")) == "preserve", "cleanup preserves unknown files");
            File.Delete(Path.Combine(clearRoot, "unknown.keep"));
        }
        using (var reopened = new CacheStore(clearRoot, fast))
            Check(reopened.IsReady(aid, a) && !reopened.IsReady(bid, b), "cleanup journal persists across restart");
        using (var store = new CacheStore(Root("clear-copy"), fast with { BytesPerSecond = size }))
        {
            using var cancel = new CancellationTokenSource();
            var task = store.CopyAsync(aid, a, cancel.Token);
            await Task.Delay(75);
            Check(store.ClearUnoccupied().State == "busy" && store.Usage.ReservedBytes == size, "cleanup refuses in-flight copy and preserves reservation");
            cancel.Cancel();
            await RejectAsync<OperationCanceledException>(async () => await task, "copy can still cancel after busy cleanup");
        }
        Console.WriteLine($"{count} cache storage checks passed. Fixtures retained under {testRoot}");
        return count;
    }
}
