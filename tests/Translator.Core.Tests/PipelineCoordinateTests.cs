using FluentAssertions;
using Translator.Core;
using Translator.Core.Abstractions;
using Translator.Core.Caching;
using Translator.Core.Layout;
using Translator.Core.Models;
using Translator.Core.Terms;
using Xunit;

namespace Translator.Core.Tests;

/// <summary>
/// 坐标系回归测试。
///
/// 捕获与 OCR 都在"相对捕获位图"的坐标里工作，而布局的视口、占用图、
/// 悬浮栏避让区都是屏幕绝对坐标。两者混用会让气泡整体错位
/// （候选全落在视口外 → 全部降级成小图标 / 跑到屏幕左上角）。
/// </summary>
public class PipelineCoordinateTests
{
    /// <summary>捕获后端替身：位图尺寸等于区域尺寸，内容全黑。</summary>
    private sealed class FakeCapture : ICapture
    {
        public string BackendName => "fake";
        public bool SupportsBorderRemoval => true;

        public Task<CapturedFrame> CaptureAsync(RectInt region, CancellationToken ct = default)
            => Task.FromResult(new CapturedFrame
            {
                Pixels = new byte[region.Width * region.Height * 4],
                Width = region.Width,
                Height = region.Height,
                Stride = region.Width * 4,
                SourceBounds = region,
                Timestamp = DateTimeOffset.Now,
            });

        public IReadOnlyList<MonitorInfo> GetMonitors() => [];

        public void Dispose() { }
    }

    /// <summary>OCR 替身：返回"相对位图"的坐标（真实引擎就是这么给的）。</summary>
    private sealed class FakeOcr : IOcr
    {
        private readonly OcrResult _result;

        public FakeOcr(OcrResult result) => _result = result;

        public bool IsAvailable => true;
        public string LanguageTag => "en-US";
        public bool UseUpscale { get; set; }
        public bool IsLanguageInstalled(string languageTag) => true;
        public Task<OcrResult> RecognizeAsync(CapturedFrame frame, CancellationToken ct = default)
            => Task.FromResult(_result);
        public void Dispose() { }
    }

    private sealed class FakeTranslator : ITranslator
    {
        private readonly string _translated;

        public FakeTranslator(string translated) => _translated = translated;

        public string BackendName => "fake";
        public bool IsReady => true;

        public Task<TranslationResult> TranslateAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new TranslationResult(text, _translated, false, BackendName));

        public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TranslationResult>>(
                texts.Select(t => new TranslationResult(t, _translated, false, BackendName)).ToList());

        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Unload() { }
        public void Dispose() { }
    }

    private static OcrResult BitmapRelativeLine(string text, RectInt bounds)
        => new([new OcrLine(text, bounds, [new OcrWord(text, bounds)])], "en-US");

    private static async Task<PipelineResult> RunAsync(
        RectInt region, RectInt ocrBounds, double uiScale)
    {
        using var cache = new TranslationCache();
        using var capture = new FakeCapture();
        using var ocr = new FakeOcr(BitmapRelativeLine("Hello world", ocrBounds));
        using var translator = new FakeTranslator("你好世界");

        var pipeline = new TranslationPipeline(
            capture, ocr, translator, new TermLayer(), cache,
            new PipelineOptions { UiScale = uiScale });

        return await pipeline.RunAsync(region);
    }

    [Fact]
    public async Task OCR相对坐标应换算成屏幕绝对坐标()
    {
        // 区域在屏幕 (800,400)，OCR 给出的行是位图内的 (60,80) ⇒ 屏幕坐标应为 (860,480)
        var region = new RectInt(800, 400, 600, 200);
        var result = await RunAsync(region, new RectInt(60, 80, 120, 20), uiScale: 1.0);

        result.Occupied.Should().Contain(r => r.X == 860 && r.Y == 480);
        result.Ocr.Lines[0].Bounds.X.Should().Be(860);
        result.Ocr.Lines[0].Bounds.Y.Should().Be(480);
    }

    [Fact]
    public async Task 气泡应落在原文旁边而不是降级成小图标()
    {
        var region = new RectInt(800, 400, 600, 200);
        var result = await RunAsync(region, new RectInt(60, 80, 120, 20), uiScale: 1.0);

        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Kind.Should().NotBe(PlacementKind.Badge, "坐标对齐时应该能找到气泡位置");
        placement.Rect.X.Should().BeGreaterThan(860 + 120, "气泡应贴在原文右侧");
        placement.Rect.Y.Should().BeInRange(400, 560, "气泡应与原文大致同一水平位置");
    }

    [Fact]
    public async Task 缩放变大时气泡估算尺寸也应变大()
    {
        // 窄原文 + 短译文：宽度由最小宽度决定，所以能看出缩放是否生效
        var region = new RectInt(0, 0, 1200, 800);
        var ocrBounds = new RectInt(60, 80, 60, 20);

        var normal = await RunAsync(region, ocrBounds, uiScale: 1.0);
        var scaled = await RunAsync(region, ocrBounds, uiScale: 2.0);

        var normalRect = normal.Placements.Single().Rect;
        var scaledRect = scaled.Placements.Single().Rect;

        scaledRect.Height.Should().BeGreaterThan(normalRect.Height, "行高应按缩放换算成物理像素");
        scaledRect.Width.Should().BeGreaterThan(normalRect.Width, "最小宽度也应按缩放换算");
    }

    [Fact]
    public async Task 区域原点为零时坐标不应被平移两次()
    {
        var region = new RectInt(0, 0, 600, 400);
        var result = await RunAsync(region, new RectInt(60, 80, 120, 20), uiScale: 1.0);

        result.Occupied.Should().Contain(r => r.X == 60 && r.Y == 80);
    }
}
