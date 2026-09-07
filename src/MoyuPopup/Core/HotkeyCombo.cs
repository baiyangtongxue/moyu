using System;
using System.Collections.Generic;

namespace MoyuPopup.Core;

/// <summary>全局热键组合（如 Ctrl+Alt+V），支持解析与规范显示</summary>
public readonly struct HotkeyCombo : IEquatable<HotkeyCombo>
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;

    /// <summary>MOD_* 修饰键组合位</summary>
    public uint Modifiers { get; }

    /// <summary>Win32 虚拟键码</summary>
    public uint VirtualKey { get; }

    /// <summary>规范显示文本（如 Ctrl+Alt+V）</summary>
    public string Display { get; }

    /// <summary>构造热键组合</summary>
    public HotkeyCombo(uint modifiers, uint virtualKey, string display)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        Display = display;
    }

    /// <summary>解析热键描述（Ctrl/Alt/Shift/Win + 键名），要求至少一个修饰键且恰有一个主键</summary>
    public static bool TryParse(string? text, out HotkeyCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint mods = 0;
        uint vk = 0;
        var keyDisplay = string.Empty;

        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.ToLowerInvariant();
            switch (s)
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win" or "windows": mods |= MOD_WIN; continue;
                default:
                    if (vk != 0 || !TryParseVk(s, out vk, out keyDisplay)) return false;
                    continue;
            }
        }

        if (vk == 0 || mods == 0) return false;

        var parts = new List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(keyDisplay);
        combo = new HotkeyCombo(mods, vk, string.Join("+", parts));
        return true;
    }

    /// <summary>解析单个键名（字母/数字/F1~F24/Space/方向键等）为虚拟键码</summary>
    private static bool TryParseVk(string s, out uint vk, out string display)
    {
        vk = 0;
        display = s.ToUpperInvariant();

        if (s.Length == 1)
        {
            var c = char.ToUpperInvariant(s[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                vk = c;                 // 字母与数字的 VK 码与 ASCII 相同
                display = c.ToString();
                return true;
            }
        }

        if (s.Length >= 2 && (s[0] == 'f' || s[0] == 'F')
            && uint.TryParse(s.AsSpan(1), out var f) && f >= 1 && f <= 24)
        {
            vk = 0x70 + (f - 1);        // VK_F1 = 0x70
            display = "F" + f;
            return true;
        }

        switch (s)
        {
            case "space" or "spacebar": vk = 0x20; display = "Space"; return true;
            case "left": vk = 0x25; display = "Left"; return true;
            case "up": vk = 0x26; display = "Up"; return true;
            case "right": vk = 0x27; display = "Right"; return true;
            case "down": vk = 0x28; display = "Down"; return true;
            case "home": vk = 0x24; display = "Home"; return true;
            case "end": vk = 0x23; display = "End"; return true;
            case "tab": vk = 0x09; display = "Tab"; return true;
            // 常见符号键：避免与显卡「屏幕旋转」的 Ctrl+Alt+方向键冲突
            case "comma" or ",": vk = 0xBC; display = ","; return true;
            case "period" or "dot" or ".": vk = 0xBE; display = "."; return true;
            case "minus" or "-": vk = 0xBD; display = "-"; return true;
            case "equal" or "plus" or "=": vk = 0xBB; display = "="; return true;
        }
        return vk != 0;
    }

    /// <summary>按修饰键与虚拟键判断等价</summary>
    public bool Equals(HotkeyCombo other) => Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;

    /// <summary>对象等价比较</summary>
    public override bool Equals(object? obj) => obj is HotkeyCombo other && Equals(other);

    /// <summary>哈希（修饰键 ^ 虚拟键）</summary>
    public override int GetHashCode() => (int)(Modifiers ^ (VirtualKey << 8));
}
