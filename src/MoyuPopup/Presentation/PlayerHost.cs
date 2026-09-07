using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MoyuPopup.Core;
using WV2 = Microsoft.Web.WebView2.Wpf;

namespace MoyuPopup.Presentation;

/// <summary>
/// WebView2 宿主：懒初始化（首次播放才创建，设计书 12）、自动播放策略放行、
/// 导航去重、10s 加载超时监测（超时按失败回调）、JS 执行与取值。
/// </summary>
public sealed class PlayerHost
{
    private const int LoadTimeoutSec = 10;   // 设计书 5.5：页面加载超时 10s

    private readonly Grid _container;
    private readonly TextBlock _hint;
    private readonly DispatcherTimer _navTimer;
    private WV2.WebView2? _webView;
    private string? _loadedUrl;

    /// <summary>是否已完成 WebView2 初始化</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>页面导航完成（含超时判定）：false = 加载失败或超时</summary>
    public event Action<bool>? NavigationCompleted;

    /// <summary>构造：持有播放层容器与提示文本块，准备加载超时计时器</summary>
    public PlayerHost(Grid container, TextBlock hint)
    {
        _container = container;
        _hint = hint;
        _navTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(LoadTimeoutSec) };
        _navTimer.Tick += (s, e) =>
        {
            _navTimer.Stop();
            Log.Warn($"页面加载超时({LoadTimeoutSec}s): {_loadedUrl}");
            NavigationCompleted?.Invoke(false);
        };
    }

    /// <summary>设置播放层提示文案；非空时隐藏 WebView 显示提示，空串恢复</summary>
    public void SetHint(string text)
    {
        _hint.Text = text;
        var show = !string.IsNullOrEmpty(text);
        _hint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_webView != null) _webView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>懒初始化 WebView2（约 1~2 秒，仅首次）；失败返回 false 并提示</summary>
    public async Task<bool> EnsureInitializedAsync()
    {
        if (IsInitialized) return true;

        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 Runtime 未安装", ex);
            SetHint("未检测到 WebView2 运行时，请安装 Microsoft Edge WebView2 后重试");
            return false;
        }

        try
        {
            _webView = new WV2.WebView2();
            _container.Children.Insert(0, _webView);

            var options = new CoreWebView2EnvironmentOptions
            {
                // 允许无用户手势自动播放：悬停即播的关键（设计书 3.3）
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            var env = await CoreWebView2Environment.CreateAsync(
                null, Path.Combine(ConfigManager.AppDataDir, "WebView2"), options);
            await _webView.EnsureCoreWebView2Async(env);

            // 降低“存在感”：禁右键菜单/状态栏/DevTools
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            _webView.NavigationCompleted += (s, e) =>
            {
                _navTimer.Stop();
                NavigationCompleted?.Invoke(e.IsSuccess);
            };
            IsInitialized = true;
            Log.Info("WebView2 初始化完成");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 初始化失败", ex);
            SetHint("播放组件初始化失败，详见日志");
            return false;
        }
    }

    /// <summary>导航到指定地址（相同地址跳过，避免重复刷新；启动 10s 超时计时）</summary>
    public void Navigate(string url)
    {
        if (_webView == null || _loadedUrl == url) return;
        _loadedUrl = url;
        _webView.Source = new Uri(url);
        _navTimer.Stop();
        _navTimer.Start();
    }

    /// <summary>重新加载当前页（队列仅剩单条目时的失败重试）</summary>
    public void Reload()
    {
        if (_webView?.CoreWebView2 == null) return;
        _navTimer.Stop();
        _navTimer.Start();
        _webView.CoreWebView2.Reload();
    }

    /// <summary>执行 JS 片段（未初始化时忽略；失败仅记日志不打断流程）</summary>
    public async Task ExecuteJsAsync(string js)
    {
        if (!IsInitialized || _webView?.CoreWebView2 == null) return;
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch (Exception ex)
        {
            Log.Warn($"JS 执行失败: {ex.Message}");
        }
    }

    /// <summary>执行 JS 并返回原始 JSON 结果（未初始化/失败返回 null）</summary>
    public async Task<string?> ExecuteScriptWithResultAsync(string js)
    {
        if (!IsInitialized || _webView?.CoreWebView2 == null) return null;
        try
        {
            return await _webView.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch (Exception ex)
        {
            Log.Warn($"JS 查询失败: {ex.Message}");
            return null;
        }
    }
}
