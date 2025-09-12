
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTube;

// Minimal channel to expose a root folder; in practice you'd query jellytube for configured sources
public class JellyTubeChannel : IChannel, IRequiresMediaInfoCallback
{
    public string Name => "JellyTube";
    public string Description => "Browse YouTube via jellytube";
    public string DataVersion => "1";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.General;

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        // TODO: call jellytube /channel, /playlist, /search to populate
        return Task.FromResult(new ChannelItemResult
        {
            Items = new List<ChannelItemInfo>(),
            TotalRecordCount = 0
        });
    }

    public Task<MediaSourceInfo> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        // Jellyfin calls this when it needs direct media info. We defer to the MediaSourceProvider typically.
        return Task.FromResult(new MediaSourceInfo());
    }
}
