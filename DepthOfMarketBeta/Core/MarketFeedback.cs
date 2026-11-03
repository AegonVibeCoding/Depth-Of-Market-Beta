namespace DepthOfMarketBeta.Core;

public readonly record struct FeedbackPrint(DateTime Time, decimal Price, decimal Volume, int Direction);

public sealed record TradeDistribution(int Prices, int Prints, decimal PeakPrice, decimal PeakShare,
    int AdjacentActivePrices, int WindowSeconds)
{
    // Describes the observed distribution, not an absorption/entry threshold or an individual order.
    public string Summary => Prints < 3 ? "成交样本少"
        : PeakShare >= .70m ? $"单价集中{PeakShare:P0}"
        : AdjacentActivePrices >= 2 ? $"相邻{AdjacentActivePrices}价成交·峰{PeakShare:P0}"
        : $"分散{Prices}价成交·峰{PeakShare:P0}";

    public static TradeDistribution From(IEnumerable<FeedbackPrint> prints, decimal tick, int seconds)
    {
        var rows = prints.Where(p => p.Volume > 0).ToArray();
        var levels = rows.GroupBy(p => p.Price).Select(g => (Price: g.Key, Volume: g.Sum(p => p.Volume))).OrderBy(x => x.Price).ToArray();
        var total = levels.Sum(x => x.Volume);
        if (total == 0) return new(0, 0, 0, 0, 0, seconds);
        var peak = levels.OrderByDescending(x => x.Volume).ThenBy(x => x.Price).First();
        // Ignore tiny prints when describing adjacency, without changing any zone detection rules.
        var active = levels.Where(x => x.Volume / total >= .15m).ToArray();
        var longest = 0; var run = 0; decimal? prior = null;
        foreach (var level in active)
        {
            run = prior.HasValue && level.Price - prior.Value == tick ? run + 1 : 1;
            longest = Math.Max(longest, run); prior = level.Price;
        }
        return new(levels.Length, rows.Length, peak.Price, peak.Volume / total, longest, seconds);
    }
}

/// <summary>Price feedback after an observed crossing. No forecast or execution attribution.</summary>
public sealed class CrossingFeedback
{
    private readonly decimal _level, _tick;
    private readonly int _direction;
    private readonly DateTime _crossed;
    private readonly decimal? _beforeSpeed;
    private DateTime _last;
    private decimal _firstPrice, _lastPrice, _maximum, _attack;
    private int _prints;
    private bool _reclaimed;

    public CrossingFeedback(BookSide side, decimal level, decimal tick, IEnumerable<FeedbackPrint> history, DateTime crossed)
    {
        if (tick <= 0) throw new ArgumentOutOfRangeException(nameof(tick));
        _level = level; _tick = tick; _direction = side == BookSide.Ask ? 1 : -1; _crossed = crossed;
        var prior = history.Where(p => p.Time >= crossed.AddSeconds(-1) && p.Time < crossed).ToArray();
        // Require coverage near both ends; a single burst is not a usable one-second baseline.
        if (prior.Length >= 3 && (prior[^1].Time - prior[0].Time).TotalSeconds >= .5
            && (crossed - prior[0].Time).TotalSeconds >= .75 && (crossed - prior[^1].Time).TotalSeconds <= .25)
            _beforeSpeed = Math.Max(0, _direction * (prior[^1].Price - prior[0].Price) / tick
                / (decimal)(prior[^1].Time - prior[0].Time).TotalSeconds);
    }

    public void Print(FeedbackPrint print)
    {
        if (_reclaimed || print.Volume <= 0 || print.Time < _crossed || (_prints > 0 && print.Time < _last)) return;
        if (_prints == 0) _firstPrice = print.Price;
        _prints++; _last = print.Time; _lastPrice = print.Price;
        _maximum = Math.Max(_maximum, _direction * (print.Price - _level) / _tick);
        if (print.Direction == _direction) _attack += print.Volume;
        _reclaimed = _direction * (print.Price - _level) <= 0;
    }

    public string Stage(DateTime now)
    {
        if (_reclaimed) return "已收回";
        if (_prints == 0 || (now - _last).TotalSeconds > 1) return "成交待更新";
        var seconds = (_last - _crossed).TotalSeconds;
        if (_prints < 3 || seconds < .5) return "已穿越";
        var speed = Math.Max(0, _direction * (_lastPrice - _firstPrice) / _tick / (decimal)seconds);
        return speed == 0 ? "未再推进"
            : _beforeSpeed >= 1 && speed >= _beforeSpeed * 1.5m ? "推进加快"
            : _beforeSpeed >= 1 && speed <= _beforeSpeed * .67m ? "推进放缓" : "正在推进";
    }

    public string Describe(DateTime now)
    {
        if (_prints == 0) return "等待越界成交反馈";
        var seconds = (_last - _crossed).TotalSeconds;
        if (_reclaimed) return $"{seconds:0.0}s回到原位｜最远{_maximum:0.#}t";
        // Silence is not proof that aggressive orders met resistance.
        if ((now - _last).TotalSeconds > 1) return $"近期成交反馈不足｜曾越界{_maximum:0.#}t";
        var advance = Math.Max(0, _direction * (_lastPrice - _level) / _tick);
        if (_prints < 3 || seconds < .5) return $"越界{advance:0.#}t｜等待后续成交";
        var speed = Math.Max(0, _direction * (_lastPrice - _firstPrice) / _tick / (decimal)seconds);
        var state = speed == 0 ? "越界后未再推进"
            : _beforeSpeed >= 1 && speed >= _beforeSpeed * 1.5m ? "推进加快"
            : _beforeSpeed >= 1 && speed <= _beforeSpeed * .67m ? "推进放缓" : "越界后推进";
        return $"{state}{speed:0.#}t/s｜同向{_attack:0.#}";
    }
}

