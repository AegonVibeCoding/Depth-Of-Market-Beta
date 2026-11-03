namespace DepthOfMarketBeta.Core;

// One tracker per original price bucket. All times are feed-arrival times, not chart-bar time.
public sealed class PriceReaction(BookSide side, decimal low, decimal high, decimal tick, Settings settings)
{
    private readonly Queue<(DateTime Time, decimal Price, decimal Volume, int Direction, bool Inside)> _prints = new();
    private DateTime _lastPrint, _visitStart;
    private DateTime? _awaySince;
    private DateTime _lastStall;
    private bool _armed, _stallSeen;
    private decimal _buy, _sell, _unknown, _progress;
    private bool _stalled;
    private decimal _lastPrice;
    public int Visit { get; private set; }
    public bool Structural { get; private set; }
    public DateTime LastInside { get; private set; }
    public DateTime LastEvidence { get; private set; }
    public bool Stalled => _stalled;

    public void Print(decimal price, decimal volume, int direction, DateTime now)
    {
        if (price <= 0 || volume <= 0 || (_lastPrint != default && now < _lastPrint)) return;
        var inside = price >= low && price <= high;
        var distance = price < low ? low - price : price > high ? price - high : 0;
        if (_lastPrint != default && (now - _lastPrint).TotalSeconds > settings.StaleSeconds)
        {
            _prints.Clear(); _awaySince = null; _armed = false; _stallSeen = false;
        }
        _lastPrint = now; _lastPrice = price;
        if (inside)
        {
            if (Visit == 0 || _armed)
            {
                Visit++; _visitStart = now; _prints.Clear(); _stallSeen = false;
            }
            _awaySince = null; _armed = false; LastInside = now;
        }
        else if (Visit > 0 && distance >= settings.RetestLeaveTicks * tick)
        {
            _awaySince ??= now;
            if ((now - _awaySince.Value).TotalMilliseconds >= settings.RetestLeaveMilliseconds) _armed = true;
        }
        else _awaySince = null;
        if (Visit > 0)
        {
            _prints.Enqueue((now, price, volume, direction, inside));
            while (_prints.Count > 20000) _prints.Dequeue();
        }
        Calculate(now);
        if (_stalled && inside) { _stallSeen = true; _lastStall = now; LastEvidence = now; }
        var reclaimed = side == BookSide.Bid ? price >= high + settings.ReclaimTicks * tick : price <= low - settings.ReclaimTicks * tick;
        if (_stallSeen && reclaimed && (now - _lastStall).TotalSeconds <= settings.AttackSeconds)
        { Structural = true; LastEvidence = now; }
    }

    private void Calculate(DateTime now)
    {
        while (_prints.Count > 0 && (now - _prints.Peek().Time).TotalSeconds > settings.AttackSeconds) _prints.Dequeue();
        _buy = _sell = _unknown = _progress = 0;
        decimal? first = null;
        var count = 0;
        DateTime? firstAttack = null, lastAttack = null;
        foreach (var p in _prints)
        {
            if (p.Inside)
            {
                if (p.Direction > 0) _buy += p.Volume; else if (p.Direction < 0) _sell += p.Volume; else _unknown += p.Volume;
            }
            if (p.Inside && p.Direction == (side == BookSide.Bid ? -1 : 1))
            { first ??= p.Price; firstAttack ??= p.Time; lastAttack = p.Time; count++; }
            if (first.HasValue) _progress = Math.Max(_progress, (side == BookSide.Bid ? first.Value - p.Price : p.Price - first.Value) / tick);
        }
        var attack = side == BookSide.Bid ? _sell : _buy;
        var total = _buy + _sell + _unknown;
        _stalled = count >= settings.MinimumAttackTrades && attack >= settings.AttackVolume && total > 0
            && attack / total >= settings.AttackShare && _progress <= settings.MaximumAttackProgressTicks
            && firstAttack.HasValue && (lastAttack!.Value - firstAttack.Value).TotalMilliseconds >= settings.MinimumStallMilliseconds;
    }

    public ReactionView View(DateTime now, bool healthy, ZoneState state)
    {
        Calculate(now);
        var quiet = _lastPrint == default || (now - _lastPrint).TotalSeconds > settings.AttackSeconds;
        var inside = _lastPrice >= low && _lastPrice <= high;
        var reclaimed = side == BookSide.Bid ? _lastPrice >= high + settings.ReclaimTicks * tick : _lastPrice <= low - settings.ReclaimTicks * tick;
        // Recompute text with the live rolling window: an old stall must not survive its evidence.
        var current = Visit == 0 ? "等待价格进入"
            : inside ? _stalled ? (side == BookSide.Bid ? "卖成交较多，向下推进有限" : "买成交较多，向上推进有限")
                : "区域内成交，等待价格反应"
            : reclaimed ? "已收回区域，观察延续" : "价格越过区域，观察是否接受";
        var status = state == ZoneState.Invalidated ? "结构失守，停止回测跟踪"
            : state == ZoneState.Expired ? "已过期，停止跟踪"
            : !healthy ? "行情异常，暂停判断"
            : quiet ? "近期无成交，等待新反馈" : current;
        return new(Visit, _buy, _sell, _unknown, _progress,
            Visit == 0 ? 0 : Math.Max(0, (LastInside - _visitStart).TotalSeconds),
            _stalled && healthy && !quiet, Structural, status)
        {
            Distribution = TradeDistribution.From(_prints.Where(p => p.Inside)
                .Select(p => new FeedbackPrint(p.Time, p.Price, p.Volume, p.Direction)), tick, settings.AttackSeconds)
        };
    }
}

