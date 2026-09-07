using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MoyuPopup.Core;

/// <summary>
/// 哔哩哔哩列表源：两类能力（设计书 7.3 思路）。
/// 1. 随机视频（免登录）：取官方「热门」公开接口，随机抽样 —— 无需账号，即点即取。
/// 2. 登录列表（登录态）：观看历史 / 稍后再看，通过 Cookie 调用官方接口。
/// 与 URL 解析适配器解耦——适配器管“单条链接怎么播”，列表源管“怎么拉一整个列表”。
/// 接口字段版本敏感，接口改版需跟进；失败仅影响本平台，不牵连其它源。
/// </summary>
public sealed class BilibiliListSource : IPlatformListSource
{
    // 随机分区 id 池：随机取一个分区排行，保证每次拉取内容不同（分区改动需跟进）
    private static readonly int[] RandomRids = { 0, 1, 3, 4, 5, 36, 119, 129, 155, 160, 181, 188, 201, 211, 223, 228 };

    // 登录态接口（需 SESSDATA）；history/cursor 首页无需 business 过滤，业务类型由客户端按 bvid 判定
    private const string HistoryUrl = "https://api.bilibili.com/x/web-interface/history/cursor?ps=30";
    private const string ToViewUrl = "https://api.bilibili.com/x/v2/history/toview/web";
    private const int MaxItems = 100;   // 单次最多导入条数

    private static readonly HttpClient Http = BilibiliHttpClient.Shared;

    /// <summary>平台标识</summary>
    public string Platform => "bilibili";

    /// <summary>支持的列表类别（显示名；「随机视频」置于首位，无需登录即可用）</summary>
    public IReadOnlyList<string> Categories { get; } = new[] { "随机视频", "观看历史", "稍后再看" };

    /// <summary>
    /// 拉取指定类别视频条目；登录态类别未登录抛 <see cref="PlatformNotLoggedInException"/>，
    /// 网络/接口失败抛 <see cref="InvalidOperationException"/>。「随机视频」免登录。
    /// </summary>
    public Task<IReadOnlyList<VideoItem>> FetchAsync(
        string category, IPlatformSession session, CancellationToken ct)
    {
        if (category == "随机视频") return FetchRandomAsync(ct);
        return FetchUserListsAsync(category, session, ct);
    }

    /// <summary>随机视频：跨多个随机分区累计（每次内容不同），乱序后精确截取 100 条；分区被限流则换区，兜底热门分页</summary>
    private async Task<IReadOnlyList<VideoItem>> FetchRandomAsync(CancellationToken ct)
    {
        var items = new List<VideoItem>();

        // 主源：随机分区排行（保证内容多样），跨多个分区累计，直到够 100
        for (var attempt = 0; attempt < 3 && items.Count < MaxItems; attempt++)
        {
            var rid = RandomRids[Random.Shared.Next(RandomRids.Length)];
            var url = $"https://api.bilibili.com/x/web-interface/ranking/v2?rid={rid}&type=all";
            var page = await TryFetchRandomFromAsync(url, $"排行rid{rid}", ct);
            if (page.Count == 0) continue;   // 被限流/失败则换下一分区
            foreach (var it in page)
                if (items.All(x => !string.Equals(x.InputUrl, it.InputUrl, StringComparison.OrdinalIgnoreCase)))
                    items.Add(it);
            if (items.Count < MaxItems) await Task.Delay(250, ct);   // 逐区间隔，降低被限流概率
        }

        // 兜底：热门推荐分页（随机起始页），确保能补足到 100
        if (items.Count < MaxItems)
        {
            var start = Random.Shared.Next(1, 5);
            for (var pn = start; pn <= start + 3 && items.Count < MaxItems; pn++)
            {
                var page = await TryFetchRandomFromAsync(
                    $"https://api.bilibili.com/x/web-interface/popular?pn={pn}&ps=30", "热门", ct);
                if (page.Count == 0) break;   // 已到末尾或被限流，停止逐页
                foreach (var it in page)
                    if (items.All(x => !string.Equals(x.InputUrl, it.InputUrl, StringComparison.OrdinalIgnoreCase)))
                        items.Add(it);
                if (items.Count >= MaxItems) break;
                await Task.Delay(400, ct);   // 逐页间隔，降低被限流概率
            }
        }

        // 乱序并精确截取到目标条数，每次启动/补全的顺序与内容不同
        var result = items.OrderBy(_ => Random.Shared.Next()).Take(MaxItems).ToList();
        Log.Info($"B站列表源: 随机视频 返回 {result.Count} 条");
        return result;
    }

    /// <summary>尝试从指定公开接口拉取一条随机列表；被限流/失败返回空（不抛出，供上层回退）</summary>
    private async Task<List<VideoItem>> TryFetchRandomFromAsync(string url, string tag, CancellationToken ct)
    {
        try
        {
            Log.Info($"B站列表源: 请求 {url}（免登录随机）");
            using var doc = await FetchJsonAsync(url, null, ct);
            return ParseList(doc.RootElement, tag);
        }
        catch (Exception ex)
        {
            Log.Warn($"B站列表源: {tag} 拉取失败: {ex.Message}");
            return new List<VideoItem>();
        }
    }

    /// <summary>登录态列表：观看历史 / 稍后再看（需 SESSDATA）</summary>
    private async Task<IReadOnlyList<VideoItem>> FetchUserListsAsync(
        string category, IPlatformSession session, CancellationToken ct)
    {
        var cookie = await session.GetCookieHeaderAsync("api.bilibili.com", ct);
        if (string.IsNullOrWhiteSpace(cookie)) throw NotLoggedIn();
        // 历史/稍后再看属于登录态接口，需持有登录凭证；无 SESSDATA 视为未登录
        if (!cookie.Contains("SESSDATA=", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"B站列表源: Cookie 头长度 {(cookie.Length > 9999 ? ">9999" : cookie.Length.ToString())}，缺少 SESSDATA，判定未登录");
            throw NotLoggedIn();
        }

        var url = category switch
        {
            "观看历史" => HistoryUrl,
            "稍后再看" => ToViewUrl,
            _ => throw new InvalidOperationException($"不支持的列表类别：{category}"),
        };
        Log.Info($"B站列表源: 请求 {url}");

        using var doc = await FetchJsonAsync(url, cookie, ct);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
        if (code != 0)
        {
            if (code is -101 or -400) throw NotLoggedIn();
            var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? "未知"
                : "未知";
            Log.Warn($"B站列表源: 接口返回错误 code={code}：{msg}");
            throw new InvalidOperationException($"接口返回错误 code={code}：{msg}");
        }

        var items = ParseList(root, category);
        Log.Info($"B站列表源: {category} 返回 {items.Count} 条");
        return items;
    }

    /// <summary>解析响应中的 data.list 为 VideoItem（缺省返回空列表）</summary>
    private List<VideoItem> ParseList(JsonElement root, string tag)
    {
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            Log.Warn($"B站列表源({tag}): 响应缺少 data.list，视为空列表");
            return new List<VideoItem>();
        }

        var items = new List<VideoItem>();
        foreach (var it in list.EnumerateArray())
        {
            if (items.Count >= MaxItems) break;
            if (TryToItem(it, out var item)) items.Add(item);
        }
        return items;
    }

    /// <summary>发起 GET 请求并解析为 JSON（免登录时 cookie 传 null；失败向上抛，由调用方统一处理）</summary>
    private static async Task<JsonDocument> FetchJsonAsync(string url, string? cookie, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(body);
    }

    /// <summary>从单条记录提取 VideoItem；无 bvid（非普通投稿）返回 false</summary>
    private bool TryToItem(JsonElement it, out VideoItem item)
    {
        item = null!;

        // bvid 取值：观看历史在 history.bvid，热门/排行/稍后再看在顶层 bvid
        var bvid = Str(it, "bvid");
        if (string.IsNullOrEmpty(bvid))
        {
            bvid = it.TryGetProperty("history", out var h) ? Str(h, "bvid") : "";
        }
        if (string.IsNullOrEmpty(bvid)) return false;

        var title = Str(it, "title");
        var cover = Str(it, "cover");
        if (string.IsNullOrEmpty(cover)) cover = Str(it, "pic");   // 热门/排行/稍后再看用 pic
        var duration = Int(it, "duration", out var d) && d > 0 ? d : 0;
        var progress = Int(it, "progress", out var p) && p > 0 ? p : 0;

        item = new VideoItem
        {
            Platform = Platform,
            Title = string.IsNullOrEmpty(title) ? $"B站视频 {bvid}" : title,
            InputUrl = $"https://www.bilibili.com/video/{bvid}",
            // 与 BilibiliAdapter 保持一致：不带 autoplay，由悬停时 JS 触发播放
            EmbedUrl = $"https://player.bilibili.com/player.html?bvid={bvid}&danmaku=0&high_quality=1",
            DurationSec = duration > 0 ? duration : null,
            LastPositionSec = progress,   // >30s 时由播放层续播
        };
        return true;
    }

    /// <summary>安全读取字符串（字段缺失或非字符串时返回空串）</summary>
    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>安全读取整数（字段缺失或非数字时返回 false）</summary>
    private static bool Int(JsonElement el, string name, out int value)
    {
        value = 0;
        return el.TryGetProperty(name, out var v) &&
               (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out value) : false);
    }

    /// <summary>构造未登录异常（含面向用户的提示）</summary>
    private static PlatformNotLoggedInException NotLoggedIn()
        => new("bilibili", "B站尚未登录，请先在「登录平台」中扫码/登录后再获取");
}
