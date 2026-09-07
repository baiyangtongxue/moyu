using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

    /// <summary>列表行视图模型（GridView 绑定用）</summary>
    private sealed record Row(string Id, string Title, string Platform, string PositionText, string CurrentMark);

    /// <summary>构造：绑定队列与播放控制器，挂接变化刷新</summary>
    public PlaylistWindow(PlaylistManager playlist, PlaybackController playback)
    {
        InitializeComponent();
        _playlist = playlist;
        _playback = playback;
        _playlist.Changed += Refresh;
        Closed += (s, e) => _playlist.Changed -= Refresh;   // 防重复挂接泄漏
        Refresh();
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
