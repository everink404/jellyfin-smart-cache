using JellyCache.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace JellyCache.ReadProbe;

// Installed only when the known media/encoding service registrations can all be wrapped.
public sealed class ReadLeaseStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<ReadLeaseMiddleware>();
        next(app);
    };
}

public sealed class ReadLeaseMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var previous = context.Features.Get<ReadLeaseScope>();
        using var scope = new ReadLeaseScope();
        context.Features.Set(scope);
        try { await next(context).ConfigureAwait(false); }
        finally { context.Features.Set(previous); }
    }
}

public sealed class ReadLeaseScope : IDisposable
{
    internal static readonly AsyncLocal<ReadLeaseScope?> Operation = new();
    private readonly object gate = new();
    private readonly Dictionary<Guid, (CacheStore Store, string Source, CacheLease Lease)> leases = [];
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool closed;
    public Task Completion => completion.Task;

    internal IReadOnlyList<(Guid Id, CacheLease Lease)> Detach()
    {
        lock (gate)
        {
            var result = leases.Select(pair => (pair.Key, pair.Value.Lease)).ToArray();
            leases.Clear();
            return result;
        }
    }

    public CacheLease? TryAcquire(CacheStore store, Guid id, string source)
    {
        lock (gate)
        {
            if (closed) return null;
            if (leases.TryGetValue(id, out var existing))
                return ReferenceEquals(existing.Store, store) && existing.Source == source ? existing.Lease : null;
            if (leases.Count >= 16) return null;
            var lease = store.TryAcquire(id, source);
            if (lease is not null) leases.Add(id, (store, source, lease));
            return lease;
        }
    }

    // Acquire a separate pin BEFORE starting background work. The request keeps its own
    // pin until unwinding, so either owner can finish first without a protection gap.
    public bool ProtectJob(BackgroundReadLeases jobs, object job, Guid id)
    {
        lock (gate)
        {
            if (closed || !leases.TryGetValue(id, out var existing)) return false;
            return jobs.Protect(job, existing.Store, id, existing.Source);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            try { foreach (var entry in leases.Values) entry.Lease.Dispose(); }
            finally { leases.Clear(); completion.TrySetResult(); }
        }
    }
}

public sealed class BackgroundReadLeases(int maximumJobs = 128)
{
    private readonly object gate = new();
    private readonly Dictionary<object, Dictionary<Guid, (CacheStore Store, string Source, CacheLease Lease)>> jobs
        = new(ReferenceEqualityComparer.Instance);
    internal bool Protect(object job, CacheStore store, Guid id, string source)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(job, out var files))
            {
                if (jobs.Count >= maximumJobs) return false;
                files = [];
            }
            if (files.TryGetValue(id, out var existing))
                return ReferenceEquals(existing.Store, store) && existing.Source == source;
            if (files.Count >= 16) return false;
            var lease = store.TryAcquire(id, source);
            if (lease is null) return false;
            files.Add(id, (store, source, lease));
            jobs[job] = files;
            return true;
        }
    }

    // Caller must have joined this exact work object. A playback ID or stopped HTTP
    // request is not sufficient proof; unknown work remains protected.
    public void ReleaseAfterWork(object job)
    {
        lock (gate)
        {
            if (!jobs.Remove(job, out var files)) return;
            foreach (var entry in files.Values) entry.Lease.Dispose();
        }
    }
}
