namespace DepthOfMarketBeta.Core;

public static class ZoneCaptionAnchor
{
    // Saved distances and wide DOM columns must not push every label off the chart.
    public static (int Left, int Right)? Fit(int chartLeft, int chartRight, int distance)
    {
        var availableLeft = chartLeft + 4;
        var availableRight = chartRight - 2;
        if (availableRight - availableLeft < 120) return null;
        var right = Math.Clamp(chartRight - Math.Max(0, distance), availableLeft + 120, availableRight);
        // If clamped from off-screen, reserve readable space rather than a tiny sliver.
        if (right == availableLeft + 120) right = Math.Min(availableRight, availableLeft + 300);
        return (Math.Max(availableLeft, right - 300), right);
    }
}

