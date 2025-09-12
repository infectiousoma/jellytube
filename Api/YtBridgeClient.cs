
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTube;

public class JellyTubeClient
{
    private readonly HttpClient _http;
    public string Base { get; }

    public JellyTubeClient(HttpClient http, string backendBaseUrl)
    {
        _http = http;
        Base = backendBaseUrl.TrimEnd('/');
    }

    public async Task<JsonDocument> ResolveAsync(string id, string policy, CancellationToken ct)
    {
        var url = $"{Base}/resolve?video_id={id}&policy={policy}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(s);
    }

    // TODO: add Search/Channel/Playlist/Item wrappers similarly
}
