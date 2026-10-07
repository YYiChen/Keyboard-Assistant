using System;
using System.Collections.Generic;
using System.Linq;

namespace XAssistant.Services;

/// <summary>
/// 由键鼠活动时间戳推导「电脑使用时长」。
///
/// 背景：原设计里「电脑使用时长」来自一个独立的 Windows 服务
/// （<c>XAssistant.Service</c> 的 UsageTrackingService，写 pc_usage.db），
/// 该服务需要管理员权限安装，未安装时这个功能无数据可显示 ——
/// 界面上只会显示「未启用」，而用户无从"打开"（它不是一个开关）。
///
/// 本类提供一个零依赖的替代口径：**用主程序已经在记录的键盘/鼠标活动时间戳，
/// 推导出"实际在使用"的时长**。语义上与"开机时长"不同 ——
/// 它衡量的是有人真正在操作电脑的时间，对"我今天用了多久电脑"这个问题
/// 往往比开机时长更贴近直觉。
///
/// 不含任何 I/O，纯函数，便于单独验证。
/// </summary>
public static class ActivityCalculator
{
    /// <summary>
    /// 相邻两次输入间隔超过此值即视为"离开过"，切成新的一段。
    ///
    /// 取值权衡：太小会把连续工作切得很碎（低估时长），
    /// 太大则会把中间发呆/离开的时间也算进去（高估时长）。
    /// 5 分钟是经验值 —— 常规操作（阅读、思考后继续输入）通常不会中断这么久。
    /// </summary>
    public static readonly TimeSpan DefaultIdleGap = TimeSpan.FromMinutes(5);

    /// <summary>一段连续活动。</summary>
    /// <param name="Start">该段第一次输入的时间。</param>
    /// <param name="End">该段最后一次输入的时间。</param>
    public sealed record ActivitySegment(DateTime Start, DateTime End)
    {
        /// <summary>段内时长 = 末次输入 − 首次输入。</summary>
        public TimeSpan Duration => End - Start;
    }

    /// <summary>
    /// 把时间戳切成若干"连续活动段"：相邻间隔超过阈值即断开。
    /// 段数反映当天被打断过多少次（会议、离开、切换任务），
    /// 最长段则反映最专注的一段连续时间 —— 这两个数字比总时长更有信息量。
    /// </summary>
    public static List<ActivitySegment> SplitSegments(
        IEnumerable<DateTime> timestamps,
        TimeSpan? idleGap = null
    )
    {
        var gap = idleGap ?? DefaultIdleGap;
        var sorted = timestamps.Distinct().OrderBy(t => t).ToList();

        var segments = new List<ActivitySegment>();
        if (sorted.Count == 0)
            return segments;

        var start = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - sorted[i - 1] > gap)
            {
                segments.Add(new ActivitySegment(start, sorted[i - 1]));
                start = sorted[i];
            }
        }
        // 收尾最后一段
        segments.Add(new ActivitySegment(start, sorted[^1]));

        return segments;
    }

    /// <summary>
    /// 计算活跃时长：把时间戳排序后按间隔切段，累加各段长度。
    /// </summary>
    /// <param name="timestamps">活动时间戳（可乱序、可重复）。</param>
    /// <param name="idleGap">间隔阈值，超过即断开为新的一段。</param>
    /// <returns>总活跃时长。少于 2 个时间戳时返回 <see cref="TimeSpan.Zero"/>。</returns>
    public static TimeSpan CalculateActiveDuration(
        IEnumerable<DateTime> timestamps,
        TimeSpan? idleGap = null
    )
    {
        var segments = SplitSegments(timestamps, idleGap);
        return segments.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration);
    }

    /// <summary>
    /// 活跃时长里"实际有输入的分钟数"。与活跃时长配合使用：
    /// 时长是跨度总和，分钟数反映输入的密集程度。
    /// </summary>
    public static int CountActiveMinutes(IEnumerable<DateTime> timestamps)
    {
        return timestamps
            .Select(t => new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0))
            .Distinct()
            .Count();
    }

    /// <summary>把时长格式化为可读文本（如 "2 小时 35 分"）。</summary>
    public static string Format(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
            return "—";

        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";

        return span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes} 分钟"
            : $"{span.Seconds} 秒";
    }
}
