using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;

namespace JellyCache.SinglePluginChecks;

// Research fixture only. Not included in the published bridge.
public sealed class FixtureCache(Guid itemId, string source, string cache)
{
    private readonly long sourceLength = new FileInfo(source).Length;
    private readonly DateTime sourceTime = File.GetLastWriteTimeUtc(source);
    private readonly byte[] digest = SHA256.HashData(File.ReadAllBytes(source));
    public bool Enabled { get; set; } = true;
    public bool Complete { get; set; } = true;
    public MediaSourceInfo Select(BaseItem item, MediaSourceInfo original)
    {
        if (!Enabled || !Complete || item is not Episode || item.Id != itemId
            || original.Protocol != MediaProtocol.File || original.Path != source)
            return original;
        try
        {
            var file = new FileInfo(source);
            if (file.Length != sourceLength || file.LastWriteTimeUtc != sourceTime
                || !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(File.ReadAllBytes(cache))))
                return original;
            var copy = JsonSerializer.Deserialize<MediaSourceInfo>(
                JsonSerializer.Serialize(original, JsonDefaults.Options), JsonDefaults.Options)!;
            copy.Path = cache;
            copy.EncoderPath = null;
            copy.EncoderProtocol = null;
            return copy;
        }
        catch (IOException) { return original; }
        catch (UnauthorizedAccessException) { return original; }
    }
}

public class RouteProxy : DispatchProxy
{
    private IMediaSourceManager inner = null!;
    private FixtureCache cache = null!;
    public static IMediaSourceManager Wrap(IMediaSourceManager inner, FixtureCache cache)
    {
        var result = Create<IMediaSourceManager, RouteProxy>();
        ((RouteProxy)result).inner = inner;
        ((RouteProxy)result).cache = cache;
        return result;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        object? result;
        try { result = method!.Invoke(inner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
        if (method!.Name == nameof(IMediaSourceManager.GetPlaybackMediaSources))
            return SelectList((Task<IReadOnlyList<MediaSourceInfo>>)result!, (BaseItem)args![0]!);
        if (method.Name == nameof(IMediaSourceManager.GetMediaSource)
            && string.IsNullOrEmpty((string?)args![2]))
            return SelectOne((Task<MediaSourceInfo>)result!, (BaseItem)args[0]!);
        return result;
    }
    private async Task<IReadOnlyList<MediaSourceInfo>> SelectList(Task<IReadOnlyList<MediaSourceInfo>> task, BaseItem item)
        => (await task.ConfigureAwait(false)).Select(s => cache.Select(item, s)).ToArray();
    private async Task<MediaSourceInfo> SelectOne(Task<MediaSourceInfo> task, BaseItem item)
    {
        var source = await task.ConfigureAwait(false);
        return source is null ? null! : cache.Select(item, source);
    }
}

public sealed class ProbeRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        var original = services.Single(s => s.ServiceType == typeof(IMediaSourceManager));
        if (original.Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("Unexpected media-source lifetime");
        services.Remove(original);
        services.AddSingleton<IMediaSourceManager>(sp =>
        {
            var manager = original.ImplementationInstance
                ?? original.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
            return RouteProxy.Wrap((IMediaSourceManager)manager, sp.GetRequiredService<FixtureCache>());
        });
    }
}
