using System;

namespace XAssistant.Models;

/// <summary>近 N 日活动趋势图上的一根柱。</summary>
public sealed class DayBar
{
    /// <summary>横轴标签，如 "周一"。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>日期文本，如 "10-05"。</summary>
    public string DateText { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }

    /// <summary>时长的可读文本。</summary>
    public string DurationText { get; init; } = string.Empty;

    /// <summary>柱高（像素），按周期内最大值归一化。</summary>
    public double Height { get; init; }

    /// <summary>是否为今天（界面高亮）。</summary>
    public bool IsToday { get; init; }

    /// <summary>相对前一天的变化（界面用 ↑↓ 表示）。</summary>
    public string TrendMark { get; init; } = string.Empty;

    public const double MaxHeight = 72;
}
