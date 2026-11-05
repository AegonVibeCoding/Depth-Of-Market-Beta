namespace DepthOfMarketBeta.Core;

/// <summary>
/// Single-threaded, causal arrival-time engine. The host owns synchronization.
/// MBP reductions include executions: no claim of exact cancellations or trader identity.
/// </summary>
public sealed class ZoneEngine
{
    private readonly Settings _s;
    private readonly decimal _tick;
    private readonly Dictionary<(BookSide, decimal), Level> _book = new();
    private readonly SortedSet<decimal> _bidPrices = new(), _askPrices = new();
    private readonly Dictionary<GroupKey, Group> _groups = new();
    private readonly List<Zone> _zones = new();
    // Retain only a bounded expiry timestamp for duplicate suppression, never ended zone histories.
    private readonly Dictionary<(BookSide Side, decimal Low), DateTime> _cooldowns = new();
    private readonly Queue<EventDot> _dots = new();
    private readonly Queue<Notice> _notices = new();

    private readonly Dictionary<(BookSide Side, long Bucket), (PriceReaction Tracker, DateTime Updated)> _tradeCandidates = new();
    private readonly Dictionary<(BookSide, decimal), Trade> _pendingReload = new();
    private DateTime _now, _lastDepth, _started;
    private long _second = -1, _nextId;
    private int _bar;
    private decimal _bid, _ask;
    private int _bookRepairs;

    private sealed class Level
    {
        public decimal Volume;
        public decimal Typical;
        public DateTime LastEvent;
        public int Band = -1;
    }
    private readonly record struct GroupKey(BookSide Side, long Bucket, int Band);
    private sealed class Sample
    {
        public long Second;
        public bool Observed;
        public int Events;
        public decimal Added, Reduced;
    }
    private sealed class Group
    {
        public Queue<Sample> History = new();
        public Sample Current = new();
    }
    private readonly record struct Trade(DateTime Time, decimal Price, decimal Volume, int Direction);
    private sealed class Zone
    {
        public long Id;
        public BookSide Side;
        public decimal Low, High;
        public DateTime Created, LastActive, LastTouch;
        public int CreatedBar, Events;
        public ZoneState State;
        public double Burst;
        public decimal Added, Reduced, Attack;
        public int? EndBar;

        public bool Approached;
        public DateTime? BreakSince;
        public Queue<DateTime> Reloads = new();
        public Queue<Trade> Attacks = new();
        public ZoneEvidence? Formation;
        public PriceReaction? Reaction;
        public bool FromTrades;
        public ReactionView? TradeFormation;
        public int BreakTrades;
        public decimal BreakVolume;
        public DateTime LastBreakTrade;
    }

    public ZoneEngine(Settings settings, decimal tickSize)
    {
        _s = settings.Validate();
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        _tick = tickSize;
    }

    public void Seed(IEnumerable<DepthRow> rows, DateTime now, int bar)
    {
        // Snapshot/reconnect is a new observation epoch, never a stacking burst.
        _zones.Clear(); _cooldowns.Clear();
        _book.Clear(); _groups.Clear(); _pendingReload.Clear(); _notices.Clear();
        _dots.Clear(); _tradeCandidates.Clear();
        _now = _started = _lastDepth = now;
        _second = Second(now); _bar = bar;
        foreach (var row in rows)
            if (row.Price > 0 && row.Volume > 0)
                _book[(row.Side, row.Price)] = new Level { Volume = row.Volume, Typical = row.Volume };
        UpdateQuotes();
        Observe();
    }

    public bool NeedsReseed(DateTime now) => _second < 0 || (now - _lastDepth).TotalSeconds > _s.StaleSeconds;

    public bool NeedsBookRepair(DateTime now) => _second >= 0 && now >= _now
        && (now - _lastDepth).TotalSeconds <= _s.StaleSeconds
        && (_bid <= 0 || _ask <= _bid || (_ask - _bid) / _tick > _s.MaximumSpreadTicks);

    // Called only after a real depth update, never as a timer heartbeat. A repeated old snapshot
    // must not make a stale feed healthy or manufacture stacking/withdrawal evidence.
    public bool TryRepairBook(IEnumerable<DepthRow> snapshot, DateTime now, int bar)
    {
        if (!NeedsBookRepair(now)) return false;
        var rows = snapshot.Where(r => r.Price > 0 && r.Volume > 0).ToArray();
        var bid = rows.Where(r => r.Side == BookSide.Bid).Select(r => r.Price).DefaultIfEmpty(0).Max();
        var ask = rows.Where(r => r.Side == BookSide.Ask).Select(r => r.Price).DefaultIfEmpty(0).Min();
        if (bid <= 0 || ask <= bid || (ask - bid) / _tick > _s.MaximumSpreadTicks
            || (bid == _bid && ask == _ask)) return false;
        Seed(rows, now, bar);
        _bookRepairs++;
        return true;
    }

    private string HealthProblem(DateTime now)
    {
        var quotes = $"买一{_bid:0.#####}/卖一{_ask:0.#####}";
        var age = Math.Max(0, (now - _lastDepth).TotalSeconds);
        if (age > _s.StaleSeconds)
            return $"盘口未更新，暂停生成｜上次更新{age:0.0}秒前/阈值{_s.StaleSeconds}秒｜{quotes}";
        if (_bid <= 0 || _ask <= 0)
            return $"缺少{(_bid <= 0 && _ask <= 0 ? "买卖" : _bid <= 0 ? "买" : "卖")}盘，暂停生成｜{quotes}｜盘口更新{age:0.0}秒前";
        if (_ask <= _bid)
            return $"买卖盘交叉或锁定，暂停生成｜{quotes}｜盘口更新{age:0.0}秒前";
        return $"价差超限，暂停生成｜当前{(_ask - _bid) / _tick:0.##}tick/允许{_s.MaximumSpreadTicks}tick｜{quotes}｜盘口更新{age:0.0}秒前";
    }

    public string Diagnostics(DateTime now)
    {
        if (_second < 0) return "等待盘口快照";
        if (!Healthy(now)) return HealthProblem(now);
        var cut = Second(now) - _s.WindowSeconds;
        var ready = _groups.Values.Count(g => g.History.Count(x => x.Observed && x.Second < cut) >= _s.WarmupSeconds);
        var events = _groups.Values.Select(g => g.History.Where(x => x.Observed && x.Second >= cut).Sum(x => x.Events)).DefaultIfEmpty().Max();
        return $"累计形成{_nextId}区/活动{_zones.Count(IsLive)}区｜基线就绪{ready}/{_groups.Count}组｜最高窗口事件{events}/{_s.MinimumEvents}"
            + (_bookRepairs > 0 ? $"｜盘口校正{_bookRepairs}次" : "");
    }

    public void Depth(DepthRow row, DateTime now, int bar)
    {
        if (row.Price <= 0 || row.Volume < 0 || now < _now) return;
        Advance(now, bar);
        _lastDepth = now;
        var key = (row.Side, row.Price);
        if (row.Volume > 0)
        {
            // An exchange price cannot be both a resting Bid and Ask. Never reuse the old side's history.
            var otherSide = row.Side == BookSide.Bid ? BookSide.Ask : BookSide.Bid;
            if (_book.Remove((otherSide, row.Price)))
            {
                (otherSide == BookSide.Bid ? _bidPrices : _askPrices).Remove(row.Price);
                _pendingReload.Remove((otherSide, row.Price));
                var bucket = (long)decimal.Floor(row.Price / (_tick * _s.ZoneTicks));
                foreach (var oldGroup in _groups.Keys.Where(k => k.Side == otherSide && k.Bucket == bucket).ToArray())
                    _groups.Remove(oldGroup);
            }
        }
        if (!_book.TryGetValue(key, out var level))
        {
            // A first visible level may be a newly exposed snapshot boundary, not a new order.
            _book[key] = new Level { Volume = row.Volume, Typical = Math.Max(1, row.Volume) };
            UpdateQuote(row);
            if (TryGroup(row.Side, row.Price, out var firstGroup))
            {
                GetGroup(firstGroup).Current.Observed = true;
                _book[key].Band = firstGroup.Band;
            }
            return;
        }
        var previous = level.Volume;
        var delta = row.Volume - previous;
        level.Volume = row.Volume;
        UpdateQuote(row);
        if (!Healthy(now)) { level.Band = -1; return; }
        if (!TryGroup(row.Side, row.Price, out var groupKey)) { level.Band = -1; return; }
        var changedBand = level.Band != groupKey.Band;
        level.Band = groupKey.Band;
        if (delta == 0 || changedBand) return;
        // Filter against the depth before this change; a large change cannot set its own threshold.
        var threshold = Math.Max(_s.MinimumChange, level.Typical * _s.RelativeChange);
        level.Typical = .98m * level.Typical + .02m * Math.Max(1, row.Volume);
        if (Math.Abs(delta) < threshold) return;
        var g = GetGroup(groupKey);
        g.Current.Observed = true;
        if (delta > 0) g.Current.Added += delta;
        else g.Current.Reduced -= delta;
        var kind = delta > 0 ? EventKind.Added : EventKind.Reduced;
        if (delta > 0 && _pendingReload.TryGetValue(key, out var trade)
            && (now - trade.Time).TotalMilliseconds <= _s.ReloadMilliseconds)
        {
            _pendingReload.Remove(key); // One attack print can earn at most one refill observation.
            kind = EventKind.Replenished;
            foreach (var z in _zones.Where(z => IsLive(z) && z.Side == row.Side && Inside(z, row.Price)))
                z.Reloads.Enqueue(now);
        }
        if ((now - level.LastEvent).TotalMilliseconds >= _s.MergeMilliseconds)
        {
            level.LastEvent = now;
            g.Current.Events++;
            _dots.Enqueue(new(now, bar, row.Price, row.Side, kind, Math.Abs(delta)));
            while (_dots.Count > _s.MaxDots) _dots.Dequeue();
        }
    }

    public void TradePrint(decimal price, decimal volume, int direction, DateTime now, int bar)
    {
        if (price <= 0 || volume <= 0 || now < _now) return;
        Advance(now, bar);
        if (!Healthy(now)) return;
        var trade = new Trade(now, price, volume, direction);
        var created = _s.DetectTradeZones ? DetectTradeZone(trade) : new HashSet<long>();

        if (direction != 0)
            _pendingReload[(direction < 0 ? BookSide.Bid : BookSide.Ask, price)] = trade;
        foreach (var z in _zones.Where(IsLive))
        {
            z.Reaction ??= new(z.Side, z.Low, z.High, _tick, _s);
            var previousVisit = z.Reaction.Visit;
            if (!created.Contains(z.Id)) z.Reaction.Print(price, volume, direction, now);
            if (previousVisit > 0 && z.Reaction.Visit > previousVisit)
            {
                z.Attacks.Clear(); z.Reloads.Clear();
                z.Approached = false;
                if (z.State == ZoneState.Absorbing) z.State = ZoneState.Watching;
            }
            if (Inside(z, price))
            {
                z.LastTouch = now;
                z.Attacks.Enqueue(trade);
            }
            EvaluateTrade(z, trade);
        }
        _zones.RemoveAll(z => !IsLive(z));
    }

    public void Advance(DateTime now, int bar)
    {
        if (_second < 0 || now < _now) return;
        _now = now; _bar = bar;
        var second = Second(now);
        if (second > _second)
        {
            var gap = second - _second;
            if (gap > _s.StaleSeconds)
            {
                _groups.Clear(); _pendingReload.Clear(); _tradeCandidates.Clear();
                _zones.Clear(); _cooldowns.Clear();
                _started = now;
            }
            else
            {
                // Only actually observed seconds enter the denominator; no invented quiet history.
                foreach (var pair in _groups.ToArray())
                {
                    var g = pair.Value;
                    g.History.Enqueue(g.Current);
                    while (g.History.Count > 0 && g.History.Peek().Second < second - _s.BaselineSeconds - _s.WindowSeconds)
                        g.History.Dequeue();
                    g.Current = new Sample { Second = second };
                    if (Healthy(now)) Detect(pair.Key, g, now);
                    if (!g.History.Any(x => x.Observed)) _groups.Remove(pair.Key);
                }
            }
            _second = second;
            if (Healthy(now)) Observe();
            foreach (var key in _tradeCandidates.Where(x => (now - x.Value.Updated).TotalSeconds > _s.AttackSeconds).Select(x => x.Key).ToArray())
                _tradeCandidates.Remove(key);
            // Bound retained price baselines once per second, not in the tick hot path.
            if (_book.Count > 4000)
                foreach (var key in _book.Where(k => k.Value.Volume == 0 && !TryGroup(k.Key.Item1, k.Key.Item2, out _)).Select(k => k.Key).ToArray())
                    _book.Remove(key);
        }


        foreach (var key in _pendingReload.Where(p => (now - p.Value.Time).TotalMilliseconds > _s.ReloadMilliseconds).Select(p => p.Key).ToArray())
            _pendingReload.Remove(key);
        foreach (var z in _zones.Where(IsLive))
        {
            TrimZone(z, now);
            var retained = z.Reaction is { Structural: true } reaction
                && (now - reaction.LastEvidence).TotalSeconds <= _s.StructureLifetimeSeconds;
            if (!retained && (now - z.LastActive).TotalSeconds > _s.LifetimeSeconds) End(z, ZoneState.Expired, bar);
            if (!Healthy(now)) { z.BreakSince = null; z.BreakTrades = 0; z.BreakVolume = 0; }
        }
        _zones.RemoveAll(z => !IsLive(z));
        foreach (var key in _cooldowns.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
            _cooldowns.Remove(key);
        while (_zones.Count > _s.MaxZones)
        {
            var i = _zones.FindIndex(z => !IsLive(z));
            _zones.RemoveAt(i >= 0 ? i : 0);
        }
    }

    private void Observe()
    {
        if (_bid <= 0 || _ask <= _bid) return;
        foreach (var (key, level) in _book)
        {
            if (level.Volume > 0 && TryGroup(key.Item1, key.Item2, out var group))
            {
                GetGroup(group).Current.Observed = true;
                if (level.Band < 0) level.Band = group.Band;
            }
        }
    }

    private Group GetGroup(GroupKey key)
    {
        if (!_groups.TryGetValue(key, out var g))
            _groups[key] = g = new Group { Current = new Sample { Second = _second } };
        return g;
    }

    private bool TryGroup(BookSide side, decimal price, out GroupKey key)
    {
        var distance = side == BookSide.Bid ? (_bid - price) / _tick : (price - _ask) / _tick;
        key = default;
        if (_bid <= 0 || _ask <= _bid || distance < 0 || distance > _s.ScanTicks) return false;
        // Match the same region AND a comparable distance from touch, separately for Bid/Ask.
        var band = distance <= 5 ? 0 : distance <= 20 ? 1 : 2;
        key = new(side, (long)decimal.Floor(price / (_tick * _s.ZoneTicks)), band);
        return true;
    }

    private void Detect(GroupKey key, Group g, DateTime now)
    {
        var cut = Second(now) - _s.WindowSeconds;
        var recent = g.History.Where(x => x.Second >= cut && x.Observed).ToArray();
        var baseline = g.History.Where(x => x.Second < cut && x.Observed).ToArray();
        if (baseline.Length < _s.WarmupSeconds || recent.Length < _s.ActiveSeconds) return;
        var count = recent.Sum(x => x.Events);
        var burst = count / (double)recent.Length / Math.Max(.2, baseline.Average(x => x.Events));
        if (count < _s.MinimumEvents || recent.Count(x => x.Events > 0) < _s.ActiveSeconds || burst < _s.BurstRatio) return;
        var low = key.Bucket * _tick * _s.ZoneTicks;
        var high = low + (_s.ZoneTicks - 1) * _tick;
        // Don't create a fresh support from old Ask data, or a resistance from old Bid data.
        if (key.Side == BookSide.Bid ? high > _bid : low < _ask) return;
        if (CoolingDown(key.Side, low, now)) return;
        var z = _zones.LastOrDefault(z => z.Side == key.Side && z.Low == low && IsLive(z));
        if (z is null)
        {
            z = new Zone { Id = ++_nextId, Side = key.Side, Low = low, High = high, Created = now,
                LastActive = now, CreatedBar = _bar, State = ZoneState.Watching };
            z.Formation = new(_s.WindowSeconds, recent.Length, recent.Count(x => x.Events > 0),
                count, burst, recent.Sum(x => x.Added), recent.Sum(x => x.Reduced));
            _zones.Add(z);
            _notices.Enqueue(new(NoticeKind.Formed, z.Id, now, _bar, low, z.Side));
        }
        // Only a new active second extends lifetime, not an aging rolling-window total.
        if (recent.Any(x => x.Second == Second(now) - 1 && x.Events > 0)) z.LastActive = now;
        z.Events = count; z.Burst = burst; z.Added = recent.Sum(x => x.Added); z.Reduced = recent.Sum(x => x.Reduced);
    }

    private HashSet<long> DetectTradeZone(Trade t)
    {
        var created = new HashSet<long>();
        // Observe both candidate sides with every print, including unknown direction in the denominator.
        var bucket = (long)decimal.Floor(t.Price / (_tick * _s.ZoneTicks));
        // Excursions outside a candidate bucket count as progress, not as its traded volume.
        foreach (var candidate in _tradeCandidates.Where(x => x.Key.Bucket != bucket))
            candidate.Value.Tracker.Print(t.Price, t.Volume, t.Direction, t.Time);
        foreach (var side in new[] { BookSide.Bid, BookSide.Ask })
        {
            var low = bucket * _tick * _s.ZoneTicks;
            if (CoolingDown(side, low, t.Time) || _zones.Any(z => z.Side == side && z.Low == low && IsLive(z))) continue;
            var key = (side, bucket);
            if (!_tradeCandidates.TryGetValue(key, out var item))
            {
                if (_tradeCandidates.Count >= 1000) continue;
                item = (new PriceReaction(side, low, low + (_s.ZoneTicks - 1) * _tick, _tick, _s), t.Time);
            }
            item.Tracker.Print(t.Price, t.Volume, t.Direction, t.Time);
            if (item.Tracker.Stalled)
            {
                var z = new Zone { Id = ++_nextId, Side = side, Low = low, High = low + (_s.ZoneTicks - 1) * _tick,
                    Created = t.Time, CreatedBar = _bar, LastActive = t.Time, State = ZoneState.Watching,
                    Reaction = item.Tracker, FromTrades = true, TradeFormation = item.Tracker.View(t.Time, true, ZoneState.Watching) };
                _zones.Add(z); _tradeCandidates.Remove(key); created.Add(z.Id);
                _notices.Enqueue(new(NoticeKind.Formed, z.Id, t.Time, _bar, low, side));
            }
            else
            {
                _tradeCandidates[key] = (item.Tracker, t.Time);
            }
        }
        return created;
    }

    private void EvaluateTrade(Zone z, Trade t)
    {
        var longSide = z.Side == BookSide.Bid;
        var direction = longSide ? 1 : -1;
        var distance = t.Price < z.Low ? z.Low - t.Price : t.Price > z.High ? t.Price - z.High : 0;
        if (!z.Approached && distance <= _s.ApproachTicks * _tick)
        {
            z.Approached = true;
            _notices.Enqueue(new(NoticeKind.Approach, z.Id, t.Time, _bar, t.Price, z.Side));
        }
        var broken = longSide ? t.Price < z.Low - _s.BreakTicks * _tick : t.Price > z.High + _s.BreakTicks * _tick;
        if (broken)
        {
           
            if ((t.Time - z.LastBreakTrade).TotalSeconds > _s.AttackSeconds)
            { z.BreakSince = null; z.BreakTrades = 0; z.BreakVolume = 0; }
            z.BreakSince ??= t.Time;
            z.LastBreakTrade = t.Time;
            z.BreakTrades++; z.BreakVolume += t.Volume;
            if ((t.Time - z.BreakSince.Value).TotalMilliseconds >= _s.BreakMilliseconds
                && z.BreakTrades >= _s.BreakMinimumTrades && z.BreakVolume >= _s.AttackVolume)
            {
                z.LastActive = t.Time;
                End(z, ZoneState.Invalidated, _bar);
                _notices.Enqueue(new(NoticeKind.Broken, z.Id, t.Time, _bar, t.Price, z.Side));
            }
            return;
        }
        z.BreakSince = null; z.BreakTrades = 0; z.BreakVolume = 0;
        TrimZone(z, t.Time);
        var attack = z.Attacks.Where(a => a.Direction == -direction).ToArray();
        var total = z.Attacks.Sum(a => a.Volume);
        z.Attack = attack.Sum(a => a.Volume);
        if (z.State == ZoneState.Watching && Inside(z, t.Price) && attack.Length >= _s.MinimumAttackTrades
            && z.Attack >= _s.AttackVolume && total > 0 && z.Attack / total >= _s.AttackShare
            && z.Reloads.Count >= _s.ReloadCount && z.Reaction is { Stalled: true })
        {
            z.State = ZoneState.Absorbing;
            _notices.Enqueue(new(NoticeKind.Absorption, z.Id, t.Time, _bar, t.Price, z.Side));
        }
        if (z.State != ZoneState.Absorbing) return;
        if ((t.Time - z.LastTouch).TotalSeconds > _s.AttackSeconds)
        {
            z.State = ZoneState.Watching;
            z.Attacks.Clear(); z.Reloads.Clear();
            return;
        }
        // Observation only: recovery and directional flow never create entry signals.
    }

    private void TrimZone(Zone z, DateTime now)
    {
        var cutoff = now.AddSeconds(-_s.AttackSeconds);
        while (z.Attacks.Count > 0 && z.Attacks.Peek().Time < cutoff) z.Attacks.Dequeue();
        while (z.Reloads.Count > 0 && z.Reloads.Peek() < cutoff) z.Reloads.Dequeue();
    }
    private void UpdateQuotes()
    {
        _bidPrices.Clear(); _askPrices.Clear();
        foreach (var (key, level) in _book)
        {
            if (level.Volume <= 0) continue;
            (key.Item1 == BookSide.Bid ? _bidPrices : _askPrices).Add(key.Item2);
        }
        _bid = _bidPrices.Count == 0 ? 0 : _bidPrices.Max;
        _ask = _askPrices.Count == 0 ? 0 : _askPrices.Min;
    }
    private void UpdateQuote(DepthRow row)
    {
        var prices = row.Side == BookSide.Bid ? _bidPrices : _askPrices;
        if (row.Volume > 0) prices.Add(row.Price); else prices.Remove(row.Price);
        _bid = _bidPrices.Count == 0 ? 0 : _bidPrices.Max;
        _ask = _askPrices.Count == 0 ? 0 : _askPrices.Min;
    }
    private bool Healthy(DateTime now) => _bid > 0 && _ask > _bid && (_ask - _bid) / _tick <= _s.MaximumSpreadTicks
        && (now - _lastDepth).TotalSeconds <= _s.StaleSeconds;
    private static long Second(DateTime t) => t.Ticks / TimeSpan.TicksPerSecond;
    private static bool Inside(Zone z, decimal p) => p >= z.Low && p <= z.High;
    private static bool IsLive(Zone z) => z.State is not (ZoneState.Expired or ZoneState.Invalidated);
    private bool CoolingDown(BookSide side, decimal low, DateTime now) =>
        _cooldowns.TryGetValue((side, low), out var until) && now < until;
    private void End(Zone z, ZoneState state, int bar)
    {
        z.State = state; z.EndBar = bar;
        var until = z.LastActive.AddSeconds(_s.CooldownSeconds);
        if (until > _now) _cooldowns[(z.Side, z.Low)] = until;
        if (_cooldowns.Count > 1000) _cooldowns.Remove(_cooldowns.MinBy(p => p.Value).Key);
        _tradeCandidates.Remove((z.Side, (long)decimal.Floor(z.Low / (_tick * _s.ZoneTicks))));
        z.Attacks.Clear(); z.Reloads.Clear(); z.Reaction = null; z.Formation = null; z.TradeFormation = null;
    }

    public Notice[] DrainNotices()
    {
        var result = _notices.ToArray(); _notices.Clear(); return result;
    }
    public EngineView View(DateTime now)
    {
        var remaining = Math.Max(0, _s.WarmupSeconds + _s.WindowSeconds - (int)(now - _started).TotalSeconds);
        var status = _book.Count == 0 ? "等待 Level 2 盘口" : !Healthy(now) ? HealthProblem(now)
            : remaining > 0 ? $"建立基线 {remaining}s" : "监测中（新价位单独预热）";
        return new(status, _book.Count(x => x.Value.Volume > 0), remaining,
            _zones.Select(z => new ZoneView(z.Id, z.Side, z.Low, z.High, z.Created, z.CreatedBar, z.State,
                z.LastActive, z.EndBar, z.Events, z.Burst, z.Added, z.Reduced, z.Attack, z.Reloads.Count, null, null)
                { Formation = z.Formation, FromTrades = z.FromTrades, TradeFormation = z.TradeFormation,
                    Reaction = z.Reaction?.View(now, Healthy(now), z.State) }).ToArray(), _dots.ToArray());
    }
}

