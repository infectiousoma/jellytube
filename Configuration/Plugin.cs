// File: Configuration/Plugin.cs
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTube
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        // Nullable per Option A
        public static Plugin? Instance { get; private set; }

        public override string Name => "JellyTube";
        public override string Description => "Browse and play YouTube via JellyTube bridge (demo)";
        public override Guid Id => Guid.Parse("e2a6b8b6-16b9-4a65-9b2f-1a2f2d3e5abc");

        public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
        {
            Instance = this;

            // optional: write embedded resource names for debugging
            try
            {
                System.IO.File.WriteAllLines(
                    System.IO.Path.Combine(paths.CachePath, "JellyTube.embedded.txt"),
                    typeof(Plugin).Assembly.GetManifestResourceNames()
                );
            }
            catch { /* ignore */ }
        }

        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = Name, // same as this.Name
                EmbeddedResourcePath = "Jellyfin.Plugin.JellyTube.Configuration.configPage.html"
            }
        };
    }
}
