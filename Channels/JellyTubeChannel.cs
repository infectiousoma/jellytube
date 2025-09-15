using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyTube.Channels
{
    public class JellyTubeChannel : IChannel, IRequiresMediaInfoCallback
    {
        public JellyTubeChannel()
        {
            System.Diagnostics.Debug.WriteLine("JellyTubeChannel constructor called");
        }

        public string Name => "JellyTube";
        public string Description => "Browse and play YouTube via JellyTube bridge (demo)";
        public string HomePageUrl => string.Empty;
        public string DataVersion => "0.0.1";
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public InternalChannelFeatures GetChannelFeatures()
        {
            System.Diagnostics.Debug.WriteLine("JellyTubeChannel.GetChannelFeatures called");
            return new InternalChannelFeatures
            {
                MaxPageSize = 50,
                ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip },
                MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
                SupportsSortOrderToggle = true,
                AutoRefreshLevels = 0,
                SupportsContentDownloading = false
            };
        }

        public bool IsEnabledFor(string userId)
        {
            System.Diagnostics.Debug.WriteLine($"JellyTubeChannel.IsEnabledFor called with userId: {userId}");
            return true;
        }

        public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
        {
            System.Diagnostics.Debug.WriteLine($"JellyTubeChannel.GetChannelItems called with FolderId: {query.FolderId}");

            if (string.IsNullOrEmpty(query.FolderId))
            {
                var rows = new List<ChannelItemInfo>
                {
                    new ChannelItemInfo
                    {
                        Id = "row:search",
                        Name = "Search",
                        Type = ChannelItemType.Folder,
                        MediaType = ChannelMediaType.Video,
                        FolderType = ChannelFolderType.Container
                    },
                    new ChannelItemInfo
                    {
                        Id = "row:trending",
                        Name = "Trending (Demo)",
                        Type = ChannelItemType.Folder,
                        MediaType = ChannelMediaType.Video,
                        FolderType = ChannelFolderType.Container
                    }
                };

                return Task.FromResult(new ChannelItemResult
                {
                    Items = rows.ToArray(),
                    TotalRecordCount = rows.Count
                });
            }

            if (query.FolderId == "row:trending")
            {
                var items = new List<ChannelItemInfo>
                {
                    new ChannelItemInfo
                    {
                        Id = "demo:bbb",
                        Name = "Big Buck Bunny (Demo)",
                        Type = ChannelItemType.Media,
                        ContentType = ChannelMediaContentType.Clip,
                        MediaType = ChannelMediaType.Video,
                        ImageUrl = "https://i.ytimg.com/vi/aqz-KE-bpKQ/hqdefault.jpg",
                        RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
                    },
                    new ChannelItemInfo
                    {
                        Id = "demo:ed",
                        Name = "Elephants Dream (Demo)",
                        Type = ChannelItemType.Media,
                        ContentType = ChannelMediaContentType.Clip,
                        MediaType = ChannelMediaType.Video,
                        ImageUrl = "https://i.ytimg.com/vi/eRsGyueVLvQ/hqdefault.jpg",
                        RunTimeTicks = TimeSpan.FromMinutes(11).Ticks
                    }
                };

                return Task.FromResult(new ChannelItemResult
                {
                    Items = items.ToArray(),
                    TotalRecordCount = items.Count
                });
            }

            return Task.FromResult(new ChannelItemResult
            {
                Items = Array.Empty<ChannelItemInfo>(),
                TotalRecordCount = 0
            });
        }

        public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary };

        public Task<DynamicImageResponse?> GetChannelImage(ImageType imageType, CancellationToken ct)
        {
            System.Diagnostics.Debug.WriteLine($"JellyTubeChannel.GetChannelImage called with imageType: {imageType}");
            return Task.FromResult<DynamicImageResponse?>(null);
        }

        public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
        {
            System.Diagnostics.Debug.WriteLine($"JellyTubeChannel.GetChannelItemMediaInfo called with id: {id}");
        
            // Map demo ids to HTTP MP4s
            string? url = id switch
            {
                "demo:bbb" => "https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/BigBuckBunny.mp4",
                "demo:ed"  => "https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ElephantsDream.mp4",
                _          => null
            };
        
            if (url is null)
                return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        
            // Tell Jellyfin “this is a remote MP4 with H.264 + AAC; please direct play; do NOT transcode”
            var src = new MediaSourceInfo
            {
                Id = id,                        // any opaque id for this media source
                Path = url,                     // direct HTTP URL
                Protocol = MediaProtocol.Http,
                Container = "mp4",
                IsRemote = true,
        
                // 🔑 The important bits to avoid HLS:
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = false,
                // Jellyfin sometimes flips to HLS if it can’t infer codecs/streams — be explicit:
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Codec = "h264",         // these demo files are H.264
                        // You can omit bitrate/width/height; Jellyfin can probe if needed
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Codec = "aac",
                        Channels = 2
                    }
                },
                // You can keep these as-is
                IsInfiniteStream = false,
                RequiresOpening = false
            };
        
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { src });
        }

    }
}
