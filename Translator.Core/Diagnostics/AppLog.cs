using System.Text;

namespace Translator.Core.Diagnostics;

/// <summary>日志级别。</summary>
public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 简单文件日志。
///
/// 隐私要求：默认不记录原文。调用方要用 <see cref="Redact"/> 包装任何可能
/// 含原文的字符串，或者干脆只记录长度与哈希。
/// </summary>
public sealed class AppLog : IDisposable
{
    private readonly string? _path;
    private readonly object _lock = new();
    private readonly long _maxBytes;
    private StreamWriter? _writer;

    /// <summary>是否允许记录原文。默认 false。</summary>
    public bool LogSourceText { get; set; }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public AppLog(string? path = null, long maxBytes = 2 * 1024 * 1024)
    {
        _path = path;
        _maxBytes = maxBytes;
        if (_path is not null) TryOpen();
    }

    private void TryOpen()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path!);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            RotateIfNeeded();
            _writer = new StreamWriter(new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch (Exception)
        {
            _writer = null; // 日志失败不能影响主流程
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_path!)) return;
            if (new FileInfo(_path!).Length < _maxBytes) return;
            var backup = _path + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(_path!, backup);
        }
        catch (Exception) { }
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message, Exception? ex = null)
        => Write(LogLevel.Error, ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}";

        lock (_lock)
        {
            try { _writer?.WriteLine(line); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// 把可能含原文的文本脱敏，只保留长度与哈希前缀。
    /// 任何要写进日志的原文都必须经过它。
    /// </summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        if (LogSourceText) return text;
        return $"(len={text.Length} hash={Models.Hash.TextHash(text)[..8]})";
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
