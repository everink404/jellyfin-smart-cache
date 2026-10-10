using System.Net.Http.Headers;
using System.Net.Http.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyCache.Plugin;

public sealed class PlaybackBridge(ISessionManager sessions, ILibraryManager library,
    ILogger<PlaybackBridge> logger) : BackgroundService
{
    private readonly EventBuffer<Snapshot> _buffer = new();
    private sealed record Snapshot(Guid ItemId, string? MediaSourceId);

    private void Start(object? sender, PlaybackProgressEventArgs args) => Capture(args, "start");
    private void Progress(object? sender, PlaybackProgressEventArgs args) => Capture(args, args.IsPaused ? "pause" : "progress");
    private void Stop(object? sender, PlaybackStopEventArgs args) => Capture(args, "stop");

    private void Capture(PlaybackProgressEventArgs args, string phase)
    {
        // Only bounded in-memory replacement occurs on Jellyfin's event callback.
        if (args.Session is null || args.Item is null) return;
        var playback = string.IsNullOrEmpty(args.PlaySessionId) ? args.Item.Id.ToString("N") : args.PlaySessionId;
        _buffer.Put(args.Session.Id, playback, phase,
            new(args.Item is Episode ? args.Item.Id : Guid.Empty, args.MediaSourceId));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("JELLYCACHE_BRIDGE_ENABLED") != "1") return;
        Uri endpoint;
        var token = Environment.GetEnvironmentVariable("JELLYCACHE_CONTROL_TOKEN");
        var root = Environment.GetEnvironmentVariable("JELLYCACHE_MEDIA_ROOT") ?? "/video";
        var strategy = Environment.GetEnvironmentVariable("JELLYCACHE_STRATEGY") ?? "ahead";
        var ahead = 2;
        try
        {
            endpoint = BridgeProtocol.Endpoint(Environment.GetEnvironmentVariable("JELLYCACHE_CONTROL_URL")
                ?? "http://127.0.0.1:8765/v1/events");
            if (token is null || token.Length < 32 || token.Contains('\r') || token.Contains('\n'))
                throw new ArgumentException("Missing valid control token");
            if (strategy is not ("ahead" or "next" or "season")) throw new ArgumentException("Invalid strategy");
            var setting = Environment.GetEnvironmentVariable("JELLYCACHE_AHEAD");
            if (setting is not null && (!int.TryParse(setting, out ahead) || ahead is < 1 or > 50))
                throw new ArgumentException("Invalid ahead count");
            BridgeProtocol.RelativePath(root.TrimEnd('/') + "/probe", root);
        }
        catch (ArgumentException)
        {
            logger.LogWarning("JellyCache bridge disabled: invalid environment configuration");
            return;
        }
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        sessions.PlaybackStart += Start;
        sessions.PlaybackProgress += Progress;
        sessions.PlaybackStopped += Stop;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (var ev in _buffer.Drain())
                {
                    try
                    {
                        object payload;
                        try { payload = Payload(ev, root, strategy, ahead); }
                        catch (Exception ex)
                        {
                            logger.LogWarning("JellyCache metadata unsupported ({Kind}); clearing previous session demand", ex.GetType().Name);
                            payload = new { session_id = ev.Session, sequence = ev.Sequence, phase = "stop" };
                        }
                        using var response = await http.PostAsJsonAsync(endpoint, payload, stoppingToken).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            logger.LogWarning("JellyCache control rejected an event: HTTP {Status}", (int)response.StatusCode);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // Never log token, media paths, raw sessions or exception messages.
                        logger.LogWarning("JellyCache event not delivered ({Kind}); a later progress event may retry", ex.GetType().Name);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            sessions.PlaybackStart -= Start;
            sessions.PlaybackProgress -= Progress;
            sessions.PlaybackStopped -= Stop;
        }
    }

    private object Payload(PendingEvent<Snapshot> ev, string root, string strategy, int ahead)
    {
        var phase = ev.Phase;
        if (phase == "stop" || ev.Value.ItemId == Guid.Empty)
            return new { session_id = ev.Session, sequence = ev.Sequence, phase = "stop" };
        if (library.GetItemById(ev.Value.ItemId) is not Episode current || current.SeasonId == Guid.Empty ||
            library.GetItemById(current.SeasonId) is not Season season)
            throw new InvalidOperationException("Missing season metadata");
        // Query metadata in the background, never probe or read the remote media here.
        var items = season.GetEpisodes().OfType<Episode>()
            .Where(e => !e.IsMissingEpisode && e.SeriesId == current.SeriesId && e.SeasonId == current.SeasonId).ToArray();
        if (items.Length is < 1 or > 1000) throw new InvalidOperationException("Invalid season size");
        object EpisodeData(Episode e)
        {
            // v0.1 refuses multi-version/stacked episodes instead of guessing a future version.
            if (e.MediaSourceCount != 1 || e.IsStacked || e.IsPlaceHolder || !e.IsFileProtocol ||
                e.IndexNumber is null or <= 0 || e.ParentIndexNumber is null or <= 0 ||
                (e.IndexNumberEnd.HasValue && e.IndexNumberEnd != e.IndexNumber))
                throw new InvalidOperationException("Unsupported episode metadata");
            if (e.Id == current.Id && !string.IsNullOrEmpty(ev.Value.MediaSourceId) &&
                (!Guid.TryParse(ev.Value.MediaSourceId, out var sourceId) || sourceId != e.Id))
                throw new InvalidOperationException("Unresolved playback source");
            return new { type = "Episode", id = e.Id.ToString("N"), series_id = e.SeriesId.ToString("N"),
                season_id = e.SeasonId.ToString("N"), index = e.IndexNumber.Value,
                season_number = e.ParentIndexNumber.Value, path = BridgeProtocol.RelativePath(e.Path, root) };
        }
        return new { session_id = ev.Session, sequence = ev.Sequence, phase,
            current = EpisodeData(current), episodes = items.Select(EpisodeData).ToArray(), strategy, ahead };
    }
}
