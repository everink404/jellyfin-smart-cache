using System.Text.Json;
using JellyCache.Core;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;

namespace JellyCache.ReadProbe;

// The media router resolves this dependency-free accessor, never the hosted service.
// SessionManager itself needs IMediaSourceManager, so eager host injection would cycle.
public sealed class PrefetchAccess
{
    private PrefetchHost? host;
    public PrefetchHost? Host { get => Volatile.Read(ref host); set => Volatile.Write(ref host, value); }
}

public sealed record StorageSettings(string MediaRoot, string CacheRoot, long Capacity, long MinimumFree, long Rate);
public sealed record PrefetchSettings(bool Enabled, bool Automatic, bool Paused, StorageSettings Storage, string Strategy, int Ahead, int Revision = 0)
{
    public static PrefetchSettings Current()
    {
        var c = Plugin.Instance?.Configuration ?? new ProbeConfiguration();
        return new(c.Enabled, c.AutomaticPrefetch, c.PausePrefetch,
            new(c.MediaRoot, c.CacheDirectory, c.CapacityBytes, c.MinimumFreeBytes, c.BytesPerSecond), c.Strategy, c.Ahead, c.ValidationRevision);
    }
}
public sealed record PrefetchStatus(string State, string? LastError, CacheUsage? Usage,
    IReadOnlyList<QueueItem> Queue, int RetainedSelections, long CacheSelections, long OriginSelections,
    AttemptResult? LastAttempt = null, IReadOnlyList<ProtectionObservation>? Protections = null, bool LifecycleRouting = false);

public sealed class PrefetchHost : BackgroundService
{
    private readonly ISessionManager sessions;
    private readonly ILibraryManager library;
    private readonly RegistrationResult registration;
    private readonly ILogger<PrefetchHost> logger;
    private readonly Func<PrefetchSettings> settings;
    private readonly object gate = new();
    private readonly PlaybackInbox inbox = new();
    private readonly Dictionary<Guid, CacheLease> retained = [];
    private readonly Dictionary<Guid, ProtectionObservation> protections = [];
    private readonly Dictionary<string, (Guid Id, string Source)> selectedPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlaybackEvent> viewers = [];
    private CacheStore? store;
    private PrefetchEngine? engine;
    private StorageSettings? opened;
    private string state = "disabled";
    private string? lastError;
    private long hits, origins;
    private AttemptResult? lastAttempt;
    private readonly SemaphoreSlim maintenance = new(1, 1);

    public async Task<CacheClearResult> ClearUnoccupiedAsync()
    {
        if (!await maintenance.WaitAsync(0).ConfigureAwait(false)) return new("busy");
        try
        {
            CacheStore currentStore;
            lock (gate)
            {
                var config = settings();
                if (!config.Paused || state != "paused") return new("pause-required");
                if (store is null || opened != config.Storage) return new("unavailable");
                currentStore = store;
            }
            var result = await Task.Run(currentStore.ClearUnoccupied).ConfigureAwait(false);
            logger.LogInformation("JellyCache cache clear: {State}; files={Files}; bytes={Bytes}; protected={Protected}; error={ErrorType}",
                result.State, result.DeletedFiles, result.DeletedBytes, result.SkippedProtected, result.ErrorType);
            return result;
        }
        finally { maintenance.Release(); }
    }

    public PrefetchHost(ISessionManager sessions, ILibraryManager library, RegistrationResult registration,
        ILogger<PrefetchHost> logger, PrefetchAccess access)
        : this(sessions, library, registration, logger, PrefetchSettings.Current) { access.Host = this; }
    public PrefetchHost(ISessionManager sessions, ILibraryManager library, RegistrationResult registration,
        ILogger<PrefetchHost> logger, Func<PrefetchSettings> settings)
    { this.sessions = sessions; this.library = library; this.registration = registration; this.logger = logger; this.settings = settings; }
    public bool AutomaticMode => settings().Automatic;
    private void Capture(PlaybackProgressEventArgs args, string phase)
    {
        var config = settings();
        if (!config.Enabled || !config.Automatic || args.Session is null || args.Item is null) return;
        var playback = string.IsNullOrEmpty(args.PlaySessionId) ? args.Item.Id.ToString("N") : args.PlaySessionId;
        inbox.Capture(args.Session.Id, playback, args.Item is Episode ? args.Item.Id : Guid.Empty, args.MediaSourceId, phase);
    }
    private void Start(object? sender, PlaybackProgressEventArgs args) => Capture(args, "start");
    private void Progress(object? sender, PlaybackProgressEventArgs args) => Capture(args, "progress");
    private void Stop(object? sender, PlaybackStopEventArgs args) => Capture(args, "stop");

    private void SetState(string value)
    {
        lock (gate)
        {
            if (state == value) return;
            state = value;
        }
        logger.LogInformation("JellyCache prefetch state: {State}", value);
    }
    public PrefetchStatus Snapshot()
    {
        lock (gate)
        {
            return new(state, lastError, store?.Usage, engine?.Snapshot() ?? [], retained.Count, hits, origins, lastAttempt,
                protections.Values.Take(50).ToArray(), registration.LifecycleSupported);
        }
    }

    private void RecordAttempt(AttemptResult? result)
    {
        if (result is null) return;
        lock (gate) lastAttempt = result;
        logger.LogInformation("JellyCache prefetch copy: {Outcome}; item={Item}; reason={Reason}; error={ErrorType}; failures={Failures}; elapsedMs={ElapsedMilliseconds}; retryAfter={RetryAfter}",
            result.Outcome, result.Id, result.Reason, result.ErrorType, result.Failures, result.ElapsedMilliseconds, result.RetryAfter);
    }

    public IReadOnlyList<MediaSourceInfo> Select(BaseItem item, IReadOnlyList<MediaSourceInfo> sources, string stage, HttpContext? request = null)
    {
        var config = settings();
        if (!config.Enabled || !config.Automatic || item is not Episode || sources.Count != 1
            || sources[0].Protocol != MediaProtocol.File || sources[0].Path != item.Path) return sources;
        try
        {
            MediaSourceInfo selected;
            lock (gate)
            {
                if (registration.LifecycleSupported && (stage == "negotiation" || request?.Request.RouteValues["controller"]?.ToString() == "MediaInfo"))
                    return Origin(sources, stage); // Actual file requests resolve again; do not pin speculative candidates.
                if (state is not ("ready" or "paused") || store is null || opened != config.Storage || !AllowedPath(item.Path, opened.MediaRoot))
                    return Origin(sources, stage);
                if (!store.IsReady(item.Id, item.Path)) return Origin(sources, stage);
                selected = JsonSerializer.Deserialize<MediaSourceInfo>(JsonSerializer.Serialize(sources[0], JsonDefaults.Options), JsonDefaults.Options)!;
                var scope = registration.LifecycleSupported ? ReadLeaseScope.Operation.Value ?? request?.Features.Get<ReadLeaseScope>() : null;
                CacheLease? lease;
                if (scope is not null) lease = scope.TryAcquire(store, item.Id, item.Path);
                else if (!retained.TryGetValue(item.Id, out lease))
                {
                    if (retained.Count >= 1000) return Origin(sources, stage);
                    lease = store.TryAcquire(item.Id, item.Path);
                    if (lease is null) return Origin(sources, stage);
                    retained.Add(item.Id, lease);
                }
                if (lease is null) return Origin(sources, stage);
                if (selectedPaths.Count >= 4096 && !selectedPaths.ContainsKey(lease.Path)) return Origin(sources, stage);
                selectedPaths[lease.Path] = (item.Id, item.Path);
                selected.Path = lease.Path;
                selected.EncoderPath = null; selected.EncoderProtocol = null;
                hits++;
            }
            // Never resolve host services or inspect a job while holding the cache state lock.
            var observation = ReadObservation.Capture(request, selected.Path);
            lock (gate)
            {
                if (retained.ContainsKey(item.Id))
                {
                    var previous = protections.GetValueOrDefault(item.Id);
                    protections[item.Id] = new(item.Id, previous?.FirstStage ?? stage, stage,
                        (previous?.Selections ?? 0) + 1, observation);
                }
            }
            logger.LogInformation("JellyCache prefetch selection: cache; stage={Stage}; item={Item}; reader={Reader}; job={Job}; range={Range}; playbackIdPresent={PlaybackIdPresent}",
                stage, item.Id, observation.Kind, observation.JobState, observation.RangeRequested, observation.HasPlaybackId);
            return [selected];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or ObjectDisposedException)
        { lock (gate) return Origin(sources, stage); }
    }

    internal void PinPath(ReadLeaseScope scope, string? path)
    {
        if (path is null) return;
        lock (gate)
        {
            if (store is not null && opened is not null && AllowedPath(path, opened.CacheRoot))
            {
                if (!selectedPaths.TryGetValue(path, out var known) || scope.TryAcquire(store, known.Id, known.Source) is null)
                    throw new IOException("Selected cache cannot be protected for background reading.");
            }
        }
    }

    internal bool IsSelectedCache(string? path)
    {
        lock (gate) return path is not null && selectedPaths.ContainsKey(path);
    }

    internal void RetainUnknown(ReadLeaseScope scope)
    {
        var entries = scope.Detach();
        lock (gate)
        {
            foreach (var entry in entries)
            {
                if (!retained.TryAdd(entry.Id, entry.Lease)) entry.Lease.Dispose();
                else logger.LogWarning("JellyCache lifecycle retained: completion unconfirmed; item={Item}", entry.Id);
            }
        }
        scope.Dispose();
    }

    internal void FinishedOperation()
    {
        lock (gate)
        {
            if (state == "stopped" && store is not null && store.Usage.Leases == 0)
            { store.Dispose(); store = null; engine = null; }
        }
    }
    private IReadOnlyList<MediaSourceInfo> Origin(IReadOnlyList<MediaSourceInfo> sources, string stage)
    {
        origins++;
        logger.LogInformation("JellyCache prefetch selection: origin; stage={Stage}", stage);
        return sources;
    }

    public static bool AllowedPath(string path, string root)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(root)) return false;
        return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    public static EpisodeInfo Describe(Episode e, string root)
    {
        if (e.IsMissingEpisode || e.MediaSourceCount != 1 || e.IsStacked || e.IsPlaceHolder || !e.IsFileProtocol
            || e.IndexNumber is null or <= 0 || e.ParentIndexNumber is null or <= 0
            || (e.IndexNumberEnd.HasValue && e.IndexNumberEnd != e.IndexNumber) || !AllowedPath(e.Path, root))
            throw new ArgumentException("Unsupported episode metadata.");
        return new(e.Id, e.SeriesId, e.SeasonId, e.ParentIndexNumber.Value, e.IndexNumber.Value, Path.GetFullPath(e.Path), e.IndexNumberEnd);
    }
    private void Process(PlaybackEvent ev, PrefetchSettings config)
    {
        if (engine is null) return;
        if (ev.Stopped || ev.Item == Guid.Empty) { viewers.Remove(ev.Session); engine.Stop(ev.Session); return; }
        try
        {
            if (library.GetItemById(ev.Item) is not Episode current || library.GetItemById(current.SeasonId) is not Season season)
                throw new ArgumentException("No season metadata.");
            if (!string.IsNullOrEmpty(ev.SourceId) && (!Guid.TryParse(ev.SourceId, out var sourceId) || sourceId != current.Id))
                throw new ArgumentException("Unresolved media version.");
            var catalog = season.GetEpisodes().OfType<Episode>()
                .Where(e => !e.IsMissingEpisode && e.SeriesId == current.SeriesId && e.SeasonId == current.SeasonId).ToArray();
            if (catalog.Length is < 1 or > 1000) throw new ArgumentException("Invalid season size.");
            var strategy = ParseStrategy(config.Strategy);
            engine.Update(ev.Session, Describe(current, config.Storage.MediaRoot), catalog.Select(e => Describe(e, config.Storage.MediaRoot)).ToArray(), strategy, config.Ahead);
            viewers[ev.Session] = ev;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            viewers.Remove(ev.Session); engine.Stop(ev.Session);
            lock (gate) lastError = "unsupported-episode";
            logger.LogWarning("JellyCache prefetch metadata rejected: {Kind}", ex.GetType().Name);
        }
    }
    public static PrefetchStrategy ParseStrategy(string value) => value switch
    {
        "next" => PrefetchStrategy.Next, "ahead" => PrefetchStrategy.Ahead, "season" => PrefetchStrategy.Season,
        _ => throw new ArgumentException("Invalid strategy.")
    };

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await Task.Yield();
        sessions.PlaybackStart += Start; sessions.PlaybackProgress += Progress; sessions.PlaybackStopped += Stop;
        Task<AttemptResult?>? copying = null;
        PrefetchSettings? previous = null;
        PrefetchSettings? failedConfiguration = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (copying?.IsCompleted == true)
                {
                    RecordAttempt(await copying.ConfigureAwait(false));
                    copying = null;
                }
                var config = settings();
                var enabled = config.Enabled && config.Automatic && registration.Supported;
                if (!enabled)
                {
                    engine?.SetPaused(true);
                    foreach (var session in viewers.Keys.ToArray()) engine?.Stop(session);
                    viewers.Clear(); inbox.Clear();
                    SetState(registration.Supported ? "disabled" : "unsupported-registration");
                }
                else if (config != failedConfiguration)
                {
                    try
                    {
                        _ = ParseStrategy(config.Strategy);
                        if (config.Ahead is < 1 or > 50 || config.Storage.Capacity <= 0 || config.Storage.MinimumFree < 0 || config.Storage.Rate <= 0
                            || !Path.IsPathFullyQualified(config.Storage.MediaRoot) || !Path.IsPathFullyQualified(config.Storage.CacheRoot)
                            || Path.GetFullPath(config.Storage.MediaRoot) == Path.GetFullPath(config.Storage.CacheRoot)
                            || AllowedPath(config.Storage.CacheRoot, config.Storage.MediaRoot) || AllowedPath(config.Storage.MediaRoot, config.Storage.CacheRoot))
                            throw new ArgumentException("Invalid cache configuration.");
                        if (opened is not null && opened != config.Storage)
                        { engine?.SetPaused(true); SetState("restart-required"); }
                        else
                        {
                            if (store is null)
                            {
                                SetState("initializing");
                                var created = new CacheStore(config.Storage.CacheRoot,
                                    new(config.Storage.Capacity, config.Storage.MinimumFree, config.Storage.Rate));
                                lock (gate) { store = created; opened = config.Storage; engine = new(created); }
                            }
                            engine!.SetPaused(config.Paused);
                            SetState(config.Paused ? "paused" : "ready");
                            foreach (var session in inbox.Expire(TimeSpan.FromMinutes(15)))
                            { viewers.Remove(session); engine.Stop(session); }
                            if (previous?.Strategy != config.Strategy || previous?.Ahead != config.Ahead)
                                foreach (var ev in viewers.Values.ToArray()) Process(ev, config);
                            foreach (var ev in inbox.Drain()) Process(ev, config);
                            if (copying is null && !config.Paused)
                                copying = Task.Run(() => engine!.RunNextAsync(token), token);
                            failedConfiguration = null;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        engine?.SetPaused(true); failedConfiguration = config;
                        lock (gate) lastError = ex.GetType().Name;
                        SetState("configuration-error");
                        logger.LogWarning("JellyCache prefetch unavailable: {Kind}", ex.GetType().Name);
                    }
                }
                previous = config;
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            sessions.PlaybackStart -= Start; sessions.PlaybackProgress -= Progress; sessions.PlaybackStopped -= Stop;
            engine?.SetPaused(true);
            if (copying is not null)
                try { RecordAttempt(await copying.ConfigureAwait(false)); } catch (OperationCanceledException) { }
            await maintenance.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (gate)
                {
                    state = "stopped";
                    foreach (var lease in retained.Values) lease.Dispose();
                    retained.Clear(); protections.Clear(); selectedPaths.Clear();
                    if (store?.Usage.Leases == 0) { store.Dispose(); store = null; engine = null; }
                }
            }
            finally { maintenance.Release(); }
        }
    }
}
