using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using DepthOfMarketBeta.Core;

if (args.Length != 2) throw new ArgumentException("Pass beta DLL and platform SDK directory");
var dll = Path.GetFullPath(args[0]); var sdk = Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(sdk, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
var type = assembly.GetType("DepthOfMarketBeta.DepthOfMarketBetaIndicator", true)!;
Check(assembly.GetName().Name == "DepthOfMarketBeta", "Distinct assembly identity missing");
Check(type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName == "depth of market beta", "Wrong indicator name");
Check(type.BaseType?.Name == "DOM", "Native DOM inheritance missing");
Check(!assembly.GetTypes().Any(t => new[] { "Licens", "Flash", "Overview", "Churn", "BookEventHighlight" }
    .Any(word => t.FullName!.Contains(word, StringComparison.OrdinalIgnoreCase))), "Removed module compiled into beta");
Check(!assembly.GetReferencedAssemblies().Any(a => a.Name is "System.Net.Http" or "LiquidityZones"), "Old indicator or HTTP client dependency remains");
var declared = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
Check(!declared.Any(p => new[] { "Activation", "Licens", "Flash", "Overview" }.Any(word => p.Name.Contains(word))), "Removed setting remains");
Check(!declared.Any(p => p.GetCustomAttribute<DisplayAttribute>()?.GroupName is { } group
    && (group.StartsWith("A0.") || group.StartsWith("M1.") || group.StartsWith("M2.") || group.StartsWith("M3.")
        || group.StartsWith("S1.") || group.StartsWith("S2.") || group.StartsWith("S3.") || group.StartsWith("S4."))), "Removed settings group remains");
Check(!type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Any(m => m.Name.StartsWith("ProcessMouse")), "Removed event click handler remains");
Console.WriteLine("PASS compiled beta name, native DOM, no licensing/panel/short-event types, settings, HTTP dependency or event click handlers");
foreach (var (name, group) in new[] { ("NetResetSeconds", "P1."), ("StackedResetSeconds", "P2."), ("PulledResetSeconds", "P3."), ("BothResetSeconds", "P4.") })
{
    var property = type.GetProperty(name)!;
    Check(property.GetCustomAttribute<DisplayAttribute>()?.GroupName?.StartsWith(group) == true
        && property.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false, "Independent reset setting missing: " + name);
}
Check(type.GetProperty("EnableZones") is not null && type.GetProperty("MergeYellowZones") is not null, "Yellow regions removed unexpectedly");
Console.WriteLine("PASS retained four reset settings and yellow region controls");

var now = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
var counters = new DepthColumnCounters();
counters.SetOverrideSeconds(DepthColumnMode.Net, 60, 1, now);
counters.SetOverrideSeconds(DepthColumnMode.Both, 300, 1, now);
counters.Seed([new(BookSide.Bid, 100, 100), new(BookSide.Ask, 101, 100)], 1, now);
Check(Enum.GetValues<DepthColumnMode>().All(m => counters.Snapshot(m).Length == 0), "Snapshot generated false change");
counters.Update(new(BookSide.Bid, 100, 130), 1, now.AddSeconds(10));
counters.Update(new(BookSide.Bid, 100, 110), 1, now.AddSeconds(20));
counters.Advance(2, now.AddSeconds(61));
Check(counters.Snapshot(DepthColumnMode.Net).Length == 0 && counters.Snapshot(DepthColumnMode.Pulled).Length == 0, "Minute and chart reset failed");
var both = counters.Snapshot(DepthColumnMode.Both).Single();
Check(both.Added == 30 && both.Reduced == 20 && both.Net == 10, "Both five-minute totals changed");
counters.Advance(2, now.AddSeconds(300));
Check(counters.Snapshot(DepthColumnMode.Both).Length == 0, "Both period reset failed");
counters.Update(new(BookSide.Bid, 100, 115), 2, now.AddSeconds(301));
Check(counters.Snapshot(DepthColumnMode.Both).Single().Added == 5, "Reset lost baseline");
Console.WriteLine("PASS snapshot baseline, independent chart/60s/300s resets and gross add/reduce totals");

ZoneView Zone(long id, decimal price, ZoneState state = ZoneState.Watching) =>
    new(id, BookSide.Bid, price, price, now, 1, state, now, null, 20, 3, 30, 10, 0, 0, null, null);
var merged = ZonePresentation.Merge([Zone(1, 98), Zone(2, 99), Zone(3, 105, ZoneState.Expired)], 1, true, 0, 12);
Check(merged.Length == 1 && merged[0].Members.Length == 2 && merged[0].Low == 98 && merged[0].High == 99, "Yellow merge/expiry regression");
var upper = new PresentedZone([Zone(4, 104)]);
var selected = ZoneDisplayLayout.SelectZones([merged[0], upper, new([Zone(5, 110)])], 100, 2);
Check(selected.Length == 2 && selected.Any(z => z.Low > 100) && selected.Any(z => z.High < 100), "Nearest upper/lower region selection lost");
Check(ZonePresentation.Opacity(merged[0], now.AddSeconds(330), true, 300, 25, 30) == .25, "Yellow fade regression");
var chart = new Rectangle(0, 0, 1000, 600);
var label = ZoneDisplayLayout.CompactLabel(chart, 600, 200, 300, [])!.Value;
Check(ZoneDisplayLayout.CompactLabel(chart, 600, 200, 300, [label]) is null, "Yellow labels overlap");
Console.WriteLine("PASS yellow merge, expired-region removal, upper/lower selection, fade and caption collision");

var ask = DepthPriceLayout.Row(BookSide.Ask, 101, .25m, 100, 101, 300, 20, 999, 999);
var bid = DepthPriceLayout.Row(BookSide.Bid, 100, .25m, 100, 101, 300, 20, 999, 999);
Check(ask.Top == 266 && bid.Top == 342 && ask.Height == 20, "Custom DOM row alignment changed");
Check(DepthColumnLayout.BarWidth(0, 100, 75) == 0 && DepthColumnLayout.BarWidth(-50, 100, 100) == 50, "Both base bar geometry changed");
Console.WriteLine($"PASS native/custom row layout and plain bars; beta {assembly.GetName().Version} verified");

var span = ZoneChartLayout.Span(chart, 200)!.Value;
var band = new Rectangle(span.Left, 200, span.Right - span.Left, 40);
Rectangle[] columns = [new(900, 0, 100, 600), new(765, 0, 85, 600), new(550, 0, 150, 600)];
var right = ZoneChartLayout.CaptionRight(chart, 12);
Check(right == 988, "Right caption must anchor to chart right edge");
var caption = ZoneChartLayout.RightCaption(chart, band, right, 192, 380, [])!.Value;
Check(band.Contains(caption) && caption.Right == right && caption.Width == 192, "Right caption detached or oversized");
foreach (var delta in new[] { -100, 100 })
{
    var movedSpan = ZoneChartLayout.Span(chart, 200 + delta)!.Value;
    var movedBand = new Rectangle(movedSpan.Left, 200, movedSpan.Right - movedSpan.Left, 40);
    var movedCaption = ZoneChartLayout.RightCaption(chart, movedBand, right, 192, 380, [])!.Value;
    Check(movedSpan.Left == span.Left + delta && movedSpan.Right == chart.Right - 1, "Zone extension changed");
    Check(movedCaption == caption, "Horizontal pan moved right-aligned caption");
}
var longer = ZoneChartLayout.RightCaption(chart, band, right, 900, 300, [])!.Value;
Check(longer.Width == 300 && longer.Right == caption.Right, "Measured width cap or shared right alignment failed");
var thin = ZoneChartLayout.RightCaption(chart, new Rectangle(200, 200, 799, 3), right, 192, 380, [])!.Value;
Check(thin.Top + thin.Height / 2 == 201, "Thin price band center misaligned");
var shifted = ZoneChartLayout.RightCaption(chart, new Rectangle(200, 210, 799, 40), right, 192, 380, [])!.Value;
Check(shifted.Top == caption.Top + 10 && shifted.Right == caption.Right, "Price movement detached caption");
Check(ZoneChartLayout.RightCaption(chart, band, right, 192, 380, [caption]) is null, "Overlapping caption was moved instead of omitted");
Check(ZoneChartLayout.RightCaption(chart, new Rectangle(950, 200, 49, 40), right, 192, 380, []) is null, "Too-narrow region spills outside its own band");
Check(ZoneChartLayout.RightCaption(chart, new Rectangle(200, -25, 799, 40), right, 192, 380, []) is null, "Offscreen caption pinned to chart edge");
Check(ZoneChartLayout.CaptionRight(chart, 30) == 970, "Configured chart-edge gap not applied");
Check(ZoneChartLayout.CaptionRight(chart, -1) == 1000, "Negative gap not clamped");
Check(ZoneChartLayout.CaptionRight(chart, 500) == 800, "Excessive gap not clamped");
var zeroGap = ZoneChartLayout.CaptionRight(chart, 0);
Check(ZoneChartLayout.RightCaption(chart, band, zeroGap, 192, 380, [])!.Value.Right == 1000, "Zero chart-edge margin failed");
var narrowChart = new Rectangle(0, 0, 320, 360);
var narrowBand = new Rectangle(10, 100, 309, 24);
var narrowRight = ZoneChartLayout.CaptionRight(narrowChart, 12);
var narrowLabel = ZoneChartLayout.RightCaption(narrowChart, narrowBand, narrowRight, 500, 380, [])!.Value;
Check(narrowBand.Contains(narrowLabel) && narrowLabel.Right == 308, "Narrow-chart caption overflow");
Check(ZoneChartLayout.Span(chart, -100)!.Value.Left == chart.Left && ZoneChartLayout.Span(chart, 1000) is null, "Zone clipping changed");
var setting = type.GetProperty("ZoneLabelRightGap") ?? throw new Exception("Missing visible caption gap setting");
Check(setting.GetCustomAttribute<DisplayAttribute>()?.GroupName == "Z4. 区域显示"
    && setting.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false, "Caption gap setting is hidden or misplaced");
Console.WriteLine("PASS 1.1 fixed right anchor, measured-width cap, pan/price tracking, chart-edge anchoring, collision, gap setting and narrow charts");
BookHealthChecks.Run();
