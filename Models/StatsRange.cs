using System;

namespace XAssistant.Models;

/// <summary>
/// 统计时间范围。
///
/// 各统计页共用的快捷切换维度 —— 看"今天"和看"近一个月"是两种完全不同的需求，
/// 原先只能看到固定口径（今日 / 总计），无法按需切换。
/// </summary>
public enum StatsRange
{
    Today,
    Yesterday,
    Last7Days,
    Last30Days,

    /// <summary>
    /// 自定义单日（由日期箭头切换而来）。
    /// 此时区间不完全由范围决定，而要看具体的 <c>SelectedDate</c>。
    /// </summary>
    Custom,
}

public static class StatsRangeExtensions
{
    /// <summary>范围的中文名称。</summary>
    public static string Label(this StatsRange range) =>
        range switch
        {
            StatsRange.Today => "今天",
            StatsRange.Yesterday => "昨天",
            StatsRange.Last7Days => "近 7 天",
            StatsRange.Last30Days => "近 30 天",
            _ => "自定义",
        };

    /// <summary>
    /// 范围对应的日期区间，左闭右开（[From, To)）。
    ///
    /// 用左闭右开而非闭区间，是为了让 SQL 侧统一写成
    /// <c>PressTime &gt;= @from AND PressTime &lt; @to</c>，
    /// 不必为最后一天做 "23:59:59" 这种容易出错的边界处理。
    /// </summary>
    public static (DateTime From, DateTime To) ToDateRange(this StatsRange range)
    {
        var today = DateTime.Today;
        return range switch
        {
            StatsRange.Today => (today, today.AddDays(1)),
            StatsRange.Yesterday => (today.AddDays(-1), today),
            StatsRange.Last7Days => (today.AddDays(-6), today.AddDays(1)),
            _ => (today.AddDays(-29), today.AddDays(1)),
        };
    }

    /// <summary>范围内的自然天数（用于计算日均）。</summary>
    public static int DayCount(this StatsRange range) =>
        range switch
        {
            StatsRange.Today => 1,
            StatsRange.Yesterday => 1,
            StatsRange.Last7Days => 7,
            _ => 30,
        };
}
