using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MoyuPopup.Core;

/// <summary>全局热键管理：RegisterHotKey + WM_HOTKEY 消息分发（窗口未聚焦也生效）</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    /// <summary>挂接到指定窗口的消息循环（热键消息由该窗口接收）</summary>
    public HotkeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd) ?? throw new InvalidOperationException("无法获取 HwndSource");
        _source.AddHook(WndProc);
    }

    /// <summary>注册全局热键；冲突或失败返回 false（不打断流程，仅记日志）</summary>
    public bool Register(HotkeyCombo combo, Action callback)
    {
        var id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, (int)(combo.Modifiers | MOD_NOREPEAT), (int)combo.VirtualKey))
        {
            _nextId--;
            Log.Warn($"热键注册失败（可能被其他软件占用）: {combo.Display}");
            return false;
        }
        _actions[id] = callback;
        Log.Info($"热键已注册: {combo.Display}");
        return true;
    }

    /// <summary>窗口消息处理：WM_HOTKEY → 查表回调</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var cb))
        {
            cb();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>注销全部热键并摘除消息钩子</summary>
    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
