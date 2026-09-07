using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using WF = System.Windows.Forms;

namespace MoyuPopup.Presentation;

/// <summary>托盘控制：中性图标 + 右键菜单（显隐 / 上班锁定 / 设置占位 / 退出）</summary>
public sealed class TrayController : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ToolStripMenuItem _lockItem;
    private bool _disposed;

    /// <summary>显示/隐藏 切换请求（老板键）</summary>
    public event Action? ToggleRequested;

    /// <summary>打开视频链接对话框请求（M2）</summary>
    public event Action? OpenVideoRequested;

    /// <summary>打开播放列表窗口请求（M3）</summary>
    public event Action? PlaylistRequested;

    /// <summary>打开设置窗口请求（M4）</summary>
    public event Action? SettingsRequested;

    /// <summary>上班锁定开关请求</summary>
    public event Action? LockAdToggleRequested;

    /// <summary>退出请求</summary>
    public event Action? ExitRequested;

    /// <summary>创建托盘图标与右键菜单（文案保持中性伪装）</summary>
    public TrayController(string tooltip)
    {
        _icon = new WF.NotifyIcon
        {
            Icon = BuildIcon(),
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip,
            Visible = true,
        };

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("打开视频链接…", null, (s, e) => OpenVideoRequested?.Invoke());
        menu.Items.Add("播放列表…", null, (s, e) => PlaylistRequested?.Invoke());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("显示 / 隐藏", null, (s, e) => ToggleRequested?.Invoke());
        _lockItem = new WF.ToolStripMenuItem("上班锁定（恒广告态）") { CheckOnClick = true };
        _lockItem.Click += (s, e) => LockAdToggleRequested?.Invoke();
        menu.Items.Add(_lockItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(new WF.ToolStripMenuItem("设置…") { Enabled = true });
        menu.Items[menu.Items.Count - 1].Click += (s, e) => SettingsRequested?.Invoke();
        menu.Items.Add("退出", null, (s, e) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;

        _icon.DoubleClick += (s, e) => ToggleRequested?.Invoke();
    }

    /// <summary>同步“上班锁定”勾选状态（热键触发时回调）</summary>
    public void SetLockChecked(bool on)
    {
        if (_lockItem.Checked != on) _lockItem.Checked = on;
    }

    /// <summary>托盘气泡提示（失败/切集/引导通知；伪装文案不受影响）</summary>
    public void ShowBubble(string title, string text)
        => _icon.ShowBalloonTip(3000, title, text, WF.ToolTipIcon.None);

    /// <summary>程序化生成中性托盘图标（深色圆角 + 白色播放三角，随进程生命周期回收）</summary>
    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(0xFF, 0x2B, 0x2F, 0x36));
            using var path = new GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90);
            path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90);
            path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);

            using var fg = new SolidBrush(Color.White);
            g.FillPolygon(fg, new[] { new PointF(12, 9), new PointF(24, 16), new PointF(12, 23) });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>隐藏图标并释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
