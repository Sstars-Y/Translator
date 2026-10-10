using System.Text.RegularExpressions;
using Translator.Core.Models;

namespace Translator.Core.Text;

/// <summary>为什么一段文本被跳过。</summary>
public enum SkipReason
{
    None,
    Empty,
    AlreadyTargetLanguage,
    Url,
    Code,
    Numeric,
    TooShort,
    NotEnglish,
}

/// <summary>
/// 语言过滤：决定哪些文本值得送去翻译。
/// 对应路线图阶段 2 的"跳过中文、代码、URL、纯数字"。
/// </summary>
public static partial class LanguageFilter
{
    [GeneratedRegex(@"^\s*(https?://|www\.)\S+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlOnlyRegex();

    [GeneratedRegex(@"^\s*[\w.-]+\.(com|org|net|edu|gov|io|cn|dev|app|ai)(/\S*)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BareDomainRegex();

    [GeneratedRegex(@"^\s*[\d\s.,:%$+\-/()\[\]°×]+\s*$")]
    private static partial Regex NumericRegex();

    // 中文字符（含扩展区）
    [GeneratedRegex(@"[一-鿿㐀-䶿豈-﫿]")]
    private static partial Regex CjkRegex();

    // 日文假名
    [GeneratedRegex(@"[぀-ゟ゠-ヿ]")]
    private static partial Regex KanaRegex();

    // 韩文
    [GeneratedRegex(@"[가-힯ᄀ-ᇿ]")]
    private static partial Regex HangulRegex();

    // 代码特征：大量标点符号、括号配对、关键字
    [GeneratedRegex(@"[{}<>;=]|=>|::|->|\b(function|class|public|private|return|import|export|const|var|let|def|void|null|true|false)\b")]
    private static partial Regex CodeHintRegex();

    [GeneratedRegex(@"^\s*[A-Za-z_][A-Za-z0-9_]*\s*\(.*\)\s*;?\s*$")]
    private static partial Regex FunctionCallRegex();

    // 拉丁字母
    [GeneratedRegex(@"[A-Za-z]")]
    private static partial Regex LatinRegex();

    /// <summary>
    /// 判断一段文本是否应该跳过。只有返回 SkipReason.None 才送去翻译。
    /// </summary>
    public static SkipReason ShouldSkip(string text, int minLetters = 2)
    {
        if (string.IsNullOrWhiteSpace(text)) return SkipReason.Empty;

        var trimmed = text.Trim();

        // 纯 URL
        if (UrlOnlyRegex().IsMatch(trimmed) || BareDomainRegex().IsMatch(trimmed))
            return SkipReason.Url;

        // 纯数字 / 符号
        if (NumericRegex().IsMatch(trimmed))
            return SkipReason.Numeric;

        // 代码特征
        if (LooksLikeCode(trimmed))
            return SkipReason.Code;

        // 已经主要是目标语言（中文/日文/韩文）
        if (IsMostlyTargetLanguage(trimmed))
            return SkipReason.AlreadyTargetLanguage;

        // 没有拉丁字母，没什么可翻译的
        int latinCount = CountMatches(LatinRegex(), trimmed);
        if (latinCount < minLetters) return SkipReason.NotEnglish;

        return SkipReason.None;
    }

    /// <summary>判断是否主要为中日韩文本（已经是目标语言或非英语）。</summary>
    public static bool IsMostlyTargetLanguage(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int cjk = CountMatches(CjkRegex(), text)
                + CountMatches(KanaRegex(), text)
                + CountMatches(HangulRegex(), text);
        int letters = cjk + CountMatches(LatinRegex(), text);
        if (letters == 0) return false;
        // 中日韩占非空白字符的 40% 以上就认为不是待译英文
        return cjk >= 2 && (double)cjk / Math.Max(1, letters) > 0.4;
    }

    /// <summary>判断是否像代码行。</summary>
    public static bool LooksLikeCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 明显的函数调用：foo(a, b);
        if (FunctionCallRegex().IsMatch(text)) return true;

        // 代码符号密度
        int symbols = text.Count(c => "{}[]<>;=+*/\\|&^~`".Contains(c));
        if (symbols >= 3 && symbols > text.Length * 0.10) return true;

        // 关键字 + 符号
        if (CodeHintRegex().IsMatch(text) && symbols >= 2) return true;

        // 缩进明显的多行块
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length >= 2 && lines.Count(l => l.StartsWith("    ") || l.StartsWith('\t')) >= lines.Length * 0.6)
            return true;

        return false;
    }

    /// <summary>含中文时跳过，但混排的技术文档仍应翻译英文部分 —— 这里给调用方一个明确信号。</summary>
    public static bool HasCjk(string text) => CjkRegex().IsMatch(text);

    /// <summary>统计混合文本中拉丁字母的比例，用于决定是否值得翻译。</summary>
    public static double LatinRatio(string text)
    {
        int latin = CountMatches(LatinRegex(), text);
        int total = text.Count(c => !char.IsWhiteSpace(c));
        return total == 0 ? 0 : (double)latin / total;
    }

    private static int CountMatches(Regex regex, string text)
    {
        int count = 0;
        foreach (Match m in regex.Matches(text)) count += m.Length;
        return count;
    }
}
