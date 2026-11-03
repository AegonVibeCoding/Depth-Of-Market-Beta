using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using DepthOfMarketBeta.Core;

namespace DepthOfMarketBeta;

public sealed partial class DepthOfMarketBetaIndicator
{
    private Settings _settings = new();
    private bool _enableZones;
    private MarketPreset _preset;

    [Display(Name = "启用密集区功能", GroupName = "Z1. 密集区开关", Order = 1000,
        Description = "默认关闭：保留原生 DOM。开启后采集真实盘口事件；不能从历史K线补算。")]
    public bool EnableZones { get => _enableZones; set { lock (_zonesGate) { _enableZones = value; _engine = null; } RedrawChart(); } }
    [ReadOnly(true), Display(Name = "黄线状态", GroupName = "Z1. 密集区开关", Order = 1001)]
    public string ZoneMonitorStatus
    {
        get { lock (_zonesGate) return !_enableZones ? "密集区功能已关闭"
            : (_engine?.Diagnostics(DateTime.UtcNow) ?? "等待盘口快照") + "｜" + _zoneRenderStatus; }
        set { }
    }

    [Display(Name = "品种起始参数", GroupName = "Z1. 密集区开关", Order = 1010,
        Description = "GC/NQ/ES 是未经收益回测的起始参数；切换会重置新增功能参数及观察记录，不修改原生 DOM。")]
    public MarketPreset ZonePreset { get => _preset; set { _preset = value; Change(Settings.For(value)); } }

    private void Change(Settings s) { s.Validate(); lock (_zonesGate) { _settings = s; _engine = null; } RedrawChart(); }

    [Range(1, 50), Display(Name = "区域宽度（tick）", GroupName = "Z2. 密集区筛选", Order = 1020)]
    public int ZoneWidthTicks { get => _settings.ZoneTicks; set => Change(_settings with { ZoneTicks = value }); }
    [Range(5, 1000), Display(Name = "最大观察距离（tick）", GroupName = "Z2. 密集区筛选", Order = 1030)]
    public int ScanDistanceTicks { get => _settings.ScanTicks; set => Change(_settings with { ScanTicks = value }); }
    [Range(3, 60), Display(Name = "事件窗口（秒）", GroupName = "Z2. 密集区筛选", Order = 1040)]
    public int EventWindowSeconds { get => _settings.WindowSeconds; set => Change(_settings with { WindowSeconds = value }); }
    [Range(10, 600), Display(Name = "基线最少观察秒数", GroupName = "Z2. 密集区筛选", Order = 1050,
        Description = "每个价位和距离分组单独预热，累计至少这些有效秒数后才能生成区域。")]
    public int WarmupSeconds { get => _settings.WarmupSeconds; set => Change(_settings with { WarmupSeconds = value, BaselineSeconds = Math.Max(value, _settings.BaselineSeconds) }); }
    [Range(2, 5000), Display(Name = "窗口最少事件数", GroupName = "Z2. 密集区筛选", Order = 1060)]
    public int MinimumZoneEvents { get => _settings.MinimumEvents; set => Change(_settings with { MinimumEvents = value }); }
    [Range(1.1, 100), Display(Name = "事件速度突增倍数", GroupName = "Z2. 密集区筛选", Order = 1070)]
    public double EventBurstRatio { get => _settings.BurstRatio; set => Change(_settings with { BurstRatio = value }); }
    [Range(0.01, 1000000), Display(Name = "最小变化手数", GroupName = "Z2. 密集区筛选", Order = 1080)]
    public decimal MinimumChangeVolume { get => _settings.MinimumChange; set => Change(_settings with { MinimumChange = value }); }
    [Range(0, 5), Display(Name = "变化/平常挂单量", GroupName = "Z2. 密集区筛选", Order = 1090,
        Description = "0.15=变化至少达到该档平常挂单量的15%，同时满足最小手数。")]
    public decimal RelativeChangeThreshold { get => _settings.RelativeChange; set => Change(_settings with { RelativeChange = value }); }
    [Range(10, 2000), Display(Name = "同档事件合并（毫秒）", GroupName = "Z2. 密集区筛选", Order = 1100)]
    public int EventMergeMilliseconds { get => _settings.MergeMilliseconds; set => Change(_settings with { MergeMilliseconds = value }); }
    [Range(10, 1800), Display(Name = "区域无活跃过期（秒）", GroupName = "Z2. 密集区筛选", Order = 1110)]
    public int ZoneLifetimeSeconds { get => _settings.LifetimeSeconds; set => Change(_settings with { LifetimeSeconds = value }); }

    [Range(1, 1000000), Display(Name = "最少攻击成交量", GroupName = "Z3. 成交与区域观察", Order = 1200)]
    public decimal MinimumAttackVolume { get => _settings.AttackVolume; set => Change(_settings with { AttackVolume = value }); }
    [Range(1, 20), Display(Name = "最少成交后补量次数", GroupName = "Z3. 成交与区域观察", Order = 1210,
        Description = "聚合盘口的补量观察，不保证是同一个订单。")]
    public int MinimumReloadCount { get => _settings.ReloadCount; set => Change(_settings with { ReloadCount = value }); }
    [Range(1, 20), Display(Name = "价格收回距离（tick）", GroupName = "Z3. 成交与区域观察", Order = 1220)]
    public int ReclaimDistanceTicks { get => _settings.ReclaimTicks; set => Change(_settings with { ReclaimTicks = value }); }
    // Keep legacy template fields inert so saved settings cannot re-enable entry signals.
    [Browsable(false), Display(AutoGenerateField = false)]
    public int ReversalHoldMilliseconds { get => 700; set { } }
    [Range(1, 100), Display(Name = "区域失守距离（tick）", GroupName = "Z3. 成交与区域观察", Order = 1240)]
    public int InvalidationTicks { get => _settings.BreakTicks; set => Change(_settings with { BreakTicks = value }); }
    [Range(1, 50), Display(Name = "最大允许价差（tick）", GroupName = "Z3. 成交与区域观察", Order = 1250)]
    public int SpreadLimitTicks { get => _settings.MaximumSpreadTicks; set => Change(_settings with { MaximumSpreadTicks = value }); }
    [Range(0, 50), Display(Name = "接近预警距离（tick）", GroupName = "Z3. 成交与区域观察", Order = 1260)]
    public int NearZoneTicks { get => _settings.ApproachTicks; set => Change(_settings with { ApproachTicks = value }); }

    [Display(Name = "独立检测成交停顿区域", GroupName = "Z6. 成交与回测", Order = 1600)]
    public bool DetectTradeZones { get => _settings.DetectTradeZones; set => Change(_settings with { DetectTradeZones = value }); }
    [Range(0, 50), Display(Name = "攻击最大推进（tick）", GroupName = "Z6. 成交与回测", Order = 1610)]
    public int MaximumAttackProgressTicks { get => _settings.MaximumAttackProgressTicks; set => Change(_settings with { MaximumAttackProgressTicks = value }); }
    [Range(0, 5000), Display(Name = "攻击最少持续（毫秒）", GroupName = "Z6. 成交与回测", Order = 1620)]
    public int MinimumStallMilliseconds { get => _settings.MinimumStallMilliseconds; set => Change(_settings with { MinimumStallMilliseconds = value }); }
    [Range(1, 100), Display(Name = "回测前离开距离（tick）", GroupName = "Z6. 成交与回测", Order = 1630)]
    public int RetestLeaveTicks { get => _settings.RetestLeaveTicks; set => Change(_settings with { RetestLeaveTicks = value }); }
    [Range(100, 10000), Display(Name = "离开持续时间（毫秒）", GroupName = "Z6. 成交与回测", Order = 1640)]
    public int RetestLeaveMilliseconds { get => _settings.RetestLeaveMilliseconds; set => Change(_settings with { RetestLeaveMilliseconds = value }); }
    [Range(1800, 14400), Display(Name = "结构证据保留（秒）", GroupName = "Z6. 成交与回测", Order = 1650)]
    public int StructureLifetimeSeconds { get => _settings.StructureLifetimeSeconds; set => Change(_settings with { StructureLifetimeSeconds = value }); }
    [Range(2, 100), Display(Name = "失守最少越界成交笔数", GroupName = "Z6. 成交与回测", Order = 1660)]
    public int BreakMinimumTrades { get => _settings.BreakMinimumTrades; set => Change(_settings with { BreakMinimumTrades = value }); }

    [Display(Name = "减少文字拥挤", GroupName = "Z4. 区域显示", Order = 1290,
        Description = "黄线说明之间保留额外间距，重叠时省略低优先级说明。黄线及说明受各自数量限制。")]
    public bool ReduceTextClutter { get; set; } = true;
    [Display(Name = "显示密集区域", GroupName = "Z4. 区域显示", Order = 1300)]
    public bool ShowZoneBands { get; set; } = true;
    [Display(Name = "显示形成原因", GroupName = "Z4. 区域显示", Order = 1310)]
    public bool ShowZoneLabels { get; set; } = true;
    [Range(1, 20), Display(Name = "最多显示黄线区域", GroupName = "Z4. 区域显示", Order = 1301,
        Description = "优先保留现价上下方最近区域，其余按距离筛选；内部观察仍继续。")]
    public int MaximumVisibleZones { get; set; } = 6;
    [Range(0, 8), Display(Name = "最多区域短说明", GroupName = "Z4. 区域显示", Order = 1318)]
    public int MaximumZoneLabels { get; set; } = 4;
    [Range(180, 600), Display(Name = "区域短说明宽度", GroupName = "Z4. 区域显示", Order = 1319)]
    public int CompactZoneLabelWidth { get; set; } = 380;
    [Browsable(false), Display(AutoGenerateField = false)]
    public bool ShowEventDots { get => false; set { } }
    [Display(Name = "合并相邻黄线", GroupName = "Z4. 区域显示", Order = 1311)]
    public bool MergeYellowZones { get; set; } = true;
    [Range(0, 10), Display(Name = "允许中间空档（tick）", GroupName = "Z4. 区域显示", Order = 1312)]
    public int YellowMergeGapTicks { get; set; } = 2;
    [Range(2, 100), Display(Name = "合并最大宽度（tick）", GroupName = "Z4. 区域显示", Order = 1313)]
    public int YellowMergeMaximumTicks { get; set; } = 12;
    [Display(Name = "随活跃度变淡", GroupName = "Z4. 区域显示", Order = 1314)]
    public bool FadeZoneActivity { get; set; } = true;
    [Range(5, 100), Display(Name = "最低可见强度（%）", GroupName = "Z4. 区域显示", Order = 1315)]
    public int MinimumZoneIntensity { get; set; } = 25;
    [Range(0, 600), Display(Name = "保持亮度时间（秒）", GroupName = "Z4. 区域显示", Order = 1316)]
    public int ZoneFadeHoldSeconds { get; set; } = 30;
    [Range(30, 3600), Display(Name = "淡化时间（秒）", GroupName = "Z4. 区域显示", Order = 1317,
        Description = "保持亮度后，逐渐降至最低可见强度所用时间；只影响显示，不延长区域有效期。")]
    public int ZoneFadeSeconds { get; set; } = 300;
    // Accept old templates without letting their historical display flag restore ended overlays.
    [Browsable(false), Display(AutoGenerateField = false)]
    public bool ShowEndedZones { get => false; set { } }
    [Browsable(false), Display(AutoGenerateField = false)]
    public bool ShowReversalMarks { get => false; set { } }
    [Browsable(false), Display(AutoGenerateField = false)]
    public bool ShowZoneStatus { get => false; set { } }
    [Browsable(false), Display(AutoGenerateField = false)]
    public int DotSeconds { get; set; } = 30;
    [Browsable(false), Display(AutoGenerateField = false)]
    public int EventStripWidth { get; set; } = 140;
    private int? _eventDistance;
    [Browsable(false), Display(AutoGenerateField = false)]
    public int EventDistance
    {
        get => _eventDistance ?? DefaultEventDistance();
        set { _eventDistance = Math.Clamp(value, 0, 5000); RedrawChart(); }
    }
    [Range(0, 100), Display(Name = "区域透明度强度", GroupName = "Z4. 区域显示", Order = 1380)]
    public int ZoneOpacity { get; set; } = 22;
    [Display(Name = "观察区颜色", GroupName = "Z4. 区域显示", Order = 1390)]
    public Color WatchZoneColor { get; set; } = Color.Gold;
    [Browsable(false), Display(AutoGenerateField = false)]
    public Color LongZoneColor { get; set; } = Color.LimeGreen;
    [Browsable(false), Display(AutoGenerateField = false)]
    public Color ShortZoneColor { get; set; } = Color.Tomato;

    [Display(Name = "区域形成提醒", GroupName = "Z5. 提醒", Order = 1500)]
    public bool AlertFormation { get; set; }
    [Display(Name = "价格接近提醒", GroupName = "Z5. 提醒", Order = 1510)]
    public bool AlertApproach { get; set; } = true;
    [Browsable(false), Display(AutoGenerateField = false)]
    public bool AlertReversal { get => false; set { } }
    [Display(Name = "区域失守提醒", GroupName = "Z5. 提醒", Order = 1530)]
    public bool AlertBroken { get; set; }
    [Display(Name = "提醒声音", GroupName = "Z5. 提醒", Order = 1540)]
    public string ZoneAlertSound { get; set; } = "alert1";
}

