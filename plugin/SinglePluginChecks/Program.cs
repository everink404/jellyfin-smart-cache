using Emby.Server.Implementations.Library;
using JellyCache.SinglePluginChecks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

var root = Path.GetFullPath(".tools/single-plugin-probe/fixtures");
Directory.CreateDirectory(root);
var origin = Path.Combine(root, "origin.bin");
var cached = Path.Combine(root, "cached.bin");
var payload = Enumerable.Range(0, 16384).Select(i => (byte)(i % 251)).ToArray();
File.WriteAllBytes(origin, payload);
File.WriteAllBytes(cached, payload);
var id = Guid.NewGuid();
var item = new FixtureEpisode { Id = id, Path = origin };
var originalSource = new MediaSourceInfo
{
    Id = id.ToString("N"), Path = origin, Protocol = MediaProtocol.File,
    Container = "mp4", VideoType = VideoType.VideoFile, Size = payload.Length,
    RunTimeTicks = TimeSpan.FromMinutes(1).Ticks,
    SupportsDirectPlay = true, SupportsDirectStream = true, SupportsTranscoding = true,
    MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Width = 1280, Height = 720 }]
};
item.Source = originalSource;
// Unmodified official implementation from the NuGet-stamped repository commit.
using var core = new MediaSourceManager(null!, null!, null!, null!, null!, null!,
    NullLogger<MediaSourceManager>.Instance, null!, null!, null!, null!, null!, null!);
core.AddParts([]);
var cache = new FixtureCache(id, origin, cached);
var services = new ServiceCollection();
services.AddSingleton<IMediaSourceManager>(core);
services.AddSingleton(cache);
new ProbeRegistrator().RegisterServices(services, null!);
using var provider = services.BuildServiceProvider();
var manager = provider.GetRequiredService<IMediaSourceManager>();
int checks = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException(label);
    checks++;
    Console.WriteLine("PASS " + label);
}
async Task<MediaSourceInfo> Playback(bool substitution = false)
    => (await manager.GetPlaybackMediaSources(item, null!, false, substitution, default)).Single();
Check(ReferenceEquals(manager, provider.GetRequiredService<IMediaSourceManager>()), "plugin registration preserves singleton");
Check((await Playback(true)).Path == cached, "PlaybackInfo-style substitution flag selects cache");
var hit = await Playback();
Check(hit.Path == cached, "streaming re-resolution selects cache");
Check(hit.Id == originalSource.Id && item.Id == id && item.Path == origin, "item ID, source ID and library path unchanged");
Check(originalSource.Path == origin && !ReferenceEquals(hit, originalSource), "original descriptor is not mutated");
Check(manager.GetStaticMediaSources(item, false).Single().Path == origin, "static library DTO remains original");
Check((await manager.GetMediaSource(item, hit.Id, null!, false, default)).Path == cached, "source-ID lookup selects cache");
Check(hit.SupportsDirectPlay && hit.SupportsDirectStream && hit.SupportsTranscoding && hit.MediaStreams[0].Codec == "h264", "playback metadata retained");
var encoding = new EncodingHelper(null!, null!, null!, null!, null!, null!);
var state = new EncodingJobInfo(TranscodingJobType.Progressive)
    { IsVideoRequest = true, BaseRequest = new BaseEncodingJobOptions { VideoCodec = "h264", Static = true } };
encoding.AttachMediaSourceInfo(state, new EncodingOptions(), hit, "stream.mp4");
Check(state.MediaPath == cached && state.InputProtocol == MediaProtocol.File, "official EncodingHelper attaches cache for encoder input");
using (var stream = File.OpenRead(state.MediaPath))
{
    stream.Seek(4096, SeekOrigin.Begin);
    var range = new byte[512];
    stream.ReadExactly(range);
    Check(range.SequenceEqual(payload.Skip(4096).Take(512)), "selected local file supports offset read");
}
cache.Complete = false;
Check((await Playback()).Path == origin, "incomplete cache falls back");
cache.Complete = true;
File.WriteAllBytes(cached, [1, 2, 3]);
Check((await Playback()).Path == origin, "corrupt cache falls back");
File.WriteAllBytes(cached, payload);
File.Delete(cached);
Check((await Playback()).Path == origin, "eviction after negotiation falls back on next resolution");
File.WriteAllBytes(cached, payload);
File.SetLastWriteTimeUtc(origin, File.GetLastWriteTimeUtc(origin).AddSeconds(5));
Check((await Playback()).Path == origin, "changed source invalidates cache");
cache.Enabled = false;
Check((await Playback()).Path == origin, "disabled routing preserves original");
core.AddParts([new ExtraSourceProvider(cached, id)]);
var sources = await core.GetPlaybackMediaSources(item, null!, false, false, default);
Check(sources.Count == 2 && sources[0].Path == origin, "unique provider ID adds version; original stays default");
core.AddParts([new ExtraSourceProvider(cached, id, true)]);
sources = await core.GetPlaybackMediaSources(item, null!, false, false, default);
Check(sources.Count == 2 && sources[0].Path == origin, "duplicate provider ID does not override original");
// Exercise the actual packaged plugin, not just the earlier research proxy.
core.AddParts([]);
File.WriteAllBytes(cached, payload);
var probeSettings = new JellyCache.ReadProbe.ProbeSettings(true, origin, cached, 1);
var probeState = new JellyCache.ReadProbe.ProbeState(
    NullLogger<JellyCache.ReadProbe.ProbeState>.Instance, new(true), () => probeSettings);
var realRouter = new JellyCache.ReadProbe.RoutingManager(core, probeState);
async Task<MediaSourceInfo> RealPlayback() => (await realRouter.GetPlaybackMediaSources(item, null!, false, false, default)).Single();
Check((await RealPlayback()).Path == origin, "packaged router fails open before validation");
await probeState.ValidateAsync(probeSettings, default);
Check((await RealPlayback()).Path == cached, "packaged router selects SHA-256 verified cache");
Check((await realRouter.GetMediaSource(item, originalSource.Id, null!, false, default)).Path == cached, "packaged source-ID lookup selects cache");
Check(realRouter.GetStaticMediaSources(item, false).Single().Path == origin, "packaged static metadata unchanged");
probeSettings = probeSettings with { Enabled = false };
Check((await RealPlayback()).Path == origin, "configuration disable bypasses cache immediately");
probeSettings = probeSettings with { Enabled = true, Revision = 2 };
Check((await RealPlayback()).Path == origin, "changed configuration requires revalidation");
await probeState.ValidateAsync(probeSettings, default);
File.SetLastWriteTimeUtc(cached, File.GetLastWriteTimeUtc(cached).AddSeconds(5));
Check((await RealPlayback()).Path == origin, "packaged router invalidates changed cache");
File.WriteAllBytes(cached, payload.Select(b => (byte)(b ^ 1)).ToArray());
await probeState.ValidateAsync(probeSettings, default);
Check((await RealPlayback()).Path == origin, "same-size wrong content is rejected");
File.WriteAllBytes(cached, [1]);
await probeState.ValidateAsync(probeSettings, default);
Check((await RealPlayback()).Path == origin, "partial cache is rejected");
File.Delete(cached);
await probeState.ValidateAsync(probeSettings, default);
Check((await RealPlayback()).Path == origin, "missing cache is rejected");
File.WriteAllBytes(cached, payload);
probeSettings = probeSettings with { Cache = origin };
await probeState.ValidateAsync(probeSettings, default);
Check((await RealPlayback()).Path == origin, "source cannot be its own cache");
probeSettings = probeSettings with { Cache = "relative.bin" };
await probeState.ValidateAsync(probeSettings, default);
Check((await RealPlayback()).Path == origin, "relative cache path rejected");
probeSettings = probeSettings with { Cache = cached };
await probeState.ValidateAsync(probeSettings, default);
core.AddParts([new ExtraSourceProvider(cached, id)]);
Check((await realRouter.GetPlaybackMediaSources(item, null!, false, false, default))[0].Path == origin, "multiple sources bypass packaged router");
Check((await realRouter.GetMediaSource(item, originalSource.Id, null!, false, default)).Path == origin, "source lookup cannot bypass multiple-source guard");
core.AddParts([]);
var otherItem = new FixtureEpisode { Id = Guid.NewGuid(), Path = origin + ".other", Source = originalSource };
Check((await realRouter.GetPlaybackMediaSources(otherItem, null!, false, false, default)).Single().Path == origin, "other episode is untouched");

// Match the built-in service registration shape and resolve the real decorator through DI.
var typedServices = new ServiceCollection();
typedServices.AddLogging();
foreach (var parameter in typeof(MediaSourceManager).GetConstructors().Single().GetParameters())
{
    if (parameter.ParameterType.Namespace?.StartsWith("Microsoft.Extensions.Logging") == true) continue;
    typedServices.AddSingleton(parameter.ParameterType,
        System.Reflection.DispatchProxy.Create(parameter.ParameterType, typeof(UnusedDependency)));
}
typedServices.AddSingleton<IMediaSourceManager, MediaSourceManager>();
var routerResolved = false;
var sessionsResolved = false;
typedServices.AddSingleton<MediaBrowser.Controller.Session.ISessionManager>(sp =>
{
    if (!routerResolved) throw new InvalidOperationException("Media router must not eagerly resolve its session-dependent host.");
    _ = sp.GetRequiredService<IMediaSourceManager>(); // Match the actual SessionManager dependency.
    sessionsResolved = true;
    return System.Reflection.DispatchProxy.Create<MediaBrowser.Controller.Session.ISessionManager, UnusedDependency>();
});
new JellyCache.ReadProbe.ServiceRegistrator().RegisterServices(typedServices, null!);
using (var typedProvider = typedServices.BuildServiceProvider())
{
    var resolved = typedProvider.GetRequiredService<IMediaSourceManager>();
    routerResolved = true;
    Check(!sessionsResolved, "media router resolves without eagerly constructing SessionManager");
    _ = typedProvider.GetRequiredService<JellyCache.ReadProbe.PrefetchHost>();
    Check(sessionsResolved, "host resolves after session-to-media dependency without a startup cycle");
    Check(resolved is JellyCache.ReadProbe.RoutingManager, "real plugin registrator decorates built-in service type through DI");
    Check(ReferenceEquals(resolved, typedProvider.GetRequiredService<IMediaSourceManager>()), "packaged decorator keeps singleton lifetime");
    resolved.AddParts([]);
    Check((await resolved.GetPlaybackMediaSources(item, null!, false, false, default)).Single().Path == origin, "unconfigured packaged plugin preserves playback source");
}
var unknownServices = new ServiceCollection();
unknownServices.AddSingleton<IMediaSourceManager>(core);
var untouched = unknownServices.Single();
new JellyCache.ReadProbe.ServiceRegistrator().RegisterServices(unknownServices, null!);
Check(unknownServices.Contains(untouched), "unknown registration is retained rather than replaced");
var resource = typeof(JellyCache.ReadProbe.Plugin).Assembly.GetManifestResourceNames();
Check(resource.Contains("JellyCache.ReadProbe.Configuration.html"), "dashboard configuration is embedded in packaged assembly");
probeState.Dispose();
Console.WriteLine($"{checks} checks passed. Source/interface fixture only; no actual Jellyfin playback or ffmpeg executed.");
await PrefetchHostChecks.Run();
await ReadObservationChecks.Run();
await ReadLeaseScopeChecks.Run();
if (args.Contains("--http")) await HttpReadChecks.Run();

sealed class FixtureEpisode : Episode, IHasMediaSources
{
    public MediaSourceInfo Source { get; set; } = null!;
    IReadOnlyList<MediaSourceInfo> IHasMediaSources.GetMediaSources(bool enablePathSubstitution) => [Source];
    IReadOnlyList<MediaStream> IHasMediaSources.GetMediaStreams() => Source.MediaStreams;
}
sealed class ExtraSourceProvider(string path, Guid id, bool duplicateId = false) : IMediaSourceProvider
{
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken token)
        => Task.FromResult<IEnumerable<MediaSourceInfo>>([new MediaSourceInfo
        {
            Id = duplicateId ? id.ToString("N") : "cache-fixture", Path = path,
            Protocol = MediaProtocol.File, VideoType = VideoType.VideoFile,
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Width = 1280 }]
        }]);
    public Task<ILiveStream> OpenMediaSource(string token, List<ILiveStream> streams, CancellationToken cancel)
        => throw new NotSupportedException("No live streams in this fixture");
}

public class UnusedDependency : System.Reflection.DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        => throw new NotSupportedException("Unconfigured fixture dependency: " + method?.Name);
}
