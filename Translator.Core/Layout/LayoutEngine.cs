using Translator.Core.Models;

namespace Translator.Core.Layout;

/// <summary>气泡的候选放置位置及其得分。</summary>
public sealed record BubbleCandidate(RectInt Rect, double Score, PlacementKind Kind);

/// <summary>放置方式，决定渲染样式。</summary>
public enum PlacementKind
{
    /// <summary>放在原文旁边的空白处（首选）。</summary>
    Beside,

    /// <summary>降级：编号小图标。</summary>
    Badge,

    /// <summary>降级：允许溢出屏幕边缘。</summary>
    Overflow,
}

/// <summary>一个待布局的气泡。</summary>
public sealed record BubbleRequest(
    string BlockId,
    string SourceText,
    string TranslatedText,
    RectInt SourceBounds,
    RectInt PreferredSize);

/// <summary>布局结果。</summary>
public sealed record BubblePlacement(
    string BlockId,
    RectInt Rect,
    PlacementKind Kind,
    double Score);

/// <summary>
/// 一次布局的完整输出，含所有候选位置（供调试覆盖层可视化）。
/// </summary>
public sealed record LayoutResult(
    IReadOnlyList<BubblePlacement> Placements,
    IReadOnlyList<RectInt> AllCandidates);

/// <summary>布局调参。</summary>
public sealed record LayoutOptions
{
    /// <summary>气泡与原文的最小间距。</summary>
    public int Gap { get; init; } = 8;

    /// <summary>气泡与原文允许的最大重叠比例，超过就降级。</summary>
    public double MaxSourceOverlap { get; init; } = 0.12;

    /// <summary>气泡之间允许的最大重叠比例。</summary>
    public double MaxBubbleOverlap { get; init; } = 0.0;

    /// <summary>搜索候选位置时的步长，越大越快但越粗。</summary>
    public int SearchStep { get; init; } = 6;

    /// <summary>占用图的膨胀半径，避免气泡贴着文字边缘。</summary>
    public int Inflation { get; init; } = 4;

    /// <summary>
    /// 候选位置允许的最大占用比例。超过就丢弃该候选。
    ///
    /// 这个值必须很小：占用图里已经包含了所有 OCR 文字框，
    /// 只要候选位置压在文字上就不该被采纳 —— 产品要求气泡"不盖住原文"。
    /// 放宽到 0.3 以上会出现气泡压住半个字的情况。
    /// </summary>
    public double MaxOccupancy { get; init; } = 0.10;
}

/// <summary>
/// 布局引擎：占用图 → 空白矩形搜索 → 候选打分。
/// 核心卖点，纯逻辑不依赖界面，可离线用测试集回归。
/// </summary>
public sealed class LayoutEngine
{
    private readonly LayoutOptions _options;

    public LayoutEngine(LayoutOptions? options = null)
        => _options = options ?? new LayoutOptions();

    /// <summary>
    /// 为一批气泡计算位置。
    /// </summary>
    /// <param name="requests">待布局的气泡。</param>
    /// <param name="viewport">可见区域（通常是工作区或整个屏幕）。</param>
    /// <param name="occupied">额外占用区域（OCR 文本框、悬浮栏等）。</param>
    public IReadOnlyList<BubblePlacement> Layout(
        IReadOnlyList<BubbleRequest> requests,
        RectInt viewport,
        IReadOnlyList<RectInt> occupied)
        => LayoutDetailed(requests, viewport, occupied).Placements;

    /// <summary>同上，但额外返回所有候选位置，供调试覆盖层显示。</summary>
    public LayoutResult LayoutDetailed(
        IReadOnlyList<BubbleRequest> requests,
        RectInt viewport,
        IReadOnlyList<RectInt> occupied)
    {
        var map = OccupancyMap.Build(viewport, occupied, _options.Inflation);
        var placed = new List<BubblePlacement>();
        var allCandidates = new List<RectInt>();

        foreach (var request in requests)
        {
            // 原文本身也是占用区
            map.MarkOccupied(request.SourceBounds);

            var placement = PlaceOne(request, map, viewport, placed, allCandidates);
            placed.Add(placement);

            if (placement.Kind != PlacementKind.Badge)
                map.MarkOccupied(placement.Rect);
        }

        return new LayoutResult(placed, allCandidates);
    }

    private BubblePlacement PlaceOne(
        BubbleRequest request,
        OccupancyMap map,
        RectInt viewport,
        List<BubblePlacement> placed,
        List<RectInt> allCandidates)
    {
        var src = request.SourceBounds;
        var size = request.PreferredSize;

        var candidates = GenerateCandidates(src, size, viewport)
            .Select(rect => new BubbleCandidate(rect, ScoreCandidate(rect, src, viewport, placed, map), KindOf(rect, src)))
            .Where(c => c.Score > double.NegativeInfinity)
            .OrderByDescending(c => c.Score)
            .ToList();

        allCandidates.AddRange(candidates.Select(c => c.Rect));

        if (candidates.Count == 0)
        {
            // 降级为编号小图标，贴在原文右上角（同样夹进视口，别跑到屏幕外）
            var badge = ClampInto(new RectInt(src.Right - 18, src.Y - 10, 18, 18), viewport);
            return new BubblePlacement(request.BlockId, badge, PlacementKind.Badge, -1);
        }

        // 兜底：万一选中的候选仍有溢出（例如气泡比视口还大），把它挪回视口内，
        // 否则气泡会显示在屏幕外 —— 原文贴着屏幕边缘时最容易出现。
        var best = candidates[0];
        var rect = ClampInto(best.Rect, viewport);
        return new BubblePlacement(request.BlockId, rect, best.Kind, best.Score);
    }

    /// <summary>把矩形夹进边界内：先往左上收，收到顶就贴左/上边。</summary>
    private static RectInt ClampInto(RectInt rect, RectInt bounds)
    {
        int dx = 0;
        if (rect.Right > bounds.Right) dx = bounds.Right - rect.Right;
        if (rect.X + dx < bounds.X) dx = bounds.X - rect.X;

        int dy = 0;
        if (rect.Bottom > bounds.Bottom) dy = bounds.Bottom - rect.Bottom;
        if (rect.Y + dy < bounds.Y) dy = bounds.Y - rect.Y;

        return dx == 0 && dy == 0 ? rect : rect.Offset(dx, dy);
    }

    /// <summary>生成候选位置：优先右侧空白，其次左侧、下方、上方。</summary>
    private IEnumerable<RectInt> GenerateCandidates(RectInt src, RectInt size, RectInt viewport)
    {
        int gap = _options.Gap;

        // 右侧：最符合阅读习惯（译文在原文右边）
        yield return new RectInt(src.Right + gap, src.Y, size.Width, size.Height);

        // 右侧垂直居中对齐
        yield return new RectInt(src.Right + gap, src.Y + (src.Height - size.Height) / 2, size.Width, size.Height);

        // 左侧
        yield return new RectInt(src.X - gap - size.Width, src.Y, size.Width, size.Height);

        // 下方
        yield return new RectInt(src.X, src.Bottom + gap, size.Width, size.Height);

        // 上方
        yield return new RectInt(src.X, src.Y - gap - size.Height, size.Width, size.Height);

        // 在右侧一列里上下滑动搜索（处理右侧被别的文字占住的情况）
        int step = _options.SearchStep;
        for (int dy = step; dy <= src.Height + 120; dy += step)
        {
            yield return new RectInt(src.Right + gap, src.Y + dy, size.Width, size.Height);
            yield return new RectInt(src.Right + gap, src.Y - dy, size.Width, size.Height);
        }

        // 右侧更远处（跨过一整块正文/图片后仍有空白的情况）
        for (int dx = gap + 20; dx <= 900; dx += 20)
        {
            yield return new RectInt(src.Right + dx, src.Y, size.Width, size.Height);
            // 同一距离上再试几个垂直偏移，便于绕开局部的图片或表格
            yield return new RectInt(src.Right + dx, src.Y - size.Height - gap, size.Width, size.Height);
            yield return new RectInt(src.Right + dx, src.Y + src.Height + gap, size.Width, size.Height);
        }

        // 左侧滑动
        for (int dy = step; dy <= src.Height + 120; dy += step)
            yield return new RectInt(src.X - gap - size.Width, src.Y + dy, size.Width, size.Height);

        // 左侧更远处
        for (int dx = gap + 20; dx <= 400; dx += 20)
            yield return new RectInt(src.X - gap - dx - size.Width, src.Y, size.Width, size.Height);

        // 下方更远处
        for (int dy = gap + 20; dy <= 400; dy += 20)
            yield return new RectInt(src.X, src.Bottom + dy, size.Width, size.Height);
    }

    /// <summary>给候选位置打分。越靠右、越接近原文垂直中心、越不遮挡，得分越高。</summary>
    private double ScoreCandidate(
        RectInt rect,
        RectInt src,
        RectInt viewport,
        List<BubblePlacement> placed,
        OccupancyMap map)
    {
        // 完全在视口外 → 丢弃
        if (!rect.IntersectsWith(viewport)) return double.NegativeInfinity;

        // 遮挡原文太多 → 丢弃
        if (src.OverlapRatio(rect) > _options.MaxSourceOverlap) return double.NegativeInfinity;

        double score = 0;

        // 位置偏好：右侧最佳
        if (rect.X >= src.Right) score += 100;
        else if (rect.Right <= src.X) score += 70;
        else if (rect.Y >= src.Bottom) score += 50;
        else score += 20;

        // 垂直对齐原文中心
        int srcCenter = src.Y + src.Height / 2;
        int rectCenter = rect.Y + rect.Height / 2;
        score -= Math.Abs(srcCenter - rectCenter) * 0.15;

        // 水平距离越近越好
        int hDist = rect.X >= src.Right ? rect.X - src.Right : src.X - rect.Right;
        score -= Math.Abs(hDist) * 0.05;

        // 与已放置气泡重叠 → 丢弃
        foreach (var p in placed)
        {
            if (p.Kind == PlacementKind.Badge) continue;
            if (rect.IntersectsWith(p.Rect)) return double.NegativeInfinity;
        }

        // 占用图惩罚：与已有文字/边缘重叠越少越好
        double occupancy = map.OccupancyRatio(rect);
        if (occupancy > _options.MaxOccupancy) return double.NegativeInfinity;
        score -= occupancy * 60;

        // 必须完整落在视口内。只沾到一条边的候选会有一半飘到屏幕外，
        // 所以给出压倒性惩罚：让"完整放得下"的候选（哪怕位置差一些）优先胜出。
        if (rect.X < viewport.X || rect.Right > viewport.Right) score -= 1000;
        if (rect.Y < viewport.Y || rect.Bottom > viewport.Bottom) score -= 1000;

        return score;
    }

    private static PlacementKind KindOf(RectInt rect, RectInt src)
        => rect.X >= src.Right || rect.Right <= src.X ? PlacementKind.Beside : PlacementKind.Overflow;
}
