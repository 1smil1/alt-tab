using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using WindowSwitcherWpf.Models;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// Exe-portable config: reads/writes config.json next to the exe
/// (轻量化便携 — templates/sort methods travel with the exe folder).
/// Creates an example default on first run. Chinese text is written
/// verbatim (not \uXXXX) so the file stays hand-editable.
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
        var dir = AppContext.BaseDirectory;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "config.json");
    }

    public string FilePath => _path;

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
            // 旧 config.json 缺新字段时反序列化得 null (不跑属性初始化器)
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

    /// <summary>First-run example: one template and two sort methods whose
    /// names explain the semantics; user edits this file directly.</summary>
    public static ConfigFile DefaultConfig() => new()
    {
        Hotkey = "Alt+`",
        QuickJumpModifier = "Alt",
        SuppressAltTab = null,
        Templates = new List<TemplateDefinition>
        {
            new()
            {
                Name = "示例模板",
                Slots = new List<TemplateSlot>
                {
                    new() { Slot = 1, GroupKey = "C:\\example\\terminal.exe", Title = "" },
                    new() { Slot = 2, GroupKey = "C:\\example\\browser.exe", Title = "" },
                },
            },
        },
        SortMethods = new List<SortMethodDefinition>
        {
            new()
            {
                Name = "示例-终端优先",
                Rules = new List<SortRule>
                {
                    new() { Type = "process", Value = "WindowsTerminal" },
                    new() { Type = "title", Value = "Visual Studio" },
                },
            },
            new()
            {
                Name = "示例-浏览器优先",
                Rules = new List<SortRule> { new() { Type = "process", Value = "chrome" } },
            },
        },
    };
}
