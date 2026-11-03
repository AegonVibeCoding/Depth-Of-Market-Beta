namespace DepthOfMarketBeta.Core;

public readonly record struct DepthPriceRow(int Top, int Height)
{
    public int Center => Top + Height / 2;
}

public static class DepthPriceLayout
{
    public static DepthPriceRow Row(BookSide side, decimal price, decimal tick, decimal bid, decimal ask,
        int currentY, int customHeight, int normalY, int nextY)
    {
        if (customHeight <= 0 || tick <= 0) return new(normalY, Math.Max(1, Math.Abs(nextY - normalY)));
        var height = Math.Max(1, customHeight - 1);
        if (side == BookSide.Ask)
        {
            var steps = (int)((price - ask) / tick);
            return new(currentY - height * (steps + 1) - steps - 15, height + 1);
        }
        var bidSteps = (int)((bid - price) / tick);
        var spread = bid > 0 && ask > 0 ? (int)((ask - bid) / tick) : 0;
        return new(currentY + height * (bidSteps + spread - 1) + bidSteps - 15, height + 1);
    }
}


