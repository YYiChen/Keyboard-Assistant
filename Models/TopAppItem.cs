namespace XAssistant.Models;

/// <summary>
/// 首页「应用用时 Top 5」的条目。
///
/// BarWidth 由 HomeViewModel 在装载时算好（相对第一名的百分比，
/// 0–100），视图直接绑定宽度——不在 XAML 里做转换器。
/// </summary>
public class TopAppItem
{
    /// <summary>应用显示名（由 AppInfoResolver 从进程名转换）。</summary>
    public string Name { get; init; } = "";

    /// <summary>范围内前台累计分钟数。</summary>
    public int Minutes { get; init; }

    /// <summary>时长的可读文本（不足 1 分钟显示「不足 1 分钟」）。</summary>
    public string MinutesText =>
        Minutes >= 60 ? $"{Minutes / 60} 小时 {Minutes % 60} 分" : $"{Math.Max(Minutes, 0)} 分钟";

    /// <summary>横条宽度（相对第一名的百分比，0–100）。</summary>
    public double BarWidth { get; init; }

    /// <summary>排名序号（1–5）。</summary>
    public int Rank { get; init; }
}
