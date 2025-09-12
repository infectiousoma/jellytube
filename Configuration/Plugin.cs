
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTube;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "JellyTube";
    public override Guid Id => Guid.Parse("e2a6b8b6-16b9-4a65-9b2f-1a2f2d3e5abc");

    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) { }

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "jellytube",
            EmbeddedResourcePath = GetType().Namespace + ".Web.jellytube.html"
        }
    };
}
