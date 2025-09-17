using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions; // ← added
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Controller.Providers; // DynamicImageResponse

namespace Jellyfin.Plugin.JellyTube.Channels
{
    public class JellyTubeChannel : IChannel, IRequiresMediaInfoCallback
    {
        private static readonly HttpClient Http = new HttpClient();
        private static readonly JsonSerializerOptions J = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static PluginConfiguration Cfg => Plugin.Instance?.Configuration ?? new PluginConfiguration();
        private static string BackendBase => (Cfg.BackendBaseUrl ?? "http://localhost:8080").TrimEnd('/');

        public string Name => "JellyTube";
        public string Description => "YouTube via JellyTube bridge";
        public string HomePageUrl => string.Empty;
        public string DataVersion => "0.0.2";
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public InternalChannelFeatures GetChannelFeatures() => new InternalChannelFeatures
        {
            MaxPageSize = 50,
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
            SupportsSortOrderToggle = true,
            AutoRefreshLevels = 0,
            SupportsContentDownloading = false
        };

        public bool IsEnabledFor(string userId) => true;

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(query.FolderId))
            {
                var rows = new List<ChannelItemInfo>
                {
                    Row("row:search", "Search"),
                    Row("row:favorites", "Favorites"),
                    Row("row:subs", "Subscriptions"),
                    Row("row:trending", "Trending (Demo)")
                };
                return Result(rows);
            }

            if (query.FolderId == "row:favorites")
            {
                var url = $"{BackendBase}/favorites";
                using var resp = await Http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct);
                var favs = JsonSerializer.Deserialize<List<FavItem>>(body, J) ?? new();

                var items = favs.Select(f => new ChannelItemInfo
                {
                    Id = $"vid:{f.VideoId}",
                    Name = f.Title ?? f.VideoId,
                    Type = ChannelItemType.Media,
                    ContentType = ChannelMediaContentType.Clip,
                    MediaType = ChannelMediaType.Video,
                    ImageUrl = Thumb(f.VideoId),
                }).ToList();

                return Result(items);
            }

            if (query.FolderId == "row:subs")
            {
                var url = $"{BackendBase}/subscriptions";
                using var resp = await Http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct);
                var subs = JsonSerializer.Deserialize<List<SubItem>>(body, J) ?? new();

                var items = subs.Select(s => new ChannelItemInfo
                {
                    Id = $"chan:{s.ChannelId}",
                    Name = s.Title ?? s.ChannelId,
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video,
                    FolderType = ChannelFolderType.Container,
                    ImageUrl = $"https://yt3.googleusercontent.com/ytc/{s.ChannelId}=s512-c-k-c0x00ffffff-no-rj"
                }).ToList();

                return Result(items);
            }

            if (query.FolderId.StartsWith("chan:", StringComparison.Ordinal))
            {
                var chId = query.FolderId.Substring("chan:".Length);
                var url = $"{BackendBase}/channel/{chId}?page=1";
                using var resp = await Http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(body);
                var list = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().ToArray()
                    : Array.Empty<JsonElement>();

                var items = new List<ChannelItemInfo>();
                foreach (var el in list)
                {
                    var vid = Prop(el, "videoId") ?? Prop(el, "id");
                    var title = Prop(el, "title") ?? vid;
                    var dur = TryInt(el, "lengthSeconds");
                    if (!string.IsNullOrEmpty(vid))
                    {
                        items.Add(new ChannelItemInfo
                        {
                            Id = $"vid:{vid}",
                            Name = title!,
                            Type = ChannelItemType.Media,
                            ContentType = ChannelMediaContentType.Clip,
                            MediaType = ChannelMediaType.Video,
                            ImageUrl = Thumb(vid!),
                            RunTimeTicks = dur.HasValue ? TimeSpan.FromSeconds(dur.Value).Ticks : null
                        });
                    }
                }

                return Result(items);
            }

            if (query.FolderId == "row:trending")
            {
                var items = new List<ChannelItemInfo>
                {
                    new ChannelItemInfo
                    {
                        Id = "vid:aqz-KE-bpKQ",
                        Name = "Big Buck Bunny (Demo)",
                        Type = ChannelItemType.Media,
                        ContentType = ChannelMediaContentType.Clip,
                        MediaType = ChannelMediaType.Video,
                        ImageUrl = Thumb("aqz-KE-bpKQ"),
                        RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
                    },
                    new ChannelItemInfo
                    {
                        Id = "vid:eRsGyueVLvQ",
                        Name = "Elephants Dream (Demo)",
                        Type = ChannelItemType.Media,
                        ContentType = ChannelMediaContentType.Clip,
                        MediaType = ChannelMediaType.Video,
                        ImageUrl = Thumb("eRsGyueVLvQ"),
                        RunTimeTicks = TimeSpan.FromMinutes(11).Ticks
                    }
                };
                return Result(items);
            }

            return Result(Array.Empty<ChannelItemInfo>());
        }

        public IEnumerable<ImageType> GetSupportedChannelImages()
            => Array.Empty<ImageType>();

        public Task<DynamicImageResponse> GetChannelImage(ImageType imageType, CancellationToken ct)
        {
            return Task.FromResult<DynamicImageResponse>(null!);
        }

        // ====== UPDATED METHOD ======
        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
        {
            var vid = id.StartsWith("vid:", StringComparison.Ordinal) ? id.Substring(4) : id;
            if (string.IsNullOrWhiteSpace(vid))
                return Array.Empty<MediaSourceInfo>();

            // Ask the bridge what formats exist
            string json;
            try
            {
                using var resp = await Http.GetAsync($"{BackendBase}/formats/{vid}", ct);
                resp.EnsureSuccessStatusCode();
                json = await resp.Content.ReadAsStringAsync(ct);
            }
            catch
            {
                // Bridge unreachable → fallback single source using policy
                return new[] { FallbackSource(id, vid) };
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("formats", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new[] { FallbackSource(id, vid) };

            var sources = new List<(MediaSourceInfo ms, int height, bool isItag18)>();

            foreach (var f in arr.EnumerateArray())
            {
                var itag = Prop(f, "itag");
                if (string.IsNullOrWhiteSpace(itag))
                    continue;

                var hasVideo = Bool(f, "has_video");
                var hasAudio = Bool(f, "has_audio");
                var ext = Prop(f, "ext") ?? "mp4";
                var height = TryInt(f, "height") ?? 0;

                // Skip audio-only rows in UI
                if (!hasVideo && hasAudio)
                    continue;

                var flavor = hasVideo && hasAudio ? "progressive" : "video-only";
                var labelHeight = height > 0 ? $"{height}p" : "auto";
                var name = $"YouTube {labelHeight} {flavor} (itag {itag})";

                var ms = new MediaSourceInfo
                {
                    Id = $"{id}@{itag}",
                    Path = $"{BackendBase}/play/{vid}?itag={Uri.EscapeDataString(itag)}",
                    Protocol = MediaProtocol.Http,
                    Container = ext,
                    SupportsDirectPlay = true,   // progressive direct; split remux still ok
                    SupportsTranscoding = true,
                    Name = name
                };

                sources.Add((ms, height, itag == "18"));
            }

            if (sources.Count == 0)
                return new[] { FallbackSource(id, vid) };

            // Prefer itag 18 first; then by height desc; then progressive before video-only (by name hint)
            var ordered = sources
                .OrderByDescending(t => t.isItag18)
                .ThenByDescending(t => t.height)
                .ThenByDescending(t => (t.ms.Name?.Contains("progressive") ?? false))
                .Select(t => t.ms)
                .ToList();

            return ordered;
        }
        // ====== END UPDATED METHOD ======

        // ---------- helpers ----------

        private static ChannelItemInfo Row(string id, string name) => new ChannelItemInfo
        {
            Id = id,
            Name = name,
            Type = ChannelItemType.Folder,
            MediaType = ChannelMediaType.Video,
            FolderType = ChannelFolderType.Container
        };

        private static ChannelItemResult Result(IList<ChannelItemInfo> items) => new ChannelItemResult
        {
            Items = items.ToArray(),
            TotalRecordCount = items.Count
        };

        private static string Thumb(string videoId) => $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

        private static string? Prop(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;

        private static int? TryInt(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : (int?)null;

        private static bool Bool(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
            }
            return false;
        }

        private static MediaSourceInfo FallbackSource(string fullId, string vid)
        {
            var policy = Uri.EscapeDataString(Cfg.FormatPolicy ?? "h264_mp4");
            return new MediaSourceInfo
            {
                Id = fullId,
                Path = $"{BackendBase}/play/{vid}?policy={policy}",
                Protocol = MediaProtocol.Http,
                Container = "mp4",
                SupportsDirectPlay = true,
                SupportsTranscoding = true,
                Name = "YouTube (best MP4)"
            };
        }

        private sealed record FavItem(string VideoId, string? Title, string? Channel);
        private sealed record SubItem(string ChannelId, string? Title, string? Handle);
    }
}
