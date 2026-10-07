using System;

namespace XAssistant.Models;

/// <summary>某一天的按键总数（用于趋势图）。</summary>
public sealed record DailyKeyCount(DateTime Date, int Count);

/// <summary>
/// 24 小时分布图上的一根柱。
/// </summary>
public sealed class HourBar
{
    /// <summary>小时标签，如 "00" "06" "12"，每 3 小时标一个。</summary>
    public string Label { get; init; } = string.Empty;

    public int Hour { get; init; }

    public int Count { get; init; }

    /// <summary>
    /// 柱高（像素）。由视图模型按当日最大值归一化到 0–<see cref="MaxHeight"/>，
    /// 而不是用比例 —— WPF 里按比例撑高需要 GridLength 转换器，直接给像素更简单。
    /// </summary>
    public double Height { get; init; }

    public const double MaxHeight = 56;

    /// <summary>该时段是否有数据。无数据时画一条 2px 的轨道色占位，避免图上出现"断柱"。</summary>
    public bool HasData => Count > 0;

    /// <summary>是否为当前所在小时（界面高亮用）。</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>按键类型分布的一项。</summary>
public sealed class KeyCategoryItem
{
    public string Name { get; init; } = string.Empty;

    public int Count { get; init; }

    /// <summary>占比 0–100，直接绑到 ProgressBar.Value。</summary>
    public double Percent { get; init; }
}

/// <summary>高频按键排行的一项。</summary>
public sealed class KeyRankItem
{
    public string Key { get; init; } = string.Empty;

    public int Count { get; init; }

    /// <summary>相对于榜首的百分比 0–100（用于排行条长度，不是占比）。</summary>
    public double Percent { get; init; }
}
