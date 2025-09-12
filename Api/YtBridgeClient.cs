
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTube;

public class YtBridgeClient
{
    private readonly HttpClient _http;
    public string Base { get; }
    private readonly bool _demoMode;
    private readonly IEnumerable<string> _demoQueries;

    public YtBridgeClient(HttpClient http, PluginConfiguration cfg)
    {
        _http = http;
        Base = cfg.BackendBaseUrl.TrimEnd('/');
        _demoMode = cfg.DemoMode;
        _demoQueries = cfg.DemoQueries ?? new List<string>();
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(s);
    }

    public async Task<JsonDocument> ResolveAsync(string id, string policy, CancellationToken ct)
    {
        if (_demoMode)
        {
            var fake = $"{{\"id\":\"{id}\",\"title\":\"Demo {id}\",\"duration\":1800,\"url\":\"https://example.com/{id}.mp4\",\"container\":\"mp4\"}}";
            return JsonDocument.Parse(fake);
        }
        var url = $"{Base}/resolve?video_id={id}&policy={policy}";
        return await GetAsync(url, ct).ConfigureAwait(false);
    }

    public async Task<JsonDocument> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (_demoMode)
        {
            var fake = $"[{{\"type\":\"video\",\"title\":\"Demo - {query} #1\",\"videoId\":\"dQw4w9WgXcQ\"}},{{\"type\":\"video\",\"title\":\"Demo - {query} #2\",\"videoId\":\"5qap5aO4i9A\"}}]";
            return JsonDocument.Parse(fake);
        }
        var url = $"{Base}/search?q={Uri.EscapeDataString(query)}&type=video&limit={limit}";
        return await GetAsync(url, ct).ConfigureAwait(false);
    }

    public async Task<JsonDocument> ChannelAsync(string channelId, int page, CancellationToken ct)
    {
        if (_demoMode)
        {
            var fake = "[{\"title\":\"Channel Demo #1\",\"videoId\":\"dQw4w9WgXcQ\"},{\"title\":\"Channel Demo #2\",\"videoId\":\"5qap5aO4i9A\"}]";
            return JsonDocument.Parse(fake);
        }
        var url = $"{Base}/channel/{channelId}?page={page}";
        return await GetAsync(url, ct).ConfigureAwait(false);
    }

    public async Task<JsonDocument> PlaylistAsync(string playlistId, int page, CancellationToken ct)
    {
        if (_demoMode)
        {
            var fake = "[{\"title\":\"Playlist Demo #1\",\"videoId\":\"dQw4w9WgXcQ\"},{\"title\":\"Playlist Demo #2\",\"videoId\":\"5qap5aO4i9A\"}]";
            return JsonDocument.Parse(fake);
        }
        var url = $"{Base}/playlist/{playlistId}?page={page}";
        return await GetAsync(url, ct).ConfigureAwait(false);
    }

    public async Task<JsonDocument> ItemAsync(string videoId, CancellationToken ct)
    {
        if (_demoMode)
        {
            var fake = $"{{\"videoId\":\"{videoId}\",\"title\":\"Demo Item {videoId}\",\"lengthSeconds\":1800,\"author\":\"Demo Channel\"}}";
            return JsonDocument.Parse(fake);
        }
        var url = $"{Base}/item/{videoId}";
        return await GetAsync(url, ct).ConfigureAwait(false);
    }

    public IEnumerable<string> GetDemoQueries() => _demoQueries;
}
