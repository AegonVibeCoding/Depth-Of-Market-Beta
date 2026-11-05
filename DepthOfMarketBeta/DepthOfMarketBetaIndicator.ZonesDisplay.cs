using System.Drawing;
using ATAS.Indicators;
using DepthOfMarketBeta.Core;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace DepthOfMarketBeta;

public sealed partial class DepthOfMarketBetaIndicator
{
    private string _zoneRenderStatus = "等待黄线绘制";
    private readonly RenderStringFormat _reasonFormat = new()
    {
        Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap
    };

    private void DrawZoneBands(RenderContext context, EngineView view, DateTime now)
    {
        if (!ShowZoneBands) { _zoneRenderStatus = "区域显示已关闭"; return; }
        if (ChartInfo is null || Container is null || InstrumentInfo is null) return;
        var r = Container.Region;
        var tick = InstrumentInfo.TickSize;
        var reference = GetCandle(CurrentBar - 1).Close;
        var source = view.Zones.Where(z => z.CreatedBar <= LastVisibleBarNumber
            && z.State is not (ZoneState.Invalidated or ZoneState.Expired));
        var merged = ZonePresentation.Merge(source, tick, MergeYellowZones, YellowMergeGapTicks, YellowMergeMaximumTicks);
        var visible = merged.Where(z => ChartInfo.GetYByPrice(z.High + tick / 2, false) < r.Bottom
            && ChartInfo.GetYByPrice(z.Low - tick / 2, false) > r.Top);
        var zones = ZoneDisplayLayout.SelectZones(visible, reference, MaximumVisibleZones);
        var labels = new List<(PresentedZone Zone, Rectangle Band)>();
        var painted = 0; var offscreen = 0;
        foreach (var z in zones)
        {
            var formedX = ChartInfo.GetXByBar(z.CreatedBar);
            var span = ZoneChartLayout.Span(r, formedX);
            if (span is null) { offscreen++; continue; }
            var (x1, x2) = span.Value;
            var y1 = ChartInfo.GetYByPrice(z.High + tick / 2, false);
            var y2 = ChartInfo.GetYByPrice(z.Low - tick / 2, false);
            var actualTop = Math.Min(y1, y2);
            var top = Math.Max(r.Top, actualTop);
            var bottom = Math.Min(r.Bottom, Math.Max(y1, y2));
            if (x2 <= x1 || bottom <= top) { offscreen++; continue; }
            painted++;
            var color = WatchZoneColor;
            var intensity = ZonePresentation.Opacity(z, now, FadeZoneActivity, ZoneFadeSeconds, MinimumZoneIntensity, ZoneFadeHoldSeconds);
            var lineColor = Color.FromArgb((int)Math.Round(color.A * intensity), color);
            var fillAlpha = (int)Math.Round((z.State == ZoneState.Invalidated ? 10 : Math.Clamp(ZoneOpacity, 0, 100)) * intensity * color.A / 255d);
            context.FillRectangle(Color.FromArgb(fillAlpha, color), new Rectangle(x1, top, x2 - x1, bottom - top));
            var pen = new RenderPen(lineColor, 1);
            if (actualTop >= r.Top) context.DrawLine(pen, x1, top, x2, top);
            if (Math.Max(y1, y2) < r.Bottom) context.DrawLine(pen, x1, bottom, x2, bottom);
            if (ShowZoneLabels)
                labels.Add((z, new Rectangle(x1, actualTop, x2 - x1, Math.Abs(y2 - y1))));
        }

        // Prioritise recently active regions; omit colliding explanations instead of piling up text.
        var occupied = new List<Rectangle>();
        var captionRight = ZoneChartLayout.CaptionRight(r, ZoneLabelRightGap);
        var shown = 0;
        foreach (var item in labels)
        {
            if (shown >= Math.Clamp(MaximumZoneLabels, 0, 8)) break;
            var text = ZoneDisplayLayout.Label(item.Zone, reference);
            var measuredWidth = context.MeasureString(text, _zoneFont).Width + 12;
            var placement = ZoneChartLayout.RightCaption(r, item.Band, captionRight, measuredWidth, CompactZoneLabelWidth, occupied);
            if (placement is null) continue;
            var box = placement.Value;
            occupied.Add(Rectangle.Inflate(box, 0, ReduceTextClutter ? 6 : 0));
            shown++;
            context.FillRectangle(Color.FromArgb(210, 24, 27, 32), box);
            context.DrawString(text, _zoneFont, Color.Gold,
                new Rectangle(box.Left + 6, box.Top, box.Width - 12, box.Height), _reasonFormat);
        }
        _zoneRenderStatus = $"合并{merged.Length}区/筛选{zones.Length}区/绘制{painted}区/短说明{shown}组/屏外{offscreen}区";
    }
}

