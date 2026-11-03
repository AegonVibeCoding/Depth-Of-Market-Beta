using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using ATAS.Indicators.Technical;
using DepthOfMarketBeta.Core;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace DepthOfMarketBeta;

public sealed partial class DepthOfMarketBetaIndicator
{
    private sealed class ColumnStyle
    {
        public bool Enabled = true;
        public int Width = 85, RightOffset = -1, FontSize = 9;
        public decimal Filter, FixedMaximum = 100;
        public bool AutoScale = true, ShowValues = true, GrowLeft = true;
        public Color Added = Color.WhiteSmoke, Reduced = Color.FromArgb(40, 40, 40), Text = Color.WhiteSmoke;
    }
    private readonly ColumnStyle _netColumn = new()
    {
        RightOffset = 150,
        Added = Color.FromArgb(255, 8, 153, 129), Reduced = Color.FromArgb(255, 242, 56, 90)
    };
    private readonly ColumnStyle _stackColumn = new() { Enabled = false };
    private readonly ColumnStyle _pullColumn = new() { Enabled = false };
    private readonly ColumnStyle _bothColumn = new()
    {
        Width = 150, RightOffset = 300,
        Added = Color.FromArgb(255, 8, 153, 129), Reduced = Color.FromArgb(255, 242, 56, 90)
    };
    private readonly DepthColumnCounters _depthCounters = new();
    private DateTime _lastColumnsDepth;
    private bool _independentColumns = true;
    private readonly RenderStringFormat _columnRightFormat = new() { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
    private readonly RenderStringFormat _columnLeftFormat = new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };

    public DepthOfMarketBetaIndicator() { base.ShowDepthChanges = false; Width = 100; }

    // Hide the original mutually exclusive selector. Native DOM itself remains the base class.
    [Browsable(false), Display(AutoGenerateField = false)]
    public new bool ShowDepthChanges { get => false; set => base.ShowDepthChanges = false; }
    [Browsable(false), Display(AutoGenerateField = false)]
    public new DepthChangesMode DepthChangesDisplayMode { get => base.DepthChangesDisplayMode; set => base.DepthChangesDisplayMode = value; }
    [Browsable(false), Display(AutoGenerateField = false)]
    public new ATAS.Indicators.Filter DepthChangesFilter { get => base.DepthChangesFilter; set => base.DepthChangesFilter = value; }
    [Browsable(false), Display(AutoGenerateField = false)]
    public new Color PullingColor { get => base.PullingColor; set => base.PullingColor = value; }
    [Browsable(false), Display(AutoGenerateField = false)]
    public new Color StackingColor { get => base.StackingColor; set => base.StackingColor = value; }
    [Browsable(false), Display(AutoGenerateField = false)]
    public new Color DepthChangesTextColor { get => base.DepthChangesTextColor; set => base.DepthChangesTextColor = value; }

    [Display(Name = "启用四个独立栏", GroupName = "P0. 拉取和堆叠布局", Order = 500,
        Description = "四栏可以同时开启；默认随图表K线重置，也可指定独立秒数周期。")]
    public bool IndependentDepthColumns { get => _independentColumns; set { lock (_zonesGate) { _independentColumns = value; _depthCounters.ResetData(); } RedrawChart(); } }

    [Range(0, 86400), Display(Name = "共用重置周期（秒）", GroupName = "P0. 拉取和堆叠布局", Order = 501,
        Description = "仅影响各栏重置周期设为-1的栏。0=跟随图表K线；60=1分钟；300=5分钟。正数按本机UTC时钟对齐分段；修改后仅清零继承本参数的栏，保留盘口基线。")]
    public int DepthResetSeconds
    {
        get => _depthCounters.DefaultSeconds;
        set
        {
            lock (_zonesGate)
            {
                _depthCounters.SetDefaultSeconds(value, CurrentBar - 1, DateTime.UtcNow);
            }
            RedrawChart();
        }
    }
    private void SetColumnResetSeconds(DepthColumnMode mode, int value)
    {
        lock (_zonesGate) _depthCounters.SetOverrideSeconds(mode, value, CurrentBar - 1, DateTime.UtcNow);
        RedrawChart();
    }

    private const string ColumnResetDescription = "-1=使用P0共用周期（兼容旧模板）；0=跟随图表K线；正整数=此栏独立秒数，例如60或300。按本机UTC时钟对齐分段，不是滚动窗口或刷新间隔。修改只清零此栏累计，保留盘口基线。";

    // Retain legacy layout values for existing chart templates, without exposing extra position controls.
    [Browsable(false), Display(AutoGenerateField = false)]
    public int DepthColumnGap { get; set; } = 8;

    private int ColumnDistance(DepthColumnMode mode)
    {
        // Resolve an old automatic position even while its column is disabled.
        var specs = ColumnSpecs().Select(s => s.Mode == mode ? s with { Enabled = true } : s);
        return -DepthColumnLayout.Arrange(0, 0, Width, Math.Clamp(DepthColumnGap, 0, 100), specs)
            .Single(p => p.Mode == mode).Right;
    }

    private void SetColumnDistance(DepthColumnMode mode, int value)
    {
        if (value >= 0)
        {
            // Freeze legacy automatic placements before moving one column, so its neighbours stay put.
            _eventDistance ??= DefaultEventDistance();
            var distances = Enum.GetValues<DepthColumnMode>().Select(m => (Mode: m, Distance: ColumnDistance(m))).ToArray();
            foreach (var item in distances) Style(item.Mode).RightOffset = item.Distance;
        }
        Style(mode).RightOffset = Math.Clamp(value, -1, 5000);
        RedrawChart();
    }

    private IEnumerable<ColumnSpec> ColumnSpecs()
    {
        yield return new(DepthColumnMode.Net, _netColumn.Enabled, _netColumn.Width, _netColumn.RightOffset);
        yield return new(DepthColumnMode.Stacked, _stackColumn.Enabled, _stackColumn.Width, _stackColumn.RightOffset);
        yield return new(DepthColumnMode.Pulled, _pullColumn.Enabled, _pullColumn.Width, _pullColumn.RightOffset);
        yield return new(DepthColumnMode.Both, _bothColumn.Enabled, _bothColumn.Width, _bothColumn.RightOffset);
    }
    private ColumnStyle Style(DepthColumnMode mode) => mode switch
    {
        DepthColumnMode.Net => _netColumn, DepthColumnMode.Stacked => _stackColumn,
        DepthColumnMode.Pulled => _pullColumn, _ => _bothColumn
    };

    private void ProcessDepthColumns(IEnumerable<ATAS.Indicators.MarketDataArg> depths)
    {
        if (!_independentColumns || !_ready || _disposed || CurrentBar < 1 || MarketDepthInfo is null) return;
        lock (_zonesGate)
        {
            var now = DateTime.UtcNow;
            if (!_depthCounters.Initialized || (now - _lastColumnsDepth).TotalSeconds > _settings.StaleSeconds)
            {
                _depthCounters.Seed(MarketDepthInfo.GetMarketDepthSnapshot().Where(d => d.IsBid || d.IsAsk).Select(ToRow), CurrentBar - 1, now);
            }
            else
            {
                foreach (var d in depths)
                    if (d.IsBid || d.IsAsk) _depthCounters.Update(ToRow(d), CurrentBar - 1, now);
            }
            _lastColumnsDepth = now;
        }
    }

    private int DefaultEventDistance()
    {
        if (!_independentColumns) return Width + 12;
        var placements = DepthColumnLayout.Arrange(0, 0, Width, Math.Clamp(DepthColumnGap, 0, 100), ColumnSpecs());
        return (placements.Length == 0 ? Width : -placements.Min(p => p.Left)) + 12;
    }

    private void DrawDepthColumns(RenderContext context)
    {
        if (!_independentColumns || ChartInfo is null || Container is null || InstrumentInfo is null || CurrentBar < 1) return;
        // Like native DOM, current depth is hidden when scrolled entirely into historical bars.
        if (ChartInfo.GetXByBar(CurrentBar - 1) > Container.Region.Right) return;
        Dictionary<DepthColumnMode, DepthChangeRow[]> columnRows;
        bool initialized;
        DateTime lastUpdate;
        decimal bestBid, bestAsk;
        lock (_zonesGate)
        {
            _depthCounters.Advance(CurrentBar - 1, DateTime.UtcNow);
            columnRows = Enum.GetValues<DepthColumnMode>().ToDictionary(mode => mode, mode => _depthCounters.Snapshot(mode));
            initialized = _depthCounters.Initialized; lastUpdate = _lastColumnsDepth;
            bestBid = _depthCounters.BestBid; bestAsk = _depthCounters.BestAsk;
        }
        var r = Container.Region;
        var tick = InstrumentInfo.TickSize;
        var stale = initialized && (DateTime.UtcNow - lastUpdate).TotalSeconds > _settings.StaleSeconds;
        var high = ChartInfo.PriceChartContainer.High;
        var low = ChartInfo.PriceChartContainer.Low;
        var currentY = ChartInfo.GetYByPrice(GetCandle(CurrentBar - 1).Close);
        foreach (var p in DepthColumnLayout.Arrange(r.Left, r.Right, Width, Math.Clamp(DepthColumnGap, 0, 100), ColumnSpecs()))
        {
            if (p.Right <= r.Left || p.Left >= r.Right) continue;
            var s = Style(p.Mode);
            var font = new RenderFont("Microsoft YaHei", Math.Clamp(s.FontSize, 6, 24));
            if (stale || !initialized) continue;
            var rows = columnRows[p.Mode];
            var visible = rows.Where(x => x.Price >= low && x.Price <= high).ToArray();
            var columnBid = bestBid != 0 ? bestBid : rows.Where(x => x.Side == BookSide.Bid).Select(x => x.Price).DefaultIfEmpty(0).Max();
            var columnAsk = bestAsk != 0 ? bestAsk : rows.Where(x => x.Side == BookSide.Ask).Select(x => x.Price).DefaultIfEmpty(0).Min();
            var max = s.AutoScale ? visible.Select(x => x.Magnitude(p.Mode)).DefaultIfEmpty(1).Max() : Math.Max(1, s.FixedMaximum);
            foreach (var row in visible)
            {
                var geometry = DepthPriceLayout.Row(row.Side, row.Price, tick, columnBid, columnAsk, currentY,
                    PriceLevelsHeight, ChartInfo.GetYByPrice(row.Price, true), ChartInfo.GetYByPrice(row.Price - tick, true));
                var y = geometry.Top;
                var height = geometry.Height;
                if (y + height < r.Top || y >= r.Bottom) continue;
                if (p.Mode == DepthColumnMode.Both)
                {
                    var half = p.Width / 2;
                    DrawColumnValue(context, r, p.Left, half, y, height, -row.Reduced, max, true, s, font);
                    DrawColumnValue(context, r, p.Left + half, p.Width - half, y, height, row.Added, max, false, s, font);
                }
                else
                {
                    var v = p.Mode == DepthColumnMode.Net ? row.Net : p.Mode == DepthColumnMode.Stacked ? row.Added : -row.Reduced;
                    DrawColumnValue(context, r, p.Left, p.Width, y, height, v, max, s.GrowLeft, s, font);
                }
            }
        }
    }

    private void DrawColumnValue(RenderContext context, Rectangle bounds, int left, int width, int y, int height,
        decimal value, decimal maximum, bool growLeft, ColumnStyle style, RenderFont font)
    {
        if (Math.Abs(value) <= style.Filter) return;
        var barWidth = DepthColumnLayout.BarWidth(value, maximum, width);
        var rect = Rectangle.Intersect(bounds, new Rectangle(growLeft ? left + width - barWidth : left, y, barWidth, Math.Max(1, height)));
        if (rect.Width > 0 && rect.Height > 0) context.FillRectangle(value > 0 ? style.Added : style.Reduced, rect);
        if (!style.ShowValues || height < style.FontSize + 3) return;
        var textRect = Rectangle.Intersect(bounds, new Rectangle(left, y, width, height));
        var label = (value > 0 ? "+" : "−") + ChartInfo!.TryGetMinimizedVolumeString(Math.Abs(value));
        context.DrawString(label, font, style.Text, textRect, growLeft ? _columnRightFormat : _columnLeftFormat);
    }

    
    [Display(Name = "显示此栏", GroupName = "P1. Net 净额", Order = 510)]
    public bool NetEnabled { get => _netColumn.Enabled; set { _netColumn.Enabled = value; RedrawChart(); } }

    [Browsable(false), Display(AutoGenerateField = false)]
    public int NetWidth { get => _netColumn.Width; set { _netColumn.Width = Math.Clamp(value, 20, 1000); RedrawChart(); } }

    [Range(0, 5000)]
    [Display(Name = "距离（像素）", GroupName = "P1. Net 净额", Order = 511,
        Description = "此栏右边缘距图表右侧的距离；越大越往左。只移动此栏，不改变柱条宽度。")]
    public int NetRightOffset { get => ColumnDistance(DepthColumnMode.Net); set => SetColumnDistance(DepthColumnMode.Net, value); }

    [Range(-1, 86400), Display(Name = "重置周期（秒）", GroupName = "P1. Net 净额", Order = 512, Description = ColumnResetDescription)]
    public int NetResetSeconds { get => _depthCounters.OverrideSeconds(DepthColumnMode.Net); set => SetColumnResetSeconds(DepthColumnMode.Net, value); }

    [Range(0, 1000000000)]
    [Display(Name = "最小显示量", GroupName = "P1. Net 净额", Order = 513)]
    public decimal NetFilter { get => _netColumn.Filter; set { _netColumn.Filter = Math.Max(0, value); RedrawChart(); } }

    
    [Display(Name = "独立自动缩放", GroupName = "P1. Net 净额", Order = 514)]
    public bool NetAutoScale { get => _netColumn.AutoScale; set { _netColumn.AutoScale = value; RedrawChart(); } }

    [Range(1, 1000000000)]
    [Display(Name = "固定满宽对应量", GroupName = "P1. Net 净额", Order = 515)]
    public decimal NetFixedMaximum { get => _netColumn.FixedMaximum; set { _netColumn.FixedMaximum = Math.Max(1, value); RedrawChart(); } }

    
    [Display(Name = "显示数值", GroupName = "P1. Net 净额", Order = 516)]
    public bool NetShowValues { get => _netColumn.ShowValues; set { _netColumn.ShowValues = value; RedrawChart(); } }

    
    [Browsable(false), Display(AutoGenerateField = false)]
    public bool NetShowTitle { get => false; set { } }

    [Range(6, 24)]
    [Display(Name = "数字字号", GroupName = "P1. Net 净额", Order = 518)]
    public int NetFontSize { get => _netColumn.FontSize; set { _netColumn.FontSize = Math.Clamp(value, 6, 24); RedrawChart(); } }

    
    [Display(Name = "增加颜色", GroupName = "P1. Net 净额", Order = 519)]
    public Color NetAdded { get => _netColumn.Added; set { _netColumn.Added = value; RedrawChart(); } }

    
    [Display(Name = "减少颜色", GroupName = "P1. Net 净额", Order = 520)]
    public Color NetReduced { get => _netColumn.Reduced; set { _netColumn.Reduced = value; RedrawChart(); } }

    
    [Display(Name = "文字颜色", GroupName = "P1. Net 净额", Order = 521)]
    public Color NetText { get => _netColumn.Text; set { _netColumn.Text = value; RedrawChart(); } }

    
    [Display(Name = "柱条向左延伸", GroupName = "P1. Net 净额", Order = 522)]
    public bool NetGrowLeft { get => _netColumn.GrowLeft; set { _netColumn.GrowLeft = value; RedrawChart(); } }

    
    [Display(Name = "显示此栏", GroupName = "P2. Stacked only 仅堆叠", Order = 610)]
    public bool StackedEnabled { get => _stackColumn.Enabled; set { _stackColumn.Enabled = value; RedrawChart(); } }

    [Browsable(false), Display(AutoGenerateField = false)]
    public int StackedWidth { get => _stackColumn.Width; set { _stackColumn.Width = Math.Clamp(value, 20, 1000); RedrawChart(); } }

    [Range(0, 5000)]
    [Display(Name = "距离（像素）", GroupName = "P2. Stacked only 仅堆叠", Order = 611,
        Description = "此栏右边缘距图表右侧的距离；越大越往左。只移动此栏，不改变柱条宽度。")]
    public int StackedRightOffset { get => ColumnDistance(DepthColumnMode.Stacked); set => SetColumnDistance(DepthColumnMode.Stacked, value); }

    [Range(-1, 86400), Display(Name = "重置周期（秒）", GroupName = "P2. Stacked only 仅堆叠", Order = 612, Description = ColumnResetDescription)]
    public int StackedResetSeconds { get => _depthCounters.OverrideSeconds(DepthColumnMode.Stacked); set => SetColumnResetSeconds(DepthColumnMode.Stacked, value); }

    [Range(0, 1000000000)]
    [Display(Name = "最小显示量", GroupName = "P2. Stacked only 仅堆叠", Order = 613)]
    public decimal StackedFilter { get => _stackColumn.Filter; set { _stackColumn.Filter = Math.Max(0, value); RedrawChart(); } }

    
    [Display(Name = "独立自动缩放", GroupName = "P2. Stacked only 仅堆叠", Order = 614)]
    public bool StackedAutoScale { get => _stackColumn.AutoScale; set { _stackColumn.AutoScale = value; RedrawChart(); } }

    [Range(1, 1000000000)]
    [Display(Name = "固定满宽对应量", GroupName = "P2. Stacked only 仅堆叠", Order = 615)]
    public decimal StackedFixedMaximum { get => _stackColumn.FixedMaximum; set { _stackColumn.FixedMaximum = Math.Max(1, value); RedrawChart(); } }

    
    [Display(Name = "显示数值", GroupName = "P2. Stacked only 仅堆叠", Order = 616)]
    public bool StackedShowValues { get => _stackColumn.ShowValues; set { _stackColumn.ShowValues = value; RedrawChart(); } }

    
    [Browsable(false), Display(AutoGenerateField = false)]
    public bool StackedShowTitle { get => false; set { } }

    [Range(6, 24)]
    [Display(Name = "数字字号", GroupName = "P2. Stacked only 仅堆叠", Order = 618)]
    public int StackedFontSize { get => _stackColumn.FontSize; set { _stackColumn.FontSize = Math.Clamp(value, 6, 24); RedrawChart(); } }

    
    [Display(Name = "增加颜色", GroupName = "P2. Stacked only 仅堆叠", Order = 619)]
    public Color StackedAdded { get => _stackColumn.Added; set { _stackColumn.Added = value; RedrawChart(); } }

    
    [Display(Name = "减少颜色", GroupName = "P2. Stacked only 仅堆叠", Order = 620)]
    public Color StackedReduced { get => _stackColumn.Reduced; set { _stackColumn.Reduced = value; RedrawChart(); } }

    
    [Display(Name = "文字颜色", GroupName = "P2. Stacked only 仅堆叠", Order = 621)]
    public Color StackedText { get => _stackColumn.Text; set { _stackColumn.Text = value; RedrawChart(); } }

    
    [Display(Name = "柱条向左延伸", GroupName = "P2. Stacked only 仅堆叠", Order = 622)]
    public bool StackedGrowLeft { get => _stackColumn.GrowLeft; set { _stackColumn.GrowLeft = value; RedrawChart(); } }

    
    [Display(Name = "显示此栏", GroupName = "P3. Pulled only 仅拉取", Order = 710)]
    public bool PulledEnabled { get => _pullColumn.Enabled; set { _pullColumn.Enabled = value; RedrawChart(); } }

    [Browsable(false), Display(AutoGenerateField = false)]
    public int PulledWidth { get => _pullColumn.Width; set { _pullColumn.Width = Math.Clamp(value, 20, 1000); RedrawChart(); } }

    [Range(0, 5000)]
    [Display(Name = "距离（像素）", GroupName = "P3. Pulled only 仅拉取", Order = 711,
        Description = "此栏右边缘距图表右侧的距离；越大越往左。只移动此栏，不改变柱条宽度。")]
    public int PulledRightOffset { get => ColumnDistance(DepthColumnMode.Pulled); set => SetColumnDistance(DepthColumnMode.Pulled, value); }

    [Range(-1, 86400), Display(Name = "重置周期（秒）", GroupName = "P3. Pulled only 仅拉取", Order = 712, Description = ColumnResetDescription)]
    public int PulledResetSeconds { get => _depthCounters.OverrideSeconds(DepthColumnMode.Pulled); set => SetColumnResetSeconds(DepthColumnMode.Pulled, value); }

    [Range(0, 1000000000)]
    [Display(Name = "最小显示量", GroupName = "P3. Pulled only 仅拉取", Order = 713)]
    public decimal PulledFilter { get => _pullColumn.Filter; set { _pullColumn.Filter = Math.Max(0, value); RedrawChart(); } }

    
    [Display(Name = "独立自动缩放", GroupName = "P3. Pulled only 仅拉取", Order = 714)]
    public bool PulledAutoScale { get => _pullColumn.AutoScale; set { _pullColumn.AutoScale = value; RedrawChart(); } }

    [Range(1, 1000000000)]
    [Display(Name = "固定满宽对应量", GroupName = "P3. Pulled only 仅拉取", Order = 715)]
    public decimal PulledFixedMaximum { get => _pullColumn.FixedMaximum; set { _pullColumn.FixedMaximum = Math.Max(1, value); RedrawChart(); } }

    
    [Display(Name = "显示数值", GroupName = "P3. Pulled only 仅拉取", Order = 716)]
    public bool PulledShowValues { get => _pullColumn.ShowValues; set { _pullColumn.ShowValues = value; RedrawChart(); } }

    
    [Browsable(false), Display(AutoGenerateField = false)]
    public bool PulledShowTitle { get => false; set { } }

    [Range(6, 24)]
    [Display(Name = "数字字号", GroupName = "P3. Pulled only 仅拉取", Order = 718)]
    public int PulledFontSize { get => _pullColumn.FontSize; set { _pullColumn.FontSize = Math.Clamp(value, 6, 24); RedrawChart(); } }

    
    [Display(Name = "增加颜色", GroupName = "P3. Pulled only 仅拉取", Order = 719)]
    public Color PulledAdded { get => _pullColumn.Added; set { _pullColumn.Added = value; RedrawChart(); } }

    
    [Display(Name = "减少颜色", GroupName = "P3. Pulled only 仅拉取", Order = 720)]
    public Color PulledReduced { get => _pullColumn.Reduced; set { _pullColumn.Reduced = value; RedrawChart(); } }

    
    [Display(Name = "文字颜色", GroupName = "P3. Pulled only 仅拉取", Order = 721)]
    public Color PulledText { get => _pullColumn.Text; set { _pullColumn.Text = value; RedrawChart(); } }

    
    [Display(Name = "柱条向左延伸", GroupName = "P3. Pulled only 仅拉取", Order = 722)]
    public bool PulledGrowLeft { get => _pullColumn.GrowLeft; set { _pullColumn.GrowLeft = value; RedrawChart(); } }

    
    [Display(Name = "显示此栏", GroupName = "P4. Both 双向", Order = 810)]
    public bool BothEnabled { get => _bothColumn.Enabled; set { _bothColumn.Enabled = value; RedrawChart(); } }

    [Browsable(false), Display(AutoGenerateField = false)]
    public int BothWidth { get => _bothColumn.Width; set { _bothColumn.Width = Math.Clamp(value, 20, 1000); RedrawChart(); } }

    [Range(0, 5000)]
    [Display(Name = "距离（像素）", GroupName = "P4. Both 双向", Order = 811,
        Description = "此栏右边缘距图表右侧的距离；越大越往左。只移动此栏，不改变柱条宽度。")]
    public int BothRightOffset { get => ColumnDistance(DepthColumnMode.Both); set => SetColumnDistance(DepthColumnMode.Both, value); }

    [Range(-1, 86400), Display(Name = "重置周期（秒）", GroupName = "P4. Both 双向", Order = 812, Description = ColumnResetDescription)]
    public int BothResetSeconds { get => _depthCounters.OverrideSeconds(DepthColumnMode.Both); set => SetColumnResetSeconds(DepthColumnMode.Both, value); }

    [Range(0, 1000000000)]
    [Display(Name = "最小显示量", GroupName = "P4. Both 双向", Order = 813)]
    public decimal BothFilter { get => _bothColumn.Filter; set { _bothColumn.Filter = Math.Max(0, value); RedrawChart(); } }

    
    [Display(Name = "独立自动缩放", GroupName = "P4. Both 双向", Order = 814)]
    public bool BothAutoScale { get => _bothColumn.AutoScale; set { _bothColumn.AutoScale = value; RedrawChart(); } }

    [Range(1, 1000000000)]
    [Display(Name = "固定满宽对应量", GroupName = "P4. Both 双向", Order = 815)]
    public decimal BothFixedMaximum { get => _bothColumn.FixedMaximum; set { _bothColumn.FixedMaximum = Math.Max(1, value); RedrawChart(); } }

    
    [Display(Name = "显示数值", GroupName = "P4. Both 双向", Order = 816)]
    public bool BothShowValues { get => _bothColumn.ShowValues; set { _bothColumn.ShowValues = value; RedrawChart(); } }

    
    [Browsable(false), Display(AutoGenerateField = false)]
    public bool BothShowTitle { get => false; set { } }

    [Range(6, 24)]
    [Display(Name = "数字字号", GroupName = "P4. Both 双向", Order = 818)]
    public int BothFontSize { get => _bothColumn.FontSize; set { _bothColumn.FontSize = Math.Clamp(value, 6, 24); RedrawChart(); } }

    
    [Display(Name = "增加颜色", GroupName = "P4. Both 双向", Order = 819)]
    public Color BothAdded { get => _bothColumn.Added; set { _bothColumn.Added = value; RedrawChart(); } }

    
    [Display(Name = "减少颜色", GroupName = "P4. Both 双向", Order = 820)]
    public Color BothReduced { get => _bothColumn.Reduced; set { _bothColumn.Reduced = value; RedrawChart(); } }

    
    [Display(Name = "文字颜色", GroupName = "P4. Both 双向", Order = 821)]
    public Color BothText { get => _bothColumn.Text; set { _bothColumn.Text = value; RedrawChart(); } }
}

