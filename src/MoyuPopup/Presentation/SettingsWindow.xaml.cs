using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoyuPopup.Core;

namespace MoyuPopup.Presentation;

/// <summary>
/// 设置窗口（设计书 6.2）：常规 / 窗口 / 热键录制（含冲突检测）/ 视频源 / 广告 / 关于。
/// 保存后写回 ConfigManager 并回调（主窗口应用尺寸/热键/轮播参数，注册表落盘自启）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly Action _onSaved;

    /// <summary>构造：装载当前配置到控件；onSaved 在保存成功后回调</summary>
    public SettingsWindow(Action onSaved)
    {
        InitializeComponent();
        _onSaved = onSaved;
        _cfg = ConfigManager.Config;
        LoadFromConfig();
    }

    /// <summary>把配置值填入各控件（热键为只读录制框）</summary>
    private void LoadFromConfig()
    {
        ChkAutoStart.IsChecked = AutoStartManager.IsEnabled();
        ChkStartInAd.IsChecked = _cfg.Startup.StartInAdMode;

        TxtWidth.Text = _cfg.Window.Width.ToString("0");
        TxtHeight.Text = _cfg.Window.Height.ToString("0");
        SldOpacity.Value = Math.Clamp(_cfg.Window.Opacity, 0.4, 1);
        LblOpacity.Text = SldOpacity.Value.ToString("0.00");
        TxtHoverDelay.Text = _cfg.Behavior.HoverDelayMs.ToString();
        ChkTopmost.IsChecked = _cfg.Window.Topmost;
        ChkPauseOnLeave.IsChecked = _cfg.Behavior.PauseOnLeave;
        ChkRememberPos.IsChecked = _cfg.Behavior.RememberPosition;

        HkToggle.Text = _cfg.Hotkeys.Toggle;
        HkNext.Text = _cfg.Hotkeys.Next;
        HkPrev.Text = _cfg.Hotkeys.Prev;
        HkPlayPause.Text = _cfg.Hotkeys.PlayPause;
        HkVolUp.Text = _cfg.Hotkeys.VolUp;
        HkVolDown.Text = _cfg.Hotkeys.VolDown;
        HkLockAd.Text = _cfg.Hotkeys.LockAd;

        var enabled = _cfg.Sources.Enabled;
        ChkBilibili.IsChecked = enabled.Contains("bilibili");
        ChkTencent.IsChecked = enabled.Contains("tencent");
        ChkDouyin.IsChecked = enabled.Contains("douyin");
        ChkIqiyi.IsChecked = enabled.Contains("iqiyi");
        ChkLoginEnabled.IsChecked = _cfg.Sources.LoginEnabled;

        TxtAdInterval.Text = _cfg.Behavior.AdIntervalSec.ToString();
        ChkAdShuffle.IsChecked = _cfg.Behavior.AdShuffle;

        LblVersion.Text = $"广告弹窗投放 v{typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)}";
    }

    /// <summary>热键录制：捕获修饰键 + 主键，生成规范串（必须带修饰键，防误占普通键）</summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Escape)
            return;

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            ((TextBox)sender).Text = "(需含 Ctrl/Alt/Shift/Win)";
            return;
        }

        // 仅允许 HotkeyCombo 可解析的键：字母/数字/F1~F24/Space/方向键/Home/End
        var name = KeyName(key);
        if (name == null)
        {
            ((TextBox)sender).Text = "(不支持该键)";
            return;
        }

        var text = string.Concat(
            mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "",
            mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "",
            mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "",
            mods.HasFlag(ModifierKeys.Windows) ? "Win+" : "",
            name);
        ((TextBox)sender).Text = text;
    }

    /// <summary>WPF Key → 配置串键名（仅返回 HotkeyCombo 支持的键）</summary>
    private static string? KeyName(Key key)
    {
        return key switch
        {
            Key.A => "A", Key.B => "B", Key.C => "C", Key.D => "D", Key.E => "E",
            Key.F => "F", Key.G => "G", Key.H => "H", Key.I => "I", Key.J => "J",
            Key.K => "K", Key.L => "L", Key.M => "M", Key.N => "N", Key.O => "O",
            Key.P => "P", Key.Q => "Q", Key.R => "R", Key.S => "S", Key.T => "T",
            Key.U => "U", Key.V => "V", Key.W => "W", Key.X => "X", Key.Y => "Y",
            Key.Z => "Z",
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
            Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
            Key.Space => "Space",
            Key.Left => "Left", Key.Up => "Up", Key.Right => "Right", Key.Down => "Down",
            Key.Home => "Home", Key.End => "End",
            _ when key is >= Key.F1 and <= Key.F24 => ((int)key - (int)Key.F1 + 1).ToString(),
            _ => null,
        };
    }

    /// <summary>打开登录窗口（未勾选启用时提示）</summary>
    private void OnOpenLogin(object sender, RoutedEventArgs e)
    {
        if (ChkLoginEnabled.IsChecked != true)
        {
            MessageBox.Show("请先勾选「启用登录窗口」并保存。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new LoginWindow().Show();
    }

    /// <summary>打开广告素材目录（资源管理器；不存在则先创建）</summary>
    private void OnOpenAdsDir(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(ConfigManager.AppDataDir, "ads");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开广告素材目录失败", ex);
        }
    }

    /// <summary>保存：校验 → 写回配置/注册表 → 落盘 → 回调主窗口应用 → 关闭</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        // 数值校验
        if (!int.TryParse(TxtWidth.Text, out var w) || w is < 200 or > 800)
        {
            MessageBox.Show("宽度需在 200~800 之间", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(TxtHeight.Text, out var h) || h is < 120 or > 600)
        {
            MessageBox.Show("高度需在 120~600 之间", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(TxtHoverDelay.Text, out var hover) || hover is < 0 or > 5000)
        {
            MessageBox.Show("悬停延时需在 0~5000 之间", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(TxtAdInterval.Text, out var adSec) || adSec < 2)
        {
            MessageBox.Show("轮播间隔至少 2 秒", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 热键解析 + 冲突检测
        var hotkeys = new Dictionary<string, string>
        {
            ["toggle"] = HkToggle.Text.Trim(),
            ["next"] = HkNext.Text.Trim(),
            ["prev"] = HkPrev.Text.Trim(),
            ["playPause"] = HkPlayPause.Text.Trim(),
            ["volUp"] = HkVolUp.Text.Trim(),
            ["volDown"] = HkVolDown.Text.Trim(),
            ["lockAd"] = HkLockAd.Text.Trim(),
        };
        var parsed = new Dictionary<string, HotkeyCombo>();
        foreach (var (name, text) in hotkeys)
        {
            if (!HotkeyCombo.TryParse(text, out var combo))
            {
                MessageBox.Show($"热键「{name}」无效：{text}\n需形如 Ctrl+Alt+V", "校验失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (parsed.ContainsValue(combo))
            {
                var dup = parsed.First(p => p.Value.Equals(combo)).Key;
                MessageBox.Show($"热键冲突：「{name}」与「{dup}」相同", "校验失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            parsed[name] = combo;
        }

        // 写回配置
        _cfg.Startup.StartInAdMode = ChkStartInAd.IsChecked == true;
        _cfg.Window.Width = w;
        _cfg.Window.Height = h;
        _cfg.Window.Opacity = SldOpacity.Value;
        _cfg.Window.Topmost = ChkTopmost.IsChecked == true;
        _cfg.Behavior.HoverDelayMs = hover;
        _cfg.Behavior.PauseOnLeave = ChkPauseOnLeave.IsChecked == true;
        _cfg.Behavior.RememberPosition = ChkRememberPos.IsChecked == true;
        _cfg.Hotkeys.Toggle = hotkeys["toggle"];
        _cfg.Hotkeys.Next = hotkeys["next"];
        _cfg.Hotkeys.Prev = hotkeys["prev"];
        _cfg.Hotkeys.PlayPause = hotkeys["playPause"];
        _cfg.Hotkeys.VolUp = hotkeys["volUp"];
        _cfg.Hotkeys.VolDown = hotkeys["volDown"];
        _cfg.Hotkeys.LockAd = hotkeys["lockAd"];

        var enabled = new List<string>();
        if (ChkBilibili.IsChecked == true) enabled.Add("bilibili");
        if (ChkTencent.IsChecked == true) enabled.Add("tencent");
        if (ChkDouyin.IsChecked == true) enabled.Add("douyin");
        if (ChkIqiyi.IsChecked == true) enabled.Add("iqiyi");
        _cfg.Sources.Enabled = enabled.Count > 0
            ? enabled.ToArray()
            : new[] { "custom" };   // 全关时仅保留 custom 兜底
        _cfg.Sources.LoginEnabled = ChkLoginEnabled.IsChecked == true;
        _cfg.Behavior.AdIntervalSec = adSec;
        _cfg.Behavior.AdShuffle = ChkAdShuffle.IsChecked == true;

        if (!AutoStartManager.Set(ChkAutoStart.IsChecked == true))
        {
            MessageBox.Show("开机自启写入注册表失败（详见日志）", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ConfigManager.Save();
        Log.Info("设置已保存");
        _onSaved?.Invoke();
        Close();
    }

    /// <summary>透明度滑条联动数值标签</summary>
    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblOpacity != null) LblOpacity.Text = e.NewValue.ToString("0.00");
    }
}
