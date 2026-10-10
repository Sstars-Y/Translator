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

    /// <summary>气泡宽度占原文宽度的倍数，用于估算气泡尺寸。</summary>
    public double BubbleWidthFactor { get; init; } = 1.0;

    /// <summary>气泡内每行高度估算（像素）。</summary>
    public int BubbleLineHeight { get; init; } = 22;
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

            var layoutResult = _layout.LayoutDetailed(requests, region, occupied);
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

    /// <summary>估算气泡尺寸：中文比英文短，按字符数折算行数。</summary>
    private RectInt EstimateBubbleSize(TextBlock block, string translated)
    {
        int width = Math.Max(80, (int)(block.Bounds.Width * _options.BubbleWidthFactor));

        // 中文每字符约等于一个行高的一半宽度
        int charWidth = _options.BubbleLineHeight / 2 + 2;
        int charsPerLine = Math.Max(4, width / Math.Max(1, charWidth));
        int lines = Math.Max(1, (int)Math.Ceiling(translated.Length / (double)charsPerLine));
        int height = lines * _options.BubbleLineHeight + 10;

        return new RectInt(0, 0, width, height);
    }
}
