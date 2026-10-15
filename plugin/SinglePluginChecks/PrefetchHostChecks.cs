using System.Reflection;
using JellyCache.Core;
using JellyCache.ReadProbe;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using MediaBrowser.Controller.MediaEncoding;

internal static class PrefetchHostChecks
{
    public static async Task Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var root = Path.GetFullPath(Path.Combine(".tools", "host-checks", Guid.NewGuid().ToString("N")));
        var media = Path.Combine(root, "media"); Directory.CreateDirectory(media);
        var seasonId = Guid.NewGuid(); var seriesId = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 5).Select(n => new HostEpisode
        {
            Id = Guid.NewGuid(), SeriesId = seriesId, SeasonId = seasonId, IndexNumber = n,
            ParentIndexNumber = 1, Path = Path.Combine(media, $"E{n}.mkv"), Name = $"Episode {n}",
            VideoType = VideoType.VideoFile
        }).ToArray();
        foreach (var episode in episodes)
        {
            File.WriteAllBytes(episode.Path, Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray());
            episode.Source = new() { Id = episode.Id.ToString("N"), Path = episode.Path, Protocol = MediaProtocol.File,
                Container = "mkv", MediaStreams = [new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" }] };
        }
        var season = new Season { Id = seasonId, SeriesId = seriesId, IndexNumber = 1, Name = "Season 1" };
        var series = new Series { Id = seriesId, Name = "Fixture" };
        var lib = DispatchProxy.Create<ILibraryManager, TestProxy>();
        ((TestProxy)(object)lib).Handler = (method, args) => method.Name switch
        {
            "GetItemById" => (Guid)args[0]! == seasonId ? season : (Guid)args[0]! == seriesId ? series : episodes.Single(e => e.Id == (Guid)args[0]!),
            "GetLinkedAlternateVersions" => Array.Empty<Video>(),
            "GetLocalAlternateVersionIds" => Array.Empty<Guid>(),
            "GetItemList" => episodes.Cast<BaseItem>().ToArray(),
            "GetLibraryOptions" => new LibraryOptions(),
            "GetCollectionFolders" => new List<Folder>(),
            "Sort" => (IEnumerable<BaseItem>)args[0]!,
            _ => throw new NotSupportedException(method.Name)
        };
        var configManager = DispatchProxy.Create<IServerConfigurationManager, TestProxy>();
        ((TestProxy)(object)configManager).Handler = (m, _) => m.Name == "get_Configuration" ? new ServerConfiguration() : throw new NotSupportedException(m.Name);
        var savedLibrary = BaseItem.LibraryManager; var savedConfig = BaseItem.ConfigurationManager;
        var savedFs = BaseItem.FileSystem; var savedSources = BaseItem.MediaSourceManager;
        var fileSystem = DispatchProxy.Create<MediaBrowser.Model.IO.IFileSystem, TestProxy>();
        ((TestProxy)(object)fileSystem).Handler = (m, _) => m.Name == "IsPathFile" ? true : throw new NotSupportedException(m.Name);
        var mediaSources = DispatchProxy.Create<IMediaSourceManager, TestProxy>();
        ((TestProxy)(object)mediaSources).Handler = (m, _) => m.Name == "GetPathProtocol" ? MediaProtocol.File : throw new NotSupportedException(m.Name);
        BaseItem.FileSystem = fileSystem; BaseItem.MediaSourceManager = mediaSources;
        BaseItem.LibraryManager = lib; BaseItem.ConfigurationManager = configManager;
        var sessionManager = DispatchProxy.Create<ISessionManager, TestProxy>();
        var sessionProxy = (TestProxy)(object)sessionManager;
        var settings = new PrefetchSettings(true, true, false, new(media, Path.Combine(root, "managed"), 32768, 0, 1024 * 1024 * 1024), "ahead", 2);
        using var host = new PrefetchHost(sessionManager, lib, new(true, true), NullLogger<PrefetchHost>.Instance, () => settings);
        using var builtin = new Emby.Server.Implementations.Library.MediaSourceManager(null!, null!, null!, null!, null!, null!,
            NullLogger<Emby.Server.Implementations.Library.MediaSourceManager>.Instance, null!, null!, null!, null!, null!, null!);
        builtin.AddParts([]);
        using var manual = new ProbeState(NullLogger<ProbeState>.Instance, new(true));
        var router = new RoutingManager(builtin, manual, new PrefetchAccess { Host = host });
        async Task Wait(Func<bool> condition, string name)
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(40);
            Check(condition(), name + ": " + host.Snapshot().State + "/" + host.Snapshot().LastError);
        }
        PlaybackProgressEventArgs Args(Episode episode, string playback) => new()
        { Item = episode, Session = new SessionInfo(sessionManager, NullLogger.Instance) { Id = "viewer" }, PlaySessionId = playback, MediaSourceId = episode.Id.ToString("N") };
        IReadOnlyList<MediaSourceInfo> Sources(Episode e) => [new()
        { Id = e.Id.ToString("N"), Path = e.Path, Protocol = MediaProtocol.File, Container = "mkv", EncoderPath = e.Path }];
        try
        {
            Check(season.GetEpisodes().Count == 5, "real season query fixture");
            foreach (var episode in episodes) _ = PrefetchHost.Describe(episode, media);
            await host.StartAsync(default);
            await Wait(() => host.Snapshot().State == "ready", "host initialized");
            Check(sessionProxy.Events.ContainsKey("PlaybackStart"), "actual hosted service subscribes playback");
            sessionProxy.Raise("PlaybackStart", Args(episodes[0], "play1"));
            await Wait(() => host.Snapshot().Queue.Count == 2 && host.Snapshot().Queue.All(q => q.State == "ready"), "real season adapter and events produce two verified copies");
            Check(host.Snapshot().Queue.Select(q => q.EpisodeNumber).SequenceEqual([2, 3]), "same-season window");
            await Wait(() => host.Snapshot().LastAttempt?.Outcome == "ready", "completed copy diagnostic published");
            Check(host.Snapshot().LastAttempt is { Outcome: "ready", Failures: 0, ErrorType: null }, "admin snapshot retains copy diagnostic");
            var before = Sources(episodes[1]);
            Check(host.Select(episodes[1], before, "negotiation").Single().Path == episodes[1].Path && host.Snapshot().Usage!.Leases == 0,
                "lifecycle negotiation keeps origin metadata without speculative protection");
            string scopedPath;
            using (var scope = new ReadLeaseScope())
            {
                var context = new DefaultHttpContext(); context.Features.Set(scope);
                scopedPath = host.Select(episodes[1], before, "stream-resolution", context).Single().Path;
                Check(scopedPath != episodes[1].Path && host.Snapshot().RetainedSelections == 0 && host.Snapshot().Usage!.Leases == 1,
                    "actual request scope protects route without until-restart pin");
            }
            Check(host.Snapshot().Usage!.Leases == 0, "actual route request completion releases cache");
            var rawSubtitles = DispatchProxy.Create<ISubtitleEncoder, ObservationProxy>();
            var finishSubtitle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((ObservationProxy)(object)rawSubtitles).Handler = (_, _) => finishSubtitle.Task;
            var protectedSubtitles = new ProtectedSubtitleEncoder(rawSubtitles, new PrefetchAccess { Host = host });
            var extracting = protectedSubtitles.ExtractAllExtractableSubtitles(new() { Path = scopedPath }, default);
            Check(host.Snapshot().Usage!.Leases == 1, "subtitle decorator pins existing descriptor before extraction");
            finishSubtitle.SetResult(); await extracting;
            Check(host.Snapshot().Usage!.Leases == 0, "successful subtitle task releases operation protection");
            using var oldJob = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { HasExited = true, MediaSource = new() { Path = scopedPath } };
            var rawTranscode = DispatchProxy.Create<ITranscodeManager, ObservationProxy>();
            ((ObservationProxy)(object)rawTranscode).Handler = (_, _) => oldJob;
            var accessor = new HttpContextAccessor();
            var protectedTranscode = new ProtectedTranscodeManager(rawTranscode, new PrefetchAccess { Host = host }, accessor);
            Check(protectedTranscode.GetTranscodingJob("old-playback") is null, "exited cached job forces fresh source resolution before seek");
            oldJob.HasExited = false;
            using (var scope = new ReadLeaseScope())
            {
                accessor.HttpContext = new DefaultHttpContext(); accessor.HttpContext.Features.Set(scope);
                Check(ReferenceEquals(protectedTranscode.GetTranscodingJob("active-playback"), oldJob) && host.Snapshot().Usage!.Leases == 1,
                    "reused active job descriptor acquires request transition pin");
            }
            accessor.HttpContext = null;
            Check(host.Snapshot().Usage!.Leases == 0, "reused-job request releases its own pin");
            var rawAttachments = DispatchProxy.Create<IAttachmentExtractor, ObservationProxy>();
            ((ObservationProxy)(object)rawAttachments).Handler = (_, _) => Task.FromException(new IOException("synthetic unconfirmed process failure"));
            var protectedAttachments = new ProtectedAttachmentExtractor(rawAttachments, new PrefetchAccess { Host = host });
            try { await protectedAttachments.ExtractAllAttachments(scopedPath, new() { Path = scopedPath }, default); }
            catch (IOException) { }
            Check(host.Snapshot().RetainedSelections == 1 && host.Snapshot().Usage!.Leases == 1,
                "unconfirmed native task failure retains conservative protection");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            ((ObservationProxy)(object)rawAttachments).Handler = (_, _) => Task.FromCanceled(cancellation.Token);
            try { await protectedAttachments.ExtractAllAttachments(scopedPath, new() { Path = scopedPath }, cancellation.Token); }
            catch (OperationCanceledException) { }
            Check(host.Snapshot().RetainedSelections == 1 && host.Snapshot().Usage!.Leases == 1,
                "repeated uncertain cancellation deduplicates conservative cache pins");
            var hit = host.Select(episodes[1], before, "stream-resolution").Single();
            Check(hit.Path != episodes[1].Path && File.ReadAllBytes(hit.Path).SequenceEqual(File.ReadAllBytes(episodes[1].Path)), "host route selects copied bytes");
            Check(before.Single().Path == episodes[1].Path && hit.Id == before.Single().Id && hit.EncoderPath is null, "descriptor and identity preserved");
            Check((await router.GetPlaybackMediaSources(episodes[1], null!, false, false, default)).Single().Path == hit.Path, "automatic mode flows through real packaged playback router");
            Check((await router.GetMediaSource(episodes[1], episodes[1].Id.ToString("N"), null!, false, default)).Path == hit.Path, "automatic source-ID re-resolution flows through pinned built-in manager");
            host.Select(episodes[1], before, "source-lookup");
            Check(host.Snapshot().RetainedSelections == 1 && host.Snapshot().Usage!.Leases == 1, "one retained lease covers repeated resolution");
            Check(host.Snapshot().Protections!.Single() is { FirstStage: "stream-resolution", LastStage: "source-lookup", Selections: 4, ReleasePolicy: "until-host-stops" },
                "bounded protection diagnostic aggregates repeated resolution without another lease");
            Check(host.Select(episodes[1], [before[0], before[0]], "stream-resolution").First().Path == episodes[1].Path, "multiple versions bypass routing");
            settings = settings with { Paused = true };
            await Wait(() => host.Snapshot().State == "paused", "pause reaches host");
            Check(host.Select(episodes[1], before, "stream-resolution").Single().Path == hit.Path, "pause preserves cache reading");
            settings = settings with { Enabled = false };
            Check(host.Select(episodes[1], before, "stream-resolution").Single().Path == episodes[1].Path, "disable bypass is immediate");
            await Wait(() => host.Snapshot().State == "disabled" && host.Snapshot().Queue.Count == 0, "disable clears demand");
            Check(File.Exists(hit.Path), "disable retains selected file for existing playback");
            settings = settings with { Enabled = true, Paused = false, Storage = settings.Storage with { Capacity = 65536 } };
            await Wait(() => host.Snapshot().State == "restart-required", "structural settings require restart");
            Check(host.Select(episodes[1], before, "stream-resolution").Single().Path == episodes[1].Path, "unapplied storage settings fail open");
            settings = settings with { Storage = settings.Storage with { Capacity = 32768 } };
            await Wait(() => host.Snapshot().State == "ready", "restored settings resume existing store");
            sessionProxy.Raise("PlaybackStart", Args(episodes[3], "play4"));
            await Wait(() => host.Snapshot().Queue.Count == 1 && host.Snapshot().Queue[0].EpisodeNumber == 5, "jump uses fresh window");
            sessionProxy.Raise("PlaybackStopped", new PlaybackStopEventArgs
            { Item = episodes[0], Session = new SessionInfo(sessionManager, NullLogger.Instance) { Id = "viewer" }, PlaySessionId = "play1" });
            await Task.Delay(600);
            Check(host.Snapshot().Queue.Count == 1, "delayed old stop does not clear new demand");
            await host.StopAsync(default);
            Check(host.Snapshot().State == "stopped" && host.Snapshot().RetainedSelections == 0, "shutdown joins copy before releasing retained leases");
            Check(host.Snapshot().Protections!.Count == 0, "shutdown clears protection observations with leases");
        }
        finally
        {
            await host.StopAsync(default);
            BaseItem.LibraryManager = savedLibrary; BaseItem.ConfigurationManager = savedConfig;
            BaseItem.FileSystem = savedFs; BaseItem.MediaSourceManager = savedSources;
        }
        Console.WriteLine($"{count} hosted prefetch checks passed. Synthetic episode bytes; no actual Jellyfin server or ffmpeg.");
    }
}
internal sealed class HostEpisode : Episode, IHasMediaSources
{
    public MediaSourceInfo Source { get; set; } = null!;
    IReadOnlyList<MediaSourceInfo> IHasMediaSources.GetMediaSources(bool substitution) => [Source];
    IReadOnlyList<MediaStream> IHasMediaSources.GetMediaStreams() => Source.MediaStreams;
}
public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[], object?>? Handler { get; set; }
    public Dictionary<string, Delegate?> Events { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var name = method!.Name;
        if (name.StartsWith("add_", StringComparison.Ordinal))
        { var key = name[4..]; Events[key] = Delegate.Combine(Events.GetValueOrDefault(key), (Delegate)args![0]!); return null; }
        if (name.StartsWith("remove_", StringComparison.Ordinal))
        { var key = name[7..]; Events[key] = Delegate.Remove(Events.GetValueOrDefault(key), (Delegate)args![0]!); return null; }
        return Handler?.Invoke(method, args ?? []) ?? throw new NotSupportedException(name);
    }
    public void Raise(string name, EventArgs args) => Events.GetValueOrDefault(name)?.DynamicInvoke(this, args);
}
