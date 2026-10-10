using Translator.Core.Abstractions;
using Translator.Core.Models;

namespace Translator.Core.Layout;

/// <summary>
/// 占用图：把屏幕上"已经有东西"的区域标记出来，供布局算法找空白。
/// 来源有两类 —— OCR 文本框，以及图像边缘密度（路线图阶段 4）。
///
/// 用降采样的位图（每格一个字节）来表示，兼顾精度与速度。
/// </summary>
public sealed class OccupancyMap
{
    private readonly byte[] _cells;
    private readonly int _cols;
    private readonly int _rows;
    private readonly RectInt _bounds;
    private readonly int _cellSize;

    public RectInt Bounds => _bounds;
    public int CellSize => _cellSize;
    public int Columns => _cols;
    public int Rows => _rows;

    private OccupancyMap(RectInt bounds, int cellSize, int cols, int rows)
    {
        _bounds = bounds;
        _cellSize = cellSize;
        _cols = cols;
        _rows = rows;
        _cells = new byte[cols * rows];
    }

    /// <summary>从占用矩形集合构建。每格按被覆盖的比例记 0..255。</summary>
    public static OccupancyMap Build(RectInt bounds, IReadOnlyList<RectInt> occupied, int inflation = 4, int cellSize = 4)
    {
        cellSize = Math.Max(1, cellSize);
        int cols = Math.Max(1, (bounds.Width + cellSize - 1) / cellSize);
        int rows = Math.Max(1, (bounds.Height + cellSize - 1) / cellSize);
        var map = new OccupancyMap(bounds, cellSize, cols, rows);

        foreach (var rect in occupied)
            map.MarkOccupied(rect, inflation);

        return map;
    }

    /// <summary>从捕获帧的位图构建，加入图像边缘密度（找出图片、图标等非文字内容）。</summary>
    public static OccupancyMap BuildFromFrame(CapturedFrame frame, RectInt bounds, IReadOnlyList<RectInt> textBoxes,
        int inflation = 4, int cellSize = 4, int edgeThreshold = 48)
    {
        var map = Build(bounds, textBoxes, inflation, cellSize);
        map.AddEdgeDensity(frame, edgeThreshold);
        return map;
    }

    /// <summary>标记一块区域为占用。</summary>
    public void MarkOccupied(RectInt rect, int inflation = 0)
    {
        var r = inflation > 0 ? rect.Inflate(inflation, inflation) : rect;

        int c0 = Math.Max(0, (r.X - _bounds.X) / _cellSize);
        int r0 = Math.Max(0, (r.Y - _bounds.Y) / _cellSize);
        int c1 = Math.Min(_cols - 1, (r.Right - 1 - _bounds.X) / _cellSize);
        int r1 = Math.Min(_rows - 1, (r.Bottom - 1 - _bounds.Y) / _cellSize);

        for (int row = r0; row <= r1; row++)
            for (int col = c0; col <= c1; col++)
                _cells[row * _cols + col] = 255;
    }

    /// <summary>
    /// 用位图的边缘密度补充占用信息。滚动条、图片、图标这类没有 OCR 文字的区域
    /// 也要避开，否则气泡会盖在图上。
    /// </summary>
    public void AddEdgeDensity(CapturedFrame frame, int edgeThreshold = 48)
    {
        if (frame.Width < 2 || frame.Height < 2) return;

        for (int row = 0; row < _rows; row++)
        {
            for (int col = 0; col < _cols; col++)
            {
                int idx = row * _cols + col;
                if (_cells[idx] == 255) continue; // 已被文字占用

                // 取该格中心点，与其右/下邻居比较亮度差
                int px = col * _cellSize + _cellSize / 2;
                int py = row * _cellSize + _cellSize / 2;
                if (px + 1 >= frame.Width || py + 1 >= frame.Height) continue;

                int lum = Luma(frame.GetPixel(px, py));
                int lumR = Luma(frame.GetPixel(px + 1, py));
                int lumD = Luma(frame.GetPixel(px, py + 1));

                int edge = Math.Max(Math.Abs(lum - lumR), Math.Abs(lum - lumD));
                if (edge > edgeThreshold)
                {
                    // 边缘强度映射成占用度，最高 180（低于文字的 255）
                    _cells[idx] = (byte)Math.Min(180, 60 + edge);
                }
            }
        }
    }

    /// <summary>某矩形内的平均占用比例，0..1。</summary>
    public double OccupancyRatio(RectInt rect)
    {
        int c0 = Math.Max(0, (rect.X - _bounds.X) / _cellSize);
        int r0 = Math.Max(0, (rect.Y - _bounds.Y) / _cellSize);
        int c1 = Math.Min(_cols - 1, (rect.Right - 1 - _bounds.X) / _cellSize);
        int r1 = Math.Min(_rows - 1, (rect.Bottom - 1 - _bounds.Y) / _cellSize);

        if (c1 < c0 || r1 < r0) return 1.0; // 完全在界外 → 当作全占用

        long sum = 0;
        int count = 0;
        for (int row = r0; row <= r1; row++)
        {
            for (int col = c0; col <= c1; col++)
            {
                sum += _cells[row * _cols + col];
                count++;
            }
        }

        return count == 0 ? 1.0 : sum / (double)count / 255.0;
    }

    /// <summary>导出成可视化位图，供调试覆盖层显示。</summary>
    public byte[] ToVisualization(out int width, out int height)
    {
        width = _cols;
        height = _rows;
        var pixels = new byte[_cols * _rows * 4];
        for (int i = 0; i < _cells.Length; i++)
        {
            byte v = _cells[i];
            int o = i * 4;
            // 占用越高越红（BGRA）
            pixels[o + 0] = (byte)(255 - v);
            pixels[o + 1] = (byte)(255 - v / 2);
            pixels[o + 2] = v;
            pixels[o + 3] = 255;
        }
        return pixels;
    }

    private static int Luma(uint bgra)
    {
        int b = (int)(bgra & 0xFF);
        int g = (int)((bgra >> 8) & 0xFF);
        int r = (int)((bgra >> 16) & 0xFF);
        return (r * 299 + g * 587 + b * 114) / 1000;
    }
}
