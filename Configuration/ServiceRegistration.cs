// Configuration/ServiceRegistration.cs
using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Controller;               // IServerApplicationHost
using MediaBrowser.Controller.Plugins;       // IPluginServiceRegistrator
using MediaBrowser.Controller.Channels;      // IChannel

namespace Jellyfin.Plugin.JellyTube
{
    // Discovered automatically by Jellyfin. Must have a parameterless ctor.
    public class ServiceRegistration : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
        {
            // Register your Channel so Jellyfin can discover it
            services.AddSingleton<IChannel, Channels.JellyTubeChannel>();
        }
    }
}
