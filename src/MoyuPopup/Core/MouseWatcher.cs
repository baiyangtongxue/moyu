using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MoyuPopup.Core;

/// <summary>
/// 鼠标监视器：100ms 轮询光标是否停留在窗口内。
/// WebView2/无边框窗口下事件不可靠，轮询 + Win32 判定最稳（设计书 3.3/5.4）。
/// </summary>
public sealed class MouseWatcher : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private int _insideMs;
    private bool _entered;

    /// <summary>触发悬停所需的连续停留毫秒数（防路过误触）</summary>
    public int HoverDelayMs { get; set; } = 300;

    /// <summary>光标在窗口内持续停留达到阈值</summary>
    public event Action? Entered;

    /// <summary>光标离开窗口（立即触发）</summary>
    public event Action? Left;

    /// <summary>构造并准备 100ms 轮询定时器（Background 优先级，不抢占 UI）</summary>
    public MouseWatcher(Window window)
    {
        _window = window;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>开始监视（确保窗口句柄创建并启动定时器）</summary>
    public void Start()
    {
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        _timer.Start();
    }

    /// <summary>重置内部悬停计数（窗口隐藏/状态复位时调用，避免恢复后误触发）</summary>
    public void Reset()
    {
        _insideMs = 0;
        _entered = false;
    }

    /// <summary>停止监视并释放</summary>
    public void Dispose() => _timer.Stop();

    /// <summary>轮询：判定光标是否在窗口内，维护 Entered/Left 事件</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        if (IsCursorInside())
        {
            _insideMs += 100;
            if (!_entered && _insideMs >= HoverDelayMs)
            {
                _entered = true;
                Entered?.Invoke();
            }
        }
        else
        {
            if (_entered)
            {
                _entered = false;
                Left?.Invoke();
            }
            _insideMs = 0;
        }
    }

    /// <summary>光标位于窗口可视矩形内，且该点最顶层窗口属于本窗口（防被更高层窗口遮挡时误判）</summary>
    private bool IsCursorInside()
    {
        if (_hwnd == IntPtr.Zero || !_window.IsVisible) return false;
        if (!GetCursorPos(out var pt)) return false;
        if (!GetWindowRect(_hwnd, out var rc)) return false;
        if (pt.X < rc.Left || pt.X >= rc.Right || pt.Y < rc.Top || pt.Y >= rc.Bottom) return false;
        var root = GetAncestor(WindowFromPoint(pt), GA_ROOT);
        return root == _hwnd;
    }

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
}
