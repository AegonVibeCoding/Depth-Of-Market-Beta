using System.Drawing;
namespace DepthOfMarketBeta.Core;
public static class ZoneDisplayLayout
{
    public static PresentedZone[] SelectZones(IEnumerable<PresentedZone> source, decimal reference, int maximum)
    {
        var live = source.Where(z => !z.Finished).ToArray();
        maximum = Math.Clamp(maximum, 1, 20);
        decimal Distance(PresentedZone z) => reference < z.Low ? z.Low - reference : reference > z.High ? reference - z.High : 0;
        var nearest = live.OrderBy(Distance).ThenByDescending(z => z.LastActive).ToArray();
        var chosen = new List<PresentedZone>();
        // When there is room, preserve the nearest observation on each side of current price.
        if (maximum >= 2)
        {
            var upper = nearest.FirstOrDefault(z => z.Low > reference);
            var lower = nearest.FirstOrDefault(z => z.High < reference);
            if (upper is not null) chosen.Add(upper);
            if (lower is not null) chosen.Add(lower);
        }
        foreach (var z in nearest)
            if (chosen.Count < maximum && !chosen.Contains(z)) chosen.Add(z);
        return chosen.OrderBy(Distance).ThenByDescending(z => z.LastActive).ToArray();
    }
    public static string Range(PresentedZone z) => z.Low == z.High ? $"{z.Low:0.#####}" : $"{z.Low:0.#####}–{z.High:0.#####}";
    public static string Brief(PresentedZone z)
    {
        var main = z.Members.OrderByDescending(m => m.LastActive).First();
        var current = main.Reaction;
        if (current?.Status.Contains("暂停") == true) return "数据不足，暂停判断";
        if (current?.Status.StartsWith("已收回") == true) return "已收回，观察再次触及";
        if (current?.Status.StartsWith("价格越过") == true) return "已越界，等待持续成交";
        if (current?.Stalled == true) return "成交停顿，观察能否守住";
        if (z.State == ZoneState.Absorbing) return "成交后补量，观察后续";
        if (current is { Visit: > 1 }) return $"第{current.Visit - 1}次回测";
        if (current is { Visit: 1 }) return "已触及，观察成交推进";
        var evidence = main.Formation;
        if (evidence is null) return "成交集中，等待回测";
        var total = evidence.Added + evidence.Reduced;
        if (total <= 0) return "等待价格触及";
        return evidence.Added / total >= .65m ? "增挂较多，等待触及"
            : evidence.Reduced / total >= .65m ? "减量较多，等待触及" : "挂量反复，等待触及";
    }
    public static string Label(PresentedZone z, decimal reference) =>
        $"{(z.Low > reference ? "上方" : z.High < reference ? "下方" : "当前")} {Range(z)} · {Brief(z)}";

    public static Rectangle? CompactLabel(Rectangle chart, int right, int priceY, int width, IEnumerable<Rectangle> occupied)
    {
        if (chart.Width < 180 || chart.Height < 28 || priceY < chart.Top + 24 || priceY >= chart.Bottom) return null;
        right = Math.Clamp(right, chart.Left + 164, chart.Right - 4);
        var box = new Rectangle(Math.Max(chart.Left + 4, right - Math.Clamp(width, 160, 600)), priceY - 23,
            Math.Min(Math.Clamp(width, 160, 600), right - chart.Left - 4), 21);
        return occupied.Any(r => Rectangle.Inflate(r, 3, 4).IntersectsWith(box)) ? null : box;
    }
}

