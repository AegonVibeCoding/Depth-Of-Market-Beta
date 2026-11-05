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

    public static int CaptionRight(Rectangle chart, int gap)
    {
        return chart.Right - Math.Clamp(gap, 0, 200);
    }

    public static Rectangle? RightCaption(Rectangle chart, Rectangle band, int right,
        int measuredWidth, int maximumWidth, IEnumerable<Rectangle> occupied)
    {
        if (band.Width <= 0 || band.Height <= 0) return null;
        var top = band.Top + (band.Height - 21) / 2;
        if (top < chart.Top || top + 21 > chart.Bottom) return null;
        var left = Math.Max(chart.Left + 4, band.Left + 6);
        // Every visible caption shares this screen anchor. Do not move labels to another price or gap.
        if (right > chart.Right || right - left < 64) return null;
        var width = Math.Min(Math.Max(24, measuredWidth), Math.Min(Math.Clamp(maximumWidth, 180, 600), right - left));
        var box = new Rectangle(right - width, top, width, 21);
        return occupied.Any(r => Rectangle.Inflate(r, 3, 4).IntersectsWith(box)) ? null : box;
    }
}
