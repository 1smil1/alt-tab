using System;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// Parses the settings hotkey strings ("Alt+`", "Alt+Tab",
/// "Ctrl+Shift+F5", …) into Win32 virtual-key codes. v1 hook model:
/// the overlay trigger must include Alt (Alt-up = commit), so the
/// modifier part is validated but only the KEY drives registration.
/// </summary>
public static class HotkeyParser
{
    /// <summary>Parse "Mod+Mod+Key". Returns the virtual-key code of the
    /// final key segment. Accepts friendly names (Tab, `- = [ ; ' , .
    /// / Space, F1..F24, letters, digits) and raw forms (Vk0xBD).</summary>
    public static bool TryParseKey(string? text, out uint vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var segs = text.Split('+', StringSplitOptions.TrimEntries);
        if (segs.Length == 0) return false;
        return TryKeyToVk(segs[^1], out vk);
    }

    /// <summary>The trigger must contain Alt (hook model: Alt-up commits;
    /// non-Alt triggers would need a different commit story).</summary>
    public static bool HasAlt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true; // default Alt+`
        foreach (var seg in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (seg.Equals("Alt", StringComparison.OrdinalIgnoreCase)
                || seg.Equals("Menu", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Is the configured hotkey exactly Alt+Tab? (Drives the
    /// SuppressAltTab 自动规则: Alt+Tab 热键 ⇒ 默认拦截系统 Alt+Tab.)</summary>
    public static bool IsAltTab(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var segs = text.Split('+', StringSplitOptions.TrimEntries);
        return segs.Length == 2
            && segs[0].Equals("Alt", StringComparison.OrdinalIgnoreCase)
            && segs[1].Equals("Tab", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Quick-jump modifier name → the virtual keys that carry it
    /// (left+right+shared). Unknown names fall back to Alt.</summary>
    public static uint[] ModifierKeys(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "ctrl" or "control" => new uint[] { 0x11, 0xA2, 0xA3 },   // VK_CONTROL, L/R
        "shift" => new uint[] { 0x10, 0xA0, 0xA1 },
        "win" or "windows" or "meta" => new uint[] { 0x5B, 0x5C }, // L/R win
        _ => new uint[] { 0x12, 0xA4, 0xA5 },                     // VK_MENU Alt, L/R
    };

    private static bool TryKeyToVk(string key, out uint vk)
    {
        vk = key.ToUpperInvariant() switch
        {
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "SPACE" => 0x20,
            "BACKSPACE" or "BACK" => 0x08,
            "CAPSLOCK" or "CAPS" => 0x14,
            "ESC" or "ESCAPE" => 0x1B,
            "PGUP" or "PAGEUP" => 0x21,
            "PGDN" or "PAGEDOWN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            "INS" or "INSERT" => 0x2D,
            "DEL" or "DELETE" => 0x2E,
            "PRINTSCREEN" or "PRTSC" => 0x2C,
            "SCROLLLOCK" => 0x91,
            "PAUSE" => 0x13,
            "`" or "OEM3" or "BACKQUOTE" or "~" => 0xC0,
            "-" or "OEMINUS" or "MINUS" or "_" => 0xBD,
            "=" or "OEMPLUS" or "EQUAL" or "+" => 0xBB,
            "[" or "OEM4" or "{" => 0xDB,
            "]" or "OEM6" or "}" => 0xDD,
            "\\" or "OEM5" or "|" => 0xDC,
            ";" or "OEM1" or ":" => 0xBA,
            "'" or "OEM7" or "\"" => 0xDE,
            "," or "OEMCOMMA" or "<" => 0xBC,
            "." or "OEMPERIOD" or ">" => 0xBE,
            "/" or "OEM2" or "?" => 0xBF,
            _ => 0,
        };
        if (vk != 0) return true;

        // F1..F24
        if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f')
            && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24)
        {
            vk = (uint)(0x6F + f); // VK_F1 = 0x70
            return true;
        }
        // Single letter / digit
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }        // 0x41+
            if (c is >= '0' and <= '9') { vk = c; return true; }        // 0x30+
        }
        // Raw "Vk0xBD" form
        if (key.StartsWith("VK0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(key[4..], System.Globalization.NumberStyles.HexNumber,
                null, out var raw))
        {
            vk = raw;
            return true;
        }
        return false;
    }
}
