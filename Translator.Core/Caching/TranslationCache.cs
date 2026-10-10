using System.Collections.Concurrent;
using System.Text.Json;
using Translator.Core.Models;

namespace Translator.Core.Caching;

/// <summary>缓存统计，用于诊断覆盖层与设置界面。</summary>
public sealed record CacheStats(int Hits, int Misses, int Count)
{
    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);
}

/// <summary>
/// 翻译缓存。按文本哈希存储，键包含术语库版本与目标语言，
/// 所以改了术语表后不会命中旧缓存（路线图阶段 6 的验收标准）。
/// </summary>
public sealed class TranslationCache : IDisposable
{
    private readonly ConcurrentDictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly string? _diskPath;
    private readonly int _maxEntries;
    private int _hits;
    private int _misses;
    private bool _dirty;
    private readonly object _flushLock = new();

    public TranslationCache(string? diskPath = null, int maxEntries = 20000)
    {
        _diskPath = diskPath;
        _maxEntries = maxEntries;
        if (diskPath is not null) TryLoadFromDisk();
    }

    /// <summary>构造缓存键：原文 + 术语版本 + 目标语言。</summary>
    public static string BuildKey(string text, string termVersion, string targetLanguage)
        => Hash.CompositeKey(text, termVersion, targetLanguage);

    public bool TryGet(string key, out string value)
    {
        if (_memory.TryGetValue(key, out var v))
        {
            Interlocked.Increment(ref _hits);
            value = v;
            return true;
        }
        Interlocked.Increment(ref _misses);
        value = string.Empty;
        return false;
    }

    public void Set(string key, string value)
    {
        _memory[key] = value;
        _dirty = true;
        if (_memory.Count > _maxEntries) EvictOldest();
    }

    /// <summary>一键清除（隐私设置里提供）。</summary>
    public void Clear()
    {
        _memory.Clear();
        _dirty = true;
        Flush();
    }

    public CacheStats GetStats() => new(_hits, _misses, _memory.Count);

    private void EvictOldest()
    {
        // 简单策略：清掉约四分之一，避免每次插入都做全排序
        int target = _maxEntries * 3 / 4;
        foreach (var key in _memory.Keys.Take(_memory.Count - target))
            _memory.TryRemove(key, out _);
    }

    public void Flush()
    {
        if (_diskPath is null || !_dirty) return;
        lock (_flushLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_diskPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var payload = new Dictionary<string, string>(_memory, StringComparer.Ordinal);
                // 先写临时文件再替换，避免写一半崩溃导致缓存损坏
                var tmp = _diskPath + ".tmp";
                using (var fs = File.Create(tmp))
                    JsonSerializer.Serialize(fs, payload);
                File.Move(tmp, _diskPath, overwrite: true);
                _dirty = false;
            }
            catch (Exception)
            {
                // 缓存写失败不影响主流程
            }
        }
    }

    private void TryLoadFromDisk()
    {
        try
        {
            if (_diskPath is null || !File.Exists(_diskPath)) return;
            using var fs = File.OpenRead(_diskPath);
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(fs);
            if (payload is null) return;
            foreach (var (k, v) in payload) _memory[k] = v;
        }
        catch (Exception)
        {
            // 缓存损坏就当作空缓存
        }
    }

    public void Dispose() => Flush();
}
