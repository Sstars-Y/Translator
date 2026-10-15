using System.Diagnostics;
using Translator.Core.Abstractions;
using Translator.Core.Caching;
using Translator.Core.Layout;
using Translator.Core.Models;
using Translator.Core.Text;

namespace Translator.Core;

/// <summary>一次流水线运行的诊断信息，供调试覆盖层显示。</summary>
public sealed record PipelineDiagnostics(
    long CaptureMs,
    long OcrMs,
    long MergeMs,
    long TranslateMs,
    long LayoutMs,
    int LineCount,
    int BlockCount,
    int SkippedCount,
    int CacheHits,
    int TranslatedCount);

/// <summary>一次流水线运行的输出。</summary>
public sealed record PipelineResult(
    IReadOnlyList<BubblePlacement> Placements,
    IReadOnlyList<TextBlock> Blocks,
    PipelineDiagnostics Diagnostics)
{
    /// <summary>OCR 原始结果，供调试覆盖层显示。</summary>
    public OcrResult Ocr { get; init; } = OcrResult.Empty;

    /// <summary>参与布局的占用区域，供调试覆盖层显示。</summary>
    public IReadOnlyList<RectInt> Occupied { get; init; } = [];

    /// <summary>所有候选落点，供调试覆盖层显示。</summary>
    public IReadOnlyList<RectInt> Candidates { get; init; } = [];

    /// <summary>段落到译文的映射，供渲染使用。</summary>
    public IReadOnlyDictionary<string, string> Translations { get; init; }
        = new Dictionary<string, string>();

    public static readonly PipelineResult Empty = new(
        [], [], new PipelineDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
}

/// <summary>流水线配置。</summary>
public sealed record PipelineOptions
{
    public string TargetLanguage { get; init; } = "zh-Hans";
    public LayoutOptions Layout { get; init; } = new();
    public ParagraphMergeOptions Merge { get; init; } = new();

    /// <summary>译文文本宽度的倍数，用于估算气泡尺寸。</summary>
    public double BubbleWidthFactor { get; init; } = 1.0;

    /// <summary>气泡内每行高度估算（像素）。</summary>
    public int BubbleLineHeight { get; init; } = 22;

    /// <summary>
    /// 界面缩放比（显示器 DPI / 96）。
    ///
    /// 流水线里的坐标全是屏幕物理像素，而字号、间距这类界面尺寸是 DIP，
    /// 所以估算气泡大小时要乘上它，否则 150% 缩放下估算尺寸只有实际的三分之二，
    /// 布局会算出互相压住或不贴边的结果。
    /// </summary>
    public double UiScale { get; init; } = 1.0;

    /// <summary>
    /// 气泡估算宽度的上限（屏幕物理像素）。0 表示不限制。
    ///
    /// 估算宽度同时被当成"占位宽度"参与落点判断：按原文宽度等比例预留（倍数 1.0）
    /// 对中文译文来说过于悲观，原文贴边时会把气泡误判成放不下而丢到别处。
    /// 实际渲染宽度受气泡样式 MaxWidth 限制，所以这里也按同一个上限截断。
    /// </summary>
    public int MaxBubbleWidth { get; init; }
}

/// <summary>
/// 处理流水线：捕获 → 变化检测 → OCR → 段落合并 → 语言过滤 →
/// 术语与翻译 → 缓存 → 布局 → 渲染。
///
/// 这里只负责编排与"渲染前的数据"，实际渲染由 App 层的窗口负责。
/// </summary>
public sealed class TranslationPipeline
{
    private readonly ICapture _capture;
    private readonly IOcr _ocr;
    private readonly ITranslator _translator;
    private readonly ITermStore _terms;
    private readonly TranslationCache _cache;
    private readonly LayoutEngine _layout;
    private readonly ChangeDetector _changeDetector;
    private readonly PipelineOptions _options;

    public TranslationPipeline(
        ICapture capture,
        IOcr ocr,
        ITranslator translator,
        ITermStore terms,
        TranslationCache cache,
        PipelineOptions? options = null)
    {
        _capture = capture;
        _ocr = ocr;
        _translator = translator;
        _terms = terms;
        _cache = cache;
        _options = options ?? new PipelineOptions();
        _layout = new LayoutEngine(_options.Layout);
        _changeDetector = new ChangeDetector();
    }

    /// <summary>强制重新计算（例如手动点"翻译一次"）。</summary>
    public bool ForceRecompute { get; set; }

    /// <summary>
    /// 跑一次流水线。
    /// </summary>
    /// <param name="region">工作区。</param>
    /// <param name="overlayExclusions">需要从占用图里排除的区域（悬浮栏、气泡自身）。</param>
    public async Task<PipelineResult> RunAsync(
        RectInt region,
        IReadOnlyList<RectInt>? overlayExclusions = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. 捕获
        var frame = await _capture.CaptureAsync(region, ct).ConfigureAwait(false);
        long captureMs = sw.ElapsedMilliseconds;

        try
        {
            // 2. 变化检测
            sw.Restart();
            if (!ForceRecompute && !_changeDetector.HasChanged(frame))
                return PipelineResult.Empty;
            ForceRecompute = false;

            // 3. OCR
            var ocr = await _ocr.RecognizeAsync(frame, ct).ConfigureAwait(false);

            // OCR 给的是"相对捕获位图"的坐标，而布局的视口/占用区都是屏幕绝对坐标。
            // 这里统一平移一次，往后流水线里所有几何量都是屏幕物理像素。
            ocr = RebaseToScreen(ocr, region);
            long ocrMs = sw.ElapsedMilliseconds;

            // 4. 段落合并
            sw.Restart();
            var blocks = ParagraphMerger.Merge(ocr, _options.Merge);
            long mergeMs = sw.ElapsedMilliseconds;

            // 5. 语言过滤
            var candidates = new List<TextBlock>();
            int skipped = 0;
            foreach (var block in blocks)
            {
                if (LanguageFilter.ShouldSkip(block.Text) == SkipReason.None)
                    candidates.Add(block);
                else
                    skipped++;
            }

            if (candidates.Count == 0)
            {
                return new PipelineResult([], blocks,
                    new PipelineDiagnostics(captureMs, ocrMs, mergeMs, 0, 0,
                        ocr.Lines.Count, blocks.Count, skipped, 0, 0));
            }

            // 6. 术语 + 翻译 + 缓存
            sw.Restart();
            var translations = await TranslateBlocksAsync(candidates, ct).ConfigureAwait(false);
            long translateMs = sw.ElapsedMilliseconds;
            int cacheHits = translations.Count(t => t.FromCache);

            // 7. 布局
            sw.Restart();
            var occupied = new List<RectInt>();
            foreach (var line in ocr.Lines) occupied.Add(line.Bounds);
            if (overlayExclusions is not null) occupied.AddRange(overlayExclusions);

            var requests = new List<BubbleRequest>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var tr = translations[i];
                if (!tr.IsSuccess || string.IsNullOrWhiteSpace(tr.TranslatedText)) continue;

                requests.Add(new BubbleRequest(
                    candidates[i].Id,
                    candidates[i].Text,
                    tr.TranslatedText,
                    candidates[i].Bounds,
                    EstimateBubbleSize(candidates[i], tr.TranslatedText)));
            }

            // 可见区域取显示器而不是选区本身：选区通常远小于屏幕，
            // 拿选区当边界会让"贴原文右侧"这种正确位置因为超出选区被否决。
            var viewport = ResolveViewport(region);
            var layoutResult = _layout.LayoutDetailed(requests, viewport, occupied);
            var placements = layoutResult.Placements;
            long layoutMs = sw.ElapsedMilliseconds;

            var diag = new PipelineDiagnostics(
                captureMs, ocrMs, mergeMs, translateMs, layoutMs,
                ocr.Lines.Count, blocks.Count, skipped, cacheHits, translations.Count);

            var translationMap = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (translations[i].IsSuccess)
                    translationMap[candidates[i].Id] = translations[i].TranslatedText;
            }

            return new PipelineResult(placements, blocks, diag)
            {
                Ocr = ocr,
                Occupied = occupied,
                Candidates = layoutResult.AllCandidates,
                Translations = translationMap,
            };
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// 把 OCR 结果从"捕获位图坐标"平移到"屏幕绝对坐标"。
    /// 少了这一步，原文位置与布局视口就不在同一坐标系里，气泡会整体错位。
    /// </summary>
    private static OcrResult RebaseToScreen(OcrResult ocr, RectInt region)
    {
        if (ocr.Lines.Count == 0 || (region.X == 0 && region.Y == 0)) return ocr;

        var lines = new List<OcrLine>(ocr.Lines.Count);
        foreach (var line in ocr.Lines)
        {
            var words = line.Words
                .Select(w => w with { Bounds = w.Bounds.Offset(region.X, region.Y) })
                .ToList();
            lines.Add(line with { Bounds = line.Bounds.Offset(region.X, region.Y), Words = words });
        }

        return ocr with { Lines = lines };
    }

    private async Task<List<TranslationResult>> TranslateBlocksAsync(
        List<TextBlock> blocks, CancellationToken ct)
    {
        var results = new List<TranslationResult>(blocks.Count);
        var pending = new List<(int Index, TextBlock Block, string PreparedText)>();

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];

            // 术语先应用，再算缓存键 —— 键里含术语版本
            var application = _terms.Apply(block.Text);
            string key = TranslationCache.BuildKey(application.Text, _terms.Version, _options.TargetLanguage);

            if (_cache.TryGet(key, out var cached))
            {
                results.Add(new TranslationResult(block.Text, cached, FromCache: true));
                continue;
            }

            results.Add(null!); // 占位，稍后填充
            pending.Add((i, block, application.Text));
        }

        if (pending.Count == 0) return results;

        // 批量翻译，效率更高
        var texts = pending.Select(p => p.PreparedText).ToList();
        var translated = await _translator.TranslateBatchAsync(texts, ct).ConfigureAwait(false);

        for (int j = 0; j < pending.Count; j++)
        {
            var (index, block, prepared) = pending[j];
            var tr = j < translated.Count ? translated[j] : new TranslationResult(prepared, "", false, Error: "no result");

            if (tr.IsSuccess)
            {
                // 还原术语占位符
                var final = _terms.RestoreAndClean(tr.TranslatedText);

                string key = TranslationCache.BuildKey(prepared, _terms.Version, _options.TargetLanguage);
                _cache.Set(key, final);
                results[index] = new TranslationResult(block.Text, final, FromCache: false, tr.Backend);
            }
            else
            {
                results[index] = new TranslationResult(block.Text, "", false, Error: tr.Error);
            }
        }

        return results;
    }

    /// <summary>
    /// 布局用的可见区域：取与选区交叠面积最大的那台显示器。
    ///
    /// 不能拿选区本身当可见区域 —— 选区是 OCR 范围，不是可见范围。
    /// 用户框选的区域常常只比原文大一圈，气泡贴着选区边缘就会被判成"溢出"，
    /// 于是一个明明在屏幕上的好位置被否决，气泡被挪到远处甚至屏幕外。
    /// 真正的边界是显示器；取不到显示器信息时就退回选区。
    /// </summary>
    private RectInt ResolveViewport(RectInt region)
    {
        try
        {
            RectInt best = region;
            long bestArea = 0;

            foreach (var monitor in _capture.GetMonitors())
            {
                var inter = monitor.Bounds.Intersect(region);
                long area = (long)inter.Width * inter.Height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = monitor.Bounds;
                }
            }

            return best;
        }
        catch
        {
            return region;
        }
    }

    /// <summary>估算气泡尺寸（屏幕物理像素）：中文比英文短，按字符数折算行数。</summary>
    private RectInt EstimateBubbleSize(TextBlock block, string translated)
    {
        double scale = _options.UiScale > 0 ? _options.UiScale : 1.0;
        int lineHeight = Math.Max(8, (int)Math.Round(_options.BubbleLineHeight * scale));
        int minWidth = Math.Max(40, (int)Math.Round(80 * scale));

        // 中文每字符约等于一个行高的一半宽度
        int charWidth = lineHeight / 2 + 2;
        int padding = (int)Math.Round(20 * scale);

        // 气泡实际渲染宽度由译文文字决定（原文宽 ≠ 译文宽，中文通常更窄）：
        // 按译文长度估一行文本的宽度，乘用户可调的 BubbleWidthFactor，再限制在 MaxBubbleWidth 内。
        // 以前用原文宽度估算，长原文会把占位宽度估得过大，明明放得下的"贴原文右侧"
        // 会被误判成放不下，气泡被丢到更远的位置甚至屏幕外。
        int textWidth = (int)Math.Round((translated.Length * charWidth + padding) * _options.BubbleWidthFactor);
        int width = Math.Max(minWidth, textWidth);
        if (_options.MaxBubbleWidth > 0)
            width = Math.Min(width, Math.Max(minWidth, _options.MaxBubbleWidth));

        int charsPerLine = Math.Max(4, width / Math.Max(1, charWidth));
        int lines = Math.Max(1, (int)Math.Ceiling(translated.Length / (double)charsPerLine));
        int height = lines * lineHeight + (int)Math.Round(10 * scale);

        return new RectInt(0, 0, width, height);
    }
}
