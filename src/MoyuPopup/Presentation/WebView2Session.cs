using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using MoyuPopup.Core;
using WV2 = Microsoft.Web.WebView2.Wpf;

namespace MoyuPopup.Presentation;

/// <summary>
/// 平台登录会话：从 WebView2 CookieManager（与登录窗口/播放层共用同一用户数据目录）读取
/// 指定主机的 Cookie 头，供 <see cref="IPlatformListSource"/> 拉取登录态列表。
/// 懒初始化：首次读取时才创建 WebView2，复用同一 UserDataFolder 避免重复起浏览器进程。
/// 读取失败仅记日志并返回 null（由列表源判定未登录），不打断播放流程。
/// </summary>
public sealed class WebView2Session : IPlatformSession, IDisposable
{
    private WV2.WebView2? _webView;
    private Panel? _host;
    private bool _initialized;

    /// <summary>挂接宿主容器（必须是参与布局的 Panel——WebView2 需 HWND 才能初始化，未挂接会阻塞）</summary>
    public void Attach(Panel host) => _host = host;

    /// <summary>读取指定主机（host 不含协议，如 api.bilibili.com）的 Cookie 头；无 Cookie 返回 null</summary>
    public async Task<string?> GetCookieHeaderAsync(string host, CancellationToken ct)
    {
        try
        {
            if (!await EnsureInitializedAsync()) return null;
            var uri = $"https://{host}/";
            var cookies = await _webView!.CoreWebView2.CookieManager.GetCookiesAsync(uri);
            var header = string.Join("; ",
                cookies.Where(c => !string.IsNullOrEmpty(c.Value))
                       .Select(c => $"{c.Name}={c.Value}"));
            return string.IsNullOrWhiteSpace(header) ? null : header;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 Cookie 失败({host}): {ex.Message}");
            return null;
        }
    }

    /// <summary>懒初始化 WebView2（与播放层/登录窗口同一 UserDataFolder，Cookie 共享）</summary>
    private async Task<bool> EnsureInitializedAsync()
    {
        if (_initialized && _webView != null) return true;
        try
        {
            _webView = new WV2.WebView2();
            if (_host != null) _host.Children.Add(_webView);
            // 复用共享环境（与登录窗口/播放层同一 UserDataFolder，Cookie 共享）
            var env = await WebView2EnvironmentProvider.GetAsync();
            await _webView.EnsureCoreWebView2Async(env);
            _initialized = true;
            Log.Info("登录会话 WebView2 初始化完成");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("登录会话 WebView2 初始化失败", ex);
            return false;
        }
    }

    /// <summary>释放 WebView2 句柄</summary>
    public void Dispose()
    {
        _webView?.Dispose();
        _webView = null;
        _initialized = false;
        Log.Info("登录会话已释放");
    }
}
