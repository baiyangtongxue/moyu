namespace MoyuPopup.Core;

/// <summary>应用配置根对象（对应设计书 8.2 config.json）</summary>
public sealed class AppConfig
{
    /// <summary>窗口外观与停靠</summary>
    public WindowConfig Window { get; set; } = new();

    /// <summary>交互行为</summary>
    public BehaviorConfig Behavior { get; set; } = new();

    /// <summary>隐蔽性</summary>
    public StealthConfig Stealth { get; set; } = new();

    /// <summary>全局热键</summary>
    public HotkeyConfig Hotkeys { get; set; } = new();

    /// <summary>视频源（M2 起生效）</summary>
    public SourcesConfig Sources { get; set; } = new();

    /// <summary>启动行为</summary>
    public StartupConfig Startup { get; set; } = new();
}

/// <summary>窗口外观与停靠配置</summary>
public sealed class WindowConfig
{
    /// <summary>内容区宽度（DIP，不含阴影留白）</summary>
    public double Width { get; set; } = 400;

    /// <summary>内容区高度（DIP）</summary>
    public double Height { get; set; } = 225;

    /// <summary>距屏幕右/下边缘留白（DIP）</summary>
    public double Margin { get; set; } = 24;

    /// <summary>窗口整体不透明度 0~1</summary>
    public double Opacity { get; set; } = 0.95;

    /// <summary>是否置顶</summary>
    public bool Topmost { get; set; } = true;

    /// <summary>已保存位置（null 表示未保存，使用默认停靠）</summary>
    public PointConfig? Position { get; set; }

    /// <summary>锁定 16:9 尺寸（预留，M4 设置界面使用）</summary>
    public bool SizeLocked { get; set; } = true;
}

/// <summary>窗口坐标</summary>
public sealed class PointConfig
{
    /// <summary>所在显示器设备名（多屏精细化定位，M4 使用）</summary>
    public string? Screen { get; set; }

    /// <summary>窗口 Left（DIP）</summary>
    public double? X { get; set; }

    /// <summary>窗口 Top（DIP）</summary>
    public double? Y { get; set; }
}

/// <summary>交互行为配置</summary>
public sealed class BehaviorConfig
{
    /// <summary>悬停多少毫秒后才切换到播放态（防路过误触）</summary>
    public int HoverDelayMs { get; set; } = 300;

    /// <summary>鼠标移开是否暂停视频（M2 生效）</summary>
    public bool PauseOnLeave { get; set; } = true;

    /// <summary>启动时是否进入上班锁定（恒广告态）</summary>
    public bool LockAdMode { get; set; } = false;

    /// <summary>广告轮播间隔（秒）</summary>
    public int AdIntervalSec { get; set; } = 5;

    /// <summary>广告是否随机顺序轮播</summary>
    public bool AdShuffle { get; set; } = true;

    /// <summary>默认音量百分比（M2 生效，防突然外放）</summary>
    public int DefaultVolume { get; set; } = 40;

    /// <summary>是否记忆窗口位置</summary>
    public bool RememberPosition { get; set; } = true;
}

/// <summary>隐蔽性配置</summary>
public sealed class StealthConfig
{
    /// <summary>伪装窗口标题</summary>
    public string WindowTitle { get; set; } = "广告推广";

    /// <summary>托盘图标是否可见</summary>
    public bool TrayVisible { get; set; } = true;

    /// <summary>托盘悬停提示（伪装文案）</summary>
    public string TrayTooltip { get; set; } = "广告服务";

    /// <summary>老板键动作（hide=隐藏窗口 / ad=仅切广告，M1 仅支持 hide）</summary>
    public string BossKeyAction { get; set; } = "Hide";
}

/// <summary>全局热键配置（M1 注册 Toggle/LockAd，其余 M2/M3 启用）</summary>
public sealed class HotkeyConfig
{
    /// <summary>显示/隐藏（老板键）</summary>
    public string Toggle { get; set; } = "Ctrl+Alt+V";

    /// <summary>下一集（M3）</summary>
    public string Next { get; set; } = "Ctrl+Alt+Right";

    /// <summary>上一集（M3）</summary>
    public string Prev { get; set; } = "Ctrl+Alt+Left";

    /// <summary>播放/暂停（M2）</summary>
    public string PlayPause { get; set; } = "Ctrl+Alt+Space";

    /// <summary>音量+（M2）</summary>
    public string VolUp { get; set; } = "Ctrl+Alt+Up";

    /// <summary>音量-（M2）</summary>
    public string VolDown { get; set; } = "Ctrl+Alt+Down";

    /// <summary>上班锁定开关</summary>
    public string LockAd { get; set; } = "Ctrl+Alt+L";
}

/// <summary>视频源配置（M2 起生效）</summary>
public sealed class SourcesConfig
{
    /// <summary>启用的平台（顺序即适配器注册顺序）</summary>
    public string[] Enabled { get; set; } = { "bilibili", "tencent", "custom", "douyin", "iqiyi" };

    /// <summary>是否启用登录窗口（Cookie 共享看 VIP 内容）</summary>
    public bool LoginEnabled { get; set; } = false;
}

/// <summary>启动行为配置</summary>
public sealed class StartupConfig
{
    /// <summary>开机自启（M4 实现）</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>启动后先进入广告伪装态</summary>
    public bool StartInAdMode { get; set; } = true;
}
