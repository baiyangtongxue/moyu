using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using MoyuPopup.Core;

namespace MoyuPopup.Presentation;

/// <summary>
/// 播放控制器：队列驱动（PlaylistManager 为唯一数据源）→ PlayerHost 导航 → JS 控制。
/// 悬停即播 / 移开即停 / 热键切集 / 位置记忆 / 失败自动跳集（设计书 5.4/5.5）。
/// </summary>
public sealed class PlaybackController
{
    // 通用 HTML5 video 控制：嵌入页以顶层文档加载，video 元素可直接操控
    private const string JsPlay =
        "(function(){var v=document.querySelector('video');if(v){var p=v.play();if(p&&p.catch){p.catch(function(){});}}})()";
    private const string JsPause =
        "(function(){var v=document.querySelector('video');if(v){v.pause();}})()";
    private const string JsToggle =
        "(function(){var v=document.querySelector('video');if(v){if(v.paused){var p=v.play();if(p&&p.catch){p.catch(function(){});}}else{v.pause();}}})()";
    private const string JsQueryTime =
        "(function(){var v=document.querySelector('video');return v?Math.floor(v.currentTime):-1;})()";

    private readonly PlaylistManager _playlist;
    private readonly DispatcherTimer _posTimer;   // 5s 位置轮询（播放中定时记忆进度）
    private PlayerHost? _host;
    private double _volume = 0.4;
    private bool _shouldPlay;              // 仅 Playing 态轮询保存位置
    private int _consecutiveFails;         // 连续加载失败计数（≥3 暂停队列）
    private int? _pendingSeek;             // 导航完成后的续播起点（秒）

    /// <summary>当前条目（透传自播放队列）</summary>
    public VideoItem? Current => _playlist.Current;

    /// <summary>播放队列（供播放列表窗口读写）</summary>
    public PlaylistManager Playlist => _playlist;

    /// <summary>失败/提示通知 (title, message) → 托盘气泡</summary>
    public event Action<string, string>? Notify;

    /// <summary>连续 3 次加载失败 → 请求回广告态（暂停自动切换）</summary>
    public event Action? QueueFailedPause;

    /// <summary>构造：绑定队列并准备 5s 位置轮询定时器</summary>
    public PlaybackController(AppConfig cfg, PlaylistManager playlist)
    {
        _playlist = playlist;
        _volume = Math.Clamp(cfg.Behavior.DefaultVolume / 100.0, 0, 1);
        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _posTimer.Tick += (s, e) =>
        {
            if (_shouldPlay && Current is { } item) _ = SavePositionAsync(item);
        };
    }

    /// <summary>挂接播放层容器与提示文本块，并启动位置轮询</summary>
    public void Attach(Grid webViewContainer, TextBlock hint)
    {
        _host = new PlayerHost(webViewContainer, hint);
        _host.NavigationCompleted += OnNavigationCompleted;
        _posTimer.Start();
        if (Current == null) SetHintDefault();
    }

    /// <summary>添加链接并置为当前、预加载就绪（托盘「打开视频链接」入口）</summary>
    public async Task LoadUrlAsync(string url)
    {
        url = url.Trim();
        if (url.Length == 0 || _host == null) return;

        try
        {
            var parsed = await VideoSourceRouter.ParseAsync(url, CancellationToken.None);
            var item = _playlist.AddOrUpdate(parsed);
            _playlist.SetCurrentById(item.Id);
            _playlist.AddHistory(item);
            _host.SetHint("");
            await PreloadAsync(item);
            Log.Info($"视频就绪: [{item.Platform}] {item.Title} → {item.EmbedUrl}");
        }
        catch (Exception ex)
        {
            Log.Error($"链接解析失败: {url}", ex);
            _host.SetHint($"链接解析失败：{ex.Message}");
        }
    }

    /// <summary>悬停 → 播放当前条目（首次悬停才初始化 WebView2，之后仅 JS 开播）</summary>
    public async Task ResumeAsync()
    {
        if (_host == null) return;

        var item = Current;
        if (item == null)
        {
            SetHintDefault();
            return;
        }
        _shouldPlay = true;              // 先置位：导航完成回调按此决定自动开播
        _host.SetHint("");
        if (!await _host.EnsureInitializedAsync()) return;

        _host.Navigate(item.EmbedUrl);
        await _host.ExecuteJsAsync(JsVolume(_volume));
        await _host.ExecuteJsAsync(JsPlay);
    }

    /// <summary>移开/老板键 → 暂停（pauseOnLeave=false 时移开不调用此方法）</summary>
    public Task PauseAsync()
    {
        _shouldPlay = false;
        return _host?.ExecuteJsAsync(JsPause) ?? Task.CompletedTask;
    }

    /// <summary>播放/暂停切换（热键）</summary>
    public Task PlayOrPauseAsync() => _host?.ExecuteJsAsync(JsToggle) ?? Task.CompletedTask;

    /// <summary>音量调节（percent: 0-100）</summary>
    public Task SetVolumeAsync(int percent)
    {
        _volume = Math.Clamp(percent, 0, 100) / 100.0;
        Log.Info($"音量: {Math.Clamp(percent, 0, 100)}%");
        return _host?.ExecuteJsAsync(JsVolume(_volume)) ?? Task.CompletedTask;
    }

    /// <summary>
    /// 热键/双击切集：尽力保存当前位置 → 推进队列 → 预加载新条目。
    /// 返回新条目；队列为空返回 null（由调用方气泡提示）。
    /// </summary>
    public VideoItem? StepTo(int offset)
    {
        if (Current is { } old) _ = SavePositionAsync(old);
        var item = _playlist.StepTo(offset);
        if (item == null) return null;
        _playlist.AddHistory(item);
        _ = PreloadAsync(item);
        return item;
    }

    /// <summary>播放列表双击播放：置为当前、记历史并预加载（播放态下导航完成即自动开播）</summary>
    public async Task PlayItemAsync(VideoItem item)
    {
        if (!_playlist.SetCurrentById(item.Id)) return;
        _playlist.AddHistory(item);
        await PreloadAsync(item);
    }

    /// <summary>预加载：初始化 WebView + 导航（记录续播起点）</summary>
    private async Task PreloadAsync(VideoItem item)
    {
        if (_host == null) return;
        _host.SetHint("");
        if (!await _host.EnsureInitializedAsync()) return;
        _pendingSeek = item.LastPositionSec > 30 ? item.LastPositionSec : null;
        _host.Navigate(item.EmbedUrl);
    }

    /// <summary>页面导航完成：按期望状态收尾（播放态开播+续播，其余暂停防广告态出声）；失败 → 自动跳集或暂停队列</summary>
    private void OnNavigationCompleted(bool success)
    {
        var item = Current;
        if (item == null || _host == null) return;

        if (!success)
        {
            HandleLoadFailure(item);
            return;
        }

        _consecutiveFails = 0;
        _ = _host.ExecuteJsAsync(JsVolume(_volume));
        if (_pendingSeek is > 30) _ = _host.ExecuteJsAsync(JsSeek(_pendingSeek.Value));
        _pendingSeek = null;
        var adapt = PlatformAdaptJs(item.Platform);      // 平台适配（如抖音隐藏页面 UI）
        if (adapt.Length > 0) _ = _host.ExecuteJsAsync(adapt);
        // 播放态：补一次开播（ResumeAsync 的 JsPlay 可能早于页面就绪而落空）；
        // 非播放态：强制暂停，防预加载页面自动播出声
        _ = _host.ExecuteJsAsync(_shouldPlay ? JsPlay : JsPause);
    }

    /// <summary>
    /// 平台适配 JS（网页适配型平台注入，设计书 7.2）：
    /// 抖音 = 隐藏顶栏/侧栏/评论等页面 UI（尽力而为，页面改版需跟进）+ 兜底点击播放钮。
    /// </summary>
    private static string PlatformAdaptJs(string platform)
    {
        if (platform != "douyin") return "";
        return
            "(function(){try{" +
            "var s=document.createElement('style');" +
            "s.textContent='#ssr-wrapper [class*=navWrap],[class*=sidebar]," +
            "[class*=comment],[class*=relatedVideo],[class*=loginGuide]," +
            "[class*=liveInfo]{display:none!important;}" +
            "div[data-e2e=video-feed]{height:100%!important;}" +
            "document.head.appendChild(s);" +
            "var v=document.querySelector('video');" +
            "if(v&&v.paused){var b=document.querySelector('.xgplayer-play,[class*=playIcon]');" +
            "if(b)b.click();}" +
            "}catch(e){}})()";
    }

    /// <summary>加载失败：连续 ≥3 次暂停队列并回广告态；否则气泡提示并自动跳下一集</summary>
    private void HandleLoadFailure(VideoItem item)
    {
        _consecutiveFails++;
        if (_consecutiveFails >= 3)
        {
            _consecutiveFails = 0;
            Log.Warn($"连续 3 次加载失败，暂停自动切换: {item.Title}");
            Notify?.Invoke("播放失败", $"「{item.Title}」连续加载失败，已暂停自动切换");
            QueueFailedPause?.Invoke();
            return;
        }

        Log.Warn($"视频加载失败，自动切换下一集: {item.Title}");
        Notify?.Invoke("该视频暂不可播", $"「{item.Title}」加载失败，自动切换下一集");
        if (_playlist.Items.Count == 1)
        {
            _host?.Reload();                 // 单条目：原地重试
            return;
        }
        _ = StepTo(1);
    }

    /// <summary>保存条目播放位置（&gt;30s 才记录，切回续播）</summary>
    private async Task SavePositionAsync(VideoItem item)
    {
        try
        {
            var sec = await QueryCurrentTimeAsync();
            if (sec > 0) _playlist.SetPosition(item.Id, sec);
        }
        catch (Exception ex)
        {
            Log.Warn($"保存播放位置失败: {ex.Message}");
        }
    }

    /// <summary>查询当前视频播放秒数（无 video 元素返回 -1）</summary>
    private async Task<int> QueryCurrentTimeAsync()
    {
        if (_host == null || !_host.IsInitialized) return -1;
        var json = await _host.ExecuteScriptWithResultAsync(JsQueryTime);
        if (string.IsNullOrEmpty(json)) return -1;
        var t = json.Trim().Trim('"');
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    /// <summary>默认引导提示</summary>
    private void SetHintDefault() => _host?.SetHint("通过托盘「打开视频链接」添加视频\n悬停即播 · 移开即停");

    /// <summary>生成音量 JS（对页面内全部 video 元素生效）</summary>
    private static string JsVolume(double v)
        => $"(function(){{var vs=document.querySelectorAll('video');for(var i=0;i<vs.length;i++){{vs[i].volume={v.ToString("0.##", CultureInfo.InvariantCulture)};}}}})()";

    /// <summary>生成续播定位 JS</summary>
    private static string JsSeek(int sec)
        => $"(function(){{var v=document.querySelector('video');if(v){{v.currentTime={sec};}}}})()";
}
