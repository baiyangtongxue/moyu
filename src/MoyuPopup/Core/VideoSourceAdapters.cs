using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace MoyuPopup.Core;

/// <summary>
/// 哔哩哔哩适配器：支持 BV 号 / b23.tv 短链 / 视频页链接，
/// 产出官方 iframe 嵌入播放器地址（player.bilibili.com，合规稳定）。
/// </summary>
public sealed class BilibiliAdapter : IVideoSourceAdapter
{
    private static readonly Regex BvRegex = new("BV[0-9A-Za-z]{10}", RegexOptions.Compiled);
    private static readonly HttpClient Http = BilibiliHttpClient.Shared;

    /// <summary>平台标识</summary>
    public string Platform => "bilibili";

    /// <summary>浏览器附加参数（无特殊要求）</summary>
    public IEnumerable<string> BrowserArgs => Array.Empty<string>();

    /// <summary>判断是否 B 站链接 / BV 号 / b23.tv 短链</summary>
    public bool CanParse(string url)
    {
        var s = url.Trim();
        if (BvRegex.IsMatch(s)) return true;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) &&
               (u.Host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
                u.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析：短链先跟随 30x 跳转，再提取 BV 号生成官方嵌入地址</summary>
    public async Task<VideoItem> ParseAsync(string url, CancellationToken ct)
    {
        var s = url.Trim();

        if (Uri.TryCreate(s, UriKind.Absolute, out var u) &&
            u.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase))
        {
            using var resp = await Http.GetAsync(s, HttpCompletionOption.ResponseHeadersRead, ct);
            var final = resp.RequestMessage?.RequestUri?.ToString();
            if (!string.IsNullOrEmpty(final)) s = final;
        }

        var bv = BvRegex.Match(s).Value;
        if (string.IsNullOrEmpty(bv))
            throw new FormatException("未能从链接中识别出 B 站视频 BV 号");

        return new VideoItem
        {
            Platform = Platform,
            Title = $"B站视频 {bv}",
            InputUrl = url.Trim(),
            // 不带 autoplay：由悬停时 JS 触发播放，避免广告态下预加载自播出声
            EmbedUrl = $"https://player.bilibili.com/player.html?bvid={bv}&danmaku=0&high_quality=1",
        };
    }
}

/// <summary>腾讯视频适配器：从观看页文件名 / vid 参数提取 vid，产出官方 txp 嵌入播放器（合规）</summary>
public sealed class TencentAdapter : IVideoSourceAdapter
{
    /// <summary>平台标识</summary>
    public string Platform => "tencent";

    /// <summary>浏览器附加参数（无特殊要求）</summary>
    public IEnumerable<string> BrowserArgs => Array.Empty<string>();

    /// <summary>判断是否 v.qq.com 链接</summary>
    public bool CanParse(string url)
        => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) &&
           u.Host.EndsWith("v.qq.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>提取 vid：优先 vid= 查询参数，其次观看页 HTML 文件名</summary>
    public Task<VideoItem> ParseAsync(string url, CancellationToken ct)
    {
        var s = url.Trim();
        string? vid = null;

        if (Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            vid = HttpUtility.ParseQueryString(u.Query)["vid"];
            if (string.IsNullOrEmpty(vid))
            {
                var seg = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var last = seg.LastOrDefault();
                if (last != null && last.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(last);
                    if (name.Length is >= 6 and <= 24 &&
                        Regex.IsMatch(name, "^[a-z0-9]+$", RegexOptions.IgnoreCase))
                    {
                        vid = name;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(vid))
            throw new FormatException("未能从链接中识别出腾讯视频 vid");

        return Task.FromResult(new VideoItem
        {
            Platform = Platform,
            Title = $"腾讯视频 {vid}",
            InputUrl = s,
            EmbedUrl = $"https://v.qq.com/txp/iframe/player.html?vid={vid}",
        });
    }
}

/// <summary>自定义 URL 适配器：兜底直接加载任意网页（通用 video 元素控制）</summary>
public sealed class CustomUrlAdapter : IVideoSourceAdapter
{
    /// <summary>平台标识</summary>
    public string Platform => "custom";

    /// <summary>浏览器附加参数（无特殊要求）</summary>
    public IEnumerable<string> BrowserArgs => Array.Empty<string>();

    /// <summary>接受任意绝对 http(s) 链接（永远兜底）</summary>
    public bool CanParse(string url)
        => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) &&
           (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>原样加载链接</summary>
    public Task<VideoItem> ParseAsync(string url, CancellationToken ct)
    {
        var s = url.Trim();
        var u = new Uri(s);
        return Task.FromResult(new VideoItem
        {
            Platform = Platform,
            Title = u.Host,
            InputUrl = s,
            EmbedUrl = s,
        });
    }
}

/// <summary>
/// 抖音适配器（P1 网页适配）：v.douyin.com 短链跟随跳转 → www.douyin.com/video/{id}，
/// 播放层加载视频页 + 注入 CSS 隐藏页面 UI（设计书 7.2；页面改版需跟进）。
/// </summary>
public sealed class DouyinAdapter : IVideoSourceAdapter
{
    private static readonly Regex VideoIdRegex = new(@"/video/(\d+)", RegexOptions.Compiled);
    private static readonly HttpClient Http = BilibiliHttpClient.Shared;   // 复用同款浏览器 UA 客户端

    /// <summary>平台标识</summary>
    public string Platform => "douyin";

    /// <summary>浏览器附加参数（无特殊要求）</summary>
    public IEnumerable<string> BrowserArgs => Array.Empty<string>();

    /// <summary>判断是否抖音链接（v.douyin.com 短链 / douyin.com 视频页）</summary>
    public bool CanParse(string url)
    {
        var s = url.Trim();
        return Uri.TryCreate(s, UriKind.Absolute, out var u) &&
               (u.Host.Equals("v.douyin.com", StringComparison.OrdinalIgnoreCase) ||
                (u.Host.EndsWith("douyin.com", StringComparison.OrdinalIgnoreCase) &&
                 VideoIdRegex.IsMatch(u.AbsolutePath)));
    }

    /// <summary>解析：短链跟随 30x 跳转，提取 /video/{id} 生成视频页地址</summary>
    public async Task<VideoItem> ParseAsync(string url, CancellationToken ct)
    {
        var s = url.Trim();

        if (Uri.TryCreate(s, UriKind.Absolute, out var u) &&
            u.Host.Equals("v.douyin.com", StringComparison.OrdinalIgnoreCase))
        {
            using var resp = await Http.GetAsync(s, HttpCompletionOption.ResponseHeadersRead, ct);
            var final = resp.RequestMessage?.RequestUri?.ToString();
            if (!string.IsNullOrEmpty(final)) s = final;
        }

        var id = VideoIdRegex.Match(s).Groups[1].Value;
        if (string.IsNullOrEmpty(id))
            throw new FormatException("未能从链接中识别出抖音视频 ID");

        return new VideoItem
        {
            Platform = Platform,
            Title = $"抖音视频 {id}",
            InputUrl = url.Trim(),
            // recommend=0 关闭自动连播；autoplay=0 防广告态自播，悬停时由 JS 开播
            EmbedUrl = $"https://www.douyin.com/video/{id}?recommend=0&autoplay=0",
        };
    }
}

/// <summary>
/// 爱奇艺适配器（P1 网页适配）：iqiyi.com 观看页直接加载（无公开嵌入播放器），
/// 通用 video 元素控制，部分内容受平台策略限制。
/// </summary>
public sealed class IqiyiAdapter : IVideoSourceAdapter
{
    /// <summary>平台标识</summary>
    public string Platform => "iqiyi";

    /// <summary>浏览器附加参数（无特殊要求）</summary>
    public IEnumerable<string> BrowserArgs => Array.Empty<string>();

    /// <summary>判断是否爱奇艺链接（iqiyi.com 及其短链 iq.com）</summary>
    public bool CanParse(string url)
        => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) &&
           (u.Host.EndsWith("iqiyi.com", StringComparison.OrdinalIgnoreCase) ||
            u.Host.EndsWith("iq.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>原样加载观看页（爱奇艺页面自带播放器，无需改写地址）</summary>
    public Task<VideoItem> ParseAsync(string url, CancellationToken ct)
    {
        var s = url.Trim();
        var u = new Uri(s);
        return Task.FromResult(new VideoItem
        {
            Platform = Platform,
            Title = $"爱奇艺 {u.Segments.LastOrDefault()}",
            InputUrl = s,
            EmbedUrl = s,
        });
    }
}

/// <summary>共享 HttpClient（抖音/B 站短链跳转共用，避免重复构造）</summary>
internal static class BilibiliHttpClient
{
    /// <summary>带浏览器 UA 的单例客户端（8s 超时）</summary>
    public static readonly HttpClient Shared = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        return c;
    }
}

/// <summary>
/// 视频源路由：按配置 sources.enabled 顺序匹配适配器，custom 永远兜底。
/// 供播放控制器与播放列表窗口统一调用，避免重复装配逻辑。
/// </summary>
public static class VideoSourceRouter
{
    private static readonly BilibiliAdapter Bilibili = new();
    private static readonly TencentAdapter Tencent = new();
    private static readonly DouyinAdapter Douyin = new();
    private static readonly IqiyiAdapter Iqiyi = new();

    /// <summary>当前启用的适配器链（custom 兜底追加在末尾）</summary>
    public static IReadOnlyList<IVideoSourceAdapter> Adapters
    {
        get
        {
            var list = new List<IVideoSourceAdapter>();
            foreach (var tag in ConfigManager.Config.Sources.Enabled)
            {
                IVideoSourceAdapter? a = tag switch
                {
                    "bilibili" => Bilibili,
                    "tencent" => Tencent,
                    "douyin" => Douyin,
                    "iqiyi" => Iqiyi,
                    _ => null,
                };
                if (a != null && !list.Contains(a)) list.Add(a);
            }
            list.Add(new CustomUrlAdapter());
            return list;
        }
    }

    /// <summary>解析 URL 为 VideoItem（无匹配时 custom 兜底；解析失败抛异常）</summary>
    public static async Task<VideoItem> ParseAsync(string url, CancellationToken ct)
    {
        var adapter = Adapters.FirstOrDefault(x => x.CanParse(url))
                      ?? throw new FormatException("无法识别的链接，支持 B 站 / 腾讯视频 / 抖音 / 爱奇艺 / 网页地址");
        return await adapter.ParseAsync(url, ct);
    }
}
