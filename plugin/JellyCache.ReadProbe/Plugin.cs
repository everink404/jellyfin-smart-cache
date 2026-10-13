using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace JellyCache.ReadProbe;

public sealed class ProbeConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string SourcePath { get; set; } = "";
    public string CachePath { get; set; } = "";
    public int ValidationRevision { get; set; }
    public bool AutomaticPrefetch { get; set; }
    public bool PausePrefetch { get; set; }
    public string MediaRoot { get; set; } = "/video";
    public string CacheDirectory { get; set; } = "";
    public long CapacityBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long BytesPerSecond { get; set; } = 8L * 1024 * 1024;
    public string Strategy { get; set; } = "ahead";
    public int Ahead { get; set; } = 2;
}

public sealed class Plugin : BasePlugin<ProbeConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => "JellyCache single-plugin read test";
    public override Guid Id => new("d9ed3154-82f7-4e4b-9b74-24831fc59d65");
    public override string Description => "Single-plugin cache routing and bounded same-season prefetch test.";
    public IEnumerable<PluginPageInfo> GetPages() => [new()
    {
        Name = "jellycache-read-test", EmbeddedResourcePath = "JellyCache.ReadProbe.Configuration.html"
    }];
}

// Own the raw built-in singleton exactly once, independently of the forwarding decorator.
public sealed class ManagerOwner(IMediaSourceManager manager) : IDisposable
{
    public IMediaSourceManager Manager { get; } = manager;
    public void Dispose() => (Manager as IDisposable)?.Dispose();
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        var candidates = services.Where(d => d.ServiceType == typeof(IMediaSourceManager)).ToArray();
        var supported = typeof(IMediaSourceManager).Assembly.GetName().Version == new Version(12, 1, 0, 0)
            && candidates.Length == 1 && candidates[0].Lifetime == ServiceLifetime.Singleton
            && candidates[0].ImplementationType?.FullName == "Emby.Server.Implementations.Library.MediaSourceManager";
        services.AddSingleton(new RegistrationResult(supported));
        services.AddSingleton<ProbeState>();
        services.AddHostedService(sp => sp.GetRequiredService<ProbeState>());
        services.AddSingleton<PrefetchAccess>();
        services.AddSingleton<PrefetchHost>();
        services.AddHostedService(sp => sp.GetRequiredService<PrefetchHost>());
        if (!supported) return; // Preserve unknown registrations, rather than breaking server startup.
        var original = candidates[0];
        services.Remove(original);
        services.AddSingleton(sp => new ManagerOwner((IMediaSourceManager)
            ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)));
        services.AddSingleton<IMediaSourceManager>(sp => new RoutingManager(
            sp.GetRequiredService<ManagerOwner>().Manager, sp.GetRequiredService<ProbeState>(), sp.GetRequiredService<PrefetchAccess>()));
    }
}

public sealed record RegistrationResult(bool Supported);
