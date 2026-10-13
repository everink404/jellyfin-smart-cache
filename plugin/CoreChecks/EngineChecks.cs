using JellyCache.Core;

internal static class EngineChecks
{
    public static async Task Run()
    {
        var checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        var directory = Path.GetFullPath(Path.Combine(".tools", "engine-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var series = Guid.NewGuid(); var season = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 5).Select(n => new EpisodeInfo(Guid.NewGuid(), series, season, 1, n, Path.Combine(directory, $"E{n}.bin"))).ToArray();
        foreach (var e in episodes) File.WriteAllBytes(e.SourcePath, Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray());
        var clock = new TestClock();
        using (var store = new CacheStore(Path.Combine(directory, "managed"), new(16384, 0, 1024 * 1024 * 1024)))
        {
            var engine = new PrefetchEngine(store, clock);
            engine.Update("viewer", episodes[0], episodes);
            Check(engine.Snapshot().Select(e => e.EpisodeNumber).SequenceEqual([2, 3]), "E01 creates E02/E03");
            Check((await engine.RunNextAsync())?.Id == episodes[1].Id, "E02 copied first");
            Check((await engine.RunNextAsync())?.Id == episodes[2].Id, "E03 copied second");
            Check(engine.Snapshot().All(e => e.State == "ready"), "window fully ready");
            Check(await engine.RunNextAsync() is null, "ready window does not copy again");
            var lease = store.TryAcquire(episodes[1].Id, episodes[1].SourcePath)!;
            engine.Update("viewer", episodes[1], episodes);
            Check((await engine.RunNextAsync())?.Outcome == "capacity-blocked", "playing E02 and desired E03 protect against E04 eviction");
            Check(store.IsReady(episodes[2].Id, episodes[2].SourcePath), "capacity block does not thrash desired E03");
            lease.Dispose(); clock.Advance(TimeSpan.FromSeconds(6));
            Check((await engine.RunNextAsync())?.Id == episodes[3].Id && store.IsReady(episodes[3].Id, episodes[3].SourcePath), "released old item can be reclaimed");
            Check(!store.IsReady(episodes[1].Id, episodes[1].SourcePath), "LRU older non-window item removed");
            engine.SetPaused(true); engine.Update("viewer", episodes[3], episodes);
            Check(await engine.RunNextAsync() is null && engine.Snapshot().Single().State == "paused", "pause blocks new work");
            engine.SetPaused(false); engine.Stop("viewer");
            Check(engine.Snapshot().Count == 0 && await engine.RunNextAsync() is null, "stop clears pending work");
        }
        var missing = episodes.ToArray(); missing[1] = missing[1] with { SourcePath = Path.Combine(directory, "missing") };
        using (var store = new CacheStore(Path.Combine(directory, "retry"), new(16384, 0)))
        {
            var engine = new PrefetchEngine(store, clock);
            engine.Update("viewer", missing[0], missing, PrefetchStrategy.Next);
            Check((await engine.RunNextAsync())?.Outcome == "copy-failed", "copy failure isolated");
            Check(await engine.RunNextAsync() is null, "retry is backed off");
            clock.Advance(TimeSpan.FromSeconds(6)); await engine.RunNextAsync();
            clock.Advance(TimeSpan.FromSeconds(31)); await engine.RunNextAsync();
            Check(engine.Snapshot().Single().Attempts == 3 && engine.Snapshot().Single().State == "failed", "retry bounded to three");
            engine.Update("viewer", missing[0], missing, PrefetchStrategy.Next);
            Check(await engine.RunNextAsync() is null, "progress does not reset retry limit");
            engine.Stop("viewer"); engine.Update("viewer", missing[0], missing, PrefetchStrategy.Next);
            Check((await engine.RunNextAsync())?.Outcome == "copy-failed" && engine.Snapshot().Single().Attempts == 1, "new demand can retry after stop");
        }
        using (var store = new CacheStore(Path.Combine(directory, "cancel"), new(16384, 0, 8192)))
        {
            var engine = new PrefetchEngine(store);
            engine.Update("viewer", episodes[0], episodes);
            var copying = engine.RunNextAsync();
            await Task.Delay(100); engine.SetPaused(true);
            Check((await copying)?.Outcome == "cancelled" && store.Usage.ReservedBytes == 0, "pause cancels and cleans active copy");
            engine.SetPaused(false); copying = engine.RunNextAsync();
            await Task.Delay(100); engine.Update("viewer", episodes[3], episodes);
            Check((await copying)?.Outcome == "cancelled", "jump cancels obsolete copy");
        }
        var inbox = new PlaybackInbox(clock);
        Check(inbox.Capture("viewer", "A", episodes[0].Id, null, "start"), "capture playback start");
        Check(inbox.Capture("viewer", "B", episodes[1].Id, null, "start"), "new start replaces old playback");
        Check(!inbox.Capture("viewer", "A", episodes[0].Id, null, "stop"), "late stop refused");
        Check(!inbox.Capture("viewer", "A", episodes[0].Id, null, "progress"), "late progress refused");
        Check(inbox.Drain().Single().Playback == "B", "latest event coalesced");
        Check(inbox.Capture("viewer", "B", episodes[1].Id, null, "stop") && inbox.Drain().Single().Stopped, "matching stop accepted");
        Check(!inbox.Capture("viewer", "B", episodes[1].Id, null, "progress"), "stopped playback cannot resurrect from progress");
        inbox.Capture("viewer", "C", episodes[2].Id, null, "start"); clock.Advance(TimeSpan.FromMinutes(16));
        Check(inbox.Expire(TimeSpan.FromMinutes(15)).SequenceEqual(["viewer"]) && inbox.Drain().Length == 0, "expired session and stale pending cleared");
        for (var i = 0; i < 128; i++) inbox.Capture($"v{i}", "A", episodes[0].Id, null, "start");
        Check(!inbox.Capture("overflow", "A", episodes[0].Id, null, "start"), "inbox bounded");
        inbox.Drain(); inbox.Capture("v0", "A", episodes[0].Id, null, "stop"); inbox.Drain();
        Check(inbox.Capture("replacement", "A", episodes[0].Id, null, "start"), "stopped session frees inbox capacity");
        Console.WriteLine($"{checks} prefetch engine/event checks passed.");
    }
}
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan amount) => now += amount;
}
