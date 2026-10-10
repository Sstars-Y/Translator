using Translator.Core.Abstractions;
using Translator.Core.Models;

namespace Translator.Core.Text;

/// <summary>
/// 变化检测：画面没变就不重新处理。用降采样比较，避免逐像素扫描。
/// 对应路线图阶段 3 的"变化检测（降采样比较）加防抖"。
/// </summary>
public sealed class ChangeDetector
{
    private readonly int _sampleStep;
    private readonly int _pixelThreshold;
    private readonly double _changeRatio;
    private byte[]? _previous;
    private int _width;
    private int _height;

    /// <param name="sampleStep">降采样步长，4 表示每 4 像素取一个。</param>
    /// <param name="pixelThreshold">单像素亮度差超过多少算变化。</param>
    /// <param name="changeRatio">变化像素比例超过多少认为画面变了。</param>
    public ChangeDetector(int sampleStep = 4, int pixelThreshold = 24, double changeRatio = 0.01)
    {
        _sampleStep = Math.Max(1, sampleStep);
        _pixelThreshold = pixelThreshold;
        _changeRatio = changeRatio;
    }

    /// <summary>与上一帧比较。第一帧总是返回 true。</summary>
    public bool HasChanged(CapturedFrame frame)
    {
        int cols = Math.Max(1, frame.Width / _sampleStep);
        int rows = Math.Max(1, frame.Height / _sampleStep);
        var gray = Downsample(frame, cols, rows);

        if (_previous is null || _width != cols || _height != rows)
        {
            _previous = gray;
            _width = cols;
            _height = rows;
            return true;
        }

        int changed = 0;
        for (int i = 0; i < gray.Length; i++)
        {
            if (Math.Abs(gray[i] - _previous[i]) > _pixelThreshold) changed++;
        }

        bool result = changed / (double)gray.Length > _changeRatio;
        if (result) _previous = gray;
        return result;
    }

    /// <summary>重置，用于会话恢复（锁屏、显示器热插拔）后强制重算。</summary>
    public void Reset() => _previous = null;

    private byte[] Downsample(CapturedFrame frame, int cols, int rows)
    {
        var result = new byte[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int x = Math.Min(frame.Width - 1, c * _sampleStep);
                int y = Math.Min(frame.Height - 1, r * _sampleStep);
                result[r * cols + c] = Luma(frame.GetPixel(x, y));
            }
        }
        return result;
    }

    private static byte Luma(uint bgra)
    {
        int b = (int)(bgra & 0xFF);
        int g = (int)((bgra >> 8) & 0xFF);
        int r = (int)((bgra >> 16) & 0xFF);
        return (byte)((r * 299 + g * 587 + b * 114) / 1000);
    }
}
