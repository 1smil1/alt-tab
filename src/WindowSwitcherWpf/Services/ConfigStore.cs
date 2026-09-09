using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using WindowSwitcherWpf.Models;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// Settings persistence backend: reads/writes config.json in
/// %APPDATA%\WindowSwitcherWpf (与 order.json 并列). Edited exclusively
/// through the Settings window (设置窗口) — not a hand-editable portable
/// file. A legacy exe-adjacent config.json (v1.0 便携布局) is migrated
/// here once, then removed. Chinese text is written verbatim (not \uXXXX).
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public ConfigStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowSwitcherWpf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "config.json");
        MigrateFromExeDir();
    }

    public string FilePath => _path;

    /// <summary>One-time upgrade from the v1.0 exe-portable layout: with no
    /// config.json on the APPDATA side yet, copy the exe-folder one over
    /// (真实堆叠/热键必须原样保留), then delete the original — the exe-side
    /// file is retired. APPDATA always wins if it already exists; a failed
    /// delete just leaves the stale copy behind (harmless).</summary>
    private void MigrateFromExeDir()
    {
        try
        {
            if (File.Exists(_path)) return;
            var legacy = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (!File.Exists(legacy)) return;
            File.Copy(legacy, _path);
            Log.Info("Config", $"migrated legacy config.json from {legacy}");
            File.Delete(legacy);
        }
        catch (Exception ex)
        {
            Log.Exception("Config", ex);
        }
    }

    public ConfigFile Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var def = DefaultConfig();
                Save(def);
                Log.Info("Config", "created default config.json");
                return def;
            }
            var json = File.ReadAllText(_path);
            var cfg = JsonSerializer.Deserialize<ConfigFile>(json, JsonOptions);
            cfg ??= DefaultConfig();
            // 旧设置文件缺新字段时反序列化得 null (不跑属性初始化器)
            // → 补默认值, 与 "文件不存在" 路径行为一致.
            var dft = DefaultConfig();
            cfg.Hotkey ??= dft.Hotkey;
            cfg.QuickJumpModifier ??= dft.QuickJumpModifier;
            Log.Info("Config", $"loaded config: templates={cfg.Templates.Count} sortMethods={cfg.SortMethods.Count}");
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Exception("Config", ex);
            return DefaultConfig();
        }
    }

    public void Save(ConfigFile cfg)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(cfg, JsonOptions));
            Log.Info("Config", "saved config.json");
        }
        catch (Exception ex)
        {
            Log.Exception("Config", ex);
        }
    }

    /// <summary>Fresh-install defaults: hotkeys only. Templates and smart
    /// sort methods start empty — they are added via the Settings window
    /// (示例条目是手编时代的产物; 设置窗口时代默认从零开始).</summary>
    public static ConfigFile DefaultConfig() => new()
    {
        Hotkey = "Alt+`",
        QuickJumpModifier = "Alt",
        SuppressAltTab = null,
    };
}
