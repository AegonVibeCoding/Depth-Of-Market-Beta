using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using ATAS.Indicators.Technical;
using DepthOfMarketBeta.Core;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace DepthOfMarketBeta;

[DisplayName("depth of market beta")]
[Display(Description = "原生 ATAS Depth Of Market，集成盘口事件区域及成交观察。")]
[Category("Order Book")]
public sealed partial class DepthOfMarketBetaIndicator : DOM
{
    private readonly object _zonesGate = new();
    private readonly RenderFont _zoneFont = new("Microsoft YaHei", 10);
    private ZoneEngine? _engine;
    private bool _ready, _disposed;
    private DateTime _lastZoneBookCheck;
    private static readonly TimeSpan ZoneTimer = TimeSpan.FromMilliseconds(250);

    protected override void OnInitialize()
    {
        base.OnInitialize();
        SubscribeToTimer(ZoneTimer, OnZoneTimer);
    }

    protected override void OnRecalculate()
    {
        base.OnRecalculate();
        lock (_zonesGate) { _engine = null; _depthCounters.ResetData(); _ready = false; }
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        base.OnCalculate(bar, value); // All original DOM state, scaling and snapshot initialization.
        if (bar == CurrentBar - 1)
        {
            lock (_zonesGate)
            {
                _ready = true;
                var now = DateTime.UtcNow;
                if (_depthCounters.Initialized) _depthCounters.Advance(CurrentBar - 1, now);
                else if (_independentColumns && MarketDepthInfo is not null)
                {
                    var rows = MarketDepthInfo.GetMarketDepthSnapshot().Where(d => d.IsBid || d.IsAsk).Select(ToRow).ToArray();
                    if (rows.Length > 0) { _depthCounters.Seed(rows, CurrentBar - 1, now); _lastColumnsDepth = now; }
                }
            }
        }
    }

    protected override void MarketDepthChanged(MarketDataArg depth)
    {
        base.MarketDepthChanged(depth);
        ProcessDepthColumns(new[] { depth });
        ProcessZoneDepths(new[] { depth });
    }

    protected override void MarketDepthsChanged(IEnumerable<MarketDataArg> depths)
    {
        // Materialize once because both the original DOM and the extension consume this batch.
        var batch = depths.ToArray();
        base.MarketDepthsChanged(batch);
        ProcessDepthColumns(batch);
        ProcessZoneDepths(batch);
    }

    private void ProcessZoneDepths(IEnumerable<MarketDataArg> depths)
    {
        if (!_enableZones || !_ready || _disposed || InstrumentInfo is null || MarketDepthInfo is null || CurrentBar < 1) return;
        var updates = depths.Where(d => d.IsBid || d.IsAsk).Select(ToRow)
            .Where(d => d.Price > 0 && d.Volume >= 0).ToArray();
        if (updates.Length == 0) return;
        lock (_zonesGate)
        {
            var now = DateTime.UtcNow;
            if (_engine is null || _engine.NeedsReseed(now))
            {
                _engine ??= new ZoneEngine(_settings, InstrumentInfo.TickSize);
                _engine.Seed(MarketDepthInfo.GetMarketDepthSnapshot().Where(d => d.IsBid || d.IsAsk)
                    .Select(ToRow), now, CurrentBar - 1);
                return; // The triggering batch is already represented by the snapshot.
            }
            foreach (var row in updates) _engine.Depth(row, now, CurrentBar - 1);
            // Recover a divergent incremental book from the platform's authoritative snapshot.
            // Only a real depth callback may trigger this check; redraw/timer ticks cannot renew data.
            if (_engine.NeedsBookRepair(now) && (now - _lastZoneBookCheck).TotalSeconds >= 1)
            {
                _lastZoneBookCheck = now;
                _engine.TryRepairBook(MarketDepthInfo.GetMarketDepthSnapshot()
                    .Where(d => d.IsBid || d.IsAsk).Select(ToRow), now, CurrentBar - 1);
            }
            DispatchNotices();
        }
    }

    protected override void OnNewTrade(MarketDataArg trade)
    {
        base.OnNewTrade(trade);
        if (!_enableZones || !_ready || _disposed || CurrentBar < 1) return;
        lock (_zonesGate)
        {
            var direction = trade.Direction == TradeDirection.Buy ? 1 : trade.Direction == TradeDirection.Sell ? -1 : 0;
            var now = DateTime.UtcNow;
            if (_enableZones) _engine?.TradePrint(trade.Price, trade.Volume, direction, now, CurrentBar - 1);
            DispatchNotices();
        }
    }

    private void OnZoneTimer()
    {
        if ((!_enableZones && !_independentColumns) || !_ready || _disposed || CurrentBar < 1) return;
        lock (_zonesGate)
        {
            if (_independentColumns) _depthCounters.Advance(CurrentBar - 1, DateTime.UtcNow);
            if (_enableZones) _engine?.Advance(DateTime.UtcNow, CurrentBar - 1);
            DispatchNotices();
        }
        RedrawChart();
    }

    private void DispatchNotices()
    {
        if (_engine is null) return;
        foreach (var n in _engine.DrainNotices())
        {
            var enabled = n.Kind switch
            {
                NoticeKind.Formed => AlertFormation,
                NoticeKind.Approach => AlertApproach,
                NoticeKind.Broken => AlertBroken,
                _ => false
            };
            if (!enabled) continue;
            var action = n.Kind switch
            {
                NoticeKind.Formed => "密集区形成",
                NoticeKind.Approach => "价格接近密集区",
                NoticeKind.Broken => "密集区失守",
                _ => "吸收候选"
            };
            AddAlert(ZoneAlertSound, $"DOM Zones | {action} | {n.Price} | #{n.ZoneId}");
        }
    }

    private static DepthRow ToRow(MarketDataArg d) => new(d.IsBid ? BookSide.Bid : BookSide.Ask, d.Price, d.Volume);

    protected override void OnDispose()
    {
        _disposed = true;
        UnsubscribeFromTimer(ZoneTimer, OnZoneTimer);
        lock (_zonesGate) { _engine = null; }
        base.OnDispose();
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        base.OnRender(context, layout); // Native DOM histogram, fonts, backgrounds and filters.
        if (ChartInfo is null || Container is null || InstrumentInfo is null || CurrentBar < 1)
            return;
        EngineView? view;
        DateTime now;
        lock (_zonesGate) { now = DateTime.UtcNow; view = _enableZones ? _engine?.View(now) : null; }
        if (view is not null) DrawZoneBands(context, view, now);
        DrawDepthColumns(context);
    }
}

