using System;

namespace MoyuPopup.Core;

/// <summary>应用状态：隐藏 / 广告伪装 / 悬停播放</summary>
public enum AppStatus
{
    /// <summary>老板键隐藏（窗口不可见，视频强制暂停）</summary>
    Hidden,

    /// <summary>广告伪装态（轮播广告，默认态）</summary>
    Ad,

    /// <summary>悬停播放态（M1 为占位层，M2 接入 WebView2）</summary>
    Playing,
}

/// <summary>状态迁移事件</summary>
public enum AppEvent
{
    /// <summary>光标在窗口内悬停达标</summary>
    MouseEnter,

    /// <summary>光标离开窗口</summary>
    MouseLeave,

    /// <summary>老板键（隐藏）</summary>
    BossKey,

    /// <summary>恢复显示</summary>
    Restore,

    /// <summary>上班锁定开关</summary>
    LockAdToggle,

    /// <summary>下一集（M3 生效）</summary>
    NextItem,

    /// <summary>上一集（M3 生效）</summary>
    PrevItem,
}

/// <summary>状态机：维护三态迁移（规则见设计书 9.2），迁移时广播 Transitioned</summary>
public sealed class AppStateMachine
{
    /// <summary>当前状态（初始为广告伪装态）</summary>
    public AppStatus Current { get; private set; } = AppStatus.Ad;

    /// <summary>上班锁定（恒广告态，悬停不播放）</summary>
    public bool LockAdMode { get; private set; }

    /// <summary>状态迁移通知 (old, new)，UI 线程回调</summary>
    public event Action<AppStatus, AppStatus>? Transitioned;

    /// <summary>设置初始锁定态（仅启动时使用）</summary>
    public void SetLockAd(bool on) => LockAdMode = on;

    /// <summary>触发事件并执行迁移；无效事件保持原状态</summary>
    public void Fire(AppEvent evt)
    {
        var next = evt switch
        {
            // 悬停达标：Ad → Playing（锁定时不切换）
            AppEvent.MouseEnter when Current == AppStatus.Ad && !LockAdMode => AppStatus.Playing,
            // 移开即停：Playing → Ad
            AppEvent.MouseLeave when Current == AppStatus.Playing => AppStatus.Ad,
            // 老板键：任意可见态 → Hidden
            AppEvent.BossKey when Current != AppStatus.Hidden => AppStatus.Hidden,
            // 恢复：Hidden → Ad（恢复后必回广告态，安全第一）
            AppEvent.Restore when Current == AppStatus.Hidden => AppStatus.Ad,
            // 热键切集：非 Hidden 态均可切集并进入播放（含 Ad 态，设计书 9.3）
            AppEvent.NextItem when Current != AppStatus.Hidden => AppStatus.Playing,
            AppEvent.PrevItem when Current != AppStatus.Hidden => AppStatus.Playing,
            _ => Current,
        };

        if (evt == AppEvent.LockAdToggle)
        {
            LockAdMode = !LockAdMode;
            Log.Info($"上班锁定: {(LockAdMode ? "开" : "关")}");
            if (LockAdMode && Current == AppStatus.Playing) next = AppStatus.Ad;
        }

        if (next != Current)
        {
            var old = Current;
            Current = next;
            Log.Info($"状态迁移: {old} → {next} (事件: {evt})");
            Transitioned?.Invoke(old, next);
        }
    }
}
