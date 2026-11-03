namespace DepthOfMarketBeta.Core;

/// <summary>One feed, four independently timed accumulators. The host serializes access.</summary>
public sealed class DepthColumnCounters
{
    private static readonly DepthColumnMode[] Modes = Enum.GetValues<DepthColumnMode>();
    private readonly Dictionary<DepthColumnMode, DepthChangesCounter> _counters =
        Modes.ToDictionary(mode => mode, _ => new DepthChangesCounter());
    private readonly Dictionary<DepthColumnMode, int> _overrides = Modes.ToDictionary(mode => mode, _ => -1);

    public int DefaultSeconds { get; private set; }
    public bool Initialized => _counters[DepthColumnMode.Net].Initialized;
    public decimal BestBid => _counters[DepthColumnMode.Net].BestBid;
    public decimal BestAsk => _counters[DepthColumnMode.Net].BestAsk;
    public int OverrideSeconds(DepthColumnMode mode) => _overrides[mode];
    public int EffectiveSeconds(DepthColumnMode mode) => _overrides[mode] < 0 ? DefaultSeconds : _overrides[mode];
    private long Key(DepthColumnMode mode, int chartBar, DateTime now) =>
        DepthAggregationPeriod.Key(chartBar, now, EffectiveSeconds(mode));

    public void SetDefaultSeconds(int seconds, int chartBar, DateTime now)
    {
        if (seconds < 0 || seconds > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (seconds == DefaultSeconds) return;
        DefaultSeconds = seconds;
        foreach (var mode in Modes)
            if (_overrides[mode] < 0 && _counters[mode].Initialized)
                _counters[mode].ResetTotals(Key(mode, chartBar, now));
    }

    public void SetOverrideSeconds(DepthColumnMode mode, int seconds, int chartBar, DateTime now)
    {
        if (seconds < -1 || seconds > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (_overrides[mode] == seconds) return;
        _overrides[mode] = seconds;
        if (_counters[mode].Initialized) _counters[mode].ResetTotals(Key(mode, chartBar, now));
    }

    // Recalculation/toggle discards data, not the user's period settings.
    public void ResetData()
    {
        foreach (var mode in Modes) _counters[mode] = new DepthChangesCounter();
    }

    public void Seed(IEnumerable<DepthRow> rows, int chartBar, DateTime now)
    {
        var snapshot = rows.ToArray();
        foreach (var mode in Modes) _counters[mode].Seed(snapshot, Key(mode, chartBar, now));
    }

    public void Advance(int chartBar, DateTime now)
    {
        foreach (var mode in Modes)
            if (_counters[mode].Initialized) _counters[mode].SetBar(Key(mode, chartBar, now));
    }

    public void Update(DepthRow row, int chartBar, DateTime now)
    {
        foreach (var mode in Modes) _counters[mode].Update(row, Key(mode, chartBar, now));
    }

    public DepthChangeRow[] Snapshot(DepthColumnMode mode) => _counters[mode].Snapshot();
}

