using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowSwitcherWpf.Models;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// Reads and writes the fixed order of window groups. Persistence path is
/// <c>%APPDATA%\WindowSwitcherWpf\order.json</c>. Writes are debounced 500ms
/// to absorb rapid drag operations.
/// </summary>
public sealed class OrderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _writeLock = new();
    private CancellationTokenSource? _pendingWrite;

    public OrderStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowSwitcherWpf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "order.json");
    }

    public string FilePath => _path;

    public OrderFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new OrderFile();
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<OrderFile>(json, JsonOptions);
            return data ?? new OrderFile();
        }
        catch (Exception ex)
        {
            TryBackupCorrupt();
            return new OrderFile();
        }
    }

    public void SaveDebounced(OrderFile file)
    {
        lock (_writeLock)
        {
            _pendingWrite?.Cancel();
            _pendingWrite = new CancellationTokenSource();
            var token = _pendingWrite.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token);
                    var json = JsonSerializer.Serialize(file, JsonOptions);
                    var tmp = _path + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _path, overwrite: true);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    File.AppendAllText(_path + ".log", $"{DateTime.Now:O} {ex}\n");
                }
            });
        }
    }

    public void SaveImmediate(OrderFile file)
    {
        lock (_writeLock)
        {
            _pendingWrite?.Cancel();
            var json = JsonSerializer.Serialize(file, JsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private void TryBackupCorrupt()
    {
        try
        {
            if (File.Exists(_path))
                File.Move(_path, _path + $".bak.{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
        }
        catch { /* best-effort */ }
    }
}
