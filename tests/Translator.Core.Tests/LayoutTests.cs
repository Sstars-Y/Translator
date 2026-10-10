using FluentAssertions;
using Translator.Core.Abstractions;
using Translator.Core.Layout;
using Translator.Core.Models;
using Xunit;

namespace Translator.Core.Tests;

public class LayoutEngineTests
{
    private static readonly RectInt Viewport = new(0, 0, 1200, 700);

    private static BubbleRequest Request(string id, RectInt src, int w = 300, int h = 44)
        => new(id, "source text", "译文内容", src, new RectInt(0, 0, w, h));

    [Fact]
    public void 应优先放在原文右侧()
    {
        var engine = new LayoutEngine();
        var src = new RectInt(60, 100, 400, 20);

        var placements = engine.Layout([Request("a", src)], Viewport, []);

        var p = placements.Single();
        p.Kind.Should().Be(PlacementKind.Beside);
        p.Rect.X.Should().BeGreaterThanOrEqualTo(src.Right, "气泡应在原文右侧");
    }

    [Fact]
    public void 气泡不应遮挡原文()
    {
        var engine = new LayoutEngine();
        var src = new RectInt(60, 100, 400, 20);

        var p = engine.Layout([Request("a", src)], Viewport, []).Single();

        src.OverlapRatio(p.Rect).Should().BeLessThanOrEqualTo(0.12);
    }

    [Fact]
    public void 多个气泡之间不应重叠()
    {
        var engine = new LayoutEngine();
        var requests = Enumerable.Range(0, 5)
            .Select(i => Request($"b{i}", new RectInt(60, 100 + i * 40, 400, 20)))
            .ToList();

        var placements = engine.Layout(requests, Viewport, []);

        for (int i = 0; i < placements.Count; i++)
            for (int j = i + 1; j < placements.Count; j++)
                if (placements[i].Kind != PlacementKind.Badge && placements[j].Kind != PlacementKind.Badge)
                    placements[i].Rect.IntersectsWith(placements[j].Rect).Should().BeFalse(
                        $"气泡 {i} 与 {j} 不应重叠");
    }

    [Fact]
    public void 应避让已占用的文字区域()
    {
        var engine = new LayoutEngine();
        var src = new RectInt(60, 100, 200, 20);
        // 右侧一整块被别的文字占据
        var occupied = new List<RectInt> { new(280, 0, 400, 700) };

        var p = engine.Layout([Request("a", src, 150, 40)], Viewport, occupied).Single();

        // 不应该落在被占用的区域里
        p.Rect.IntersectsWith(occupied[0]).Should().BeFalse();
    }

    [Fact]
    public void 找不到空白时应降级为编号小图标()
    {
        var engine = new LayoutEngine();
        var src = new RectInt(60, 100, 200, 20);
        // 整屏占满
        var occupied = new List<RectInt> { new(0, 0, 1200, 700) };

        var p = engine.Layout([Request("a", src)], Viewport, occupied).Single();

        p.Kind.Should().Be(PlacementKind.Badge);
    }

    [Fact]
    public void 相同输入应得到相同布局()
    {
        var engine = new LayoutEngine();
        var requests = new List<BubbleRequest>
        {
            Request("a", new RectInt(60, 100, 400, 20)),
            Request("b", new RectInt(60, 200, 400, 20)),
        };

        var first = engine.Layout(requests, Viewport, []);
        var second = engine.Layout(requests, Viewport, []);

        first.Select(p => p.Rect).Should().Equal(second.Select(p => p.Rect),
            "内容不变就不应重排");
    }

    [Fact]
    public void 气泡应留在视口内()
    {
        var engine = new LayoutEngine();
        var src = new RectInt(60, 100, 300, 20);

        var p = engine.Layout([Request("a", src, 400, 100)], Viewport, []).Single();

        p.Rect.X.Should().BeGreaterThanOrEqualTo(0);
        p.Rect.Y.Should().BeGreaterThanOrEqualTo(0);
        p.Rect.Right.Should().BeLessThanOrEqualTo(Viewport.Right);
    }
}

public class OccupancyMapTests
{
    [Fact]
    public void 标记区域后占用率应上升()
    {
        var map = OccupancyMap.Build(new RectInt(0, 0, 400, 400), [], inflation: 0);

        map.OccupancyRatio(new RectInt(10, 10, 100, 100)).Should().Be(0);

        map.MarkOccupied(new RectInt(10, 10, 100, 100));

        map.OccupancyRatio(new RectInt(10, 10, 100, 100)).Should().BeGreaterThan(0.9);
    }

    [Fact]
    public void 区域外的占用率应视为满()
        => OccupancyMap.Build(new RectInt(0, 0, 100, 100), [])
            .OccupancyRatio(new RectInt(500, 500, 50, 50)).Should().Be(1.0);

    [Fact]
    public void 膨胀应扩大占用范围()
    {
        var tight = OccupancyMap.Build(new RectInt(0, 0, 400, 400), [new RectInt(100, 100, 50, 50)], inflation: 0);
        var inflated = OccupancyMap.Build(new RectInt(0, 0, 400, 400), [new RectInt(100, 100, 50, 50)], inflation: 10);

        var probe = new RectInt(85, 85, 10, 10);
        inflated.OccupancyRatio(probe).Should().BeGreaterThan(tight.OccupancyRatio(probe));
    }

    [Fact]
    public void 边缘密度应标记出高对比区域()
    {
        // 左半白右半黑的位图，中间有明显边缘
        const int w = 64, h = 64;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                byte v = x < w / 2 ? (byte)255 : (byte)0;
                pixels[o] = v; pixels[o + 1] = v; pixels[o + 2] = v; pixels[o + 3] = 255;
            }

        var frame = new CapturedFrame
        {
            Pixels = pixels, Width = w, Height = h, Stride = w * 4,
            SourceBounds = new RectInt(0, 0, w, h), Timestamp = DateTimeOffset.Now,
        };

        var map = OccupancyMap.BuildFromFrame(frame, new RectInt(0, 0, w, h), [], inflation: 0, cellSize: 2);

        // 边缘处（中间列）占用度应高于纯色区域
        double atEdge = map.OccupancyRatio(new RectInt(w / 2 - 2, 10, 4, 20));
        double flat = map.OccupancyRatio(new RectInt(4, 10, 4, 20));

        atEdge.Should().BeGreaterThan(flat);
    }
}
