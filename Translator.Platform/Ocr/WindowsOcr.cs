using System.Runtime.InteropServices.WindowsRuntime;
using Translator.Core.Abstractions;
using Translator.Core.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using CoreOcrResult = Translator.Core.Models.OcrResult;
using CoreOcrLine = Translator.Core.Models.OcrLine;
using CoreOcrWord = Translator.Core.Models.OcrWord;
using WinRtBuffer = Windows.Storage.Streams.Buffer;

namespace Translator.Platform.Ocr;

/// <summary>
/// Windows 自带 OCR 的封装。
///
/// 实测发现：多数中文 Windows 上只装了 zh-Hans 语言包，没有 en-US，
/// 所以 <c>TryCreateFromLanguage("en-US")</c> 会返回 null。这里的策略是
/// 优先要英文引擎，拿不到就退回用户配置语言，并在界面引导安装英文包
/// （首次启动向导会用到 <see cref="IsLanguageInstalled"/>）。
/// </summary>
public sealed class WindowsOcr : IOcr
{
    private readonly OcrEngine? _engine;

    public string LanguageTag { get; }

    /// <summary>
    /// 是否做放大预处理。默认关闭。
    ///
    /// 实测结论（本机 Win11 26200，仅装 zh-Hans OCR 包）：最近邻放大到 2 倍
    /// 反而让识别质量下降 —— zh 引擎在 1 倍下读 16pt 英文约 97% 正确，放大后
    /// 出现 "Of 什 ne"、"YOLI" 这类错误。放大只对很小的字号（&lt;10pt）有帮助，
    /// 需要时应配合真正的插值缩放而非最近邻。
    /// </summary>
    public bool UseUpscale { get; set; }
    public bool IsAvailable => _engine is not null;

    /// <summary>实际是否退化成了非英文引擎，界面需要提示用户装英文语言包。</summary>
    public bool IsFallbackLanguage { get; }

    public WindowsOcr(string preferredLanguage = "en-US")
    {
        var engine = TryCreate(preferredLanguage);
        bool fallback = false;

        if (engine is null)
        {
            // 退回用户配置语言，至少能用；界面提示装英文包
            engine = OcrEngine.TryCreateFromUserProfileLanguages();
            fallback = engine is not null;
        }

        _engine = engine;
        IsFallbackLanguage = fallback;
        LanguageTag = engine?.RecognizerLanguage?.LanguageTag ?? preferredLanguage;
    }

    private static OcrEngine? TryCreate(string languageTag)
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language(languageTag));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>系统是否装了该语言的 OCR 包。</summary>
    public bool IsLanguageInstalled(string languageTag)
        => OcrEngine.AvailableRecognizerLanguages
            .Any(l => l.LanguageTag.StartsWith(languageTag, StringComparison.OrdinalIgnoreCase));

    /// <summary>列出系统已安装的 OCR 语言，供首次启动向导显示。</summary>
    public static IReadOnlyList<(string Tag, string Display)> GetInstalledLanguages()
        => OcrEngine.AvailableRecognizerLanguages
            .Select(l => (l.LanguageTag, l.DisplayName))
            .ToList();

    /// <summary>OCR 引擎允许的最大图像边长。</summary>
    public static uint MaxImageDimension => OcrEngine.MaxImageDimension;

    public async Task<CoreOcrResult> RecognizeAsync(CapturedFrame frame, CancellationToken ct = default)
    {
        if (_engine is null) return CoreOcrResult.Empty;

        // 预处理：放大能明显提升小字号识别率
        var prepared = UseUpscale ? Upscale(frame, 2) : frame;
        double invScale = prepared.Width == 0 ? 1.0 : (double)frame.Width / prepared.Width;

        try
        {
            using var bitmap = ToSoftwareBitmap(prepared);
            ct.ThrowIfCancellationRequested();

            var winrtResult = await _engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);

            var lines = new List<CoreOcrLine>(winrtResult.Lines.Count);
            foreach (var line in winrtResult.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text)) continue;

                var words = new List<CoreOcrWord>(line.Words.Count);
                foreach (var word in line.Words)
                {
                    var bounds = Scale(word.BoundingRect, invScale);
                    if (bounds.Width > 0 && bounds.Height > 0)
                        words.Add(new CoreOcrWord(word.Text, bounds));
                }

                var lineBounds = words.Count > 0
                    ? Union(words.Select(w => w.Bounds))
                    : Scale(line.Words.Count > 0 ? line.Words[0].BoundingRect : default, invScale);

                lines.Add(new CoreOcrLine(line.Text, lineBounds, words));
            }

            return new CoreOcrResult(lines, LanguageTag);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // OCR 失败不应中断流水线
            return CoreOcrResult.Empty;
        }
    }

    /// <summary>把 BGRA32 像素直接装进 SoftwareBitmap，避免走编码器。</summary>
    private static SoftwareBitmap ToSoftwareBitmap(CapturedFrame frame)
    {
        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Premultiplied);

        // 行紧凑时直接整块拷贝；有 padding 时逐行拷贝
        if (frame.Stride == frame.Width * 4)
        {
            bitmap.CopyFromBuffer(frame.Pixels.AsBuffer());
        }
        else
        {
            var packed = new byte[frame.Width * 4 * frame.Height];
            for (int y = 0; y < frame.Height; y++)
                System.Buffer.BlockCopy(frame.Pixels, y * frame.Stride, packed, y * frame.Width * 4, frame.Width * 4);
            bitmap.CopyFromBuffer(packed.AsBuffer());
        }

        return bitmap;
    }

    private static RectInt Scale(Windows.Foundation.Rect rect, double invScale)
        => new(
            (int)Math.Round(rect.X * invScale),
            (int)Math.Round(rect.Y * invScale),
            Math.Max(0, (int)Math.Round(rect.Width * invScale)),
            Math.Max(0, (int)Math.Round(rect.Height * invScale)));

    private static RectInt Union(IEnumerable<RectInt> rects)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var x in rects)
        {
            l = Math.Min(l, x.X); t = Math.Min(t, x.Y);
            r = Math.Max(r, x.Right); b = Math.Max(b, x.Bottom);
        }
        return r <= l || b <= t ? default : RectInt.FromLtrb(l, t, r, b);
    }

    /// <summary>整数倍放大，提升小字号识别率。</summary>
    public static CapturedFrame Upscale(CapturedFrame src, int factor)
    {
        if (factor <= 1) return src;

        int w = src.Width * factor;
        int h = src.Height * factor;
        int stride = w * 4;
        var dst = new byte[stride * h];

        for (int y = 0; y < h; y++)
        {
            int sy = y / factor;
            int srcRow = sy * src.Stride;
            int dstRow = y * stride;
            for (int x = 0; x < w; x++)
            {
                int so = srcRow + (x / factor) * 4;
                int doff = dstRow + x * 4;
                dst[doff + 0] = src.Pixels[so + 0];
                dst[doff + 1] = src.Pixels[so + 1];
                dst[doff + 2] = src.Pixels[so + 2];
                dst[doff + 3] = src.Pixels[so + 3];
            }
        }

        return new CapturedFrame
        {
            Pixels = dst,
            Width = w,
            Height = h,
            Stride = stride,
            SourceBounds = src.SourceBounds,
            Timestamp = src.Timestamp,
        };
    }

    public void Dispose() { }
}
