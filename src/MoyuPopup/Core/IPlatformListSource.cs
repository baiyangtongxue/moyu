using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MoyuPopup.Core;

/// <summary>列表源未登录异常：告知用户需先通过登录窗口登录该平台（Cookie 与播放层/登录窗口共享）</summary>
public sealed class PlatformNotLoggedInException : Exception
{
    /// <summary>平台标识（如 bilibili）</summary>
    public string Platform { get; }

    /// <summary>构造未登录异常（含面向用户的提示）</summary>
    public PlatformNotLoggedInException(string platform, string message)
        : base(message) => Platform = platform;
}

/// <summary>
/// 平台登录会话：向列表源提供登录 Cookie 头（实现位于表现层，封装 WebView2 CookieManager；
/// 因与登录窗口/播放层共用同一用户数据目录，登录后自动有态、无需二次导出）。
/// </summary>
public interface IPlatformSession
{
    /// <summary>
    /// 返回指定主机（host 不含协议）的 Cookie 头字符串，形如 "SESSDATA=...; buvid3=..."；
    /// 无 Cookie 返回 null。
    /// </summary>
    Task<string?> GetCookieHeaderAsync(string host, CancellationToken ct);
}

/// <summary>
/// 平台视频列表源：登录后动态拉取该平台的一类视频列表。
/// 与 URL 解析适配器（IVideoSourceAdapter）解耦——适配器管“单条链接怎么播”，列表源管“登录后怎么拉一整个列表”。
/// </summary>
public interface IPlatformListSource
{
    /// <summary>平台标识（对应 VideoItem.Platform，如 bilibili）</summary>
    string Platform { get; }

    /// <summary>支持的列表类别（显示名，如“稍后再看/历史记录/收藏夹”）</summary>
    IReadOnlyList<string> Categories { get; }

    /// <summary>
    /// 拉取指定类别全部视频条目；未登录抛 <see cref="PlatformNotLoggedInException"/>；
    /// 网络/接口失败抛 <see cref="InvalidOperationException"/>（含原因）。
    /// </summary>
    Task<IReadOnlyList<VideoItem>> FetchAsync(string category, IPlatformSession session, CancellationToken ct);
}

/// <summary>
/// 列表源注册表：按平台标识取实现。新增平台 = 新增实现类并在下方登记，核心零改动（设计书 7.3 思路）。
/// </summary>
public static class ListSourceRegistry
{
    // 列表源保持进程级单例即可（无实例字段状态，仅做拉取）
    private static readonly IPlatformListSource[] Sources = { new BilibiliListSource() };

    /// <summary>按平台标识查找列表源；未登记返回 null</summary>
    public static IPlatformListSource? Get(string platform)
    {
        foreach (var s in Sources)
            if (string.Equals(s.Platform, platform, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    /// <summary>所有已登记列表源（用于平台下拉选择）</summary>
    public static IReadOnlyList<IPlatformListSource> All => Sources;
}
