
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTube;

public class JellyTubeMediaSourceProvider : IMediaSourceProvider
{
    private readonly YtBridgeClient _client;
    private readonly PluginConfiguration _cfg;

    public JellyTubeMediaSourceProvider(YtBridgeClient client, PluginConfiguration cfg)
    {
        _client = client;
        _cfg = cfg;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken ct)
    {
        var id = item.GetProviderId("YouTube");
        if (string.IsNullOrWhiteSpace(id))
        {
            return new List<MediaSourceInfo>();
        }

        using var resolved = await _client.ResolveAsync(id, _cfg.FormatPolicy, ct).ConfigureAwait(false);
        var root = resolved.RootElement;

        var path = _cfg.UseProxyPlayback
            ? $"{_client.Base}/play/{id}?policy={_cfg.FormatPolicy}"
            : root.GetProperty("url").GetString();

        var container = "mp4";
        if (root.TryGetProperty("container", out var c))
            container = c.GetString() ?? "mp4";

        return new[] {
            new MediaSourceInfo {
                Path = path,
                Protocol = MediaProtocol.Http,
                Container = container,
                SupportsDirectPlay = true,
                RequiresOpening = false
            }
        };
    }
}
