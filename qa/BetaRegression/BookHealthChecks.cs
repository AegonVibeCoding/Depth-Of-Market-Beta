using DepthOfMarketBeta.Core;

static class BookHealthChecks
{
    public static void Run()
    {
        var start = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        DateTime At(double s) => start.AddSeconds(s);
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        DepthRow[] Normal(decimal bid = 100, decimal ask = 101) =>
            [new(BookSide.Bid, bid, 20), new(BookSide.Ask, ask, 20)];
        var engine = new ZoneEngine(new Settings(), 1);
        Check(engine.Diagnostics(At(0)).Contains("等待盘口快照"), "Unseeded book must wait");
        engine.Seed(Normal(), At(0), 1);
        Check(!engine.NeedsBookRepair(At(1)), "Healthy book requested snapshot repair");
        Check(!engine.TryRepairBook(Normal(200, 201), At(1), 1), "Healthy baseline replaced without divergence");
        var stale = engine.Diagnostics(At(6));
        Check(stale.Contains("盘口未更新") && stale.Contains("6.0秒前") && stale.Contains("阈值5秒"), "Stale diagnostic lacks evidence");
        Check(!engine.TryRepairBook(Normal(), At(6), 1), "Stale snapshot revived disconnected book");
        Check(engine.Diagnostics(At(7)).Contains("7.0秒前"), "Snapshot check refreshed stale timestamp");

        engine.Seed([new(BookSide.Ask, 101, 20)], At(10), 1);
        Check(engine.Diagnostics(At(10)).Contains("缺少买盘"), "Missing Bid mislabeled as generic stale");
        Check(!engine.TryRepairBook([new(BookSide.Ask, 101, 30)], At(10.1), 1), "One-sided snapshot accepted");
        Check(engine.TryRepairBook(Normal(), At(10.2), 1), "Complete snapshot failed to recover missing side");
        Check(engine.View(At(10.2)).Dots.Length == 0 && engine.View(At(10.2)).Zones.Length == 0, "Snapshot counted as activity");

        engine.Seed(Normal(102, 101), At(20), 1);
        var crossed = engine.Diagnostics(At(20));
        Check(crossed.Contains("交叉或锁定") && crossed.Contains("买一102/卖一101"), "Crossed book diagnostic incomplete");
        Check(!engine.TryRepairBook(Normal(102, 101), At(20.1), 1), "Crossed authoritative snapshot accepted");
        Check(engine.TryRepairBook(Normal(), At(20.2), 1), "Valid authoritative snapshot failed to repair cross");
        Check(engine.Diagnostics(At(20.2)).Contains("盘口校正"), "Repair not visible in diagnostics");

        engine.Seed(Normal(100, 110), At(30), 1);
        var wide = engine.Diagnostics(At(30));
        Check(wide.Contains("价差超限") && wide.Contains("当前10tick/允许4tick"), "Wide-spread diagnostic lacks actual/limit");
        Check(!engine.TryRepairBook(Normal(100, 110), At(30.1), 1), "Real excessive spread bypassed guard");
        var widerLimit = new ZoneEngine(new Settings { MaximumSpreadTicks = 12 }, 1);
        widerLimit.Seed(Normal(100, 110), At(30), 1);
        Check(!widerLimit.NeedsBookRepair(At(30)), "Configured spread limit ignored");

        // New quotes can jump beyond an old opposite-side level without a matching-price deletion.
        engine.Seed(Normal(), At(40), 1);
        engine.Depth(new(BookSide.Bid, 104, 20), At(40.1), 1);
        Check(engine.NeedsBookRepair(At(40.1)), "Divergent incremental quote was not detected");
        Check(engine.TryRepairBook(Normal(104, 105), At(40.1), 1), "Jumped book did not recover from snapshot");
        Check(!engine.Diagnostics(At(40.1)).Contains("暂停生成"), "Recovered book still paused");
        Check(!engine.TryRepairBook(Normal(104, 105), At(40.2), 1), "Normal updates repeatedly reseed book");

        var fixture = new ZoneEngine(new Settings { ZoneTicks = 1, WindowSeconds = 3, WarmupSeconds = 3,
            BaselineSeconds = 30, ActiveSeconds = 2, MinimumEvents = 4, MinimumChange = 1,
            RelativeChange = 0, BurstRatio = 2, MergeMilliseconds = 50, DetectTradeZones = false }, 1);
        fixture.Seed(Normal(), At(50), 1);
        for (int i = 1; i <= 6; i++) fixture.Depth(new(BookSide.Bid, 100, 20), At(50 + i), 1);
        foreach (var (time, volume) in new[] { (57.1, 24), (57.4, 20), (58.1, 24), (58.4, 20), (59.0, 20) })
            fixture.Depth(new(BookSide.Bid, 100, volume), At(time), 1);
        Check(fixture.View(At(59)).Zones.Length == 1, "Recovery test did not form a zone");
        fixture.Depth(new(BookSide.Bid, 104, 20), At(59.1), 1);
        Check(fixture.TryRepairBook(Normal(104, 105), At(59.1), 1), "Formed book not repaired");
        var view = fixture.View(At(59.1));
        Check(view.Zones.Length == 0 && view.Dots.Length == 0 && fixture.DrainNotices().Length == 0,
            "Old regions/evidence survived an unreliable-book reset");
        Console.WriteLine("PASS detailed missing/crossed/wide/stale diagnostics; authoritative recovery without fake freshness, events or repeated reseeds");
    }
}
