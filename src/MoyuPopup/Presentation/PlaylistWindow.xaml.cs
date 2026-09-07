using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoyuPopup.Core;

namespace MoyuPopup.Presentation;

/// <summary>
/// 播放列表管理窗口（对外伪装为「素材清单」）：增删条目、双击立即播放、
/// 标记当前条目与续播进度；队列变化自动刷新（设计书 5.7）。
/// </summary>
public partial class PlaylistWindow : Window
{
    private readonly PlaylistManager _playlist;
    private readonly PlaybackController _playback;
    private readonly WebView2Session _session = new();
    private IPlatformListSource? _source;
    private bool _busy;   // 防止重复触发获取

    /// <summary>可选登录平台（与登录窗口一致；仅登记了列表源的平台支持自动获取列表）</summary>
    private static readonly (string Tag, string Name)[] SelectablePlatforms =
    {
        ("bilibili", "哔哩哔哩"),
        ("tencent", "腾讯视频"),
        ("douyin", "抖音"),
    };

    /// <summary>列表行视图模型（GridView 绑定用）</summary>
    private sealed record Row(string Id, string Title, string Platform, string PositionText, string CurrentMark);

    /// <summary>构造：绑定队列与播放控制器，挂接变化刷新</summary>
    public PlaylistWindow(PlaylistManager playlist, PlaybackController playback)
    {
        InitializeComponent();
        _playlist = playlist;
        _playback = playback;
        _session.Attach(SessionHost);          // 为会话 WebView2 提供宿主（需 HWND 才能初始化）
        _playlist.Changed += Refresh;
        Closed += (s, e) =>
        {
            _playlist.Changed -= Refresh;   // 防重复挂接泄漏
            _session.Dispose();             // 释放登录会话 WebView2
        };
        PopulateSources();
        Refresh();
    }

    /// <summary>填充平台下拉（与登录窗口一致的平台；未登记列表源的平台可登录但不能自动获取）</summary>
    private void PopulateSources()
    {
        CmbSource.Items.Clear();
        foreach (var (tag, name) in SelectablePlatforms)
            CmbSource.Items.Add(new ComboBoxItem { Content = name, Tag = tag });
        if (CmbSource.Items.Count > 0) CmbSource.SelectedIndex = 0;
    }

    /// <summary>平台切换 → 刷新类别下拉；无列表源的平台禁用「获取列表」</summary>
    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (CmbSource.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _source = ListSourceRegistry.Get(tag);
        CmbCategory.Items.Clear();
        if (_source != null)
        {
            foreach (var cat in _source.Categories)
                CmbCategory.Items.Add(cat);
            if (CmbCategory.Items.Count > 0) CmbCategory.SelectedIndex = 0;
            BtnFetch.IsEnabled = true;
            LblStatus.Text = "双击条目立即播放 · Ctrl+Alt+←/→ 快速切集";
        }
        else
        {
            BtnFetch.IsEnabled = false;
            LblStatus.Text = "该平台暂不支持自动获取列表，请用「粘贴链接」添加。";
        }
    }

    /// <summary>登录平台：打开登录窗口（预选当前平台），登录成功自动获取列表</summary>
    private void OnLogin(object sender, RoutedEventArgs e)
    {
        var tag = (CmbSource.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (string.IsNullOrEmpty(tag)) return;
        var login = new LoginWindow(tag);
        login.LoginCompleted += platform => { _ = FetchListAsync(); };   // 登录成功即自动获取
        login.Show();
    }

    /// <summary>手动获取列表</summary>
    private async void OnFetch(object sender, RoutedEventArgs e) => await FetchListAsync();

    /// <summary>获取当前平台所选类别的列表并追加到队列；未登录/失败给出提示</summary>
    private async Task FetchListAsync()
    {
        var source = _source;
        if (source == null)
        {
            LblStatus.Text = "该平台暂不支持自动获取列表，请用「粘贴链接」添加。";
            return;
        }
        if (_busy) return;
        var category = CmbCategory.SelectedItem as string;
        if (category == null)
        {
            LblStatus.Text = "请选择列表类别。";
            return;
        }

        _busy = true;
        BtnFetch.IsEnabled = false;
        LblStatus.Text = "正在获取列表…";
        Log.Info($"开始获取播放列表: {source.Platform}/{category}");
        try
        {
            var items = await source.FetchAsync(category, _session, CancellationToken.None);
            if (items.Count == 0)
            {
                LblStatus.Text = "未获取到条目（该类别可能为空）。";
                Log.Warn($"B站列表源: 该类别无条目: {category}");
                return;
            }
            var added = _playlist.AddRange(items);
            Log.Info($"获取播放列表: {source.Platform}/{category} 共 {items.Count} 条, 新增 {added} 条");
            LblStatus.Text = $"已获取 {items.Count} 条，新增 {added} 条。";
        }
        catch (PlatformNotLoggedInException ex)
        {
            Log.Warn($"未登录: {ex.Message}");
            LblStatus.Text = "尚未登录，请先点「登录平台」。";
            MessageBox.Show(ex.Message, "获取列表", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error($"获取播放列表失败: {ex.Message}", ex);
            LblStatus.Text = "获取失败，详见日志。";
            MessageBox.Show($"获取失败：{ex.Message}", "获取列表", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            BtnFetch.IsEnabled = true;
        }
    }

    /// <summary>全量重建列表（队列规模小，O(n) 足够）；当前条目标记 ▶ 与进度</summary>
    private void Refresh()
    {
        if (_playlist.Items.Count == 0)
        {
            QueueList.ItemsSource = new List<Row>
            {
                new("", "（队列为空：粘贴链接后点「添加」）", "-", "-", "")
            };
            return;
        }

        var rows = _playlist.Items.Select((it, i) => new Row(
            it.Id,
            (i == _playlist.CurrentIndex ? "▶ " : "") + it.Title,
            it.Platform,
            it.LastPositionSec > 0 ? FormatSec(it.LastPositionSec) : "-",
            i == _playlist.CurrentIndex ? "●" : "")).ToList();
        QueueList.ItemsSource = rows;
    }

    /// <summary>秒数 → mm:ss / h:mm:ss</summary>
    private static string FormatSec(int sec)
    {
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    /// <summary>解析选中的真实条目（空队列占位行 Id 为空）</summary>
    private VideoItem? SelectedItem()
    {
        var id = (QueueList.SelectedItem as Row)?.Id;
        return string.IsNullOrEmpty(id) ? null : _playlist.Items.FirstOrDefault(x => x.Id == id);
    }

    /// <summary>添加链接：复用 VideoSourceRouter 解析（custom 兜底），成功后置为当前</summary>
    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0) return;

        try
        {
            var item = await VideoSourceRouter.ParseAsync(url, CancellationToken.None);
            var stored = _playlist.AddOrUpdate(item);
            _playlist.SetCurrentById(stored.Id);
            UrlBox.Clear();
            Log.Info($"播放列表添加: [{stored.Platform}] {stored.Title}");
        }
        catch (Exception ex)
        {
            Log.Error($"播放列表添加失败: {url}", ex);
            MessageBox.Show($"链接解析失败：{ex.Message}", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>输入框内回车 = 添加</summary>
    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnAdd(sender, e);
    }

    /// <summary>双击条目：置为当前并预加载（悬停弹窗即续播）</summary>
    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedItem() is { } item)
        {
            await _playback.PlayItemAsync(item);
            Refresh();
        }
    }

    /// <summary>移除选中条目（队列修正当前索引由 PlaylistManager 负责）</summary>
    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (SelectedItem() is { } item) _playlist.Remove(item.Id);
    }

    /// <summary>清空队列（二次确认防误触）</summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_playlist.Items.Count == 0) return;
        if (MessageBox.Show("确定清空整个播放队列？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _playlist.Clear();
    }
}
