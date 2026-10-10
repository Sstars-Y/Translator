using System.Text;
using Translator.Core.Models;

namespace Translator.Core.Text;

/// <summary>段落合并的调参项。</summary>
public sealed record ParagraphMergeOptions
{
    /// <summary>行间距小于中位行高的多少倍时视为同一段落。</summary>
    public double LineGapFactor { get; init; } = 0.8;

    /// <summary>左右边缘对齐的容差（像素）。</summary>
    public int EdgeAlignTolerance { get; init; } = 8;

    /// <summary>字号差异超过该比例就断段。</summary>
    public double FontSizeTolerance { get; init; } = 0.25;

    /// <summary>行宽差异超过该比例认为不是同一段（例如表格列）。</summary>
    public double WidthRatioTolerance { get; init; } = 0.55;
}

/// <summary>
/// 把 OCR 的相邻行合并成段落。对应路线图阶段 2 的"段落合并 v1"。
/// 纯逻辑，不依赖界面，可单测。
/// </summary>
public static class ParagraphMerger
{
    public static IReadOnlyList<TextBlock> Merge(
        OcrResult ocr,
        ParagraphMergeOptions? options = null)
    {
        options ??= new ParagraphMergeOptions();
        var lines = ocr.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .OrderBy(l => l.Bounds.Y)
            .ThenBy(l => l.Bounds.X)
            .ToList();

        if (lines.Count == 0) return [];

        // 估算版面中位行高，用于判断间距
        double medianHeight = Median(lines.Select(l => (double)l.Bounds.Height));

        var blocks = new List<List<OcrLine>>();
        var current = new List<OcrLine> { lines[0] };

        for (int i = 1; i < lines.Count; i++)
        {
            var prev = lines[i - 1];
            var line = lines[i];

            if (ShouldJoin(prev, line, medianHeight, options))
                current.Add(line);
            else
            {
                blocks.Add(current);
                current = [line];
            }
        }
        blocks.Add(current);

        return blocks.Select((group, index) => BuildBlock(group, index)).ToList();
    }

    private static bool ShouldJoin(
        OcrLine prev,
        OcrLine next,
        double medianHeight,
        ParagraphMergeOptions options)
    {
        // 垂直间距：太远就不是同一段
        int gap = next.Bounds.Y - prev.Bounds.Bottom;
        if (gap > medianHeight * options.LineGapFactor) return false;
        // 负得太多说明是换列/换栏
        if (gap < -medianHeight * 1.5) return false;

        // 字号差异过大（标题 vs 正文）
        double h1 = prev.MedianHeight;
        double h2 = next.MedianHeight;
        if (h1 > 0 && h2 > 0)
        {
            double ratio = Math.Abs(h1 - h2) / Math.Max(h1, h2);
            if (ratio > options.FontSizeTolerance) return false;
        }

        // 水平位置：左边缘应大致对齐（允许悬挂缩进）
        int leftDelta = Math.Abs(next.Bounds.X - prev.Bounds.X);
        bool leftAligned = leftDelta <= options.EdgeAlignTolerance;

        if (!leftAligned) return false;

        // 宽度差异：段落的最后一行天然较短（"applications."），不能据此断段。
        // 宽度差异只在配合"左边缘也明显偏移"时才说明是分栏/表格，
        // 而那种情况上面的左对齐检查已经拦掉了。
        // 这里只拦一种情况：下一行比上一行窄很多，且左边缘也缩进了
        // （典型的悬挂缩进新段落，或表格的下一列）。
        double w1 = prev.Bounds.Width;
        double w2 = next.Bounds.Width;
        if (w1 > 0 && w2 > 0)
        {
            double wr = Math.Min(w1, w2) / Math.Max(w1, w2);
            bool muchNarrower = wr < options.WidthRatioTolerance;
            bool indented = next.Bounds.X > prev.Bounds.X + options.EdgeAlignTolerance;
            if (muchNarrower && indented) return false;
        }

        // 上一行以句末标点结束，且下一行是明显的缩进开头 → 新段落
        if (EndsSentence(prev.Text) && leftDelta > options.EdgeAlignTolerance / 2)
            return false;

        return true;
    }

    private static TextBlock BuildBlock(List<OcrLine> group, int index)
    {
        var bounds = Union(group.Select(l => l.Bounds));
        var text = JoinLines(group.Select(l => l.Text));
        var id = $"blk_{index:D3}_{Hash.TextHash(text)[..8]}";
        return new TextBlock(id, text, bounds, group);
    }

    /// <summary>
    /// 拼接行文本，处理断词：行尾的连字符要接上，普通换行补空格。
    /// </summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        string? prev = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (prev is null)
            {
                sb.Append(line);
            }
            else if (prev.EndsWith('-') && prev.Length > 1 && char.IsLetter(prev[^2]))
            {
                // 断词：software engi-\nneering → software engineering
                sb.Length--; // 去掉连字符
                sb.Append(line);
            }
            else
            {
                sb.Append(' ').Append(line);
            }
            prev = line;
        }

        return Hash.Normalize(sb.ToString());
    }

    private static bool EndsSentence(string text)
    {
        var t = text.TrimEnd();
        return t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?')
            || t.EndsWith(':') || t.EndsWith('。') || t.EndsWith('；');
    }

    private static RectInt Union(IEnumerable<RectInt> rects)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var x in rects)
        {
            l = Math.Min(l, x.X);
            t = Math.Min(t, x.Y);
            r = Math.Max(r, x.Right);
            b = Math.Max(b, x.Bottom);
        }
        return r <= l || b <= t ? default : RectInt.FromLtrb(l, t, r, b);
    }

    private static double Median(IEnumerable<double> values)
    {
        var arr = values.OrderBy(v => v).ToArray();
        if (arr.Length == 0) return 0;
        return arr[arr.Length / 2];
    }
}
