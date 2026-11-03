namespace DepthOfMarketBeta.Core;

public enum DepthColumnMode { Net, Stacked, Pulled, Both }
public readonly record struct DepthChangeRow(BookSide Side, decimal Price, decimal Added, decimal Reduced)
{
    public decimal Net => Added - Reduced;
    public decimal Magnitude(DepthColumnMode mode) => mode switch
    {
        DepthColumnMode.Net => Math.Abs(Net),
        DepthColumnMode.Stacked => Added,
        DepthColumnMode.Pulled => Reduced,
        _ => Math.Max(Added, Reduced)
    };
}

/// <summary>
/// Raw depth accumulator for one period. Matches the public DOM algorithm:
/// count depth deltas, reset totals at each selected period, discard the old side on a side flip.
/// Executions are not subtracted from decreases. First snapshot establishes a baseline.
/// </summary>
public sealed class DepthChangesCounter
{
    private readonly Dictionary<(BookSide, decimal), decimal> _baseline = new();
    private readonly Dictionary<(BookSide, decimal), DepthChangeRow> _totals = new();
    public long Bar { get; private set; } = -1;
    public bool Initialized { get; private set; }

    public void Seed(IEnumerable<DepthRow> rows, long bar)
    {
        _baseline.Clear(); _totals.Clear(); Bar = bar; Initialized = true;
        foreach (var r in rows)
            if (r.Volume > 0) _baseline[(r.Side, r.Price)] = r.Volume;
    }

    public void SetBar(long bar)
    {
        if (bar == Bar) return;
        ResetTotals(bar);
    }

    public void ResetTotals(long bar)
    {
        Bar = bar;
        _totals.Clear();
    }

    public void Update(DepthRow row, long bar)
    {
        if (!Initialized || row.Volume < 0) return;
        SetBar(bar);
        var key = (row.Side, row.Price);
        if (row.Volume > 0)
        {
            var opposite = (row.Side == BookSide.Bid ? BookSide.Ask : BookSide.Bid, row.Price);
            _baseline.Remove(opposite);
            _totals.Remove(opposite);
        }
        var previous = _baseline.GetValueOrDefault(key);
        var delta = row.Volume - previous;
        if (delta != 0)
        {
            var total = _totals.GetValueOrDefault(key, new(row.Side, row.Price, 0, 0));
            _totals[key] = total with { Added = total.Added + Math.Max(0, delta), Reduced = total.Reduced + Math.Max(0, -delta) };
        }
        if (row.Volume == 0) _baseline.Remove(key);
        else _baseline[key] = row.Volume;
    }

    public DepthChangeRow[] Snapshot() => _totals.Values.ToArray();
    public decimal BestBid => _baseline.Keys.Where(k => k.Item1 == BookSide.Bid).Select(k => k.Item2).DefaultIfEmpty(0).Max();
    public decimal BestAsk => _baseline.Keys.Where(k => k.Item1 == BookSide.Ask).Select(k => k.Item2).DefaultIfEmpty(0).Min();
}

public static class DepthAggregationPeriod
{
    public static long Key(int chartBar, DateTime utcNow, int seconds)
    {
        if (seconds < 0 || seconds > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        return seconds == 0 ? chartBar : utcNow.Ticks / (TimeSpan.TicksPerSecond * seconds);
    }
}

public readonly record struct ColumnPlacement(DepthColumnMode Mode, int Left, int Width)
{
    public int Right => Left + Width;
}
public readonly record struct ColumnSpec(DepthColumnMode Mode, bool Enabled, int Width, int RightOffset);
public static class DepthColumnLayout
{
    public static ColumnPlacement[] Arrange(int chartLeft, int chartRight, int domWidth, int gap, IEnumerable<ColumnSpec> columns)
    {
        var cursor = chartRight - Math.Max(0, domWidth) - gap;
        var result = new List<ColumnPlacement>();
        foreach (var c in columns)
        {
            if (!c.Enabled) continue;
            var width = Math.Clamp(c.Width, 20, 1000);
            var right = c.RightOffset < 0 ? cursor : chartRight - c.RightOffset;
            var left = right - width;
            // Preserve the requested width; draw only the visible intersection in the host.
            result.Add(new(c.Mode, left, width));
            cursor = Math.Min(cursor, left - gap);
        }
        return result.ToArray();
    }

    public static int BarWidth(decimal value, decimal scale, int width) =>
        width <= 0 || value == 0 ? 0 : Math.Clamp((int)(Math.Min(1m, Math.Abs(value) / Math.Max(1m, scale)) * width), 1, width);
}

