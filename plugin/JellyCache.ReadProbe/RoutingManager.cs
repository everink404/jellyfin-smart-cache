#nullable disable
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;

namespace JellyCache.ReadProbe;

// Explicit forwarding: interface changes fail compilation instead of failing reflective invocation.
public sealed class RoutingManager(IMediaSourceManager inner, ProbeState state, PrefetchAccess access = null,
    IHttpContextAccessor requestContext = null) : IMediaSourceManager
{
    private IReadOnlyList<MediaSourceInfo> Select(BaseItem item, IReadOnlyList<MediaSourceInfo> sources, string stage)
    {
        var prefetch = access?.Host;
        return prefetch?.AutomaticMode == true ? prefetch.Select(item, sources, stage, requestContext?.HttpContext) : state.Select(item, sources, stage);
    }
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken token)
    {
        var sources = await inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, token).ConfigureAwait(false);
        return Select(item, sources, enablePathSubstitution ? "negotiation" : "stream-resolution");
    }
    public async Task<MediaSourceInfo> GetMediaSource(BaseItem item, string sourceId, string liveId, bool substitution, CancellationToken token)
    {
        if (!string.IsNullOrEmpty(liveId))
            return await inner.GetMediaSource(item, sourceId, liveId, substitution, token).ConfigureAwait(false);
        // Match the pinned core implementation, retaining the full source list so
        // a lookup cannot accidentally bypass the single-source eligibility check.
        var sources = await inner.GetPlaybackMediaSources(item, null, false, substitution, token).ConfigureAwait(false);
        return Select(item, sources, "source-lookup")
            .FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
    }
    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => inner.AddParts(providers);
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid id) => inner.GetMediaStreams(id);
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => inner.GetMediaStreams(query);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid id) => inner.GetMediaAttachments(id);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => inner.GetMediaAttachments(query);
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool substitution, User user = null) => inner.GetStaticMediaSources(item, substitution, user);
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken token) => inner.OpenLiveStream(request, token);
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken token) => inner.OpenLiveStreamInternal(request, token);
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken token) => inner.GetLiveStream(id, token);
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken token) => inner.GetLiveStreamWithDirectStreamProvider(id, token);
    public ILiveStream GetLiveStreamInfo(string id) => inner.GetLiveStreamInfo(id);
    public ILiveStream GetLiveStreamInfoByUniqueId(string id) => inner.GetLiveStreamInfoByUniqueId(id);
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken token) => inner.GetRecordingStreamMediaSources(info, token);
    public Task CloseLiveStream(string id) => inner.CloseLiveStream(id);
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken token) => inner.GetLiveStreamMediaInfo(id, token);
    public bool SupportsDirectStream(string path, MediaProtocol protocol) => inner.SupportsDirectStream(path, protocol);
    public MediaProtocol GetPathProtocol(string path) => inner.GetPathProtocol(path);
    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user) => inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
    public Task AddMediaInfoWithProbe(MediaSourceInfo source, bool audio, string key, bool delay, bool live, CancellationToken token) => inner.AddMediaInfoWithProbe(source, audio, key, delay, live, token);
}
