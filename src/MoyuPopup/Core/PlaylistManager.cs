using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoyuPopup.Core;

/// <summary>
/// 播放队列管理：持久化 playlist.json / history.json（设计书 8.3）。
/// 以 EmbedUrl 哈希为 ID 去重；支持位置记忆与 500 条滚动历史。
/// 所有操作须在 UI 线程调用（与播放控制同线程）。
/// </summary>
public sealed class PlaylistManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class PlaylistFile
    {
        public int Version { get; set; } = 1;
        public List<VideoItem> Items { get; set; } = new();
        public int CurrentIndex { get; set; }
    }

    private sealed class HistoryEntry
    {
        public string Id { get; set; } = "";
        public string Platform { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime PlayedAt { get; set; }
    }

    private sealed class HistoryFile
    {
        public int Version { get; set; } = 1;
        public List<HistoryEntry> Items { get; set; } = new();
    }

    private const int MaxHistory = 500;
    private const int MinRememberSec = 30;   // 播放超过 30s 才记忆位置

    private List<VideoItem> _items = new();
    private int _currentIndex;
    private List<HistoryEntry> _history = new();

    /// <summary>队列条目（顺序即播放顺序）</summary>
    public IReadOnlyList<VideoItem> Items => _items;

    /// <summary>当前条目索引</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>当前条目（空队列返回 null）</summary>
    public VideoItem? Current => _items.Count == 0
        ? null
        : _items[Math.Clamp(_currentIndex, 0, _items.Count - 1)];

    /// <summary>队列变化通知（UI 刷新用）</summary>
    public event Action? Changed;

    /// <summary>加载 playlist.json / history.json；损坏自动回退默认</summary>
    public void Load()
    {
        _items = LoadFile<PlaylistFile>("playlist.json")?.Items ?? new List<VideoItem>();
        _currentIndex = 0;
        _history = LoadFile<HistoryFile>("history.json")?.Items ?? new List<HistoryEntry>();
        Log.Info($"播放队列加载完成: {_items.Count} 条, 历史 {_history.Count} 条");
    }

    /// <summary>读取 JSON 文件（损坏时备份 .bad 并返回 null）</summary>
    private T? LoadFile<T>(string name) where T : class
    {
        var path = Path.Combine(ConfigManager.AppDataDir, name);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Error($"{name} 加载失败，回退默认", ex);
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* 备份失败忽略 */ }
            return null;
        }
    }

    /// <summary>原子写 JSON 文件</summary>
    private static void SaveFile<T>(string name, T data)
    {
        try
        {
            var path = Path.Combine(ConfigManager.AppDataDir, name);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"{name} 保存失败", ex);
        }
    }

    private void SavePlaylist() => SaveFile("playlist.json",
        new PlaylistFile { Items = _items, CurrentIndex = _currentIndex });

    private void SaveHistory() => SaveFile("history.json", new HistoryFile { Items = _history });

    /// <summary>计算条目稳定 ID（EmbedUrl SHA256 前 16 位十六进制）</summary>
    public static string ComputeId(string embedUrl)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(embedUrl));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>添加或更新条目（按 ID 去重；更新保留位置/标签），返回存储后的条目</summary>
    public VideoItem AddOrUpdate(VideoItem item)
    {
        item.Id = ComputeId(item.EmbedUrl);
        var existing = _items.FirstOrDefault(x => x.Id == item.Id);
        if (existing != null)
        {
            existing.Platform = item.Platform;
            existing.Title = item.Title;
            existing.InputUrl = item.InputUrl;
            existing.EmbedUrl = item.EmbedUrl;
            SavePlaylist();
            Changed?.Invoke();
            return existing;
        }
        item.AddedAt = DateTime.Now;
        _items.Add(item);
        SavePlaylist();
        Changed?.Invoke();
        return item;
    }

    /// <summary>批量导入（懒去重）：按 EmbedUrl 哈希去重，仅新增未存在条目，一次性落盘并通知一次。返回实际新增条数。</summary>
    public int AddRange(IEnumerable<VideoItem> items)
    {
        var added = 0;
        foreach (var it in items)
        {
            it.Id = ComputeId(it.EmbedUrl);
            if (_items.Any(x => x.Id == it.Id)) continue;   // 已存在，跳过
            it.AddedAt = DateTime.Now;
            _items.Add(it);
            added++;
        }
        if (added > 0)
        {
            SavePlaylist();
            Changed?.Invoke();
        }
        return added;
    }

    /// <summary>推进当前索引（环形，支持负偏移），返回新当前条目</summary>
    public VideoItem? StepTo(int offset)
    {
        if (_items.Count == 0) return null;
        _currentIndex = ((_currentIndex + offset) % _items.Count + _items.Count) % _items.Count;
        SavePlaylist();
        Changed?.Invoke();
        return Current;
    }

    /// <summary>按 ID 置为当前条目；成功返回 true</summary>
    public bool SetCurrentById(string id)
    {
        var idx = _items.FindIndex(x => x.Id == id);
        if (idx < 0) return false;
        _currentIndex = idx;
        SavePlaylist();
        Changed?.Invoke();
        return true;
    }

    /// <summary>移除条目并修正当前索引</summary>
    public bool Remove(string id)
    {
        var idx = _items.FindIndex(x => x.Id == id);
        if (idx < 0) return false;
        _items.RemoveAt(idx);
        if (_currentIndex >= _items.Count) _currentIndex = Math.Max(0, _items.Count - 1);
        SavePlaylist();
        Changed?.Invoke();
        return true;
    }

    /// <summary>清空队列</summary>
    public void Clear()
    {
        _items.Clear();
        _currentIndex = 0;
        SavePlaylist();
        Changed?.Invoke();
    }

    /// <summary>记录播放位置（&gt;30s 才有效，避免碎片位置污染）</summary>
    public void SetPosition(string id, int sec)
    {
        if (sec < MinRememberSec) return;
        var it = _items.FirstOrDefault(x => x.Id == id);
        if (it == null || it.LastPositionSec == sec) return;
        it.LastPositionSec = sec;
        SavePlaylist();
    }

    /// <summary>追加播放历史（最新在前，滚动保留 500 条）</summary>
    public void AddHistory(VideoItem item)
    {
        _history.Insert(0, new HistoryEntry
        {
            Id = item.Id,
            Platform = item.Platform,
            Title = item.Title,
            PlayedAt = DateTime.Now,
        });
        if (_history.Count > MaxHistory)
            _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
        SaveHistory();
    }
}
