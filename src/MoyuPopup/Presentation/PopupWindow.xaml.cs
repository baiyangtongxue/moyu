using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MoyuPopup.Core;

namespace MoyuPopup.Presentation;

/// <summary>
/// 右下角弹窗主窗口：双层内容（广告轮播/播放层）按状态机互斥显隐，
/// 老板键显隐、全局热键、Ctrl 拖拽、右下角定位（设计书 5.1/5.2/5.6）。
/// </summary>
public partial class PopupWindow : Window
{
    private const double ShadowMargin = 12;      // 外层阴影留白（DIP）
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly AppConfig _cfg;
    private readonly AppStateMachine _machine;
    private readonly PlaybackController _playback;
    private readonly DispatcherTimer _barShowTimer;   // 播放态悬停 1s 后浮现工具条
    private readonly DispatcherTimer _barHideTimer;   // 3s 无操作淡出工具条
    private PlaylistWindow? _playlistWindow;
    private SettingsWindow? _settingsWindow;
    private HotkeyManager? _hotkeys;
    private MouseWatcher? _watcher;
    private int _volumePercent;
    private bool _allowClose;
    private bool _suppressVolume;                     // 音量滑条初始化赋值时抑制 ValueChanged

    // 记录“广告态”正常外观（深色背景 + 边框 + 阴影），供透明模式切换恢复
    private readonly Brush? _rootBorderBrush;          // 正常背景
    private readonly Brush? _rootBorderStroke;         // 正常边框刷
    private readonly Thickness _rootBorderThickness;   // 正常边框粗细
    private readonly Effect? _rootShadow;

    /// <summary>透明模式底色：极低 Alpha（≈0.8%），肉眼近乎全透明，但整块窗口区域仍可命中（悬停/拖动）</summary>
    private static readonly Brush TransparentBody = new SolidColorBrush(Color.FromArgb(0x02, 0x14, 0x16, 0x1A));

    /// <summary>上班锁定状态变化（由热键触发，用于同步托盘勾选）</summary>
    public event Action<bool>? TrayLockChanged;

    /// <summary>播放控制器（供 App 层挂接托盘气泡与播放列表窗口）</summary>
    public PlaybackController Playback => _playback;

    /// <summary>失败/提示气泡请求 (title, message) → 托盘（App 层转接）</summary>
    public event Action<string, string>? BubbleRequested;

    /// <summary>构造：应用配置（尺寸/透明度/置顶/标题），挂接状态迁移，装载广告素材与播放队列</summary>
    public PopupWindow(AppConfig cfg, AppStateMachine machine, PlaylistManager playlist)
    {
        InitializeComponent();
        _cfg = cfg;
        _machine = machine;
        _rootBorderBrush = RootBorder.Background;
        _rootBorderStroke = RootBorder.BorderBrush;
        _rootBorderThickness = RootBorder.BorderThickness;
        _rootShadow = RootBorder.Effect;

        Title = cfg.Stealth.WindowTitle;
        Width = cfg.Window.Width + ShadowMargin * 2;
        Height = cfg.Window.Height + ShadowMargin * 2;
        Opacity = cfg.Window.Opacity;
        Topmost = cfg.Window.Topmost;

        _machine.Transitioned += OnTransitioned;
        RootBorder.MouseLeftButtonDown += OnRootMouseDown;

        _volumePercent = Math.Clamp(cfg.Behavior.DefaultVolume, 0, 100);
        _playback = new PlaybackController(cfg, playlist);
        _playback.Attach(WebViewHost, PlayHint);
        _playback.Notify += (t, m) => BubbleRequested?.Invoke(t, m);
        _playback.QueueFailedPause += () => _machine.Fire(AppEvent.MouseLeave);   // 连续失败 → 回广告态
        ApplyAdSlides();

        // 迷你工具条定时器：悬停 1s 淡入、3s 无操作淡出（设计书 6.3）
        _barShowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _barShowTimer.Tick += (s, e) =>
        {
            _barShowTimer.Stop();
            if (_machine.Current == AppStatus.Playing) ShowMiniBar();
        };
        _barHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _barHideTimer.Tick += (s, e) =>
        {
            _barHideTimer.Stop();
            HideMiniBar();
        };

        _suppressVolume = true;
        VolumeSlider.Value = _volumePercent;
        _suppressVolume = false;
        ApplyAdAppearance();
    }

    /// <summary>句柄就绪：工具窗口样式 → 定位 → 热键 → 鼠标监视</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyToolWindowStyle();
        PlaceWindow(restoreSaved: _cfg.Behavior.RememberPosition);

        _hotkeys = new HotkeyManager(this);
        RegisterHotkeys();

        _watcher = new MouseWatcher(this) { HoverDelayMs = _cfg.Behavior.HoverDelayMs };
        _watcher.Entered += () => _machine.Fire(AppEvent.MouseEnter);
        _watcher.Left += () => _machine.Fire(AppEvent.MouseLeave);
        _watcher.Start();
    }

    /// <summary>装载广告素材（目录为空时使用内置兜底素材）并启动轮播</summary>
    private void ApplyAdSlides()
    {
        var dir = Path.Combine(ConfigManager.AppDataDir, "ads");
        var slides = AdContentFactory.LoadAds(dir);
        if (slides.Count == 0)
        {
            slides.Add(AdContentFactory.CreateFallbackSlide());
            Log.Warn("广告目录为空或不可读，使用兜底素材");
        }
        AdLayer.LoadSlides(slides, _cfg.Behavior.AdIntervalSec, _cfg.Behavior.AdShuffle);
        AdLayer.CloseRequested += ToggleBoss;   // 假 ✘ = 老板键
        AdLayer.Start();
    }

    /// <summary>按透明模式开关应用「广告态」外观：隐身+细悬停边 vs 深色广告框</summary>
    private void ApplyAdAppearance()
    {
        if (_cfg.Window.TransparentMode)
        {
            // 完全隐身：清背景/边框/阴影，广告图透明到零，无任何可见边；
            // 底色保留极低 Alpha，使整块窗口区域都可命中（悬停即出视频、左键可拖动）
            RootBorder.Background = TransparentBody;
            RootBorder.BorderBrush = null;
            RootBorder.BorderThickness = new Thickness(0);
            RootBorder.Effect = null;
            AdLayer.Visibility = Visibility.Collapsed;
            AdLayer.Pause();
        }
        else
        {
            RootBorder.Background = _rootBorderBrush ?? Brushes.Transparent;
            RootBorder.BorderBrush = _rootBorderStroke;
            RootBorder.BorderThickness = _rootBorderThickness;
            RootBorder.Effect = _rootShadow;
            AdLayer.Visibility = Visibility.Visible;
            AdLayer.Resume();
        }
    }

    /// <summary>注册 M1 热键：老板键显隐 + 上班锁定开关</summary>
    private void RegisterHotkeys()
    {
        if (_hotkeys == null) return;

        if (HotkeyCombo.TryParse(_cfg.Hotkeys.Toggle, out var toggle))
            _hotkeys.Register(toggle, ToggleBoss);
        else
            Log.Warn($"老板键配置无效: {_cfg.Hotkeys.Toggle}");

        if (HotkeyCombo.TryParse(_cfg.Hotkeys.LockAd, out var lockAd))
            _hotkeys.Register(lockAd, () =>
            {
                _machine.Fire(AppEvent.LockAdToggle);
                TrayLockChanged?.Invoke(_machine.LockAdMode);
            });
        else
            Log.Warn($"上班锁定热键配置无效: {_cfg.Hotkeys.LockAd}");

        // M2：播放暂停 / 音量；M3：上/下一集
        if (HotkeyCombo.TryParse(_cfg.Hotkeys.PlayPause, out var playPause))
            _hotkeys.Register(playPause, () => FireAndForget(_playback.PlayOrPauseAsync()));

        if (HotkeyCombo.TryParse(_cfg.Hotkeys.VolUp, out var volUp))
            _hotkeys.Register(volUp, () =>
            {
                _volumePercent = Math.Min(100, _volumePercent + 10);
                FireAndForget(_playback.SetVolumeAsync(_volumePercent));
            });

        if (HotkeyCombo.TryParse(_cfg.Hotkeys.VolDown, out var volDown))
            _hotkeys.Register(volDown, () =>
            {
                _volumePercent = Math.Max(0, _volumePercent - 10);
                FireAndForget(_playback.SetVolumeAsync(_volumePercent));
            });

        if (HotkeyCombo.TryParse(_cfg.Hotkeys.Next, out var next))
            _hotkeys.Register(next, () => StepItem(1));
        else
            Log.Warn($"下一集热键配置无效: {_cfg.Hotkeys.Next}");

        if (HotkeyCombo.TryParse(_cfg.Hotkeys.Prev, out var prev))
            _hotkeys.Register(prev, () => StepItem(-1));
        else
            Log.Warn($"上一集热键配置无效: {_cfg.Hotkeys.Prev}");
    }

    /// <summary>
    /// 热键切集：推进队列（尽力保存当前进度）→ 状态机迁入 Playing（含广告态，设计书 9.3）。
    /// 队列为空时气泡提示且不触发迁移；切集本身不再弹出托盘气泡（避免气泡遮挡右下角弹窗导致误判 MouseLeave）。
    /// </summary>
    private void StepItem(int offset)
    {
        var item = _playback.StepTo(offset);
        if (item == null)
        {
            BubbleRequested?.Invoke("播放列表为空", "请通过托盘「打开视频链接」或「播放列表」添加视频");
            return;
        }
        _machine.Fire(offset > 0 ? AppEvent.NextItem : AppEvent.PrevItem);
    }

    /// <summary>打开/激活播放列表窗口（托盘触发；单实例，随队列变化自动刷新）</summary>
    public void ShowPlaylist()
    {
        if (_playlistWindow == null || !_playlistWindow.IsLoaded)
            _playlistWindow = new PlaylistWindow(_playback.Playlist, _playback);
        _playlistWindow.Show();
        _playlistWindow.Activate();
    }

    /// <summary>打开/激活设置窗口（托盘触发；保存后经 ReapplySettings 应用变更）</summary>
    public void ShowSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            _settingsWindow = new SettingsWindow(ReapplySettings);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>设置保存后应用：窗口外观/位置、轮播参数、热键重注册（配置已由设置窗口落盘）</summary>
    public void ReapplySettings()
    {
        Title = _cfg.Stealth.WindowTitle;
        Width = _cfg.Window.Width + ShadowMargin * 2;
        Height = _cfg.Window.Height + ShadowMargin * 2;
        Opacity = Math.Clamp(_cfg.Window.Opacity, 0.4, 1);
        Topmost = _cfg.Window.Topmost;
        PlaceWindow(restoreSaved: _cfg.Behavior.RememberPosition);

        AdLayer.UpdateSchedule(_cfg.Behavior.AdIntervalSec, _cfg.Behavior.AdShuffle);
        ApplyAdAppearance();   // 应用透明模式开关

        _hotkeys?.Dispose();
        _hotkeys = new HotkeyManager(this);
        RegisterHotkeys();
        Log.Info("设置变更已应用");
    }

    /// <summary>打开「输入视频链接」对话框并加载（托盘触发；对话框弹出即触发 MouseLeave 回广告态）</summary>
    public async void OpenVideoDialog()
    {
        try
        {
            var dlg = new UrlInputDialog();
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.VideoUrl))
                await _playback.LoadUrlAsync(dlg.VideoUrl);
        }
        catch (Exception ex)
        {
            Log.Error("打开视频链接对话框异常", ex);
        }
    }

    /// <summary>老板键：显示 ↔ 隐藏切换（迁移动作由 OnTransitioned 统一处理）</summary>
    public void ToggleBoss()
    {
        if (_machine.Current == AppStatus.Hidden)
            _machine.Fire(AppEvent.Restore);
        else
            _machine.Fire(AppEvent.BossKey);
    }

    /// <summary>状态迁移的表现层更新：两层互斥显隐、窗口显隐、轮播暂停恢复、视频播放/暂停、工具条调度</summary>
    private void OnTransitioned(AppStatus old, AppStatus next)
    {
        switch (next)
        {
            case AppStatus.Hidden:
                _watcher?.Reset();
                _barShowTimer.Stop();
                _barHideTimer.Stop();
                HideMiniBar();
                FireAndForget(_playback.PauseAsync());   // 老板键：强制暂停
                Hide();
                break;

            case AppStatus.Ad:
                if (old == AppStatus.Hidden) Show();
                _barShowTimer.Stop();
                _barHideTimer.Stop();
                HideMiniBar();
                PlayLayer.Visibility = Visibility.Collapsed;
                ApplyAdAppearance();
                if (_cfg.Behavior.PauseOnLeave) FireAndForget(_playback.PauseAsync());
                break;

            case AppStatus.Playing:
                AdLayer.Pause();
                AdLayer.Visibility = Visibility.Collapsed;
                PlayLayer.Visibility = Visibility.Visible;
                _barShowTimer.Stop();
                _barShowTimer.Start();                   // 悬停满 1s 浮现工具条
                _barHideTimer.Stop();
                FireAndForget(_playback.ResumeAsync());
                break;
        }
    }

    /// <summary>淡入迷你工具条并启动 3s 无操作淡出计时</summary>
    private void ShowMiniBar()
    {
        _barHideTimer.Stop();
        _barHideTimer.Start();
        if (MiniBar.Visibility == Visibility.Visible && MiniBar.Opacity >= 1) return;
        MiniBar.Visibility = Visibility.Visible;
        MiniBar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>淡出并折叠迷你工具条</summary>
    private void HideMiniBar()
    {
        if (MiniBar.Visibility != Visibility.Visible) return;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
        anim.Completed += (s, e) => MiniBar.Visibility = Visibility.Collapsed;
        MiniBar.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>工具条内鼠标移动：重置 3s 淡出计时（视为有操作）</summary>
    private void OnMiniBarMouseMove(object sender, MouseEventArgs e)
    {
        _barHideTimer.Stop();
        _barHideTimer.Start();
    }

    /// <summary>工具条：上一集（与热键同路径，含气泡与状态机迁移）</summary>
    private void OnPrevClick(object sender, RoutedEventArgs e) => StepItem(-1);

    /// <summary>工具条：下一集</summary>
    private void OnNextClick(object sender, RoutedEventArgs e) => StepItem(1);

    /// <summary>工具条：播放/暂停切换</summary>
    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        => FireAndForget(_playback.PlayOrPauseAsync());

    /// <summary>工具条：音量滑条调节（初始化赋值时静默）</summary>
    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVolume) return;
        _volumePercent = (int)Math.Clamp(e.NewValue, 0, 100);
        FireAndForget(_playback.SetVolumeAsync(_volumePercent));
    }

    /// <summary>工具条：隐藏 = 老板键</summary>
    private void OnHideClick(object sender, RoutedEventArgs e) => ToggleBoss();

    /// <summary>播放态右上角关闭按钮 = 老板键</summary>
    private void OnPlayCloseClick(object sender, MouseButtonEventArgs e) => ToggleBoss();

    /// <summary>异步播放控制兜底：异常仅记日志，不打断 UI 流程</summary>
    private void FireAndForget(Task task)
    {
        task.ContinueWith(
            t => Log.Error("播放控制异常", t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>附加 WS_EX_TOOLWINDOW：不出现在任务栏与 Alt+Tab（配合 ShowInTaskbar=false 双保险）</summary>
    private void ApplyToolWindowStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// 定位窗口：优先恢复已保存位置（夹取到主屏工作区），
    /// 否则停靠主屏工作区右下角（视觉留白 = 配置 margin，需补偿阴影留白）。
    /// </summary>
    private void PlaceWindow(bool restoreSaved)
    {
        var wa = SystemParameters.WorkArea;
        var p = _cfg.Window.Position;

        if (restoreSaved && p is { X: not null, Y: not null })
        {
            Left = p.X.Value;
            Top = p.Y.Value;
        }
        else
        {
            Left = wa.Right - _cfg.Window.Margin + ShadowMargin - Width;
            Top = wa.Bottom - _cfg.Window.Margin + ShadowMargin - Height;
        }

        Left = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        Top = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
    }

    /// <summary>Ctrl + 左键拖动窗口；透明模式下改为按住左键直接拖动。拖动结束将位置持久化到配置</summary>
    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // 透明模式：按住左键即可拖动定位；其它模式保持 Ctrl+左键拖动
        if (!_cfg.Window.TransparentMode && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        try
        {
            DragMove();
            _cfg.Window.Position = new PointConfig { X = Left, Y = Top };
            ConfigManager.Save();
        }
        catch (InvalidOperationException)
        {
            // 拖动前鼠标已释放，忽略
        }
    }

    /// <summary>内容区尺寸变化：按圆角裁剪内层，避免图片直角溢出圆角边框</summary>
    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ContentClip.Clip = new RectangleGeometry(
            new Rect(0, 0, ContentClip.ActualWidth, ContentClip.ActualHeight), 7, 7);
    }

    /// <summary>拦截窗口关闭：默认等效老板键；仅退出流程（AllowClose）放行</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        ToggleBoss();
    }

    /// <summary>放行窗口关闭（托盘“退出”前调用）</summary>
    public void AllowClose() => _allowClose = true;

    /// <summary>退出清理：注销热键、停止鼠标监视</summary>
    public void Cleanup()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        _watcher?.Dispose();
        _watcher = null;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
