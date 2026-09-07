using System;
using Microsoft.Win32;

namespace MoyuPopup.Core;

/// <summary>
/// 开机自启管理（F14）：HKCU\...\Run 注册表键，静默启动进广告态。
/// 仅当前用户，无需管理员权限；写入进程自身路径。
/// </summary>
public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AdPopup";

    /// <summary>是否已启用开机自启（Run 键存在且指向本进程）</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string v && v.Equals(ExePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取开机自启状态失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>启用/停用开机自启；失败仅记日志（返回 false）</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
                key.SetValue(ValueName, $"\"{ExePath()}\"");
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName);
            Log.Info($"开机自启: {(enabled ? "开" : "关")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
            return false;
        }
    }

    /// <summary>当前进程主模块路径（Run 键值内容）</summary>
    private static string ExePath() => Environment.ProcessPath ?? "";
}
