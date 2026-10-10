using System.Text;
using System.Text.RegularExpressions;
using Translator.Core.Abstractions;
using Translator.Core.Models;

namespace Translator.Core.Terms;

/// <summary>
/// 术语层：在翻译前把术语替换成占位符，翻译后再还原成指定译文。
///
/// 为什么不直接用 Bergamot 内置的 dictionary：实测它会把术语译文"前置"到
/// 句子前面而不是就地替换（"The Mana Reactor was built by Shinra."
/// → "魔能反应堆马纳反应堆由辛拉建造。"），所以必须自己做替换。
///
/// 纯逻辑，不依赖界面。
/// </summary>
public sealed class TermLayer : ITermStore
{
    private readonly List<TermEntry> _entries = [];
    private readonly Dictionary<string, string> _placeholders = new(StringComparer.Ordinal);
    private string _version = "0";

    /// <summary>
    /// 占位符格式。用数学符号 ⟦⟧（U+27E6/U+27E7）包裹。
    ///
    /// 这个格式是实测选出来的：把 20 种候选格式送进 Bergamot 跑一遍，只有
    /// ⟦0⟧、⁁0⁁、&lt;0&gt;、XXX0XXX、TERM0、@0@、|0|、#0# 等少数几种能原样穿过。
    /// ⟦⟧ 的优势是正文里几乎不会出现，且不会被误当成标点或货币符号
    /// （「«0»」会被替换成全角引号，「$0$」会变成「0美元」）。
    /// </summary>
    private const string PlaceholderOpen = "⟦";
    private const string PlaceholderClose = "⟧";

    public string Version => _version;

    public string? ActiveDomain { get; set; }

    public IReadOnlyList<TermEntry> AllTerms => _entries;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        RecomputeVersion();
    }

    /// <summary>直接设置术语集合（来自文件或界面编辑）。</summary>
    public void SetTerms(IEnumerable<TermEntry> terms)
    {
        _entries.Clear();
        _entries.AddRange(terms.Where(t => !string.IsNullOrWhiteSpace(t.Source)));
        // 长术语优先，避免 "Mana" 抢先匹配掉 "Mana Reactor"
        _entries.Sort((a, b) => b.Source.Length.CompareTo(a.Source.Length));
        RecomputeVersion();
    }

    public IReadOnlyList<TermEntry> GetActiveTerms()
        => _entries.Where(IsActive).ToList();

    private bool IsActive(TermEntry e) => e.Scope switch
    {
        TermScope.Global => true,
        TermScope.Domain => ActiveDomain is not null
                            && string.Equals(e.ScopeValue, ActiveDomain, StringComparison.OrdinalIgnoreCase),
        TermScope.Process => false, // 预留，v2.0 游戏模式
        _ => false,
    };

    /// <summary>
    /// 把命中的术语替换成占位符。返回的文本送去翻译，译文里含占位符，
    /// 再用 <see cref="Restore"/> 还原成术语译文。
    /// </summary>
    public TermApplication Apply(string text)
    {
        if (string.IsNullOrEmpty(text) || _entries.Count == 0)
            return new TermApplication(text, []);

        var active = GetActiveTerms();
        if (active.Count == 0) return new TermApplication(text, []);

        var result = text;
        var hits = new List<TermEntry>();
        _placeholders.Clear();

        int index = 0;
        foreach (var entry in active)
        {
            var (pattern, options) = BuildPattern(entry);
            var matches = Regex.Matches(result, pattern, options);
            if (matches.Count == 0) continue;

            // 同一个术语在本次应用中共用一个占位符
            var token = $"{PlaceholderOpen}{index++}{PlaceholderClose}";
            _placeholders[token] = entry.Target;
            hits.Add(entry);

            result = Regex.Replace(result, pattern, token, options);
        }

        return new TermApplication(result, hits);
    }

    /// <summary>
    /// 把译文里的占位符还原成术语译文。
    /// 模型可能在占位符两侧插入空格，这里做容错匹配。
    /// </summary>
    public string Restore(string translated)
    {
        if (_placeholders.Count == 0 || string.IsNullOrEmpty(translated))
            return translated;

        var result = translated;
        foreach (var (token, target) in _placeholders)
        {
            // 1. 精确匹配
            if (result.Contains(token, StringComparison.Ordinal))
            {
                result = result.Replace(token, target, StringComparison.Ordinal);
                continue;
            }

            // 2. 容错：括号与数字之间可能被插入空格
            var inner = token[PlaceholderOpen.Length..^PlaceholderClose.Length];
            var loose = new Regex(
                $@"{Regex.Escape(PlaceholderOpen)}\s*{Regex.Escape(inner)}\s*{Regex.Escape(PlaceholderClose)}");
            result = loose.Replace(result, target);
        }

        return result;
    }

    /// <summary>还原并清理可能残留的占位符符号。</summary>
    public string RestoreAndClean(string translated)
    {
        var r = Restore(translated);
        r = r.Replace(PlaceholderOpen, string.Empty).Replace(PlaceholderClose, string.Empty);
        return r;
    }

    /// <summary>构造匹配模式。返回模式与正则选项。</summary>
    private static (string Pattern, RegexOptions Options) BuildPattern(TermEntry entry)
    {
        var escaped = Regex.Escape(entry.Source);

        // 词边界：术语两端是词字符时要求边界，避免 "art" 命中 "start"
        var prefix = entry.WholeWord && IsWordChar(entry.Source[0]) ? @"\b" : string.Empty;
        var suffix = entry.WholeWord && IsWordChar(entry.Source[^1]) ? @"\b" : string.Empty;

        // 默认不区分大小写：术语通常按语义匹配，大小写差异不应导致漏译
        var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

        // 基础词形：允许常见的复数/时态后缀
        var tail = entry.WholeWord ? @"(?:s|es|ed|ing|'s)?" : string.Empty;

        return ($"{prefix}{escaped}{tail}{suffix}", options);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void RecomputeVersion()
    {
        var signature = string.Join('\u001f', _entries
            .Select(e => $"{e.Source}\u001e{e.Target}\u001e{e.Scope}\u001e{e.ScopeValue}\u001e{e.CaseSensitive}\u001e{e.WholeWord}"));
        _version = Hash.TextHash(signature)[..12];
    }

    /// <summary>把占位符译文还原成给用户看的文本（去掉内部标记）。</summary>
    public static string Sanitize(string text)
        => text.Replace(PlaceholderOpen, string.Empty).Replace(PlaceholderClose, string.Empty);
}
