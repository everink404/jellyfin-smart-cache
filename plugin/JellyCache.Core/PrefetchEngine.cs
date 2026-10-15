using System.Diagnostics;

namespace JellyCache.Core;

public sealed record QueueItem(Guid Id, int EpisodeNumber, int Distance, string State, int Attempts);
public sealed record AttemptResult(Guid Id, string Outcome, int Failures = 0, string? Reason = null,
    string? ErrorType = null, long ElapsedMilliseconds = 0, DateTimeOffset? RetryAfter = null);

// Host metadata snapshots enter here; file work runs independently of playback callbacks.
public sealed class PrefetchEngine(CacheStore store, TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly SessionDemand demand = new();
    private readonly Dictionary<Guid, (int Count, DateTimeOffset After)> failures = [];
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim serial = new(1, 1);
    private CancellationTokenSource? active;
    private EpisodeInfo? current;
    private bool paused;
    private string? cancellationReason;

    public void Update(string session, EpisodeInfo episode, IReadOnlyList<EpisodeInfo> catalog,
        PrefetchStrategy strategy = PrefetchStrategy.Ahead, int ahead = 2)
    {
        lock (gate)
        {
            try { demand.Replace(session, episode, catalog, strategy, ahead); }
            finally { Reconcile(); }
        }
    }
    public void Stop(string session)
    {
        lock (gate) { demand.Stop(session); Reconcile(); }
    }
    public void SetPaused(bool value)
    {
        lock (gate) { paused = value; if (value && active is not null) { cancellationReason ??= "paused"; active.Cancel(); } }
    }
    private void Reconcile()
    {
        var window = demand.Snapshot();
        var wanted = window.Select(d => d.Episode.Id).ToHashSet();
        foreach (var id in failures.Keys.Where(id => !wanted.Contains(id)).ToArray()) failures.Remove(id);
        if (current is not null && !window.Any(d => d.Episode == current))
        { cancellationReason ??= "demand-changed"; active?.Cancel(); }
    }
    public IReadOnlyList<QueueItem> Snapshot()
    {
        lock (gate)
        {
            return demand.Snapshot().Select(d =>
            {
                var retry = failures.GetValueOrDefault(d.Episode.Id);
                var state = store.IsReady(d.Episode.Id, d.Episode.SourcePath) ? "ready"
                    : current?.Id == d.Episode.Id ? "copying"
                    : retry.Count >= 3 ? "failed"
                    : paused ? "paused" : retry.After > time.GetUtcNow() ? "waiting" : "queued";
                return new QueueItem(d.Episode.Id, d.Episode.Number, d.Distance, state, retry.Count);
            }).ToArray();
        }
    }
    public async Task<AttemptResult?> RunNextAsync(CancellationToken token = default)
    {
        if (!await serial.WaitAsync(0, token).ConfigureAwait(false)) return null;
        CancellationTokenSource? linked = null;
        EpisodeInfo? target = null;
        var watch = Stopwatch.StartNew();
        try
        {
            HashSet<Guid> protectedIds;
            lock (gate)
            {
                if (paused) return null;
                var window = demand.Snapshot();
                protectedIds = window.Select(d => d.Episode.Id).ToHashSet();
                target = demand.Snapshot(advance: true).Select(d => d.Episode).FirstOrDefault(e =>
                {
                    var retry = failures.GetValueOrDefault(e.Id);
                    return retry.Count < 3 && retry.After <= time.GetUtcNow() && !store.IsReady(e.Id, e.SourcePath);
                });
                if (target is null) return null;
                current = target;
                cancellationReason = null;
                active = linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            }
            AttemptResult Report(string outcome, string? reason = null, Exception? error = null)
            {
                lock (gate)
                {
                    var retry = failures.GetValueOrDefault(target.Id);
                    return new(target.Id, outcome, retry.Count, reason, error?.GetType().Name,
                        watch.ElapsedMilliseconds, retry.After == default ? null : retry.After);
                }
            }
            try
            {
                var result = await store.CopyAsync(target.Id, target.SourcePath, linked.Token, protectedIds).ConfigureAwait(false);
                lock (gate)
                {
                    if (result == CopyOutcome.CapacityBlocked)
                        failures[target.Id] = (failures.GetValueOrDefault(target.Id).Count, time.GetUtcNow().AddSeconds(5));
                    else failures.Remove(target.Id);
                }
                return Report(result == CopyOutcome.CapacityBlocked ? "capacity-blocked" : "ready",
                    result == CopyOutcome.CapacityBlocked ? "capacity-or-protection" : null);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            { return Report("cancelled", token.IsCancellationRequested ? "host-stopping" : cancellationReason ?? "cancelled"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                lock (gate)
                {
                    var attempts = failures.GetValueOrDefault(target.Id).Count + 1;
                    failures[target.Id] = (attempts, time.GetUtcNow().AddSeconds(attempts == 1 ? 5 : 30));
                }
                return Report("copy-failed", ex switch
                {
                    CacheCopyException copy => copy.Reason,
                    FileNotFoundException or DirectoryNotFoundException => "source-or-directory-missing",
                    UnauthorizedAccessException => "access-denied",
                    ArgumentException or NotSupportedException => "invalid-source",
                    _ => "io-error"
                }, ex);
            }
        }
        finally
        {
            lock (gate)
            {
                current = null; active = null;
                Reconcile();
            }
            linked?.Dispose(); serial.Release();
        }
    }
}
