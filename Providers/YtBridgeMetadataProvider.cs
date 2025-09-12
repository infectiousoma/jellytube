
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTube;

public class JellyTubeMetadataProvider : IRemoteMetadataProvider<Video, VideoInfo>
{
    public string Name => "JellyTube";

    public Task<MetadataResult<Video>> GetMetadata(VideoInfo info, CancellationToken cancellationToken)
    {
        // TODO: call jellytube /item/{id} to fill metadata fields.
        var result = new MetadataResult<Video> { HasMetadata = false };
        return Task.FromResult(result);
    }

    public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        => Task.FromResult<HttpResponseInfo>(null);
}
