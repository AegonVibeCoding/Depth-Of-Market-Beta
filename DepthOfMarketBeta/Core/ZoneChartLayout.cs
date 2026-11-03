using System.Drawing;

namespace DepthOfMarketBeta.Core;

public static class ZoneChartLayout
{
    // Preserve the original active-region extension to the chart's right edge.
    public static (int Left, int Right)? Span(Rectangle chart, int formedX)
    {
        var left = Math.Max(chart.Left, formedX);
        var right = chart.Right - 1;
        return right > left ? (left, right) : null;
    }

    public static Rectangle? Caption(Rectangle chart, Rectangle band,
        int width, IEnumerable<Rectangle> occupied)
    {
        if (band.Width <= 0 || band.Height <= 0) return null;
        var top = band.Top + (band.Height - 21) / 2;
        if (top < chart.Top || top + 21 > chart.Bottom) return null;
        var left = Math.Max(chart.Left + 4, band.Left + 6);
        var right = Math.Min(chart.Right - 4, band.Right - 6);
        var center = band.Left + band.Width / 2;
        var gaps = new List<(int Left, int Right)>();
        var cursor = left;
        foreach (var block in occupied.Select(r => Rectangle.Inflate(r, 3, 4))
            .Where(r => r.Top < top + 21 && r.Bottom > top).OrderBy(r => r.Left))
        {
            if (block.Right <= cursor || block.Left >= right) continue;
            if (block.Left > cursor) gaps.Add((cursor, Math.Min(right, block.Left)));
            cursor = Math.Max(cursor, block.Right);
        }
        if (cursor < right) gaps.Add((cursor, right));
        // Follow the middle of the extended band, using free space inside it when columns overlap.
        return gaps.Where(g => g.Right - g.Left >= 160).Select(g =>
        {
            var w = Math.Min(Math.Clamp(width, 160, 600), g.Right - g.Left);
            var x = Math.Clamp(center - w / 2, g.Left, g.Right - w);
            return new Rectangle(x, top, w, 21);
        }).OrderBy(r => Math.Abs(r.Left + r.Width / 2 - center)).ThenByDescending(r => r.Width)
          .Select(r => (Rectangle?)r).FirstOrDefault();
    }
}
