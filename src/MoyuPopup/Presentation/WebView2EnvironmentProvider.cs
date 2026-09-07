using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using MoyuPopup.Core;

namespace MoyuPopup.Presentation;

/// <summary>
/// 共享 WebView2 环境（单例）：所有 WebView2（播放层 / 登录窗口 / 登录会话）复用同一
/// UserDataFolder 与同一 Environment，确保 Cookie 共享且不因多环境争用同一数据目录而失败。
/// 附加 "--autoplay-policy=no-user-gesture-required"：悬停即播需要（对登录页等无副作用）。
/// </summary>
internal static class WebView2EnvironmentProvider
{
    private static Task<CoreWebView2Environment>? _envTask;
    private static readonly object LockObj = new();

    private static readonly CoreWebView2EnvironmentOptions Options = new()
    {
        // 允许无用户手势自动播放：悬停即播的关键（设计书 3.3）
        AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
    };

    /// <summary>获取共享环境（线程安全；仅创建一次，所有调用方 await 同一实例）</summary>
    public static Task<CoreWebView2Environment> GetAsync()
    {
        if (_envTask != null) return _envTask;
        lock (LockObj)
        {
            _envTask ??= CreateAsync();
        }
        return _envTask;
    }

    /// <summary>创建共享环境（加载失败则向上抛，由调用方各自兜底提示）</summary>
    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        var path = Path.Combine(ConfigManager.AppDataDir, "WebView2");
        return await CoreWebView2Environment.CreateAsync(null, path, Options);
    }
}
