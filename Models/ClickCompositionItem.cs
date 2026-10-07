namespace XAssistant.Models;

/// <summary>
/// 鼠标按键构成的一项（用于占比条）。
/// 与键盘页的 <see cref="KeyCategoryItem"/> 结构一致，保持两页展示形式统一。
/// </summary>
public sealed class ClickCompositionItem
{
    public string Name { get; init; } = string.Empty;

    public int Count { get; init; }

    /// <summary>占比 0–100，直接绑到 ProgressBar.Value。</summary>
    public double Percent { get; init; }

    public string ValueText => Count > 0 ? $"{Count:N0}" : "—";
}
