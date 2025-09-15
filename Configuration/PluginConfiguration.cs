// File: Configuration/PluginConfiguration.cs
using MediaBrowser.Model.Plugins;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTube
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string BackendBaseUrl { get; set; } = "http://localhost:8080";
        public string FormatPolicy    { get; set; } = "h264_mp4";

        // Optional preconfigured sources
        public List<string> Channels   { get; set; } = new();   // YouTube channel IDs
        public List<string> Playlists  { get; set; } = new();   // YouTube playlist IDs
        public List<string> Searches   { get; set; } = new();   // Plain search terms

        public int  SyncIntervalMinutes        { get; set; } = 60;
        public bool UseProxyPlayback           { get; set; } = true;
        public bool IncludeSponsorBlockChapters{ get; set; } = true;

        // Demo mode (lets you test without a backend)
        public bool         DemoMode    { get; set; } = true;
        public List<string> DemoQueries { get; set; } = new() { "lofi", "coding music" };
    }
}
