using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace JellyCache.Plugin;

public sealed class PluginConfiguration : BasePluginConfiguration { }

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer) : BasePlugin<PluginConfiguration>(paths, serializer)
{
    public override string Name => "JellyCache experimental bridge";
    public override Guid Id => new("7dbbb7e6-2274-498c-af54-203587f64f52");
    public override string Description => "Experimental playback events for the local JellyCache worker.";
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
        => services.AddHostedService<PlaybackBridge>();
}
