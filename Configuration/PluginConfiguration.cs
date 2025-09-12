
using MediaBrowser.Model.Plugins;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTube;

public class PluginConfiguration : BasePluginConfiguration
{
    public string BackendBaseUrl { get; set; } = "http://jellytube:8080";
    public string FormatPolicy { get; set; } = "h264_mp4";
    public List<string> Channels { get; set; } = new();
    public List<string> Playlists { get; set; } = new();
    public List<string> Searches { get; set; } = new();
    public int SyncIntervalMinutes { get; set; } = 60;
    public bool UseProxyPlayback { get; set; } = true;
    public bool IncludeSponsorBlockChapters { get; set; } = true;
}
