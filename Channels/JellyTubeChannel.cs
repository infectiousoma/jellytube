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
// using Jellyfin.Data.Enums; // MediaStreamProtocol (not needed for 10.10.x fix)

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

        // Prefer the very robust 360p progressive by default
        private static readonly bool PreferStable360 = EnvBool("JELLYTUBE_PREFER_STABLE_360", true);

        // Force Jellyfin to transcode instead of remux/copy (works around ffmpeg exit 8 on some sources)
        private static readonly bool ForceTranscode = EnvBool("JELLYTUBE_FORCE_TRANSCODE", true);

        private static readonly bool ForceHlsTs = EnvBool("JELLYTUBE_HLS_TS", false);

        // Read >1 byte in preflight to avoid false positives (0 keeps old behavior)
        private static readonly long PreflightBytes = Math.Max(0, EnvInt("JELLYTUBE_PREFLIGHT_BYTES", 65536)); // 64KiB default

        // Policy handling
        private static readonly bool IncludePolicy = EnvBool("JELLYTUBE_INCLUDE_POLICY", true);
        private static readonly bool AllowPolicyOnSoft = EnvBool("JELLYTUBE_ALLOW_POLICY_SOFT", false);

        // Prefer policy if /formats fails, or even earlier if configured
        private static readonly bool PolicyFirst = EnvBool("JELLYTUBE_POLICY_FIRST", true);
        private static readonly bool PolicyOnFormatsError = EnvBool("JELLYTUBE_POLICY_ON_FORMATS_ERROR", true);

        // Block bad itags (defaults to block 22 which is flaky for many)
        private static readonly HashSet<string> BlockItags =
            new HashSet<string>(
                (Environment.GetEnvironmentVariable("JELLYTUBE_BLOCK_ITAGS") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

        static JellyTubeChannel()
        {
            if (!BlockItags.Contains("22"))
                BlockItags.Add("22");
        }

        // -------------------- channel metadata --------------------
        public string Name => "JellyTube";
        public string Description => "YouTube via JellyTube bridge";
        public string HomePageUrl => string.Empty;

        // bump this so Jellyfin refreshes cached rows
        public string DataVersion => "0.1.4";

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

            _log?.LogInformation("JT: mediaInfo id={Id} vid={Vid} base={Base} policy={Policy} progressiveOnly={ProgOnly} preflight={Preflight} preflightMax={PMax} preflightBytes={PBytes}",
                id, vid, BackendBase, PolicyRaw, ProgressiveOnly, !DisablePreflight, PreflightMax, PreflightBytes);

            var candidates = new List<(MediaSourceInfo ms, int prio)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool has18 = false, has22 = false;
            bool formatsErrored = false;

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

                    foreach (var f in fmts)
                    {
                        var itag = f.Itag; if (string.IsNullOrWhiteSpace(itag)) continue;
                        if (itag == "18") has18 = true; else if (itag == "22") has22 = true;
                        if (BlockItags.Contains(itag)) continue;

                        if (f.HasVideo && f.HasAudio)
                        {
                            var height = f.Height ?? 0;
                            var v = (f.Vcodec ?? "").ToLowerInvariant();
                            var isAvc = v.Contains("avc") || v.Contains("h264");
                            var isMp4 = string.Equals(f.Ext, "mp4", StringComparison.OrdinalIgnoreCase);

                            var prio = (itag == "22" ? 1_000_000 : 0) +
                                       (itag == "18" ? 500_000 : 0) +
                                       (isAvc ? 50_000 : 0) +
                                       (isMp4 ? 10_000 : 0) +
                                       height;

                            if (PreferStable360)
                            {
                                if (itag == "18") prio += 1_000_000;
                                if (itag == "22") prio -= 100_000;
                            }

                            var ms = NewMs($"{id}@{itag}",
                                $"{BackendBase}/play/{vid}?itag={Uri.EscapeDataString(itag)}",
                                f.Ext ?? "mp4",
                                $"{(height > 0 ? $"{height}p" : "auto")} progressive (itag {itag})",
                                "h264", "aac",
                                includeStreams: true);

                            if (seen.Add(ms.Id!))
                                candidates.Add((ms, prio));
                        }
                        else if (!ProgressiveOnly && f.HasVideo && !f.HasAudio)
                        {
                            var height = f.Height ?? 0;
                            var v = (f.Vcodec ?? "").ToLowerInvariant();
                            var isAvc = v.Contains("avc") || v.Contains("h264");
                            var isMp4 = string.Equals(f.Ext, "mp4", StringComparison.OrdinalIgnoreCase);

                            var prio = (isAvc ? 100_000 : 0) +
                                       (height == 720 ? 10_000 : 0) +
                                       (isMp4 ? 5_000 : 0) +
                                       height;

                            var ms = NewMs($"{id}@{itag}",
                                $"{BackendBase}/play/{vid}?itag={Uri.EscapeDataString(itag)}",
                                f.Ext ?? "mp4",
                                $"{(height > 0 ? $"{height}p" : "auto")} video-only (itag {itag})",
                                "h264", null,
                                includeStreams: false);

                            if (seen.Add(ms.Id!))
                                candidates.Add((ms, prio));
                        }
                    }
                }
                else
                {
                    formatsErrored = true;
                    _log?.LogWarning("JT: /formats non-success {Status} for vid={Vid}", (int)resp.StatusCode, vid);
                    if (PolicyOnFormatsError) { has18 = has22 = true; } // suppress forced itag fallbacks
                }
            }
            catch (Exception ex)
            {
                formatsErrored = true;
                _log?.LogWarning(ex, "JT: formats failed for vid={Vid}", vid);
                if (PolicyOnFormatsError) { has18 = has22 = true; } // suppress forced itag fallbacks
            }

            // 2) Ensure progressive fallbacks exist (dedup) — respect BlockItags
            void EnsureFallback(string itag, int basePrio, string label)
            {
                if (BlockItags.Contains(itag)) return;

                var ms = NewMs($"{id}@{itag}",
                    $"{BackendBase}/play/{vid}?itag={Uri.EscapeDataString(itag)}",
                    "mp4", $"{label} progressive (itag {itag})", "h264", "aac",
                    includeStreams: true);

                if (seen.Add(ms.Id!))
                    candidates.Add((ms, basePrio));
            }

            if (!has22) EnsureFallback("22", 900_000, "720p");
            if (!has18) EnsureFallback("18", PreferStable360 ? 1_100_000 : 800_000, "360p");

            // 3) Prepare policy candidate and preflight ordering
            MediaSourceInfo? policyMs = null;
            if (IncludePolicy)
            {
                policyMs = NewMs($"{id}@policy",
                    $"{BackendBase}/play/{vid}?policy={PolicyEnc}",
                    "mp4", $"Policy: {PolicyRaw}", "h264", null, includeStreams: false);
            }

            var ordered = candidates.OrderByDescending(t => t.prio).Select(t => t.ms).ToList();

            // Prefer policy very early when configured
            if (PolicyFirst && policyMs != null && !ordered.Any(ms => string.Equals(ms.Id, policyMs.Id, StringComparison.Ordinal)))
                ordered.Insert(0, policyMs);

            // If nothing at all, at least return policy so playback has a shot
            if (ordered.Count == 0 && policyMs != null)
                ordered.Add(policyMs);

            var playable = new List<MediaSourceInfo>();
            var usedSoftFallback = false;

            if (DisablePreflight)
            {
                _log?.LogInformation("JT: preflight disabled; returning top candidate(s)");
                playable.AddRange(ordered.Take(3));
            }
            else
            {
                var slice = ordered.Take(PreflightMax).ToList();
                foreach (var ms in slice)
                {
                    if (string.IsNullOrEmpty(ms.Path)) continue;

                    var ok = await IsUrlPlayableAsync(ms.Path, PreflightBytes, ct);
                    if (ok)
                    {
                        playable.Add(ms);
                    }
                    else
                    {
                        var hint = await QuickStatusAsync(ms.Path, ct);
                        if (!string.IsNullOrEmpty(hint))
                            _log?.LogWarning("JT: preflight fail path={Path} hint={Hint}", ms.Path, hint);
                    }
                }

                // SOFT PREFLIGHT: if none passed, prefer policy first, then everything else
                if (playable.Count == 0 && ordered.Count > 0)
                {
                    usedSoftFallback = true;

                    if (policyMs != null && !playable.Contains(policyMs))
                        playable.Add(policyMs);

                    foreach (var ms in ordered)
                        if (!playable.Contains(ms)) playable.Add(ms);
                }
            }

            // If preflight succeeded, append policy LAST (backup only)
            if (!usedSoftFallback && policyMs != null && !playable.Contains(policyMs))
                playable.Add(policyMs);

            // Summary log AFTER variables exist (and includes formatsErrored)
            _log?.LogInformation(
                "JT: candidates={C} playable={P} (formatsError={FE} preflight {Pref}/{Bytes}B max={Max} soft={Soft})",
                ordered.Count,
                playable.Count,
                formatsErrored,
                DisablePreflight ? "off" : "on",
                PreflightBytes,
                PreflightMax,
                usedSoftFallback);

            // 5) Final deterministic ordering: 18 -> 22 -> others -> policy
            playable = playable.OrderBy(StableRank).ToList();

            if (playable.Count > 0)
            {
                var first = playable[0];
                _log?.LogInformation("JT: first-choice id={Id} name={Name} path={Path}", first.Id, first.Name, first.Path);
            }

            return playable;
        }

        // Prefer stable ordering helper
        private static int StableRank(MediaSourceInfo ms)
        {
            var id = ms.Id ?? "";
            if (id.EndsWith("@18", StringComparison.Ordinal)) return 0;   // most reliable
            if (id.EndsWith("@22", StringComparison.Ordinal)) return 1;   // often flaky
            if (id.EndsWith("@policy", StringComparison.Ordinal)) return 9;
            return 2; // other itags in between
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

        // Lightweight status probe to help understand preflight failures
        private static async Task<string> QuickStatusAsync(string url, CancellationToken outerCt)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                // ask for a single byte; many origins omit useful headers on HEAD
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                var status = (int)resp.StatusCode;
                var ctHdr  = resp.Content?.Headers?.ContentType?.ToString();
                var len    = resp.Content?.Headers?.ContentLength;

                // Content-Range (prefer content header; fall back to raw header)
                string? cr = null;
                var crHeader = resp.Content?.Headers?.ContentRange;
                if (crHeader != null)
                    cr = crHeader.ToString();
                if (cr == null && resp.Headers.TryGetValues("Content-Range", out var crVals))
                    cr = crVals.FirstOrDefault();

                // Accept-Ranges
                string? ar = null;
                if (resp.Headers.TryGetValues("Accept-Ranges", out var arVals))
                    ar = arVals.FirstOrDefault();

                // drain a byte so the connection can be cleanly reused
                try
                {
                    var content = resp.Content;
                    if (content != null)
                    {
                        using var s = await content.ReadAsStreamAsync(cts.Token);
                        var one = new byte[1];
                        _ = await s.ReadAsync(one.AsMemory(0, 1), cts.Token);
                    }
                }
                catch
                {
                    // ignore – this is best-effort
                }

                return $"status={status} type={(ctHdr ?? "-")} len={(len?.ToString() ?? "-")} cr={(cr ?? "-")} ar={(ar ?? "-")}";
            }
            catch (Exception ex)
            {
                return $"status=ERR ex={ex.GetType().Name}";
            }
        }

        // Windowed favorites fetch that works with offset/limit or page-only backends
        private async Task<List<FavItem>> FetchFavoritesWindowAsync(int start, int takePlusOne, CancellationToken ct)
        {
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

            // Fallback: aggregate pages until we cover [start, start+takePlusOne)
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

        // Range GET preflight (optionally read N bytes instead of 1)
        private static async Task<bool> IsUrlPlayableAsync(string url, long bytesToRead, CancellationToken outerCt)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (bytesToRead <= 0)
                    req.Headers.Range = new RangeHeaderValue(0, 0);
                else
                    req.Headers.Range = new RangeHeaderValue(0, bytesToRead - 1);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(PreflightTimeout);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                var status = (int)resp.StatusCode;
                if (status != 200 && status != 206) return false;

                if (bytesToRead <= 0)
                {
                    var hasLen = resp.Content.Headers.ContentLength.GetValueOrDefault() > 0;
                    var chunked = resp.Headers.TransferEncodingChunked == true;
                    return status == 206 || hasLen || chunked;
                }

                // Stream-read up to bytesToRead; succeed if we read anything
                using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[8192];
                long remaining = bytesToRead;
                long readTotal = 0;
                while (remaining > 0)
                {
                    var want = (int)Math.Min(buf.Length, remaining);
                    var read = await stream.ReadAsync(buf.AsMemory(0, want), cts.Token);
                    if (read <= 0) break;
                    readTotal += read;
                    remaining -= read;
                }
                return readTotal > 0;
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

                // Force transcoding when requested (removes fragile remux paths causing ffmpeg code 8)
                SupportsDirectPlay   = !ForceTranscode,
                SupportsDirectStream = !ForceTranscode,
                SupportsTranscoding  = true,

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

            if (ForceHlsTs)
            {
                // Ask Jellyfin to use HLS with MPEG-TS segments instead of fMP4
                ms.TranscodingContainer = "ts";
                // Do NOT set TranscodingSubProtocol here; older Jellyfin builds lack the enum value.
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
