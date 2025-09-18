using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers; // RangeHeaderValue
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers; // DynamicImageResponse
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTube.Channels
{
    public class JellyTubeChannel : IChannel, IRequiresMediaInfoCallback
    {
        // -------------------- plumbing & config --------------------
        private static readonly HttpClient Http = new HttpClient();
        private static readonly JsonSerializerOptions J = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private readonly ILogger<JellyTubeChannel>? _log;

        public JellyTubeChannel(ILogger<JellyTubeChannel>? log = null) => _log = log;

        private static PluginConfiguration Cfg => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // Effective config with ENV fallbacks, then defaults.
        private static string BackendBaseRaw =>
            Environment.GetEnvironmentVariable("JELLYTUBE_BACKEND")
            ?? Cfg.BackendBaseUrl
            ?? "http://localhost:8080";

        private static string PolicyRaw =>
            Environment.GetEnvironmentVariable("JELLYTUBE_POLICY")
            ?? Cfg.FormatPolicy
            ?? "h264_mp4";

        private static string BackendBase => BackendBaseRaw.TrimEnd('/');
        private static string PolicyEnc => Uri.EscapeDataString(PolicyRaw);

        // -------- Optional timeouts / feature flags (ENV) --------
        // General: JELLYTUBE_HTTP_TIMEOUT_MS
        // Specific: JELLYTUBE_LIST_TIMEOUT_MS, JELLYTUBE_FORMATS_TIMEOUT_MS, JELLYTUBE_PREFLIGHT_TIMEOUT_MS
        // Flags:
        //   JELLYTUBE_PROGRESSIVE_ONLY (=true) -> ignore video-only itags
        //   JELLYTUBE_DISABLE_PREFLIGHT (=false) -> skip Range checks
        //   JELLYTUBE_PREFLIGHT_MAX (=5) -> cap how many candidates to preflight
        //   JELLYTUBE_MAX_PAGE_SIZE (=500) -> UI paging hint for Jellyfin
        private static bool EnvBool(string name, bool def = false)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v)) return def;
            return v.Equals("1") || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        private static int EnvInt(string name, int def)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return int.TryParse(v, out var n) && n > 0 ? n : def;
        }

        private static TimeSpan ParseTimeout(string? msOrSec, TimeSpan fallback)
        {
            if (string.IsNullOrWhiteSpace(msOrSec)) return fallback;
            if (int.TryParse(msOrSec, out var ms) && ms > 0 && ms <= 300_000) return TimeSpan.FromMilliseconds(ms);
            if (double.TryParse(msOrSec, out var sec) && sec > 0 && sec <= 300) return TimeSpan.FromSeconds(sec);
            return fallback;
        }
        private static TimeSpan EnvTimeout(string specific, string general, TimeSpan fallback)
        {
            var generalVal = Environment.GetEnvironmentVariable(general);
            var specificVal = Environment.GetEnvironmentVariable(specific);
            var baseVal = ParseTimeout(generalVal, fallback);
            return ParseTimeout(specificVal, baseVal);
        }

        private static readonly TimeSpan ListTimeout =
            EnvTimeout("JELLYTUBE_LIST_TIMEOUT_MS", "JELLYTUBE_HTTP_TIMEOUT_MS", TimeSpan.FromSeconds(10));

        private static readonly TimeSpan FormatsTimeout =
            EnvTimeout("JELLYTUBE_FORMATS_TIMEOUT_MS", "JELLYTUBE_HTTP_TIMEOUT_MS", TimeSpan.FromSeconds(20));

        private static readonly TimeSpan PreflightTimeout =
            EnvTimeout("JELLYTUBE_PREFLIGHT_TIMEOUT_MS", "JELLYTUBE_HTTP_TIMEOUT_MS", TimeSpan.FromSeconds(4));

        private static readonly bool ProgressiveOnly = EnvBool("JELLYTUBE_PROGRESSIVE_ONLY", true);
        private static readonly bool DisablePreflight = EnvBool("JELLYTUBE_DISABLE_PREFLIGHT", false);
        private static readonly int PreflightMax = Math.Max(1, EnvInt("JELLYTUBE_PREFLIGHT_MAX", 5));
        private static readonly int UiMaxPageSize = Math.Min(Math.Max(50, EnvInt("JELLYTUBE_MAX_PAGE_SIZE", 500)), 2000);

        // -------------------- channel metadata --------------------
        public string Name => "JellyTube";
        public string Description => "YouTube via JellyTube bridge";
        public string HomePageUrl => string.Empty;

        // bump this so Jellyfin refreshes cached rows
        public string DataVersion => "0.0.6";

        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public InternalChannelFeatures GetChannelFeatures() => new InternalChannelFeatures
        {
            MaxPageSize = UiMaxPageSize,
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
            SupportsSortOrderToggle = true,
            AutoRefreshLevels = 0,
            SupportsContentDownloading = false
        };

        public bool IsEnabledFor(string userId) => true;

        // -------------------- LISTING --------------------
        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
        {
            _log?.LogInformation("JT: GetChannelItems folder={FolderId} start={Start} limit={Limit} base={Base} policy={Policy} pagesize={PageSize}",
                query.FolderId, query.StartIndex, query.Limit, BackendBase, PolicyRaw, UiMaxPageSize);

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
                var start = (int)(query.StartIndex ?? 0);
                var take = (int)(query.Limit ?? UiMaxPageSize);
                if (take <= 0) take = UiMaxPageSize;

                // Fetch take+1 to detect if there's another page
                var window = await FetchFavoritesWindowAsync(start, take + 1, ct);
                var hasMore = window.Count > take;
                if (hasMore) window.RemoveAt(window.Count - 1);

                _log?.LogInformation("JT: favorites page start={Start} take={Take} returned={Returned} hasMore={HasMore}",
                    start, take, window.Count, hasMore);

                var items = window.Select(f => new ChannelItemInfo
                {
                    Id = $"vid:{f.VideoId}",
                    Name = f.Title ?? f.VideoId,
                    Type = ChannelItemType.Media,
                    ContentType = ChannelMediaContentType.Clip,
                    MediaType = ChannelMediaType.Video,
                    ImageUrl = Thumb(f.VideoId)
                }).ToList();

                return new ChannelItemResult
                {
                    Items = items.ToArray(),
                    // Hint that there may be more pages:
                    TotalRecordCount = start + items.Count + (hasMore ? 1 : 0)
                };
            }

            if (query.FolderId == "row:subs")
            {
                var all = await FetchSubsAsync(ct);
                _log?.LogInformation("JT: subs total={Count}", all.Count);

                return PageMap(all, query,
                    s => new ChannelItemInfo
                    {
                        Id = $"chan:{s.ChannelId}",
                        Name = s.Title ?? s.ChannelId,
                        Type = ChannelItemType.Folder,
                        MediaType = ChannelMediaType.Video,
                        FolderType = ChannelFolderType.Container,
                        ImageUrl = $"https://yt3.googleusercontent.com/ytc/{s.ChannelId}=s512-c-k-c0x00ffffff-no-rj"
                    });
            }

            if (query.FolderId.StartsWith("chan:", StringComparison.Ordinal))
            {
                var chId = query.FolderId.Substring("chan:".Length);
                var all = await FetchChannelItemsAsync(chId, ct);
                _log?.LogInformation("JT: channel {Chan} items total={Count}", chId, all.Count);

                return PageMap(all, query, el =>
                {
                    var vid = Prop(el, "videoId") ?? Prop(el, "id");
                    var title = Prop(el, "title") ?? vid;
                    var dur = TryInt(el, "lengthSeconds");

                    return string.IsNullOrEmpty(vid) ? null : new ChannelItemInfo
                    {
                        Id = $"vid:{vid}",
                        Name = title!,
                        Type = ChannelItemType.Media,
                        ContentType = ChannelMediaContentType.Clip,
                        MediaType = ChannelMediaType.Video,
                        ImageUrl = Thumb(vid!),
                        RunTimeTicks = dur.HasValue ? TimeSpan.FromSeconds(dur.Value).Ticks : null
                    };
                });
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

        public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();
        public Task<DynamicImageResponse> GetChannelImage(ImageType imageType, CancellationToken ct)
            => Task.FromResult<DynamicImageResponse>(null!);

        // -------------------- MEDIA INFO (formats-first + progressive + preflight) --------------------
        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
        {
            var vid = id.StartsWith("vid:", StringComparison.Ordinal) ? id.Substring(4) : id;
            if (string.IsNullOrWhiteSpace(vid))
                return Array.Empty<MediaSourceInfo>();

            _log?.LogInformation("JT: mediaInfo id={Id} vid={Vid} base={Base} policy={Policy} progressiveOnly={ProgOnly} preflight={Preflight} preflightMax={PMax}",
                id, vid, BackendBase, PolicyRaw, ProgressiveOnly, !DisablePreflight, PreflightMax);

            var candidates = new List<(MediaSourceInfo ms, int prio)>();

            // 1) Formats
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(FormatsTimeout);
                using var resp = await Http.GetAsync($"{BackendBase}/formats/{vid}", cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(cts.Token);
                    var fr = JsonSerializer.Deserialize<FormatsResponse>(json, J);
                    var fmts = fr?.Formats ?? new List<FormatItem>();

                    IEnumerable<FormatItem> withAV = fmts.Where(f => f.HasVideo && f.HasAudio);
                    IEnumerable<FormatItem> videoOnly = fmts.Where(f => f.HasVideo && !f.HasAudio);

                    // Progressive (A/V) first
                    foreach (var f in withAV)
                    {
                        var itag = f.Itag; if (string.IsNullOrWhiteSpace(itag)) continue;
                        var height = f.Height ?? 0;
                        var v = (f.Vcodec ?? "").ToLowerInvariant();
                        var isAvc = v.Contains("avc") || v.Contains("h264");
                        var isMp4 = string.Equals(f.Ext, "mp4", StringComparison.OrdinalIgnoreCase);

                        var prio = (itag == "22" ? 1_000_000 : 0) +
                                   (itag == "18" ? 500_000 : 0) +
                                   (isAvc ? 50_000 : 0) +
                                   (isMp4 ? 10_000 : 0) +
                                   height;

                        candidates.Add((NewMs($"{id}@{itag}",
                            $"{BackendBase}/play/{vid}?itag={Uri.EscapeDataString(itag)}",
                            f.Ext ?? "mp4",
                            $"{(height > 0 ? $"{height}p" : "auto")} progressive (itag {itag})",
                            "h264", "aac"), prio));
                    }

                    // Optionally include video-only (lets Jellyfin transcode, but can be flaky)
                    if (!ProgressiveOnly)
                    {
                        foreach (var f in videoOnly)
                        {
                            var itag = f.Itag; if (string.IsNullOrWhiteSpace(itag)) continue;
                            var height = f.Height ?? 0;
                            var v = (f.Vcodec ?? "").ToLowerInvariant();
                            var isAvc = v.Contains("avc") || v.Contains("h264");
                            var isMp4 = string.Equals(f.Ext, "mp4", StringComparison.OrdinalIgnoreCase);

                            var prio = (isAvc ? 100_000 : 0) +
                                       (height == 720 ? 10_000 : 0) +
                                       (isMp4 ? 5_000 : 0) +
                                       height;

                            candidates.Add((NewMs($"{id}@{itag}",
                                $"{BackendBase}/play/{vid}?itag={Uri.EscapeDataString(itag)}",
                                f.Ext ?? "mp4",
                                $"{(height > 0 ? $"{height}p" : "auto")} video-only (itag {itag})",
                                "h264", null), prio));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "JT: formats failed for vid={Vid}", vid);
            }

            // 2) Progressive fallbacks if formats missing
            if (candidates.Count == 0)
            {
                candidates.Add((NewMs($"{id}@22", $"{BackendBase}/play/{vid}?itag=22",
                    "mp4", "720p progressive (itag 22)", "h264", "aac"), 900_000));

                candidates.Add((NewMs($"{id}@18", $"{BackendBase}/play/{vid}?itag=18",
                    "mp4", "360p progressive (itag 18)", "h264", "aac"), 800_000));
            }

            // 3) Policy last resort — do NOT advertise streams (avoids ffmpeg -map errors on video-only redirects)
            var policyMs = NewMs($"{id}@policy",
                $"{BackendBase}/play/{vid}?policy={PolicyEnc}",
                "mp4", $"Policy: {PolicyRaw}", "h264", null, includeStreams: false);
            candidates.Add((policyMs, 100));

            // 4) Preflight (Range: 0-0) a few best candidates
            var ordered = candidates.OrderByDescending(t => t.prio).Select(t => t.ms).ToList();
            List<MediaSourceInfo> playable;

            if (DisablePreflight)
            {
                _log?.LogInformation("JT: preflight disabled; returning top candidate(s)");
                playable = ordered.Take(3).ToList();
            }
            else
            {
                playable = new List<MediaSourceInfo>();
                var slice = ordered.Take(PreflightMax).ToList();
                foreach (var ms in slice)
                {
                    if (!string.IsNullOrEmpty(ms.Path) && await IsUrlPlayableAsync(ms.Path, ct))
                        playable.Add(ms);
                }
            }

            if (playable.Count == 0) playable.Add(policyMs);
            _log?.LogInformation("JT: candidates={C} playable={P} (preflight {Pref} max={Max})",
                ordered.Count, playable.Count, DisablePreflight ? "off" : "on", PreflightMax);

            return playable;
        }

        // -------------------- helpers (HTTP) --------------------
        private static async Task<string> GetStringWithTimeoutAsync(string url, TimeSpan timeout, CancellationToken outerCt)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            cts.CancelAfter(timeout);
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            resp.EnsureSuccessStatusCode();
            return body;
        }

        // Windowed favorites fetch that works with offset/limit or page-only backends
        private async Task<List<FavItem>> FetchFavoritesWindowAsync(int start, int takePlusOne, CancellationToken ct)
        {
            // 1) Try common offset/limit styles
            var probes = new[]
            {
                $"{BackendBase}/favorites?offset={start}&limit={takePlusOne}",
                $"{BackendBase}/favorites?limit={takePlusOne}&offset={start}",
                $"{BackendBase}/favorites?page={(start / Math.Max(1, takePlusOne - 1)) + 1}&page_size={takePlusOne}",
                $"{BackendBase}/favorites?page={(start / Math.Max(1, takePlusOne - 1)) + 1}&per_page={takePlusOne}"
            };

            foreach (var url in probes)
            {
                try
                {
                    var body = await GetStringWithTimeoutAsync(url, ListTimeout, ct);
                    var favs = JsonSerializer.Deserialize<List<FavItem>>(body, J) ?? new();
                    _log?.LogInformation("JT: favorites probe {Url} -> {Count}", url, favs.Count);
                    if (start == 0 && favs.Count > 0) return favs;
                    if (favs.Count > 0 && favs.Count <= takePlusOne) return favs;
                }
                catch (Exception ex)
                {
                    _log?.LogDebug(ex, "JT: favorites probe failed {Url}", url);
                }
            }

            // 2) Fallback: aggregate pages until we cover [start, start+takePlusOne)
            var acc = new List<FavItem>();
            for (var page = 1; acc.Count < start + takePlusOne && page <= 200; page++)
            {
                var url = $"{BackendBase}/favorites?page={page}";
                try
                {
                    var body = await GetStringWithTimeoutAsync(url, ListTimeout, ct);
                    var batch = JsonSerializer.Deserialize<List<FavItem>>(body, J) ?? new();
                    _log?.LogInformation("JT: favorites page {Page} -> {Count}", page, batch.Count);
                    if (batch.Count == 0) break;
                    acc.AddRange(batch);
                    if (acc.Count > 5000) break; // guard rail
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "JT: favorites page fetch failed {Url}", url);
                    break;
                }
            }

            if (acc.Count <= start) return new();
            return acc.Skip(start).Take(takePlusOne).ToList();
        }

        private async Task<List<SubItem>> FetchSubsAsync(CancellationToken ct)
        {
            var url = $"{BackendBase}/subscriptions";
            try
            {
                var body = await GetStringWithTimeoutAsync(url, ListTimeout, ct);
                return JsonSerializer.Deserialize<List<SubItem>>(body, J) ?? new();
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "JT: subs fetch failed {Url}", url);
                return new();
            }
        }

        private async Task<List<JsonElement>> FetchChannelItemsAsync(string channelId, CancellationToken ct)
        {
            var url = $"{BackendBase}/channel/{channelId}?page=1";
            try
            {
                var body = await GetStringWithTimeoutAsync(url, ListTimeout, ct);
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().ToList()
                    : new List<JsonElement>();
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "JT: channel fetch failed {Url}", url);
                return new();
            }
        }

        // Range GET preflight
        private static async Task<bool> IsUrlPlayableAsync(string url, CancellationToken outerCt)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(0, 0);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(PreflightTimeout);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                var status = (int)resp.StatusCode;
                if (status == 206) return true;
                if (status == 200)
                {
                    var hasLen = resp.Content.Headers.ContentLength.GetValueOrDefault() > 0;
                    var chunked = resp.Headers.TransferEncodingChunked == true;
                    return hasLen || chunked;
                }
                return false;
            }
            catch { return false; }
        }

        // -------------------- helpers (mapping, small utils) --------------------
        private static ChannelItemResult PageMap<T>(IList<T> all, InternalChannelItemQuery query, Func<T, ChannelItemInfo?> map)
        {
            var start = (int)(query.StartIndex ?? 0);
            var take = (int)(query.Limit ?? 50);
            if (start < 0) start = 0;
            if (take <= 0) take = 50;

            var page = all.Skip(start).Take(take)
                .Select(map)
                .Where(i => i != null)
                .Cast<ChannelItemInfo>()
                .ToList();

            return new ChannelItemResult
            {
                Items = page.ToArray(),
                TotalRecordCount = all.Count
            };
        }

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
        {
            if (!el.TryGetProperty(name, out var v)) return null;
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

        private static MediaSourceInfo NewMs(
            string id,
            string url,
            string container,
            string name,
            string? videoCodec,
            string? audioCodec,
            bool includeStreams = true)
        {
            var ms = new MediaSourceInfo
            {
                Id = id,
                Path = url,
                Protocol = MediaProtocol.Http,
                Container = container,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                IsInfiniteStream = false,
                RequiresOpening = false,
                Name = $"YouTube {name}"
            };

            if (includeStreams)
            {
                var streams = new List<MediaStream>
                {
                    new MediaStream { Type = MediaStreamType.Video, Codec = videoCodec ?? "h264", Index = 0 }
                };
                if (!string.IsNullOrEmpty(audioCodec))
                    streams.Add(new MediaStream { Type = MediaStreamType.Audio, Codec = audioCodec!, Index = 1 });

                ms.MediaStreams = streams;
            }

            return ms;
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

        private sealed record FavItem(string VideoId, string? Title, string? Channel);
        private sealed record SubItem(string ChannelId, string? Title, string? Handle);
    }
}
