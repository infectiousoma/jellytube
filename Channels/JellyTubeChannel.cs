using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization; // ← added
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers; // DynamicImageResponse
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

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

        // ====== MEDIA INFO (muxed-first with split fallback) ======
        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
        {
            var vid = id.StartsWith("vid:", StringComparison.Ordinal) ? id.Substring(4) : id;
            if (string.IsNullOrWhiteSpace(vid))
                return Array.Empty<MediaSourceInfo>();

            // 1) Try muxed via /resolve (so we know if the current URL is valid)
            try
            {
                var policy = Uri.EscapeDataString(Cfg.FormatPolicy ?? "h264_mp4");
                var resolveUrl = $"{BackendBase}/resolve?video_id={Uri.EscapeDataString(vid)}&policy={policy}";
                using var r = await Http.GetAsync(resolveUrl, ct);
                if (r.IsSuccessStatusCode)
                {
                    var txt = await r.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(txt);
                    if (doc.RootElement.TryGetProperty("kind", out var k) &&
                        k.ValueKind == JsonValueKind.String &&
                        string.Equals(k.GetString(), "muxed", StringComparison.OrdinalIgnoreCase))
                    {
                        var mediaUrl = $"{BackendBase}/play/{vid}?policy={policy}";
                        return new[]
                        {
                            new MediaSourceInfo
                            {
                                Id = vid,
                                Path = mediaUrl,
                                Protocol = MediaProtocol.Http,
                                Container = "mp4",
                                SupportsDirectPlay = true,
                                SupportsDirectStream = true,
                                SupportsTranscoding = true,
                                IsInfiniteStream = false,
                                RequiresOpening = false,
                                Name = "YouTube (muxed MP4)"
                            }
                        };
                    }
                }
            }
            catch
            {
                // ignore; fall through to split
            }

            // 2) Fallback to split: pick a good video-only itag and let the bridge live-remux to MP4
            try
            {
                var itag = await PickBestVideoOnlyItagAsync(vid, targetHeight: 720, ct);
                if (!string.IsNullOrEmpty(itag))
                {
                    var mediaUrl = $"{BackendBase}/play/{vid}?itag={Uri.EscapeDataString(itag)}";
                    return new[]
                    {
                        new MediaSourceInfo
                        {
                            Id = vid,
                            Path = mediaUrl,
                            Protocol = MediaProtocol.Http,
                            Container = "mp4", // bridge outputs fMP4 when remuxing
                            SupportsDirectPlay = true,
                            SupportsDirectStream = true,
                            SupportsTranscoding = true,
                            IsInfiniteStream = false,
                            RequiresOpening = false,
                            Name = $"YouTube 720p (split remux itag {itag})"
                        }
                    };
                }
            }
            catch
            {
                // ignore; last resort below
            }

            // 3) Last resort: generic best-MP4 policy
            return new[] { FallbackSource(id, vid) };
        }
        // ====== END MEDIA INFO ======

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

        // ---- robust int parser (handles null/number/string) ----
        private static int? TryInt(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v))
                return null;

            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    if (v.TryGetInt32(out var n32)) return n32;
                    if (v.TryGetInt64(out var n64) && n64 <= int.MaxValue) return (int)n64;
                    if (v.TryGetDouble(out var d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
                    return null;

                case JsonValueKind.String:
                    var s = v.GetString();
                    if (int.TryParse(s, out var n)) return n;
                    if (long.TryParse(s, out var l) && l <= int.MaxValue) return (int)l;
                    if (double.TryParse(s, out var dd) && dd >= int.MinValue && dd <= int.MaxValue) return (int)dd;
                    return null;

                default:
                    return null;
            }
        }

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
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                Name = "YouTube (best MP4)"
            };
        }

        // ---------- formats DTOs ----------
        private sealed class FormatsResponse
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("title")] public string? Title { get; set; }
            [JsonPropertyName("formats")] public List<FormatItem>? Formats { get; set; }
        }
        private sealed class FormatItem
        {
            [JsonPropertyName("itag")] public string? Itag { get; set; }
            [JsonPropertyName("ext")] public string? Ext { get; set; }
            [JsonPropertyName("has_video")] public bool HasVideo { get; set; }
            [JsonPropertyName("has_audio")] public bool HasAudio { get; set; }
            [JsonPropertyName("vcodec")] public string? Vcodec { get; set; }
            [JsonPropertyName("acodec")] public string? Acodec { get; set; }
            [JsonPropertyName("height")] public int? Height { get; set; }
            [JsonPropertyName("tbr")] public double? Tbr { get; set; }
            [JsonPropertyName("quality_label")] public string? QualityLabel { get; set; }
        }

        // ---------- pick best video-only itag for split-remux ----------
        private async Task<string?> PickBestVideoOnlyItagAsync(string videoId, int targetHeight, CancellationToken ct)
        {
            using var resp = await Http.GetAsync($"{BackendBase}/formats/{videoId}", ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);

            var fr = JsonSerializer.Deserialize<FormatsResponse>(json, J);
            var fmts = fr?.Formats ?? new List<FormatItem>();
            if (fmts.Count == 0) return null;

            var vids = fmts.Where(f => f.HasVideo && !f.HasAudio && f.Height.HasValue).ToList();
            if (vids.Count == 0) return null;

            IEnumerable<FormatItem> pool = vids.Where(f => f.Height == targetHeight);
            if (!pool.Any()) pool = vids.Where(f => f.Height <= 1080);
            if (!pool.Any()) pool = vids;

            var best = pool
                .OrderByDescending(f =>
                {
                    var v = (f.Vcodec ?? "").ToLowerInvariant();
                    var isAvc = v.Contains("avc");
                    var isMp4 = string.Equals(f.Ext, "mp4", StringComparison.OrdinalIgnoreCase);
                    var tbr = f.Tbr ?? 0;
                    var h = f.Height ?? 0;
                    return (isAvc ? 1_000_000 : 0) + (isMp4 ? 10_000 : 0) + (int)(tbr * 100) + h;
                })
                .FirstOrDefault();

            return best?.Itag;
        }

        private sealed record FavItem(string VideoId, string? Title, string? Channel);
        private sealed record SubItem(string ChannelId, string? Title, string? Handle);
    }
}
