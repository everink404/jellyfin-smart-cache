using JellyCache.Core;
using JellyCache.ReadProbe;

internal static class ReadLeaseScopeChecks
{
    public static async Task Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var root = Path.GetFullPath(Path.Combine(".tools", "lease-scope-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "a.bin"); var other = Path.Combine(root, "b.bin");
        File.WriteAllBytes(source, new byte[4096]); File.WriteAllBytes(other, new byte[4096]);
        var id = Guid.NewGuid(); var otherId = Guid.NewGuid();
        using var store = new CacheStore(Path.Combine(root, "managed"), new(4096, 0, 1024 * 1024 * 1024));
        await store.CopyAsync(id, source);
        var jobs = new BackgroundReadLeases();
        using var first = new ReadLeaseScope();
        var lease = first.TryAcquire(store, id, source);
        Check(lease is not null && ReferenceEquals(first.TryAcquire(store, id, source), lease) && store.Usage.Leases == 1,
            "repeated request lookup shares one request pin");
        Check(first.TryAcquire(store, id, other) is null && store.Usage.Leases == 1, "conflicting source identity cannot reuse request lease");
        var jobA = new string('a', 1); var jobB = new string('a', 1);
        Check(first.ProtectJob(jobs, jobA, id) && store.Usage.Leases == 2, "background pin acquired before request pin released");
        Check(first.ProtectJob(jobs, jobA, id) && store.Usage.Leases == 2, "repeated association does not duplicate job pin");
        first.Dispose();
        Check(first.Completion.IsCompleted && store.Usage.Leases == 1 && first.TryAcquire(store, id, source) is null,
            "closed request cannot acquire and does not release background pin");
        Check(!first.ProtectJob(jobs, jobB, id), "late background association after request completion refused");
        using (var second = new ReadLeaseScope())
        {
            second.TryAcquire(store, id, source);
            Check(second.ProtectJob(jobs, jobB, id) && store.Usage.Leases == 3, "new job with equal text has independent object identity");
        }
        jobs.ReleaseAfterWork(new string('a', 1));
        Check(store.Usage.Leases == 2, "unrelated equal-valued stop cannot release either job");
        jobs.ReleaseAfterWork(jobA); jobs.ReleaseAfterWork(jobA);
        Check(store.Usage.Leases == 1 && await store.CopyAsync(otherId, other) == CopyOutcome.CapacityBlocked,
            "old job completion is idempotent and cannot release replacement job");
        jobs.ReleaseAfterWork(jobB);
        Check(store.Usage.Leases == 0 && await store.CopyAsync(otherId, other) == CopyOutcome.Published,
            "joined final job permits safe reclaim");
        using (var request = new ReadLeaseScope())
        {
            request.TryAcquire(store, otherId, other);
            var limited = new BackgroundReadLeases(1); var early = new object();
            Check(request.ProtectJob(limited, early, otherId) && !request.ProtectJob(limited, new object(), otherId)
                && store.Usage.Leases == 2, "full background registry refuses new job without dropping protection");
            limited.ReleaseAfterWork(early);
            Check(store.Usage.Leases == 1 && await store.CopyAsync(id, source) == CopyOutcome.CapacityBlocked,
                "job ending before request cannot release request pin");
        }
        Check(store.Usage.Leases == 0, "request finally releases last owner");
        Console.WriteLine($"{count} request/background lease ownership checks passed. Explicit work identity; no actual ffmpeg.");
    }
}
