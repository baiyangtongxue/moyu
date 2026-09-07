using System;
using System.IO;
using System.Windows;
using MoyuPopup.Core;
using MoyuPopup.Presentation;

namespace MoyuPopup;

/// <summary>应用入口：启动编排与退出清理</summary>
public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private TrayController? _tray;
    private PopupWindow? _window;

    /// <summary>启动流程：单实例检查 → 日志 → 配置 → 默认广告素材 → 状态机/窗口 → 托盘</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _guard = new SingleInstanceGuard();
        if (!_guard.TryAcquire())
        {
            MessageBox.Show("摸鱼视频弹窗已在运行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Init(Path.Combine(ConfigManager.AppDataDir, "logs"));
        var cfg = ConfigManager.Load();

        try
        {
            AdContentFactory.EnsureDefaultAds(Path.Combine(ConfigManager.AppDataDir, "ads"));
        }
        catch (Exception ex)
        {
            Log.Error("生成默认广告素材失败（将使用内存兜底素材）", ex);
        }

        var machine = new AppStateMachine();
        machine.SetLockAd(cfg.Behavior.LockAdMode);

        var playlist = new PlaylistManager();
        playlist.Load();

        _window = new PopupWindow(cfg, machine, playlist);

        _tray = new TrayController(cfg.Stealth.TrayTooltip);
        _tray.OpenVideoRequested += () => _window.OpenVideoDialog();
        _tray.PlaylistRequested += () => _window.ShowPlaylist();
        _tray.SettingsRequested += () => _window.ShowSettings();
        _tray.ToggleRequested += _window.ToggleBoss;
        _tray.LockAdToggleRequested += () =>
        {
            machine.Fire(AppEvent.LockAdToggle);
            _tray?.SetLockChecked(machine.LockAdMode);
        };
        _tray.ExitRequested += ExitApp;
        _window.TrayLockChanged += on => _tray.SetLockChecked(on);
        _window.BubbleRequested += (t, m) => _tray.ShowBubble(t, m);   // 播放失败/切集通知

        _window.Show();
        Log.Info("App 启动完成");
    }

    /// <summary>退出：放行窗口关闭后停止应用</summary>
    private void ExitApp()
    {
        _window?.AllowClose();
        Shutdown();
    }

    /// <summary>退出清理：配置落盘、注销热键、释放托盘与单实例锁</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try { ConfigManager.Save(); } catch (Exception ex) { Log.Error("配置保存失败", ex); }
        _window?.Cleanup();
        _tray?.Dispose();
        _guard?.Dispose();
        Log.Info("App 退出");
        base.OnExit(e);
    }
}
