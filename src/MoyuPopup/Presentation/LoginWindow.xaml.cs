using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MoyuPopup.Core;
using WV2 = Microsoft.Web.WebView2.Wpf;

namespace MoyuPopup.Presentation;

/// <summary>
/// 登录窗口：独立 WebView2 承载各平台登录页，
/// 与播放层共用同一 UserDataFolder（Cookie 自动共享，设计书 5.8）。
/// 可由设置页「启用登录窗口」进入，也可由播放列表「登录平台」预选平台打开；
/// 检测到对应平台登录 Cookie 后触发 <see cref="LoginCompleted"/>（窗口保持打开，供切换其它平台）。
/// </summary>
public partial class LoginWindow : Window
{
    /// <summary>平台 → 登录页地址（腾讯视频无独立免跳登录页，载首页由用户点登录）</summary>
    private static readonly (string Tag, string Url)[] LoginUrls =
    {
        ("bilibili", "https://passport.bilibili.com/login"),
        ("tencent", "https://v.qq.com/"),
        ("douyin", "https://www.douyin.com/login"),
    };

    /// <summary>登录成功通知（平台标识）——供上层自动获取播放列表</summary>
    public event Action<string>? LoginCompleted;

    /// <summary>平台 → 登录成功判定的标识 Cookie 与检查地址（无标识的平台不做自动判定，改由手动「获取列表」）</summary>
    private static readonly (string Tag, string AuthCookie, string CheckUri)[] AuthMarkers =
    {
        ("bilibili", "SESSDATA", "https://www.bilibili.com/"),
        ("douyin", "sessionid", "https://www.douyin.com/"),
    };

    private WV2.WebView2? _webView;
    private bool _initialized;
    private DispatcherTimer? _loginPoll;
    private bool _wasLoggedIn;   // 记录上次检测结果，仅在“未登录→已登录”跳变时通知一次，避免每 2s 重复触发

    /// <summary>构造：载入默认平台登录页</summary>
    public LoginWindow() : this(null) { }

    /// <summary>构造：外部预选平台（用于「登录平台」流程）</summary>
    public LoginWindow(string? initialPlatform)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(initialPlatform)) SelectPlatform(initialPlatform);
        Loaded += async (s, e) => await EnsureInitializedAsync();
    }

    /// <summary>懒初始化 WebView2（与播放层同用户数据目录，Cookie 共享）</summary>
    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        try
        {
            _webView = new WV2.WebView2();
            WebContainer.Children.Add(_webView);
            // 复用共享环境（与播放层同一 UserDataFolder，Cookie 共享）
            var env = await WebView2EnvironmentProvider.GetAsync();
            await _webView.EnsureCoreWebView2Async(env);

            // 收敛存在感：禁右键菜单/状态栏/DevTools，与播放层一致
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            _initialized = true;
            NavigateToPlatform();
            StartLoginPoll();
            Log.Info("登录窗口 WebView2 初始化完成");
        }
        catch (Exception ex)
        {
            Log.Error("登录窗口 WebView2 初始化失败", ex);
            WebContainer.Children.Add(new TextBlock
            {
                Text = "登录组件初始化失败，请确认已安装 WebView2 运行时",
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(12),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    /// <summary>切换平台 → 导航到对应登录页</summary>
    private void OnPlatformChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) NavigateToPlatform();
        _wasLoggedIn = false;   // 切换平台后重新判定登录态，便于再次触发自动获取
    }

    /// <summary>按当前下拉选择导航登录页</summary>
    private void NavigateToPlatform()
    {
        if (_webView?.CoreWebView2 == null) return;
        var tag = (CmbPlatform.SelectedItem as ComboBoxItem)?.Tag as string ?? "bilibili";
        var url = LoginUrls.FirstOrDefault(x => x.Tag == tag).Url;
        if (!string.IsNullOrEmpty(url)) _webView.Source = new Uri(url);
    }

    /// <summary>按平台标识选中下拉项（外部预选平台用）</summary>
    private void SelectPlatform(string tag)
    {
        foreach (ComboBoxItem it in CmbPlatform.Items)
        {
            if (string.Equals(it.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                CmbPlatform.SelectedItem = it;
                return;
            }
        }
    }

    /// <summary>启动登录状态轮询（每 2s 检测一次，登录成功后自动通知，不关闭窗口便于切换平台）</summary>
    private void StartLoginPoll()
    {
        _loginPoll?.Stop();
        _loginPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _loginPoll.Tick += async (s, e) => await CheckLoginAsync();
        _loginPoll.Start();
    }

    /// <summary>检测当前平台是否已登录（对应标识 Cookie 存在）；从未登录→已登录时通知一次，不自动关闭（供用户继续切换平台）</summary>
    private async Task CheckLoginAsync()
    {
        if (_webView?.CoreWebView2 == null) return;
        var tag = (CmbPlatform.SelectedItem as ComboBoxItem)?.Tag as string ?? "bilibili";
        var marker = AuthMarkers.FirstOrDefault(x => x.Tag == tag);
        if (string.IsNullOrEmpty(marker.AuthCookie)) return;   // 该平台无可靠登录标识，不做自动判定
        try
        {
            var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(marker.CheckUri);
            var loggedIn = cookies.Any(c => c.Name == marker.AuthCookie && !string.IsNullOrEmpty(c.Value));
            if (loggedIn && !_wasLoggedIn)
            {
                _wasLoggedIn = true;
                Log.Info($"登录成功：{tag}");
                LoginCompleted?.Invoke(tag);   // 通知上层自动获取（窗口保持打开，用户可继续切换平台）
            }
            else if (!loggedIn)
            {
                _wasLoggedIn = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"登录状态检测失败: {ex.Message}");
        }
    }

    /// <summary>关闭前停用 WebView，尽快释放句柄</summary>
    protected override void OnClosed(EventArgs e)
    {
        _loginPoll?.Stop();
        _loginPoll = null;
        if (_webView != null)
        {
            _webView.Dispose();
            _webView = null;
        }
        base.OnClosed(e);
    }
}
