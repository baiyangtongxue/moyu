using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using MoyuPopup.Core;
using WV2 = Microsoft.Web.WebView2.Wpf;

namespace MoyuPopup.Presentation;

/// <summary>
/// 隐藏登录窗口（F10）：独立 WebView2 承载各平台登录页，
/// 与播放层共用同一 UserDataFolder（Cookie 自动共享，设计书 5.8）。
/// 默认由设置页「启用登录窗口」开启后才能进入。
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

    private WV2.WebView2? _webView;
    private bool _initialized;

    /// <summary>构造：启用校验 → 载入默认平台登录页</summary>
    public LoginWindow()
    {
        InitializeComponent();
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
            var env = await CoreWebView2Environment.CreateAsync(
                null, System.IO.Path.Combine(ConfigManager.AppDataDir, "WebView2"));
            await _webView.EnsureCoreWebView2Async(env);

            // 收敛存在感：禁右键菜单/状态栏/DevTools，与播放层一致
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            _initialized = true;
            NavigateToPlatform();
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
    }

    /// <summary>按当前下拉选择导航登录页</summary>
    private void NavigateToPlatform()
    {
        if (_webView?.CoreWebView2 == null) return;
        var tag = (CmbPlatform.SelectedItem as ComboBoxItem)?.Tag as string ?? "bilibili";
        var url = LoginUrls.FirstOrDefault(x => x.Tag == tag).Url;
        if (!string.IsNullOrEmpty(url)) _webView.Source = new Uri(url);
    }

    /// <summary>关闭前停用 WebView，尽快释放句柄</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (_webView != null)
        {
            _webView.Dispose();
            _webView = null;
        }
        base.OnClosed(e);
    }
}
