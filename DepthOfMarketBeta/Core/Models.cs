namespace DepthOfMarketBeta.Core;

public enum BookSide { Bid, Ask }
public enum ZoneState { Watching, Absorbing, Confirmed, Invalidated, Expired }
public enum NoticeKind { Formed, Approach, Absorption, Reversal, Broken }
public enum EventKind { Added, Reduced, Replenished }
public enum MarketPreset { Custom, GC, NQ, ES }

public sealed record Settings
{
    public int ZoneTicks { get; init; } = 4;
    public int ScanTicks { get; init; } = 80;
    public int WindowSeconds { get; init; } = 10;
    public int BaselineSeconds { get; init; } = 180;
    public int WarmupSeconds { get; init; } = 30;
    public int MinimumEvents { get; init; } = 12;
    public int ActiveSeconds { get; init; } = 3;
    public double BurstRatio { get; init; } = 3;
    public decimal MinimumChange { get; init; } = 2;
    public decimal RelativeChange { get; init; } = .15m;
    public int MergeMilliseconds { get; init; } = 100;
    public int LifetimeSeconds { get; init; } = 90;
    public int ApproachTicks { get; init; } = 4;
    public int AttackSeconds { get; init; } = 5;
    public decimal AttackVolume { get; init; } = 20;
    public int MinimumAttackTrades { get; init; } = 3;
    public decimal AttackShare { get; init; } = .65m;
    public int ReloadCount { get; init; } = 2;
    public int ReloadMilliseconds { get; init; } = 800;
    public int ConfirmationMilliseconds { get; init; } = 700;
    public int ConfirmationWindowMilliseconds { get; init; } = 1500;
    public decimal ConfirmationVolume { get; init; } = 5;
    public decimal ConfirmationShare { get; init; } = .60m;
    public int ReclaimTicks { get; init; } = 1;
    public int MaximumEntryDistanceTicks { get; init; } = 12;
    public int BreakTicks { get; init; } = 3;
    public int BreakMilliseconds { get; init; } = 800;
    public int MaximumSpreadTicks { get; init; } = 4;
    public int StaleSeconds { get; init; } = 5;
    public int CooldownSeconds { get; init; } = 30;
    public int MaxZones { get; init; } = 300;
    public int MaxDots { get; init; } = 2000;
    public int MaximumAttackProgressTicks { get; init; } = 2;
    public int MinimumStallMilliseconds { get; init; } = 500;
    public int RetestLeaveTicks { get; init; } = 3;
    public int RetestLeaveMilliseconds { get; init; } = 1000;
    public int StructureLifetimeSeconds { get; init; } = 1800;
    public int BreakMinimumTrades { get; init; } = 2;
    public bool DetectTradeZones { get; init; } = true;

    public static Settings For(MarketPreset preset) => preset switch
    {
        MarketPreset.GC => new() { ZoneTicks = 3, ScanTicks = 100, AttackVolume = 20, BreakTicks = 4, MaximumEntryDistanceTicks = 12 },
        MarketPreset.NQ => new() { ZoneTicks = 6, ScanTicks = 120, MinimumChange = 1, AttackVolume = 15, BreakTicks = 6, MaximumSpreadTicks = 6, MaximumEntryDistanceTicks = 20 },
        MarketPreset.ES => new() { ZoneTicks = 2, ScanTicks = 60, MinimumChange = 3, AttackVolume = 60, BreakTicks = 2, MaximumEntryDistanceTicks = 8 },
        _ => new()
    };

    public Settings Validate()
    {
        if (ZoneTicks < 1 || ScanTicks < ZoneTicks || WindowSeconds < 2 || BaselineSeconds < WarmupSeconds
            || WarmupSeconds < 2 || ActiveSeconds < 2 || ActiveSeconds > WindowSeconds || MinimumEvents < 2
            || BurstRatio <= 1 || MinimumChange <= 0 || RelativeChange < 0 || MergeMilliseconds < 1
            || LifetimeSeconds < AttackSeconds || AttackSeconds < 1 || AttackVolume <= 0
            || MinimumAttackTrades < 2 || AttackShare <= .5m || AttackShare > 1 || ReloadCount < 1
            || ReloadMilliseconds < 1 || ConfirmationMilliseconds < 1 || ConfirmationVolume <= 0
            || ConfirmationWindowMilliseconds < ConfirmationMilliseconds || ConfirmationShare <= .5m || ConfirmationShare > 1
            || ReclaimTicks < 1 || MaximumEntryDistanceTicks < ReclaimTicks || BreakTicks < 1 || BreakMilliseconds < 1
            || MaximumSpreadTicks < 1 || StaleSeconds < 1 || CooldownSeconds < 0 || ApproachTicks < 0
            || MaxZones < 1 || MaxDots < 1 || MaximumAttackProgressTicks < 0 || MinimumStallMilliseconds < 0
            || RetestLeaveTicks < 1 || RetestLeaveMilliseconds < 1 || StructureLifetimeSeconds < LifetimeSeconds
            || BreakMinimumTrades < 2)
            throw new ArgumentException("盘口区域参数组合无效。");
        return this;
    }
}

public readonly record struct DepthRow(BookSide Side, decimal Price, decimal Volume);
public readonly record struct EventDot(DateTime Time, int Bar, decimal Price, BookSide Side, EventKind Kind, decimal Volume);
public readonly record struct Notice(NoticeKind Kind, long ZoneId, DateTime Time, int Bar, decimal Price, BookSide Side);
public sealed record ZoneView(long Id, BookSide Side, decimal Low, decimal High, DateTime Created, int CreatedBar,
    ZoneState State, DateTime LastActive, int? EndBar, int EventCount, double Burst, decimal Added, decimal Reduced,
    decimal Attack, int Reloads, int? SignalBar, decimal? SignalPrice)
{
    public ZoneEvidence? Formation { get; init; }
    public ReactionView? Reaction { get; init; }
    public bool FromTrades { get; init; }
    public ReactionView? TradeFormation { get; init; }
}
public sealed record ReactionView(int Visit, decimal Buy, decimal Sell, decimal Unknown, decimal ProgressTicks,
    double ElapsedSeconds, bool Stalled, bool Structural, string Status)
{
    public TradeDistribution? Distribution { get; init; }
}
public sealed record ZoneEvidence(int WindowSeconds, int ObservedSeconds, int ActiveSeconds,
    int Events, double Burst, decimal Added, decimal Reduced);
public sealed record EngineView(string Status, int ObservedLevels, int WarmupRemaining, ZoneView[] Zones, EventDot[] Dots);

