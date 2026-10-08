using System;
using System.Collections.Generic;
using System.Linq;
using XAssistant.Models;
using XAssistant.Services;

/// <summary>
/// 轻量断言测试：不引第三方测试框架，直接跑断言并打印 PASS/FAIL。
///
/// 覆盖范围为「可写成断言」的纯逻辑 —— 时间分段、区间计算、版本比较。
/// 涉及数据库与 UI 的部分用探针脚本 + 截图核对（见 Bug清单.md 第四节）。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  FAIL  {name}   {detail}");
        }
    }

    private static int Main()
    {
        Console.WriteLine("════ T1: ActivityCalculator 分段与时长 ════");
        TestActivityCalculator();

        Console.WriteLine();
        Console.WriteLine("════ T2: StatsRange 区间计算 ════");
        TestStatsRange();

        Console.WriteLine();
        Console.WriteLine($"════ 结果：{_pass} 通过 / {_fail} 失败 ════");
        return _fail == 0 ? 0 : 1;
    }

    private static void TestActivityCalculator()
    {
        var t0 = new DateTime(2026, 10, 8, 9, 0, 0);

        // 空输入
        Check(
            "空输入 → Zero",
            ActivityCalculator.CalculateActiveDuration(Array.Empty<DateTime>()) == TimeSpan.Zero
        );

        // 单点输入 → 0（一个时间点构不成时长）
        Check(
            "单点输入 → Zero",
            ActivityCalculator.CalculateActiveDuration(new[] { t0 }) == TimeSpan.Zero
        );

        // 连续输入（间隔 < 5 分钟）应全部计入：10 个点，间隔 1 分钟 → 9 分钟
        var dense = Enumerable.Range(0, 10).Select(i => t0.AddMinutes(i)).ToList();
        var denseDur = ActivityCalculator.CalculateActiveDuration(dense);
        Check(
            "密集输入累加（9 分钟）",
            Math.Abs(denseDur.TotalMinutes - 9) < 0.001,
            $"实得 {denseDur.TotalMinutes} 分钟"
        );

        // 超过 5 分钟的空档应断开：9:00 与 9:10 → 两段各 0 秒 → 总时长 0
        var gapped = new[] { t0, t0.AddMinutes(10) };
        var gappedDur = ActivityCalculator.CalculateActiveDuration(gapped);
        Check(
            "超过 5 分钟空档 → 不计入",
            gappedDur == TimeSpan.Zero,
            $"实得 {gappedDur}"
        );

        // 段数：9:00-9:04 / 9:10-9:14 / 9:20 → 3 段
        var threeSeg = new List<DateTime>();
        for (int i = 0; i <= 4; i++) threeSeg.Add(t0.AddMinutes(i));
        for (int i = 10; i <= 14; i++) threeSeg.Add(t0.AddMinutes(i));
        threeSeg.Add(t0.AddMinutes(20));
        var segs = ActivityCalculator.SplitSegments(threeSeg);
        Check("切成 3 段", segs.Count == 3, $"实得 {segs.Count} 段");

        // 最长段 = 4 分钟（9:00→9:04）
        var longest = segs.Max(s => s.Duration);
        Check(
            "最长段 4 分钟",
            Math.Abs(longest.TotalMinutes - 4) < 0.001,
            $"实得 {longest.TotalMinutes} 分钟"
        );

        // 乱序输入应先排序再处理
        var shuffled = new[] { t0.AddMinutes(2), t0, t0.AddMinutes(4), t0.AddMinutes(1), t0.AddMinutes(3) };
        var shuffledDur = ActivityCalculator.CalculateActiveDuration(shuffled);
        Check(
            "乱序输入等价于有序（4 分钟）",
            Math.Abs(shuffledDur.TotalMinutes - 4) < 0.001,
            $"实得 {shuffledDur.TotalMinutes} 分钟"
        );

        // 重复时间戳应去重：同一时刻重复 5 次不应产生时长
        var dup = Enumerable.Repeat(t0, 5).ToList();
        Check("重复时间戳 → Zero", ActivityCalculator.CalculateActiveDuration(dup) == TimeSpan.Zero);

        // 跨午夜：23:58 → 次日 00:02，间隔 4 分钟 → 应连续（不断开）
        var cross = new[] { new DateTime(2026, 10, 8, 23, 58, 0), new DateTime(2026, 10, 9, 0, 2, 0) };
        var crossDur = ActivityCalculator.CalculateActiveDuration(cross);
        Check(
            "跨午夜连续活动（4 分钟）",
            Math.Abs(crossDur.TotalMinutes - 4) < 0.001,
            $"实得 {crossDur.TotalMinutes} 分钟"
        );

        // 活跃分钟数去重
        var minCount = ActivityCalculator.CountActiveMinutes(new[]
        {
            t0, t0.AddSeconds(10), t0.AddSeconds(50), t0.AddMinutes(1)
        });
        Check("活跃分钟去重 = 2", minCount == 2, $"实得 {minCount}");
    }

    private static void TestStatsRange()
    {
        var today = DateTime.Today;

        // 今天：[今天, 明天) —— 左闭右开
        var (f, t) = StatsRange.Today.ToDateRange();
        Check("今天 起点=今日", f == today);
        Check("今天 终点=明日", t == today.AddDays(1));
        Check("今天 天数=1", StatsRange.Today.DayCount() == 1);

        // 昨天：[昨天, 今天)
        var (yf, yt) = StatsRange.Yesterday.ToDateRange();
        Check("昨天 起点=昨日", yf == today.AddDays(-1));
        Check("昨天 终点=今日（不含）", yt == today);

        // 近 7 天：含今天共 7 天 → [today-6, tomorrow)
        var (sf, st) = StatsRange.Last7Days.ToDateRange();
        Check("近7天 起点=today-6", sf == today.AddDays(-6));
        Check("近7天 终点=明日", st == today.AddDays(1));
        Check("近7天 天数=7", StatsRange.Last7Days.DayCount() == 7);
        Check("近7天 区间跨度=7 天", (st - sf).TotalDays == 7);

        // 近 30 天
        var (mf, mt) = StatsRange.Last30Days.ToDateRange();
        Check("近30天 起点=today-29", mf == today.AddDays(-29));
        Check("近30天 区间跨度=30 天", (mt - mf).TotalDays == 30);

        // 区间必须左闭右开且非空 —— 这是 SQL 侧 `>= from AND < to` 的前提
        foreach (var r in new[] { StatsRange.Today, StatsRange.Yesterday, StatsRange.Last7Days, StatsRange.Last30Days })
        {
            var (rf, rt) = r.ToDateRange();
            Check($"{r.Label()} 区间非空且 from<to", rf < rt);
        }
    }
}
