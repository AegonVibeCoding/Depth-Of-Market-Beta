namespace DepthOfMarketBeta.Core;

// Presentation only: source zones keep their individual lifetimes and confirmation rules.
public sealed record PresentedZone(ZoneView[] Members)
{
    public decimal Low => Members.Min(z => z.Low);
    public decimal High => Members.Max(z => z.High);
    public BookSide Side => Members[0].Side;
    public ZoneState State => Members[0].State;
    // The full merged range did not exist until its newest component formed.
    public int CreatedBar => Members.Max(z => z.CreatedBar);
    public int? EndBar => Members[0].EndBar;
    public DateTime LastActive => Members.Max(z => z.LastActive);
    public bool Finished => State is ZoneState.Invalidated or ZoneState.Expired;
}

public static class ZonePresentation
{
    public static PresentedZone[] Merge(IEnumerable<ZoneView> zones, decimal tick, bool enabled, int gapTicks, int maxTicks)
    {
        if (tick <= 0) throw new ArgumentOutOfRangeException(nameof(tick));
        var result = new List<PresentedZone>();
        foreach (var group in zones.Where(z => z.State is not (ZoneState.Invalidated or ZoneState.Expired)).GroupBy(z => (z.Side, z.State)))
        {
            var members = new List<ZoneView>();
            foreach (var z in group.OrderBy(z => z.Low).ThenBy(z => z.Id))
            {
                // Only yellow observation zones merge. Never mix confirmed, ended or opposite-side zones.
                var merge = enabled && z.State == ZoneState.Watching && members.Count > 0
                    && z.Low - members.Max(x => x.High) <= (Math.Max(0, gapTicks) + 1) * tick
                    && (Math.Max(z.High, members.Max(x => x.High)) - members[0].Low) / tick + 1 <= Math.Max(1, maxTicks);
                if (!merge && members.Count > 0) { result.Add(new(members.ToArray())); members.Clear(); }
                members.Add(z);
            }
            if (members.Count > 0) result.Add(new(members.ToArray()));
        }
        return result.OrderByDescending(z => z.High).ThenBy(z => z.Side).ToArray();
    }

    public static double Opacity(PresentedZone zone, DateTime now, bool fade, int fadeSeconds, int minimumPercent, int holdSeconds = 0)
    {
        if (zone.State == ZoneState.Invalidated) return .18;
        if (!fade) return 1;
        var minimum = Math.Clamp(minimumPercent, 5, 100) / 100d;
        // A single refreshed member must not make all older members fully bright.
        return zone.Members.Average(z =>
        {
            var age = Math.Clamp(((now - z.LastActive).TotalSeconds - Math.Max(0, holdSeconds)) / Math.Max(1, fadeSeconds), 0, 1);
            return 1 - (1 - minimum) * age;
        });
    }

    public static string[] Reason(PresentedZone zone, bool marketFeedback = false)
    {
        // Use one actual formation sample, not a sum of potentially overlapping windows.
        var main = zone.Members.OrderByDescending(z => z.Formation?.Burst ?? 0)
            .ThenByDescending(z => z.Formation?.Events ?? 0).ThenBy(z => z.Id).First();
        var evidence = main.Formation;
        var side = main.Side == BookSide.Bid ? "买盘侧" : "卖盘侧";
        var merged = zone.Members.Length > 1 ? $"合并{zone.Members.Length}区·主区" : "";
        var state = zone.State switch
        {
            ZoneState.Absorbing => "·成交后补量", ZoneState.Confirmed => "",
            ZoneState.Invalidated => "·已失效", ZoneState.Expired => "·已过期", _ => ""
        };
        if (evidence is null)
        {
            var f = main.TradeFormation;
            return [$"{side}{state}｜{merged}形成：成交集中，推进有限"
                + (f is null ? "" : $" 买{f.Buy:0.#}/卖{f.Sell:0.#}·推进{f.ProgressTicks:0.#}t"), Current(main, zone.Members.Length > 1, marketFeedback)];
        }
        var total = evidence.Added + evidence.Reduced;
        var share = total > 0 ? evidence.Added / total : .5m;
        var behavior = total == 0 ? "变化量不足"
            : share >= .65m ? $"增挂主导 {share:P0}"
            : share <= .35m ? $"减量主导 {1 - share:P0}（含成交消耗）"
            : "增减量接近";
        return [$"{side}{state}｜{merged}形成时：{behavior}·{evidence.Events}次·{evidence.Burst:F1}倍",
            main.Reaction is null ? $"{evidence.WindowSeconds}秒窗口｜+{evidence.Added:0.##}/−{evidence.Reduced:0.##}｜当前：等待成交"
                : Current(main, zone.Members.Length > 1, marketFeedback)];
    }

    private static string Current(ZoneView zone, bool merged, bool marketFeedback)
    {
        var r = zone.Reaction;
        if (r is null) return "当前：等待成交";
        var visit = r.Visit == 0 ? "尚未触及" : r.Visit == 1 ? "首次触及" : $"第{r.Visit - 1}次回测";
        if (marketFeedback && r.Distribution is { } distribution)
        {
            var state = r.Status.Contains("暂停") || r.Status.Contains("停止") || r.Visit == 0 || distribution.Prints == 0
                ? r.Status : r.Stalled ? "成交后推进有限"
                : r.Status.StartsWith("已收回") ? "已收回区域" : r.Status.StartsWith("价格越过") ? "已越过区域" : "区域内成交";
            return $"{(merged ? "主区" : "")}当前：{state}｜{visit}｜{distribution.Summary}"
                + $"｜{distribution.WindowSeconds}s量{r.Buy + r.Sell + r.Unknown:0.#}·Δ{r.Buy - r.Sell:0.#}·推进{r.ProgressTicks:0.#}t"
                + (r.Unknown > 0 ? $"·未知{r.Unknown:0.#}" : "");
        }
        return $"{(merged ? "主区" : "")}当前：{r.Status}｜{visit}｜买{r.Buy:0.#}/卖{r.Sell:0.#}/未知{r.Unknown:0.#}"
            + $"·Δ{r.Buy - r.Sell:0.#}·推进{r.ProgressTicks:0.#}t·触及跨度{r.ElapsedSeconds:0.0}s";
    }
}

